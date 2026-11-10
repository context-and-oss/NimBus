using System;
using System.Collections.Generic;

namespace NimBus.MessageStore.States;

public class EndpointMetricsResult
{
    public List<EndpointEventTypeCount> Published { get; set; } = new();
    public List<EndpointEventTypeCount> Handled { get; set; } = new();
    public List<EndpointEventTypeCount> Failed { get; set; } = new();
}

public class EndpointEventTypeCount
{
    public string EndpointId { get; set; } = null!;
    public string EventTypeId { get; set; } = null!;
    public int Count { get; set; }
}

public class TimeSeriesResult
{
    public string BucketSize { get; set; } = null!;
    public List<TimeSeriesBucket> DataPoints { get; set; } = new();
}

public class TimeSeriesBucket
{
    public string Timestamp { get; set; } = null!;
    public int Published { get; set; }
    public int Handled { get; set; }
    public int Failed { get; set; }
}

public class EventTypeTimeSeriesResult
{
    public string BucketSize { get; set; } = null!;
    public List<EventTypeSeriesEntry> Series { get; set; } = new();
}

// Published-message counts only; dataPoints are sparse (no zero-fill) and sorted ascending.
public class EventTypeSeriesEntry
{
    public string EventTypeId { get; set; } = null!;
    public int Total { get; set; }
    public List<EventTypeSeriesBucket> DataPoints { get; set; } = new();
}

public class EventTypeSeriesBucket
{
    public string Timestamp { get; set; } = null!;
    public int Published { get; set; }
}

public class FailedMessageInfo
{
    public string EndpointId { get; set; } = null!;
    public string EventTypeId { get; set; } = null!;
    public string? ErrorText { get; set; }
    public DateTime EnqueuedTimeUtc { get; set; }
    public string EventId { get; set; } = null!;

    /// <summary>
    /// The failed attempt's own message id: the ErrorResponse's broker-assigned id, which failure
    /// classifications are keyed on. Null when a provider does not project it; consumers must then
    /// treat attempt identity as unavailable rather than guess it from <see cref="EventId"/>.
    /// </summary>
    public string? MessageId { get; set; }

    /// <summary>
    /// The failed attempt's session. Together with <see cref="EventId"/> it forms the stored event id,
    /// <c>{EventId}_{SessionId}</c>, which <c>IMessageTrackingStore.GetEventsByIds</c> requires (a bare
    /// event id finds nothing on Cosmos). Null when a provider does not project it.
    /// </summary>
    public string? SessionId { get; set; }
}

public class EndpointLatencyMetricsResult
{
    public List<EndpointLatencyAggregate> Latencies { get; set; } = new();
}

// One row per (endpoint, eventType) with both timing series side-by-side.
// Computed server-side in Cosmos via AVG/MIN/MAX/COUNT GROUP BY — no
// percentiles (would require pulling raw values into memory; not viable
// at scale). Tail latency is monitored via App Insights histograms instead.
public class EndpointLatencyAggregate
{
    public string EndpointId { get; set; } = null!;
    public string EventTypeId { get; set; } = null!;
    public LatencyAggregate Queue { get; set; } = new();
    public LatencyAggregate Processing { get; set; } = new();
}

/// <summary>
/// Queue and processing time per time bucket across all endpoints
/// (<see cref="Abstractions.IMonitorMetricsStore.GetLatencyTimeSeriesMetrics"/>).
/// </summary>
public class LatencyTimeSeriesResult
{
    /// <summary>The bucket label the caller passed: minute, hour or day.</summary>
    public string BucketSize { get; set; } = null!;

    /// <summary>The buckets with at least one timed outcome, ascending. Empty buckets are absent.</summary>
    public List<LatencyTimeSeriesBucket> DataPoints { get; set; } = new();
}

/// <summary>One bucket of <see cref="LatencyTimeSeriesResult"/>.</summary>
public class LatencyTimeSeriesBucket
{
    /// <summary>The bucket start as ISO-8601 UTC, truncated to the bucket size.</summary>
    public string Timestamp { get; set; } = null!;

    /// <summary>Time spent waiting to be picked up. Count is 0 when no message in the bucket carries it.</summary>
    public LatencyAggregate Queue { get; set; } = new();

    /// <summary>Time spent in the handler. Count is 0 when no message in the bucket carries it.</summary>
    public LatencyAggregate Processing { get; set; } = new();
}

public class LatencyAggregate
{
    public int Count { get; set; }
    public double AvgMs { get; set; }
    public double MinMs { get; set; }
    public double MaxMs { get; set; }
}
