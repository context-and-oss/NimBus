#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using NimBus.Management.ServiceBus;
using NimBus.ServiceBus.Provisioning;

namespace NimBus.WebApp.Tests;

/// <summary>
/// In-memory <see cref="IServiceBusManagement"/> recording the management calls
/// <c>SubscriptionAdminService</c> makes, so its pause / purge / recreate decisions can be
/// asserted without a namespace. The repo has no mocking library, so this is hand-written.
/// </summary>
internal sealed class FakeServiceBusManagement : IServiceBusManagement
{
    private readonly Dictionary<string, Dictionary<string, SubscriptionProperties>> _subscriptions =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Topic, string Subscription), List<RuleProperties>> _rules = new();
    private readonly Dictionary<string, SubscriptionRuntimeProperties> _runtime = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<TopicProperties> _topics = new();
    private readonly List<TopicRuntimeProperties> _topicRuntime = new();

    /// <summary>Every (status, forwardTo, changeForwardTo) update, in order.</summary>
    public List<(string Topic, string Subscription, EntityStatus Status, string? ForwardTo, bool ChangeForwardTo)> Updates { get; } = new();

    public List<(string Topic, string Subscription)> DeletedSubscriptions { get; } = new();
    public List<string> DeletedTopics { get; } = new();
    public List<(string Topic, string Subscription, string Rule)> DeletedRules { get; } = new();
    public List<(string Topic, string Subscription, string Rule, string Filter, string? Action)> CreatedRules { get; } = new();

    /// <summary>Set to throw from the next <see cref="UpdateSubscription"/> whose status matches.</summary>
    public EntityStatus? FailUpdateToStatus { get; set; }

    public void SeedSubscription(
        string topicName,
        string subscriptionName,
        bool requiresSession = false,
        string? forwardTo = null,
        // EntityStatus is a struct, not an enum, so it can't be a default parameter value.
        EntityStatus? status = null,
        params (string Name, string Filter, string? Action)[] rules)
    {
        if (!_subscriptions.TryGetValue(topicName, out var byName))
        {
            byName = new Dictionary<string, SubscriptionProperties>(StringComparer.OrdinalIgnoreCase);
            _subscriptions[topicName] = byName;
        }

        byName[subscriptionName] = ServiceBusModelFactory.SubscriptionProperties(
            topicName, subscriptionName,
            lockDuration: TimeSpan.FromSeconds(30),
            requiresSession: requiresSession,
            defaultMessageTimeToLive: TimeSpan.MaxValue,
            autoDeleteOnIdle: TimeSpan.MaxValue,
            deadLetteringOnMessageExpiration: false,
            maxDeliveryCount: 10,
            enableBatchedOperations: true,
            status: status ?? EntityStatus.Active,
            forwardTo: forwardTo,
            forwardDeadLetteredMessagesTo: string.Empty,
            userMetadata: string.Empty);

        _rules[(topicName, subscriptionName)] = rules
            .Select(rule => ServiceBusModelFactory.RuleProperties(
                rule.Name,
                new SqlRuleFilter(rule.Filter),
                string.IsNullOrEmpty(rule.Action) ? null : new SqlRuleAction(rule.Action)))
            .ToList();
    }

    public Task DeleteTopic(string topicName)
    {
        DeletedTopics.Add(topicName);
        return Task.CompletedTask;
    }

    public void SeedTopic(string topicName, long active = 0, long deadLetter = 0, long transfer = 0, long transferDeadLetter = 0)
    {
        _topics.Add(ServiceBusModelFactory.TopicProperties(
            topicName,
            defaultMessageTimeToLive: TimeSpan.FromDays(1),
            autoDeleteOnIdle: TimeSpan.MaxValue,
            duplicateDetectionHistoryTimeWindow: TimeSpan.FromMinutes(10),
            status: EntityStatus.Active));
        _topicRuntime.Add(ServiceBusModelFactory.TopicRuntimeProperties(topicName, subscriptionCount: 1));
        _runtime[topicName] = ServiceBusModelFactory.SubscriptionRuntimeProperties(
            topicName, "any",
            activeMessageCount: active,
            deadLetterMessageCount: deadLetter,
            transferMessageCount: transfer,
            transferDeadLetterMessageCount: transferDeadLetter);
    }

    // ───────────────────────── Reads ─────────────────────────

    public Task<SubscriptionProperties?> GetSubscription(string topicName, string subscriptionName) =>
        Task.FromResult(
            _subscriptions.TryGetValue(topicName, out var byName) && byName.TryGetValue(subscriptionName, out var properties)
                ? properties
                : null);

    public async IAsyncEnumerable<TopicProperties> ListTopicsAsync()
    {
        foreach (var topic in _topics) yield return topic;
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<TopicRuntimeProperties> ListTopicRuntimePropertiesAsync()
    {
        foreach (var topic in _topicRuntime) yield return topic;
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<SubscriptionProperties> ListSubscriptionsAsync(string topicName)
    {
        if (_subscriptions.TryGetValue(topicName, out var byName))
        {
            foreach (var properties in byName.Values) yield return properties;
        }
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<SubscriptionRuntimeProperties> ListSubscriptionRuntimePropertiesAsync(string topicName)
    {
        if (_runtime.TryGetValue(topicName, out var runtime)) yield return runtime;
        await Task.CompletedTask;
    }

    public async IAsyncEnumerable<RuleProperties> ListRulesAsync(string topicName, string subscriptionName)
    {
        if (_rules.TryGetValue((topicName, subscriptionName), out var rules))
        {
            foreach (var rule in rules.ToList()) yield return rule;
        }
        await Task.CompletedTask;
    }

    // ───────────────────────── Writes ─────────────────────────

    public Task UpdateSubscription(
        string topicName, string subscriptionName, EntityStatus status, string? forwardTo, bool changeForwardTo)
    {
        Updates.Add((topicName, subscriptionName, status, forwardTo, changeForwardTo));

        if (FailUpdateToStatus == status)
        {
            throw new InvalidOperationException($"Simulated failure updating to {status}.");
        }

        return Task.CompletedTask;
    }

    public Task DeleteSubscription(string topicName, string subscriptionName)
    {
        DeletedSubscriptions.Add((topicName, subscriptionName));
        if (_subscriptions.TryGetValue(topicName, out var byName)) byName.Remove(subscriptionName);
        return Task.CompletedTask;
    }

    public Task DeleteRule(string topicName, string subscriptionName, string ruleName)
    {
        DeletedRules.Add((topicName, subscriptionName, ruleName));
        if (_rules.TryGetValue((topicName, subscriptionName), out var rules))
        {
            rules.RemoveAll(rule => rule.Name.Equals(ruleName, StringComparison.OrdinalIgnoreCase));
        }
        return Task.CompletedTask;
    }

    public Task CreateCustomRule(string topicName, string subscriptionName, string ruleName, string filter, string? action)
    {
        CreatedRules.Add((topicName, subscriptionName, ruleName, filter, action));
        return Task.CompletedTask;
    }

    // ───────────────────────── Not exercised here ─────────────────────────

    public Task CreateSubscription(string topicName, string subscriptionName) => Task.CompletedTask;
    public Task DisableSubscription(string topicName, string subscriptionName) => Task.CompletedTask;
    public Task EnableSubscription(string topicName, string subscriptionName) => Task.CompletedTask;
    public Task<bool> IsSubscriptionActive(string topicName, string subscriptionName) => Task.FromResult(true);
    public Task<SubscriptionState> GetSubscriptionState(string topicName, string subscriptionName) =>
        Task.FromResult(SubscriptionState.Active);
    public Task DisableTopicSend(string topicName) => Task.CompletedTask;
    public Task EnableTopicSend(string topicName) => Task.CompletedTask;
    public Task<TopicSendState> GetTopicSendState(string topicName) => Task.FromResult(TopicSendState.Enabled);
    public Task UpdateForwardTo(string topicName, string subscriptionName, string forwardTo) => Task.CompletedTask;
}

/// <summary>Records rebuild requests; optionally fails to model a delete that couldn't be undone.</summary>
internal sealed class FakeTopologyRebuilder : ITopologyRebuilder
{
    public List<(string Topic, ExpectedSubscription Expected)> Rebuilt { get; } = new();

    public bool Fail { get; set; }

    public Task EnsureSubscriptionAsync(
        string topicName, ExpectedSubscription expected, CancellationToken cancellationToken = default)
    {
        Rebuilt.Add((topicName, expected));
        return Fail
            ? Task.FromException(new InvalidOperationException("Simulated rebuild failure."))
            : Task.CompletedTask;
    }
}
