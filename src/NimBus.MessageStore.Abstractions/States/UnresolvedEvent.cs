using NimBus.Core.Messages;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;

namespace NimBus.MessageStore;

public class UnresolvedEvent
{
    public DateTime UpdatedAt { get; set; }
    public DateTime EnqueuedTimeUtc { get; set; }

    //Identifiers
    public string EventId { get; set; } = null!;
    public string? SessionId { get; set; }
    public string? CorrelationId { get; set; }

    //Servicebus related Fields
    [JsonConverter(typeof(StringEnumConverter))]
    public ResolutionStatus ResolutionStatus { get; set; }

    [JsonConverter(typeof(StringEnumConverter))]
    public EndpointRole EndpointRole { get; set; }
    public string EndpointId { get; set; } = null!;
    public int? RetryCount { get; set; }
    public int? RetryLimit { get; set; }

    [JsonConverter(typeof(StringEnumConverter))]
    public MessageType MessageType { get; set; }
    public string? DeadLetterReason { get; set; }
    public string? DeadLetterErrorDescription { get; set; }

    //References
    public string? LastMessageId { get; set; }
    public string? OriginatingMessageId { get; set; }
    public string? ParentMessageId { get; set; }
    public string? Reason { get; set; }
    public string? OriginatingFrom {  get; set; }

    //Event
    public string? EventTypeId { get; set; }
    public string? To { get; set; }
    public string? From { get; set; }
    public MessageContent MessageContent { get; set; } = null!;

    // Per-message timings carried on the response message (subscriber →
    // Resolver). Nullable: original publishes don't have them, and old
    // documents written before this field existed deserialize as null.
    public long? QueueTimeMs { get; set; }
    public long? ProcessingTimeMs { get; set; }

    // PendingHandoff sub-status discriminator. null for ordinary Pending
    // entries; "Handoff" when the row was projected from a
    // PendingHandoffResponse so the WebApp can render an "Awaiting external"
    // badge without changing the ResolutionStatus enum.
    public string? PendingSubStatus { get; set; }
    public string? HandoffReason { get; set; }
    public string? ExternalJobId { get; set; }
    public DateTime? ExpectedBy { get; set; }

    // CloudEvents identity of the inbound CloudEvent (null for native messages).
    // Populated by the Resolver from the response message so CloudEvents identity
    // surfaces in the message store / management UI alongside the native fields.
    public string? CloudEventId { get; set; }
    public string? CloudEventSource { get; set; }
    public string? CloudEventType { get; set; }
    public string? CloudEventSubject { get; set; }
}
