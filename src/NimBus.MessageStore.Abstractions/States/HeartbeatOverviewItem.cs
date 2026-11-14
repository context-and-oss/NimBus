using System;

namespace NimBus.MessageStore.States;

/// <summary>
/// One row of the operator-facing heartbeat overview: an endpoint's current status
/// plus the details of its last actual response.
/// </summary>
/// <remarks>
/// <see cref="Status"/> is the last <em>settled</em> outcome (On/Off/Unsupported);
/// an in-flight Pending probe never masks it. The response fields
/// (<see cref="LastReceivedTime"/>, <see cref="LastEndTime"/>,
/// <see cref="RoundTripMs"/>, <see cref="SdkVersion"/>) come from the last probe
/// that was actually answered — a swept, timed-out row carried no response.
/// </remarks>
public class HeartbeatOverviewItem
{
    /// <summary>The endpoint this row describes.</summary>
    public string EndpointId { get; set; } = null!;

    /// <summary>Opt-in flag; null when the endpoint has never been configured either way.</summary>
    public bool? IsHeartbeatEnabled { get; set; }

    /// <summary>Correlation id of the most recent probe, answered or not.</summary>
    public string MessageId { get; set; } = null!;

    /// <summary>Send time of the most recent probe, answered or not.</summary>
    public DateTime? LastStartTime { get; set; }

    /// <summary>Endpoint-reported receive time of the last answered probe.</summary>
    public DateTime? LastReceivedTime { get; set; }

    /// <summary>Platform-side completion time of the last answered probe.</summary>
    public DateTime? LastEndTime { get; set; }

    /// <summary>Round-trip of the last answered probe.</summary>
    public long? RoundTripMs { get; set; }

    /// <summary>NimBus SDK version from the last answered probe; empty for pre-heartbeat SDKs.</summary>
    public string SdkVersion { get; set; } = null!;

    /// <summary>Last settled outcome. Never reflects an in-flight probe unless nothing has settled yet.</summary>
    public HeartbeatStatus Status { get; set; } = HeartbeatStatus.Unknown;
}
