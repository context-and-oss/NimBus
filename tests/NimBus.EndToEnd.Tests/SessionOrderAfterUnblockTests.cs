#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.EndToEnd.Tests.Infrastructure;

namespace NimBus.EndToEnd.Tests;

/// <summary>
/// Session order across an unblock. The deferred processor republishes parked messages to the
/// back of the topic, so a new event can reach the endpoint before their replays; it must park
/// behind them rather than overtake them.
/// </summary>
[TestClass]
public class SessionOrderAfterUnblockTests
{
    private const string EndpointId = "OrderPlaced";

    [TestMethod]
    public async Task NewEvent_ArrivingBeforeReplayAfterSkip_ParksBehindParkedMessage()
    {
        const string sessionId = "session-overtake";
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionFactory = order => order.OrderId == "ORD-FAIL" ? new InvalidOperationException("fails") : null,
        };
        fixture.RegisterHandler(() => handler);

        // ORD-FAIL blocks the session and ORD-PARKED parks behind it.
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-FAIL" });
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-PARKED" });
        await fixture.DeliverAll();
        var failedEventId = fixture.ResponseBus.SentMessages
            .Single(message => message.MessageType == MessageType.ErrorResponse).EventId;

        // The operator skips ORD-FAIL: the session unblocks and a drain is requested.
        await fixture.PublishBus.Send(CreateSkipRequest(sessionId, failedEventId));
        await fixture.DeliverAll();
        Assert.AreEqual(1, CountDrainRequests(fixture));

        // ORD-NEW arrives before the drain's replay of ORD-PARKED.
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-NEW" });
        await fixture.DeliverAll();

        Assert.AreEqual(0, handler.ReceivedEvents.Count, "ORD-NEW must not run ahead of ORD-PARKED.");
        Assert.AreEqual(2, ParkedCopies(fixture).Count, "ORD-NEW should be parked behind ORD-PARKED.");
        Assert.IsTrue(fixture.ResponseBus.SentMessages.Any(message =>
            message.MessageType == MessageType.DeferralResponse && Contains(message, "ORD-NEW")),
            "ORD-NEW should be recorded as deferred.");
        Assert.AreEqual(2, CountDrainRequests(fixture),
            "The first event parked behind replays requests a drain of its own.");

        // The drain replays both parked messages in order.
        await ReplayParked(fixture, ParkedCopies(fixture));

        CollectionAssert.AreEqual(
            new[] { "ORD-PARKED", "ORD-NEW" },
            handler.ReceivedEvents.Select(order => order.OrderId).ToArray());

        // With every parked message replayed, the next event runs straight away.
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-LATER" });
        await fixture.DeliverAll();

        Assert.AreEqual("ORD-LATER", handler.ReceivedEvents.Last().OrderId);
        Assert.AreEqual(2, ParkedCopies(fixture).Count, "ORD-LATER must not be parked.");
        Assert.AreEqual(2, CountDrainRequests(fixture));
    }

    [TestMethod]
    public async Task EventParkedAfterTheRequestedDrain_IsDrainedWhenTheRequestingReplayArrives()
    {
        const string sessionId = "session-overtake-tail";
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionFactory = order => order.OrderId == "ORD-FAIL" ? new InvalidOperationException("fails") : null,
        };
        fixture.RegisterHandler(() => handler);

        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-FAIL" });
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-PARKED" });
        await fixture.DeliverAll();
        var failedEventId = fixture.ResponseBus.SentMessages
            .Single(message => message.MessageType == MessageType.ErrorResponse).EventId;
        await fixture.PublishBus.Send(CreateSkipRequest(sessionId, failedEventId));
        await fixture.DeliverAll();

        // Two events park behind ORD-PARKED; only the first requests a drain.
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-NEW-1" });
        await fixture.DeliverAll();
        var drainedCopies = ParkedCopies(fixture);
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-NEW-2" });
        await fixture.DeliverAll();
        Assert.AreEqual(2, CountDrainRequests(fixture), "ORD-NEW-2 relies on the drain ORD-NEW-1 requested.");

        // That drain finished before ORD-NEW-2 was parked: it replays ORD-PARKED and ORD-NEW-1 only.
        await ReplayParked(fixture, drainedCopies);

        Assert.AreEqual(3, CountDrainRequests(fixture),
            "The replay of ORD-NEW-1 must request a drain for ORD-NEW-2, parked after its drain.");
        CollectionAssert.AreEqual(
            new[] { "ORD-PARKED", "ORD-NEW-1" },
            handler.ReceivedEvents.Select(order => order.OrderId).ToArray());

        await ReplayParked(fixture, ParkedCopies(fixture).Skip(drainedCopies.Count).ToList());

        CollectionAssert.AreEqual(
            new[] { "ORD-PARKED", "ORD-NEW-1", "ORD-NEW-2" },
            handler.ReceivedEvents.Select(order => order.OrderId).ToArray());
        Assert.AreEqual(3, CountDrainRequests(fixture), "Nothing is left to drain.");
    }

    private static List<IMessage> ParkedCopies(EndToEndFixture fixture) =>
        fixture.ResponseBus.SentMessages
            .Where(message => message.To == Constants.DeferredSubscriptionName)
            .OrderBy(message => message.DeferralSequence)
            .ToList();

    private static int CountDrainRequests(EndToEndFixture fixture) =>
        fixture.ResponseBus.SentMessages.Count(message => message.MessageType == MessageType.ProcessDeferredRequest);

    private static bool Contains(IMessage message, string orderId) =>
        message.MessageContent.EventContent?.EventJson?.Contains(orderId, StringComparison.Ordinal) == true;

    // Replays the parked copies in order, the way DeferredMessageProcessor does: every
    // application property except OriginalSessionId and To is kept, with a new MessageId.
    private static async Task ReplayParked(EndToEndFixture fixture, IEnumerable<IMessage> parkedCopies)
    {
        foreach (var copy in parkedCopies)
        {
            var replay = JsonConvert.DeserializeObject<Message>(JsonConvert.SerializeObject(copy))!;
            replay.MessageId = Guid.NewGuid().ToString();
            replay.To = EndpointId;
            replay.OriginalSessionId = null!;
            await fixture.PublishBus.Send(replay);
            await fixture.DeliverAll();
        }
    }

    private static Message CreateSkipRequest(string sessionId, string blockedByEventId) => new()
    {
        To = EndpointId,
        SessionId = sessionId,
        CorrelationId = Guid.NewGuid().ToString(),
        MessageId = Guid.NewGuid().ToString(),
        EventId = blockedByEventId,
        EventTypeId = EndpointId,
        MessageType = MessageType.SkipRequest,
        From = Constants.ManagerId,
        OriginatingMessageId = Constants.Self,
        ParentMessageId = Constants.Self,
        MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = EndpointId, EventJson = "{}" },
        },
    };
}
