using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages;

public interface IResponseService
{
    Task SendResolutionResponse(IMessageContext messageContext, CancellationToken cancellationToken = default);
    Task SendSkipResponse(IMessageContext messageContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Notifies the Resolver that inbox deduplication skipped a message.
    /// The default implementation preserves compatibility with existing custom response services.
    /// </summary>
    Task SendDuplicateResponse(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
        SendSkipResponse(messageContext, cancellationToken);

    /// <summary>
    /// Notifies the Resolver that a failed message was intentionally discarded.
    /// The default implementation preserves compatibility with custom response services
    /// by sending the existing skipped outcome without enriched failure details.
    /// </summary>
    /// <param name="messageContext">The discarded inbound message.</param>
    /// <param name="exception">The original handler exception.</param>
    /// <param name="classifierName">The classifier type that selected the discard disposition.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    Task SendDiscardResponse(
        IMessageContext messageContext,
        Exception exception,
        string classifierName,
        CancellationToken cancellationToken = default) =>
        SendSkipResponse(messageContext, cancellationToken);

    /// <summary>
    /// Answers a platform heartbeat probe with a <see cref="MessageType.ResolutionResponse"/>
    /// carrying a stamped <see cref="Events.Heartbeat"/> payload (receive/send times, endpoint,
    /// SDK version) so the WebApp can report round-trip time and adapter version.
    /// The default implementation preserves compatibility with existing custom response
    /// services by sending the plain resolution outcome without the heartbeat stamps.
    /// </summary>
    /// <param name="messageContext">The inbound heartbeat request.</param>
    /// <param name="cancellationToken">A token used to cancel the operation.</param>
    Task SendHeartbeatResolutionResponse(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
        SendResolutionResponse(messageContext, cancellationToken);

    Task SendErrorResponse(IMessageContext messageContext, Exception exception, CancellationToken cancellationToken = default);

    /// <summary>
    /// Notifies the Resolver that a message was dead-lettered. Sends a response
    /// message routed to the Resolver carrying the dead-letter reason and
    /// formatted exception so the audit record is classified as DeadLettered.
    /// </summary>
    Task SendDeadLetterResponse(IMessageContext messageContext, string reason, Exception? exception, CancellationToken cancellationToken = default);
    Task SendDeferralResponse(IMessageContext messageContext, SessionBlockedException exception, CancellationToken cancellationToken = default);
    /// <summary>
    /// Sends a ContinuationRequest for a message parked with the legacy Service Bus defer API.
    /// </summary>
    [Obsolete("Only the legacy Service Bus defer drain sent ContinuationRequests, and it was removed in v4 (spec 027 §3). Removed in the next major version.")]
    Task SendContinuationRequestToSelf(IMessageContext deferredMessageContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Schedules a retry response after the specified delay.
    /// </summary>
    /// <param name="messageContext">The failed message context.</param>
    /// <param name="messageDelay">The precise delay before the retry is enqueued.</param>
    /// <param name="cancellationToken">A token that can cancel the operation.</param>
    /// <remarks>Legacy implementations fall back to the whole-minute overload.</remarks>
    Task SendRetryResponse(IMessageContext messageContext, TimeSpan messageDelay, CancellationToken cancellationToken = default) =>
        SendRetryResponse(messageContext, (int)Math.Ceiling(messageDelay.TotalMinutes), cancellationToken);

    Task SendRetryResponse(IMessageContext messageContext, int messageDelayMinutes, CancellationToken cancellationToken = default);
    Task SendUnsupportedResponse(IMessageContext messageContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a message to the non-session deferred subscription.
    /// Used when the session is blocked and new messages need to be deferred.
    /// </summary>
    Task SendToDeferredSubscription(IMessageContext messageContext, int deferralSequence, CancellationToken cancellationToken = default);

    /// <summary>
    /// Sends a ProcessDeferredRequest to trigger processing of deferred messages.
    /// Called when a session is unblocked and there are deferred messages to process.
    /// </summary>
    Task SendProcessDeferredRequest(IMessageContext messageContext, CancellationToken cancellationToken = default);

    /// <summary>
    /// Notifies the Resolver that the handler signalled
    /// <c>HandlerOutcome.PendingHandoff</c>. Carries the supplied
    /// <see cref="HandoffMetadata"/> on the response message; the
    /// <c>ExpectedBy</c> duration is converted to an absolute UTC
    /// deadline at send time.
    /// </summary>
    Task SendPendingHandoffResponse(IMessageContext messageContext, HandoffMetadata handoff, CancellationToken cancellationToken = default);
}
