using System.Collections.Generic;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Storage operations for endpoint notification subscriptions (operator/team subscribers
/// to failed-event alerts and similar). Implemented per storage provider.
/// </summary>
public interface ISubscriptionStore
{
    Task<EndpointSubscription?> SubscribeToEndpointNotification(string endpointId, string mail, string type,
        string author, string url, List<string> eventTypes, string payload, int frequency);

    Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpoint(string endpointId);

    Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpointWithEventtype(string endpoint,
        string eventtypes, string payload, string errorText);

    Task<bool> UpdateSubscription(EndpointSubscription subscription);
    Task<bool> UnsubscribeById(string endpointId, string id);
    Task<bool> UnsubscribeByMail(string endpointId, string mail);
    Task<bool> DeleteSubscription(string subscriptionId);
}
