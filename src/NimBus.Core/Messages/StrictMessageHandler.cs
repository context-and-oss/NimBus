using NimBus.Core.Events;
using NimBus.Core.Extensions;
using NimBus.Core.Inbox;
using NimBus.Core.Messages.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages;

public class StrictMessageHandler : MessageHandler
{
    // Once a delivery's outcome is decided, its settlement runs to the end on this token
    // instead of the caller's: every response, session-state change, park, completion and
    // retry schedule, and the reads between them. The caller's token is the processor's, and
    // the receiver cancels it by stopping the processor, for example when the endpoint circuit
    // opens because of failures in other sessions. A sequence abandoned half-way leaves state
    // that nothing repairs: an ErrorResponse sent and the session blocked, but the message
    // neither completed nor its retry scheduled. Only the reads that choose a path (the inbox
    // pre-check and the session guards) and the handler itself observe the caller's token, so
    // a stop that arrives before the outcome is decided still leaves the delivery unsettled
    // for redelivery. Each settlement call stays bounded by the transport's own timeout.
    private static CancellationToken SettlementToken => CancellationToken.None;

    private readonly IEventContextHandler _eventContextHandler;
    private readonly IResponseService _responseService;
    private readonly IRetryPolicyProvider? _retryPolicyProvider;
    private readonly IFailureDispositionClassifier _failureDispositionClassifier;
    private readonly InboxDuplicateDetector? _inboxDuplicateDetector;
    private readonly ILogger _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="StrictMessageHandler"/> class.
    /// Only the event context handler and response service are required; every other
    /// dependency is optional and defaults to "not configured".
    /// </summary>
    /// <param name="eventContextHandler">The event context handler.</param>
    /// <param name="responseService">The response service.</param>
    /// <param name="logger">The logger; a no-op logger when omitted.</param>
    /// <param name="retryPolicyProvider">The retry policy provider; no retries when omitted.</param>
    /// <param name="pipeline">The message pipeline.</param>
    /// <param name="lifecycleNotifier">The message lifecycle notifier.</param>
    /// <param name="failureDispositionClassifier">
    /// The failure disposition classifier; every handler failure is retried when omitted.
    /// </param>
    /// <param name="inboxDuplicateDetector">
    /// The optional inbox duplicate detector, consulted before the session-state guards so a
    /// redelivered duplicate is surfaced as a duplicate even when session state moved on.
    /// </param>
    public StrictMessageHandler(
        IEventContextHandler eventContextHandler,
        IResponseService responseService,
        ILogger? logger = null,
        IRetryPolicyProvider? retryPolicyProvider = null,
        MessagePipeline? pipeline = null,
        MessageLifecycleNotifier? lifecycleNotifier = null,
        IFailureDispositionClassifier? failureDispositionClassifier = null,
        InboxDuplicateDetector? inboxDuplicateDetector = null)
        : base(logger ?? NullLogger.Instance, pipeline!, lifecycleNotifier!, responseService)
    {
        _eventContextHandler = eventContextHandler;
        _responseService = responseService;
        _retryPolicyProvider = retryPolicyProvider;
        _failureDispositionClassifier = failureDispositionClassifier ?? new DefaultFailureDispositionClassifier();
        _inboxDuplicateDetector = inboxDuplicateDetector;
        _logger = logger ?? NullLogger.Instance;
    }

    public override async Task HandleEventRequest(IMessageContext messageContext, CancellationToken cancellationToken = default)
    {
        try
        {
            LogInfo(messageContext, "Handle");

            // Answered before the inbox pre-check and the session guard: a heartbeat is a
            // liveness probe, not business traffic. It must reach the reply even when the
            // session is blocked by a failed event (otherwise a stuck endpoint would look
            // dead), and it must never be recorded for inbox deduplication — every probe
            // carries a fresh id and consuming inbox rows for them is pure waste.
            if (IsHeartbeat(messageContext))
            {
                await _responseService.SendHeartbeatResolutionResponse(messageContext, SettlementToken);
                await CompleteMessage(messageContext);
                LogInfo(messageContext, "Successfully processed (Heartbeat)");
                return;
            }

            // Inbox pre-check ahead of the session guard: a recorded EventRequest that
            // redelivers while its session is blocked by another event must surface as a
            // duplicate skip, not defer behind the blocker.
            if (await IsInboxDuplicate(messageContext, cancellationToken))
            {
                await SendDuplicateResponseAndComplete(messageContext, "DuplicateDetected");
                return;
            }

            // Session guard: an event defers behind the event that blocks its session, except
            // a delivery of the blocking event itself.
            var blockedBy = await messageContext.GetBlockedByEventId(cancellationToken);
            if (IsThisEvent(messageContext, blockedBy))
            {
                await CompleteRedeliveryOfBlockingEvent(messageContext);
                return;
            }

            if (!string.IsNullOrEmpty(blockedBy))
                throw new SessionBlockedException($"Session {messageContext.SessionId} is blocked by {blockedBy}", blockedBy);

            var discardedFailure = await HandleEventContent(messageContext, cancellationToken);
            if (discardedFailure is not null)
            {
                await DiscardMessage(messageContext, discardedFailure);
                return;
            }

            if (messageContext.HandlerOutcome == HandlerOutcome.DuplicateDetected)
            {
                await SendDuplicateResponseAndComplete(messageContext, "DuplicateDetected");
                return;
            }

            // PendingHandoff branch — handler handed off to an external system.
            // Send PendingHandoffResponse, block the session so siblings defer
            // until the Manager settles via IHandoffClient CompleteAsync / FailAsync, and
            // skip the usual ResolutionResponse. If HandleEventContent threw,
            // execution never reaches here — the catch branches below own it.
            if (messageContext.HandlerOutcome == HandlerOutcome.PendingHandoff)
            {
                await ParkPendingHandoff(messageContext, requestName: null);
                return;
            }

            await SendResolutionResponse(messageContext);
            await CompleteMessage(messageContext);
            LogInfo(messageContext, "Successfully processed");
        }
        catch (EventHandlerNotFoundException exception)
        {
            messageContext.HandlerOutcome = HandlerOutcome.NotDispatched;
            LogError(messageContext, "Failed to handle event", exception);
            await SendUnsupportedResponse(messageContext);
            await CompleteMessage(messageContext);
        }
        catch (SessionBlockedException exception)
        {
            LogError(messageContext, "Failed to handle event", exception);
            await SendDeferralResponse(messageContext, exception);
            await DeferMessageToSubscription(messageContext);
            throw;
        }
        catch (EventContextHandlerException exception)
        {
            LogError(messageContext, "Failed to handle event", exception);
            await SendErrorResponse(messageContext, exception);
            await BlockSession(messageContext);
            await CompleteMessage(messageContext);
            await CheckForRetry(messageContext, exception);
            throw;
        }
    }

    public override async Task HandleRetryRequest(IMessageContext messageContext, CancellationToken cancellationToken = default)
    {
        try
        {
            LogInfo(messageContext, "Handle (RetryRequest)");

            // Inbox pre-check ahead of the blocked-by-this guard: a successfully handled
            // RetryRequest leaves its session unblocked, so its redelivery would otherwise
            // fail the guard and complete with a normal response, hiding the duplicate. When
            // the crash happened between recording and unblocking, the session is still
            // blocked by this event — release it and drain deferred siblings before
            // completing the duplicate, or the session would stay blocked forever. When the
            // crash happened between unblocking and the deferred drain, the session is not
            // blocked at all — the duplicate path is then the only remaining drain trigger,
            // so run it here. A session blocked by an unrelated later event is left alone;
            // that blocker owns the drain.
            if (await IsInboxDuplicate(messageContext, cancellationToken))
            {
                if (await messageContext.IsSessionBlockedByThis(cancellationToken))
                {
                    await UnblockSession(messageContext);
                    await ContinueWithAnyDeferredMessages(messageContext);
                }
                else if (string.IsNullOrEmpty(await messageContext.GetBlockedByEventId(cancellationToken)))
                {
                    await ContinueWithAnyDeferredMessages(messageContext);
                }

                await SendDuplicateResponseAndComplete(messageContext, "RetryRequest DuplicateDetected");
                return;
            }

            await VerifySessionIsBlockedByThis(messageContext, cancellationToken);
            var discardedFailure = await HandleEventContent(messageContext, cancellationToken);

            // Park BEFORE unblocking: falling through would drain deferred siblings
            // and send a ResolutionResponse, falsely completing the handoff.
            if (discardedFailure is null && messageContext.HandlerOutcome == HandlerOutcome.PendingHandoff)
            {
                await ParkPendingHandoff(messageContext, "RetryRequest");
                return;
            }

            await UnblockSession(messageContext);
            await ContinueWithAnyDeferredMessages(messageContext);
            if (discardedFailure is not null)
            {
                await DiscardMessage(messageContext, discardedFailure);
                return;
            }
            if (messageContext.HandlerOutcome == HandlerOutcome.DuplicateDetected)
            {
                await SendDuplicateResponseAndComplete(messageContext, "RetryRequest DuplicateDetected");
                return;
            }
            await SendResolutionResponse(messageContext);
            await CompleteMessage(messageContext);
            LogInfo(messageContext, "Successfully processed (RetryRequest)");
        }
        catch (SessionBlockedException exception)
        {
            // The event no longer blocks its session (resubmitted or skipped meanwhile), so
            // the retry is answered as resolved without running the handler.
            messageContext.HandlerOutcome = HandlerOutcome.NotDispatched;
            LogError(messageContext, "Failed to handle event (RetryRequest)", exception);
            await SendResolutionResponse(messageContext);
            await CompleteMessage(messageContext);
        }
        catch (EventContextHandlerException exception)
        {
            LogError(messageContext, "Failed to handle event (RetryRequest)", exception);
            await SendErrorResponse(messageContext, exception);
            await CompleteMessage(messageContext);
            await CheckForRetry(messageContext, exception);
            // Settled here rather than rethrown: report the failure through the context so
            // pipeline behaviors (the circuit breaker recorder) do not see a success.
            messageContext.HandledFailure = exception;
        }
    }

    public override async Task HandleResubmissionRequest(IMessageContext messageContext, CancellationToken cancellationToken = default)
    {
        try
        {
            LogInfo(messageContext, "Handle (Resubmission)");
            AuthorizeManagerRequest(messageContext);

            // Inbox pre-check: the decorator at the handler seam is record-only in hosted
            // compositions (one check, one record per delivery), so every entry point that
            // dispatches the handler must run the check itself. The session handling below
            // mirrors the normal resubmission path.
            if (await IsInboxDuplicate(messageContext, cancellationToken))
            {
                if (await messageContext.IsSessionBlockedByThis(cancellationToken))
                    await UnblockSession(messageContext);
                await ContinueWithAnyDeferredMessages(messageContext);
                await SendDuplicateResponseAndComplete(messageContext, "Resubmission DuplicateDetected");
                return;
            }

            var discardedFailure = await HandleEventContent(messageContext, cancellationToken);

            // Park BEFORE unblocking: falling through would drain deferred siblings
            // and send a ResolutionResponse, falsely completing the handoff.
            if (discardedFailure is null && messageContext.HandlerOutcome == HandlerOutcome.PendingHandoff)
            {
                // Unlike RetryRequest, a resubmission runs without an ownership
                // check, so a stale resubmission for event A can arrive while
                // event B owns the session block. Parking would overwrite
                // BlockedByEventId (B → A), stranding B's settlement and its
                // deferred siblings. Keep the row Pending+Handoff — the external
                // work is genuinely in flight — but leave the block alone; A's
                // eventual settlement resolves through the misaddressed-settlement
                // catches in HandleHandoffCompleted/FailedRequest.
                var blockedBy = await messageContext.GetBlockedByEventId(SettlementToken);
                if (!string.IsNullOrEmpty(blockedBy)
                    && !blockedBy.Equals(messageContext.EventId, StringComparison.OrdinalIgnoreCase))
                {
                    await _responseService.SendPendingHandoffResponse(messageContext, messageContext.HandoffMetadata, SettlementToken);
                    await CompleteMessage(messageContext);
                    LogInfo(messageContext, $"Successfully processed (Resubmission, PendingHandoff) — session owned by event '{blockedBy}', block left intact");
                    return;
                }

                await ParkPendingHandoff(messageContext, "Resubmission");
                return;
            }

            if (await messageContext.IsSessionBlockedByThis(SettlementToken))
                await UnblockSession(messageContext);
            await ContinueWithAnyDeferredMessages(messageContext);
            if (discardedFailure is not null)
            {
                await DiscardMessage(messageContext, discardedFailure);
                return;
            }
            if (messageContext.HandlerOutcome == HandlerOutcome.DuplicateDetected)
            {
                await SendDuplicateResponseAndComplete(messageContext, "Resubmission DuplicateDetected");
                return;
            }
            await SendResolutionResponse(messageContext);
            await CompleteMessage(messageContext);
            LogInfo(messageContext, "Successfully processed (Resubmission)");
        }
        catch (EventHandlerNotFoundException exception)
        {
            messageContext.HandlerOutcome = HandlerOutcome.NotDispatched;
            LogError(messageContext, "Failed to handle event (Resubmission)", exception);
            await SendUnsupportedResponse(messageContext);
            await CompleteMessage(messageContext);
        }
        catch (EventContextHandlerException exception)
        {
            LogError(messageContext, "Failed to handle event (Resubmission)", exception);
            await SendErrorResponse(messageContext, exception);
            await CompleteMessage(messageContext);
            // Settled rather than rethrown, as in HandleRetryRequest.
            messageContext.HandledFailure = exception;
        }
    }

    public override async Task HandleSkipRequest(IMessageContext messageContext, CancellationToken cancellationToken = default)
    {
        messageContext.HandlerOutcome = HandlerOutcome.NotDispatched;
        try
        {
            LogInfo(messageContext, "Handle (Skip)");
            AuthorizeManagerRequest(messageContext);
            await VerifySessionIsBlockedByThis(messageContext, cancellationToken);
            await UnblockSession(messageContext);
            await ContinueWithAnyDeferredMessages(messageContext);
            await SendSkipResponse(messageContext);
            await CompleteMessage(messageContext);
        }
        catch (SessionBlockedException)
        {
            await SendSkipResponse(messageContext);
            await CompleteMessage(messageContext);
        }

        LogInfo(messageContext, "Successfully processed (Skip)");
    }

    public override async Task HandleHandoffCompletedRequest(IMessageContext messageContext, CancellationToken cancellationToken = default)
    {
        messageContext.HandlerOutcome = HandlerOutcome.NotDispatched;
        try
        {
            LogInfo(messageContext, "Handle (HandoffCompleted)");
            AuthorizeManagerRequest(messageContext);
            await VerifySessionIsBlockedByThis(messageContext, cancellationToken);
            await UnblockSession(messageContext);
            await ContinueWithAnyDeferredMessages(messageContext);
            // Existing ResolutionResponse path flips Pending → Completed on the
            // original audit row. The user handler is intentionally NOT invoked.
            await SendResolutionResponse(messageContext);
            await CompleteMessage(messageContext);
            LogInfo(messageContext, "Successfully processed (HandoffCompleted)");
        }
        catch (SessionBlockedException exception)
        {
            // The settlement's SessionId isn't blocked, or its EventId ≠
            // BlockedByEventId — a misaddressed or duplicate settlement. There is
            // no matching blocked event to unblock; the original is already
            // resolved. Mirror HandleRetryRequest's catch: surface it as resolved
            // in the Flow and Complete, rather than letting the base handler
            // swallow it and silently dead-letter. Do NOT unblock.
            LogError(messageContext, "HandoffCompleted settlement does not match a blocked session — surfacing as resolved", exception);
            // A previous delivery may have cleared the block and then lost its
            // lock before scheduling the drain. Repair that gap on redelivery,
            // but never resume work behind a newer event's block.
            if (string.IsNullOrEmpty(exception.BlockedByEventId))
            {
                await ContinueWithAnyDeferredMessages(messageContext);
            }
            await SendResolutionResponse(messageContext);
            await CompleteMessage(messageContext);
        }
    }

    public override async Task HandleHandoffFailedRequest(IMessageContext messageContext, CancellationToken cancellationToken = default)
    {
        messageContext.HandlerOutcome = HandlerOutcome.NotDispatched;
        try
        {
            LogInfo(messageContext, "Handle (HandoffFailed)");
            AuthorizeManagerRequest(messageContext);
            await VerifySessionIsBlockedByThis(messageContext, cancellationToken);
            // Synthesise an exception from the inbound ErrorContent so the existing
            // SendErrorResponse path flips the audit row Pending → Failed with the
            // operator-supplied error text preserved verbatim. Session stays
            // blocked — the operator decides Resubmit / Skip from the WebApp.
            var handoffError = BuildHandoffError(messageContext);
            await SendErrorResponse(messageContext, handoffError);
            await CompleteMessage(messageContext);
            LogInfo(messageContext, "Successfully processed (HandoffFailed)");
        }
        catch (SessionBlockedException exception)
        {
            // The settlement's SessionId isn't blocked, or its EventId ≠
            // BlockedByEventId — a misaddressed or duplicate settlement. There is
            // no matching blocked event to flip to Failed, and a SendErrorResponse
            // here could mis-target a different event. Log an Error with full
            // metadata and Complete so the failure is surfaced where an operator
            // looks instead of silently dead-lettering via the base handler.
            LogError(messageContext, "HandoffFailed settlement does not match a blocked session — no matching event to fail", exception);
            await CompleteMessage(messageContext);
        }
    }

    /// <summary>
    /// A ContinuationRequest was only ever produced by the legacy Service Bus defer drain,
    /// which NimBus no longer runs (spec 027 §3). One still in flight from an older version
    /// is logged and completed rather than dead-lettered.
    /// </summary>
    public override async Task HandleContinuationRequest(IMessageContext messageContext, CancellationToken cancellationToken = default)
    {
        messageContext.HandlerOutcome = HandlerOutcome.NotDispatched;
        _logger.LogWarning(
            "Completing a legacy ContinuationRequest without processing it; the Service Bus defer drain was removed in v4. EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
            messageContext.GetEventIdOrDefault(),
            messageContext.GetMessageIdOrDefault(),
            messageContext.GetSessionIdOrDefault());
        await CompleteMessage(messageContext);
    }

    // HandleProcessDeferredRequest is intentionally NOT overridden here.
    // Deferred message processing is handled by a separate DeferredProcessorFunction
    // in each subscriber app, not by the core message handler.

    private static Task CompleteMessage(IMessageContext messageContext) =>
        messageContext.Complete(SettlementToken);

    // The user property is authoritative; the deserialized body is the fallback for
    // senders that only stamp the event type inside the content.
    private static bool IsHeartbeat(IMessageContext messageContext)
    {
        var eventTypeId = messageContext.EventTypeId
            ?? messageContext.MessageContent?.EventContent?.EventTypeId;

        return eventTypeId?.Equals(Heartbeat.EventTypeId, StringComparison.OrdinalIgnoreCase) == true;
    }

    private async Task<bool> IsInboxDuplicate(IMessageContext messageContext, CancellationToken cancellationToken)
    {
        if (_inboxDuplicateDetector is null)
            return false;

        return await _inboxDuplicateDetector.IsDuplicateAsync(messageContext, cancellationToken);
    }

    private async Task SendDuplicateResponseAndComplete(IMessageContext messageContext, string logSuffix)
    {
        await _responseService.SendDuplicateResponse(messageContext, SettlementToken);
        await CompleteMessage(messageContext);
        LogInfo(messageContext, $"Successfully processed ({logSuffix})");
    }

    private static bool IsThisEvent(IMessageContext messageContext, string? blockedByEventId) =>
        !string.IsNullOrEmpty(blockedByEventId)
        && blockedByEventId.Equals(messageContext.GetEventIdOrDefault(), StringComparison.OrdinalIgnoreCase);

    // The session is blocked by this very event: an earlier delivery of it sent its
    // ErrorResponse or PendingHandoffResponse and blocked the session, but was never completed
    // (the process stopped or the session lock was lost), or a second copy of the event
    // arrived. The outcome is recorded and the block keeps its own way out (an operator's
    // resubmit or skip, the Manager's handoff settlement), so the delivery is completed without
    // a response: a duplicate response would be recorded as Skipped over the Failed or pending
    // outcome. The handler is not run again, because session state cannot tell a failure's
    // block from a handoff's and rerunning a handoff repeats its external job. Nor is the
    // delivery parked like other blocked events: the drain after the block clears would replay
    // it and run the event a second time, even after an operator skipped it.
    private async Task CompleteRedeliveryOfBlockingEvent(IMessageContext messageContext)
    {
        messageContext.HandlerOutcome = HandlerOutcome.NotDispatched;
        _logger.LogWarning(
            "Completing a delivery of the event that blocks its session without running it again; the event's outcome is already recorded. EventTypeId:{EventTypeId}, EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
            messageContext.EventTypeId,
            messageContext.GetEventIdOrDefault(),
            messageContext.GetMessageIdOrDefault(),
            messageContext.GetSessionIdOrDefault());
        await CompleteMessage(messageContext);
    }

    // Shared parking sequence for a handler that signalled MarkPendingHandoff:
    // emit the PendingHandoffResponse (projected to Pending+Handoff, shown as
    // "Awaiting External"), block the session so FIFO siblings defer and the
    // Manager's later settlement can land, then complete the inbound message.
    // Used by the EventRequest, RetryRequest and ResubmissionRequest paths —
    // the latter two must NOT fall through to SendResolutionResponse, which
    // would falsely flip the event to Completed.
    private async Task ParkPendingHandoff(IMessageContext messageContext, string? requestName)
    {
        await _responseService.SendPendingHandoffResponse(messageContext, messageContext.HandoffMetadata, SettlementToken);
        await BlockSession(messageContext);
        await CompleteMessage(messageContext);
        LogInfo(messageContext, requestName == null
            ? "Successfully processed (PendingHandoff)"
            : $"Successfully processed ({requestName}, PendingHandoff)");
    }

    private async Task DeferMessageToSubscription(IMessageContext messageContext)
    {
        int deferralSequence = await messageContext.GetNextDeferralSequenceAndIncrement(SettlementToken);
        await _responseService.SendToDeferredSubscription(messageContext, deferralSequence, SettlementToken);
        await messageContext.IncrementDeferredCount(SettlementToken);
        await CompleteMessage(messageContext);
    }

    private Task SendResolutionResponse(IMessageContext messageContext) =>
        _responseService.SendResolutionResponse(messageContext, SettlementToken);

    private Task SendSkipResponse(IMessageContext messageContext) =>
        _responseService.SendSkipResponse(messageContext, SettlementToken);

    private Task SendErrorResponse(IMessageContext messageContext, EventContextHandlerException exception) =>
        _responseService.SendErrorResponse(messageContext, exception, SettlementToken);

    private Task SendDeferralResponse(IMessageContext messageContext, SessionBlockedException exception) =>
        _responseService.SendDeferralResponse(messageContext, exception, SettlementToken);

    private static Task BlockSession(IMessageContext messageContext) =>
        messageContext.BlockSession(SettlementToken);

    private static Task UnblockSession(IMessageContext messageContext) =>
        messageContext.UnblockSession(SettlementToken);

    private Task SendRetryResponse(IMessageContext messageContext, TimeSpan messageDelay) =>
        _responseService.SendRetryResponse(messageContext, messageDelay, SettlementToken);

    private Task SendUnsupportedResponse(IMessageContext messageContext) =>
        _responseService.SendUnsupportedResponse(messageContext, SettlementToken);

    private void AuthorizeManagerRequest(IMessageContext messageContext)
    {
        if (!messageContext.From!.Equals(Constants.ManagerId, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"Only {Constants.ManagerId} is authorized to send {messageContext.MessageType} messages.");
    }

    private async Task VerifySessionIsBlockedByThis(IMessageContext messageContext, CancellationToken cancellationToken = default)
    {
        if (!await messageContext.IsSessionBlockedByThis(cancellationToken))
        {
            var blockedBy = await messageContext.GetBlockedByEventId(cancellationToken);
            throw new SessionBlockedException($"Session {messageContext.SessionId} is blocked by {blockedBy}", blockedBy);
        }
    }

    private async Task<DiscardedFailure?> HandleEventContent(IMessageContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await _eventContextHandler.Handle(context, cancellationToken);
            return null;
        }
        catch (TransientException)
        {
            throw;
        }
        catch (EventHandlerNotFoundException)
        {
            throw;
        }
        catch (PermanentFailureException)
        {
            // A permanent failure (e.g. an invalid or unknown-type CloudEvent
            // rejected by the CloudEvents validator) must dead-letter, not go
            // through the retry/error-response path. Let it propagate to
            // MessageHandler.Handle's dedicated dead-letter catch.
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Cooperative shutdown must remain cancellation all the way through
            // MessageHandler; wrapping it would trigger error responses, session
            // blocking, retries, and settlement.
            throw;
        }
        catch (Exception exception)
        {
            var disposition = _failureDispositionClassifier.Classify(
                exception,
                context.EventTypeId,
                context.To);

            switch (disposition)
            {
                case FailureDisposition.Retry:
                    throw new EventContextHandlerException(exception)
                    {
                        Source = exception.Source
                    };

                case FailureDisposition.DeadLetter:
                    throw new PermanentFailureException(exception);

                case FailureDisposition.Discard:
                    var classifierName = _failureDispositionClassifier.GetType().FullName
                        ?? _failureDispositionClassifier.GetType().Name;
                    return new DiscardedFailure(exception, classifierName);

                default:
                    // Keep the handler failure as the inner exception so the
                    // dead-letter record still explains why the handler failed.
                    throw new InvalidOperationException(
                        $"Failure disposition classifier returned an unsupported value '{disposition}'.",
                        exception);
            }
        }
    }

    private async Task DiscardMessage(IMessageContext messageContext, DiscardedFailure discardedFailure)
    {
        _logger.LogWarning(
            discardedFailure.Exception,
            "Discarding failed message without retry or dead-letter. Classifier:{ClassifierName}, EventTypeId:{EventTypeId}, EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
            discardedFailure.ClassifierName,
            messageContext.EventTypeId,
            messageContext.GetEventIdOrDefault(),
            messageContext.GetMessageIdOrDefault(),
            messageContext.GetSessionIdOrDefault());
        await _responseService.SendDiscardResponse(
            messageContext,
            discardedFailure.Exception,
            discardedFailure.ClassifierName,
            SettlementToken);
        await CompleteMessage(messageContext);
        // Settled rather than rethrown, as in HandleRetryRequest. The circuit breaker does not
        // count it: only the retry disposition does.
        messageContext.HandledFailure = discardedFailure.Exception;
    }

    private async Task ContinueWithAnyDeferredMessages(IMessageContext messageContext)
    {
        var deferredCount = await messageContext.GetDeferredCount(SettlementToken);
        if (deferredCount > 0)
        {
            await _responseService.SendProcessDeferredRequest(messageContext, SettlementToken);
            LogInfo(messageContext, $"Send ProcessDeferredRequest ({deferredCount} deferred messages)");
        }
    }

    private async Task CheckForRetry(IMessageContext messageContext, EventContextHandlerException exception)
    {
        // No registered IRetryPolicyProvider means no retry — the failure
        // surfaces as an error response. (The legacy RetryDefinitions
        // fallback with hardcoded demo rules was removed; configure retries
        // via ConfigureRetryPolicies / a registered IRetryPolicyProvider.)
        if (_retryPolicyProvider == null)
        {
            return;
        }

        var eventTypeId = messageContext.EventTypeId;
        var retryCount = messageContext.RetryCount ?? 0;

        var policy = _retryPolicyProvider.GetRetryPolicy(eventTypeId, exception, messageContext.To);
        if (policy != null && retryCount < policy.MaxRetries)
        {
            var delay = ApplyRetryAfterHint(policy, policy.GetDelay(retryCount), exception);
            await SendRetryResponse(messageContext, delay);
        }
    }

    // A server-provided Retry-After (IRetryAfterHint anywhere in the exception chain) can only
    // lengthen the policy's delay, and never beyond the policy's MaxDelay.
    private static TimeSpan ApplyRetryAfterHint(RetryPolicy policy, TimeSpan policyDelay, Exception exception)
    {
        for (var current = exception; current != null; current = current.InnerException)
        {
            if (current is IRetryAfterHint { RetryAfter: { } hint } && hint > TimeSpan.Zero)
            {
                var delay = hint > policyDelay ? hint : policyDelay;
                return policy.MaxDelay is { } maxDelay && delay > maxDelay ? maxDelay : delay;
            }
        }

        return policyDelay;
    }

    // HandoffFailedRequest carries errorText/errorType in
    // MessageContent.ErrorContent. Wrap them in a synthetic
    // EventContextHandlerException so the existing SendErrorResponse path
    // produces a response whose ErrorText preserves the operator-supplied
    // text verbatim. ErrorType is not round-tripped through this exception
    // shape — the response's ErrorType reflects the synthetic wrapper, not
    // the operator-supplied errorType.
    private static EventContextHandlerException BuildHandoffError(IMessageContext messageContext)
    {
        var errorContent = messageContext?.MessageContent?.ErrorContent;
        var errorText = errorContent?.ErrorText ?? "Handoff failed.";
        var inner = new HandoffFailedException(errorText, errorContent?.ErrorType);
        return new EventContextHandlerException(inner);
    }

    // Identity fields are read through the non-throwing accessors: the Service Bus context
    // throws for fields absent on the wire, and diagnostic logging must never abort
    // processing of such messages (e.g. the missing-MessageId inbox bypass path).
    private void LogInfo(IMessageContext messageContext, string prefixMessage)
    {
        _logger.LogInformation("{Prefix} EventTypeId:{EventTypeId}, EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
            prefixMessage, messageContext.EventTypeId, messageContext.GetEventIdOrDefault(), messageContext.GetMessageIdOrDefault(), messageContext.GetSessionIdOrDefault());
    }

    private void LogError(IMessageContext messageContext, string prefixMessage, Exception exception)
    {
        _logger.LogError(exception, "{Prefix} EventTypeId:{EventTypeId}, EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
            prefixMessage, messageContext.EventTypeId, messageContext.GetEventIdOrDefault(), messageContext.GetMessageIdOrDefault(), messageContext.GetSessionIdOrDefault());
    }

    private sealed record DiscardedFailure(Exception Exception, string ClassifierName);
}
