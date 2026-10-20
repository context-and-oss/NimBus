using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using NimBus.Core.Messages;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace NimBus.Management.ServiceBus;

public enum SubscriptionState
{
    Active,
    Disabled,
    NotFound,
}

public enum TopicSendState
{
    Enabled,
    SendDisabled,
    NotFound,
}
/// <summary>
/// Runtime management operations against an existing Service Bus topology
/// (status toggles, forward-to updates, and the subscription/rule rebuild used
/// by ClearEndpoint). Topology <em>provisioning</em> lives exclusively in
/// <c>NimBus.ServiceBus.Provisioning.ServiceBusTopologyProvisioner</c> — do not
/// add entity-creation methods here that duplicate it.
/// </summary>
public interface IServiceBusManagement
{
    Task CreateCustomRule(string topicName, string subscriptionName, string ruleName, string filter, string? action);
    Task CreateSubscription(string topicName, string subscriptionName);
    Task DeleteRule(string topicName, string subscriptionName, string ruleName);
    Task DeleteSubscription(string topicName, string subscriptionName);
    Task DeleteTopic(string topicName);
    Task DisableSubscription(string topicName, string subscriptionName);
    Task EnableSubscription(string topicName, string subscriptionName);
    Task<bool> IsSubscriptionActive(string topicName, string subscriptionName);
    Task<SubscriptionState> GetSubscriptionState(string topicName, string subscriptionName);
    Task DisableTopicSend(string topicName);
    Task EnableTopicSend(string topicName);
    Task<TopicSendState> GetTopicSendState(string topicName);
    Task UpdateForwardTo(string topicName, string subscriptionName, string forwardTo);

    /// <summary>
    /// Sets status and auto-forward destination in a single update, so a pause that both
    /// blocks delivery and detaches forwarding cannot half-apply. A null or empty
    /// <paramref name="forwardTo"/> clears auto-forwarding; pass
    /// <paramref name="changeForwardTo"/> = false to leave the destination as it is.
    /// </summary>
    Task UpdateSubscription(
        string topicName,
        string subscriptionName,
        EntityStatus status,
        string? forwardTo,
        bool changeForwardTo);

    /// <summary>
    /// Fetches one subscription's settings (status, session flag, ForwardTo), or null when
    /// it doesn't exist. Unlike the toggles above, nothing other than not-found is swallowed.
    /// </summary>
    Task<SubscriptionProperties?> GetSubscription(string topicName, string subscriptionName);

    /// <summary>Enumerates every topic in the namespace.</summary>
    IAsyncEnumerable<TopicProperties> ListTopicsAsync();

    /// <summary>Enumerates the subscriptions on a topic.</summary>
    IAsyncEnumerable<SubscriptionProperties> ListSubscriptionsAsync(string topicName);

    /// <summary>Enumerates the rules on a subscription.</summary>
    IAsyncEnumerable<RuleProperties> ListRulesAsync(string topicName, string subscriptionName);

    /// <summary>
    /// Live message counters for every topic in the namespace — one management call per
    /// page rather than one per topic, so this stays cheap as the platform grows.
    /// </summary>
    IAsyncEnumerable<TopicRuntimeProperties> ListTopicRuntimePropertiesAsync();

    /// <summary>
    /// Live message counters (active / dead-letter / in-transit) for every subscription on
    /// a topic, in a single management call per page.
    /// </summary>
    IAsyncEnumerable<SubscriptionRuntimeProperties> ListSubscriptionRuntimePropertiesAsync(string topicName);
}

public class ServiceBusManagement : IServiceBusManagement
{
    private readonly ServiceBusAdministrationClient client;
    private readonly ILogger? _logger;

    public ServiceBusManagement(ServiceBusAdministrationClient client, ILogger<ServiceBusManagement>? logger = null)
    {
        this.client = client;
        _logger = logger;
    }

    public async Task CreateSubscription(string topicName, string subscriptionName)
    {
        ServiceBusFilterValidator.ValidateEntityPath(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
        try
        {
            var subscriptionProperties = new CreateSubscriptionOptions(topicName, subscriptionName)
            {
                MaxDeliveryCount = Constants.ServiceBusMaxDeliveryCount,
                LockDuration = TimeSpan.FromSeconds(30),
                EnableBatchedOperations = true,
                EnableDeadLetteringOnFilterEvaluationExceptions = true,
                RequiresSession = true
            };

            _logger?.LogTrace("Creating subscription...");
            await client.CreateSubscriptionAsync(subscriptionProperties);
            _logger?.LogTrace("Created subscription successfully.");
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not create subscription");
            throw;
        }
    }

    public async Task DeleteSubscription(string topicName, string subscriptionName)
    {
        ServiceBusFilterValidator.ValidateEntityPath(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
        try
        {
            _logger?.LogTrace("Deleting subscription...");
            var result = await client.DeleteSubscriptionAsync(topicName, subscriptionName);
            _logger?.LogTrace("Deleted subscription successfully.");
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not delete subscription");
            throw;
        }
    }

    public async Task DeleteTopic(string topicName)
    {
        ServiceBusFilterValidator.ValidateEntityPath(topicName, nameof(topicName));
        try
        {
            await client.DeleteTopicAsync(topicName);
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not delete topic");
            throw;
        }
    }

    public async Task CreateCustomRule(string topicName, string subscriptionName, string ruleName, string filter, string? action)
    {
        // Names are validated here; `filter` and `action` are SQL templates whose
        // interpolated values must already have been validated upstream. We don't
        // second-guess the SQL syntax of an explicitly-supplied custom rule.
        ServiceBusFilterValidator.ValidateEntityPath(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
        ServiceBusFilterValidator.ValidateName(ruleName, nameof(ruleName));
        try
        {
            var ruleOptions = new CreateRuleOptions
            {
                Filter = new SqlRuleFilter(filter),
                Name = ruleName
            };

            if (!String.IsNullOrEmpty(action))
            {
                ruleOptions.Action = new SqlRuleAction(action);
            }

            _logger?.LogTrace("Creating rule...");
            var result = await client.CreateRuleAsync(topicName, subscriptionName, ruleOptions);
            _logger?.LogTrace("Created rule successfully.");
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not create rule");
            throw;
        }
    }

    public async Task DeleteRule(string topicName, string subscriptionName, string ruleName)
    {
        ServiceBusFilterValidator.ValidateEntityPath(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
        ServiceBusFilterValidator.ValidateName(ruleName, nameof(ruleName));
        try
        {
            _logger?.LogTrace("Deleting rule...");
            var response = await client.DeleteRuleAsync(topicName, subscriptionName, ruleName);
            _logger?.LogTrace("Deleted rule successfully.");
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not delete rule");
            throw;
        }
    }
    public async Task DisableSubscription(string topicName, string subscriptionName)
    {
        ServiceBusFilterValidator.ValidateEntityPath(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
        try
        {
            var subscription = await client.GetSubscriptionAsync(topicName, subscriptionName);
            if (subscription != null)
            {
                _logger?.LogTrace("Updating status for subscription...");

                subscription.Value.Status = EntityStatus.ReceiveDisabled;
                await client.UpdateSubscriptionAsync(subscription);

                _logger?.LogTrace("Status updated on subscription successfully.");
            }
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not update status for subscription");
            throw;
        }
    }

    public async Task EnableSubscription(string topicName, string subscriptionName)
    {
        ServiceBusFilterValidator.ValidateEntityPath(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
        try
        {
            var subscription = await client.GetSubscriptionAsync(topicName, subscriptionName);
            if (subscription != null)
            {
                _logger?.LogTrace("Updating status for subscription...");

                subscription.Value.Status = EntityStatus.Active;
                await client.UpdateSubscriptionAsync(subscription);

                _logger?.LogTrace("Status updated on subscription successfully.");
            }
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not update status for subscription");
            throw;
        }
    }

    public async Task DisableTopicSend(string topicName)
    {
        ServiceBusFilterValidator.ValidateEntityPath(topicName, nameof(topicName));
        try
        {
            var topic = await client.GetTopicAsync(topicName);
            if (topic != null)
            {
                _logger?.LogTrace("Updating send status for topic...");

                topic.Value.Status = EntityStatus.SendDisabled;
                await client.UpdateTopicAsync(topic);

                _logger?.LogTrace("Send status updated on topic successfully.");
            }
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not update send status for topic");
            throw;
        }
    }

    public async Task EnableTopicSend(string topicName)
    {
        ServiceBusFilterValidator.ValidateEntityPath(topicName, nameof(topicName));
        try
        {
            var topic = await client.GetTopicAsync(topicName);
            if (topic != null)
            {
                _logger?.LogTrace("Updating send status for topic...");

                topic.Value.Status = EntityStatus.Active;
                await client.UpdateTopicAsync(topic);

                _logger?.LogTrace("Send status updated on topic successfully.");
            }
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not update send status for topic");
            throw;
        }
    }

    public async Task<TopicSendState> GetTopicSendState(string topicName)
    {
        ServiceBusFilterValidator.ValidateEntityPath(topicName, nameof(topicName));
        try
        {
            var topic = await client.GetTopicAsync(topicName);
            // Any non-Active status (SendDisabled, or fully Disabled) means producers
            // can't publish, so it collapses to SendDisabled from the send perspective.
            return topic?.Value?.Status == EntityStatus.Active
                ? TopicSendState.Enabled
                : TopicSendState.SendDisabled;
        }
        catch (Azure.Messaging.ServiceBus.ServiceBusException ex)
            when (ex.Reason == Azure.Messaging.ServiceBus.ServiceBusFailureReason.MessagingEntityNotFound)
        {
            _logger?.LogInformation("Topic '{TopicName}' was not found.", topicName);
            return TopicSendState.NotFound;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            _logger?.LogInformation("Topic '{TopicName}' was not found.", topicName);
            return TopicSendState.NotFound;
        }
    }

    public async Task UpdateForwardTo(string topicName, string subscriptionName, string forwardTo)
    {
        ServiceBusFilterValidator.ValidateEntityPath(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
        ServiceBusFilterValidator.ValidateEntityPath(forwardTo, nameof(forwardTo));
        try
        {
            var subscription = await client.GetSubscriptionAsync(topicName, subscriptionName);
            if (subscription != null)
            {
                _logger?.LogTrace("Updating forward to for subscription...");

                subscription.Value.ForwardTo = forwardTo;
                await client.UpdateSubscriptionAsync(subscription);

                _logger?.LogTrace("Forward to updated on subscription successfully.");
            }
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not update forward to for subscription");
            throw;
        }
    }

    public async Task UpdateSubscription(
        string topicName,
        string subscriptionName,
        EntityStatus status,
        string? forwardTo,
        bool changeForwardTo)
    {
        ServiceBusFilterValidator.ValidateEntityPath(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
        if (changeForwardTo && !String.IsNullOrEmpty(forwardTo))
        {
            ServiceBusFilterValidator.ValidateEntityPath(forwardTo, nameof(forwardTo));
        }

        try
        {
            var subscription = await client.GetSubscriptionAsync(topicName, subscriptionName);
            if (subscription == null) return;

            subscription.Value.Status = status;
            if (changeForwardTo)
            {
                // Empty string is how the SDK expresses "no auto-forward"; null round-trips
                // as "leave unchanged" on some paths, which would silently keep forwarding.
                subscription.Value.ForwardTo = String.IsNullOrEmpty(forwardTo) ? String.Empty : forwardTo;
            }

            _logger?.LogTrace("Updating subscription status and forwarding...");
            await client.UpdateSubscriptionAsync(subscription);
            _logger?.LogTrace("Subscription updated successfully.");
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "Could not update subscription");
            throw;
        }
    }

    public async Task<SubscriptionProperties?> GetSubscription(string topicName, string subscriptionName)
    {
        ServiceBusFilterValidator.ValidateEntityPath(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
        try
        {
            var subscription = await client.GetSubscriptionAsync(topicName, subscriptionName);
            return subscription?.Value;
        }
        catch (Azure.Messaging.ServiceBus.ServiceBusException ex)
            when (ex.Reason == Azure.Messaging.ServiceBus.ServiceBusFailureReason.MessagingEntityNotFound)
        {
            return null;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            return null;
        }
    }

    public async IAsyncEnumerable<TopicProperties> ListTopicsAsync()
    {
        await foreach (var page in client.GetTopicsAsync().AsPages())
        {
            foreach (var topic in page.Values)
            {
                yield return topic;
            }
        }
    }

    public async IAsyncEnumerable<SubscriptionProperties> ListSubscriptionsAsync(string topicName)
    {
        ServiceBusFilterValidator.ValidateEntityPath(topicName, nameof(topicName));
        await foreach (var page in client.GetSubscriptionsAsync(topicName).AsPages())
        {
            foreach (var subscription in page.Values)
            {
                yield return subscription;
            }
        }
    }

    public async IAsyncEnumerable<RuleProperties> ListRulesAsync(string topicName, string subscriptionName)
    {
        ServiceBusFilterValidator.ValidateEntityPath(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
        await foreach (var page in client.GetRulesAsync(topicName, subscriptionName).AsPages())
        {
            foreach (var rule in page.Values)
            {
                yield return rule;
            }
        }
    }

    public async IAsyncEnumerable<TopicRuntimeProperties> ListTopicRuntimePropertiesAsync()
    {
        await foreach (var page in client.GetTopicsRuntimePropertiesAsync().AsPages())
        {
            foreach (var topic in page.Values)
            {
                yield return topic;
            }
        }
    }

    public async IAsyncEnumerable<SubscriptionRuntimeProperties> ListSubscriptionRuntimePropertiesAsync(string topicName)
    {
        ServiceBusFilterValidator.ValidateEntityPath(topicName, nameof(topicName));
        await foreach (var page in client.GetSubscriptionsRuntimePropertiesAsync(topicName).AsPages())
        {
            foreach (var subscription in page.Values)
            {
                yield return subscription;
            }
        }
    }

    public async Task<bool> IsSubscriptionActive(string topicName, string subscriptionName)
    {
        return await GetSubscriptionState(topicName, subscriptionName) == SubscriptionState.Active;
    }

    public async Task<SubscriptionState> GetSubscriptionState(string topicName, string subscriptionName)
    {
        ServiceBusFilterValidator.ValidateEntityPath(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
        try
        {
            var subscription = await client.GetSubscriptionAsync(topicName, subscriptionName);
            return subscription?.Value?.Status == EntityStatus.Active
                ? SubscriptionState.Active
                : SubscriptionState.Disabled;
        }
        catch (Azure.Messaging.ServiceBus.ServiceBusException ex)
            when (ex.Reason == Azure.Messaging.ServiceBus.ServiceBusFailureReason.MessagingEntityNotFound)
        {
            _logger?.LogInformation("Subscription '{SubscriptionName}' on topic '{TopicName}' was not found.", subscriptionName, topicName);
            return SubscriptionState.NotFound;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            _logger?.LogInformation("Subscription '{SubscriptionName}' on topic '{TopicName}' was not found.", subscriptionName, topicName);
            return SubscriptionState.NotFound;
        }
    }

}
