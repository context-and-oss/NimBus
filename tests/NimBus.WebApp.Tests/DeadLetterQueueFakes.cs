#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;

namespace NimBus.WebApp.Tests;

/// <summary>
/// A <see cref="ServiceBusClient"/> whose receivers are one subscription's two dead-letter
/// queues. Records which entity and sub-queue each receiver was created for.
/// </summary>
internal sealed class FakeDeadLetterBus : ServiceBusClient
{
    public FakeDeadLetterQueue DeadLetters { get; } = new();

    public FakeDeadLetterQueue TransferDeadLetters { get; } = new();

    public List<(string Topic, string Subscription, SubQueue SubQueue)> Receivers { get; } = new();

    public override ServiceBusReceiver CreateReceiver(string topicName, string subscriptionName, ServiceBusReceiverOptions options)
    {
        Receivers.Add((topicName, subscriptionName, options.SubQueue));
        return options.SubQueue switch
        {
            SubQueue.DeadLetter => DeadLetters,
            SubQueue.TransferDeadLetter => TransferDeadLetters,
            _ => throw new InvalidOperationException("Only the dead-letter queues are faked."),
        };
    }
}

/// <summary>
/// The parts of a dead-letter queue a drain relies on: peek by sequence number, receive skips
/// messages another receive still holds, complete removes, abandon releases.
/// </summary>
[SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Test double avoids disposing uninitialized SDK internals.")]
internal sealed class FakeDeadLetterQueue : ServiceBusReceiver
{
    private readonly List<ServiceBusReceivedMessage> _messages = new();
    private readonly HashSet<long> _locked = new();
    private long _nextSequenceNumber = 1;
    private int _completions;
    private int _receives;

    public int Completed => _completions;

    public int Abandoned { get; private set; }

    /// <summary>Thrown by every peek and receive, as an unreachable or disabled entity would.</summary>
    public ServiceBusException? Failure { get; set; }

    /// <summary>The completion, counted from 1, that throws instead of removing the message.</summary>
    public int? FailCompletion { get; set; }

    /// <summary>The most messages one receive hands back, below whatever the caller asks for.</summary>
    public int? MaxBatch { get; set; }

    /// <summary>
    /// The receive, counted from 1, before which every lock a receive took has expired, so those
    /// messages are handed out again, oldest first, as Service Bus does.
    /// </summary>
    public int? ExpireLocksBeforeReceive { get; set; }

    /// <summary>
    /// The receive, counted from 1, that comes back empty although messages are waiting, as a
    /// Service Bus receive can.
    /// </summary>
    public int? EmptyReceive { get; set; }

    /// <summary>Event ids of the messages still in the queue, oldest first.</summary>
    public IReadOnlyList<string> RemainingEventIds =>
        _messages.Select(message => (string)message.ApplicationProperties["EventId"]).ToList();

    /// <summary>Dead-letters one message per event id, carrying it as the EventId property.</summary>
    public void Add(params string[] eventIds)
    {
        foreach (var eventId in eventIds)
        {
            _messages.Add(ServiceBusModelFactory.ServiceBusReceivedMessage(
                messageId: "dlq-" + eventId,
                sequenceNumber: _nextSequenceNumber++,
                properties: new Dictionary<string, object> { ["EventId"] = eventId }));
        }
    }

    public override Task<IReadOnlyList<ServiceBusReceivedMessage>> PeekMessagesAsync(
        int maxMessages, long? fromSequenceNumber = null, CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
            return Task.FromException<IReadOnlyList<ServiceBusReceivedMessage>>(Failure);

        IReadOnlyList<ServiceBusReceivedMessage> page = _messages
            .Where(message => message.SequenceNumber >= (fromSequenceNumber ?? 0))
            .Take(maxMessages)
            .ToList();
        return Task.FromResult(page);
    }

    public override Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveMessagesAsync(
        int maxMessages, TimeSpan? maxWaitTime = null, CancellationToken cancellationToken = default)
    {
        if (Failure is not null)
            return Task.FromException<IReadOnlyList<ServiceBusReceivedMessage>>(Failure);

        _receives++;
        if (_receives == ExpireLocksBeforeReceive)
            _locked.Clear();
        if (_receives == EmptyReceive)
            return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(Array.Empty<ServiceBusReceivedMessage>());

        IReadOnlyList<ServiceBusReceivedMessage> batch = _messages
            .Where(message => !_locked.Contains(message.SequenceNumber))
            .Take(Math.Min(maxMessages, MaxBatch ?? int.MaxValue))
            .ToList();
        foreach (var message in batch)
            _locked.Add(message.SequenceNumber);
        return Task.FromResult(batch);
    }

    public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
    {
        if (++_completions == FailCompletion)
        {
            _completions--;
            return Task.FromException(new ServiceBusException("Lock lost", ServiceBusFailureReason.MessageLockLost));
        }

        _messages.RemoveAll(queued => queued.SequenceNumber == message.SequenceNumber);
        _locked.Remove(message.SequenceNumber);
        return Task.CompletedTask;
    }

    public override Task AbandonMessageAsync(
        ServiceBusReceivedMessage message,
        IDictionary<string, object>? propertiesToModify = null,
        CancellationToken cancellationToken = default)
    {
        _locked.Remove(message.SequenceNumber);
        Abandoned++;
        return Task.CompletedTask;
    }

    public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
