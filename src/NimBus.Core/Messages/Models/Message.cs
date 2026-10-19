using System;
using NimBus.Core.CloudEvents;

namespace NimBus.Core.Messages;

public interface IMessage
{
    string EventId { get; }
    string To { get; }
    string SessionId { get; }
    string? CorrelationId { get; }
    string? MessageId { get; }
    MessageType MessageType { get; }
    MessageContent MessageContent { get; }

    /// <summary>
    /// The message this message was a response to.
    /// 
    /// If this message was the first for the Event (EventRequest message type), ParentMessageId will be Constants.Self. 
    /// </summary>
    /// <value></value>
    string? ParentMessageId { get; }

    /// <summary>
    /// The message that initially spawned the Event -- the chronologically first message.
    /// 
    /// If this message was the first for the Event (EventRequest message type), OriginatingMessageId will be Constants.Self. 
    /// </summary>
    /// <value></value>
    string? OriginatingMessageId { get; }
    int? RetryCount { get; }
    string? From { get; }
    string? OriginatingFrom { get; }

    public string EventTypeId { get; }

    /// <summary>
    /// The original session ID for deferred messages sent to the non-session subscription.
    /// </summary>
    string? OriginalSessionId { get; }

    /// <summary>
    /// The sequence number for ordering deferred messages within a session.
    /// </summary>
    int? DeferralSequence { get; }

    /// <summary>
    /// W3C trace context diagnostic ID for distributed tracing.
    /// </summary>
    string? DiagnosticId => null;

    /// <summary>
    /// Reply-to destination for request/response patterns.
    /// Set by the requester; the responder sends the response to this address.
    /// </summary>
    string? ReplyTo => null;

    /// <summary>
    /// Session ID for reply correlation in request/response patterns.
    /// The requester listens on this session for the response.
    /// </summary>
    string? ReplyToSessionId => null;

    /// <summary>
    /// Time the message spent in Service Bus before the handler was invoked
    /// (enqueued → handler entry). Populated on response messages by the
    /// receiving subscriber so the Resolver can persist it on the audit doc.
    /// Null on original publishes.
    /// </summary>
    long? QueueTimeMs => null;

    /// <summary>
    /// Time the handler took to run (handler entry → completion / failure).
    /// Populated on response messages by the receiving subscriber. Null on
    /// original publishes.
    /// </summary>
    long? ProcessingTimeMs => null;

    /// <summary>
    /// Reason the message was dead-lettered, mirrored from the Service Bus
    /// dead-letter properties. Set on dead-letter notification messages
    /// sent to the Resolver so it can record the dead-letter outcome.
    /// </summary>
    string? DeadLetterReason => null;

    /// <summary>
    /// Detailed error description for a dead-lettered message (typically
    /// the formatted exception). Set on dead-letter notification messages
    /// sent to the Resolver — its presence drives the Resolver to classify
    /// the audit record as DeadLettered.
    /// </summary>
    string? DeadLetterErrorDescription => null;

    /// <summary>
    /// Free-text reason supplied by an event handler when it signals
    /// PendingHandoff via <c>IEventHandlerContext.MarkPendingHandoff</c>.
    /// Carried on <see cref="MessageType.PendingHandoffResponse"/> to the
    /// Resolver for audit-row enrichment.
    /// </summary>
    string? HandoffReason => null;

    /// <summary>
    /// Optional external-system identifier (e.g. a D365 F&amp;O DMF job id)
    /// for messages awaiting completion of long-running external work.
    /// Carried alongside <see cref="HandoffReason"/>.
    /// </summary>
    string? ExternalJobId => null;

    /// <summary>
    /// Optional UTC deadline by which the external work is expected to
    /// settle. Used by the optional Resolver-side timeout sweeper. Null
    /// means no deadline (operator manages indefinitely).
    /// </summary>
    DateTime? ExpectedBy => null;

    /// <summary>
    /// Pre-serialized <see cref="MessageContent"/> JSON, set by publish
    /// paths that already serialized the content for batch sizing so the
    /// transport doesn't serialize it a second time. Null (the default)
    /// means the transport serializes <see cref="MessageContent"/> itself.
    /// Treat as a cache only: it must always equal
    /// <c>JsonConvert.SerializeObject(MessageContent)</c> at send time.
    /// </summary>
    string? SerializedMessageContent => null;

    /// <summary>
    /// Opt-in CloudEvents publish context. Non-null only when the publisher has
    /// enabled CloudEvents; tells the transport binding to emit this message as a
    /// CloudEvent in the configured content mode. Null (the default) preserves the
    /// native NimBus wire format exactly. Persisted through the outbox so a
    /// CloudEvents message keeps its envelope when dispatched later.
    /// </summary>
    CloudEventPublishContext? CloudEvent => null;

    /// <summary>
    /// CloudEvents <c>id</c> of the inbound CloudEvent this message relates to,
    /// carried on the response sent to the Resolver so the tracking/audit record
    /// preserves the CloudEvent identity. Null (the default) on native messages.
    /// </summary>
    string? CloudEventId => null;

    /// <summary>CloudEvents <c>source</c>, carried to the Resolver. Null for a native message.</summary>
    string? CloudEventSource => null;

    /// <summary>CloudEvents <c>type</c>, carried to the Resolver. Null for a native message.</summary>
    string? CloudEventType => null;

    /// <summary>CloudEvents <c>subject</c>, carried to the Resolver. Null for a native message.</summary>
    string? CloudEventSubject => null;

    /// <summary>
    /// The <see cref="MessageId"/> of the delivery this message stands in for. A RetryRequest
    /// and a parked (deferred) copy reach the endpoint with a fresh <see cref="MessageId"/>,
    /// so they carry the id the source delivered the event with; when the stand-in succeeds,
    /// the inbox records that id too, and a later delivery of the original is a duplicate.
    /// Null on every other message. Unlike <see cref="OriginatingMessageId"/>, which names
    /// the first message of the whole causal chain (possibly another event's), this is
    /// always a delivery of the same event to the same endpoint.
    /// </summary>
    string? InboxMessageId => null;
}

public class Message : IMessage
{
    public string To { get; set; } = null!;

    public string SessionId { get; set; } = null!;

    public MessageType MessageType { get; set; }

    public MessageContent MessageContent { get; set; } = null!;

    public string EventId { get; set; } = null!;

    public string? CorrelationId { get; set; }

    public string? MessageId { get; set; }

    public string? ParentMessageId { get; set; }

    public string? OriginatingMessageId { get; set; }
    public string? From { get; set; }
    public string? OriginatingFrom { get; set; }
    public int? RetryCount { get; set; }
    public string EventTypeId { get; set; } = null!;
    public string? OriginalSessionId { get; set; }
    public int? DeferralSequence { get; set; }
    public string? DiagnosticId { get; set; }
    public string? ReplyTo { get; set; }
    public string? ReplyToSessionId { get; set; }
    public long? QueueTimeMs { get; set; }
    public long? ProcessingTimeMs { get; set; }
    public string? DeadLetterReason { get; set; }
    public string? DeadLetterErrorDescription { get; set; }
    public string? HandoffReason { get; set; }
    public string? ExternalJobId { get; set; }
    public DateTime? ExpectedBy { get; set; }

    /// <summary>
    /// See <see cref="IMessage.SerializedMessageContent"/>. JsonIgnore is
    /// mandatory: OutboxSender serializes the whole <see cref="Message"/>
    /// into the outbox Payload column — persisting this cache would double
    /// the payload and round-trip a potentially stale body.
    /// </summary>
    [Newtonsoft.Json.JsonIgnore]
    public string? SerializedMessageContent { get; set; }

    /// <summary>
    /// See <see cref="IMessage.CloudEvent"/>. Intentionally NOT
    /// <c>[JsonIgnore]</c>: the outbox serializes the whole <see cref="Message"/>
    /// into its Payload column and this envelope must survive that round-trip so
    /// an outbox-dispatched CloudEvents message is still emitted as a CloudEvent.
    /// </summary>
    public CloudEventPublishContext? CloudEvent { get; set; }

    /// <summary>See <see cref="IMessage.CloudEventId"/>.</summary>
    public string? CloudEventId { get; set; }

    /// <summary>See <see cref="IMessage.CloudEventSource"/>.</summary>
    public string? CloudEventSource { get; set; }

    /// <summary>See <see cref="IMessage.CloudEventType"/>.</summary>
    public string? CloudEventType { get; set; }

    /// <summary>See <see cref="IMessage.CloudEventSubject"/>.</summary>
    public string? CloudEventSubject { get; set; }

    /// <summary>See <see cref="IMessage.InboxMessageId"/>.</summary>
    public string? InboxMessageId { get; set; }
}
