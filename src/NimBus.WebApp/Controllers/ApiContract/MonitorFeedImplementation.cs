using Microsoft.AspNetCore.Mvc;
using NimBus.Core;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.DashboardTokens;
using NimBus.WebApp.Services.Heartbeat;
using NimBus.WebApp.Services.MonitorFeed;
using StoredEndpointMetadata = NimBus.MessageStore.States.EndpointMetadata;

namespace NimBus.WebApp.Controllers.ApiContract;

/// <summary>
/// The Monitor wall's read API: three operations, one per refresh cadence. Admitted for a site
/// Reader or a dashboard token (the <see cref="DashboardTokenDefaults.PolicyName"/> policy), and
/// unfiltered: every caller gets the same fleet-wide payload, composed once per cache window for
/// all of them. Nothing here writes, apart from the store's own pruning of expired acknowledgements.
/// </summary>
public sealed partial class MonitorFeedImplementation : IMonitorFeedApiController
{
    /// <summary>How long the status composition is reused.</summary>
    public static readonly TimeSpan StatusTtl = TimeSpan.FromSeconds(5);

    /// <summary>How long a blocked-session count is reused.</summary>
    public static readonly TimeSpan BlockedSessionsTtl = TimeSpan.FromSeconds(15);

    /// <summary>How long the heartbeat overview is reused.</summary>
    public static readonly TimeSpan HeartbeatTtl = TimeSpan.FromSeconds(30);

    /// <summary>How long the acknowledgement snapshot is reused (writes drop it).</summary>
    public static readonly TimeSpan AcknowledgementsTtl = TimeSpan.FromSeconds(5);

    /// <summary>How long the last-hour metrics composition is reused.</summary>
    public static readonly TimeSpan MetricsTtl = TimeSpan.FromSeconds(30);

    /// <summary>How long the history composition is reused.</summary>
    public static readonly TimeSpan HistoryTtl = TimeSpan.FromMinutes(5);

    private readonly HttpContext _context;
    private readonly IPlatform _platform;
    private readonly IConfiguration _configuration;
    private readonly IEndpointAuthorizationService _authorization;
    private readonly IMessageTrackingStore _trackingStore;
    private readonly IEndpointMetadataStore _metadataStore;
    private readonly IMetricsStore _metricsStore;
    private readonly IMonitorMetricsStore? _monitorStore;
    private readonly IStoreResultCache _storeResultCache;
    private readonly IMonitorFeedCache _feedCache;
    private readonly IMonitorAcknowledgementService _acknowledgements;
    private readonly IHeartbeatService _heartbeats;
    private readonly TimeProvider _time;
    private readonly ILogger<MonitorFeedImplementation> _logger;

    /// <summary>Creates the controller implementation.</summary>
    public MonitorFeedImplementation(
        IHttpContextAccessor contextAccessor,
        IPlatform platform,
        IConfiguration configuration,
        IEndpointAuthorizationService authorization,
        IMessageTrackingStore trackingStore,
        IEndpointMetadataStore metadataStore,
        IMetricsStore metricsStore,
        IStoreResultCache storeResultCache,
        IMonitorFeedCache feedCache,
        IMonitorAcknowledgementService acknowledgements,
        IHeartbeatService heartbeats,
        TimeProvider time,
        ILogger<MonitorFeedImplementation> logger,
        IMonitorMetricsStore? monitorStore = null)
    {
        ArgumentNullException.ThrowIfNull(contextAccessor);
        _context = contextAccessor.HttpContext!;
        _platform = platform;
        _configuration = configuration;
        _authorization = authorization;
        _trackingStore = trackingStore;
        _metadataStore = metadataStore;
        _metricsStore = metricsStore;
        _monitorStore = monitorStore;
        _storeResultCache = storeResultCache;
        _feedCache = feedCache;
        _acknowledgements = acknowledgements;
        _heartbeats = heartbeats;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<ActionResult<MonitorFeedStatus>> GetMonitorFeedStatusAsync()
    {
        if (await AuthorizeAsync() is not { } tokenAccess)
            return new ForbidResult();

        if (await ReadAsync("status", "monitor-feed:status", StatusTtl, ComposeStatusAsync) is not { } snapshot)
            return Unavailable();

        // Caller- and moment-specific fields are added outside the shared cached value.
        return new MonitorFeedStatus
        {
            Environment = _configuration.GetValue<string>("Environment"),
            PlatformName = _platform.GetType().Assembly.GetName().Name,
            ServerTimeUtc = _time.GetUtcNow().UtcDateTime,
            GeneratedAtUtc = snapshot.GeneratedAtUtc,
            Stale = snapshot.Stale,
            TokenAccess = tokenAccess,
            Endpoints = snapshot.Value,
        };
    }

    /// <inheritdoc />
    public async Task<ActionResult<MonitorFeedMetrics>> GetMonitorFeedMetricsAsync()
    {
        if (await AuthorizeAsync() is null)
            return new ForbidResult();

        var minute = MonitorFeedWindows.FloorToMinute(_time.GetUtcNow().UtcDateTime);
        if (await ReadAsync("metrics", $"monitor-feed:metrics:{minute:o}", MetricsTtl, ComposeMetricsAsync) is not { } snapshot)
            return Unavailable();

        // A new response object: the cached composition is shared by every caller.
        return new MonitorFeedMetrics
        {
            GeneratedAtUtc = snapshot.GeneratedAtUtc,
            Stale = snapshot.Stale,
            LastHour = snapshot.Value.LastHour,
            Fleet = snapshot.Value.Fleet,
            Endpoints = snapshot.Value.Endpoints,
        };
    }

    /// <inheritdoc />
    public async Task<ActionResult<MonitorFeedHistory>> GetMonitorFeedHistoryAsync()
    {
        if (await AuthorizeAsync() is null)
            return new ForbidResult();

        var hour = MonitorFeedWindows.FloorToHour(_time.GetUtcNow().UtcDateTime);
        if (await ReadAsync("history", $"monitor-feed:history:{hour:o}", HistoryTtl, ComposeHistoryAsync) is not { } snapshot)
            return Unavailable();

        return new MonitorFeedHistory
        {
            GeneratedAtUtc = snapshot.GeneratedAtUtc,
            Stale = snapshot.Stale,
            LastThreeDays = snapshot.Value.LastThreeDays,
            LatencyLastDay = snapshot.Value.LatencyLastDay,
            Endpoints = snapshot.Value.Endpoints,
        };
    }

    /// <summary>
    /// True for a dashboard token caller, false for a signed-in site Reader, null for anyone else.
    /// The token identity is recognised by its authentication type, so a claim of the same name
    /// from an identity provider cannot pass for it.
    /// </summary>
    private async Task<bool?> AuthorizeAsync()
    {
        // Every response is per-moment wall data; nothing between here and the browser may keep it.
        _context.Response.Headers.CacheControl = "no-store";

        var tokenIdentity = _context.User.Identities.Any(identity =>
            identity.IsAuthenticated
            && string.Equals(identity.AuthenticationType, DashboardTokenDefaults.SchemeName, StringComparison.Ordinal)
            && identity.HasClaim(claim => claim.Type == DashboardTokenDefaults.TokenIdClaim));
        if (tokenIdentity)
            return true;

        return await _authorization.HasRoleAsync(AccessRole.Reader) ? false : null;
    }

    private async Task<MonitorFeedSnapshot<T>?> ReadAsync<T>(string operation, string key, TimeSpan ttl, Func<Task<T>> compose)
        where T : class
    {
        try
        {
            return await _feedCache.GetAsync(operation, key, ttl, compose);
        }
#pragma warning disable CA1031 // Nothing earlier to fall back on: report 503, the wall keeps retrying.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogFeedUnavailable(_logger, ex, operation);
            return null;
        }
    }

    private static ObjectResult Unavailable() => new("The store could not be read.") { StatusCode = StatusCodes.Status503ServiceUnavailable };

    // ───────── Status ─────────

    private async Task<List<MonitorFeedEndpoint>> ComposeStatusAsync()
    {
        var environment = _configuration.GetValue<string>("Environment");
        var endpointIds = (_platform.Endpoints ?? [])
            .Select(e => e.Id)
            .Where(id => EndpointVisibility.IsListed(id, environment))
            .ToList();

        var statuses = new MonitorFeedEndpoint[endpointIds.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Min(System.Environment.ProcessorCount, 4) };
        await Parallel.ForEachAsync(Enumerable.Range(0, endpointIds.Count), options, async (i, _) =>
        {
            var status = await EndpointStatusProjection.ReadOrStubAsync(_storeResultCache, _trackingStore, _logger, endpointIds[i]);
            statuses[i] = new MonitorFeedEndpoint
            {
                Status = status,
                BlockedSessionCount = status.StorageStatus == "unavailable" ? null : await BlockedSessionCountAsync(endpointIds[i]),
            };
        });

        // Subscription status as stored in endpoint metadata. The Endpoints page probes Service
        // Bus for unknown ones; a wall polling every few seconds does not.
        var metadata = await _metadataStore.GetMetadatas(endpointIds) ?? new List<StoredEndpointMetadata>();
        EndpointStatusProjection.ApplySubscriptionStatus(statuses.Select(s => s.Status), metadata);

        var heartbeats = await HeartbeatsAsync();
        var acknowledgements = await _storeResultCache.GetOrCreateAsync(
            MonitorAcknowledgementService.ActiveSnapshotCacheKey, AcknowledgementsTtl, _acknowledgements.GetActiveAsync);

        foreach (var endpoint in statuses)
        {
            var id = endpoint.Status.EndpointId;
            if (heartbeats.TryGetValue(id, out var heartbeat))
                endpoint.Heartbeat = heartbeat;
            var acknowledgement = acknowledgements.FirstOrDefault(a => string.Equals(a.EndpointId, id, StringComparison.OrdinalIgnoreCase));
            if (acknowledgement is not null)
                endpoint.Acknowledgement = MonitorImplementation.ToApi(acknowledgement, id);
        }

        return statuses.ToList();
    }

    private async Task<int?> BlockedSessionCountAsync(string endpointId)
    {
        if (_monitorStore is null)
            return null;

        try
        {
            return await _storeResultCache.GetOrCreateAsync(
                $"monitor-feed:blocked-sessions:{endpointId}", BlockedSessionsTtl,
                () => _monitorStore.GetBlockedSessionCount(endpointId));
        }
#pragma warning disable CA1031 // One endpoint's count must not fail the wall.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogBlockedSessionsUnavailable(_logger, ex, endpointId);
            return null;
        }
    }

    private async Task<Dictionary<string, MonitorFeedHeartbeat>> HeartbeatsAsync()
    {
        try
        {
            var overview = await _storeResultCache.GetOrCreateAsync("monitor-feed:heartbeat", HeartbeatTtl, _heartbeats.GetOverviewAsync);
            return overview
                .Where(row => row.IsHeartbeatEnabled != false && !string.IsNullOrEmpty(row.EndpointId))
                .GroupBy(row => row.EndpointId, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    g => g.Key,
                    g => new MonitorFeedHeartbeat
                    {
                        Liveness = HeartbeatImplementation.ToLiveness(g.First().Status),
                        // The last answered probe; LastStartTime is the last one sent.
                        LastAnsweredUtc = g.First().LastEndTime,
                    },
                    StringComparer.OrdinalIgnoreCase);
        }
#pragma warning disable CA1031 // Heartbeat is a side signal; the wall shows the rest without it.
        catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
        {
            LogHeartbeatUnavailable(_logger, ex);
            return new Dictionary<string, MonitorFeedHeartbeat>(StringComparer.OrdinalIgnoreCase);
        }
    }

    // ───────── Metrics ─────────

    private async Task<MonitorFeedMetrics> ComposeMetricsAsync()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var series = MonitorFeedWindows.LastHourByMinute(now);
        var trailing = MonitorFeedWindows.TrailingHour(now);

        var seriesTask = _metricsStore.GetTimeSeriesMetrics(series.From, series.To, MonitorFeedWindows.MinuteKeyLength, "minute");
        var countsTask = _metricsStore.GetEndpointMetrics(trailing.From, trailing.To);
        var latencyTask = _metricsStore.GetEndpointLatencyMetrics(trailing.From, trailing.To);
        await Task.WhenAll(seriesTask, countsTask, latencyTask);

        var counts = await countsTask;
        var latency = await latencyTask;
        var endpoints = new Dictionary<string, (int Published, int Handled, int Failed, List<LatencyAggregate> Queue, List<LatencyAggregate> Processing)>(StringComparer.OrdinalIgnoreCase);
        (int Published, int Handled, int Failed, List<LatencyAggregate> Queue, List<LatencyAggregate> Processing) Row(string? id)
        {
            var key = Canonical(id);
            if (!endpoints.TryGetValue(key, out var row))
            {
                row = (0, 0, 0, [], []);
                endpoints[key] = row;
            }
            return row;
        }

        foreach (var c in counts.Published) { var r = Row(c.EndpointId); endpoints[Canonical(c.EndpointId)] = r with { Published = r.Published + c.Count }; }
        foreach (var c in counts.Handled) { var r = Row(c.EndpointId); endpoints[Canonical(c.EndpointId)] = r with { Handled = r.Handled + c.Count }; }
        foreach (var c in counts.Failed) { var r = Row(c.EndpointId); endpoints[Canonical(c.EndpointId)] = r with { Failed = r.Failed + c.Count }; }
        foreach (var l in latency.Latencies)
        {
            var r = Row(l.EndpointId);
            r.Queue.Add(l.Queue);
            r.Processing.Add(l.Processing);
        }

        return new MonitorFeedMetrics
        {
            LastHour = ToApi(await seriesTask),
            Fleet = new MonitorFeedCounts
            {
                Published = endpoints.Values.Sum(e => e.Published),
                Handled = endpoints.Values.Sum(e => e.Handled),
                Failed = endpoints.Values.Sum(e => e.Failed),
                Queue = MetricsImplementation.ToDto(MonitorFeedWindows.Combine(latency.Latencies.Select(l => l.Queue))),
                Processing = MetricsImplementation.ToDto(MonitorFeedWindows.Combine(latency.Latencies.Select(l => l.Processing))),
            },
            Endpoints = endpoints
                .OrderBy(e => e.Key, StringComparer.OrdinalIgnoreCase)
                .Select(e => new MonitorFeedEndpointMetrics
                {
                    EndpointId = e.Key,
                    Counts = new MonitorFeedCounts
                    {
                        Published = e.Value.Published,
                        Handled = e.Value.Handled,
                        Failed = e.Value.Failed,
                        Queue = MetricsImplementation.ToDto(MonitorFeedWindows.Combine(e.Value.Queue)),
                        Processing = MetricsImplementation.ToDto(MonitorFeedWindows.Combine(e.Value.Processing)),
                    },
                })
                .ToList(),
        };
    }

    // ───────── History ─────────

    private async Task<MonitorFeedHistory> ComposeHistoryAsync()
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var days = MonitorFeedWindows.LastThreeDaysByHour(now);
        var day = MonitorFeedWindows.LastDayByHour(now);
        var trailing = MonitorFeedWindows.TrailingDay(now);

        var seriesTask = _metricsStore.GetTimeSeriesMetrics(days.From, days.To, MonitorFeedWindows.HourKeyLength, "hour");
        var latencyTask = _metricsStore.GetEndpointLatencyMetrics(trailing.From, trailing.To);
        var latencySeriesTask = _monitorStore?.GetLatencyTimeSeriesMetrics(day.From, day.To, MonitorFeedWindows.HourKeyLength, "hour")
            ?? Task.FromResult<LatencyTimeSeriesResult>(null!);
        await Task.WhenAll(seriesTask, latencyTask, latencySeriesTask);

        var latencySeries = await latencySeriesTask;
        return new MonitorFeedHistory
        {
            LastThreeDays = ToApi(await seriesTask),
            LatencyLastDay = latencySeries?.DataPoints
                .Select(p => new MonitorFeedLatencyPoint
                {
                    Timestamp = p.Timestamp,
                    Queue = MetricsImplementation.ToDto(p.Queue),
                    Processing = MetricsImplementation.ToDto(p.Processing),
                })
                .ToList(),
            Endpoints = (await latencyTask).Latencies
                .GroupBy(l => Canonical(l.EndpointId), StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase)
                .Select(g => new MonitorFeedEndpointHistory
                {
                    EndpointId = g.Key,
                    Processing = MetricsImplementation.ToDto(MonitorFeedWindows.Combine(g.Select(l => l.Processing))),
                })
                .ToList(),
        };
    }

    private static List<TimeSeriesDataPoint> ToApi(TimeSeriesResult result)
        => result.DataPoints.Select(p => new TimeSeriesDataPoint
        {
            Timestamp = p.Timestamp,
            Published = p.Published,
            Handled = p.Handled,
            Failed = p.Failed,
        }).ToList();

    // The platform's spelling of an endpoint id; metrics rows may differ in case.
    private string Canonical(string? endpointId)
    {
        var id = endpointId ?? string.Empty;
        return _platform.Endpoints?
            .FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase))?.Id ?? id;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Monitor feed {Operation} unavailable: the store could not be read and nothing earlier is cached")]
    private static partial void LogFeedUnavailable(ILogger logger, Exception exception, string operation);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Blocked-session count unavailable for endpoint {EndpointId}")]
    private static partial void LogBlockedSessionsUnavailable(ILogger logger, Exception exception, string endpointId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Heartbeat overview unavailable for the Monitor feed")]
    private static partial void LogHeartbeatUnavailable(ILogger logger, Exception exception);
}
