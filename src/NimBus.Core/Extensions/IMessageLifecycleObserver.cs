using NimBus.Core.Messages;
using NimBus.Core.CircuitBreaker;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Extensions;

/// <summary>
/// Observes message lifecycle events without modifying the message flow.
/// All methods have default no-op implementations so observers only need to override events they care about.
/// </summary>
public interface IMessageLifecycleObserver
{
    /// <summary>
    /// Called when a message is received and about to be processed.
    /// </summary>
    Task OnMessageReceived(MessageLifecycleContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Called when a message has been successfully processed.
    /// </summary>
    Task OnMessageCompleted(MessageLifecycleContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Called when a message handler throws an exception.
    /// </summary>
    Task OnMessageFailed(MessageLifecycleContext context, Exception exception, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Called when a message is sent to the dead-letter queue.
    /// </summary>
    Task OnMessageDeadLettered(MessageLifecycleContext context, string reason, Exception? exception = null, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Called when a message arrives for a session that is blocked by an earlier failed event.
    /// The blocking event id is supplied so observers can reference the incident that caused the block.
    /// </summary>
    Task OnSessionBlocked(MessageLifecycleContext context, string? blockedByEventId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Called when inbox deduplication skips an already processed message.
    /// </summary>
    Task OnDuplicateDetected(MessageLifecycleContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>Called when an endpoint circuit changes state.</summary>
    Task OnCircuitStateChanged(CircuitStateChangeContext context, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

/// <summary>Context information for an endpoint circuit state transition.</summary>
public sealed record CircuitStateChangeContext(
    string Endpoint,
    CircuitState From,
    CircuitState To,
    string Reason,
    DateTimeOffset Timestamp)
{
    /// <summary>Creates a lifecycle context from a circuit transition.</summary>
    public static CircuitStateChangeContext FromStateChange(CircuitStateChange change)
    {
        ArgumentNullException.ThrowIfNull(change);
        return new(change.Endpoint, change.From, change.To, change.Reason, change.Timestamp);
    }
}

/// <summary>
/// Context information for message lifecycle events.
/// </summary>
public class MessageLifecycleContext
{
    public string? MessageId { get; init; }
    public string? EventId { get; init; }
    public string EventTypeId { get; init; } = null!;
    public string? CorrelationId { get; init; }
    public string? SessionId { get; init; }

    /// <summary>
    /// Gets the endpoint that received the message.
    /// </summary>
    public string? EndpointId { get; init; }
    public MessageType MessageType { get; init; }
    public DateTimeOffset EnqueuedTimeUtc { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Creates a lifecycle context from a message context. Identity fields are read through
    /// the non-throwing accessors: transport contexts throw
    /// <see cref="Messages.Exceptions.InvalidMessageException"/> for fields absent on the
    /// wire, and lifecycle notification must never break processing of such messages.
    /// </summary>
    public static MessageLifecycleContext FromMessageContext(IMessageContext messageContext)
    {
        return new MessageLifecycleContext
        {
            MessageId = messageContext.GetMessageIdOrDefault(),
            EventId = messageContext.GetEventIdOrDefault(),
            EventTypeId = messageContext.EventTypeId,
            CorrelationId = Safe(() => messageContext.CorrelationId),
            SessionId = messageContext.GetSessionIdOrDefault(),
            EndpointId = messageContext.GetEndpointIdOrDefault(),
            MessageType = Safe(() => messageContext.MessageType),
            EnqueuedTimeUtc = messageContext.EnqueuedTimeUtc,
        };
    }

    private static T? Safe<T>(Func<T> read)
    {
        try { return read(); }
        catch (Messages.Exceptions.InvalidMessageException) { return default; }
    }
}
