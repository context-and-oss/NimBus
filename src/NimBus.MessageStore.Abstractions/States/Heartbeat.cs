using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace NimBus.MessageStore.States;

/// <summary>
/// One heartbeat probe of an endpoint: the request the platform sent and, once it
/// settles, the answer it got back. Rows are stored per endpoint (embedded in the
/// endpoint metadata document on Cosmos, a child table on SQL Server) and pruned
/// to the most recent few.
/// </summary>
/// <remarks>
/// This is the stored state, not the wire event. The message that travels over
/// Service Bus is <c>NimBus.Core.Events.Heartbeat</c>.
/// </remarks>
public class Heartbeat
{
    /// <summary>Correlation id of the probe — the row key within an endpoint.</summary>
    public string MessageId { get; set; } = null!;

    /// <summary>When the platform sent the probe.</summary>
    public DateTime StartTime { get; set; }

    /// <summary>When the endpoint received the probe, as reported by the endpoint.</summary>
    public DateTime ReceivedTime { get; set; }

    /// <summary>When the platform received the answer.</summary>
    public DateTime EndTime { get; set; }

    /// <summary>
    /// Schedule interval represented by this probe. Persisted on the pending row and
    /// preserved when the response settles it so historical coverage remains correct
    /// after settings changes. Zero identifies a legacy row.
    /// </summary>
    public int IntervalSeconds { get; set; }

    /// <summary>NimBus SDK version reported by the answering endpoint; absent for pre-heartbeat SDKs.</summary>
    public string? SdkVersion { get; set; }

    /// <summary>Outcome of this probe. <c>Pending</c> until it is answered or swept.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public HeartbeatStatus EndpointHeartbeatStatus { get; set; } = HeartbeatStatus.Unknown;
}
