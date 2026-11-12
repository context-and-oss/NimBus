using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
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
    /// Sets status, auto-forward destination and <c>UserMetadata</c> in a single update, so a
    /// marker written alongside a status change can never be seen without it. Forwarding
    /// behaves as in <see cref="UpdateSubscription(string, string, EntityStatus, string?, bool)"/>.
    /// A non-null <paramref name="userMetadata"/>, including <see cref="string.Empty"/>,
    /// replaces the subscription's <c>UserMetadata</c>; null leaves it unchanged. To clear it,
    /// pass <see cref="string.Empty"/>: Service Bus does not accept null for <c>UserMetadata</c>.
    /// </summary>
    Task UpdateSubscription(
        string topicName,
        string subscriptionName,
        EntityStatus status,
        string? forwardTo,
        bool changeForwardTo,
        string? userMetadata);

    /// <summary>
    /// Fetches one subscription's settings (status, session flag, ForwardTo, UserMetadata), or
    /// null when it doesn't exist. A <c>UserMetadata</c> Service Bus returns as null reads back
    /// as <see cref="string.Empty"/>, so callers can treat "none" one way. Unlike the toggles
    /// above, nothing other than not-found is swallowed.
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

public partial class ServiceBusManagement : IServiceBusManagement
{
    private readonly ServiceBusAdministrationClient client;
    private readonly ILogger _logger;

    public ServiceBusManagement(ServiceBusAdministrationClient client, ILogger<ServiceBusManagement>? logger = null)
    {
        this.client = client;
        _logger = logger ?? NullLogger<ServiceBusManagement>.Instance;
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

            LogCreatingSubscription(_logger);
            await client.CreateSubscriptionAsync(subscriptionProperties);
            LogCreatedSubscriptionSuccessfully(_logger);
        }
        catch (Exception e)
        {
            LogCouldNotCreateSubscription(_logger, e);
            throw;
        }
    }

    public async Task DeleteSubscription(string topicName, string subscriptionName)
    {
        ServiceBusFilterValidator.ValidateEntityPath(topicName, nameof(topicName));
        ServiceBusFilterValidator.ValidateName(subscriptionName, nameof(subscriptionName));
        try
        {
            LogDeletingSubscription(_logger);
            var result = await client.DeleteSubscriptionAsync(topicName, subscriptionName);
            LogDeletedSubscriptionSuccessfully(_logger);
        }
        catch (Exception e)
        {
            LogCouldNotDeleteSubscription(_logger, e);
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
            LogCouldNotDeleteTopic(_logger, e);
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

            LogCreatingRule(_logger);
            var result = await client.CreateRuleAsync(topicName, subscriptionName, ruleOptions);
            LogCreatedRuleSuccessfully(_logger);
        }
        catch (Exception e)
        {
            LogCouldNotCreateRule(_logger, e);
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
            LogDeletingRule(_logger);
            var response = await client.DeleteRuleAsync(topicName, subscriptionName, ruleName);
            LogDeletedRuleSuccessfully(_logger);
        }
        catch (Exception e)
        {
            LogCouldNotDeleteRule(_logger, e);
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
                LogUpdatingStatusForSubscription(_logger);

                subscription.Value.Status = EntityStatus.ReceiveDisabled;
                await client.UpdateSubscriptionAsync(subscription);

                LogStatusUpdatedOnSubscriptionSuccessfully(_logger);
            }
        }
        catch (Exception e)
        {
            LogCouldNotUpdateStatusForSubscription(_logger, e);
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
                LogUpdatingStatusForSubscription(_logger);

                subscription.Value.Status = EntityStatus.Active;
                await client.UpdateSubscriptionAsync(subscription);

                LogStatusUpdatedOnSubscriptionSuccessfully(_logger);
            }
        }
        catch (Exception e)
        {
            LogCouldNotUpdateStatusForSubscription(_logger, e);
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
                LogUpdatingSendStatusForTopic(_logger);

                topic.Value.Status = EntityStatus.SendDisabled;
                await client.UpdateTopicAsync(topic);

                LogSendStatusUpdatedOnTopicSuccessfully(_logger);
            }
        }
        catch (Exception e)
        {
            LogCouldNotUpdateSendStatus(_logger, e);
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
                LogUpdatingSendStatusForTopic(_logger);

                topic.Value.Status = EntityStatus.Active;
                await client.UpdateTopicAsync(topic);

                LogSendStatusUpdatedOnTopicSuccessfully(_logger);
            }
        }
        catch (Exception e)
        {
            LogCouldNotUpdateSendStatus(_logger, e);
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
            LogTopicWasNotFound(_logger, topicName);
            return TopicSendState.NotFound;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            LogTopicWasNotFound(_logger, topicName);
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
                LogUpdatingForwardToForSubscription(_logger);

                subscription.Value.ForwardTo = forwardTo;
                await client.UpdateSubscriptionAsync(subscription);

                LogForwardToUpdatedOnSubscriptionSuccessfully(_logger);
            }
        }
        catch (Exception e)
        {
            LogCouldNotUpdateForward(_logger, e);
            throw;
        }
    }

    public Task UpdateSubscription(
        string topicName,
        string subscriptionName,
        EntityStatus status,
        string? forwardTo,
        bool changeForwardTo) =>
        UpdateSubscription(topicName, subscriptionName, status, forwardTo, changeForwardTo, userMetadata: null);

    public async Task UpdateSubscription(
        string topicName,
        string subscriptionName,
        EntityStatus status,
        string? forwardTo,
        bool changeForwardTo,
        string? userMetadata)
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

            // Null means "leave unchanged"; the SDK rejects null for UserMetadata anyway.
            if (userMetadata is not null)
            {
                subscription.Value.UserMetadata = userMetadata;
            }

            LogUpdatingSubscriptionStatusAndForwarding(_logger);
            await client.UpdateSubscriptionAsync(subscription);
            LogSubscriptionUpdatedSuccessfully(_logger);
        }
        catch (Exception e)
        {
            LogCouldNotUpdateSubscription(_logger, e);
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
            var properties = subscription?.Value;

            // A subscription nobody wrote metadata to can read back null. Normalise it so
            // "no metadata" is always string.Empty, the value a later update can write back.
            if (properties is not null && properties.UserMetadata is null)
            {
                properties.UserMetadata = String.Empty;
            }

            return properties;
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
            LogSubscriptionOnTopicWasNotFound(_logger, subscriptionName, topicName);
            return SubscriptionState.NotFound;
        }
        catch (Azure.RequestFailedException ex) when (ex.Status == 404)
        {
            LogSubscriptionOnTopicWasNotFound(_logger, subscriptionName, topicName);
            return SubscriptionState.NotFound;
        }
    }


    [LoggerMessage(Level = LogLevel.Trace, Message = "Creating subscription...")]
    private static partial void LogCreatingSubscription(ILogger logger);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Created subscription successfully.")]
    private static partial void LogCreatedSubscriptionSuccessfully(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not create subscription")]
    private static partial void LogCouldNotCreateSubscription(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Deleting subscription...")]
    private static partial void LogDeletingSubscription(ILogger logger);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Deleted subscription successfully.")]
    private static partial void LogDeletedSubscriptionSuccessfully(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not delete subscription")]
    private static partial void LogCouldNotDeleteSubscription(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not delete topic")]
    private static partial void LogCouldNotDeleteTopic(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Creating rule...")]
    private static partial void LogCreatingRule(ILogger logger);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Created rule successfully.")]
    private static partial void LogCreatedRuleSuccessfully(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not create rule")]
    private static partial void LogCouldNotCreateRule(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Deleting rule...")]
    private static partial void LogDeletingRule(ILogger logger);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Deleted rule successfully.")]
    private static partial void LogDeletedRuleSuccessfully(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not delete rule")]
    private static partial void LogCouldNotDeleteRule(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Updating status for subscription...")]
    private static partial void LogUpdatingStatusForSubscription(ILogger logger);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Status updated on subscription successfully.")]
    private static partial void LogStatusUpdatedOnSubscriptionSuccessfully(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not update status for subscription")]
    private static partial void LogCouldNotUpdateStatusForSubscription(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Updating send status for topic...")]
    private static partial void LogUpdatingSendStatusForTopic(ILogger logger);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Send status updated on topic successfully.")]
    private static partial void LogSendStatusUpdatedOnTopicSuccessfully(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not update send status for topic")]
    private static partial void LogCouldNotUpdateSendStatus(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Topic '{TopicName}' was not found.")]
    private static partial void LogTopicWasNotFound(ILogger logger, string topicName);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Updating forward to for subscription...")]
    private static partial void LogUpdatingForwardToForSubscription(ILogger logger);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Forward to updated on subscription successfully.")]
    private static partial void LogForwardToUpdatedOnSubscriptionSuccessfully(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not update forward to for subscription")]
    private static partial void LogCouldNotUpdateForward(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Updating subscription status and forwarding...")]
    private static partial void LogUpdatingSubscriptionStatusAndForwarding(ILogger logger);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Subscription updated successfully.")]
    private static partial void LogSubscriptionUpdatedSuccessfully(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Could not update subscription")]
    private static partial void LogCouldNotUpdateSubscription(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Subscription '{SubscriptionName}' on topic '{TopicName}' was not found.")]
    private static partial void LogSubscriptionOnTopicWasNotFound(ILogger logger, string subscriptionName, string topicName);
}
