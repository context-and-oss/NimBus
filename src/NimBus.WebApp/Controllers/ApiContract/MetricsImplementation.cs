using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Controllers.ApiContract;

public class MetricsImplementation : IMetricsApiController
{
    private readonly IMetricsStore _metricsStore;
    private readonly IStoreResultCache _storeResultCache;
    private readonly IEndpointAuthorizationService _authorizationService;

    // Metrics aggregates are period-keyed and carry no per-user data, so a 30s
    // TTL bounds the (RU-expensive) cross-partition aggregate queries to at
    // most one run per period per window regardless of how many dashboards poll.
    private static readonly TimeSpan MetricsTtl = TimeSpan.FromSeconds(30);

    /// <summary>Longest custom metrics window; mirrors <see cref="FailedImplementation.MaxWindow"/>.</summary>
    internal static readonly TimeSpan MaxWindow = FailedImplementation.MaxWindow;

    private static BadRequestObjectResult InvalidWindow() => new(
        $"A custom window needs both from and to, from < to, and may span at most {MaxWindow.TotalDays} days.");

    public MetricsImplementation(
        IMetricsStore metricsStore,
        IStoreResultCache storeResultCache,
        IEndpointAuthorizationService authorizationService)
    {
        _metricsStore = metricsStore;
        _storeResultCache = storeResultCache;
        _authorizationService = authorizationService;
    }

    // Metrics are cross-endpoint aggregates, so the read floor is a site role
    // (spec 026 phase D).
    private Task<bool> IsSiteReaderAsync() => _authorizationService.HasRoleAsync(AccessRole.Reader);

    public async Task<ActionResult<MetricsOverview>> GetMetricsOverviewAsync(Period period, DateTime? from = null, DateTime? to = null)
    {
        if (!await IsSiteReaderAsync())
            return new ForbidResult();
        if (ResolveWindow(period, from, to, DateTime.UtcNow) is not { } window)
            return InvalidWindow();

        var result = await _storeResultCache.GetOrCreateAsync(
            $"metrics:overview:{window.CacheKey}",
            MetricsTtl,
            () => _metricsStore.GetEndpointMetrics(window.From, window.To));

        return new MetricsOverview
        {
            Published = result.Published.Select(p => new EndpointEventTypeMessageCount
            {
                EndpointId = p.EndpointId,
                EventTypeId = p.EventTypeId,
                Count = p.Count
            }).ToList(),
            Handled = result.Handled.Select(h => new EndpointEventTypeMessageCount
            {
                EndpointId = h.EndpointId,
                EventTypeId = h.EventTypeId,
                Count = h.Count
            }).ToList(),
            Failed = result.Failed.Select(f => new EndpointEventTypeMessageCount
            {
                EndpointId = f.EndpointId,
                EventTypeId = f.EventTypeId,
                Count = f.Count
            }).ToList()
        };
    }

    public async Task<ActionResult<LatencyOverview>> GetMetricsLatencyAsync(Period period, DateTime? from = null, DateTime? to = null)
    {
        if (!await IsSiteReaderAsync())
            return new ForbidResult();
        if (ResolveWindow(period, from, to, DateTime.UtcNow) is not { } window)
            return InvalidWindow();

        // Aggregated server-side in Cosmos against the per-message timings the
        // Resolver persists on every outcome document — no App Insights
        // dependency, no in-memory raw-value scan. Percentiles are
        // intentionally omitted: Cosmos can't compute them without pulling
        // raw values, and AVG/MIN/MAX gives enough signal for an operator
        // dashboard. Tail latency monitoring lives with the OpenTelemetry
        // histograms (nimbus.message.queue_wait, nimbus.pipeline.duration).
        var result = await _storeResultCache.GetOrCreateAsync(
            $"metrics:latency:{window.CacheKey}",
            MetricsTtl,
            () => _metricsStore.GetEndpointLatencyMetrics(window.From, window.To));

        return new LatencyOverview
        {
            Latencies = result.Latencies.Select(m => new EndpointLatency
            {
                EndpointId = m.EndpointId,
                EventTypeId = m.EventTypeId,
                Queue = ToDto(m.Queue),
                Processing = ToDto(m.Processing),
            }).ToList()
        };
    }

    private static LatencyStats ToDto(LatencyAggregate source)
    {
        if (source is null) return new LatencyStats();
        return new LatencyStats
        {
            Count = source.Count,
            AvgMs = Math.Round(source.AvgMs, 1),
            MinMs = Math.Round(source.MinMs, 1),
            MaxMs = Math.Round(source.MaxMs, 1),
        };
    }

    public async Task<ActionResult<FailedInsightsOverview>> GetMetricsFailedInsightsAsync(Period period, DateTime? from = null, DateTime? to = null)
    {
        if (!await IsSiteReaderAsync())
            return new ForbidResult();
        if (ResolveWindow(period, from, to, DateTime.UtcNow) is not { } window)
            return InvalidWindow();

        // Keep authorization outside the shared cache. Cache the finished aggregate
        // so repeated dashboards avoid both store reads and error normalization.
        return await _storeResultCache.GetOrCreateAsync(
            $"metrics:failed-insights:{window.CacheKey}",
            MetricsTtl,
            () => BuildFailedInsightsAsync(window));
    }

    private async Task<FailedInsightsOverview> BuildFailedInsightsAsync(MetricsWindow window)
    {
        var messages = await _metricsStore.GetFailedMessageInsights(window.From, window.To);

        var groups = messages
            .GroupBy(m => ExtractErrorCategory(m.ErrorText))
            .Select(g =>
            {
                var subGroups = g
                    .GroupBy(m => NormalizeErrorPattern(m.ErrorText))
                    .Select(sg => new ErrorSubGroup
                    {
                        NormalizedPattern = sg.Key,
                        Count = sg.Count(),
                        Endpoints = sg.Select(m => m.EndpointId).Where(e => e != null).Distinct().ToList(),
                        EventTypes = sg.Select(m => m.EventTypeId).Where(e => e != null).Distinct().ToList(),
                        LatestOccurrence = sg.Max(m => m.EnqueuedTimeUtc),
                        ExampleErrorText = sg.First().ErrorText
                    })
                    .OrderByDescending(sg => sg.Count)
                    .ToList();

                return new ErrorPatternGroup
                {
                    ErrorCategory = g.Key,
                    Count = g.Count(),
                    Endpoints = g.Select(m => m.EndpointId).Where(e => e != null).Distinct().ToList(),
                    EventTypes = g.Select(m => m.EventTypeId).Where(e => e != null).Distinct().ToList(),
                    LatestOccurrence = g.Max(m => m.EnqueuedTimeUtc),
                    ExampleErrorText = g.First().ErrorText,
                    SubGroups = subGroups
                };
            })
            .OrderByDescending(g => g.Count)
            .ToList();

        return new FailedInsightsOverview
        {
            Groups = groups,
            TotalFailed = messages.Count
        };
    }

    public async Task<ActionResult<TimeSeriesOverview>> GetMetricsTimeseriesAsync(Period period, DateTime? from = null, DateTime? to = null)
    {
        if (!await IsSiteReaderAsync())
            return new ForbidResult();
        if (ResolveWindow(period, from, to, DateTime.UtcNow) is not { } window)
            return InvalidWindow();

        var result = await _storeResultCache.GetOrCreateAsync(
            $"metrics:timeseries:{window.CacheKey}",
            MetricsTtl,
            () => _metricsStore.GetTimeSeriesMetrics(window.From, window.To, window.SubstringLength, window.BucketLabel));

        return new TimeSeriesOverview
        {
            BucketSize = result.BucketSize,
            DataPoints = result.DataPoints.Select(dp => new TimeSeriesDataPoint
            {
                Timestamp = dp.Timestamp,
                Published = dp.Published,
                Handled = dp.Handled,
                Failed = dp.Failed
            }).ToList()
        };
    }

    public async Task<ActionResult<EventTypeTimeSeriesOverview>> GetMetricsTimeseriesByEventtypeAsync(Period period, DateTime? from = null, DateTime? to = null)
    {
        if (!await IsSiteReaderAsync())
            return new ForbidResult();
        if (ResolveWindow(period, from, to, DateTime.UtcNow) is not { } window)
            return InvalidWindow();

        var result = await _storeResultCache.GetOrCreateAsync(
            $"metrics:timeseries-by-eventtype:{window.CacheKey}",
            MetricsTtl,
            () => _metricsStore.GetEventTypeTimeSeriesMetrics(window.From, window.To, window.SubstringLength, window.BucketLabel));

        return new EventTypeTimeSeriesOverview
        {
            BucketSize = result.BucketSize,
            Series = result.Series.Select(s => new EventTypeSeries
            {
                EventTypeId = s.EventTypeId,
                Total = s.Total,
                DataPoints = s.DataPoints.Select(p => new EventTypeSeriesPoint
                {
                    Timestamp = p.Timestamp,
                    Published = p.Published,
                }).ToList(),
            }).ToList(),
        };
    }

    /// <summary>
    /// The metrics window: a custom from/to when either is given, else the period preset ending at
    /// <paramref name="nowUtc"/>. A custom window keeps its exact bounds and buckets by minute up to
    /// 2 hours, by hour up to 14 days and by day beyond. Null for an invalid custom window: only one
    /// bound, from not before to, or longer than <see cref="MaxWindow"/>.
    /// </summary>
    internal static MetricsWindow? ResolveWindow(Period period, DateTime? from, DateTime? to, DateTime nowUtc)
    {
        if (from.HasValue || to.HasValue)
        {
            if (!from.HasValue || !to.HasValue) return null;

            var start = AsUtc(from.Value);
            var end = AsUtc(to.Value);
            if (end <= start || end - start > MaxWindow) return null;

            var span = end - start;
            var (substringLength, bucketLabel) =
                span <= TimeSpan.FromHours(2) ? (16, "minute")
                : span <= TimeSpan.FromDays(14) ? (13, "hour")
                : (10, "day");
            return new MetricsWindow(start, end, substringLength, bucketLabel, $"{start.Ticks}-{end.Ticks}");
        }

        var (presetLength, presetLabel) = PeriodToBucketConfig(period);
        return new MetricsWindow(nowUtc - PeriodToTimeSpan(period), nowUtc, presetLength, presetLabel, period.ToString());
    }

    // Query-string DateTimes bind as Local or Unspecified depending on the input; read them as UTC.
    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc),
    };

    private static (int substringLength, string label) PeriodToBucketConfig(Period period) => period switch
    {
        Period._1h => (16, "minute"),
        Period._12h => (13, "hour"),
        Period._1d => (13, "hour"),
        Period._3d => (13, "hour"),
        Period._7d => (13, "hour"),
        Period._30d => (10, "day"),
        _ => (13, "hour")
    };

    internal static string ExtractErrorCategory(string? errorText) =>
        ErrorPatternNormalizer.ExtractCategory(errorText);

    internal static string NormalizeErrorPattern(string? errorText) =>
        ErrorPatternNormalizer.Normalize(errorText);

    private static TimeSpan PeriodToTimeSpan(Period period) => period switch
    {
        Period._1h => TimeSpan.FromHours(1),
        Period._12h => TimeSpan.FromHours(12),
        Period._1d => TimeSpan.FromDays(1),
        Period._3d => TimeSpan.FromDays(3),
        Period._7d => TimeSpan.FromDays(7),
        Period._30d => TimeSpan.FromDays(30),
        _ => TimeSpan.FromDays(1)
    };
}

/// <summary>A resolved metrics window: exact UTC bounds, bucket key length and label, and the cache key suffix.</summary>
internal readonly record struct MetricsWindow(DateTime From, DateTime To, int SubstringLength, string BucketLabel, string CacheKey);
