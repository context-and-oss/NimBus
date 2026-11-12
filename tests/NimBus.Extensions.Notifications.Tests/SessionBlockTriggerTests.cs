#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.Core.Messages;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.Extensions.Notifications.Tests;

[TestClass]
public class SessionBlockTriggerTests
{
    // ── AC 7(c): the handler emits a session-block lifecycle signal ──────

    [TestMethod]
    public async Task MessageHandler_OnSessionBlockedException_NotifiesObserversWithBlockingEvent()
    {
        var observer = new RecordingSessionObserver();
        var notifier = new MessageLifecycleNotifier([observer]);
        var handler = new SessionBlockingHandler(notifier, blockedByEventId: "evt-blocking");
        var context = new FakeMessageContext { SessionId = "session-7", MessageType = MessageType.EventRequest };

        // Must not throw — the block is swallowed after the lifecycle signal is emitted.
        await handler.Handle(context);

        Assert.AreEqual(1, observer.SessionBlocks.Count);
        Assert.AreEqual("session-7", observer.SessionBlocks[0].SessionId);
        Assert.AreEqual("evt-blocking", observer.SessionBlocks[0].BlockedByEventId);
    }

    [TestMethod]
    public void SessionBlockedException_CarriesBlockingEventId()
    {
        var ex = new SessionBlockedException("blocked", "evt-99");
        Assert.AreEqual("evt-99", ex.BlockedByEventId);
    }

    // ── AC 7(c): the observer routes a Critical session-block notification ──

    [TestMethod]
    public async Task Observer_WhenEnabled_RoutesCriticalSessionBlockNotificationReferencingEvent()
    {
        var channel = new FakeNotificationChannel();
        var router = new NotificationRouter(
            [new ChannelRegistration(channel, new WebhookChannelOptions { MinSeverity = NotificationSeverity.Warning })],
            new NotificationRouterOptions(),
            NullLogger<NotificationRouter>.Instance);
        var observer = new NotificationLifecycleObserver(
            [channel], new NotificationOptions { NotifyOnSessionBlock = true }, router);

        await observer.OnSessionBlocked(
            new MessageLifecycleContext
            {
                SessionId = "session-7",
                MessageId = "m-1",
                EventTypeId = "OrderPlaced",
                EventId = "e-current",
                CorrelationId = "c-1",
            },
            blockedByEventId: "evt-blocking");

        Assert.AreEqual(1, channel.Received.Count);
        var notification = channel.Received.Single();
        Assert.AreEqual(NotificationSeverity.Critical, notification.Severity);
        Assert.AreEqual("evt-blocking", notification.EventId);
        StringAssert.Contains(notification.Message, "evt-blocking", StringComparison.Ordinal);
        StringAssert.Contains(notification.Message, "blocked", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Observer_WhenSessionBlockDisabled_DeliversNothing()
    {
        var channel = new FakeNotificationChannel();
        var router = new NotificationRouter(
            [new ChannelRegistration(channel, new WebhookChannelOptions { MinSeverity = NotificationSeverity.Warning })],
            new NotificationRouterOptions(),
            NullLogger<NotificationRouter>.Instance);
        var observer = new NotificationLifecycleObserver(
            [channel], new NotificationOptions { NotifyOnSessionBlock = false }, router);

        await observer.OnSessionBlocked(
            new MessageLifecycleContext { SessionId = "s", MessageId = "m" }, "evt-blocking");

        Assert.AreEqual(0, channel.Received.Count);
    }
}
