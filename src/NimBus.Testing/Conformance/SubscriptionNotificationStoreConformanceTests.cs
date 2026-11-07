#pragma warning disable CA1707, CA2007
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Provider-agnostic conformance suite for <see cref="ISubscriptionNotificationStore"/>: the
/// compare-and-set on a subscription's <c>NotifiedAt</c> that throttles endpoint alerts.
/// </summary>
[TestClass]
public abstract class SubscriptionNotificationStoreConformanceTests
{
    private const string First = "2026-11-05 10:00";
    private const string Second = "2026-11-05 11:00";

    private readonly string _scope = $"ct-{Guid.NewGuid():N}"[..16];

    /// <summary>Creates the provider's subscription store.</summary>
    protected abstract ISubscriptionStore CreateStore();

    /// <summary>Returns the notification store backed by the same storage as <paramref name="store"/>.</summary>
    protected abstract ISubscriptionNotificationStore NotificationStoreFor(ISubscriptionStore store);

    private string Id(string value) => $"{_scope}-{value}";

    private async Task<(ISubscriptionStore Store, ISubscriptionNotificationStore Notifications, EndpointSubscription Subscription)> SubscribeAsync(string endpoint)
    {
        var store = CreateStore();
        var subscription = await store.SubscribeToEndpointNotification(
            Id(endpoint),
            "ops@example.com",
            "mail",
            "alice",
            null!,
            new List<string> { "OrderPlaced" },
            null!,
            3600);
        return (store, NotificationStoreFor(store), subscription!);
    }

    private static async Task<EndpointSubscription> ReadAsync(ISubscriptionStore store, EndpointSubscription subscription)
        => (await store.GetSubscriptionsOnEndpoint(subscription.EndpointId)).Single(s => s.Id == subscription.Id);

    [TestMethod]
    public async Task TrySetNotifiedAt_from_never_notified_succeeds_once()
    {
        var (store, notifications, subscription) = await SubscribeAsync("ep-first");

        Assert.IsTrue(await notifications.TrySetNotifiedAtAsync(subscription.Id, null, First));
        Assert.IsFalse(await notifications.TrySetNotifiedAtAsync(subscription.Id, null, Second));

        Assert.AreEqual(First, (await ReadAsync(store, subscription)).NotifiedAt);
    }

    [TestMethod]
    public async Task TrySetNotifiedAt_with_a_stale_snapshot_fails()
    {
        var (store, notifications, subscription) = await SubscribeAsync("ep-stale");
        var snapshot = await ReadAsync(store, subscription);

        Assert.IsTrue(await notifications.TrySetNotifiedAtAsync(subscription.Id, snapshot.NotifiedAt, First));

        // The snapshot was read before the first set, so it must still carry the old value
        // and lose the race; a store that hands out live references would let it win.
        Assert.IsFalse(await notifications.TrySetNotifiedAtAsync(subscription.Id, snapshot.NotifiedAt, Second));
        Assert.AreEqual(First, (await ReadAsync(store, subscription)).NotifiedAt);
    }

    [TestMethod]
    public async Task TrySetNotifiedAt_with_the_current_value_succeeds()
    {
        var (store, notifications, subscription) = await SubscribeAsync("ep-current");

        Assert.IsTrue(await notifications.TrySetNotifiedAtAsync(subscription.Id, null, First));
        Assert.IsTrue(await notifications.TrySetNotifiedAtAsync(subscription.Id, First, Second));

        Assert.AreEqual(Second, (await ReadAsync(store, subscription)).NotifiedAt);
    }

    [TestMethod]
    public async Task TrySetNotifiedAt_back_to_null_releases_the_window()
    {
        var (store, notifications, subscription) = await SubscribeAsync("ep-release");

        Assert.IsTrue(await notifications.TrySetNotifiedAtAsync(subscription.Id, null, First));
        Assert.IsTrue(await notifications.TrySetNotifiedAtAsync(subscription.Id, First, null));
        Assert.IsNull((await ReadAsync(store, subscription)).NotifiedAt);

        Assert.IsTrue(await notifications.TrySetNotifiedAtAsync(subscription.Id, null, Second));
        Assert.AreEqual(Second, (await ReadAsync(store, subscription)).NotifiedAt);
    }

    [TestMethod]
    public async Task TrySetNotifiedAt_on_a_missing_subscription_returns_false()
    {
        var (_, notifications, _) = await SubscribeAsync("ep-missing");

        Assert.IsFalse(await notifications.TrySetNotifiedAtAsync(Id("no-such-subscription"), null, First));
    }

    [TestMethod]
    public async Task TrySetNotifiedAt_leaves_the_other_fields_unchanged()
    {
        var (store, notifications, subscription) = await SubscribeAsync("ep-fields");

        Assert.IsTrue(await notifications.TrySetNotifiedAtAsync(subscription.Id, null, First));

        var fetched = await ReadAsync(store, subscription);
        Assert.AreEqual("ops@example.com", fetched.Mail);
        Assert.AreEqual("mail", fetched.Type);
        Assert.AreEqual("alice", fetched.AuthorId);
        Assert.AreEqual(3600, fetched.Frequency);
        CollectionAssert.AreEqual(new List<string> { "OrderPlaced" }, fetched.EventTypes);
    }

    [TestMethod]
    public async Task Concurrent_TrySetNotifiedAt_from_the_same_value_lets_exactly_one_win()
    {
        var (store, notifications, subscription) = await SubscribeAsync("ep-race");
        var candidates = Enumerable.Range(0, 8).Select(i => $"2026-11-05 10:0{i}").ToArray();

        var results = await Task.WhenAll(candidates.Select(value =>
            Task.Run(() => notifications.TrySetNotifiedAtAsync(subscription.Id, null, value))));

        Assert.AreEqual(1, results.Count(won => won));
        var winner = candidates[System.Array.IndexOf(results, true)];
        Assert.AreEqual(winner, (await ReadAsync(store, subscription)).NotifiedAt);
    }
}
