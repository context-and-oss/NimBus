#pragma warning disable CA1707, CA2007
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace NimBus.Extensions.Notifications.Tests;

[TestClass]
public class NotificationRouterTests
{
    private static readonly DateTimeOffset Start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static ChannelRegistration Reg(FakeNotificationChannel channel, NotificationSeverity minSeverity) =>
        new(channel, new WebhookChannelOptions { MinSeverity = minSeverity });

    private static NotificationRouter NewRouter(
        IEnumerable<ChannelRegistration> registrations,
        NotificationRouterOptions? options = null,
        TimeProvider? timeProvider = null) =>
        new(registrations, options ?? new NotificationRouterOptions(),
            NullLogger<NotificationRouter>.Instance, timeProvider ?? TimeProvider.System);

    // ── AC 4: per-channel severity routing ───────────────────────────────

    [TestMethod]
    public async Task Route_DeliversOnlyToChannelsAtOrAboveMinSeverity()
    {
        var warnChannel = new FakeNotificationChannel();
        var critChannel = new FakeNotificationChannel();
        var router = NewRouter(
        [
            Reg(warnChannel, NotificationSeverity.Warning),
            Reg(critChannel, NotificationSeverity.Critical),
        ]);

        await router.RouteAsync(TestNotifications.Build(severity: NotificationSeverity.Error, eventId: "e-1"));

        Assert.AreEqual(1, warnChannel.Received.Count, "Warning channel should receive an Error notification.");
        Assert.AreEqual(0, critChannel.Received.Count, "Critical channel should filter out an Error notification.");

        await router.RouteAsync(TestNotifications.Build(severity: NotificationSeverity.Critical, eventId: "e-2"));

        Assert.AreEqual(2, warnChannel.Received.Count);
        Assert.AreEqual(1, critChannel.Received.Count);
    }

    // ── FR-031: short-circuit when no channel qualifies ──────────────────

    [TestMethod]
    public async Task Route_WhenAllChannelsGatedAbove_DeliversNothing()
    {
        var channel = new FakeNotificationChannel();
        var router = NewRouter([Reg(channel, NotificationSeverity.Critical)]);

        await router.RouteAsync(TestNotifications.Build(severity: NotificationSeverity.Warning));

        Assert.AreEqual(0, channel.Received.Count);
    }

    // ── AC 6: deduplication ──────────────────────────────────────────────

    [TestMethod]
    public async Task Route_DropsDuplicateWithinWindow_DeliversAfterWindowOrDifferentKey()
    {
        var channel = new FakeNotificationChannel();
        var time = new MutableTimeProvider(Start);
        var options = new NotificationRouterOptions { DedupWindow = TimeSpan.FromMinutes(5) };
        var router = NewRouter([Reg(channel, NotificationSeverity.Warning)], options, time);

        await router.RouteAsync(TestNotifications.Build(severity: NotificationSeverity.Critical, eventId: "dup"));
        await router.RouteAsync(TestNotifications.Build(severity: NotificationSeverity.Critical, eventId: "dup"));
        Assert.AreEqual(1, channel.Received.Count, "Second identical notification within the window is dropped.");

        time.Advance(TimeSpan.FromMinutes(6));
        await router.RouteAsync(TestNotifications.Build(severity: NotificationSeverity.Critical, eventId: "dup"));
        Assert.AreEqual(2, channel.Received.Count, "After the window the same key is delivered again.");

        await router.RouteAsync(TestNotifications.Build(severity: NotificationSeverity.Critical, eventId: "other"));
        Assert.AreEqual(3, channel.Received.Count, "A different key is always delivered.");
    }

    [TestMethod]
    public async Task Route_DedupKeyIncludesSeverity_EscalationStillDelivers()
    {
        var channel = new FakeNotificationChannel();
        var router = NewRouter([Reg(channel, NotificationSeverity.Warning)]);

        await router.RouteAsync(TestNotifications.Build(severity: NotificationSeverity.Error, eventId: "same"));
        await router.RouteAsync(TestNotifications.Build(severity: NotificationSeverity.Critical, eventId: "same"));

        Assert.AreEqual(2, channel.Received.Count, "Same event id but escalating severity is a distinct dedup key.");
    }

    // ── AC 5: rate limiting + storm summary ──────────────────────────────

    [TestMethod]
    public async Task Route_RateLimit_DeliversBurstThenSuppresses_AndEmitsSummary()
    {
        var channel = new FakeNotificationChannel();
        var time = new MutableTimeProvider(Start);
        var options = new NotificationRouterOptions { MaxPerMinute = 10, BurstCapacity = 20 };
        var router = NewRouter([Reg(channel, NotificationSeverity.Warning)], options, time);

        for (var i = 0; i < 100; i++)
        {
            await router.RouteAsync(TestNotifications.Build(severity: NotificationSeverity.Critical, eventId: $"e-{i}"));
        }

        Assert.AreEqual(20, channel.Received.Count, "Only the burst capacity is delivered immediately.");
        Assert.IsFalse(channel.Received.Any(n => n.Title.Contains("suppressed", StringComparison.Ordinal)));

        // Replenish tokens, then route one more — the accumulated storm is summarised once.
        time.Advance(TimeSpan.FromMinutes(1));
        await router.RouteAsync(TestNotifications.Build(severity: NotificationSeverity.Critical, eventId: "e-final"));

        var summaries = channel.Received.Where(n => n.Title.Contains("suppressed", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(1, summaries.Count, "Exactly one storm summary is emitted when tokens replenish.");
        StringAssert.Contains(summaries[0].Title, "80", StringComparison.Ordinal);
        Assert.IsTrue(channel.Received.Any(n => n.EventId == "e-final"), "The replenished notification is delivered.");
    }

    [TestMethod]
    public async Task Route_WithoutRateLimit_DeliversEveryDistinctNotification()
    {
        var channel = new FakeNotificationChannel();
        var router = NewRouter([Reg(channel, NotificationSeverity.Warning)]);

        for (var i = 0; i < 50; i++)
        {
            await router.RouteAsync(TestNotifications.Build(severity: NotificationSeverity.Critical, eventId: $"e-{i}"));
        }

        Assert.AreEqual(50, channel.Received.Count);
    }

    // ── NFR-001: a throwing channel never blocks siblings ────────────────

    [TestMethod]
    public async Task Route_ThrowingChannel_DoesNotPreventSiblingDelivery()
    {
        var bad = new FakeNotificationChannel { ThrowOnSend = true };
        var good = new FakeNotificationChannel();
        var router = NewRouter(
        [
            Reg(bad, NotificationSeverity.Warning),
            Reg(good, NotificationSeverity.Warning),
        ]);

        await router.RouteAsync(TestNotifications.Build(severity: NotificationSeverity.Critical));

        Assert.AreEqual(1, good.Received.Count);
    }
}
