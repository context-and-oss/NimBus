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
/// A <see cref="ServiceBusClient"/> with per-session state and queued messages on any
/// subscription, for Operations → Session state: accept a session, read and write its state,
/// and peek its messages.
/// </summary>
internal sealed class FakeSessionStateBus : ServiceBusClient
{
    private readonly Dictionary<(string Subscription, string SessionId), BinaryData?> _states = new();
    private readonly Dictionary<(string Subscription, string SessionId), int> _messages = new();
    private readonly object _gate = new();

    /// <summary>Sessions, by subscription, that a receiver holds.</summary>
    public HashSet<(string Subscription, string SessionId)> Locked { get; } = new();

    /// <summary>Every state write, in order.</summary>
    public List<(string Subscription, string SessionId, BinaryData? State)> Writes { get; } = new();

    public void SetState(string subscription, string sessionId, BinaryData? state)
    {
        lock (_gate)
            _states[(subscription, sessionId)] = state;
    }

    public BinaryData? GetState(string subscription, string sessionId)
    {
        lock (_gate)
            return _states.GetValueOrDefault((subscription, sessionId));
    }

    public void AddMessages(string subscription, string sessionId, int count)
    {
        lock (_gate)
            _messages[(subscription, sessionId)] = _messages.GetValueOrDefault((subscription, sessionId)) + count;
    }

    public override Task<ServiceBusSessionReceiver> AcceptSessionAsync(
        string topicName,
        string subscriptionName,
        string sessionId,
        ServiceBusSessionReceiverOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        if (Locked.Contains((subscriptionName, sessionId)))
            throw new ServiceBusException("Session locked", ServiceBusFailureReason.SessionCannotBeLocked);
        return Task.FromResult<ServiceBusSessionReceiver>(new Receiver(this, subscriptionName, sessionId));
    }

    [SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Test double avoids disposing uninitialized SDK internals.")]
    private sealed class Receiver(FakeSessionStateBus bus, string subscription, string sessionId) : ServiceBusSessionReceiver
    {
        public override Task<BinaryData> GetSessionStateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(bus.GetState(subscription, sessionId)!);

        public override Task SetSessionStateAsync(BinaryData sessionState, CancellationToken cancellationToken = default)
        {
            lock (bus._gate)
            {
                bus._states[(subscription, sessionId)] = sessionState;
                bus.Writes.Add((subscription, sessionId, sessionState));
            }
            return Task.CompletedTask;
        }

        public override Task<IReadOnlyList<ServiceBusReceivedMessage>> PeekMessagesAsync(
            int maxMessages, long? fromSequenceNumber = null, CancellationToken cancellationToken = default)
        {
            int count;
            lock (bus._gate)
                count = Math.Min(maxMessages, bus._messages.GetValueOrDefault((subscription, sessionId)));
            IReadOnlyList<ServiceBusReceivedMessage> page = Enumerable.Range(1, count)
                .Select(i => ServiceBusModelFactory.ServiceBusReceivedMessage(sessionId: sessionId, sequenceNumber: i))
                .ToList();
            return Task.FromResult(page);
        }

        public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

        public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
