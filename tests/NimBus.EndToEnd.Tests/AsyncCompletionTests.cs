#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.EndToEnd.Tests.Infrastructure;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;

namespace NimBus.EndToEnd.Tests;

/// <summary>
/// End-to-end coverage for the PendingHandoff outcome (issue #15 / spec 002).
///
/// Test 1 (SC-008) — handler signals PendingHandoff, siblings defer, the
/// Manager completes the handoff, and deferred siblings replay in FIFO.
/// Critically asserts the user handler is NOT re-invoked on settlement (SC-003).
///
/// Test 2 (SC-009) — handler signals PendingHandoff, the Manager fails the
/// handoff carrying DMF-shaped error text. Asserts the operator-supplied
/// errorText is preserved verbatim on the resulting ErrorResponse audit row
/// (SC-004 / NFR-004), then drives an operator Skip and asserts siblings
/// replay through the existing skip flow (SC-006).
///
/// Settlement messages (HandoffCompletedRequest / HandoffFailedRequest) are
/// constructed directly to mirror what <c>NimBus.Manager.ManagerClient</c>
/// would emit — the in-memory fixture has no <c>ServiceBusClient</c>, so
/// invoking <c>ManagerClient</c> directly is not possible here.
/// </summary>
[TestClass]
public class AsyncCompletionTests
{
    private const string EndpointName = "OrderPlaced";

    [TestMethod]
    public async Task PendingHandoff_DefersSiblings_Then_CompleteHandoff_ReplaysInFifoOrder()
    {
        // Arrange — handler that hands off the FIRST event it sees, returns
        // normally on subsequent invocations. Records every call so we can
        // verify the original event is NOT re-invoked on settlement (SC-003).
        const string sessionId = "session-handoff-complete";
        var fixture = new EndToEndFixture();
        var handler = new HandoffOnFirstHandler
        {
            HandoffReason = "DMF import in flight",
            ExternalJobId = "JOB-1",
            ExpectedBy = TimeSpan.FromMinutes(2),
        };
        fixture.RegisterHandler(() => handler);

        // Act 1 — publish three messages on the same session. The first
        // triggers MarkPendingHandoff; the next two should park on the
        // Deferred subscription.
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-CREATE-42" });
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-UPDATE-A" });
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-UPDATE-B" });
        await fixture.DeliverAll();

        // Assert intermediate state.
        Assert.AreEqual(1, handler.HandleCalls,
            "Only the first message should have invoked the handler; siblings must defer.");

        var responses = fixture.ResponseBus.SentMessages;

        var pendingHandoff = responses.SingleOrDefault(r => r.MessageType == MessageType.PendingHandoffResponse);
        Assert.IsNotNull(pendingHandoff, "Subscriber should emit a PendingHandoffResponse for the first event.");
        Assert.AreEqual("DMF import in flight", pendingHandoff.HandoffReason);
        Assert.AreEqual("JOB-1", pendingHandoff.ExternalJobId);
        Assert.IsNotNull(pendingHandoff.ExpectedBy, "ExpectedBy must be projected on the PendingHandoff response.");

        var pendingEventId = pendingHandoff.EventId;
        var pendingMessageId = pendingHandoff.CorrelationId;
        var pendingOriginatingMessageId = pendingHandoff.OriginatingMessageId;

        var deferralResponses = responses.Where(r => r.MessageType == MessageType.DeferralResponse).ToList();
        Assert.AreEqual(2, deferralResponses.Count,
            "Both sibling messages should have produced DeferralResponse audit rows.");

        var deferredEnvelopes = responses
            .Where(r => r.MessageType == MessageType.EventRequest && r.To == Constants.DeferredSubscriptionName)
            .ToList();
        Assert.AreEqual(2, deferredEnvelopes.Count,
            "Both siblings should have been parked on the Deferred subscription.");

        Assert.IsFalse(responses.Any(r => r.MessageType == MessageType.ResolutionResponse),
            "ResolutionResponse must NOT fire while the handoff is in flight.");

        // Capture the parked sibling envelopes in order (FIFO) so we can
        // simulate the DeferredProcessor's republish step below.
        var deferredSiblingsInOrder = deferredEnvelopes
            .OrderBy(r => r.DeferralSequence ?? 0)
            .ToList();

        // Act 2 — Manager settles the handoff via CompleteHandoff. Construct
        // the message directly (mirrors the retired ManagerClient settlement path).
        var handoffCompleted = CreateHandoffCompletedRequest(
            sessionId,
            pendingEventId,
            pendingMessageId,
            pendingOriginatingMessageId,
            EndpointName);
        await fixture.PublishBus.Send(handoffCompleted);
        await fixture.DeliverAll();

        // Assert — the original event flips to Completed without re-invoking
        // the user handler (SC-003).
        Assert.AreEqual(1, handler.HandleCalls,
            "HandoffCompleted must NOT re-invoke the user handler on the original event.");
        Assert.IsTrue(fixture.ResponseBus.SentMessages.Any(r =>
            r.MessageType == MessageType.ResolutionResponse && r.EventId == pendingEventId),
            "HandoffCompleted should produce a ResolutionResponse for the original EventId (Pending → Completed).");
        Assert.IsTrue(fixture.ResponseBus.SentMessages.Any(r => r.MessageType == MessageType.ProcessDeferredRequest),
            "HandoffCompleted should ask the DeferredProcessor to drain parked siblings.");

        // Act 3 — simulate DeferredProcessor republish (mirrors how the
        // existing skip / resubmit flow is exercised in EventRoutingTests).
        // Siblings are republished in FIFO order; each becomes a fresh
        // EventRequest on the main subscription.
        foreach (var sibling in deferredSiblingsInOrder)
        {
            await fixture.PublishBus.Send(CreateRepublishedDeferredEventRequest(sibling));
        }
        await fixture.DeliverAll();
        await DrainUntilEmpty(fixture);

        // Assert — siblings replayed in FIFO and the original handler
        // invocation count is unchanged.
        Assert.AreEqual(3, handler.HandleCalls,
            "Handler should now be invoked once per event: 1 original (handed off) + 2 replayed siblings.");
        Assert.AreEqual(1, handler.HandoffCallCount,
            "MarkPendingHandoff should fire exactly once — only the first message hands off.");
        CollectionAssert.AreEqual(
            new[] { "ORD-CREATE-42", "ORD-UPDATE-A", "ORD-UPDATE-B" },
            handler.ReceivedOrderIds,
            "Siblings must replay in FIFO order behind the original event.");

        var resolutionResponses = fixture.ResponseBus.SentMessages
            .Where(r => r.MessageType == MessageType.ResolutionResponse).ToList();
        Assert.IsTrue(resolutionResponses.Any(r => r.EventId == pendingEventId),
            "Original EventId must produce a ResolutionResponse (Pending → Completed).");
        Assert.IsTrue(resolutionResponses.Count >= 3,
            $"Expected at least 3 ResolutionResponses (original + 2 siblings); got {resolutionResponses.Count}.");
    }

    [TestMethod]
    public async Task IHandoffClient_CompleteAsync_PublishesHandoffCompletedRequest_AndDrivesResolution()
    {
        // Validates the IHandoffClient surface end-to-end: the new typed
        // settlement API must produce a HandoffCompletedRequest that flows
        // through the subscriber pipeline exactly the same way the legacy
        // retired IManagerClient settlement path did.
        const string sessionId = "session-handoff-client-complete";
        var fixture = new EndToEndFixture();
        var handler = new HandoffOnFirstHandler
        {
            HandoffReason = "DMF import in flight",
            ExternalJobId = "JOB-CLIENT-OK",
        };
        fixture.RegisterHandler(() => handler);

        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-CLIENT-OK" });
        await fixture.DeliverAll();

        var pendingHandoff = fixture.ResponseBus.SentMessages.Single(r => r.MessageType == MessageType.PendingHandoffResponse);
        var coords = new HandoffSettlement(
            EventId: pendingHandoff.EventId,
            SessionId: sessionId,
            MessageId: pendingHandoff.CorrelationId!,
            EventTypeId: "OrderPlaced",
            CorrelationId: pendingHandoff.CorrelationId!,
            OriginatingMessageId: pendingHandoff.OriginatingMessageId!);

        // Invoke IHandoffClient against the PublishBus directly — same on-wire
        // path as production, just routed through the in-memory fixture.
        var handoffClient = new HandoffClient(
            fixture.PublishBus,
            new HandoffClientOptions { Endpoint = EndpointName });
        await handoffClient.CompleteAsync(coords, new { importedRecordId = "ext-1" });
        await fixture.DeliverAll();

        Assert.AreEqual(1, handler.HandleCalls,
            "HandoffClient.CompleteAsync must NOT re-invoke the user handler.");

        var resolution = fixture.ResponseBus.SentMessages.SingleOrDefault(r =>
            r.MessageType == MessageType.ResolutionResponse && r.EventId == pendingHandoff.EventId);
        Assert.IsNotNull(resolution, "CompleteAsync should drive a ResolutionResponse for the original EventId (Pending → Completed).");
    }

    [TestMethod]
    public async Task IHandoffClient_FailAsync_PreservesErrorText_VerbatimOnErrorResponse()
    {
        // Sister test of the CompleteAsync case above — IHandoffClient.FailAsync
        // must produce an ErrorResponse whose ErrorText carries the operator-
        // supplied message verbatim (SC-004 / NFR-004).
        const string sessionId = "session-handoff-client-fail";
        const string dmfErrorText = "DMF rejected: missing mandatory address line";
        const string dmfErrorType = "DmfValidationError";

        var fixture = new EndToEndFixture();
        var handler = new HandoffOnFirstHandler
        {
            HandoffReason = "DMF import in flight",
            ExternalJobId = "JOB-CLIENT-FAIL",
        };
        fixture.RegisterHandler(() => handler);

        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-CLIENT-FAIL" });
        await fixture.DeliverAll();

        var pendingHandoff = fixture.ResponseBus.SentMessages.Single(r => r.MessageType == MessageType.PendingHandoffResponse);
        var coords = new HandoffSettlement(
            EventId: pendingHandoff.EventId,
            SessionId: sessionId,
            MessageId: pendingHandoff.CorrelationId!,
            EventTypeId: "OrderPlaced",
            CorrelationId: pendingHandoff.CorrelationId!,
            OriginatingMessageId: pendingHandoff.OriginatingMessageId!);

        var handoffClient = new HandoffClient(
            fixture.PublishBus,
            new HandoffClientOptions { Endpoint = EndpointName });
        await handoffClient.FailAsync(coords, dmfErrorText, dmfErrorType);
        await fixture.DeliverAll();
        await DrainUntilEmpty(fixture);

        Assert.AreEqual(1, handler.HandleCalls,
            "HandoffClient.FailAsync must NOT re-invoke the user handler.");

        var errorResponse = fixture.ResponseBus.SentMessages
            .Where(r => r.MessageType == MessageType.ErrorResponse && r.EventId == pendingHandoff.EventId)
            .OrderBy(r => r.MessageId)
            .LastOrDefault();
        Assert.IsNotNull(errorResponse, "FailAsync should produce an ErrorResponse for the original EventId.");
        StringAssert.Contains(errorResponse!.MessageContent?.ErrorContent?.ErrorText ?? string.Empty, dmfErrorText,
            $"Operator-supplied errorText must round-trip verbatim. Got: {errorResponse.MessageContent?.ErrorContent?.ErrorText}");
    }

    [TestMethod]
    public async Task PendingHandoff_FailHandoff_PreservesErrorText_ThenOperatorSkip()
    {
        // Arrange — handler always hands off (only one event reaches it,
        // siblings defer behind the blocked session).
        const string sessionId = "session-handoff-fail";
        const string dmfErrorText = "DMF rejected: invalid postal code";
        const string dmfErrorType = "DmfValidationError";

        var fixture = new EndToEndFixture();
        var handler = new HandoffOnFirstHandler
        {
            HandoffReason = "DMF import in flight",
            ExternalJobId = "JOB-FAIL",
            ExpectedBy = null,
        };
        fixture.RegisterHandler(() => handler);

        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-CREATE-42" });
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-UPDATE-A" });
        await fixture.DeliverAll();

        Assert.AreEqual(1, handler.HandleCalls);

        var responses = fixture.ResponseBus.SentMessages;
        var pendingHandoff = responses.Single(r => r.MessageType == MessageType.PendingHandoffResponse);
        var pendingEventId = pendingHandoff.EventId;
        var pendingMessageId = pendingHandoff.CorrelationId;
        var pendingOriginatingMessageId = pendingHandoff.OriginatingMessageId;

        var deferredSibling = responses.Single(r =>
            r.MessageType == MessageType.EventRequest &&
            r.To == Constants.DeferredSubscriptionName &&
            r.MessageContent?.EventContent?.EventJson?.Contains("ORD-UPDATE-A", StringComparison.Ordinal) == true);

        // Act 1 — Manager fails the handoff with operator-supplied error text
        // (mirrors the retired ManagerClient settlement path).
        var handoffFailed = CreateHandoffFailedRequest(
            sessionId,
            pendingEventId,
            pendingMessageId,
            pendingOriginatingMessageId,
            EndpointName,
            dmfErrorText,
            dmfErrorType);
        await fixture.PublishBus.Send(handoffFailed);
        await fixture.DeliverAll();
        await DrainUntilEmpty(fixture);

        // Assert — handler must NOT have been re-invoked by FailHandoff.
        Assert.AreEqual(1, handler.HandleCalls,
            "FailHandoff must not re-invoke the user handler.");

        // Locate the ErrorResponse caused by HandoffFailedRequest.
        var errorResponse = fixture.ResponseBus.SentMessages
            .Where(r => r.MessageType == MessageType.ErrorResponse && r.EventId == pendingEventId)
            .OrderBy(r => r.MessageId)
            .LastOrDefault();
        Assert.IsNotNull(errorResponse, "FailHandoff should produce an ErrorResponse to the Resolver.");

        // SC-004 / NFR-004: errorText is preserved verbatim. ErrorType wraps
        // a synthetic HandoffFailedException by intent — see ADR-012.
        var errorContent = errorResponse.MessageContent?.ErrorContent;
        Assert.IsNotNull(errorContent, "ErrorContent must be populated.");
        Assert.IsNotNull(errorContent.ErrorText, "ErrorText must not be null.");
        StringAssert.Contains(errorContent.ErrorText, dmfErrorText,
            $"Operator-supplied errorText must be preserved verbatim. Got: {errorContent.ErrorText}");

        // Act 2 — operator-initiated Skip on the failed handoff entry,
        // mirroring ManagerClient.Skip. Existing skip → unblock → replay flow
        // must work without modification (SC-006).
        var skipRequest = CreateSkipRequest(
            sessionId,
            pendingEventId,
            pendingMessageId,
            pendingOriginatingMessageId,
            EndpointName);
        await fixture.PublishBus.Send(skipRequest);
        await fixture.DeliverAll();

        Assert.IsTrue(
            fixture.ResponseBus.SentMessages.Any(r => r.MessageType == MessageType.SkipResponse),
            "Skip should produce a SkipResponse audit row.");
        Assert.IsTrue(
            fixture.ResponseBus.SentMessages.Any(r => r.MessageType == MessageType.ProcessDeferredRequest),
            "Skip should ask the DeferredProcessor to drain parked siblings.");

        // Act 3 — simulate DeferredProcessor republish.
        await fixture.PublishBus.Send(CreateRepublishedDeferredEventRequest(deferredSibling));
        await fixture.DeliverAll();
        await DrainUntilEmpty(fixture);

        // Assert — sibling replays after the skip and reaches Completed.
        Assert.IsTrue(handler.ReceivedOrderIds.Contains("ORD-UPDATE-A"),
            "Sibling must replay after operator skip.");
        Assert.IsTrue(
            fixture.ResponseBus.SentMessages.Any(r =>
                r.MessageType == MessageType.ResolutionResponse &&
                r.MessageContent?.EventContent?.EventJson?.Contains("ORD-UPDATE-A", StringComparison.Ordinal) == true),
            "Sibling should produce a ResolutionResponse after replay.");
    }

    /// <summary>
    /// Drives the bus repeatedly until no further messages are produced.
    /// HandoffCompletedRequest replay generates ContinuationRequests, which
    /// generate further work; one DeliverAll pass is not always enough.
    /// </summary>
    private static async Task DrainUntilEmpty(EndToEndFixture fixture)
    {
        for (var i = 0; i < 10; i++)
        {
            if (fixture.PublishBus.MessageCount == 0)
                return;
            await fixture.DeliverAll();
        }

        Assert.IsTrue(fixture.PublishBus.MessageCount == 0,
            "Bus did not quiesce within 10 drain passes.");
    }

    private static Message CreateHandoffCompletedRequest(
        string sessionId,
        string eventId,
        string? parentMessageId,
        string? originatingMessageId,
        string endpoint)
    {
        return new Message
        {
            To = endpoint,
            From = Constants.ManagerId,
            SessionId = sessionId,
            CorrelationId = Guid.NewGuid().ToString(),
            MessageId = Guid.NewGuid().ToString(),
            EventId = eventId,
            EventTypeId = "OrderPlaced",
            MessageType = MessageType.HandoffCompletedRequest,
            OriginatingMessageId = originatingMessageId ?? parentMessageId,
            ParentMessageId = parentMessageId,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = "OrderPlaced",
                    EventJson = "{}",
                },
            },
        };
    }

    private static Message CreateHandoffFailedRequest(
        string sessionId,
        string eventId,
        string? parentMessageId,
        string? originatingMessageId,
        string endpoint,
        string errorText,
        string errorType)
    {
        return new Message
        {
            To = endpoint,
            From = Constants.ManagerId,
            SessionId = sessionId,
            CorrelationId = Guid.NewGuid().ToString(),
            MessageId = Guid.NewGuid().ToString(),
            EventId = eventId,
            EventTypeId = "OrderPlaced",
            MessageType = MessageType.HandoffFailedRequest,
            OriginatingMessageId = originatingMessageId ?? parentMessageId,
            ParentMessageId = parentMessageId,
            MessageContent = new MessageContent
            {
                ErrorContent = new ErrorContent
                {
                    ErrorText = errorText,
                    ErrorType = errorType,
                },
            },
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
            DeferralSequence = deferredMessage.DeferralSequence,
            MessageContent = deferredMessage.MessageContent,
            DiagnosticId = deferredMessage.DiagnosticId,
        };
    }

    private static Message CreateSkipRequest(
        string sessionId,
        string eventId,
        string? parentMessageId,
        string? originatingMessageId,
        string endpoint)
    {
        return new Message
        {
            To = endpoint,
            From = Constants.ManagerId,
            SessionId = sessionId,
            CorrelationId = Guid.NewGuid().ToString(),
            MessageId = Guid.NewGuid().ToString(),
            EventId = eventId,
            EventTypeId = "OrderPlaced",
            MessageType = MessageType.SkipRequest,
            OriginatingMessageId = originatingMessageId ?? parentMessageId,
            ParentMessageId = parentMessageId,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = "OrderPlaced",
                    EventJson = "{}",
                },
            },
        };
    }

    /// <summary>
    /// Hands off on the first invocation, returns normally afterwards.
    /// Records every order id seen, plus a separate counter for handoff calls.
    /// </summary>
    private sealed class HandoffOnFirstHandler : IEventHandler<OrderPlaced>
    {
        public string HandoffReason { get; set; } = "test reason";
        public string? ExternalJobId { get; set; }
        public TimeSpan? ExpectedBy { get; set; }

        public List<string?> ReceivedOrderIds { get; } = new();
        public int HandleCalls { get; private set; }
        public int HandoffCallCount { get; private set; }

        public Task Handle(OrderPlaced message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            HandleCalls++;
            ReceivedOrderIds.Add(message.OrderId);

            if (HandoffCallCount == 0)
            {
                context.MarkPendingHandoff(HandoffReason, ExternalJobId, ExpectedBy);
                HandoffCallCount++;
            }

            return Task.CompletedTask;
        }
    }
}
