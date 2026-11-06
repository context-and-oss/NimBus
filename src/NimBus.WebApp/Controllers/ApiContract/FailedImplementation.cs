using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using NimBus.Core;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using FailedEventHistogram = NimBus.MessageStore.States.FailedEventHistogram;
using SearchResponse = NimBus.WebApp.ManagementApi.SearchResponse;
using StoreEventFilter = NimBus.MessageStore.EventFilter;
using StoreResolutionStatus = NimBus.MessageStore.ResolutionStatus;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Controllers.ApiContract;

/// <summary>
/// The Failed page: unresolved failures (Failed, DeadLettered, Unsupported) across every
/// endpoint the caller can read — search, a per-bucket histogram, and grouping by error.
/// </summary>
public class FailedImplementation : IFailedApiController
{
    internal const int DefaultPageSize = 100;
    internal const int MaxPageSize = 500;

    /// <summary>Failures the error grouping reads before it reports Truncated.</summary>
    internal const int ErrorGroupCap = 5000;

    /// <summary>Longest custom histogram window.</summary>
    internal static readonly TimeSpan MaxWindow = TimeSpan.FromDays(90);

    /// <summary>Most bars a custom window may produce.</summary>
    internal const int MaxBuckets = 60;

    /// <summary>Largest step the store counts in; local bars are folded from these steps.</summary>
    internal static readonly TimeSpan StoreStep = TimeSpan.FromMinutes(15);

    // Error text kept per failure in the error grouping; the full text is on the event page.
    private const int ErrorTextLimit = 1000;

    private static readonly TimeSpan[] CustomBucketSizes =
    {
        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(1), TimeSpan.FromHours(3), TimeSpan.FromHours(6),
        TimeSpan.FromHours(12), TimeSpan.FromDays(1),
    };

    private readonly IPlatform _platform;
    private readonly IMessageTrackingStore _messageStore;
    private readonly IEndpointAuthorizationService _authorizationService;
    private readonly IAuditLogService _auditLogService;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly PayloadRedaction _payloadRedaction;
    private readonly ILogger<FailedImplementation> _logger;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the implementation; <paramref name="timeProvider"/> defaults to the system clock.</summary>
    public FailedImplementation(
        IPlatform platform,
        IMessageTrackingStore messageStore,
        IEndpointAuthorizationService authorizationService,
        IAuditLogService auditLogService,
        IHttpContextAccessor httpContextAccessor,
        PayloadRedaction payloadRedaction,
        ILogger<FailedImplementation> logger,
        TimeProvider? timeProvider = null)
    {
        _platform = platform;
        _messageStore = messageStore;
        _authorizationService = authorizationService;
        _auditLogService = auditLogService;
        _httpContextAccessor = httpContextAccessor;
        _payloadRedaction = payloadRedaction;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<ActionResult<SearchResponse>> PostFailedSearchAsync(FailedSearchRequest body)
    {
        var auditData = JsonConvert.SerializeObject(body);
        var (scope, refused) = await ResolveScopeAsync(body?.Filter, auditData);
        if (refused != null) return refused;

        var pageSize = body!.MaxSearchItemsCount <= 0 ? DefaultPageSize : Math.Min(body.MaxSearchItemsCount, MaxPageSize);
        var result = await _messageStore.GetFailedEventsAcrossEndpoints(
            ToEventFilter(body.Filter), scope!, body.ContinuationToken, pageSize);
        await _auditLogService.LogAuditAsync(MessageAuditType.SearchEvents, _httpContextAccessor.HttpContext!, data: auditData);

        var events = result.Events.Select(Mapper.EventFromMessageStoreEvent).ToList();
        foreach (var endpointEvents in events.Where(e => e.EndpointId != null).GroupBy(e => e.EndpointId!))
            await EventRowEnrichment.AttachAsync(_messageStore, _logger, endpointEvents.Key, endpointEvents.ToList());

        if (!await _authorizationService.CanReadPiiAsync())
            _payloadRedaction.Redact(events);

        return new SearchResponse
        {
            Events = events,
            ContinuationToken = string.IsNullOrEmpty(result.ContinuationToken) ? null! : result.ContinuationToken,
        };
    }

    /// <inheritdoc />
    public async Task<ActionResult<FailedHistogram>> PostFailedHistogramAsync(FailedHistogramRequest body)
    {
        var (scope, refused) = await ResolveScopeAsync(body?.Filter, JsonConvert.SerializeObject(body));
        if (refused != null) return refused;

        var zone = ResolveTimeZone(body!.TimeZone);
        var window = ResolveWindow(body.Period, body.From, body.To, _timeProvider.GetUtcNow().UtcDateTime, zone);
        if (window == null)
            return new BadRequestObjectResult($"The custom window needs from < to and may span at most {MaxWindow.TotalDays} days.");

        // The store counts in fixed steps from the window start; local bars are whole numbers of
        // steps, because every time zone offset and daylight-saving shift is a multiple of 15 minutes.
        var step = window.Bucket < StoreStep ? window.Bucket : StoreStep;
        var histogram = await _messageStore.GetFailedEventHistogram(ToEventFilter(body.Filter), scope!, window.From, window.To, step);
        return BuildHistogram(histogram, window);
    }

    /// <inheritdoc />
    public async Task<ActionResult<FailedErrorGroups>> PostFailedErrorGroupsAsync(FailedErrorGroupsRequest body)
    {
        var auditData = JsonConvert.SerializeObject(body);
        var (scope, refused) = await ResolveScopeAsync(body?.Filter, auditData);
        if (refused != null) return refused;

        var filter = ToEventFilter(body!.Filter);
        var failures = new List<UnresolvedEvent>();
        string? token = null;
        do
        {
            var page = await _messageStore.GetFailedEventsAcrossEndpoints(filter, scope!, token, PaginationLimits.MaxPageSize);
            failures.AddRange(page.Events);
            token = page.ContinuationToken;
        }
        while (!string.IsNullOrEmpty(token) && failures.Count < ErrorGroupCap);

        await _auditLogService.LogAuditAsync(MessageAuditType.SearchEvents, _httpContextAccessor.HttpContext!, data: auditData);

        var truncated = failures.Count > ErrorGroupCap || !string.IsNullOrEmpty(token);
        return BuildErrorGroups(failures.Take(ErrorGroupCap).ToList(), truncated);
    }

    // The endpoints to query: those named in the filter (every one must be readable), else
    // every platform endpoint the caller holds Reader on.
    private async Task<(IReadOnlyCollection<string>? Scope, ActionResult? Refused)> ResolveScopeAsync(
        FailedSearchFilter? filter,
        string auditData)
    {
        var requested = filter?.EndpointIds?.Where(id => !string.IsNullOrWhiteSpace(id)).ToList();
        if (requested is { Count: > 0 })
        {
            var scope = new List<string>();
            foreach (var id in requested)
            {
                var endpoint = _platform.Endpoints.FirstOrDefault(e => e.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
                if (endpoint == null)
                    return (null, new BadRequestObjectResult($"Unknown endpoint '{id}'."));
                if (!await _authorizationService.HasRoleAsync(AccessRole.Reader, endpoint.Id))
                {
                    await _auditLogService.LogAuditAsync(MessageAuditType.SearchEvents, _httpContextAccessor.HttpContext!,
                        accessDenied: true, data: auditData, endpointId: endpoint.Id);
                    return (null, new ForbidResult());
                }

                scope.Add(endpoint.Id);
            }

            return (scope.Distinct(StringComparer.Ordinal).ToList(), null);
        }

        var readable = new List<string>();
        foreach (var endpoint in _platform.Endpoints)
        {
            if (await _authorizationService.HasRoleAsync(AccessRole.Reader, endpoint.Id))
                readable.Add(endpoint.Id);
        }

        return (readable, null);
    }

    internal static StoreEventFilter ToEventFilter(FailedSearchFilter? filter)
    {
        if (filter == null) return new StoreEventFilter();
        return new StoreEventFilter
        {
            EventId = NullIfBlank(filter.EventId),
            LastMessageId = NullIfBlank(filter.LastMessageId),
            SessionId = NullIfBlank(filter.SessionId),
            From = NullIfBlank(filter.From),
            To = NullIfBlank(filter.To),
            ErrorText = NullIfBlank(filter.ErrorText),
            EventTypeId = filter.EventTypeId is { Count: > 0 }
                ? filter.EventTypeId.Where(t => !string.IsNullOrWhiteSpace(t)).ToList()
                : null,
            UpdatedAtFrom = filter.UpdatedAtFrom,
            UpdatedAtTo = filter.UpdatedAtTo,
            ResolutionStatus = filter.Statuses is { Count: > 0 }
                ? filter.Statuses.Select(s => s.ToString()).ToList()
                : null,
        };
    }

    /// <summary>
    /// The histogram window and bucket size: a custom from/to when both are given (the smallest
    /// bucket that keeps it within <see cref="MaxBuckets"/> bars), else the period preset. The
    /// window ends at the end of the bucket holding <paramref name="nowUtc"/>. Bars follow the
    /// wall clock of <paramref name="zone"/> (UTC when null): they start at local midnight and
    /// step in whole buckets of local time, so a bar across a daylight-saving change is an hour
    /// shorter or longer. Null for an invalid custom window.
    /// </summary>
    internal static HistogramWindow? ResolveWindow(
        Period period,
        DateTime? from,
        DateTime? to,
        DateTime nowUtc,
        TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Utc;
        if (from.HasValue && to.HasValue)
        {
            var start = DateTime.SpecifyKind(from.Value.ToUniversalTime(), DateTimeKind.Utc);
            var end = DateTime.SpecifyKind(to.Value.ToUniversalTime(), DateTimeKind.Utc);
            if (end <= start || end - start > MaxWindow) return null;

            var span = end - start;
            var bucket = CustomBucketSizes.FirstOrDefault(b => span.Ticks / b.Ticks <= MaxBuckets, CustomBucketSizes[^1]);
            var firstWall = FloorWall(ToWall(start, zone), bucket);
            var lastWall = FloorWall(ToWall(end, zone), bucket);
            if (ToUtc(lastWall, zone) < end) lastWall += bucket;
            return new HistogramWindow(Boundaries(firstWall, lastWall, bucket, zone), bucket);
        }

        var (window, size) = period switch
        {
            Period._1h => (TimeSpan.FromHours(1), TimeSpan.FromMinutes(5)),
            Period._12h => (TimeSpan.FromHours(12), TimeSpan.FromMinutes(30)),
            Period._3d => (TimeSpan.FromDays(3), TimeSpan.FromHours(3)),
            Period._7d => (TimeSpan.FromDays(7), TimeSpan.FromHours(6)),
            Period._30d => (TimeSpan.FromDays(30), TimeSpan.FromDays(1)),
            _ => (TimeSpan.FromDays(1), TimeSpan.FromHours(1)),
        };
        var endWall = FloorWall(ToWall(nowUtc, zone), size) + size;
        return new HistogramWindow(Boundaries(endWall - window, endWall, size, zone), size);
    }

    /// <summary>The zone named by an IANA id, or UTC when it is absent or unknown.</summary>
    internal static TimeZoneInfo ResolveTimeZone(string? id) =>
        !string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out var zone)
            ? zone
            : TimeZoneInfo.Utc;

    /// <summary>
    /// The UTC instant of a wall-clock time in <paramref name="zone"/>, resolved the way a
    /// JavaScript <c>Date</c> resolves local times: a time skipped by a daylight-saving change
    /// moves forward by the gap, and a repeated time is the earlier of the two instants.
    /// </summary>
    internal static DateTime ToUtc(DateTime wallClock, TimeZoneInfo zone)
    {
        var wall = DateTime.SpecifyKind(wallClock, DateTimeKind.Unspecified);
        TimeSpan offset;
        if (zone.IsInvalidTime(wall))
        {
            // Read with the offset in force just before the gap, which lands after it.
            var before = wall;
            do before = before.AddMinutes(-15); while (zone.IsInvalidTime(before));
            offset = zone.GetUtcOffset(before);
        }
        else if (zone.IsAmbiguousTime(wall))
        {
            offset = zone.GetAmbiguousTimeOffsets(wall).Max();
        }
        else
        {
            offset = zone.GetUtcOffset(wall);
        }

        return DateTime.SpecifyKind(wall - offset, DateTimeKind.Utc);
    }

    private static DateTime ToWall(DateTime utc, TimeZoneInfo zone) =>
        DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), zone), DateTimeKind.Unspecified);

    // Every bucket size divides a day, so whole buckets from midnight tile each local day.
    private static DateTime FloorWall(DateTime wall, TimeSpan bucket) =>
        wall.Date + TimeSpan.FromTicks(wall.TimeOfDay.Ticks - wall.TimeOfDay.Ticks % bucket.Ticks);

    // The UTC instants of the wall-clock boundaries from first to last, dropping any that a
    // skipped hour collapses onto the previous one.
    private static List<DateTime> Boundaries(DateTime firstWall, DateTime lastWall, TimeSpan bucket, TimeZoneInfo zone)
    {
        var boundaries = new List<DateTime>();
        for (var wall = firstWall; wall <= lastWall; wall += bucket)
        {
            var utc = ToUtc(wall, zone);
            if (boundaries.Count == 0 || utc > boundaries[^1]) boundaries.Add(utc);
        }

        return boundaries;
    }

    /// <summary>Every bucket in the window, oldest first and zero-filled, plus totals.</summary>
    internal static FailedHistogram BuildHistogram(FailedEventHistogram histogram, HistogramWindow window)
    {
        var bounds = window.Boundaries;
        var count = bounds.Count - 1;
        var buckets = Enumerable.Range(0, count)
            .Select(k => new FailedHistogramBucket
            {
                Start = bounds[k],
                ByEndpoint = new Dictionary<string, int>(),
            })
            .ToList();

        var totalsByEndpoint = new Dictionary<string, FailedEndpointTotals>(StringComparer.Ordinal);
        var totals = new FailedHistogramTotals();
        foreach (var row in histogram.Rows)
        {
            if (row.BucketStartUtc < bounds[0] || row.BucketStartUtc >= bounds[^1]) continue;

            // The last boundary at or before the row's start: the bar the store step falls in.
            var found = bounds.BinarySearch(row.BucketStartUtc);
            var index = found >= 0 ? found : ~found - 1;

            var target = buckets[index];
            if (!totalsByEndpoint.TryGetValue(row.EndpointId, out var endpointTotals))
                totalsByEndpoint[row.EndpointId] = endpointTotals = new FailedEndpointTotals { EndpointId = row.EndpointId };

            switch (row.Status)
            {
                case nameof(StoreResolutionStatus.Failed):
                    target.Failed += row.Count; totals.Failed += row.Count; endpointTotals.Failed += row.Count;
                    break;
                case nameof(StoreResolutionStatus.DeadLettered):
                    target.DeadLettered += row.Count; totals.DeadLettered += row.Count; endpointTotals.DeadLettered += row.Count;
                    break;
                case nameof(StoreResolutionStatus.Unsupported):
                    target.Unsupported += row.Count; totals.Unsupported += row.Count; endpointTotals.Unsupported += row.Count;
                    break;
                default:
                    continue;
            }

            target.ByEndpoint[row.EndpointId] = target.ByEndpoint.GetValueOrDefault(row.EndpointId) + row.Count;
        }

        totals.ByEndpoint = totalsByEndpoint.Values
            .OrderByDescending(e => e.Failed + e.DeadLettered + e.Unsupported)
            .ThenBy(e => e.EndpointId, StringComparer.Ordinal)
            .ToList();

        return new FailedHistogram
        {
            BucketMinutes = (int)window.Bucket.TotalMinutes,
            From = window.From,
            To = window.To,
            Buckets = buckets,
            Totals = totals,
            Truncated = histogram.Truncated,
        };
    }

    /// <summary>
    /// Groups failures by error category, then by normalized pattern — the same grouping as the
    /// Insights page — keeping each pattern's failures, newest first.
    /// </summary>
    internal static FailedErrorGroups BuildErrorGroups(IReadOnlyList<UnresolvedEvent> failures, bool truncated)
    {
        var rows = failures
            .Select(e => (Event: e, ErrorText: ErrorTextOf(e)))
            .OrderByDescending(r => r.Event.UpdatedAt)
            .ToList();

        var groups = rows
            .GroupBy(r => ErrorPatternNormalizer.ExtractCategory(r.ErrorText ?? string.Empty))
            .Select(category => new FailedErrorGroup
            {
                ErrorCategory = category.Key,
                Count = category.Count(),
                Endpoints = DistinctValues(category.Select(r => r.Event.EndpointId)),
                EventTypes = DistinctValues(category.Select(r => r.Event.EventTypeId)),
                LatestOccurrence = category.Max(r => r.Event.UpdatedAt),
                ExampleErrorText = Truncate(category.First().ErrorText) ?? string.Empty,
                SubGroups = category
                    .GroupBy(r => ErrorPatternNormalizer.Normalize(r.ErrorText ?? string.Empty))
                    .Select(pattern => new FailedErrorSubGroup
                    {
                        NormalizedPattern = pattern.Key,
                        Count = pattern.Count(),
                        Endpoints = DistinctValues(pattern.Select(r => r.Event.EndpointId)),
                        EventTypes = DistinctValues(pattern.Select(r => r.Event.EventTypeId)),
                        LatestOccurrence = pattern.Max(r => r.Event.UpdatedAt),
                        ExampleErrorText = Truncate(pattern.First().ErrorText) ?? string.Empty,
                        Events = pattern.Select(r => new FailedEventRef
                        {
                            EventId = r.Event.EventId,
                            LastMessageId = r.Event.LastMessageId,
                            EndpointId = r.Event.EndpointId,
                            SessionId = r.Event.SessionId,
                            EventTypeId = r.Event.EventTypeId,
                            ResolutionStatus = r.Event.ResolutionStatus.ToString(),
                            UpdatedAt = r.Event.UpdatedAt,
                            ErrorText = Truncate(r.ErrorText),
                        }).ToList(),
                    })
                    .OrderByDescending(p => p.Count)
                    .ToList(),
            })
            .OrderByDescending(g => g.Count)
            .ToList();

        return new FailedErrorGroups { Groups = groups, Total = rows.Count, Truncated = truncated };
    }

    /// <summary>Grouping text for an Unsupported event that carries no error of its own.</summary>
    internal const string UnsupportedErrorText = "Unsupported: no handler for this event type";

    /// <summary>
    /// The text a failure is grouped and shown by: the handler's error, else the dead-letter
    /// description or reason Service Bus recorded, else the recorded reason. An Unsupported event
    /// has none (no handler ran), so it groups under <see cref="UnsupportedErrorText"/>.
    /// </summary>
    internal static string? ErrorTextOf(UnresolvedEvent e) =>
        new[]
        {
            e.MessageContent?.ErrorContent?.ErrorText,
            e.DeadLetterErrorDescription,
            e.DeadLetterReason,
            e.Reason,
        }.FirstOrDefault(t => !string.IsNullOrWhiteSpace(t))
        ?? (e.ResolutionStatus == StoreResolutionStatus.Unsupported ? UnsupportedErrorText : null);

    private static List<string> DistinctValues(IEnumerable<string?> values) =>
        values.Where(v => !string.IsNullOrEmpty(v)).Distinct(StringComparer.Ordinal).ToList()!;

    private static string? Truncate(string? text) =>
        text == null || text.Length <= ErrorTextLimit ? text : text[..ErrorTextLimit] + "…";

    private static string? NullIfBlank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}

/// <summary>
/// A failure histogram's bars: <paramref name="Boundaries"/> are the UTC starts of every bar plus
/// the end of the last, ascending; each bar ends where the next starts.
/// </summary>
/// <param name="Boundaries">Bar starts, then the window end, in UTC.</param>
/// <param name="Bucket">Nominal bar size.</param>
internal sealed record HistogramWindow(List<DateTime> Boundaries, TimeSpan Bucket)
{
    /// <summary>Start of the first bar.</summary>
    public DateTime From => Boundaries[0];

    /// <summary>End of the last bar (exclusive).</summary>
    public DateTime To => Boundaries[^1];

    /// <summary>Deconstructs into the window's start, end and nominal bar size.</summary>
    public void Deconstruct(out DateTime from, out DateTime to, out TimeSpan bucket)
    {
        from = From;
        to = To;
        bucket = Bucket;
    }
}
