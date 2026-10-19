using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace NimBus.MessageStore.States;

/// <summary>
/// Liveness of one platform service (currently only the Resolver), measured by a
/// round-trip probe rather than an HTTP ping.
/// </summary>
/// <remarks>
/// Deliberately not stored as an endpoint <see cref="Heartbeat"/>: the Cosmos
/// backend embeds heartbeats in the endpoint's metadata document, so a
/// "Resolver" heartbeat would surface as a phantom endpoint.
/// <para>
/// A probe is in flight exactly while <see cref="LastProbeMessageId"/> is set;
/// <see cref="Status"/> holds the last <em>settled</em> outcome so an in-flight
/// probe never masks a service that is known to be down, matching the endpoint
/// heartbeat's semantics.
/// </para>
/// </remarks>
public class ServiceHealth
{
    /// <summary>Service identifier, e.g. <c>Resolver</c>. Also the document id.</summary>
    [JsonProperty(PropertyName = "id")]
    public string ServiceId { get; set; } = null!;

    /// <summary>Last settled outcome. Never <c>Pending</c>.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public HeartbeatStatus Status { get; set; } = HeartbeatStatus.Unknown;

    /// <summary>Informational assembly version reported by the responding service.</summary>
    public string Version { get; set; } = null!;

    /// <summary>Correlation id of the in-flight probe; null once it has settled.</summary>
    public string? LastProbeMessageId { get; set; }

    /// <summary>When the most recent probe was sent, regardless of outcome. Drives both the send claim and the timeout sweep.</summary>
    public DateTime? LastProbeSentUtc { get; set; }

    /// <summary>When the service last answered a probe.</summary>
    public DateTime? LastSeenUtc { get; set; }

    /// <summary>Round-trip of the last answered probe.</summary>
    public long? RoundTripMs { get; set; }
}
