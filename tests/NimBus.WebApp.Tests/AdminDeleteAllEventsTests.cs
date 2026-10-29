#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore;
using NimBus.Testing.Conformance;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using CoreConstants = NimBus.Core.Messages.Constants;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Operations → Delete all events wipes an endpoint: its own and Deferred subscriptions are
/// rebuilt empty, and both its stored events and its message history are deleted.
/// </summary>
[TestClass]
public sealed class AdminDeleteAllEventsTests
{
    private const string EndpointId = "endpoint-a";
    private const string OtherEndpointId = "endpoint-b";

    [TestMethod]
    public async Task Delete_all_recreates_both_subscriptions_and_wipes_events_and_message_history()
    {
        var store = new InMemoryMessageStore();
        await store.UploadPendingMessage("evt-1", "s1", EndpointId, Event(EndpointId, "evt-1"));
        await store.UploadFailedMessage("evt-2", "s1", EndpointId, Event(EndpointId, "evt-2"));
        await store.UploadPendingMessage("evt-3", "s1", OtherEndpointId, Event(OtherEndpointId, "evt-3"));
        await store.StoreMessage(Message("evt-1", "msg-1", EndpointId));
        await store.StoreMessage(Message("evt-2", "msg-2", EndpointId));
        await store.StoreMessage(Message("evt-3", "msg-3", OtherEndpointId));
        var subscriptions = new RecordingSubscriptionAdminService();

        var result = await CreateAdminService(store, subscriptions).DeleteAllEventsAsync(EndpointId);

        CollectionAssert.AreEqual(
            new[] { $"{EndpointId}/{EndpointId}", $"{EndpointId}/{CoreConstants.DeferredSubscriptionName}" },
            subscriptions.Recreated);
        Assert.AreEqual(4, result.Processed);
        Assert.AreEqual(4, result.Succeeded);
        Assert.AreEqual(0, result.Failed);
        Assert.AreEqual(0, result.Errors.Count);

        Assert.IsNull(await store.GetEvent(EndpointId, "evt-1"));
        Assert.IsNull(await store.GetEvent(EndpointId, "evt-2"));
        Assert.IsFalse((await store.GetEventHistory("evt-1")).Any());
        Assert.IsFalse((await store.GetEventHistory("evt-2")).Any());

        // Another endpoint's data is untouched.
        Assert.IsNotNull(await store.GetEvent(OtherEndpointId, "evt-3"));
        Assert.AreEqual(1, (await store.GetEventHistory("evt-3")).Count());
    }

    [TestMethod]
    public async Task A_failed_subscription_rebuild_is_reported_and_storage_is_still_wiped()
    {
        var store = new InMemoryMessageStore();
        await store.UploadPendingMessage("evt-1", "s1", EndpointId, Event(EndpointId, "evt-1"));
        await store.StoreMessage(Message("evt-1", "msg-1", EndpointId));
        var subscriptions = new RecordingSubscriptionAdminService
        {
            FailOn = CoreConstants.DeferredSubscriptionName,
        };

        var result = await CreateAdminService(store, subscriptions).DeleteAllEventsAsync(EndpointId);

        Assert.AreEqual(4, result.Processed);
        Assert.AreEqual(3, result.Succeeded);
        Assert.AreEqual(1, result.Failed);
        Assert.IsTrue(
            result.Errors.Any(error => error.Contains($"{EndpointId}/{CoreConstants.DeferredSubscriptionName}", StringComparison.Ordinal)),
            "The error names the subscription that could not be rebuilt.");
        Assert.IsNull(await store.GetEvent(EndpointId, "evt-1"));
        Assert.IsFalse((await store.GetEventHistory("evt-1")).Any());
    }

    private static UnresolvedEvent Event(string endpointId, string eventId) => new()
    {
        EventId = eventId,
        SessionId = "s1",
        EndpointId = endpointId,
        EventTypeId = "OrderPlaced",
        UpdatedAt = DateTime.UtcNow,
        EnqueuedTimeUtc = DateTime.UtcNow,
    };

    private static MessageEntity Message(string eventId, string messageId, string endpointId) => new()
    {
        EventId = eventId,
        MessageId = messageId,
        EndpointId = endpointId,
        EnqueuedTimeUtc = DateTime.UtcNow,
        MessageContent = new NimBus.Core.Messages.MessageContent(),
    };

    private static AdminService CreateAdminService(InMemoryMessageStore store, ISubscriptionAdminService subscriptions) =>
        new(
            platform: null!,
            messageStore: store,
            capabilities: null!,
            sbAdmin: null!,
            sbClient: null!,
            managerClient: null!,
            subscriptionAdmin: subscriptions,
            logger: NullLogger<AdminService>.Instance,
            rawCosmosClient: null);

    private sealed class RecordingSubscriptionAdminService : ISubscriptionAdminService
    {
        public List<string> Recreated { get; } = new();

        public string? FailOn { get; init; }

        public Task<SubscriptionActionResult> RecreateSubscriptionAsync(string topicName, string subscriptionName)
        {
            Recreated.Add($"{topicName}/{subscriptionName}");
            var failed = string.Equals(subscriptionName, FailOn, StringComparison.Ordinal);
            return Task.FromResult(new SubscriptionActionResult
            {
                TopicName = topicName,
                SubscriptionName = subscriptionName,
                Action = "recreate",
                Succeeded = !failed,
                Message = failed ? "Subscription was deleted but could not be recreated." : "Recreated.",
                Errors = new List<string>(),
            });
        }

        public Task<IEnumerable<ServiceBusTopicOverview>> GetTopicOverviewAsync() => throw Unexpected();
        public Task<SubscriptionActionResult> DeleteTopicAsync(string topicName) => throw Unexpected();
        public Task<IEnumerable<ServiceBusSubscriptionInfo>> GetSubscriptionsAsync(string topicName) => throw Unexpected();
        public Task<DeadLetterOverview> GetResolverDeadLettersAsync(string subscriptionName, CancellationToken cancellationToken = default) => throw Unexpected();
        public Task<BulkOperationResult> ResubmitResolverDeadLettersAsync(string subscriptionName, bool all, string? reason, CancellationToken cancellationToken = default) => throw Unexpected();
        public Task<SubscriptionActionResult> SetSubscriptionStatusAsync(string topicName, string subscriptionName, bool enable) => throw Unexpected();
        public Task<BulkOperationResult> PurgeSubscriptionAsync(string topicName, string subscriptionName) => throw Unexpected();
        public Task<BulkOperationResult> PurgeDeadLetterQueueAsync(string topicName, string subscriptionName) => throw Unexpected();
        public Task<SubscriptionActionResult> DeleteSubscriptionAsync(string topicName, string subscriptionName) => throw Unexpected();
        public Task<SubscriptionActionResult> DeleteRuleAsync(string topicName, string subscriptionName, string ruleName) => throw Unexpected();
        public Task<SubscriptionActionResult> RestoreRulesAsync(string topicName, string subscriptionName) => throw Unexpected();

        private static Exception Unexpected() => new AssertFailedException("Unexpected subscription admin call.");
    }
}
