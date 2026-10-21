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

    [TestMethod]
    public async Task DrainRequestWhoseReplayNeverArrives_IsRequestedAgainAfterItExpires()
    {
        const string sessionId = "session-overtake-lost-drain";
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

        // ORD-NEW-1 requests a drain, but that drain never runs (its trigger is dead-lettered,
        // say), so the replay that would clear the request never arrives.
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-NEW-1" });
        await fixture.DeliverAll();
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-NEW-2" });
        await fixture.DeliverAll();
        Assert.AreEqual(2, CountDrainRequests(fixture), "A fresh request covers ORD-NEW-2.");

        // Once the request is older than the timeout, the next parked event requests a drain again.
        var session = fixture.PublishBus.GetSession(sessionId);
        session.State.DrainRequestedAtUtc = DateTime.UtcNow - TimeSpan.FromMinutes(5);
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-NEW-3" });
        await fixture.DeliverAll();

        Assert.AreEqual(3, CountDrainRequests(fixture), "An expired request must not keep the session parked.");
        Assert.AreEqual(ParkedCopies(fixture).Last().DeferralSequence, session.State.DrainRequestedForSequence,
            "The new request belongs to ORD-NEW-3.");

        await ReplayParked(fixture, ParkedCopies(fixture));

        CollectionAssert.AreEqual(
            new[] { "ORD-PARKED", "ORD-NEW-1", "ORD-NEW-2", "ORD-NEW-3" },
            handler.ReceivedEvents.Select(order => order.OrderId).ToArray());
        Assert.IsNull(session.State.DrainRequestedForSequence);
    }

    [TestMethod]
    public async Task ReplayFromAnEarlierVersionDrain_GoesThroughTheGuardInsteadOfParkingAgain()
    {
        // During a rolling deploy, a drain from the previous version strips DeferralSequence
        // but keeps the InboxMessageId every parked copy carries.
        const string sessionId = "session-overtake-legacy-drain";
        var (fixture, handler) = await ParkBehindASkippedFailure(sessionId);
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-NEW" });
        await fixture.DeliverAll();
        var parked = ParkedCopies(fixture);
        Assert.IsTrue(parked.All(copy => copy.InboxMessageId is not null), "Every parked copy carries its InboxMessageId.");

        await ReplayParked(fixture, parked, stripDeferralSequence: true);

        CollectionAssert.AreEqual(
            new[] { "ORD-PARKED", "ORD-NEW" },
            handler.ReceivedEvents.Select(order => order.OrderId).ToArray());
        Assert.AreEqual(parked.Count, ParkedCopies(fixture).Count, "A legacy replay must not be parked again.");
    }

    [TestMethod]
    public async Task NewEvent_ArrivingBeforeReprocessReplays_ParksBehindThem()
    {
        // The WebApp's "reprocess deferred" clears the block and requests a drain. It must keep
        // the replay position, or a new event overtakes the parked ones as after any unblock.
        const string sessionId = "session-overtake-reprocess";
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionFactory = order => order.OrderId == "ORD-FAIL" ? new InvalidOperationException("fails") : null,
        };
        fixture.RegisterHandler(() => handler);
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-FAIL" });
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-PARKED" });
        await fixture.DeliverAll();

        var session = fixture.PublishBus.GetSession(sessionId);
        session.State.ClearForReprocess();
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-NEW" });
        await fixture.DeliverAll();

        Assert.AreEqual(0, handler.ReceivedEvents.Count, "ORD-NEW must not run ahead of ORD-PARKED.");
        await ReplayParked(fixture, ParkedCopies(fixture));
        CollectionAssert.AreEqual(
            new[] { "ORD-PARKED", "ORD-NEW" },
            handler.ReceivedEvents.Select(order => order.OrderId).ToArray());
    }

    [TestMethod]
    public async Task NewEvent_AfterPurge_RunsStraightAway()
    {
        const string sessionId = "session-overtake-purge";
        var (fixture, handler) = await ParkBehindASkippedFailure(sessionId);
        var session = fixture.PublishBus.GetSession(sessionId);

        // The purge removed the parked copies, so nothing is left to wait for.
        session.State.ClearAfterPurge();
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-NEW" });
        await fixture.DeliverAll();

        CollectionAssert.AreEqual(new[] { "ORD-NEW" }, handler.ReceivedEvents.Select(order => order.OrderId).ToArray());
        Assert.AreEqual(1, ParkedCopies(fixture).Count, "ORD-NEW must not be parked.");
    }

    [TestMethod]
    public async Task ReplayThatBlocksTheSessionAgain_KeepsLaterEventsInOrder()
    {
        const string sessionId = "session-overtake-reblock";
        var failing = new HashSet<string>(StringComparer.Ordinal) { "ORD-FAIL", "ORD-PARKED-1" };
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionFactory = order => order.OrderId is { } id && failing.Contains(id) ? new InvalidOperationException("fails") : null,
        };
        fixture.RegisterHandler(() => handler);
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-FAIL" });
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-PARKED-1" });
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-PARKED-2" });
        await fixture.DeliverAll();
        await fixture.PublishBus.Send(CreateSkipRequest(sessionId, ErrorEventId(fixture, "ORD-FAIL")));
        await fixture.DeliverAll();

        // The drain replays both: ORD-PARKED-1 fails and blocks the session again, so the
        // replay of ORD-PARKED-2 parks again behind it.
        await ReplayParked(fixture, ParkedCopies(fixture));
        Assert.AreEqual(0, handler.ReceivedEvents.Count);
        Assert.AreEqual(3, ParkedCopies(fixture).Count, "ORD-PARKED-2 should be parked again.");

        // The operator skips ORD-PARKED-1. A new event arrives before the replay.
        failing.Clear();
        await fixture.PublishBus.Send(CreateSkipRequest(sessionId, ErrorEventId(fixture, "ORD-PARKED-1")));
        await fixture.DeliverAll();
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-NEW" });
        await fixture.DeliverAll();
        Assert.AreEqual(0, handler.ReceivedEvents.Count, "ORD-NEW must not run ahead of ORD-PARKED-2.");

        await ReplayParked(fixture, ParkedCopies(fixture).Skip(2).ToList());

        CollectionAssert.AreEqual(
            new[] { "ORD-PARKED-2", "ORD-NEW" },
            handler.ReceivedEvents.Select(order => order.OrderId).ToArray());
    }

    // ORD-FAIL blocks the session, ORD-PARKED parks behind it, and the operator skips ORD-FAIL.
    private static async Task<(EndToEndFixture Fixture, RecordingOrderPlacedHandler Handler)> ParkBehindASkippedFailure(string sessionId)
    {
        var fixture = new EndToEndFixture();
        var handler = new RecordingOrderPlacedHandler
        {
            ExceptionFactory = order => order.OrderId == "ORD-FAIL" ? new InvalidOperationException("fails") : null,
        };
        fixture.RegisterHandler(() => handler);
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-FAIL" });
        await fixture.Publisher.Publish(new OrderPlaced(sessionId) { OrderId = "ORD-PARKED" });
        await fixture.DeliverAll();
        await fixture.PublishBus.Send(CreateSkipRequest(sessionId, ErrorEventId(fixture, "ORD-FAIL")));
        await fixture.DeliverAll();
        return (fixture, handler);
    }

    private static string ErrorEventId(EndToEndFixture fixture, string orderId) =>
        fixture.ResponseBus.SentMessages
            .Last(message => message.MessageType == MessageType.ErrorResponse && Contains(message, orderId))
            .EventId;

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
    // stripDeferralSequence replays the way the previous version's drain did.
    private static async Task ReplayParked(EndToEndFixture fixture, IEnumerable<IMessage> parkedCopies, bool stripDeferralSequence = false)
    {
        foreach (var copy in parkedCopies)
        {
            var replay = JsonConvert.DeserializeObject<Message>(JsonConvert.SerializeObject(copy))!;
            replay.MessageId = Guid.NewGuid().ToString();
            replay.To = EndpointId;
            replay.OriginalSessionId = null!;
            if (stripDeferralSequence)
                replay.DeferralSequence = null;
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
