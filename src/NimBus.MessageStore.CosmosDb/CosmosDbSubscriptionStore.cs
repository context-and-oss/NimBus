using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore;

internal sealed class CosmosDbSubscriptionStore : ISubscriptionStore
{
    private readonly Func<Task<ICosmosContainerAdapter>> _getSubscriptionsContainer;
    private readonly Func<string, Task<string>> _getEndpointErrorList;
    private readonly ILogger? _logger;

    private static bool ValidateEmail(string mail)
    {
        var regex = new Regex(@"^([\w\.\-]+)@([\w\-]+)((\.(\w){2,3})+)$");
        var match = regex.Match(mail);
        return match.Success;
    }

    public CosmosDbSubscriptionStore(
        Func<Task<ICosmosContainerAdapter>> getSubscriptionsContainer,
        Func<string, Task<string>> getEndpointErrorList,
        ILogger? logger)
    {
        _getSubscriptionsContainer = getSubscriptionsContainer;
        _getEndpointErrorList = getEndpointErrorList;
        _logger = logger;
    }
    public async Task<EndpointSubscription> SubscribeToEndpointNotification(string endpointId, string mail,
        string type, string author, string url, List<string> eventTypes, string payload, int frequency)
    {
        var formattedType = string.Equals(type, "mail", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(type, "teams", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(type, "mail;teams", StringComparison.OrdinalIgnoreCase)
            ? type.ToLowerInvariant()
            : throw new ArgumentException($"Invalid type.{type} valid: mail or teams ", nameof(type));

        if (!ValidateEmail(mail)) throw new ArgumentException($"Invalid email: {mail}", nameof(mail));

        var subscriptionContainer = await _getSubscriptionsContainer();
        var subscription = new EndpointSubscription
        {
            Mail = mail,
            Url = url,
            Type = formattedType,
            EndpointId = endpointId,
            AuthorId = author,
            Id = Guid.NewGuid().ToString(),
            EventTypes = eventTypes,
            Payload = payload,
            Frequency = frequency
        };

        //Add author here
        var response = await subscriptionContainer.UpsertItemAsync(subscription, new PartitionKey(subscription.Id));

        if (response.StatusCode == HttpStatusCode.OK || response.StatusCode == HttpStatusCode.Created)
        {
            _logger?.LogTrace(
                "COSMOS SUBSCRIPTION: endpointId: {EndpointId}, SubscriptionId: {SubscriptionId}, HttpStatusCode: {StatusCode}", subscription.EndpointId, subscription.Id, response.StatusCode);
            return subscription;
        }

        _logger?.LogError(
            "COSMOS SUBSCRIPTION ERROR: endpointId: {EndpointId}, SubscriptionId: {SubscriptionId}, HttpStatusCode: {StatusCode}", subscription.EndpointId, subscription.Id, response.StatusCode);
        return null!; //Return error?
    }

    public async Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpoint(string endpointId)
    {
        var subscriptions = new List<EndpointSubscription>();
        var subscriptionContainer = await _getSubscriptionsContainer();

        var queryDefinition = new QueryDefinition("SELECT * FROM c WHERE c.endpointId = @endpointId")
            .WithParameter("@endpointId", endpointId);
        var result = subscriptionContainer.GetItemQueryIterator<EndpointSubscription>(queryDefinition);

        while (result.HasMoreResults)
        {
            var subDbo = await result.ReadNextAsync();
            foreach (var queryResult in subDbo)
            {
                subscriptions.Add(queryResult);
            }
        }

        return subscriptions;
    }

    public async Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpointWithEventtype(string endpointId,
        string eventType, string payload, string errorText)
    {
        var subscriptions = new List<EndpointSubscription>();
        var subscriptionContainer = await _getSubscriptionsContainer();

        var sqlQuery = "SELECT * FROM c WHERE c.endpointId = @endpointId";

        // Build query dynamically with parameterized values
        if (!String.IsNullOrEmpty(eventType))
        {
            sqlQuery += " AND (ARRAY_CONTAINS(c.eventTypes, @eventType) OR ARRAY_LENGTH(c.eventTypes) = 0 OR c.eventTypes = null OR c.eventTypes = '' OR (NOT IS_DEFINED(c.eventTypes)))";
        }
        if (!String.IsNullOrEmpty(payload))
        {
            sqlQuery += " AND (CONTAINS(@payload, c.payload) OR c.payload = null OR c.payload = '' OR (NOT IS_DEFINED(c.payload))";
            if (!String.IsNullOrEmpty(errorText))
            {
                sqlQuery += " OR CONTAINS(@errorText, c.payload)";
            }
            sqlQuery += ")";
        }

        var queryDefinition = new QueryDefinition(sqlQuery)
            .WithParameter("@endpointId", endpointId);

        if (!String.IsNullOrEmpty(eventType))
            queryDefinition = queryDefinition.WithParameter("@eventType", eventType);
        if (!String.IsNullOrEmpty(payload))
            queryDefinition = queryDefinition.WithParameter("@payload", payload);
        if (!String.IsNullOrEmpty(errorText))
            queryDefinition = queryDefinition.WithParameter("@errorText", errorText);

        var result = subscriptionContainer.GetItemQueryIterator<EndpointSubscription>(queryDefinition);

        while (result.HasMoreResults)
        {
            var subDbo = await result.ReadNextAsync();
            foreach (var queryResult in subDbo)
            {
                subscriptions.Add(queryResult);
            }
        }

        return subscriptions;
    }

    public async Task<bool> DeleteSubscription(string subscriptionId)
    {
        if (string.IsNullOrWhiteSpace(subscriptionId)) return false;

        var subscriptionContainer = await _getSubscriptionsContainer();

        try
        {
            var response = await subscriptionContainer.DeleteItemAsync<SubscriptionDbo>(subscriptionId, new PartitionKey(subscriptionId));
            _logger?.LogTrace(
                "COSMOS REMOVE-SUBSCRIPTION: SubscriptionId: {SubscriptionId}, HttpStatusCode: {StatusCode}", subscriptionId, response.StatusCode);
            return true;
        }
        catch (Exception e)
        {
            _logger?.LogError(e,
                "COSMOS REMOVE-SUBSCRIPTION: SubscriptionId: {SubscriptionId}", subscriptionId);
            return false;
        }
    }
    public async Task<bool> UnsubscribeById(string endpointId, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;

        var subscriptionContainer = await _getSubscriptionsContainer();

        try
        {
            var response = await subscriptionContainer.DeleteItemAsync<SubscriptionDbo>(id, new PartitionKey(id));
            _logger?.LogTrace(
                "COSMOS REMOVE-SUBSCRIPTION: endpointId: {EndpointId}, SubscriptionId: {SubscriptionId}, HttpStatusCode: {StatusCode}", endpointId, id, response.StatusCode);
            return true;
        }
        catch (Exception e)
        {
            _logger?.LogError(e,
                "COSMOS REMOVE-SUBSCRIPTION: endpointId: {EndpointId}, SubscriptionId: {SubscriptionId}", endpointId, id);
            return false;
        }
    }

    public async Task<bool> UnsubscribeByMail(string endpointId, string mail)
    {
        if (string.IsNullOrWhiteSpace(mail)) return false;

        var subs = await GetSubscriptionsOnEndpoint(endpointId);
        var mySubscription =
            subs.FirstOrDefault(x => string.Equals(mail, x.Mail, StringComparison.OrdinalIgnoreCase));
        if (mySubscription != null)
        {
            return await UnsubscribeById(endpointId, mySubscription.Id);
        }

        return false;
    }

    public async Task<bool> UpdateSubscription(EndpointSubscription subscription)
    {
        subscription.ErrorList = await _getEndpointErrorList(subscription.EndpointId);
        subscription.NotifiedAt = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
        try
        {
            var subscriberContainer = await _getSubscriptionsContainer();
            await subscriberContainer.UpsertItemAsync(subscription, new PartitionKey(subscription.Id));
            return true;
        }
        catch (Exception e)
        {
            _logger?.LogError(e,
                "COSMOS UPDATE-SUBSCRIPTION: Endpoint: {EndpointId}, SubscriptionId: {SubscriptionId}", subscription.EndpointId, subscription.Id);
            return false;
        }
    }

    private sealed class SubscriptionDbo
    {
        [JsonProperty(PropertyName = "id")] public string Id { get; set; } = null!;
        [JsonProperty(PropertyName = "type")] public string Type { get; set; } = null!;
        [JsonProperty(PropertyName = "severity")] public string Severity { get; set; } = null!;
        [JsonProperty(PropertyName = "mail")] public string Mail { get; set; } = null!;
        [JsonProperty(PropertyName = "endpointId")] public string EndpointId { get; set; } = null!;
    }

}
