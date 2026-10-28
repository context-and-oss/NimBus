using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Azure.Messaging.ServiceBus;
using Azure.Messaging.ServiceBus.Administration;
using Microsoft.Extensions.Logging;
using NimBus.Core;
using NimBus.Management.ServiceBus;
using NimBus.ServiceBus.Provisioning;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Services;

/// <inheritdoc cref="ISubscriptionAdminService"/>
public class SubscriptionAdminService : ISubscriptionAdminService
{
    private readonly IPlatform _platform;
    private readonly IServiceBusManagement _sbManagement;
    private readonly ITopologyRebuilder _rebuilder;
    private readonly ServiceBusClient _sbClient;
    private readonly ILogger<SubscriptionAdminService> _logger;
    private readonly bool _isEmulator;
    private readonly IResolverDeadLetterClient? _resolverDeadLetterClient;

    /// <summary>
    /// Receive batch size and idle wait used when draining a subscription. The short wait
    /// is what terminates the drain loop once the subscription runs dry, so it can't be zero.
    /// </summary>
    private const int DrainBatchSize = 100;
    private static readonly TimeSpan DrainIdleTimeout = TimeSpan.FromSeconds(5);

    public SubscriptionAdminService(
        IPlatform platform,
        IServiceBusManagement sbManagement,
        ITopologyRebuilder rebuilder,
        ServiceBusClient sbClient,
        ILogger<SubscriptionAdminService> logger,
        bool isEmulator = false,
        IResolverDeadLetterClient? resolverDeadLetterClient = null)
    {
        _platform = platform;
        _sbManagement = sbManagement;
        _rebuilder = rebuilder;
        _sbClient = sbClient;
        _logger = logger;
        _isEmulator = isEmulator;
        _resolverDeadLetterClient = resolverDeadLetterClient;
    }

    // ───────────────────────── Read ─────────────────────────

    public async Task<IEnumerable<ServiceBusTopicOverview>> GetTopicOverviewAsync()
    {
        var endpointIds = new HashSet<string>(
            _platform.Endpoints.Select(endpoint => endpoint.Id),
            StringComparer.OrdinalIgnoreCase);

        // Two namespace-wide listings: settings (for Status) and runtime counters. Neither
        // is per-topic, so this stays cheap as the platform grows.
        var statuses = new Dictionary<string, EntityStatus>(StringComparer.OrdinalIgnoreCase);
        await foreach (var topic in _sbManagement.ListTopicsAsync())
        {
            statuses[topic.Name] = topic.Status;
        }

        var overview = new List<ServiceBusTopicOverview>();
        await foreach (var runtime in _sbManagement.ListTopicRuntimePropertiesAsync())
        {
            var row = new ServiceBusTopicOverview
            {
                Name = runtime.Name,
                IsSystemTopic = TopologyDescriptor.IsSystemTopic(runtime.Name),
                IsKnownToPlatform = endpointIds.Contains(runtime.Name)
                                    || TopologyDescriptor.IsSystemTopic(runtime.Name),
                Status = statuses.TryGetValue(runtime.Name, out var status) ? status.ToString() : "Unknown",
                SubscriptionCount = runtime.SubscriptionCount,
                ScheduledMessageCount = runtime.ScheduledMessageCount,
                SizeInBytes = runtime.SizeInBytes,
            };

            // A topic has no message count of its own — its messages live in its
            // subscriptions — so roll the subscription counters up. This is the number
            // that makes a flood visible at a glance.
            await foreach (var subscription in _sbManagement.ListSubscriptionRuntimePropertiesAsync(runtime.Name))
            {
                row.ActiveMessageCount += subscription.ActiveMessageCount;
                row.DeadLetterMessageCount += subscription.DeadLetterMessageCount;
                row.TransferMessageCount += subscription.TransferMessageCount;
                // Kept separate from DeadLetterMessageCount: a failed auto-forward strands
                // messages here, not in the regular DLQ, and that is exactly the incident
                // this page diagnoses.
                row.TransferDeadLetterMessageCount += subscription.TransferDeadLetterMessageCount;
            }

            overview.Add(row);
        }

        return overview
            .OrderByDescending(topic => topic.ActiveMessageCount + topic.TransferMessageCount)
            .ThenBy(topic => topic.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<SubscriptionActionResult> DeleteTopicAsync(string topicName)
    {
        var result = NewResult(topicName, string.Empty, "delete-topic");
        try
        {
            await _sbManagement.DeleteTopic(topicName);
            result.Succeeded = true;
            result.Message = $"Topic '{topicName}' deleted along with its subscriptions and messages.";
            _logger.LogWarning("Topic {Topic} deleted by operator", topicName);
        }
        catch (Exception exception)
        {
            Fail(result, exception, "Failed to delete topic {Topic}", topicName);
        }
        return result;
    }

    public async Task<IEnumerable<ServiceBusSubscriptionInfo>> GetSubscriptionsAsync(string topicName)
    {
        var runtimeByName = new Dictionary<string, SubscriptionRuntimeProperties>(StringComparer.OrdinalIgnoreCase);
        await foreach (var runtime in _sbManagement.ListSubscriptionRuntimePropertiesAsync(topicName))
        {
            runtimeByName[runtime.SubscriptionName] = runtime;
        }

        var result = new List<ServiceBusSubscriptionInfo>();
        await foreach (var settings in _sbManagement.ListSubscriptionsAsync(topicName))
        {
            var ruleNames = new List<string>();
            await foreach (var rule in _sbManagement.ListRulesAsync(topicName, settings.SubscriptionName))
            {
                ruleNames.Add(rule.Name);
            }

            var expected = FindExpected(topicName, settings.SubscriptionName);
            var missingRules = expected is null
                ? new List<string>()
                : expected.Rules
                    .Where(rule => !ruleNames.Any(actual => actual.Equals(rule.Name, StringComparison.OrdinalIgnoreCase)))
                    .Select(rule => rule.Name)
                    .ToList();

            // Only a rule the descriptor can put back is safe to detach — see DeleteRuleAsync.
            var detachableRules = expected is null
                ? new List<string>()
                : ruleNames
                    .Where(actual => expected.Rules.Any(rule => rule.Name.Equals(actual, StringComparison.OrdinalIgnoreCase)))
                    .ToList();

            runtimeByName.TryGetValue(settings.SubscriptionName, out var counts);

            result.Add(new ServiceBusSubscriptionInfo
            {
                Name = settings.SubscriptionName,
                TopicName = topicName,
                Status = settings.Status.ToString(),
                RequiresSession = settings.RequiresSession,
                ForwardTo = settings.ForwardTo,
                ExpectedForwardTo = expected?.ForwardTo,
                RuleNames = ruleNames,
                MissingRuleNames = missingRules,
                DetachableRuleNames = detachableRules,
                CanRecreate = expected is not null,
                ActiveMessageCount = counts?.ActiveMessageCount ?? 0,
                DeadLetterMessageCount = counts?.DeadLetterMessageCount ?? 0,
                TransferMessageCount = counts?.TransferMessageCount ?? 0,
                TransferDeadLetterMessageCount = counts?.TransferDeadLetterMessageCount ?? 0,
                TotalMessageCount = counts?.TotalMessageCount ?? 0,
                AccessedAt = counts?.AccessedAt.UtcDateTime ?? default,
            });
        }

        return result
            .OrderByDescending(subscription => subscription.ActiveMessageCount + subscription.TransferMessageCount)
            .ThenBy(subscription => subscription.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // ───────────────────────── Pause / resume ─────────────────────────

    public async Task<DeadLetterOverview> GetResolverDeadLettersAsync(
        string subscriptionName,
        CancellationToken cancellationToken = default)
    {
        await ValidateResolverDeadLetterTargetAsync(subscriptionName);
        return await ResolverDeadLetterClient.GetOverviewAsync(
            NimBus.Core.Messages.Constants.ResolverId,
            subscriptionName,
            cancellationToken);
    }

    public async Task<BulkOperationResult> ResubmitResolverDeadLettersAsync(
        string subscriptionName,
        bool all,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        await ValidateResolverDeadLetterTargetAsync(subscriptionName);
        return await ResolverDeadLetterClient.ResubmitAsync(
            NimBus.Core.Messages.Constants.ResolverId,
            subscriptionName,
            all,
            reason,
            cancellationToken);
    }

    private IResolverDeadLetterClient ResolverDeadLetterClient =>
        _resolverDeadLetterClient ?? throw new InvalidOperationException("Resolver dead-letter administration is not configured.");

    private async Task ValidateResolverDeadLetterTargetAsync(string subscriptionName)
    {
        const string topicName = NimBus.Core.Messages.Constants.ResolverId;
        var expected = FindExpected(topicName, subscriptionName);
        if (expected is null || !expected.RequiresSession || !string.IsNullOrEmpty(expected.ForwardTo))
        {
            throw new ResolverDeadLetterTargetNotSupportedException(subscriptionName);
        }

        var actual = await _sbManagement.GetSubscription(topicName, subscriptionName)
            ?? throw new SubscriptionNotFoundException(topicName, subscriptionName);
        if (!actual.RequiresSession || !string.IsNullOrEmpty(actual.ForwardTo))
        {
            throw new ResolverDeadLetterTargetNotSupportedException(subscriptionName);
        }
    }

    /// <summary>
    /// Pause / resume, handling auto-forwarding subscriptions explicitly.
    /// </summary>
    /// <remarks>
    /// Azure documents what happens when the <em>destination</em> of an auto-forward is
    /// disabled (the source dead-letters everything it can't deliver — not what an operator
    /// wants mid-incident), but says nothing about a <em>source</em> subscription set to
    /// <c>ReceiveDisabled</c>. So rather than rely on undocumented behaviour, pausing a
    /// forwarding subscription also detaches its <c>ForwardTo</c>: messages then simply
    /// accumulate in the subscription, which is unambiguous and is what a paused
    /// subscription should mean. Both changes go out as one update so the pause can't
    /// half-apply, and resume puts the destination back from the platform topology.
    /// </remarks>
    public async Task<SubscriptionActionResult> SetSubscriptionStatusAsync(
        string topicName, string subscriptionName, bool enable)
    {
        var result = NewResult(topicName, subscriptionName, enable ? "resume" : "pause");

        try
        {
            var settings = await _sbManagement.GetSubscription(topicName, subscriptionName)
                ?? throw new SubscriptionNotFoundException(topicName, subscriptionName);

            var expected = FindExpected(topicName, subscriptionName);

            if (enable)
            {
                // Restore forwarding only if the platform knows where it should point and
                // it isn't already pointing there.
                var restoreForwarding = expected?.ForwardTo is not null
                                        && string.IsNullOrEmpty(settings.ForwardTo);

                await _sbManagement.UpdateSubscription(
                    topicName, subscriptionName, EntityStatus.Active,
                    forwardTo: expected?.ForwardTo, changeForwardTo: restoreForwarding);

                result.Message = restoreForwarding
                    ? $"Delivery resumed and forwarding to '{expected!.ForwardTo}' restored."
                    : "Delivery resumed.";
            }
            else
            {
                var detachForwarding = !string.IsNullOrEmpty(settings.ForwardTo);

                // Refuse to detach a forwarding destination we couldn't put back — a pause
                // has to be reversible.
                if (detachForwarding && expected?.ForwardTo is null)
                {
                    result.Succeeded = false;
                    result.Message =
                        $"'{subscriptionName}' auto-forwards to '{settings.ForwardTo}', but the platform topology " +
                        "doesn't describe it, so pausing could not be undone automatically. Detach its rules instead.";
                    result.Errors.Add(result.Message);
                    return result;
                }

                await _sbManagement.UpdateSubscription(
                    topicName, subscriptionName, EntityStatus.ReceiveDisabled,
                    forwardTo: null, changeForwardTo: detachForwarding);

                result.Message = detachForwarding
                    ? $"Paused: delivery disabled and forwarding to '{settings.ForwardTo}' detached. Messages now " +
                      "collect in this subscription instead of moving on. Resume restores the destination."
                    : "Paused (ReceiveDisabled). Messages keep arriving but are not delivered.";
            }

            result.Succeeded = true;
            _logger.LogInformation("Subscription {Topic}/{Subscription} delivery {Action}",
                topicName, subscriptionName, enable ? "resumed" : "paused");
        }
        catch (SubscriptionNotFoundException)
        {
            throw;
        }
        catch (Exception exception)
        {
            Fail(result, exception, "Failed to change delivery on {Topic}/{Subscription}", topicName, subscriptionName);
        }

        return result;
    }

    // ───────────────────────── Purge ─────────────────────────

    public async Task<BulkOperationResult> PurgeSubscriptionAsync(string topicName, string subscriptionName)
    {
        var settings = await _sbManagement.GetSubscription(topicName, subscriptionName)
            ?? throw new SubscriptionNotFoundException(topicName, subscriptionName);

        if (!string.IsNullOrEmpty(settings.ForwardTo))
        {
            // Service Bus rejects receive operations on an auto-forwarding entity, so there
            // is nothing a drain loop can do here.
            throw new SubscriptionPurgeNotSupportedException(
                $"Subscription '{subscriptionName}' auto-forwards to '{settings.ForwardTo}' and cannot be drained " +
                "with a receiver. Use 'Delete & recreate' to discard its backlog.");
        }

        var errors = new List<string>();
        int removed = 0;

        // Service Bus also rejects receive on a ReceiveDisabled entity, so the advertised
        // Pause → Purge workflow would silently remove nothing. Make the subscription
        // receivable for the duration of the drain and put its status back afterwards,
        // whatever happens.
        var pausedStatus = settings.Status != EntityStatus.Active ? settings.Status : (EntityStatus?)null;
        if (pausedStatus.HasValue)
        {
            await _sbManagement.UpdateSubscription(
                topicName, subscriptionName, EntityStatus.Active,
                forwardTo: null, changeForwardTo: false);
        }

        try
        {
            if (settings.RequiresSession)
            {
                removed = await DrainSessionsAsync(topicName, subscriptionName, errors);
            }
            else
            {
                var receiver = _sbClient.CreateReceiver(topicName, subscriptionName);
                await using (receiver)
                {
                    removed = await DrainAsync(receiver);
                }
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Error draining {Topic}/{Subscription}", topicName, subscriptionName);
            errors.Add(exception.Message);
        }
        finally
        {
            if (pausedStatus.HasValue)
            {
                try
                {
                    await _sbManagement.UpdateSubscription(
                        topicName, subscriptionName, pausedStatus.Value,
                        forwardTo: null, changeForwardTo: false);
                }
                catch (Exception exception)
                {
                    // Leaving it Active is recoverable and visible in the table, but the
                    // operator has to be told the pause didn't survive.
                    _logger.LogError(exception, "Drained {Topic}/{Subscription} but could not restore status {Status}",
                        topicName, subscriptionName, pausedStatus.Value);
                    errors.Add(
                        $"Messages were drained, but the subscription could not be returned to {pausedStatus.Value} " +
                        $"and is now Active: {exception.Message}");
                }
            }
        }

        _logger.LogInformation("Purged {Count} message(s) from {Topic}/{Subscription}",
            removed, topicName, subscriptionName);

        return new BulkOperationResult
        {
            Processed = removed + errors.Count,
            Succeeded = removed,
            Failed = errors.Count,
            Errors = errors,
        };
    }

    /// <summary>
    /// Drains a session-enabled subscription. Sessions must be accepted by id, and peeking
    /// works on a session subscription without accepting anything — so discover the ids
    /// from a peek sweep first, then drain each session. (The alternative,
    /// <c>AcceptNextSessionAsync</c> in a loop, only signals "no sessions left" by burning
    /// the client's full operation timeout.)
    /// </summary>
    private async Task<int> DrainSessionsAsync(string topicName, string subscriptionName, List<string> errors)
    {
        var sessionIds = new HashSet<string>(StringComparer.Ordinal);

        var peekReceiver = _sbClient.CreateReceiver(topicName, subscriptionName);
        await using (peekReceiver)
        {
            long fromSequenceNumber = 0;
            while (true)
            {
                var peeked = await peekReceiver.PeekMessagesAsync(DrainBatchSize, fromSequenceNumber);
                if (peeked.Count == 0) break;

                foreach (var message in peeked)
                {
                    sessionIds.Add(message.SessionId ?? string.Empty);
                }

                fromSequenceNumber = peeked[peeked.Count - 1].SequenceNumber + 1;
            }
        }

        int removed = 0;
        foreach (var sessionId in sessionIds)
        {
            ServiceBusSessionReceiver receiver;
            try
            {
                receiver = await _sbClient.AcceptSessionAsync(topicName, subscriptionName, sessionId);
            }
            catch (ServiceBusException exception)
            {
                errors.Add($"Session '{sessionId}': {exception.Message}");
                continue;
            }

            await using (receiver)
            {
                try
                {
                    removed += await DrainAsync(receiver);
                }
                catch (Exception exception)
                {
                    errors.Add($"Session '{sessionId}': {exception.Message}");
                }
            }
        }

        return removed;
    }

    public async Task<BulkOperationResult> PurgeDeadLetterQueueAsync(string topicName, string subscriptionName)
    {
        _ = await _sbManagement.GetSubscription(topicName, subscriptionName)
            ?? throw new SubscriptionNotFoundException(topicName, subscriptionName);

        // No status flip here, unlike Purge: a paused subscription made receivable would hand its
        // paused backlog to consumers. If Service Bus refuses the receive, the result says so.
        var errors = new List<string>();
        int removed = 0;
        foreach (var (subQueue, queueName) in new[]
                 {
                     (SubQueue.DeadLetter, "dead-letter queue"),
                     (SubQueue.TransferDeadLetter, "transfer dead-letter queue"),
                 })
        {
            var receiver = _sbClient.CreateReceiver(topicName, subscriptionName,
                new ServiceBusReceiverOptions { SubQueue = subQueue });
            await using (receiver)
            {
                try
                {
                    await DrainAsync(receiver, () => removed++);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(exception, "Error draining the {Queue} of {Topic}/{Subscription}",
                        queueName, topicName, subscriptionName);
                    errors.Add($"The {queueName} of {subscriptionName}: {exception.Message}");
                }
            }
        }

        return new BulkOperationResult
        {
            Processed = removed + errors.Count,
            Succeeded = removed,
            Failed = errors.Count,
            Errors = errors,
        };
    }

    /// <summary>
    /// Completes every active message a receiver hands back, plus any deferred messages
    /// parked behind them, until the entity runs dry. The caller owns the receiver's lifetime.
    /// <paramref name="onRemoved"/> runs after each completion, so a caller still knows what
    /// was removed when a later completion throws.
    /// </summary>
    internal static async Task<int> DrainAsync(ServiceBusReceiver receiver, Action? onRemoved = null)
    {
        int removed = 0;

        // Deferred messages never come back from ReceiveMessagesAsync, so peek for their
        // sequence numbers first and settle them explicitly.
        long fromSequenceNumber = 0;
        var deferred = new List<long>();
        while (true)
        {
            var peeked = await receiver.PeekMessagesAsync(DrainBatchSize, fromSequenceNumber);
            if (peeked.Count == 0) break;

            deferred.AddRange(peeked
                .Where(message => message.State == ServiceBusMessageState.Deferred)
                .Select(message => message.SequenceNumber));

            fromSequenceNumber = peeked[peeked.Count - 1].SequenceNumber + 1;
        }

        foreach (var sequenceNumber in deferred)
        {
            try
            {
                var message = await receiver.ReceiveDeferredMessageAsync(sequenceNumber);
                if (message == null) continue;
                await receiver.CompleteMessageAsync(message);
                removed++;
                onRemoved?.Invoke();
            }
            catch (ServiceBusException exception) when (exception.Reason == ServiceBusFailureReason.MessageNotFound)
            {
                // Settled by someone else between the peek and the receive.
            }
        }

        while (true)
        {
            var batch = await receiver.ReceiveMessagesAsync(DrainBatchSize, DrainIdleTimeout);
            if (batch.Count == 0) break;

            foreach (var message in batch)
            {
                await receiver.CompleteMessageAsync(message);
                removed++;
                onRemoved?.Invoke();
            }
        }

        return removed;
    }

    /// <summary>
    /// Completes the messages a receiver hands back that <paramref name="matches"/> accepts and
    /// releases the rest. A message that doesn't match stays locked until the pass ends, so each
    /// receive moves on to messages not seen yet; one whose lock expired and came back again is
    /// not looked at twice. <paramref name="onCompleted"/> runs after each completion.
    /// </summary>
    internal static async Task CompleteMatchingAsync(
        ServiceBusReceiver receiver, Func<ServiceBusReceivedMessage, bool> matches, Action onCompleted)
    {
        var seen = new HashSet<long>();
        var held = new List<ServiceBusReceivedMessage>();
        try
        {
            while (true)
            {
                var batch = await receiver.ReceiveMessagesAsync(DrainBatchSize, DrainIdleTimeout);
                var fresh = batch.Where(message => seen.Add(message.SequenceNumber)).ToList();
                held.AddRange(batch.Except(fresh));
                if (fresh.Count == 0) break;

                foreach (var message in fresh)
                {
                    if (!matches(message))
                    {
                        held.Add(message);
                        continue;
                    }

                    await receiver.CompleteMessageAsync(message);
                    onCompleted();
                }
            }
        }
        finally
        {
            foreach (var message in held)
            {
                try
                {
                    await receiver.AbandonMessageAsync(message);
                }
                catch (ServiceBusException)
                {
                    // Its lock already expired, so it is back in the queue anyway.
                }
            }
        }
    }

    /// <summary>Counts, by peeking only, the messages <paramref name="matches"/> accepts.</summary>
    internal static async Task<int> CountMatchingAsync(
        ServiceBusReceiver receiver, Func<ServiceBusReceivedMessage, bool> matches)
    {
        int count = 0;
        long fromSequenceNumber = 0;
        while (true)
        {
            var peeked = await receiver.PeekMessagesAsync(DrainBatchSize, fromSequenceNumber);
            if (peeked.Count == 0) break;

            count += peeked.Count(matches);
            fromSequenceNumber = peeked[peeked.Count - 1].SequenceNumber + 1;
        }

        return count;
    }

    // ───────────────────────── Delete / recreate / rules ─────────────────────────

    public async Task<SubscriptionActionResult> DeleteSubscriptionAsync(string topicName, string subscriptionName)
    {
        var result = NewResult(topicName, subscriptionName, "delete");

        try
        {
            await _sbManagement.DeleteSubscription(topicName, subscriptionName);
            result.Succeeded = true;
            result.Message = "Subscription deleted along with every message it held.";
            _logger.LogWarning("Subscription {Topic}/{Subscription} deleted by operator", topicName, subscriptionName);
        }
        catch (Exception exception)
        {
            Fail(result, exception, "Failed to delete {Topic}/{Subscription}", topicName, subscriptionName);
        }

        return result;
    }

    public async Task<SubscriptionActionResult> RecreateSubscriptionAsync(string topicName, string subscriptionName)
    {
        var result = NewResult(topicName, subscriptionName, "recreate");

        var expected = FindExpected(topicName, subscriptionName)
            ?? throw new SubscriptionNotDescribableException(
                $"The platform has no recipe for '{topicName}/{subscriptionName}', so it cannot be rebuilt after " +
                "deletion. Delete it explicitly if you are sure, then re-create it by hand.");

        try
        {
            await _sbManagement.DeleteSubscription(topicName, subscriptionName);
        }
        catch (ServiceBusException exception) when (exception.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
        {
            // Already gone — carry on and provision it.
        }
        catch (Exception exception)
        {
            Fail(result, exception, "Failed to delete {Topic}/{Subscription} while recreating it", topicName, subscriptionName);
            return result;
        }

        try
        {
            await _rebuilder.EnsureSubscriptionAsync(topicName, expected);
            result.Succeeded = true;
            result.RulesRestored = expected.Rules.Select(rule => rule.Name).ToList();
            result.Message = expected.ForwardTo is null
                ? "Subscription recreated empty with its expected rules."
                : $"Subscription recreated empty, forwarding to '{expected.ForwardTo}' again.";

            _logger.LogWarning("Subscription {Topic}/{Subscription} recreated by operator (backlog discarded)",
                topicName, subscriptionName);
        }
        catch (Exception exception)
        {
            // The subscription is gone at this point, so make the failure loud: an operator
            // has to know the entity is missing, not just that a button failed.
            Fail(result, exception, "Deleted {Topic}/{Subscription} but failed to recreate it", topicName, subscriptionName);
            result.Message = "Subscription was deleted but could not be recreated — re-run 'nb topology apply'.";
        }

        return result;
    }

    public async Task<SubscriptionActionResult> DeleteRuleAsync(
        string topicName, string subscriptionName, string ruleName)
    {
        var result = NewResult(topicName, subscriptionName, "detach-rule");

        // Detach is advertised as reversible, and RestoreRulesAsync can only recreate rules
        // the descriptor knows. Deleting anything else — a $Default that is a subscription's
        // entire routing, or a rule on a hand-made subscription — would stop delivery with
        // no way back, so refuse rather than offer a one-way door.
        var expected = FindExpected(topicName, subscriptionName);
        var isRestorable = expected?.Rules
            .Any(rule => rule.Name.Equals(ruleName, StringComparison.OrdinalIgnoreCase)) == true;

        if (!isRestorable)
        {
            result.Succeeded = false;
            result.Message =
                $"Rule '{ruleName}' isn't part of the platform topology for '{topicName}/{subscriptionName}', so " +
                "detaching it could not be undone from here. Remove it from Topology → Catalog drift if it really is " +
                "deprecated, or pause the subscription instead.";
            result.Errors.Add(result.Message);
            return result;
        }

        try
        {
            await _sbManagement.DeleteRule(topicName, subscriptionName, ruleName);
            result.Succeeded = true;
            result.Message = $"Rule '{ruleName}' detached. No new messages will enter this subscription through it; " +
                             "whatever is already queued still drains.";
            _logger.LogWarning("Rule {Rule} detached from {Topic}/{Subscription} by operator",
                ruleName, topicName, subscriptionName);
        }
        catch (Exception exception)
        {
            Fail(result, exception, "Failed to detach a rule from {Topic}/{Subscription}", topicName, subscriptionName);
        }

        return result;
    }

    public async Task<SubscriptionActionResult> RestoreRulesAsync(string topicName, string subscriptionName)
    {
        var result = NewResult(topicName, subscriptionName, "restore-rules");

        var expected = FindExpected(topicName, subscriptionName)
            ?? throw new SubscriptionNotDescribableException(
                $"The platform has no recipe for '{topicName}/{subscriptionName}', so there are no rules to restore.");

        var existing = new List<string>();
        await foreach (var rule in _sbManagement.ListRulesAsync(topicName, subscriptionName))
        {
            existing.Add(rule.Name);
        }

        var missing = expected.Rules
            .Where(rule => !existing.Any(actual => actual.Equals(rule.Name, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var rule in missing)
        {
            try
            {
                await _sbManagement.CreateCustomRule(topicName, subscriptionName, rule.Name, rule.Filter, rule.Action);
                result.RulesRestored.Add(rule.Name);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed to restore a rule on {Topic}/{Subscription}",
                    topicName, subscriptionName);
                result.Errors.Add($"{rule.Name}: {exception.Message}");
            }
        }

        result.Succeeded = result.Errors.Count == 0;
        result.Message = missing.Count == 0
            ? "Nothing to restore — every expected rule is already attached."
            : $"Restored {result.RulesRestored.Count} of {missing.Count} missing rule(s).";

        return result;
    }

    // ───────────────────────── Helpers ─────────────────────────

    private ExpectedSubscription? FindExpected(string topicName, string subscriptionName) =>
        TopologyDescriptor.FindSubscription(topicName, subscriptionName, _platform, _isEmulator);

    private static SubscriptionActionResult NewResult(string topicName, string subscriptionName, string action) =>
        new()
        {
            TopicName = topicName,
            SubscriptionName = subscriptionName,
            Action = action,
            Succeeded = false,
            Message = string.Empty,
            RulesRestored = new List<string>(),
            Errors = new List<string>(),
        };

    private void Fail(SubscriptionActionResult result, Exception exception, string message, params object[] args)
    {
        _logger.LogWarning(exception, message, args);
        result.Succeeded = false;
        result.Message = exception.Message;
        result.Errors.Add(exception.Message);
    }
}

/// <summary>Thrown when the named subscription doesn't exist on the topic.</summary>
public class ResolverDeadLetterTargetNotSupportedException : Exception
{
    public ResolverDeadLetterTargetNotSupportedException(string subscriptionName)
        : base($"Subscription '{subscriptionName}' is not a terminal, session-enabled Resolver subscription.")
    {
    }
}

public class SubscriptionNotFoundException : Exception
{
    public SubscriptionNotFoundException(string topicName, string subscriptionName)
        : base($"Subscription '{subscriptionName}' not found on topic '{topicName}'.")
    {
    }

    public SubscriptionNotFoundException() { }

    public SubscriptionNotFoundException(string message) : base(message) { }

    public SubscriptionNotFoundException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>Thrown when a subscription can't be drained with a receiver.</summary>
public class SubscriptionPurgeNotSupportedException : Exception
{
    public SubscriptionPurgeNotSupportedException(string message) : base(message) { }

    public SubscriptionPurgeNotSupportedException() { }

    public SubscriptionPurgeNotSupportedException(string message, Exception innerException) : base(message, innerException) { }
}

/// <summary>
/// Thrown when the platform topology has no recipe for a subscription, so it must not be
/// offered a "recreate" that would leave the entity missing.
/// </summary>
public class SubscriptionNotDescribableException : Exception
{
    public SubscriptionNotDescribableException(string message) : base(message) { }

    public SubscriptionNotDescribableException() { }

    public SubscriptionNotDescribableException(string message, Exception innerException) : base(message, innerException) { }
}
