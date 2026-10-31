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
/// A <see cref="ServiceBusClient"/> over one session-enabled subscription, with just enough
/// behaviour for Operations → Purge subscription: peek, accept a session, receive active and
/// deferred messages, complete and abandon.
/// </summary>
internal sealed class FakePurgeBus : ServiceBusClient
{
    private readonly List<ServiceBusReceivedMessage> _messages = new();
    private long _nextSequenceNumber = 1;

    /// <summary>Sessions that cannot be accepted.</summary>
    public HashSet<string> LockedSessions { get; } = new();

    /// <summary>Deferred sequence numbers that are gone by the time the purge receives them.</summary>
    public HashSet<long> MissingDeferred { get; } = new();

    /// <summary>Sequence numbers whose completion throws.</summary>
    public HashSet<long> FailCompletion { get; } = new();

    /// <summary>Sequence numbers of the messages the purge completed.</summary>
    public List<long> Completed { get; } = new();

    /// <summary>Sequence numbers of the messages the purge abandoned.</summary>
    public List<long> Abandoned { get; } = new();

    /// <summary>
    /// Adds a message to the subscription, carrying <paramref name="eventId"/> as its EventId
    /// property (none when null), and returns its sequence number.
    /// </summary>
    public long Add(
        string sessionId,
        string? eventId,
        ServiceBusMessageState state = ServiceBusMessageState.Active,
        DateTimeOffset? enqueuedTime = null)
    {
        var properties = new Dictionary<string, object>();
        if (eventId is not null)
            properties["EventId"] = eventId;

        var sequenceNumber = _nextSequenceNumber++;
        _messages.Add(ServiceBusModelFactory.ServiceBusReceivedMessage(
            messageId: "msg-" + sequenceNumber,
            sessionId: sessionId,
            properties: properties,
            sequenceNumber: sequenceNumber,
            enqueuedTime: enqueuedTime ?? DateTimeOffset.UtcNow.AddHours(-1),
            serviceBusMessageState: state));
        return sequenceNumber;
    }

    public override ServiceBusReceiver CreateReceiver(string topicName, string subscriptionName) =>
        new PeekReceiver(this);

    public override Task<ServiceBusSessionReceiver> AcceptSessionAsync(
        string topicName,
        string subscriptionName,
        string sessionId,
        ServiceBusSessionReceiverOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (LockedSessions.Contains(sessionId))
            throw new ServiceBusException("Session locked", ServiceBusFailureReason.SessionCannotBeLocked);
        return Task.FromResult<ServiceBusSessionReceiver>(new SessionReceiver(this, sessionId));
    }

    [SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Test double avoids disposing uninitialized SDK internals.")]
    private sealed class PeekReceiver(FakePurgeBus bus) : ServiceBusReceiver
    {
        public override Task<IReadOnlyList<ServiceBusReceivedMessage>> PeekMessagesAsync(
            int maxMessages, long? fromSequenceNumber = null, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ServiceBusReceivedMessage> page = bus._messages
                .Where(message => message.SequenceNumber >= (fromSequenceNumber ?? 0))
                .Take(maxMessages)
                .ToList();
            return Task.FromResult(page);
        }

        public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Test double avoids disposing uninitialized SDK internals.")]
    private sealed class SessionReceiver(FakePurgeBus bus, string sessionId) : ServiceBusSessionReceiver
    {
        private readonly HashSet<long> _locked = new();

        public override Task<ServiceBusReceivedMessage> ReceiveDeferredMessageAsync(
            long sequenceNumber, CancellationToken cancellationToken = default)
        {
            var message = bus._messages.FirstOrDefault(m =>
                m.SequenceNumber == sequenceNumber
                && m.SessionId == sessionId
                && m.State == ServiceBusMessageState.Deferred);
            if (message is null || bus.MissingDeferred.Contains(sequenceNumber))
                throw new ServiceBusException("Not found", ServiceBusFailureReason.MessageNotFound);
            return Task.FromResult(message);
        }

        public override Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveMessagesAsync(
            int maxMessages, TimeSpan? maxWaitTime = null, CancellationToken cancellationToken = default)
        {
            IReadOnlyList<ServiceBusReceivedMessage> batch = bus._messages
                .Where(m => m.SessionId == sessionId
                    && m.State == ServiceBusMessageState.Active
                    && !_locked.Contains(m.SequenceNumber))
                .Take(maxMessages)
                .ToList();
            foreach (var message in batch)
                _locked.Add(message.SequenceNumber);
            return Task.FromResult(batch);
        }

        public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
        {
            if (bus.FailCompletion.Contains(message.SequenceNumber))
                return Task.FromException(new ServiceBusException("Lock lost", ServiceBusFailureReason.MessageLockLost));

            bus._messages.RemoveAll(m => m.SequenceNumber == message.SequenceNumber);
            _locked.Remove(message.SequenceNumber);
            bus.Completed.Add(message.SequenceNumber);
            return Task.CompletedTask;
        }

        public override Task AbandonMessageAsync(
            ServiceBusReceivedMessage message,
            IDictionary<string, object>? propertiesToModify = null,
            CancellationToken cancellationToken = default)
        {
            _locked.Remove(message.SequenceNumber);
            bus.Abandoned.Add(message.SequenceNumber);
            return Task.CompletedTask;
        }

        public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
