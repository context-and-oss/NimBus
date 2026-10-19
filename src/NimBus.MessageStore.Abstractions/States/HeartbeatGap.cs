using System;
using Newtonsoft.Json;

namespace NimBus.MessageStore.States;

/// <summary>A contiguous period in which an endpoint did not answer heartbeat probes.</summary>
public class HeartbeatGap
{
    /// <summary>Composite provider document id.</summary>
    [JsonProperty(PropertyName = "id")]
    public string Id { get; set; } = null!;

    /// <summary>Endpoint affected by the outage.</summary>
    public string EndpointId { get; set; } = null!;

    /// <summary>Start time of the first missed probe.</summary>
    public DateTime FromUtc { get; set; }

    /// <summary>Start time of the first received probe, or null while still silent.</summary>
    public DateTime? ToUtc { get; set; }

    /// <summary>Last SDK version observed before the outage.</summary>
    public string SdkVersionBefore { get; set; } = null!;

    /// <summary>SDK version observed when the endpoint returned.</summary>
    public string SdkVersionAfter { get; set; } = null!;

    /// <summary>Cosmos item TTL in seconds, or -1 while the outage is open.</summary>
    [JsonProperty(PropertyName = "ttl")]
    public int TimeToLiveSeconds { get; set; } = -1;
}
