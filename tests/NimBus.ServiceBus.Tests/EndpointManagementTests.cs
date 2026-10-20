#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Management.ServiceBus;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public sealed class EndpointManagementTests
{
    private static readonly string[] ExpectedClearCalls =
    {
        "DeleteSubscription|Orders|Orders",
        "CreateSubscription|Orders|Orders",
        "DeleteRule|Orders|Orders|$Default",
        "CreateCustomRule|Orders|Orders|to-Orders|user.To = 'Orders'|<null>",
        "CreateCustomRule|Orders|Orders|continuation|user.To = 'Continuation'|SET user.To = 'Orders'; SET user.From = 'Continuation'",
        "CreateCustomRule|Orders|Orders|retry|user.To = 'Retry'|SET user.To = 'Orders'; SET user.From = 'Retry'",
    };

    [TestMethod]
    public async Task ClearEndpoint_rebuilds_subscription_and_rules_in_provisioner_order()
    {
        var management = new RecordingServiceBusManagement();
        var sut = new EndpointManagement(management);

        await sut.ClearEndpoint("Orders");

        CollectionAssert.AreEqual(ExpectedClearCalls, management.Calls);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public async Task ClearEndpoint_stops_after_first_management_failure(int failingCall)
    {
        var management = new RecordingServiceBusManagement { FailingCall = failingCall };
        var sut = new EndpointManagement(management);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => sut.ClearEndpoint("Orders"));

        Assert.AreEqual(failingCall + 1, management.Calls.Count);
    }

    private sealed class RecordingServiceBusManagement : IServiceBusManagement
    {
        public List<string> Calls { get; } = new();

        public int? FailingCall { get; init; }

        public Task CreateCustomRule(
            string topicName,
            string subscriptionName,
            string ruleName,
            string filter,
            string? action)
            => Record($"CreateCustomRule|{topicName}|{subscriptionName}|{ruleName}|{filter}|{action ?? "<null>"}");

        public Task CreateSubscription(string topicName, string subscriptionName)
            => Record($"CreateSubscription|{topicName}|{subscriptionName}");

        public Task DeleteRule(string topicName, string subscriptionName, string ruleName)
            => Record($"DeleteRule|{topicName}|{subscriptionName}|{ruleName}");

        public Task DeleteSubscription(string topicName, string subscriptionName)
            => Record($"DeleteSubscription|{topicName}|{subscriptionName}");

        // Topic deletion is WebApp admin surface; ClearEndpoint never removes the topic itself.
        public Task DeleteTopic(string topicName)
            => throw new NotSupportedException();

        public Task DisableSubscription(string topicName, string subscriptionName)
            => throw new NotSupportedException();

        public Task EnableSubscription(string topicName, string subscriptionName)
            => throw new NotSupportedException();

        public Task<bool> IsSubscriptionActive(string topicName, string subscriptionName)
            => throw new NotSupportedException();

        public Task<SubscriptionState> GetSubscriptionState(string topicName, string subscriptionName)
            => throw new NotSupportedException();

        public Task DisableTopicSend(string topicName)
            => throw new NotSupportedException();

        public Task EnableTopicSend(string topicName)
            => throw new NotSupportedException();

        public Task<TopicSendState> GetTopicSendState(string topicName)
            => throw new NotSupportedException();

        public Task UpdateForwardTo(string topicName, string subscriptionName, string forwardTo)
            => throw new NotSupportedException();

        // Read/inspect surface used by the WebApp's subscription admin, not by ClearEndpoint.
        public Task UpdateSubscription(
            string topicName, string subscriptionName, EntityStatus status, string? forwardTo, bool changeForwardTo)
            => throw new NotSupportedException();

        public Task<SubscriptionProperties?> GetSubscription(string topicName, string subscriptionName)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TopicProperties> ListTopicsAsync()
            => throw new NotSupportedException();

        public IAsyncEnumerable<SubscriptionProperties> ListSubscriptionsAsync(string topicName)
            => throw new NotSupportedException();

        public IAsyncEnumerable<RuleProperties> ListRulesAsync(string topicName, string subscriptionName)
            => throw new NotSupportedException();

        public IAsyncEnumerable<TopicRuntimeProperties> ListTopicRuntimePropertiesAsync()
            => throw new NotSupportedException();

        public IAsyncEnumerable<SubscriptionRuntimeProperties> ListSubscriptionRuntimePropertiesAsync(string topicName)
            => throw new NotSupportedException();

        private Task Record(string call)
        {
            Calls.Add(call);
            if (FailingCall == Calls.Count - 1)
                throw new InvalidOperationException("Simulated management failure.");

            return Task.CompletedTask;
        }
    }
}
