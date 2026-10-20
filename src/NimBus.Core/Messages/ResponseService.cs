using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NimBus.Core.Diagnostics;
using NimBus.Core.Events;
using NimBus.Core.Inbox;

namespace NimBus.Core.Messages;


public class ResponseService : IResponseService
{
    private readonly ISender _sender;

    public ResponseService(ISender sender)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
    }

    public async Task SendResolutionResponse(IMessageContext messageContext, CancellationToken cancellationToken = default)
    {
        IMessage response = CreateResponse(messageContext, MessageType.ResolutionResponse, responseContent: messageContext.MessageContent);
        await _sender.Send(response, cancellationToken: cancellationToken);
    }

    public async Task SendSkipResponse(IMessageContext messageContext, CancellationToken cancellationToken = default)
    {
        IMessage response = CreateResponse(messageContext, MessageType.SkipResponse, responseContent: new MessageContent());
        await _sender.Send(response, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendDuplicateResponse(IMessageContext messageContext, CancellationToken cancellationToken = default)
    {
        var content = new MessageContent
        {
            ErrorContent = new ErrorContent
            {
                ErrorText = InboxMiddleware.DuplicateReason,
            },
            EventContent = messageContext.MessageContent?.EventContent,
        };
        IMessage response = CreateResponse(messageContext, MessageType.SkipResponse, content);
        await _sender.Send(response, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendDiscardResponse(
        IMessageContext messageContext,
        Exception exception,
        string classifierName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (string.IsNullOrWhiteSpace(classifierName))
            throw new ArgumentException("Classifier name cannot be null, empty, or whitespace.", nameof(classifierName));

        var exceptionType = exception.GetType().Name;
        var content = new MessageContent
        {
            ErrorContent = new ErrorContent
            {
                ErrorText = $"{exceptionType}: {exception.Message} Classified as Discard by {classifierName}.",
                ErrorType = exceptionType,
            },
            EventContent = (messageContext.MessageContent?.EventContent)!,
        };
        IMessage response = CreateResponse(messageContext, MessageType.SkipResponse, content);
        await _sender.Send(response, cancellationToken: cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendHeartbeatResolutionResponse(IMessageContext messageContext, CancellationToken cancellationToken = default)
    {
        var heartbeat = DeserializeHeartbeat(messageContext);
        var now = DateTime.UtcNow;
        heartbeat.ForwardReceivedTime = now;
        heartbeat.BackwardSendTime = now;
        heartbeat.Endpoint = messageContext.To;
        heartbeat.SdkVersion = GetSdkVersion();

        // A fresh MessageContent: the reply carries the stamped heartbeat, never the
        // inbound body. The Resolver attributes the row to an endpoint by the payload's
        // Endpoint first and the message's From second, so From must be stamped here —
        // CreateResponse deliberately leaves it unset for the topology rule to fill in,
        // and heartbeat replies must not depend on that rule being provisioned.
        var response = (Message)CreateResponse(messageContext, MessageType.ResolutionResponse, new MessageContent
        {
            EventContent = new EventContent
            {
                EventTypeId = Heartbeat.EventTypeId,
                EventJson = JsonConvert.SerializeObject(heartbeat),
            },
        });
        response.From = messageContext.To;
        await _sender.Send(response, cancellationToken: cancellationToken);
    }

    private static Heartbeat DeserializeHeartbeat(IMessageContext messageContext)
    {
        // A probe with a missing or malformed body must still be answered — the
        // reply's own stamps are what the WebApp reads.
        var eventJson = messageContext.MessageContent?.EventContent?.EventJson;
        if (string.IsNullOrWhiteSpace(eventJson))
            return new Heartbeat();

        try
        {
            return JsonConvert.DeserializeObject<Heartbeat>(eventJson) ?? new Heartbeat();
        }
        catch (JsonException)
        {
            return new Heartbeat();
        }
    }

    private static string GetSdkVersion()
    {
        var assembly = typeof(ResponseService).Assembly;
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            // Strip the '+<sha>' source-revision suffix the .NET SDK appends to the
            // informational version; the release identity is the bare package version.
            return informational.Split('+')[0];
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }

    public async Task SendErrorResponse(IMessageContext messageContext, Exception exception, CancellationToken cancellationToken = default)
    {
        IMessage response = CreateResponse(messageContext, MessageType.ErrorResponse, CreateErrorContent(exception, messageContext));
        await _sender.Send(response, cancellationToken: cancellationToken);
    }

    public async Task SendDeadLetterResponse(IMessageContext messageContext, string reason, Exception? exception, CancellationToken cancellationToken = default)
    {
        // Routed to the Resolver as an ErrorResponse with the dead-letter properties
        // populated. The Resolver short-circuits on DeadLetterErrorDescription and
        // classifies the audit record as DeadLettered regardless of MessageType.
        var content = exception != null
            ? CreateErrorContent(exception, messageContext)
            : new MessageContent { EventContent = messageContext.MessageContent?.EventContent };
        var response = (Message)CreateResponse(messageContext, MessageType.ErrorResponse, content);
        response.DeadLetterReason = reason;
        response.DeadLetterErrorDescription = FormatDeadLetterDescription(exception, reason);
        await _sender.Send(response, cancellationToken: cancellationToken);
    }

    private static string FormatDeadLetterDescription(Exception? exception, string reason) =>
        exception?.ToString() ?? reason;

    public async Task SendDeferralResponse(IMessageContext messageContext, SessionBlockedException exception, CancellationToken cancellationToken = default)
    {
        IMessage response = CreateResponse(messageContext, MessageType.DeferralResponse, CreateErrorContent(exception, messageContext));
        await _sender.Send(response, cancellationToken: cancellationToken);
    }

    public async Task SendPendingHandoffResponse(IMessageContext messageContext, HandoffMetadata handoff, CancellationToken cancellationToken = default)
    {
        var response = (Message)CreateResponse(messageContext, MessageType.PendingHandoffResponse, messageContext.MessageContent);
        response.HandoffReason = handoff?.Reason;
        response.ExternalJobId = handoff?.ExternalJobId;
        response.ExpectedBy = handoff?.ExpectedBy.HasValue == true
            ? DateTime.UtcNow.Add(handoff.ExpectedBy.Value)
            : (DateTime?)null;
        await _sender.Send(response, cancellationToken: cancellationToken);
    }

    public async Task SendRetryResponse(IMessageContext messageContext, int messageDelayMinutes, CancellationToken cancellationToken = default)
    {
        IMessage response = CreateRetryResponse(messageContext, MessageType.RetryRequest, responseContent: messageContext.MessageContent);
        await _sender.Send(response, messageDelayMinutes, cancellationToken);
    }

    /// <inheritdoc />
    public async Task SendRetryResponse(IMessageContext messageContext, TimeSpan messageDelay, CancellationToken cancellationToken = default)
    {
        IMessage response = CreateRetryResponse(messageContext, MessageType.RetryRequest, responseContent: messageContext.MessageContent);
        await _sender.ScheduleMessage(response, DateTimeOffset.UtcNow + messageDelay, cancellationToken);
    }

    /// <inheritdoc />
    [Obsolete("Only the legacy Service Bus defer drain sent ContinuationRequests, and it was removed in v4 (spec 027 §3). Removed in the next major version.")]
    public async Task SendContinuationRequestToSelf(IMessageContext deferredMessageContext, CancellationToken cancellationToken = default)
    {
        await _sender.Send(new Message()
        {
            To = Constants.ContinuationId,
            CorrelationId = deferredMessageContext.MessageId,
            SessionId = deferredMessageContext.SessionId,
            EventId = deferredMessageContext.EventId,
            OriginatingMessageId = !deferredMessageContext.OriginatingMessageId!.Equals(Constants.Self, StringComparison.OrdinalIgnoreCase) ? deferredMessageContext.OriginatingMessageId : deferredMessageContext.MessageId,
            ParentMessageId = deferredMessageContext.MessageId,
            EventTypeId = deferredMessageContext.EventTypeId,
            MessageType = MessageType.ContinuationRequest,
            MessageContent = new MessageContent(),
        }, cancellationToken: cancellationToken);
    }

    public async Task SendUnsupportedResponse(IMessageContext messageContext, CancellationToken cancellationToken = default)
    {
        IMessage response = CreateResponse(messageContext, MessageType.UnsupportedResponse, responseContent: messageContext.MessageContent);
        await _sender.Send(response, cancellationToken: cancellationToken);
    }

    private static IMessage CreateResponse(IMessageContext messageContext, MessageType responseType, MessageContent responseContent)
    {
        // Preserve the inbound CloudEvent's identity on the response so the Resolver
        // persists it on the tracking/audit record. Null (native message) leaves the
        // response byte-identical to today's wire form.
        var cloudEvent = messageContext.GetCloudEvent();
        // Non-throwing MessageId access: a native message without a MessageId must still be
        // able to complete its response after the inbox bypass ran its handler; the broker
        // assigns the outgoing response its own id when this is null.
        var messageId = messageContext.GetMessageIdOrDefault();
        return new Message()
        {
            To = Constants.ResolverId,
            CorrelationId = messageId,
            SessionId = messageContext.SessionId,
            EventId = messageContext.EventId,
            OriginatingMessageId = !messageContext.OriginatingMessageId!.Equals(Constants.Self, StringComparison.OrdinalIgnoreCase) ? messageContext.OriginatingMessageId : messageId,
            ParentMessageId = messageId,
            RetryCount = messageContext.RetryCount ?? null,
            OriginatingFrom = GetOriginator(messageContext),
            EventTypeId = messageContext.EventTypeId,
            MessageType = responseType,
            MessageContent = responseContent,
            // Carry per-message timings to the Resolver so it can persist them
            // on the audit doc — the message detail page renders them.
            QueueTimeMs = messageContext.QueueTimeMs,
            ProcessingTimeMs = ComputeProcessingTimeMs(messageContext),
            CloudEventId = cloudEvent?.Id,
            CloudEventSource = cloudEvent?.Source,
            CloudEventType = cloudEvent?.Type,
            CloudEventSubject = cloudEvent?.Subject,
        };
    }

    // The terminal handler calls SendResolutionResponse INSIDE the pipeline,
    // before any post-await middleware can finalise ProcessingTimeMs. Prefer
    // an explicitly-set value (used by tests / unusual flows); otherwise
    // compute from HandlerStartedAtUtc captured by ServiceBusAdapter at the
    // receive boundary.
    private static long? ComputeProcessingTimeMs(IMessageContext messageContext)
    {
        if (messageContext.ProcessingTimeMs.HasValue)
            return messageContext.ProcessingTimeMs;
        if (messageContext.HandlerStartedAtUtc is { } start)
            return Math.Max(0, (long)(DateTime.UtcNow - start).TotalMilliseconds);
        return null;
    }


    // The request's sender becomes the response's originator. From is stamped by the
    // topology's forward rule, so a request put on the topic by hand (e.g. resubmitted from
    // a dead-letter queue with a broker tool) arrives without it and the Service Bus context
    // throws on access. It is only recorded, so fall back to the originator the request
    // still carries: throwing here would dead-letter a message whose handler side effect
    // already landed.
    private static string? GetOriginator(IMessageContext messageContext) =>
        messageContext.GetFromOrDefault() ?? messageContext.OriginatingFrom;

    private IMessage CreateRetryResponse(IMessageContext messageContext, MessageType responseType, MessageContent responseContent) =>
        new Message()
        {
            To = Constants.RetryId,
            CorrelationId = messageContext.MessageId,
            SessionId = messageContext.SessionId,
            EventId = messageContext.EventId,
            OriginatingMessageId = !messageContext.OriginatingMessageId!.Equals(Constants.Self, StringComparison.OrdinalIgnoreCase) ? messageContext.OriginatingMessageId : messageContext.MessageId,
            ParentMessageId = messageContext.MessageId,
            RetryCount = messageContext.RetryCount.HasValue ? messageContext.RetryCount + 1 : 1,
            OriginatingFrom = GetOriginator(messageContext),
            EventTypeId = messageContext.EventTypeId,
            MessageType = MessageType.RetryRequest,
            MessageContent = responseContent,
            // The retry reaches the endpoint with a fresh MessageId; carry the source's so the
            // inbox records it when the retry succeeds.
            InboxMessageId = messageContext.GetSourceMessageIdOrDefault(),
        };


    private static MessageContent CreateErrorContent(Exception exception, IMessageContext messageContext)
    {
        // For ErrorType the operator wants the actual handler exception
        // (e.g. SampleApiException) — not the generic
        // EventContextHandlerException wrapper the SDK puts around it.
        // Simple Name (no namespace) keeps the field readable in the WebApp.
        var reported = exception is EventContextHandlerException wrapper && wrapper.InnerException != null
            ? wrapper.InnerException
            : exception;

        return new MessageContent()
        {
            ErrorContent = new ErrorContent()
            {
                ErrorText = exception.Message,
                ErrorType = reported.GetType().Name,
                ExceptionStackTrace = null,
                ExceptionSource = null,
            },
            EventContent = messageContext.MessageContent.EventContent
        };
    }

    public async Task SendToDeferredSubscription(IMessageContext messageContext, int deferralSequence, CancellationToken cancellationToken = default)
    {
        var from = messageContext.GetFromOrDefault();
        IMessage deferredMessage = new Message()
        {
            To = Constants.DeferredSubscriptionName,
            // Preserve the publisher when known. Otherwise mark this as deferred traffic:
            // the forward rules treat a missing From as a fresh publish. The replay processor
            // keeps this marker so neither parking nor replay fans out another event copy.
            From = from ?? Constants.DeferredSubscriptionName,
            CorrelationId = messageContext.CorrelationId,
            SessionId = messageContext.SessionId,           // Session-enabled deferred subscription
            EventId = messageContext.EventId,
            OriginatingMessageId = !messageContext.OriginatingMessageId!.Equals(Constants.Self, StringComparison.OrdinalIgnoreCase) ? messageContext.OriginatingMessageId : messageContext.MessageId,
            ParentMessageId = messageContext.MessageId,
            RetryCount = messageContext.RetryCount ?? null,
            OriginatingFrom = from ?? messageContext.OriginatingFrom,
            EventTypeId = messageContext.EventTypeId,
            MessageType = messageContext.MessageType,
            MessageContent = messageContext.MessageContent,
            OriginalSessionId = messageContext.SessionId,   // Kept for backward compatibility
            DeferralSequence = deferralSequence,
            // The replay gets a fresh MessageId; carry the source's so the inbox can check and
            // record it (see InboxDuplicateDetector).
            InboxMessageId = messageContext.GetSourceMessageIdOrDefault(),
        };

        var endpoint = messageContext.To;
        using var activity = NimBusActivitySources.DeferredProcessor.StartActivity(
            "NimBus.DeferredProcessor.Park", ActivityKind.Internal);
        if (activity is not null)
        {
            if (!string.IsNullOrEmpty(endpoint))
                activity.SetTag(MessagingAttributes.NimBusEndpoint, endpoint);
            if (!string.IsNullOrEmpty(messageContext.SessionId))
                activity.SetTag(MessagingAttributes.NimBusSessionKey, messageContext.SessionId);
            if (!string.IsNullOrEmpty(messageContext.EventTypeId))
                activity.SetTag(MessagingAttributes.NimBusEventType, messageContext.EventTypeId);
        }

        try
        {
            await _sender.Send(deferredMessage, cancellationToken: cancellationToken);
            activity?.SetStatus(ActivityStatusCode.Ok);
            NimBusMeters.DeferredParked.Add(1, BuildEndpointTag(endpoint));
        }
        catch (Exception ex)
        {
            if (activity is not null)
            {
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.SetTag(MessagingAttributes.ErrorType, ex.GetType().FullName);
            }
            throw;
        }
    }

    private static KeyValuePair<string, object?>[] BuildEndpointTag(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint))
            return Array.Empty<KeyValuePair<string, object?>>();
        return new[] { new KeyValuePair<string, object?>(MessagingAttributes.NimBusEndpoint, endpoint) };
    }

    public async Task SendProcessDeferredRequest(IMessageContext messageContext, CancellationToken cancellationToken = default)
    {
        await _sender.Send(new Message()
        {
            To = Constants.DeferredProcessorId,
            CorrelationId = messageContext.MessageId,
            SessionId = messageContext.SessionId,
            EventId = messageContext.EventId,
            OriginatingMessageId = !messageContext.OriginatingMessageId!.Equals(Constants.Self, StringComparison.OrdinalIgnoreCase) ? messageContext.OriginatingMessageId : messageContext.MessageId,
            ParentMessageId = messageContext.MessageId,
            EventTypeId = messageContext.EventTypeId,
            MessageType = MessageType.ProcessDeferredRequest,
            MessageContent = new MessageContent(),
        }, cancellationToken: cancellationToken);
    }
}
