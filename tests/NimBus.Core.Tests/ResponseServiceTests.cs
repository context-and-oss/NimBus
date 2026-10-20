#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Tests;

[TestClass]
public class ResponseServiceTests
{
    // ── SendResolutionResponse ──────────────────────────────────────────

    [TestMethod]
    public async Task SendResolutionResponse_RoutesToResolver()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext();

        await sut.SendResolutionResponse(ctx);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.ResolverId, msg.To);
        Assert.AreEqual(MessageType.ResolutionResponse, msg.MessageType);
    }

    // ── Request without From ────────────────────────────────────────────
    // From is stamped by the topology's forward rule. A request put on the topic by hand
    // (e.g. resubmitted from a dead-letter queue with a broker tool) arrives without it, and
    // the Service Bus context throws on access. From is only recorded on a response, so its
    // absence must not fail the response AFTER the handler's side effect has landed.

    [TestMethod]
    public async Task SendResolutionResponse_MissingFrom_StillResponds()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext();
        ctx.FromIsMissing = true;

        await sut.SendResolutionResponse(ctx);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(MessageType.ResolutionResponse, msg.MessageType);
        Assert.AreEqual("StorefrontEndpoint", msg.OriginatingFrom, "The originator the request still carries must survive.");
    }

    [TestMethod]
    public async Task SendRetryResponse_MissingFrom_StillSchedulesRetry()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext();
        ctx.FromIsMissing = true;

        await sut.SendRetryResponse(ctx, messageDelayMinutes: 1);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(MessageType.RetryRequest, msg.MessageType);
        Assert.AreEqual("StorefrontEndpoint", msg.OriginatingFrom);
    }

    [TestMethod]
    public async Task SendToDeferredSubscription_MissingFrom_StillParksMessage()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext();
        ctx.FromIsMissing = true;

        await sut.SendToDeferredSubscription(ctx, deferralSequence: 0);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.DeferredSubscriptionName, msg.To);
        Assert.AreEqual(Constants.DeferredSubscriptionName, msg.From);
        Assert.AreEqual("StorefrontEndpoint", msg.OriginatingFrom);
    }

    [TestMethod]
    public async Task SendResolutionResponse_DeferredMarkerAsFrom_IsNotRecordedAsTheOriginator()
    {
        // A From-less request that was parked replays with the "Deferred" marker as From.
        // The marker only keeps the forward rules from matching; it is not a sender.
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext();
        ctx.From = Constants.DeferredSubscriptionName;

        await sut.SendResolutionResponse(ctx);

        Assert.AreEqual("StorefrontEndpoint", sender.SentMessages.Single().OriginatingFrom);
    }

    [TestMethod]
    public async Task SendResolutionResponse_CopiesMessageContentFromContext()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var content = new MessageContent { EventContent = new EventContent { EventJson = "{\"x\":1}" } };
        var ctx = CreateContext(messageContent: content);

        await sut.SendResolutionResponse(ctx);

        var msg = sender.SentMessages.Single();
        Assert.AreSame(content, msg.MessageContent);
    }

    // ── SendSkipResponse ────────────────────────────────────────────────

    [TestMethod]
    public async Task SendSkipResponse_RoutesToResolverWithEmptyContent()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);

        await sut.SendSkipResponse(CreateContext());

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.ResolverId, msg.To);
        Assert.AreEqual(MessageType.SkipResponse, msg.MessageType);
        Assert.IsNotNull(msg.MessageContent);
    }

    [TestMethod]
    public async Task SendDuplicateResponse_RoutesToResolverAsSkippedWithStableReasonAndEvent()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var context = CreateContext();

        await sut.SendDuplicateResponse(context);

        var message = sender.SentMessages.Single();
        Assert.AreEqual(Constants.ResolverId, message.To);
        Assert.AreEqual(MessageType.SkipResponse, message.MessageType);
        Assert.AreEqual("DuplicateDetected", message.MessageContent.ErrorContent.ErrorText);
        Assert.AreSame(context.MessageContent.EventContent, message.MessageContent.EventContent);
    }

    [TestMethod]
    public async Task SendDiscardResponse_RoutesToResolverAsSkippedWithClassifierReason()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext();
        var exception = new InvalidOperationException("Known bad event version.");
        var classifierName = "PartnerFailureDispositionClassifier";

        await sut.SendDiscardResponse(ctx, exception, classifierName);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.ResolverId, msg.To);
        Assert.AreEqual(MessageType.SkipResponse, msg.MessageType);
        Assert.AreSame(ctx.MessageContent.EventContent, msg.MessageContent.EventContent);
        Assert.AreEqual(nameof(InvalidOperationException), msg.MessageContent.ErrorContent.ErrorType);
        StringAssert.Contains(msg.MessageContent.ErrorContent.ErrorText, exception.Message);
        StringAssert.Contains(msg.MessageContent.ErrorContent.ErrorText, classifierName);
        Assert.IsNull(msg.DeadLetterReason);
        Assert.IsNull(msg.DeadLetterErrorDescription);
    }

    // ── SendErrorResponse ───────────────────────────────────────────────

    [TestMethod]
    public async Task SendErrorResponse_RoutesToResolverWithErrorContent()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ex = new InvalidOperationException("boom");

        await sut.SendErrorResponse(CreateContext(), ex);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.ResolverId, msg.To);
        Assert.AreEqual(MessageType.ErrorResponse, msg.MessageType);
        Assert.AreEqual("boom", msg.MessageContent.ErrorContent.ErrorText);
        Assert.AreEqual(nameof(InvalidOperationException), msg.MessageContent.ErrorContent.ErrorType,
            "ErrorType should be the simple type name (no namespace) for WebApp readability");
    }

    [TestMethod]
    public async Task SendErrorResponse_WrappedHandlerException_ReportsInnerExceptionType()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        // StrictMessageHandler wraps handler failures in EventContextHandlerException
        // before calling SendErrorResponse. The operator wants the actual handler
        // exception type in the WebApp's Error type field, not the SDK wrapper.
        var ex = new EventContextHandlerException(new SomeCustomException("handler blew up"));

        await sut.SendErrorResponse(CreateContext(), ex);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(nameof(SomeCustomException), msg.MessageContent.ErrorContent.ErrorType);
        Assert.AreEqual("handler blew up", msg.MessageContent.ErrorContent.ErrorText);
    }

    // ── SendDeadLetterResponse ──────────────────────────────────────────

    [TestMethod]
    public async Task SendDeadLetterResponse_RoutesToResolverWithDeadLetterProperties()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ex = new InvalidOperationException("boom");

        await sut.SendDeadLetterResponse(CreateContext(), reason: "Failed to handle message.", exception: ex);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.ResolverId, msg.To);
        Assert.AreEqual(MessageType.ErrorResponse, msg.MessageType);
        Assert.AreEqual("Failed to handle message.", msg.DeadLetterReason);
        Assert.IsNotNull(msg.DeadLetterErrorDescription);
        StringAssert.Contains(msg.DeadLetterErrorDescription, "boom");
        StringAssert.Contains(msg.DeadLetterErrorDescription, nameof(InvalidOperationException));
    }

    [TestMethod]
    public async Task SendDeadLetterResponse_MissingFrom_StillNotifiesResolver()
    {
        // The transport context throws when From is absent on the wire. That is exactly the
        // message being dead-lettered here, so the notification must not need From.
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext();
        ctx.FromIsMissing = true;

        await sut.SendDeadLetterResponse(ctx, reason: "Failed to handle message.", exception: null!);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(MessageType.ErrorResponse, msg.MessageType);
        Assert.AreEqual("Failed to handle message.", msg.DeadLetterReason);
        Assert.AreEqual("StorefrontEndpoint", msg.OriginatingFrom);
    }

    [TestMethod]
    public async Task SendDeadLetterResponse_WithoutException_StillCarriesReason()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);

        await sut.SendDeadLetterResponse(CreateContext(), reason: "Validation failed: EventId is required", exception: null!);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual("Validation failed: EventId is required", msg.DeadLetterReason);
        Assert.AreEqual("Validation failed: EventId is required", msg.DeadLetterErrorDescription);
    }

    // ── SendDeferralResponse ────────────────────────────────────────────

    [TestMethod]
    public async Task SendDeferralResponse_RoutesToResolverWithDeferralType()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ex = new SessionBlockedException("blocked");

        await sut.SendDeferralResponse(CreateContext(), ex);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.ResolverId, msg.To);
        Assert.AreEqual(MessageType.DeferralResponse, msg.MessageType);
        Assert.AreEqual("blocked", msg.MessageContent.ErrorContent.ErrorText);
    }

    // ── SendRetryResponse ───────────────────────────────────────────────

    [TestMethod]
    public async Task SendRetryResponse_RoutesToRetryWithIncrementedCount()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(retryCount: 2);

        await sut.SendRetryResponse(ctx, 5);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.RetryId, msg.To);
        Assert.AreEqual(MessageType.RetryRequest, msg.MessageType);
        Assert.AreEqual(3, msg.RetryCount, "RetryCount should be incremented by 1");
        Assert.AreEqual(5, sender.LastDelay, "Delay should be forwarded");
    }

    [TestMethod]
    public async Task SendRetryResponse_NullRetryCount_StartsAtOne()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(retryCount: null);

        await sut.SendRetryResponse(ctx, 1);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(1, msg.RetryCount);
    }

    // ── SendUnsupportedResponse ─────────────────────────────────────────

    [TestMethod]
    public async Task SendUnsupportedResponse_RoutesToResolverWithUnsupportedType()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);

        await sut.SendUnsupportedResponse(CreateContext());

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.ResolverId, msg.To);
        Assert.AreEqual(MessageType.UnsupportedResponse, msg.MessageType);
    }

    // ── SendContinuationRequestToSelf ───────────────────────────────────

#pragma warning disable CS0618 // Covers the obsolete member until it is removed in the next major.
    [TestMethod]
    public async Task SendContinuationRequestToSelf_RoutesToContinuation()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);

        await sut.SendContinuationRequestToSelf(CreateContext());

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.ContinuationId, msg.To);
        Assert.AreEqual(MessageType.ContinuationRequest, msg.MessageType);
    }

    [TestMethod]
    public async Task SendContinuationRequestToSelf_OriginatingMessageIdSelf_UsesMessageId()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(originatingMessageId: Constants.Self, messageId: "msg-1");

        await sut.SendContinuationRequestToSelf(ctx);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual("msg-1", msg.OriginatingMessageId,
            "When OriginatingMessageId is 'self', should use MessageId instead");
    }

    [TestMethod]
    public async Task SendContinuationRequestToSelf_OriginatingMessageIdNotSelf_PreservesOriginal()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(originatingMessageId: "orig-msg-1", messageId: "msg-1");

        await sut.SendContinuationRequestToSelf(ctx);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual("orig-msg-1", msg.OriginatingMessageId);
    }

    // ── SendToDeferredSubscription ──────────────────────────────────────

#pragma warning restore CS0618

    [TestMethod]
    public async Task SendToDeferredSubscription_RoutesToDeferredWithSequenceAndSessionId()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(sessionId: "session-42");

        await sut.SendToDeferredSubscription(ctx, 7);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.DeferredSubscriptionName, msg.To);
        Assert.AreEqual(7, msg.DeferralSequence);
        Assert.AreEqual("session-42", msg.OriginalSessionId);
        Assert.AreEqual("session-42", msg.SessionId);
    }

    [TestMethod]
    public async Task SendToDeferredSubscription_PreservesMessageTypeFromContext()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(messageType: MessageType.EventRequest);

        await sut.SendToDeferredSubscription(ctx, 1);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(MessageType.EventRequest, msg.MessageType,
            "Should preserve original message type, not set a new one");
    }

    [TestMethod]
    public async Task SendToDeferredSubscription_UsesCorrelationIdFromContext()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(correlationId: "corr-99");

        await sut.SendToDeferredSubscription(ctx, 1);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual("corr-99", msg.CorrelationId);
    }

    // ── SendProcessDeferredRequest ──────────────────────────────────────

    [TestMethod]
    public async Task SendProcessDeferredRequest_RoutesToDeferredProcessor()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);

        await sut.SendProcessDeferredRequest(CreateContext());

        var msg = sender.SentMessages.Single();
        Assert.AreEqual(Constants.DeferredProcessorId, msg.To);
        Assert.AreEqual(MessageType.ProcessDeferredRequest, msg.MessageType);
    }

    // ── Common property preservation ────────────────────────────────────

    [TestMethod]
    public async Task CreateResponse_SetsCorrelationIdToMessageId()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(messageId: "msg-42");

        await sut.SendResolutionResponse(ctx);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual("msg-42", msg.CorrelationId);
    }

    [TestMethod]
    public async Task CreateResponse_PreservesSessionId()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(sessionId: "session-42");

        await sut.SendResolutionResponse(ctx);

        Assert.AreEqual("session-42", sender.SentMessages.Single().SessionId);
    }

    [TestMethod]
    public async Task CreateResponse_SetsParentMessageIdToContextMessageId()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(messageId: "msg-99");

        await sut.SendResolutionResponse(ctx);

        Assert.AreEqual("msg-99", sender.SentMessages.Single().ParentMessageId);
    }

    [TestMethod]
    public async Task CreateResponse_OriginatingMessageIdSelf_FallsBackToMessageId()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(originatingMessageId: Constants.Self, messageId: "msg-1");

        await sut.SendResolutionResponse(ctx);

        Assert.AreEqual("msg-1", sender.SentMessages.Single().OriginatingMessageId);
    }

    [TestMethod]
    public async Task CreateResponse_OriginatingMessageIdNotSelf_PreservesOriginal()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(originatingMessageId: "orig-1");

        await sut.SendResolutionResponse(ctx);

        Assert.AreEqual("orig-1", sender.SentMessages.Single().OriginatingMessageId);
    }

    [TestMethod]
    public async Task CreateResponse_PreservesOriginatingFrom()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(from: "BillingEndpoint");

        await sut.SendResolutionResponse(ctx);

        Assert.AreEqual("BillingEndpoint", sender.SentMessages.Single().OriginatingFrom);
    }

    // ── CloudEvents identity preservation (AC15) ────────────────────────

    [TestMethod]
    public async Task CreateResponse_CopiesCloudEventIdentityFromContext()
    {
        // When the inbound message was a CloudEvent, the response sent to the Resolver
        // must carry its id/source/type/subject so the tracking/audit record preserves
        // the CloudEvent identity (AC15).
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext();
        ctx.CloudEventToReturn = new NimBus.Core.CloudEvents.CloudEvent
        {
            Id = "ce-1",
            Source = "urn:ext:billing",
            Type = "InvoiceCreated",
            Subject = "customer-42",
        };

        await sut.SendResolutionResponse(ctx);

        var msg = sender.SentMessages.Single();
        Assert.AreEqual("ce-1", msg.CloudEventId);
        Assert.AreEqual("urn:ext:billing", msg.CloudEventSource);
        Assert.AreEqual("InvoiceCreated", msg.CloudEventType);
        Assert.AreEqual("customer-42", msg.CloudEventSubject);
    }

    [TestMethod]
    public async Task CreateResponse_NativeMessage_LeavesCloudEventIdentityNull()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);

        await sut.SendResolutionResponse(CreateContext()); // GetCloudEvent() == null

        var msg = sender.SentMessages.Single();
        Assert.IsNull(msg.CloudEventId);
        Assert.IsNull(msg.CloudEventSource);
        Assert.IsNull(msg.CloudEventType);
        Assert.IsNull(msg.CloudEventSubject);
    }

    // ── Constructor ─────────────────────────────────────────────────────

    [TestMethod]
    public void Constructor_NullSender_ThrowsArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new ResponseService(null!));
    }

    // ── Park instrumentation (Phase 4.2 §2) ─────────────────────────────

    [TestMethod]
    public async Task SendToDeferredSubscription_emits_park_span_and_increments_counter()
    {
        using var capture = DeferredTelemetryCapture.Start();
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(sessionId: "session-42");
        ctx.To = "BillingEndpoint";

        await sut.SendToDeferredSubscription(ctx, deferralSequence: 3);

        var span = capture.Activities.Single(a => a.OperationName == "NimBus.DeferredProcessor.Park");
        Assert.AreEqual(System.Diagnostics.ActivityKind.Internal, span.Kind);
        Assert.AreEqual("BillingEndpoint", span.GetTagItem(NimBus.Core.Diagnostics.MessagingAttributes.NimBusEndpoint));
        Assert.AreEqual("session-42", span.GetTagItem(NimBus.Core.Diagnostics.MessagingAttributes.NimBusSessionKey));
        Assert.AreEqual(System.Diagnostics.ActivityStatusCode.Ok, span.Status);

        var counter = capture.Measurements.Single(m => m.Name == "nimbus.deferred.parked");
        Assert.AreEqual(1, counter.Value);
        Assert.AreEqual("BillingEndpoint", counter.Tags[NimBus.Core.Diagnostics.MessagingAttributes.NimBusEndpoint]);
        Assert.IsFalse(counter.Tags.ContainsKey(NimBus.Core.Diagnostics.MessagingAttributes.NimBusSessionKey),
            "Session key must stay off the parked counter (FR-045 high-cardinality guard)");
    }

    [TestMethod]
    public async Task SendToDeferredSubscription_failure_records_error_status_and_error_type_tag()
    {
        using var capture = DeferredTelemetryCapture.Start();
        var sender = new ThrowingSender(new InvalidOperationException("park blew up"));
        var sut = new ResponseService(sender);
        var ctx = CreateContext();
        ctx.To = "BillingEndpoint";

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => sut.SendToDeferredSubscription(ctx, deferralSequence: 1));

        var span = capture.Activities.Single(a => a.OperationName == "NimBus.DeferredProcessor.Park");
        Assert.AreEqual(System.Diagnostics.ActivityStatusCode.Error, span.Status);
        Assert.AreEqual(typeof(InvalidOperationException).FullName,
            span.GetTagItem(NimBus.Core.Diagnostics.MessagingAttributes.ErrorType));
        Assert.AreEqual(0, capture.Measurements.Count(m => m.Name == "nimbus.deferred.parked"),
            "Counter must not increment when the parking send threw");
    }

    // ── InboxMessageId: the delivery a stand-in stands in for ─────────────

    [TestMethod]
    public async Task SendRetryResponse_CarriesTheDeliverysMessageId_NotTheOriginatingOne()
    {
        // OriginatingMessageId names the start of the causal chain, which for an event
        // published from another event's handler is that other event's delivery.
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(messageId: "message-1", originatingMessageId: "triggering-message");

        await sut.SendRetryResponse(ctx, TimeSpan.Zero);

        Assert.AreEqual("message-1", sender.SentMessages.Single().InboxMessageId);
    }

    [TestMethod]
    public async Task SendRetryResponse_ForAFailedRetry_KeepsTheSourceMessageId()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(messageId: "retry-1", messageType: MessageType.RetryRequest, retryCount: 1);
        ctx.InboxMessageId = "message-1";

        await sut.SendRetryResponse(ctx, TimeSpan.Zero);

        Assert.AreEqual("message-1", sender.SentMessages.Single().InboxMessageId);
    }

    [TestMethod]
    public async Task SendToDeferredSubscription_CarriesTheParkedDeliverysMessageId()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);

        await sut.SendToDeferredSubscription(CreateContext(messageId: "message-1"), 0);

        Assert.AreEqual("message-1", sender.SentMessages.Single().InboxMessageId);
    }

    [TestMethod]
    public async Task SendToDeferredSubscription_ForAReplayParkedAgain_KeepsTheSourceMessageId()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(messageId: "replay-1");
        ctx.InboxMessageId = "message-1";

        await sut.SendToDeferredSubscription(ctx, 3);

        Assert.AreEqual("message-1", sender.SentMessages.Single().InboxMessageId);
    }

    [TestMethod]
    public async Task SendResolutionResponse_CarriesNoInboxMessageId()
    {
        var sender = new RecordingSender();
        var sut = new ResponseService(sender);
        var ctx = CreateContext(messageId: "retry-1", messageType: MessageType.RetryRequest);
        ctx.InboxMessageId = "message-1";

        await sut.SendResolutionResponse(ctx);

        Assert.IsNull(sender.SentMessages.Single().InboxMessageId);
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static FakeMessageContext CreateContext(
        string messageId = "message-1",
        string sessionId = "session-1",
        string eventId = "event-1",
        string correlationId = "correlation-1",
        string from = "StorefrontEndpoint",
        string originatingMessageId = "originating-1",
        int? retryCount = null,
        MessageType messageType = MessageType.EventRequest,
        MessageContent? messageContent = null)
    {
        return new FakeMessageContext
        {
            MessageId = messageId,
            SessionId = sessionId,
            EventId = eventId,
            CorrelationId = correlationId,
            From = from,
            OriginatingMessageId = originatingMessageId,
            OriginatingFrom = from,
            ParentMessageId = "self",
            To = "AnalyticsEndpoint",
            RetryCount = retryCount,
            MessageType = messageType,
            EventTypeId = "OrderPlaced",
            MessageContent = messageContent ?? new MessageContent
            {
                EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" }
            },
        };
    }

    // ── Fakes ────────────────────────────────────────────────────────────

    private sealed class SomeCustomException : Exception
    {
        public SomeCustomException(string message) : base(message)
        {
        }
    }

    private sealed class RecordingSender : ISender
    {
        public List<IMessage> SentMessages { get; } = new();
        public int LastDelay { get; private set; }

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            SentMessages.Add(message);
            LastDelay = messageEnqueueDelay;
            return Task.CompletedTask;
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            SentMessages.AddRange(messages);
            LastDelay = messageEnqueueDelay;
            return Task.CompletedTask;
        }

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
        {
            SentMessages.Add(message);
            return Task.FromResult(0L);
        }

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeMessageContext : IMessageContext
    {
        public string EventId { get; set; } = string.Empty;
        public string To { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string CorrelationId { get; set; } = string.Empty;
        public string MessageId { get; set; } = string.Empty;
        public MessageType MessageType { get; set; }
        public MessageContent MessageContent { get; set; } = new();
        public string ParentMessageId { get; set; } = string.Empty;
        public string OriginatingMessageId { get; set; } = string.Empty;
        public int? RetryCount { get; set; }
        public string OriginatingFrom { get; set; } = string.Empty;
        public string EventTypeId { get; set; } = string.Empty;
        public string OriginalSessionId { get; set; } = string.Empty;
        public int? DeferralSequence { get; set; }
        public string InboxMessageId { get; set; }
        public DateTime EnqueuedTimeUtc { get; set; }
        private string _from = string.Empty;

        // Mirrors the Service Bus MessageContext, which throws when From is absent on the wire.
        public bool FromIsMissing { get; set; }

        public string From
        {
            get => FromIsMissing ? throw new InvalidMessageException("Message.UserProperties[From] is not defined.") : _from;
            set => _from = value;
        }

        public string DeadLetterReason { get; set; }
        public string DeadLetterErrorDescription { get; set; }
        public string HandoffReason { get; set; }
        public string ExternalJobId { get; set; }
        public DateTime? ExpectedBy { get; set; }
        public bool IsDeferred { get; set; }
        public int ThrottleRetryCount { get; set; }
        public long? QueueTimeMs { get; set; }
        public long? ProcessingTimeMs { get; set; }
        public DateTime? HandlerStartedAtUtc { get; set; }
        public HandlerOutcome HandlerOutcome { get; set; }
        public HandoffMetadata? HandoffMetadata { get; set; }

        public NimBus.Core.CloudEvents.CloudEvent CloudEventToReturn { get; set; }
        public NimBus.Core.CloudEvents.CloudEvent GetCloudEvent() => CloudEventToReturn;

        public Task Complete(CancellationToken ct = default) => Task.CompletedTask;
        public Task Abandon(TransientException ex) => Task.CompletedTask;
        public Task DeadLetter(string reason, Exception? ex = null, CancellationToken ct = default) => Task.CompletedTask;
        public Task BlockSession(CancellationToken ct = default) => Task.CompletedTask;
        public Task UnblockSession(CancellationToken ct = default) => Task.CompletedTask;
        public Task<bool> IsSessionBlocked(CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> IsSessionBlockedByThis(CancellationToken ct = default) => Task.FromResult(false);
        public Task<bool> IsSessionBlockedByEventId(CancellationToken ct = default) => Task.FromResult(false);
        public Task<string> GetBlockedByEventId(CancellationToken ct = default) => Task.FromResult(string.Empty);
        public Task<int> GetNextDeferralSequenceAndIncrement(CancellationToken ct = default) => Task.FromResult(0);
        public Task IncrementDeferredCount(CancellationToken ct = default) => Task.CompletedTask;
        public Task DecrementDeferredCount(CancellationToken ct = default) => Task.CompletedTask;
        public Task<int> GetDeferredCount(CancellationToken ct = default) => Task.FromResult(0);
        public Task<bool> HasDeferredMessages(CancellationToken ct = default) => Task.FromResult(false);
        public Task ResetDeferredCount(CancellationToken ct = default) => Task.CompletedTask;
        public Task ScheduleRedelivery(TimeSpan delay, int throttleRetryCount, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class ThrowingSender : ISender
    {
        private readonly Exception _exception;
        public ThrowingSender(Exception exception) => _exception = exception;
        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken ct = default) => throw _exception;
        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken ct = default) => throw _exception;
        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken ct = default) => throw _exception;
        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken ct = default) => Task.CompletedTask;
    }

    private sealed class DeferredTelemetryCapture : IDisposable
    {
        public List<System.Diagnostics.Activity> Activities { get; } = new();
        public List<DeferredMeasurement> Measurements { get; } = new();
        public List<DeferredMeasurement> HistogramObservations { get; } = new();

        private readonly System.Diagnostics.ActivityListener _activityListener;
        private readonly System.Diagnostics.Metrics.MeterListener _meterListener;

        private DeferredTelemetryCapture()
        {
            _activityListener = new System.Diagnostics.ActivityListener
            {
                ShouldListenTo = src => src.Name == NimBus.Core.Diagnostics.NimBusInstrumentation.DeferredProcessorActivitySourceName,
                Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) =>
                    System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = a => Activities.Add(a),
            };
            System.Diagnostics.ActivitySource.AddActivityListener(_activityListener);

            _meterListener = new System.Diagnostics.Metrics.MeterListener
            {
                InstrumentPublished = (instrument, listener) =>
                {
                    if (instrument.Meter.Name == NimBus.Core.Diagnostics.NimBusInstrumentation.DeferredProcessorMeterName)
                        listener.EnableMeasurementEvents(instrument);
                },
            };
            _meterListener.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
                Measurements.Add(new DeferredMeasurement(instrument.Name, value, ToDictionary(tags))));
            _meterListener.SetMeasurementEventCallback<double>((instrument, value, tags, _) =>
                HistogramObservations.Add(new DeferredMeasurement(instrument.Name, (long)value, ToDictionary(tags))));
            _meterListener.Start();
        }

        public static DeferredTelemetryCapture Start() => new();

        private static IReadOnlyDictionary<string, object?> ToDictionary(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            var dict = new Dictionary<string, object?>(tags.Length);
            foreach (var t in tags) dict[t.Key] = t.Value;
            return dict;
        }

        public void Dispose()
        {
            _activityListener.Dispose();
            _meterListener.Dispose();
        }
    }

    private sealed record DeferredMeasurement(string Name, long Value, IReadOnlyDictionary<string, object?> Tags);
}
