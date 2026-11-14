using NimBus.Core.Messages;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;

namespace NimBus.MessageStore;

public class MessageEntity : IReceivedMessage
{
    public string EventId { get; set; } = null!;

    public string MessageId { get; set; } = null!;

    public string EventTypeId { get; set; } = null!;

    public string? OriginatingMessageId { get; set; }

    public string? ParentMessageId { get; set; }

    public string? From { get; set; }

    public string To { get; set; } = null!;

    public string? OriginatingFrom { get; set; }

    public string SessionId { get; set; } = null!;

    public string? CorrelationId { get; set; }

    public DateTime EnqueuedTimeUtc { get; set; }

    public MessageContent MessageContent { get; set; } = null!;

    [JsonConverter(typeof(StringEnumConverter))]
    public MessageType MessageType { get; set; }

    [JsonConverter(typeof(StringEnumConverter))]
    public EndpointRole EndpointRole { get; set; }

    public string EndpointId { get; set; } = null!;
    public int? RetryCount { get; set; }
    public int? RetryLimit { get; set; }
    public string? DeadLetterReason { get; set; }
    public string? DeadLetterErrorDescription { get; set; }
    public string? OriginalSessionId { get; set; }
    public int? DeferralSequence { get; set; }
    public long? QueueTimeMs { get; set; }
    public long? ProcessingTimeMs { get; set; }

    // PendingHandoff sub-status discriminator. null for ordinary Pending entries;
    // "Handoff" when the audit row was projected from a PendingHandoffResponse.
    public string? PendingSubStatus { get; set; }
    public string? HandoffReason { get; set; }
    public string? ExternalJobId { get; set; }
    public DateTime? ExpectedBy { get; set; }

    // CloudEvents identity of the inbound CloudEvent (null for native messages),
    // projected onto the tracking/audit record so it surfaces in the message store.
    public string? CloudEventId { get; set; }
    public string? CloudEventSource { get; set; }
    public string? CloudEventType { get; set; }
    public string? CloudEventSubject { get; set; }
}