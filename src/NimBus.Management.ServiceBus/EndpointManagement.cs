using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Messages;
using System;
using System.Threading.Tasks;

namespace NimBus.Management.ServiceBus;
public partial class EndpointManagement
{
    private readonly IServiceBusManagement _serviceBusManagement;
    private readonly ILogger _logger;

    public EndpointManagement(IServiceBusManagement serviceBusManagement, ILogger<EndpointManagement>? logger = null)
    {
        _serviceBusManagement = serviceBusManagement;
        _logger = logger ?? NullLogger<EndpointManagement>.Instance;
    }

    public async Task ClearEndpoint(string endpointName)
    {
        string topicName = endpointName;
        string subscriptionName = endpointName;

        await _serviceBusManagement.DeleteSubscription(topicName, subscriptionName);

        await _serviceBusManagement.CreateSubscription(topicName, subscriptionName);

        await _serviceBusManagement.DeleteRule(topicName, subscriptionName, "$Default");

        // Recreate the same rule set ServiceBusTopologyProvisioner.EnsureEndpointTopologyAsync
        // puts on the endpoint's own subscription. Filter/action strings must stay
        // byte-identical to the provisioner's: its RuleMatches compares ordinally, so any
        // drift (even whitespace) makes the next `topology apply` delete and recreate rules.
        await _serviceBusManagement.CreateCustomRule(
            topicName, subscriptionName, $"to-{subscriptionName}",
            $"user.To = '{subscriptionName}'", action: null);
        await _serviceBusManagement.CreateCustomRule(
            topicName, subscriptionName, "continuation",
            $"user.To = '{Constants.ContinuationId}'",
            $"SET user.To = '{subscriptionName}'; SET user.From = '{Constants.ContinuationId}'");
        await _serviceBusManagement.CreateCustomRule(
            topicName, subscriptionName, "retry",
            $"user.To = '{Constants.RetryId}'",
            $"SET user.To = '{subscriptionName}'; SET user.From = '{Constants.RetryId}'");

        LogClearedEndpointSuccessfully(_logger);
    }

    public async Task DisableEndpoint(string endpointName)
    {
        string topicName = endpointName;
        string subscriptionName = endpointName;

        await _serviceBusManagement.DisableSubscription(topicName, subscriptionName);

        LogDisabledEndpointSuccessfully(_logger);
    }

    public async Task EnableEndpoint(string endpointName)
    {
        string topicName = endpointName;
        string subscriptionName = endpointName;

        await _serviceBusManagement.EnableSubscription(topicName, subscriptionName);

        LogEnabledEndpointSuccessfully(_logger);
    }

    public async Task<bool> IsEndpointActive(string endpointName)
    {
        string topicName = endpointName;
        string subscriptionName = endpointName;

        return await _serviceBusManagement.IsSubscriptionActive(topicName, subscriptionName);
    }

    public async Task DisableEndpointSend(string endpointName)
    {
        string topicName = endpointName;

        await _serviceBusManagement.DisableTopicSend(topicName);

        LogDisabledEndpointSendSuccessfully(_logger);
    }

    public async Task EnableEndpointSend(string endpointName)
    {
        string topicName = endpointName;

        await _serviceBusManagement.EnableTopicSend(topicName);

        LogEnabledEndpointSendSuccessfully(_logger);
    }

    public async Task<TopicSendState> GetEndpointSendState(string endpointName)
    {
        string topicName = endpointName;

        return await _serviceBusManagement.GetTopicSendState(topicName);
    }

    public async Task<SubscriptionState> GetEndpointSubscriptionState(string endpointName)
    {
        string topicName = endpointName;
        string subscriptionName = endpointName;

        return await _serviceBusManagement.GetSubscriptionState(topicName, subscriptionName);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cleared endpoint successfully")]
    private static partial void LogClearedEndpointSuccessfully(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Disabled endpoint successfully")]
    private static partial void LogDisabledEndpointSuccessfully(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Enabled endpoint successfully")]
    private static partial void LogEnabledEndpointSuccessfully(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Disabled endpoint send successfully")]
    private static partial void LogDisabledEndpointSendSuccessfully(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Enabled endpoint send successfully")]
    private static partial void LogEnabledEndpointSendSuccessfully(ILogger logger);
}
