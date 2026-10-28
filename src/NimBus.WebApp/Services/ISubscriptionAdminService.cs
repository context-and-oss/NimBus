using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Services;

/// <summary>
/// Per-subscription Service Bus operations for incident response: see where a backlog
/// actually is, stop it growing, and clear it — without the endpoint-wide blast radius of
/// the message-store bulk operations in <see cref="IAdminService"/>.
/// </summary>
/// <remarks>
/// The motivating case is a producer flooding the bus: copies of every message land both
/// on the consuming endpoint's forwarder subscription and on the per-endpoint
/// <c>Resolver</c> subscription, which auto-forwards to the Resolver topic. An
/// auto-forwarding subscription can't be drained with a receiver — Service Bus rejects
/// receive on it — so clearing one means deleting and re-provisioning it, which is only
/// safe because <c>TopologyDescriptor</c> can describe exactly what to put back.
/// </remarks>
public interface ISubscriptionAdminService
{
    /// <summary>Message counters for every topic in the namespace.</summary>
    Task<IEnumerable<ServiceBusTopicOverview>> GetTopicOverviewAsync();

    /// <summary>Deletes a topic that is outside the platform topology.</summary>
    Task<SubscriptionActionResult> DeleteTopicAsync(string topicName);

    /// <summary>Counters, settings and rules for every subscription on a topic.</summary>
    Task<IEnumerable<ServiceBusSubscriptionInfo>> GetSubscriptionsAsync(string topicName);

    Task<DeadLetterOverview> GetResolverDeadLettersAsync(
        string subscriptionName,
        CancellationToken cancellationToken = default);

    Task<BulkOperationResult> ResubmitResolverDeadLettersAsync(
        string subscriptionName,
        bool all,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pause (<c>ReceiveDisabled</c>) or resume delivery on one subscription. Reversible;
    /// messages already enqueued stay put.
    /// </summary>
    Task<SubscriptionActionResult> SetSubscriptionStatusAsync(string topicName, string subscriptionName, bool enable);

    /// <summary>
    /// Drain every message from a subscription with a receiver. Fails for an
    /// auto-forwarding subscription — use <see cref="RecreateSubscriptionAsync"/>.
    /// </summary>
    Task<BulkOperationResult> PurgeSubscriptionAsync(string topicName, string subscriptionName);

    /// <summary>
    /// Complete every message in a subscription's dead-letter queue and transfer dead-letter
    /// queue, which Service Bus never empties on its own. A paused subscription stays paused:
    /// unlike <see cref="PurgeSubscriptionAsync"/>, this never makes it receivable, since that
    /// would hand its paused backlog to consumers.
    /// </summary>
    Task<BulkOperationResult> PurgeDeadLetterQueueAsync(string topicName, string subscriptionName);

    /// <summary>
    /// Delete a subscription and immediately re-provision it from the platform topology.
    /// The fastest way to discard a large backlog; the rebuilt subscription is identical
    /// to a freshly provisioned one.
    /// </summary>
    Task<SubscriptionActionResult> RecreateSubscriptionAsync(string topicName, string subscriptionName);

    /// <summary>Delete a subscription without putting it back.</summary>
    Task<SubscriptionActionResult> DeleteSubscriptionAsync(string topicName, string subscriptionName);

    /// <summary>
    /// Detach one rule so no new messages enter the subscription. Reversible via
    /// <see cref="RestoreRulesAsync"/>.
    /// </summary>
    Task<SubscriptionActionResult> DeleteRuleAsync(string topicName, string subscriptionName, string ruleName);

    /// <summary>Re-attach any expected rules currently missing from a subscription.</summary>
    Task<SubscriptionActionResult> RestoreRulesAsync(string topicName, string subscriptionName);
}
