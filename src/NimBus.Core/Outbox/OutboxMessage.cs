using System;

namespace NimBus.Core.Outbox;

/// <summary>
/// Represents a message stored in the transactional outbox, pending dispatch to Service Bus.
/// </summary>
public class OutboxMessage
{
    /// <summary>
    /// Unique identifier for this outbox entry.
    /// </summary>
    public string Id { get; set; } = null!;

    /// <summary>
    /// The NimBus message ID.
    /// </summary>
    public string? MessageId { get; set; }

    /// <summary>
    /// The destination endpoint (topic name). Surfaced as the
    /// <c>nimbus.endpoint</c> tag on outbox enqueue/dispatch metrics and as
    /// the <c>messaging.destination.name</c> attribute on the dispatch span.
    /// Captured at enqueue time so the dispatcher does not have to
    /// deserialize the payload to read it.
    /// </summary>
    public string? To { get; set; }

    /// <summary>
    /// The event type identifier.
    /// </summary>
    public string? EventTypeId { get; set; }

    /// <summary>
    /// The session ID for ordered delivery.
    /// </summary>
    public string? SessionId { get; set; }

    /// <summary>
    /// The correlation ID for tracing.
    /// </summary>
    public string? CorrelationId { get; set; }

    /// <summary>
    /// The serialized IMessage payload (JSON).
    /// </summary>
    public string Payload { get; set; } = null!;

    /// <summary>
    /// Scheduled enqueue delay in minutes (0 for immediate).
    /// </summary>
    public int EnqueueDelayMinutes { get; set; }

    /// <summary>
    /// When this outbox entry was created.
    /// </summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>
    /// Absolute scheduled delivery time. Null for immediate delivery.
    /// Used by <see cref="ISender.ScheduleMessage"/> via the outbox.
    /// </summary>
    public DateTime? ScheduledEnqueueTimeUtc { get; set; }

    /// <summary>
    /// When this outbox entry was dispatched to Service Bus. Null if pending.
    /// </summary>
    public DateTime? DispatchedAtUtc { get; set; }

    /// <summary>
    /// W3C <c>traceparent</c> captured from <see cref="System.Diagnostics.Activity.Current"/>
    /// at the moment this row was persisted. Used by the dispatcher to attach the original
    /// publish context as an <see cref="System.Diagnostics.ActivityLink"/> on the dispatch
    /// span. Null on rows persisted before W3C capture was wired in.
    /// </summary>
    public string? TraceParent { get; set; }

    /// <summary>
    /// W3C <c>tracestate</c> captured alongside <see cref="TraceParent"/>. Null if the
    /// originating activity carried no tracestate.
    /// </summary>
    public string? TraceState { get; set; }
}
