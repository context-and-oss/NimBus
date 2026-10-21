#pragma warning disable CA1707, CA2007
using Azure.Messaging.ServiceBus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.EndToEnd.Tests.Infrastructure;

namespace NimBus.EndToEnd.Tests;

/// <summary>
/// Settlement when a delivery does not end cleanly. The receiver stops its processor when the
/// endpoint circuit opens, which cancels the token of every in-flight delivery. These tests
/// drive the real Service Bus message context over a fake session that, like the Service Bus
/// SDK, fails any call made with a cancelled token.
/// </summary>
[TestClass]
public sealed class ProcessorStopSettlementTests
{
    private static readonly MessageType[] ErrorThenRetry = [MessageType.ErrorResponse, MessageType.RetryRequest];

    [TestMethod]
    public async Task Failed_event_is_completed_and_retried_when_the_circuit_opens_after_its_session_is_blocked()
    {
        // The DynamicsBcDemo incident: the ErrorResponse was sent and the session blocked, then
        // other sessions' failures opened the circuit and the processor stop cancelled the
        // completion. No RetryRequest was scheduled, and the redelivered event deferred behind
        // its own block.
        var retryProvider = new DefaultRetryPolicyProvider();
        retryProvider.AddEventTypePolicy("OrderPlaced", new RetryPolicy
        {
            MaxRetries = 3,
            BaseDelay = TimeSpan.FromMinutes(1),
        });
        var fixture = new EndToEndFixture(retryProvider);
        fixture.RegisterHandler(() => new RecordingOrderPlacedHandler
        {
            ExceptionToThrow = new InvalidOperationException("503 Service Unavailable"),
        });
        using var processorStop = new CancellationTokenSource();
        var session = fixture.PublishBus.GetSession("circuit-session");
        session.OnStateWritten = processorStop.Cancel;

        await fixture.Publisher.Publish(new OrderPlaced("circuit-session") { OrderId = "ORD-CIRCUIT" });
        var delivery = (await fixture.DeliverAllWithResults(processorStop.Token)).Single();

        Assert.IsTrue(processorStop.IsCancellationRequested, "The processor stop must arrive mid-settlement.");
        Assert.IsNull(delivery.Exception);
        Assert.AreEqual(1, session.CompletedCount, "The failed delivery must be completed.");
        Assert.AreEqual(delivery.Context.EventId, session.State.BlockedByEventId);
        CollectionAssert.AreEqual(
            ErrorThenRetry,
            fixture.ResponseBus.SentMessages.Select(message => message.MessageType).ToArray());
    }

    [TestMethod]
    public async Task Redelivery_into_a_session_blocked_by_its_own_event_is_completed_without_running_again()
    {
        // The first delivery fails, sends its ErrorResponse and blocks the session, then loses
        // its session lock before completing, so the broker delivers the same message again.
        var fixture = new EndToEndFixture();
        var attempts = 0;
        fixture.RegisterHandler(() => new RecordingOrderPlacedHandler
        {
            ExceptionFactory = _ =>
            {
                attempts++;
                return new InvalidOperationException("503 Service Unavailable");
            },
        });
        var session = fixture.PublishBus.GetSession("self-blocked-session");
        session.NextCompleteException = new ServiceBusException(
            "The session lock was lost.",
            ServiceBusFailureReason.SessionLockLost);

        await fixture.Publisher.Publish(new OrderPlaced("self-blocked-session") { OrderId = "ORD-SELF" });
        var first = (await fixture.DeliverAllWithResults()).Single();
        Assert.AreEqual(0, session.CompletedCount, "The lost lock leaves the first delivery unsettled.");

        var redelivery = Clone(first.OriginalMessage);
        redelivery.EventId = first.Context.EventId;
        await fixture.PublishBus.Send(redelivery);
        var second = (await fixture.DeliverAllWithResults()).Single();

        Assert.IsNull(second.Exception);
        Assert.AreEqual(1, attempts, "The handler must not run again.");
        Assert.AreEqual(1, session.CompletedCount, "The redelivery must be completed.");
        Assert.AreEqual(first.Context.EventId, session.State.BlockedByEventId, "The event keeps its own block.");
        Assert.AreEqual(0, session.State.DeferredCount, "Nothing may be parked behind the event's own block.");
        Assert.AreEqual(
            MessageType.ErrorResponse,
            fixture.ResponseBus.SentMessages.Single().MessageType,
            "Only the first delivery's ErrorResponse: no deferral and no parked copy.");
    }

    [TestMethod]
    public async Task Skip_redelivered_after_it_unblocked_the_session_still_requests_the_drain()
    {
        // The first delivery of the skip clears the block, then fails before it requests the
        // drain. Its redelivery no longer finds the session blocked by the skipped event; it must
        // still request the drain, or the parked event stays parked.
        var fixture = new EndToEndFixture();
        fixture.RegisterHandler(() => new RecordingOrderPlacedHandler
        {
            ExceptionFactory = order => order.OrderId == "ORD-FAIL" ? new InvalidOperationException("fails") : null,
        });
        await fixture.Publisher.Publish(new OrderPlaced("skip-session") { OrderId = "ORD-FAIL" });
        await fixture.Publisher.Publish(new OrderPlaced("skip-session") { OrderId = "ORD-PARKED" });
        await fixture.DeliverAll();
        var failedEventId = fixture.ResponseBus.SentMessages.Single(message => message.MessageType == MessageType.ErrorResponse).EventId;
        var session = fixture.PublishBus.GetSession("skip-session");
        session.OnStateWritten = () =>
        {
            if (session.State.BlockedByEventId is null)
            {
                session.OnStateWritten = null;
                throw new ServiceBusException("The connection was lost.", ServiceBusFailureReason.ServiceCommunicationProblem);
            }
        };

        var skip = CreateSkipRequest("skip-session", failedEventId);
        await fixture.PublishBus.Send(skip);
        await fixture.DeliverAllWithResults();
        Assert.IsNull(session.State.BlockedByEventId, "The first delivery cleared the block.");
        Assert.IsFalse(fixture.ResponseBus.SentMessages.Any(message => message.MessageType == MessageType.ProcessDeferredRequest),
            "The first delivery failed before requesting the drain.");

        await fixture.PublishBus.Send(Clone(skip));
        await fixture.DeliverAllWithResults();

        Assert.AreEqual(1, fixture.ResponseBus.SentMessages.Count(message => message.MessageType == MessageType.ProcessDeferredRequest),
            "The redelivered skip must request the drain.");
        Assert.AreEqual(1, fixture.ResponseBus.SentMessages.Count(message => message.MessageType == MessageType.SkipResponse));
    }

    private static Message CreateSkipRequest(string sessionId, string eventId) => new()
    {
        To = "OrderPlaced",
        SessionId = sessionId,
        CorrelationId = Guid.NewGuid().ToString(),
        MessageId = Guid.NewGuid().ToString(),
        EventId = eventId,
        EventTypeId = "OrderPlaced",
        MessageType = MessageType.SkipRequest,
        From = Constants.ManagerId,
        OriginatingMessageId = Constants.Self,
        ParentMessageId = Constants.Self,
        MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" },
        },
    };

    private static Message Clone(IMessage message) =>
        JsonConvert.DeserializeObject<Message>(JsonConvert.SerializeObject(message))!;
}
