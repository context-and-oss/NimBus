using NimBus.Core.CloudEvents;
using NimBus.Core.Messages.Exceptions;
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages;

public interface IReceivedMessage : IMessage
{
    DateTime EnqueuedTimeUtc { get; }
    new string MessageId { get; }

    new string EventTypeId { get; }
    new string? DeadLetterReason { get; }
    new string? DeadLetterErrorDescription { get; }
    new string? HandoffReason { get; }
    new string? ExternalJobId { get; }
    new DateTime? ExpectedBy { get; }
}

public interface IMessageContext : IReceivedMessage
{
    bool IsDeferred { get; }
    Task Complete(CancellationToken cancellationToken = default);

    Task Abandon(TransientException exception);

    Task DeadLetter(string reason, Exception? exception = null, CancellationToken cancellationToken = default);

    Task BlockSession(CancellationToken cancellationToken = default);
    Task UnblockSession(CancellationToken cancellationToken = default);
    Task<bool> IsSessionBlocked(CancellationToken cancellationToken = default);
    Task<bool> IsSessionBlockedByThis(CancellationToken cancellationToken = default);
    Task<bool> IsSessionBlockedByEventId(CancellationToken cancellationToken = default);
    Task<string?> GetBlockedByEventId(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the source MessageId of the delivery whose failure (or pending handoff) blocked
    /// the session, as <see cref="BlockSession"/> stored it: the MessageId the source
    /// delivered the blocking event with. An operator resubmission of that event reaches the
    /// endpoint with a fresh MessageId, so the inbox records this id too when it succeeds.
    /// <see langword="null"/> when the session is not blocked, or was blocked by a version
    /// that did not store it; the default implementation always returns <see langword="null"/>.
    /// </summary>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The blocking delivery's source MessageId, or <see langword="null"/>.</returns>
    Task<string?> GetBlockedByMessageId(CancellationToken cancellationToken = default) =>
        Task.FromResult<string?>(null);

    /// <summary>
    /// Gets the next deferral sequence number and increments the counter.
    /// Used for ordering messages in the non-session deferred subscription.
    /// </summary>
    Task<int> GetNextDeferralSequenceAndIncrement(CancellationToken cancellationToken = default);

    /// <summary>
    /// Increments the deferred message count in session state.
    /// </summary>
    Task IncrementDeferredCount(CancellationToken cancellationToken = default);

    /// <summary>
    /// Decrements the deferred message count in session state.
    /// </summary>
    Task DecrementDeferredCount(CancellationToken cancellationToken = default);

    /// <summary>
    /// Gets the current deferred message count from session state.
    /// </summary>
    Task<int> GetDeferredCount(CancellationToken cancellationToken = default);

    /// <summary>
    /// Checks if there are any deferred messages (legacy or new approach).
    /// </summary>
    Task<bool> HasDeferredMessages(CancellationToken cancellationToken = default);

    /// <summary>
    /// Resets the deferred message count to zero.
    /// Called after all deferred messages have been republished.
    /// </summary>
    Task ResetDeferredCount(CancellationToken cancellationToken = default);

    /// <summary>
    /// Records that the replay of the parked message with <paramref name="deferralSequence"/>
    /// reached the endpoint. Replays arrive in deferral order, so every message parked with a
    /// lower sequence has been replayed too. Repeating the call is harmless, and a sequence
    /// this session never assigned (from state cleared since) is ignored. The default
    /// implementation records nothing, which leaves <see cref="HasUnreplayedDeferredMessages"/>
    /// false.
    /// </summary>
    /// <param name="deferralSequence">The replay's deferral sequence.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    Task MarkDeferredMessageReplayed(int deferralSequence, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Whether a message parked in this session has not been replayed yet. A new event that
    /// arrives while this is true must park behind the parked messages, or it overtakes them.
    /// The default implementation returns <see langword="false"/>.
    /// </summary>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns><see langword="true"/> while a parked message awaits its replay.</returns>
    Task<bool> HasUnreplayedDeferredMessages(CancellationToken cancellationToken = default) =>
        Task.FromResult(false);

    /// <summary>
    /// Gets the drain an event parked behind replays requested, or <see langword="null"/> when
    /// no such request awaits its event's replay. The default implementation returns
    /// <see langword="null"/>.
    /// </summary>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <returns>The pending drain request, or <see langword="null"/>.</returns>
    Task<DeferredDrainRequest?> GetDeferredDrainRequest(CancellationToken cancellationToken = default) =>
        Task.FromResult<DeferredDrainRequest?>(null);

    /// <summary>
    /// Stores the value <see cref="GetDeferredDrainRequest"/> returns. The default
    /// implementation stores nothing.
    /// </summary>
    /// <param name="request">The drain request, or <see langword="null"/> to clear it.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    Task SetDeferredDrainRequest(DeferredDrainRequest? request, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    /// <summary>
    /// Gets the throttle retry count from message properties.
    /// Used for tracking exponential backoff retries due to rate limiting.
    /// </summary>
    int ThrottleRetryCount { get; }

    /// <summary>
    /// Schedules the current message for redelivery after a delay.
    /// Creates a new message with the same content and completes the original.
    /// Used for exponential backoff when Cosmos DB is throttled.
    /// <para>The copy keeps the original <c>MessageId</c>: it can arrive after the outcome it
    /// belongs to, and the tracking store's stale-write guard recognises an already-answered
    /// message by that id (Spec 030). Implementations must not mint a fresh one, and the
    /// receiving entity must not have duplicate detection enabled.</para>
    /// </summary>
    /// <param name="delay">The delay before the message is redelivered.</param>
    /// <param name="throttleRetryCount">The retry count to set on the new message.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ScheduleRedelivery(TimeSpan delay, int throttleRetryCount, CancellationToken cancellationToken = default);

    /// <summary>
    /// Time the inbound message spent in Service Bus before the handler was
    /// invoked (enqueued → handler entry). Set by ServiceBusAdapter at the
    /// receive boundary; read by ResponseService when constructing the
    /// outgoing response so the Resolver can persist it.
    /// </summary>
    new long? QueueTimeMs { get; set; }

    /// <summary>
    /// Time the handler spent running (handler entry → completion or failure).
    /// May be set explicitly (e.g. by middleware) or computed by
    /// ResponseService at response-build time from <see cref="HandlerStartedAtUtc"/>.
    /// </summary>
    new long? ProcessingTimeMs { get; set; }

    /// <summary>
    /// UTC timestamp captured at handler entry (immediately before the
    /// pipeline runs). ResponseService uses this to compute processing
    /// time when the outgoing response is built — needed because the
    /// terminal handler sends the response INSIDE the pipeline, before
    /// any post-await consumer instrumentation can finalise
    /// <see cref="ProcessingTimeMs"/>.
    /// </summary>
    DateTime? HandlerStartedAtUtc { get; set; }

    /// <summary>
    /// Outcome signalled by the handler via
    /// <c>IEventHandlerContext.MarkPendingHandoff</c>. Defaults to
    /// <see cref="HandlerOutcome.Default"/>; flips to
    /// <see cref="HandlerOutcome.PendingHandoff"/> when the handler hands
    /// work off to a long-running external system. Read by
    /// <c>StrictMessageHandler</c> after the handler returns to decide
    /// whether to send a PendingHandoffResponse and block the session.
    /// The inbox sets <see cref="HandlerOutcome.DuplicateDetected"/> and
    /// <c>StrictMessageHandler</c> sets <see cref="HandlerOutcome.NotDispatched"/>
    /// when a message is answered without dispatching a handler; the endpoint
    /// circuit breaker's recorder counts neither as a success.
    /// </summary>
    HandlerOutcome HandlerOutcome { get; set; }

    /// <summary>
    /// Metadata supplied by the handler alongside
    /// <see cref="HandlerOutcome.PendingHandoff"/>. Null when
    /// <see cref="HandlerOutcome"/> is <see cref="HandlerOutcome.Default"/>.
    /// </summary>
    HandoffMetadata? HandoffMetadata { get; set; }

    /// <summary>
    /// A handler failure the message handler settled itself instead of letting it
    /// propagate. <c>StrictMessageHandler</c> sets it when a RetryRequest or
    /// ResubmissionRequest fails (it sends the ErrorResponse, completes the message and
    /// schedules any retry, then returns normally) and when the failure disposition
    /// classifier discards a failure. <see langword="null"/> when the handler succeeded or
    /// its failure propagated as an exception. Pipeline behaviors read it after
    /// <c>next</c> returns; the endpoint circuit breaker's recorder records it as a failure
    /// instead of a success, subject to the breaker's counting rules.
    /// </summary>
    /// <remarks>
    /// The default implementation discards the value so existing implementers stay
    /// source-compatible; a context that does not store it reports such a delivery to the
    /// circuit breaker as a success.
    /// </remarks>
    Exception? HandledFailure { get => null; set { } }

    /// <summary>
    /// W3C trace context extracted from the inbound message's <c>traceparent</c> /
    /// <c>tracestate</c> headers. Populated by the transport adapter at the receive
    /// boundary; consumed by the consumer-side instrumentation (which owns the
    /// <c>NimBus.Process</c> span) so that the span is parented to the publisher's
    /// trace. <c>default(ActivityContext)</c> means no parent — the consumer span
    /// is a root span and <c>nimbus.has_parent_trace</c> is set to <c>false</c>.
    /// </summary>
    /// <remarks>
    /// The setter is part of the interface so transport adapters can populate the
    /// value through the contract rather than downcasting to a concrete context
    /// type. The default getter returns <c>default</c> so existing implementers
    /// that do not yet override it are forward-compatible.
    /// </remarks>
    ActivityContext ParentTraceContext { get => default; set { } }

    /// <summary>
    /// Returns the inbound CloudEvent when this message was received as a
    /// CloudEvent (via a CloudEvents-enabled or AutoDetect subscriber), or
    /// <c>null</c> for a native NimBus message. The default implementation
    /// returns <c>null</c> so existing transport/test implementers are
    /// forward-compatible.
    /// </summary>
    CloudEvent? GetCloudEvent() => null;
}
