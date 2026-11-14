using Newtonsoft.Json;
using System;

namespace NimBus.MessageStore.States;

/// <summary>
/// Per-event "reported" marker — a manual operational flag (not a ticket-system
/// integration) so multiple operators can see that a failed event has already
/// been reported. One row/document per (EndpointId, EventId); upserted on toggle.
/// </summary>
public class EventReport
{
    /// <summary>Cosmos document id ("{endpointId}_{eventId}"); ignored by the SQL Server store.</summary>
    [JsonProperty(PropertyName = "id")]
    public string Id { get; set; } = null!;

    /// <summary>Endpoint that owns the event. Cosmos partition key.</summary>
    public string EndpointId { get; set; } = null!;

    public string EventId { get; set; } = null!;

    public bool IsReported { get; set; }

    /// <summary>Display name of the operator who last toggled the marker.</summary>
    public string? ReportedBy { get; set; }

    /// <summary>UTC timestamp of the last toggle.</summary>
    public DateTime? ReportedAtUtc { get; set; }

    /// <summary>
    /// External ticket reference (e.g. an incident number) the event was reported
    /// under, or null when marked reported without a ticket. Cleared together
    /// with the marker.
    /// </summary>
    public string? TicketId { get; set; }
}
