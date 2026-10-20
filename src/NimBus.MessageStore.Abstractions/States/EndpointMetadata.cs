using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System.Collections.Generic;

namespace NimBus.MessageStore.States;

public class EndpointMetadata
{
    [JsonProperty(PropertyName = "id")] public string EndpointId { get; set; } = null!;
    public string? EndpointOwner { get; set; }
    public string? EndpointOwnerTeam { get; set; }
    public string? EndpointOwnerEmail { get; set; }

    /// <summary>Heartbeat opt-in; null when the endpoint has never been configured either way.</summary>
    public bool? IsHeartbeatEnabled { get; set; }

    /// <summary>
    /// Rollup of <see cref="Heartbeats"/>: the most recent settled outcome, so an
    /// in-flight Pending probe never masks the last known state.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public HeartbeatStatus? EndpointHeartbeatStatus { get; set; }

    public List<TechnicalContact>? TechnicalContacts { get; set; }

    /// <summary>Recent heartbeat probes for this endpoint, newest last, pruned by the store.</summary>
    public List<Heartbeat>? Heartbeats { get; set; }

    public bool? SubscriptionStatus { get; set; } = null;
}
