using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Services.Insights;

/// <summary>A request for the failed-message insights overview (spec 043).</summary>
/// <param name="Period">Preset window, used when <paramref name="From"/> and <paramref name="To"/> are null.</param>
/// <param name="From">Custom window start (inclusive).</param>
/// <param name="To">Custom window end (exclusive).</param>
/// <param name="EndpointId">Exact endpoint filter, or null for every endpoint.</param>
/// <param name="Enrich">Whether to read saved classifications. The MCP failures view passes false.</param>
/// <param name="Refresh">Re-read saved classifications instead of using the enrichment cache.</param>
public sealed record FailedInsightsRequest(Period Period, DateTime? From, DateTime? To, string? EndpointId, bool Enrich, bool Refresh);

/// <summary>Outcome of a failed-insights query.</summary>
public enum FailedInsightsQueryStatus
{
    /// <summary>The overview was built.</summary>
    Ok,

    /// <summary>The caller lacks the site Reader role.</summary>
    Forbidden,

    /// <summary>The custom window is invalid.</summary>
    InvalidWindow,
}

/// <summary>Result of <see cref="IFailedInsightsQuery.GetOverviewAsync"/>.</summary>
public sealed record FailedInsightsQueryResult(FailedInsightsQueryStatus Status, FailedInsightsOverview? Overview);

/// <summary>
/// Builds the failed-message insights overview: error patterns from the message store, enriched
/// with saved failure classifications into likely-cause and recovery-guidance groups and
/// four-state coverage. It authorizes (site Reader) outside every shared cache.
/// </summary>
public interface IFailedInsightsQuery
{
    /// <summary>Builds the overview for one request.</summary>
    Task<FailedInsightsQueryResult> GetOverviewAsync(FailedInsightsRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Limits for failed-insights enrichment, bound from <c>NimBus:Insights</c>.</summary>
public sealed class FailedInsightsOptions
{
    /// <summary>Configuration section.</summary>
    public const string SectionName = "NimBus:Insights";

    /// <summary>Largest scope, in occurrences after the endpoint filter, that is enriched.</summary>
    public int EnrichmentCeiling { get; set; } = 10_000;

    /// <summary>Lifetime of a cached classification read; never above the 30 s metrics TTL.</summary>
    public int EnrichmentCacheSeconds { get; set; } = 15;

    /// <summary>Failure ids per classification-store call.</summary>
    public int BulkReadBatchSize { get; set; } = 500;

    /// <summary>Classification-store calls in flight at once.</summary>
    public int BulkReadConcurrency { get; set; } = 4;

    /// <summary>Error patterns embedded per cause or guidance group.</summary>
    public int TopPatternsPerGroup { get; set; } = 10;

    /// <summary>Returns the validation errors; empty when valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (EnrichmentCeiling is < 100 or > 100_000) errors.Add("EnrichmentCeiling must be between 100 and 100000.");
        if (EnrichmentCacheSeconds is < 0 or > 30) errors.Add("EnrichmentCacheSeconds must be between 0 and 30.");
        if (BulkReadBatchSize is < 50 or > 1_000) errors.Add("BulkReadBatchSize must be between 50 and 1000.");
        if (BulkReadConcurrency is < 1 or > 8) errors.Add("BulkReadConcurrency must be between 1 and 8.");
        if (TopPatternsPerGroup is < 1 or > 50) errors.Add("TopPatternsPerGroup must be between 1 and 50.");
        return errors;
    }
}

/// <summary>Saved-classification access for Insights; disabled when no reader is registered.</summary>
public interface IInsightsEnrichmentSource
{
    /// <summary>True when saved classifications can be read (read-only or ready mode).</summary>
    bool IsEnabled { get; }

    /// <summary>False when the endpoint is outside the configured analysis allow-list.</summary>
    bool IsEligible(string endpointId);

    /// <summary>Reads the saved state of many failures in bulk.</summary>
    Task<ClassificationBulkReadResult> ReadAsync(IReadOnlyCollection<string> failureMessageIds, ClassificationBulkReadOptions options, CancellationToken cancellationToken);
}

/// <summary>Reads through <see cref="FailureClassificationReader"/> when the extension registered one.</summary>
public sealed class InsightsEnrichmentSource : IInsightsEnrichmentSource
{
    private readonly FailureClassificationReader? _reader;
    private readonly string[] _allowedEndpoints;

    /// <summary>Creates the source; both sequences are empty when intelligence is off.</summary>
    public InsightsEnrichmentSource(IEnumerable<FailureClassificationReader> readers, IEnumerable<FailureClassificationOptions> options)
    {
        _reader = readers.FirstOrDefault();
        _allowedEndpoints = options.FirstOrDefault()?.AllowedEndpoints ?? [];
    }

    /// <inheritdoc />
    public bool IsEnabled => _reader is not null;

    /// <inheritdoc />
    public bool IsEligible(string endpointId)
        => _allowedEndpoints.Length == 0 || _allowedEndpoints.Contains(endpointId, StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public Task<ClassificationBulkReadResult> ReadAsync(IReadOnlyCollection<string> failureMessageIds, ClassificationBulkReadOptions options, CancellationToken cancellationToken)
        => _reader is null
            ? Task.FromResult(new ClassificationBulkReadResult(ClassificationBulkReadStatus.Unsupported, new Dictionary<string, ClassificationSnapshot>()))
            : _reader.ReadLatestInBulkAsync(failureMessageIds, options, cancellationToken);
}

/// <summary>Default <see cref="IFailedInsightsQuery"/>.</summary>
public sealed class FailedInsightsService : IFailedInsightsQuery
{
    /// <summary>Failure-occurrence and window cache lifetime; mirrors the other metrics endpoints.</summary>
    internal static readonly TimeSpan MetricsTtl = TimeSpan.FromSeconds(30);

    internal const string Uncertain = "uncertain";
    internal const string NotAnalyzed = "notAnalyzed";
    internal const string NotEligible = "notEligible";

    private static readonly Dictionary<string, string> CauseLabels = new(StringComparer.Ordinal)
    {
        ["transient_dependency"] = "Transient dependency",
        ["authentication_configuration"] = "Authentication / configuration",
        ["contract_schema"] = "Contract / schema",
        ["business_rule"] = "Business rule",
        ["missing_reference_data"] = "Missing reference data",
        ["application_defect"] = "Application defect",
        ["messaging_platform"] = "Messaging platform",
        [Uncertain] = "Uncertain",
        [NotAnalyzed] = "Not analyzed",
        [NotEligible] = "Not eligible",
    };

    private static readonly Dictionary<string, string> GuidanceLabels = new(StringComparer.Ordinal)
    {
        [nameof(FailureGuidance.RetryMayHelp)] = "Retry may help",
        [nameof(FailureGuidance.ChangeLikelyRequired)] = "Change likely required",
        [nameof(FailureGuidance.Investigate)] = "Investigate",
        [nameof(FailureGuidance.Uncertain)] = "Uncertain",
        [NotAnalyzed] = "Not analyzed",
        [NotEligible] = "Not eligible",
    };

    private readonly IMetricsStore _metricsStore;
    private readonly IStoreResultCache _cache;
    private readonly IEndpointAuthorizationService _authorization;
    private readonly IInsightsEnrichmentSource _enrichment;
    private readonly FailedInsightsOptions _options;
    private readonly TimeProvider _clock;

    /// <summary>Creates the service.</summary>
    public FailedInsightsService(
        IMetricsStore metricsStore,
        IStoreResultCache cache,
        IEndpointAuthorizationService authorization,
        IInsightsEnrichmentSource enrichment,
        IOptions<FailedInsightsOptions> options,
        TimeProvider? clock = null)
    {
        _metricsStore = metricsStore;
        _cache = cache;
        _authorization = authorization;
        _enrichment = enrichment;
        _options = options.Value;
        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public async Task<FailedInsightsQueryResult> GetOverviewAsync(FailedInsightsRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Authorization stays outside every shared cache (spec 043 §8.3).
        if (!await _authorization.HasRoleAsync(AccessRole.Reader).ConfigureAwait(false))
            return new FailedInsightsQueryResult(FailedInsightsQueryStatus.Forbidden, null);
        if (await ResolveWindowAsync(request).ConfigureAwait(false) is not { } window)
            return new FailedInsightsQueryResult(FailedInsightsQueryStatus.InvalidWindow, null);

        var occurrences = await ReadOccurrencesAsync(window).ConfigureAwait(false);
        var endpointId = string.IsNullOrWhiteSpace(request.EndpointId) ? null : request.EndpointId.Trim();
        var scoped = endpointId is null
            ? occurrences.Rows
            : occurrences.Rows.Where(r => string.Equals(r.Info.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase)).ToList();

        var (status, read) = request.Enrich
            ? await EnrichAsync(window, endpointId, scoped, request.Refresh, cancellationToken).ConfigureAwait(false)
            : (InsightsEnrichmentStatus.Disabled, null);
        if (request.Enrich)
            NimBusIntelligenceTelemetry.InsightsEnrichment.Add(1, new TagList { { "nimbus.insights.enrichment", status.ToString() } });

        var assessed = read is null ? null : Assess(scoped, read.Result.Snapshots);
        return new FailedInsightsQueryResult(FailedInsightsQueryStatus.Ok, new FailedInsightsOverview
        {
            Groups = BuildErrorPatternGroups(scoped, assessed),
            TotalFailed = scoped.Count,
            From = window.From,
            To = window.To,
            EndpointId = endpointId,
            TotalOccurrences = scoped.Count,
            OccurrencesReadAt = occurrences.ReadAt.UtcDateTime,
            ClassificationsReadAt = read?.ReadAt.UtcDateTime,
            Enrichment = status,
            EnrichmentLimit = _options.EnrichmentCeiling,
            Coverage = assessed is null ? null : Coverage(assessed.Values),
            CauseGroups = assessed is null ? null : BuildInsightGroups(scoped, assessed, a => a.CauseKey, CauseLabels),
            GuidanceGroups = assessed is null ? null : BuildInsightGroups(scoped, assessed, a => a.GuidanceKey, GuidanceLabels),
        });
    }

    /// <summary>
    /// A preset resolves once to a fixed window that is cached under the preset name, so every
    /// request in one interaction sees the same bounds. A custom window is already fixed.
    /// </summary>
    private async Task<MetricsWindow?> ResolveWindowAsync(FailedInsightsRequest request)
    {
        if (request.From.HasValue || request.To.HasValue)
            return MetricsImplementation.ResolveWindow(request.Period, request.From, request.To, _clock.GetUtcNow().UtcDateTime);
        return await _cache.GetOrCreateAsync(
            $"metrics:failed-insights:window:{request.Period}",
            MetricsTtl,
            () => Task.FromResult(MetricsImplementation.ResolveWindow(request.Period, null, null, _clock.GetUtcNow().UtcDateTime)!.Value)).ConfigureAwait(false);
    }

    /// <summary>The window's failure occurrences, normalized and de-duplicated once per cache fill.</summary>
    private Task<OccurrenceSet> ReadOccurrencesAsync(MetricsWindow window)
        => _cache.GetOrCreateAsync(
            $"metrics:failed-insights:rows:{window.From.Ticks}-{window.To.Ticks}",
            MetricsTtl,
            async () =>
            {
                var rows = await _metricsStore.GetFailedMessageInsights(window.From, window.To).ConfigureAwait(false);
                var readAt = _clock.GetUtcNow();
                var seen = new HashSet<(string, string, string)>();
                var kept = new List<OccurrenceRow>(rows.Count);
                var duplicates = 0;
                // Earliest first, so a collapsed duplicate keeps its earliest row (spec 043 §4.1).
                foreach (var info in rows.OrderBy(r => r.EnqueuedTimeUtc))
                {
                    if (!string.IsNullOrEmpty(info.MessageId)
                        && !seen.Add(((info.EndpointId ?? string.Empty).ToUpperInvariant(), info.EventId ?? string.Empty, info.MessageId)))
                    {
                        duplicates++;
                        continue;
                    }
                    var category = ErrorPatternNormalizer.ExtractCategory(info.ErrorText);
                    var pattern = ErrorPatternNormalizer.Normalize(info.ErrorText);
                    kept.Add(new OccurrenceRow(info, category, Key("ec", category), pattern, Key("ep", pattern)));
                }
                if (duplicates > 0) NimBusIntelligenceTelemetry.InsightsDuplicateIdentities.Add(duplicates);
                return new OccurrenceSet(readAt, kept);
            });

    private async Task<(InsightsEnrichmentStatus Status, EnrichmentRead? Read)> EnrichAsync(
        MetricsWindow window, string? endpointId, IReadOnlyList<OccurrenceRow> scoped, bool refresh, CancellationToken cancellationToken)
    {
        if (!_enrichment.IsEnabled) return (InsightsEnrichmentStatus.Disabled, null);
        if (scoped.Any(r => string.IsNullOrEmpty(r.Info.MessageId))) return (InsightsEnrichmentStatus.Unsupported, null);
        if (scoped.Count > _options.EnrichmentCeiling) return (InsightsEnrichmentStatus.LimitExceeded, null);

        var ids = scoped.Select(r => r.Info.MessageId!).ToHashSet(StringComparer.Ordinal);
        var key = $"metrics:failed-insights:enrichment:{window.From.Ticks}-{window.To.Ticks}:{endpointId?.ToUpperInvariant() ?? "*"}";
        if (refresh) _cache.Remove(key);
        var read = await ReadEnrichmentAsync(key, ids, cancellationToken).ConfigureAwait(false);
        // The 15 s classification read can outlive the 30 s occurrence read it was made for. An id
        // it did not cover has no established absence, so read again rather than call it unanalyzed.
        if (!ids.IsSubsetOf(read.RequestedIds))
        {
            _cache.Remove(key);
            read = await ReadEnrichmentAsync(key, ids, cancellationToken).ConfigureAwait(false);
        }
        return read.Result.Status switch
        {
            ClassificationBulkReadStatus.Available => (InsightsEnrichmentStatus.Available, read),
            ClassificationBulkReadStatus.Unsupported => (InsightsEnrichmentStatus.Unsupported, null),
            _ => (InsightsEnrichmentStatus.Unavailable, null),
        };
    }

    private async Task<EnrichmentRead> ReadEnrichmentAsync(string key, HashSet<string> ids, CancellationToken cancellationToken)
    {
        var read = await _cache.GetOrCreateAsync(key, TimeSpan.FromSeconds(_options.EnrichmentCacheSeconds), async () =>
        {
            var result = await _enrichment.ReadAsync(ids,
                new ClassificationBulkReadOptions(_options.BulkReadBatchSize, _options.BulkReadConcurrency), cancellationToken).ConfigureAwait(false);
            return new EnrichmentRead(_clock.GetUtcNow(), ids, result);
        }).ConfigureAwait(false);
        // Like a faulted read, an unavailable one must not stick: the next request retries storage.
        if (read.Result.Status != ClassificationBulkReadStatus.Available) _cache.Remove(key);
        return read;
    }

    /// <summary>Classifies each occurrence into a coverage state and its cause and guidance keys (spec 043 §5, §6).</summary>
    private Dictionary<OccurrenceRow, Assessment> Assess(IReadOnlyList<OccurrenceRow> scoped, IReadOnlyDictionary<string, ClassificationSnapshot> snapshots)
    {
        var assessed = new Dictionary<OccurrenceRow, Assessment>(scoped.Count, ReferenceEqualityComparer.Instance);
        var mismatches = 0;
        foreach (var row in scoped)
        {
            snapshots.TryGetValue(row.Info.MessageId!, out var snapshot);
            var latest = snapshot is { Deleted: false } ? snapshot.Latest : null;
            if (latest is null)
            {
                var state = _enrichment.IsEligible(row.Info.EndpointId) ? CoverageState.NotAnalyzed : CoverageState.NotEligible;
                var key = state == CoverageState.NotAnalyzed ? NotAnalyzed : NotEligible;
                assessed[row] = new Assessment(state, key, key);
                continue;
            }

            if (!string.Equals(latest.EndpointId, row.Info.EndpointId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(latest.EventId, row.Info.EventId, StringComparison.Ordinal))
            {
                mismatches++;
                assessed[row] = new Assessment(CoverageState.Uncertain, Uncertain, nameof(FailureGuidance.Uncertain));
                continue;
            }

            // Stored guidance is a Newtonsoft integer: an out-of-range value deserializes without error.
            var guidanceKnown = Enum.IsDefined(latest.Guidance);
            var categoryKnown = FailureClassificationQuestionSet.Categories.Contains(latest.Category);
            var guidanceKey = guidanceKnown ? latest.Guidance.ToString() : nameof(FailureGuidance.Uncertain);
            var classified = guidanceKnown && categoryKnown
                && latest.Category != "unknown" && latest.Guidance != FailureGuidance.Uncertain;
            assessed[row] = classified
                ? new Assessment(CoverageState.Classified, latest.Category, guidanceKey)
                : new Assessment(CoverageState.Uncertain, Uncertain, guidanceKey);
        }
        if (mismatches > 0) NimBusIntelligenceTelemetry.InsightsIdentityMismatches.Add(mismatches);
        return assessed;
    }

    private static List<ErrorPatternGroup> BuildErrorPatternGroups(IReadOnlyList<OccurrenceRow> scoped, Dictionary<OccurrenceRow, Assessment>? assessed)
        => scoped
            .GroupBy(r => r.CategoryKey, StringComparer.Ordinal)
            .Select(g =>
            {
                var rows = g.ToList();
                return new ErrorPatternGroup
                {
                    GroupKey = g.Key,
                    ErrorCategory = rows[0].Category,
                    Count = rows.Count,
                    Coverage = assessed is null ? null : Coverage(rows.Select(r => assessed[r])),
                    Endpoints = Distinct(rows.Select(r => r.Info.EndpointId)),
                    EventTypes = Distinct(rows.Select(r => r.Info.EventTypeId)),
                    LatestOccurrence = rows.Max(r => r.Info.EnqueuedTimeUtc),
                    ExampleErrorText = rows[0].Info.ErrorText,
                    SubGroups = rows
                        .GroupBy(r => r.PatternKey, StringComparer.Ordinal)
                        .Select(sg =>
                        {
                            var subRows = sg.ToList();
                            return new ErrorSubGroup
                            {
                                GroupKey = sg.Key,
                                NormalizedPattern = subRows[0].Pattern,
                                Count = subRows.Count,
                                Coverage = assessed is null ? null : Coverage(subRows.Select(r => assessed[r])),
                                Endpoints = Distinct(subRows.Select(r => r.Info.EndpointId)),
                                EventTypes = Distinct(subRows.Select(r => r.Info.EventTypeId)),
                                LatestOccurrence = subRows.Max(r => r.Info.EnqueuedTimeUtc),
                                ExampleErrorText = subRows[0].Info.ErrorText,
                            };
                        })
                        .OrderByDescending(sg => sg.Count).ThenBy(sg => sg.GroupKey, StringComparer.Ordinal)
                        .ToList(),
                };
            })
            .OrderByDescending(g => g.Count).ThenBy(g => g.GroupKey, StringComparer.Ordinal)
            .ToList();

    private List<InsightGroup> BuildInsightGroups(IReadOnlyList<OccurrenceRow> scoped, Dictionary<OccurrenceRow, Assessment> assessed,
        Func<Assessment, string> keyOf, IReadOnlyDictionary<string, string> labels)
        => scoped
            .GroupBy(r => keyOf(assessed[r]), StringComparer.Ordinal)
            .Select(g =>
            {
                var rows = g.ToList();
                var patterns = rows
                    .GroupBy(r => r.PatternKey, StringComparer.Ordinal)
                    .Select(p => (Rows: p.ToList(), Key: p.Key))
                    .OrderByDescending(p => p.Rows.Count).ThenBy(p => p.Key, StringComparer.Ordinal)
                    .ToList();
                return new InsightGroup
                {
                    GroupKey = g.Key,
                    Label = labels.TryGetValue(g.Key, out var label) ? label : g.Key,
                    Count = rows.Count,
                    Coverage = Coverage(rows.Select(r => assessed[r])),
                    Endpoints = Distinct(rows.Select(r => r.Info.EndpointId)),
                    EventTypes = Distinct(rows.Select(r => r.Info.EventTypeId)),
                    LatestOccurrence = rows.Max(r => r.Info.EnqueuedTimeUtc),
                    TopPatterns = patterns.Take(_options.TopPatternsPerGroup).Select(p => new InsightGroupPattern
                    {
                        GroupKey = p.Key,
                        NormalizedPattern = p.Rows[0].Pattern,
                        Count = p.Rows.Count,
                        ExampleErrorText = p.Rows[0].Info.ErrorText,
                    }).ToList(),
                    MorePatterns = Math.Max(0, patterns.Count - _options.TopPatternsPerGroup),
                };
            })
            .OrderByDescending(g => g.Count).ThenBy(g => g.GroupKey, StringComparer.Ordinal)
            .ToList();

    private static InsightsCoverage Coverage(IEnumerable<Assessment> assessments)
    {
        int classified = 0, uncertain = 0, notAnalyzed = 0, notEligible = 0;
        foreach (var assessment in assessments)
        {
            switch (assessment.State)
            {
                case CoverageState.Classified: classified++; break;
                case CoverageState.Uncertain: uncertain++; break;
                case CoverageState.NotAnalyzed: notAnalyzed++; break;
                default: notEligible++; break;
            }
        }
        return new InsightsCoverage
        {
            Total = classified + uncertain + notAnalyzed + notEligible,
            Analyzed = classified + uncertain,
            Classified = classified,
            Uncertain = uncertain,
            NotAnalyzed = notAnalyzed,
            NotEligible = notEligible,
        };
    }

    private static List<string> Distinct(IEnumerable<string?> values)
        => values.Where(v => v is not null).Select(v => v!).Distinct().ToList();

    /// <summary>Opaque, query-string-safe group key: a prefix plus 32 hex characters of SHA-256.</summary>
    internal static string Key(string prefix, string text)
        => $"{prefix}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..32].ToLowerInvariant()}";

    internal enum CoverageState { Classified, Uncertain, NotAnalyzed, NotEligible }

    internal sealed record Assessment(CoverageState State, string CauseKey, string GuidanceKey);

    internal sealed record OccurrenceRow(FailedMessageInfo Info, string Category, string CategoryKey, string Pattern, string PatternKey);

    internal sealed record OccurrenceSet(DateTimeOffset ReadAt, IReadOnlyList<OccurrenceRow> Rows);

    internal sealed record EnrichmentRead(DateTimeOffset ReadAt, IReadOnlySet<string> RequestedIds, ClassificationBulkReadResult Result);
}
