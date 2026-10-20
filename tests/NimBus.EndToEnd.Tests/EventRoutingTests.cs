using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.EndToEnd.Tests.Infrastructure;

namespace NimBus.EndToEnd.Tests;

/// <summary>
/// Tests that events are routed to the correct handler based on event type.
/// </summary>
[TestClass]
public class EventRoutingTests
{
    [TestMethod]
    public async Task Publish_DifferentEventTypes_RoutedToCorrectHandlers()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var orderPlacedHandler = new RecordingOrderPlacedHandler();
        var orderCancelledHandler = new RecordingOrderCancelledHandler();
        fixture.RegisterHandler(() => orderPlacedHandler);
        fixture.RegisterHandler(() => orderCancelledHandler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s1") { OrderId = "ORD-100" });
        await fixture.Publisher.Publish(new OrderCancelled("s2") { OrderId = "ORD-101", Reason = "Changed mind" });
        await fixture.Publisher.Publish(new OrderPlaced("s3") { OrderId = "ORD-102" });
        await fixture.DeliverAll();

        // Assert
        Assert.AreEqual(2, orderPlacedHandler.ReceivedEvents.Count, "OrderPlaced handler should receive 2 events");
        Assert.AreEqual(1, orderCancelledHandler.ReceivedEvents.Count, "OrderCancelled handler should receive 1 event");

        Assert.AreEqual("ORD-100", orderPlacedHandler.ReceivedEvents[0].OrderId);
        Assert.AreEqual("ORD-102", orderPlacedHandler.ReceivedEvents[1].OrderId);
        Assert.AreEqual("ORD-101", orderCancelledHandler.ReceivedEvents[0].OrderId);
        Assert.AreEqual("Changed mind", orderCancelledHandler.ReceivedEvents[0].Reason);
    }

    [TestMethod]
    public async Task Publish_UnregisteredEventType_UnsupportedResponseSent()
    {
        // Arrange — register OrderPlaced handler but NOT UnknownEvent
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        // Act — publish UnknownEvent (no handler registered)
        await fixture.Publisher.Publish(new UnknownEvent { Data = "test" });
        await fixture.DeliverAll();

        // Assert — StrictMessageHandler sends UnsupportedResponse for unregistered types
        Assert.AreEqual(0, handler.ReceivedEvents.Count, "OrderPlaced handler should not be invoked");
        var responses = fixture.ResponseBus.SentMessages;
        Assert.AreEqual(1, responses.Count);
        Assert.AreEqual(MessageType.UnsupportedResponse, responses[0].MessageType);
    }

    [TestMethod]
    public async Task Publish_MixedEventTypes_EachHandlerGetsCorrectContext()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var orderPlacedHandler = new RecordingOrderPlacedHandler();
        var orderCancelledHandler = new RecordingOrderCancelledHandler();
        fixture.RegisterHandler(() => orderPlacedHandler);
        fixture.RegisterHandler(() => orderCancelledHandler);

        // Act
        await fixture.Publisher.Publish(new OrderPlaced("s1") { OrderId = "ORD-200" });
        await fixture.Publisher.Publish(new OrderCancelled("s2") { OrderId = "ORD-201", Reason = "Defective" });
        await fixture.DeliverAll();

        // Assert — each handler's context reflects the correct event type
        Assert.AreEqual("OrderPlaced", orderPlacedHandler.ReceivedContexts[0].EventType);
        Assert.AreEqual("OrderCancelled", orderCancelledHandler.ReceivedContexts[0].EventType);
    }

    [TestMethod]
    public async Task Publish_SameEventTypeTwice_HandlerInvokedTwice()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        // Act — publish two distinct OrderPlaced events
        await fixture.Publisher.Publish(new OrderPlaced("s1") { OrderId = "ORD-300", Amount = 10m });
        await fixture.Publisher.Publish(new OrderPlaced("s2") { OrderId = "ORD-301", Amount = 20m });
        await fixture.DeliverAll();

        // Assert
        Assert.AreEqual(2, handler.ReceivedEvents.Count);
        Assert.AreEqual(10m, handler.ReceivedEvents[0].Amount);
        Assert.AreEqual(20m, handler.ReceivedEvents[1].Amount);
    }

    [TestMethod]
    public async Task Publish_SingleEvent_MultipleSubscribers_EachReceivesEvent()
    {
        // Arrange
        var publisherAndSubscriberA = new EndToEndFixture();
        var subscriberB = new EndToEndFixture();

        var handlerA = new RecordingOrderPlacedHandler();
        var handlerB = new RecordingOrderPlacedHandler();
        publisherAndSubscriberA.RegisterHandler(() => handlerA);
        subscriberB.RegisterHandler(() => handlerB);

        // Act
        await publisherAndSubscriberA.Publisher.Publish(new OrderPlaced("s-fanout") { OrderId = "ORD-FANOUT" });
        await publisherAndSubscriberA.PublishBus.DeliverAllToSubscribers([
            publisherAndSubscriberA.MessageHandler,
            subscriberB.MessageHandler
        ]);

        // Assert
        Assert.AreEqual(1, handlerA.ReceivedEvents.Count, "Subscriber A should receive the event once");
        Assert.AreEqual(1, handlerB.ReceivedEvents.Count, "Subscriber B should receive the event once");
        Assert.AreEqual("ORD-FANOUT", handlerA.ReceivedEvents[0].OrderId);
        Assert.AreEqual("ORD-FANOUT", handlerB.ReceivedEvents[0].OrderId);

        Assert.AreEqual(1, publisherAndSubscriberA.ResponseBus.SentMessages.Count);
        Assert.AreEqual(1, subscriberB.ResponseBus.SentMessages.Count);
        Assert.AreEqual(MessageType.ResolutionResponse, publisherAndSubscriberA.ResponseBus.SentMessages[0].MessageType);
        Assert.AreEqual(MessageType.ResolutionResponse, subscriberB.ResponseBus.SentMessages[0].MessageType);
    }

    [TestMethod]
    public async Task Publish_SameSession_FirstFails_SecondDeferred_SkipFirst_SecondEventuallyProcesses()
    {
        // Arrange
        const string sessionId = "session-shared";
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionFactory = order => order.OrderId == "ORD-FAIL"
                ? new InvalidOperationException("First message fails")
                : null
        };
        fixture.RegisterHandler(() => handler);

        // Act 1: publish two messages on the same session; first fails, second is deferred.
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-FAIL" });
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-SECOND" });
        await fixture.DeliverAll();

        // Assert intermediate state: no successful handler execution yet.
        Assert.AreEqual(0, handler.ReceivedEvents.Count);

        var responsesAfterFailure = fixture.ResponseBus.SentMessages;
        var failedEventId = responsesAfterFailure
            .Single(response => response.MessageType == MessageType.ErrorResponse)
            .EventId;

        var deferredSecondMessage = responsesAfterFailure.Single(response =>
            response.MessageType == MessageType.EventRequest &&
            response.To == Constants.DeferredSubscriptionName &&
            response.MessageContent.EventContent!.EventJson!.Contains("ORD-SECOND", StringComparison.Ordinal));

        // Act 2: skip the failed message.
        await fixture.PublishBus.Send(CreateSkipRequest(sessionId, failedEventId, "OrderPlaced"));
        await fixture.DeliverAll();

        // Act 3: simulate deferred processing by republishing the deferred second message.
        await fixture.PublishBus.Send(CreateRepublishedDeferredEventRequest(deferredSecondMessage));
        await fixture.DeliverAll();

        // Assert: second message is eventually processed successfully.
        Assert.AreEqual(1, handler.ReceivedEvents.Count);
        Assert.AreEqual("ORD-SECOND", handler.ReceivedEvents[0].OrderId);
        Assert.IsTrue(fixture.ResponseBus.SentMessages.Any(response => response.MessageType == MessageType.SkipResponse));
        Assert.IsTrue(fixture.ResponseBus.SentMessages.Any(response => response.MessageType == MessageType.ProcessDeferredRequest));
        Assert.IsTrue(fixture.ResponseBus.SentMessages.Any(response =>
            response.MessageType == MessageType.ResolutionResponse &&
            response.MessageContent.EventContent!.EventJson!.Contains("ORD-SECOND", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Publish_ResubmissionFromManager_HandlerReinvokedAndResolutionSent()
    {
        // Arrange
        const string sessionId = "session-resub";
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("First attempt fails")
        };
        fixture.RegisterHandler(() => handler);

        // Act 1: publish event that fails → session blocked
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-RESUB" });
        var results = await fixture.DeliverAllWithResults();

        var errorResponse = fixture.ResponseBus.SentMessages
            .Single(r => r.MessageType == MessageType.ErrorResponse);
        var failedEventId = errorResponse.EventId;

        // Clear the failure so resubmission succeeds
        handler.ExceptionToThrow = null;

        // Act 2: send ResubmissionRequest from Manager
        await fixture.PublishBus.Send(CreateResubmissionRequest(
            sessionId, failedEventId, "OrderPlaced", errorResponse.MessageContent));
        await fixture.DeliverAll();

        // Assert — handler was re-invoked and succeeded
        Assert.AreEqual(1, handler.ReceivedEvents.Count, "Handler should be invoked on resubmission");
        Assert.AreEqual("ORD-RESUB", handler.ReceivedEvents[0].OrderId);

        var resolutionResponses = fixture.ResponseBus.SentMessages
            .Where(r => r.MessageType == MessageType.ResolutionResponse).ToList();
        Assert.IsTrue(resolutionResponses.Count >= 1, "Should have ResolutionResponse after successful resubmission");
    }

    [TestMethod]
    public async Task Publish_SameSession_EventsProcessedInFIFOOrder()
    {
        // Arrange
        const string sessionId = "session-fifo";
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler();
        fixture.RegisterHandler(() => handler);

        // Act — publish 3 events on the same session
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "FIFO-1", Amount = 10m });
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "FIFO-2", Amount = 20m });
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "FIFO-3", Amount = 30m });
        await fixture.DeliverAll();

        // Assert — events received in publish order (FIFO)
        Assert.AreEqual(3, handler.ReceivedEvents.Count);
        Assert.AreEqual("FIFO-1", handler.ReceivedEvents[0].OrderId);
        Assert.AreEqual("FIFO-2", handler.ReceivedEvents[1].OrderId);
        Assert.AreEqual("FIFO-3", handler.ReceivedEvents[2].OrderId);
    }

    [TestMethod]
    public async Task Publish_FanOut_OneSubscriberFails_OtherStillProcesses()
    {
        // Arrange
        var subscriberA = new EndToEndFixture();
        var subscriberB = new EndToEndFixture();

        var failingHandler = new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("Subscriber A fails")
        };
        var successfulHandler = new RecordingOrderPlacedHandler();

        subscriberA.RegisterHandler(() => failingHandler);
        subscriberB.RegisterHandler(() => successfulHandler);

        // Act
        await subscriberA.Publisher.Publish(new OrderPlaced("s-fanout-isolated") { OrderId = "ORD-ISO-001" });
        await subscriberA.PublishBus.DeliverAllToSubscribers([
            subscriberA.MessageHandler,
            subscriberB.MessageHandler
        ]);

        // Assert
        Assert.AreEqual(0, failingHandler.ReceivedEvents.Count, "Failing subscriber should not record successful events");
        Assert.AreEqual(1, successfulHandler.ReceivedEvents.Count, "Healthy subscriber should still process the event");
        Assert.AreEqual("ORD-ISO-001", successfulHandler.ReceivedEvents[0].OrderId);

        Assert.IsTrue(subscriberA.ResponseBus.SentMessages.Any(m => m.MessageType == MessageType.ErrorResponse));
        Assert.IsFalse(subscriberA.ResponseBus.SentMessages.Any(m => m.MessageType == MessageType.ResolutionResponse));
        Assert.IsTrue(subscriberB.ResponseBus.SentMessages.Any(m => m.MessageType == MessageType.ResolutionResponse));
    }

    [TestMethod]
    public async Task Publish_UnauthorizedSkipRequest_IsDeadLetteredAndNotApplied()
    {
        // Arrange
        const string sessionId = "session-unauth-skip";
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionFactory = order => order.OrderId == "ORD-BLOCK"
                ? new InvalidOperationException("Block this event")
                : null
        };
        fixture.RegisterHandler(() => handler);

        // Act 1: block the session with a failing message.
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-BLOCK" });
        await fixture.DeliverAll();
        var failedEventId = fixture.ResponseBus.SentMessages
            .Single(response => response.MessageType == MessageType.ErrorResponse)
            .EventId;

        // Act 2: attempt unauthorized skip.
        await fixture.PublishBus.Send(CreateSkipRequest(sessionId, failedEventId, "OrderPlaced", "UnauthorizedSender"));
        var unauthorizedSkipResults = await fixture.DeliverAllWithResults();

        // Assert
        Assert.AreEqual(1, unauthorizedSkipResults.Count, "Should deliver the unauthorized skip request");
        Assert.IsTrue(unauthorizedSkipResults[0].Session.WasDeadLettered, "Unauthorized skip should be dead-lettered");
        Assert.IsFalse(fixture.ResponseBus.SentMessages.Any(response => response.MessageType == MessageType.SkipResponse),
            "Unauthorized sender must not produce SkipResponse");
    }

    [TestMethod]
    public async Task Publish_BlockedSession_DoesNotBlockDifferentSession()
    {
        // Arrange
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionFactory = order => order.OrderId == "ORD-BLOCK-SESSION-A"
                ? new InvalidOperationException("Session A fails")
                : null
        };
        fixture.RegisterHandler(() => handler);

        // Act: first message blocks session A; second message in session B should still process.
        await fixture.Publisher.Publish(new OrderPlaced("session-A") { OrderId = "ORD-BLOCK-SESSION-A" });
        await fixture.Publisher.Publish(new OrderPlaced("session-B") { OrderId = "ORD-PROCESS-SESSION-B" });
        await fixture.DeliverAll();

        // Assert
        Assert.AreEqual(1, handler.ReceivedEvents.Count, "Only session-B event should be processed successfully");
        Assert.AreEqual("ORD-PROCESS-SESSION-B", handler.ReceivedEvents[0].OrderId);
        Assert.IsTrue(fixture.ResponseBus.SentMessages.Any(response => response.MessageType == MessageType.ErrorResponse),
            "Session-A failure should emit error response");
        Assert.IsTrue(fixture.ResponseBus.SentMessages.Any(response =>
            response.MessageType == MessageType.ResolutionResponse &&
            response.MessageContent.EventContent!.EventJson!.Contains("ORD-PROCESS-SESSION-B", StringComparison.Ordinal)),
            "Session-B should complete with resolution response");
    }

    private static Message CreateSkipRequest(string sessionId, string blockedByEventId, string eventTypeId, string from = Constants.ManagerId)
    {
        return new Message
        {
            To = eventTypeId,
            SessionId = sessionId,
            CorrelationId = Guid.NewGuid().ToString(),
            MessageId = Guid.NewGuid().ToString(),
            EventId = blockedByEventId,
            EventTypeId = eventTypeId,
            MessageType = MessageType.SkipRequest,
            From = from,
            OriginatingMessageId = Constants.Self,
            ParentMessageId = Constants.Self,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventTypeId,
                    EventJson = "{}"
                }
            }
        };
    }

    private static Message CreateResubmissionRequest(string sessionId, string eventId, string eventTypeId, MessageContent messageContent)
    {
        return new Message
        {
            To = eventTypeId,
            SessionId = sessionId,
            CorrelationId = Guid.NewGuid().ToString(),
            MessageId = Guid.NewGuid().ToString(),
            EventId = eventId,
            EventTypeId = eventTypeId,
            MessageType = MessageType.ResubmissionRequest,
            From = Constants.ManagerId,
            OriginatingMessageId = Constants.Self,
            ParentMessageId = Constants.Self,
            MessageContent = messageContent
        };
    }

    private static Message CreateRepublishedDeferredEventRequest(IMessage deferredMessage)
    {
        return new Message
        {
            To = deferredMessage.EventTypeId,
            SessionId = deferredMessage.SessionId,
            CorrelationId = deferredMessage.CorrelationId,
            MessageId = Guid.NewGuid().ToString(),
            EventId = deferredMessage.EventId,
            EventTypeId = deferredMessage.EventTypeId,
            MessageType = MessageType.EventRequest,
            From = deferredMessage.From,
            OriginatingFrom = deferredMessage.OriginatingFrom,
            OriginatingMessageId = deferredMessage.OriginatingMessageId,
            ParentMessageId = deferredMessage.ParentMessageId,
            RetryCount = deferredMessage.RetryCount,
            MessageContent = deferredMessage.MessageContent,
            DiagnosticId = deferredMessage.DiagnosticId
        };
    }
}
