#pragma warning disable CA1707, CA2007
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Provider-agnostic conformance suite for <see cref="ISubscriptionStore"/>.
/// </summary>
[TestClass]
public abstract class SubscriptionStoreConformanceTests
{
    private readonly string _scope = $"ct-{Guid.NewGuid():N}"[..16];

    protected abstract ISubscriptionStore CreateStore();

    private string Id(string value) => $"{_scope}-{value}";

    [TestMethod]
    public async Task Subscribe_then_GetSubscriptionsOnEndpoint_round_trips()
    {
        var store = CreateStore();
        var endpointId = Id("ep-sub");

        var subscription = await store.SubscribeToEndpointNotification(
            endpointId: endpointId,
            mail: "ops@example.com",
            type: "mail",
            author: "alice",
            url: "https://example.test/hook",
            eventTypes: new List<string> { "OrderPlaced" },
            payload: "priority",
            frequency: 15);

        Assert.IsFalse(string.IsNullOrWhiteSpace(subscription!.Id));
        Assert.AreEqual(endpointId, subscription.EndpointId);
        Assert.AreEqual("ops@example.com", subscription.Mail);

        var subscriptions = (await store.GetSubscriptionsOnEndpoint(endpointId)).ToList();
        Assert.AreEqual(1, subscriptions.Count);
        Assert.AreEqual(subscription.Id, subscriptions[0].Id);
        CollectionAssert.AreEqual(new List<string> { "OrderPlaced" }, subscriptions[0].EventTypes);

        var otherEndpoint = (await store.GetSubscriptionsOnEndpoint(Id("ep-other"))).ToList();
        Assert.AreEqual(0, otherEndpoint.Count);
    }

    [TestMethod]
    public async Task UpdateSubscription_persists_changes()
    {
        var store = CreateStore();
        var endpointId = Id("ep-update");
        var subscription = await store.SubscribeToEndpointNotification(
            endpointId,
            "ops@example.com",
            "mail",
            "alice",
            "https://example.test/old",
            new List<string> { "OrderPlaced" },
            "old",
            15);

        subscription!.Mail = "support@example.com";
        subscription.Url = "https://example.test/new";
        subscription.EventTypes = new List<string> { "CustomerChanged" };
        subscription.Payload = "new";
        subscription.Frequency = 30;

        var updated = await store.UpdateSubscription(subscription);
        Assert.IsTrue(updated);

        var subscriptions = (await store.GetSubscriptionsOnEndpoint(endpointId)).ToList();
        Assert.AreEqual(1, subscriptions.Count);
        Assert.AreEqual("support@example.com", subscriptions[0].Mail);
        Assert.AreEqual("https://example.test/new", subscriptions[0].Url);
        CollectionAssert.AreEqual(new List<string> { "CustomerChanged" }, subscriptions[0].EventTypes);
        Assert.AreEqual("new", subscriptions[0].Payload);
        Assert.AreEqual(30, subscriptions[0].Frequency);
    }

    [TestMethod]
    public async Task UnsubscribeById_removes_only_matching_endpoint_subscription()
    {
        var store = CreateStore();
        var endpointId = Id("ep-unsub-id");
        var subscription = await store.SubscribeToEndpointNotification(
            endpointId,
            "ops@example.com",
            "mail",
            "alice",
            "https://example.test/hook",
            new List<string>(),
            string.Empty,
            15);

        var removed = await store.UnsubscribeById(endpointId, subscription!.Id);
        Assert.IsTrue(removed);

        var subscriptions = (await store.GetSubscriptionsOnEndpoint(endpointId)).ToList();
        Assert.AreEqual(0, subscriptions.Count);
    }

    [TestMethod]
    public async Task UnsubscribeByMail_removes_matching_subscription()
    {
        var store = CreateStore();
        var endpointId = Id("ep-unsub-mail");
        await store.SubscribeToEndpointNotification(
            endpointId,
            "ops@example.com",
            "mail",
            "alice",
            "https://example.test/hook",
            new List<string>(),
            string.Empty,
            15);

        var removed = await store.UnsubscribeByMail(endpointId, "ops@example.com");
        Assert.IsTrue(removed);

        var subscriptions = (await store.GetSubscriptionsOnEndpoint(endpointId)).ToList();
        Assert.AreEqual(0, subscriptions.Count);
    }

    [TestMethod]
    public async Task DeleteSubscription_removes_by_global_id()
    {
        var store = CreateStore();
        var endpointId = Id("ep-delete");
        var subscription = await store.SubscribeToEndpointNotification(
            endpointId,
            "ops@example.com",
            "mail",
            "alice",
            "https://example.test/hook",
            new List<string>(),
            string.Empty,
            15);

        var removed = await store.DeleteSubscription(subscription!.Id);
        Assert.IsTrue(removed);

        var subscriptions = (await store.GetSubscriptionsOnEndpoint(endpointId)).ToList();
        Assert.AreEqual(0, subscriptions.Count);
    }

    [TestMethod]
    public async Task Optional_subscription_fields_round_trip_null()
    {
        var store = CreateStore();
        var endpointId = Id("ep-sub-nulls");

        await store.SubscribeToEndpointNotification(
            endpointId: endpointId,
            mail: "ops@example.com",
            type: "mail",
            author: null!,
            url: null!,
            eventTypes: new List<string>(),
            payload: null!,
            frequency: 0);

        var fetched = (await store.GetSubscriptionsOnEndpoint(endpointId)).Single();

        Assert.IsNull(fetched.AuthorId);
        Assert.IsNull(fetched.Url);
        Assert.IsNull(fetched.Payload);
        Assert.IsNull(fetched.NotificationSeverity);
        Assert.IsNull(fetched.NotifiedAt);
        Assert.IsNull(fetched.ErrorList);
    }
}
