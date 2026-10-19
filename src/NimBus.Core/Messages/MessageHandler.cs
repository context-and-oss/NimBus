using NimBus.Core.Extensions;
using NimBus.Core.Messages.Exceptions;
using NimBus.Core.Pipeline;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Messages;

public class MessageHandler : IMessageHandler
{
    private readonly ILogger _logger;
    private readonly MessagePipeline? _pipeline;
    private readonly MessageLifecycleNotifier? _lifecycleNotifier;
    private readonly IResponseService? _responseService;

    public MessageHandler(ILogger logger)
        : this(logger, null, null, null)
    {
    }

    public MessageHandler(ILogger logger, MessagePipeline? pipeline, MessageLifecycleNotifier? lifecycleNotifier)
        : this(logger, pipeline, lifecycleNotifier, null)
    {
    }

    public MessageHandler(ILogger logger, MessagePipeline? pipeline, MessageLifecycleNotifier? lifecycleNotifier, IResponseService? responseService)
    {
        _logger = logger ?? NullLogger.Instance;
        _pipeline = pipeline;
        _lifecycleNotifier = lifecycleNotifier;
        _responseService = responseService;
    }

    public async Task Handle(IMessageContext messageContext, CancellationToken cancellationToken = default)
    {
        if (_lifecycleNotifier?.HasObservers == true)
        {
            await _lifecycleNotifier.NotifyReceived(messageContext, cancellationToken);
        }

        try
        {
            if (_pipeline?.HasBehaviors == true)
            {
                await _pipeline.Execute(
                    messageContext,
                    (ctx, ct) => HandleByMessageType(ctx, ct),
                    cancellationToken);
            }
            else
            {
                await HandleByMessageType(messageContext, cancellationToken);
            }

            if (_lifecycleNotifier?.HasObservers == true)
            {
                await _lifecycleNotifier.NotifyCompleted(messageContext, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Caller-driven shutdown is not a message-processing failure. Let the
            // transport observe cancellation so it can stop without settling,
            // retrying, or dead-lettering the in-flight message.
            throw;
        }
        catch (TransientException transientException)
        {
            _logger.LogError(transientException.InnerException, "Transient Error. Failed to handle message. EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                messageContext.GetEventIdOrDefault(), messageContext.GetMessageIdOrDefault(), messageContext.GetSessionIdOrDefault());

            if (_lifecycleNotifier?.HasObservers == true)
            {
                await _lifecycleNotifier.NotifyFailed(messageContext, transientException, cancellationToken);
            }

            try
            {
                await messageContext.Abandon(transientException);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to abandon message. EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                    messageContext.GetEventIdOrDefault(), messageContext.GetMessageIdOrDefault(), messageContext.GetSessionIdOrDefault());
            }
        }
        catch (SessionBlockedException sessionBlocked)
        {
            // The session is blocked by an earlier failed event; this message has been
            // deferred (StrictMessageHandler) and is swallowed here so it is not retried.
            // Surface the block as a lifecycle signal so observers (e.g. notifications)
            // can alert that the session is now blocked, referencing the blocking event.
            if (_lifecycleNotifier?.HasObservers == true && messageContext != null)
            {
                await _lifecycleNotifier.NotifySessionBlocked(messageContext, sessionBlocked.BlockedByEventId, cancellationToken);
            }
        }
        catch (EventContextHandlerException handlerFailure)
        {
            // Strict handling has already recorded the error and settled the delivery.
            // Observers still need the original failure, without settling it again.
            if (_lifecycleNotifier?.HasObservers == true)
            {
                await _lifecycleNotifier.NotifyFailed(messageContext, handlerFailure.InnerException ?? handlerFailure, cancellationToken);
            }
        }
        catch (PermanentFailureException permanentFailure)
        {
            _logger.LogWarning("Permanent failure — dead-lettering without retry. EventId:{EventId}, Exception:{ExceptionType}",
                messageContext?.EventId, permanentFailure.InnerException?.GetType().Name);

            if (_lifecycleNotifier?.HasObservers == true)
            {
                await _lifecycleNotifier.NotifyFailed(messageContext!, permanentFailure.InnerException ?? permanentFailure, cancellationToken);
            }

            try
            {
                // Include the inner exception message so a specific, inspectable
                // reason (e.g. "CloudEvents message missing required attribute
                // 'source'") lands in the DLQ reason field, not just the type name.
                var inner = permanentFailure.InnerException;
                var reason = inner is null
                    ? "Permanent failure"
                    : $"Permanent failure: {inner.GetType().Name}: {inner.Message}";
                await NotifyResolverOfDeadLetter(messageContext, reason, permanentFailure.InnerException, cancellationToken);
                await messageContext!.DeadLetter(reason, permanentFailure.InnerException, cancellationToken);

                if (_lifecycleNotifier?.HasObservers == true)
                {
                    await _lifecycleNotifier.NotifyDeadLettered(messageContext, reason, permanentFailure.InnerException, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to dead-letter permanent failure message. EventId:{EventId}", messageContext?.EventId);
            }
        }
        catch (MessageAlreadyDeadLetteredException alreadyDeadLettered)
        {
            // Message was already dead-lettered by middleware (e.g., ValidationMiddleware).
            // Fire lifecycle notifications without attempting to dead-letter again.
            _logger.LogWarning("Message already dead-lettered by middleware. EventId:{EventId}, Reason:{Reason}",
                messageContext?.EventId, alreadyDeadLettered.Message);

            await NotifyResolverOfDeadLetter(messageContext, alreadyDeadLettered.Message, alreadyDeadLettered, cancellationToken);

            if (_lifecycleNotifier?.HasObservers == true)
            {
                await _lifecycleNotifier.NotifyFailed(messageContext!, alreadyDeadLettered, cancellationToken);
                await _lifecycleNotifier.NotifyDeadLettered(messageContext!, alreadyDeadLettered.Message, alreadyDeadLettered, cancellationToken);
            }
        }
        catch (Exception unexpectedException)
        {
            if (_lifecycleNotifier?.HasObservers == true)
            {
                await _lifecycleNotifier.NotifyFailed(messageContext, unexpectedException, cancellationToken);
            }

            try
            {
                _logger.LogError(unexpectedException, "Unexpected Error. Failed to handle message. EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                    messageContext.GetEventIdOrDefault(), messageContext.GetMessageIdOrDefault(), messageContext.GetSessionIdOrDefault());
                await NotifyResolverOfDeadLetter(messageContext, "Failed to handle message.", unexpectedException, cancellationToken);
                await messageContext.DeadLetter("Failed to handle message.", unexpectedException, cancellationToken);

                if (_lifecycleNotifier?.HasObservers == true)
                {
                    await _lifecycleNotifier.NotifyDeadLettered(messageContext, "Failed to handle message.", unexpectedException, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to deadletter message. EventId:{EventId}, MessageId:{MessageId}, SessionId:{SessionId}",
                    messageContext.GetEventIdOrDefault(), messageContext.GetMessageIdOrDefault(), messageContext.GetSessionIdOrDefault());
            }
        }
    }

    private async Task NotifyResolverOfDeadLetter(IMessageContext? messageContext, string reason, Exception? exception, CancellationToken cancellationToken)
    {
        if (_responseService == null || messageContext == null) return;
        try
        {
            await _responseService.SendDeadLetterResponse(messageContext, reason, exception, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception sendException)
        {
            // Best-effort: a failure to publish the dead-letter notification must not
            // prevent the message from being dead-lettered. The DLQ is the source of
            // truth — the operator can still resubmit from the Manager.
            _logger.LogWarning(sendException,
                "Failed to publish dead-letter notification to Resolver. EventId:{EventId}, MessageId:{MessageId}",
                messageContext.GetEventIdOrDefault(), messageContext.GetMessageIdOrDefault());
        }
    }

    private Task HandleByMessageType(IMessageContext messageContext, CancellationToken cancellationToken)
    {
        switch (messageContext.MessageType)
        {
            case MessageType.EventRequest:
            case MessageType.UnsupportedResponse:
                return HandleEventRequest(messageContext, cancellationToken);

            case MessageType.ContinuationRequest:
                return HandleContinuationRequest(messageContext, cancellationToken);

            case MessageType.SkipRequest:
                return HandleSkipRequest(messageContext, cancellationToken);

            case MessageType.ResubmissionRequest:
                return HandleResubmissionRequest(messageContext, cancellationToken);

            case MessageType.HandoffCompletedRequest:
                return HandleHandoffCompletedRequest(messageContext, cancellationToken);

            case MessageType.HandoffFailedRequest:
                return HandleHandoffFailedRequest(messageContext, cancellationToken);

            case MessageType.RetryRequest:
                return HandleRetryRequest(messageContext, cancellationToken);

            case MessageType.ProcessDeferredRequest:
                return HandleProcessDeferredRequest(messageContext, cancellationToken);

            case MessageType.ErrorResponse:
                return HandleErrorResponse(messageContext, cancellationToken);

            case MessageType.ResolutionResponse:
                return HandleResolutionResponse(messageContext, cancellationToken);

            case MessageType.DeferralResponse:
                return HandleDeferralResponse(messageContext, cancellationToken);

            default:
                return HandleDefault(messageContext, cancellationToken);
        }
    }

    public virtual Task HandleDefault(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
        throw new UnsupportedMessageTypeException(messageContext.MessageType);

    public virtual Task HandleDeferralResponse(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
        HandleDefault(messageContext, cancellationToken);

    public virtual Task HandleResolutionResponse(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
        HandleDefault(messageContext, cancellationToken);

    public virtual Task HandleErrorResponse(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
        HandleDefault(messageContext, cancellationToken);

    public virtual Task HandleResubmissionRequest(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
        HandleDefault(messageContext, cancellationToken);

    public virtual Task HandleRetryRequest(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
        HandleDefault(messageContext, cancellationToken);

    public virtual Task HandleSkipRequest(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
        HandleDefault(messageContext, cancellationToken);

    public virtual Task HandleContinuationRequest(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
        HandleDefault(messageContext, cancellationToken);

    public virtual Task HandleEventRequest(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
        HandleDefault(messageContext, cancellationToken);

    public virtual Task HandleProcessDeferredRequest(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
        HandleDefault(messageContext, cancellationToken);

    public virtual Task HandleHandoffCompletedRequest(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
        HandleDefault(messageContext, cancellationToken);

    public virtual Task HandleHandoffFailedRequest(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
        HandleDefault(messageContext, cancellationToken);
}
