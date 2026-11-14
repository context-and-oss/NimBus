using System;
using Newtonsoft.Json;

namespace NimBus.MessageStore.States;

/// <summary>Durable heartbeat counters for one endpoint and UTC calendar day.</summary>
public class HeartbeatUptimeDay
{
    /// <summary>Composite provider document id.</summary>
    [JsonProperty(PropertyName = "id")]
    public string Id { get; set; } = null!;

    /// <summary>Endpoint whose probes are counted.</summary>
    public string EndpointId { get; set; } = null!;

    /// <summary>UTC midnight identifying the calendar day.</summary>
    public DateTime DayUtc { get; set; }

    /// <summary>Total settled probes represented by this row.</summary>
    public int Expected { get; set; }

    /// <summary>Settled probes that proved endpoint reachability.</summary>
    public int Received { get; set; }

    /// <summary>Settled probes that failed or timed out.</summary>
    public int Missed { get; set; }

    /// <summary>Seconds during which probes represented platform observation.</summary>
    public int ObservedSeconds { get; set; }

    /// <summary>Longest outage touching this day, in seconds.</summary>
    public int LongestGapSeconds { get; set; }

    /// <summary>Latest probe start folded into the endpoint history.</summary>
    public DateTime LastBeatUtc { get; set; }

    /// <summary>Cosmos item TTL in seconds; ignored by other providers.</summary>
    [JsonProperty(PropertyName = "ttl")]
    public int TimeToLiveSeconds { get; set; }
}
