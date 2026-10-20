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

public class LatencyAggregate
{
    public int Count { get; set; }
    public double AvgMs { get; set; }
    public double MinMs { get; set; }
    public double MaxMs { get; set; }
}
