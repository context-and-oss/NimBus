using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Transactions;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Services;

public interface IResolverDeadLetterClient
{
    Task<DeadLetterOverview> GetOverviewAsync(string topicName, string subscriptionName, CancellationToken cancellationToken = default);

    Task<BulkOperationResult> ResubmitAsync(
        string topicName,
        string subscriptionName,
        bool all,
        string? reason,
        CancellationToken cancellationToken = default);
}

public sealed class ResolverReplayInProgressException : Exception
{
    public ResolverReplayInProgressException(string subscriptionName)
        : base($"A dead-letter replay is already running for subscription '{subscriptionName}'.")
    {
    }
}

/// <summary>
/// Browses and atomically replays the regular dead-letter queue of a Resolver subscription.
/// </summary>
public sealed partial class ResolverDeadLetterClient : IResolverDeadLetterClient, IAsyncDisposable
{
    internal const int MaxReplaySnapshotMessages = 500;
    private const int ReceiveBatchSize = 100;
    private static readonly TimeSpan ReceiveWait = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan OperationBudget = TimeSpan.FromSeconds(180);
    private static readonly TimeSpan LockRenewalInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LockRenewalWindow = TimeSpan.FromSeconds(30);

    private readonly ServiceBusClient _client;
    private readonly ILogger<ResolverDeadLetterClient> _logger;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.OrdinalIgnoreCase);

    public ResolverDeadLetterClient(ServiceBusClient client, ILogger<ResolverDeadLetterClient> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<DeadLetterOverview> GetOverviewAsync(
        string topicName,
        string subscriptionName,
        CancellationToken cancellationToken = default)
    {
        await using var receiver = CreateReceiver(topicName, subscriptionName);
        var snapshot = await PeekSnapshotAsync(receiver, cancellationToken);
        var bounded = snapshot.Take(MaxReplaySnapshotMessages).ToList();

        return new DeadLetterOverview
        {
            TotalMessageCount = bounded.Count,
            IsTruncated = snapshot.Count > MaxReplaySnapshotMessages,
            SnapshotLimit = MaxReplaySnapshotMessages,
            Reasons = bounded
                .GroupBy(message => message.Reason, StringComparer.Ordinal)
                .Select(group => new DeadLetterReasonCount { Reason = group.Key, Count = group.LongCount() })
                .OrderByDescending(item => item.Count)
                .ThenBy(item => item.Reason, StringComparer.Ordinal)
                .ToList(),
        };
    }

    public async Task<BulkOperationResult> ResubmitAsync(
        string topicName,
        string subscriptionName,
        bool all,
        string? reason,
        CancellationToken cancellationToken = default)
    {
        var gateKey = $"{topicName}/{subscriptionName}";
        var gate = _gates.GetOrAdd(gateKey, static _ => new SemaphoreSlim(1, 1));
        if (!await gate.WaitAsync(0, cancellationToken))
        {
            throw new ResolverReplayInProgressException(subscriptionName);
        }

        try
        {
            return await ResubmitCoreAsync(topicName, subscriptionName, all, reason, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<BulkOperationResult> ResubmitCoreAsync(
        string topicName,
        string subscriptionName,
        bool all,
        string? reason,
        CancellationToken cancellationToken)
    {
        using var budget = new CancellationTokenSource(OperationBudget);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, budget.Token);
        await using var receiver = CreateReceiver(topicName, subscriptionName);
        IReadOnlyList<SnapshotEntry> snapshot;
        try
        {
            snapshot = await PeekSnapshotAsync(receiver, linked.Token);
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            return Result(0, 0, []);
        }

        var selected = snapshot
            .Take(MaxReplaySnapshotMessages)
            .Where(message => all || string.Equals(message.Reason, reason, StringComparison.Ordinal))
            .Select(message => message.SequenceNumber)
            .ToHashSet();
        var pending = new HashSet<long>(selected);
        var held = new ConcurrentDictionary<long, ServiceBusReceivedMessage>();
        var errors = new List<string>();
        var succeeded = 0;
        var failed = 0;

        if (pending.Count == 0)
        {
            return Result(0, 0, errors);
        }

        var boundary = selected.Max();

        await using var sender = _client.CreateSender(topicName);
        using var renewalCancellation = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
        var renewalTask = RenewHeldLocksAsync(receiver, held, renewalCancellation.Token);

        try
        {
            var reachedBoundary = false;
            while (pending.Count > 0 && !budget.IsCancellationRequested && !reachedBoundary)
            {
                var batch = await receiver.ReceiveMessagesAsync(
                    Math.Min(ReceiveBatchSize, pending.Count + held.Count + 1),
                    ReceiveWait,
                    linked.Token);
                if (batch.Count == 0)
                {
                    break;
                }

                foreach (var message in batch)
                {
                    if (message.SequenceNumber > boundary)
                    {
                        held.TryAdd(message.SequenceNumber, message);
                        reachedBoundary = true;
                        continue;
                    }

                    if (!pending.Remove(message.SequenceNumber))
                    {
                        held.TryAdd(message.SequenceNumber, message);
                        continue;
                    }

                    try
                    {
                        using (var transaction = new TransactionScope(
                                   TransactionScopeOption.Required,
                                   TransactionScopeAsyncFlowOption.Enabled))
                        {
                            await receiver.CompleteMessageAsync(message, linked.Token);
                            await sender.SendMessageAsync(CloneForReplay(message), linked.Token);
                            transaction.Complete();
                        }

                        succeeded++;
                    }
                    catch (OperationCanceledException) when (linked.IsCancellationRequested)
                    {
                        held.TryAdd(message.SequenceNumber, message);
                        throw;
                    }
                    catch (Exception exception)
                    {
                        held.TryAdd(message.SequenceNumber, message);
                        failed++;
                        errors.Add($"Sequence {message.SequenceNumber} could not be replayed.");
                        LogResolverDeadLetterReplayFailed(_logger, exception, message.SequenceNumber, topicName, subscriptionName);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Return a bounded partial result. Caller cancellation is deliberately not swallowed.
        }
        finally
        {
            renewalCancellation.Cancel();
            await renewalTask;
            foreach (var message in held.Values)
            {
                try
                {
                    await receiver.AbandonMessageAsync(message, cancellationToken: CancellationToken.None);
                }
                catch (Exception exception)
                {
                    LogCouldNotReleaseHeldResolverDead(_logger, exception, message.SequenceNumber);
                }
            }
        }

        foreach (var sequenceNumber in pending)
        {
            failed++;
            errors.Add($"Sequence {sequenceNumber} was not available for replay.");
        }

        return Result(succeeded, failed, errors);
    }

    private ServiceBusReceiver CreateReceiver(string topicName, string subscriptionName) =>
        _client.CreateReceiver(topicName, subscriptionName, new ServiceBusReceiverOptions
        {
            ReceiveMode = ServiceBusReceiveMode.PeekLock,
            SubQueue = SubQueue.DeadLetter,
        });

    private static async Task<IReadOnlyList<SnapshotEntry>> PeekSnapshotAsync(
        ServiceBusReceiver receiver,
        CancellationToken cancellationToken)
    {
        var snapshot = new List<SnapshotEntry>(MaxReplaySnapshotMessages + 1);
        long? fromSequenceNumber = 0;

        while (snapshot.Count <= MaxReplaySnapshotMessages)
        {
            var requested = Math.Min(ReceiveBatchSize, MaxReplaySnapshotMessages + 1 - snapshot.Count);
            var batch = await receiver.PeekMessagesAsync(requested, fromSequenceNumber, cancellationToken);
            if (batch.Count == 0)
            {
                break;
            }

            snapshot.AddRange(batch.Select(message => new SnapshotEntry(
                message.SequenceNumber,
                message.DeadLetterReason)));
            if (batch.Count < requested || batch[^1].SequenceNumber == long.MaxValue)
            {
                break;
            }

            fromSequenceNumber = batch[^1].SequenceNumber + 1;
        }

        return snapshot;
    }

    private async Task RenewHeldLocksAsync(
        ServiceBusReceiver receiver,
        ConcurrentDictionary<long, ServiceBusReceivedMessage> held,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(LockRenewalInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken))
            {
                var renewalThreshold = DateTimeOffset.UtcNow + LockRenewalWindow;
                foreach (var message in held.Values.Where(message => message.LockedUntil <= renewalThreshold))
                {
                    try
                    {
                        await receiver.RenewMessageLockAsync(message, cancellationToken);
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        return;
                    }
                    catch (Exception exception)
                    {
                        LogCouldNotRenewHeldResolverDead(_logger, exception, message.SequenceNumber);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal operation shutdown; held messages are abandoned by the caller.
        }
    }

    internal static ServiceBusMessage CloneForReplay(ServiceBusReceivedMessage source)
    {
        // The replay keeps the source MessageId (Spec 030 §5.7). A replayed control request can
        // land after the outcome it asked for, and the tracking store's stale-write guard
        // recognises an already-answered message by that id; a fresh GUID would reopen the
        // settled row permanently. DeadLetterOriginalMessageId is kept for continuity with
        // replays made before this change.
        var replay = new ServiceBusMessage(source);
        replay.ApplicationProperties.Remove("DeadLetterReason");
        replay.ApplicationProperties.Remove("DeadLetterErrorDescription");
        // A replay starts a fresh throttle budget. A message dead-lettered as CosmosDbThrottled
        // carries the ThrottleRetryCount that exhausted the old one, and would otherwise be
        // dead-lettered again on its first throttle.
        replay.ApplicationProperties.Remove("ThrottleRetryCount");
        replay.ApplicationProperties["DeadLetterOriginalMessageId"] = source.MessageId ?? string.Empty;
        replay.ApplicationProperties["DeadLetterOriginalReason"] = source.DeadLetterReason ?? string.Empty;
        return replay;
    }

    private static BulkOperationResult Result(int succeeded, int failed, ICollection<string> errors) => new()
    {
        Processed = succeeded + failed,
        Succeeded = succeeded,
        Failed = failed,
        Errors = errors.ToList(),
    };

    private readonly record struct SnapshotEntry(long SequenceNumber, string? Reason);

    public ValueTask DisposeAsync() => _client.DisposeAsync();

    [LoggerMessage(Level = LogLevel.Error, Message = "Resolver dead-letter replay failed for sequence {SequenceNumber} on {TopicName}/{SubscriptionName}")]
    private static partial void LogResolverDeadLetterReplayFailed(ILogger logger, Exception exception, long sequenceNumber, string topicName, string subscriptionName);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not release held Resolver dead-letter sequence {SequenceNumber}")]
    private static partial void LogCouldNotReleaseHeldResolverDead(ILogger logger, Exception exception, long sequenceNumber);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Could not renew held Resolver dead-letter sequence {SequenceNumber}")]
    private static partial void LogCouldNotRenewHeldResolverDead(ILogger logger, Exception exception, long sequenceNumber);
}
