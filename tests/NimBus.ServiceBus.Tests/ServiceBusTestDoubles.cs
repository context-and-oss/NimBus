using Azure.Messaging.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using NimBus.Core.Events;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

internal static class ServiceBusTestDoubles
{
    public static ServiceBusMessageActions CreateMessageActions() =>
        (ServiceBusMessageActions)RuntimeHelpers.GetUninitializedObject(typeof(ServiceBusMessageActions));

    public static ServiceBusSessionMessageActions CreateSessionActions() =>
        (ServiceBusSessionMessageActions)RuntimeHelpers.GetUninitializedObject(typeof(ServiceBusSessionMessageActions));
}

/// <summary>
/// Session actions that support session-state reads and writes (empty state, writes ignored),
/// unlike the uninitialized object from <see cref="ServiceBusTestDoubles.CreateSessionActions"/>.
/// </summary>
internal sealed class StateCapableSessionActions : ServiceBusSessionMessageActions
{
    public override Task<BinaryData> GetSessionStateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(BinaryData.Empty);

    public override Task SetSessionStateAsync(BinaryData sessionState, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}

internal sealed class RecordingServiceBusClient : ServiceBusClient
{
    public RecordingServiceBusSessionReceiver SessionReceiver { get; } = new();
    public RecordingServiceBusSender Sender { get; } = new();
    public Exception? CreateSenderException { get; set; }
    public Exception? AcceptSessionException { get; set; }

    public string? LastSenderEntityPath { get; private set; }
    public string? LastQueueName { get; private set; }
    public string? LastTopicName { get; private set; }
    public string? LastSubscriptionName { get; private set; }
    public string? LastSessionId { get; private set; }

    public override ServiceBusSender CreateSender(string queueOrTopicName)
    {
        LastSenderEntityPath = queueOrTopicName;
        if (CreateSenderException != null)
        {
            throw CreateSenderException;
        }

        return Sender;
    }

    public override ServiceBusSender CreateSender(string queueOrTopicName, ServiceBusSenderOptions options)
    {
        LastSenderEntityPath = queueOrTopicName;
        if (CreateSenderException != null)
        {
            throw CreateSenderException;
        }

        return Sender;
    }

    public override Task<ServiceBusSessionReceiver> AcceptSessionAsync(string queueName, string sessionId, ServiceBusSessionReceiverOptions options, CancellationToken cancellationToken)
    {
        LastQueueName = queueName;
        LastSessionId = sessionId;
        LastTopicName = null;
        LastSubscriptionName = null;
        if (AcceptSessionException != null)
            throw AcceptSessionException;
        return Task.FromResult<ServiceBusSessionReceiver>(SessionReceiver);
    }

    public override Task<ServiceBusSessionReceiver> AcceptSessionAsync(string topicName, string subscriptionName, string sessionId, ServiceBusSessionReceiverOptions options, CancellationToken cancellationToken)
    {
        LastTopicName = topicName;
        LastSubscriptionName = subscriptionName;
        LastSessionId = sessionId;
        LastQueueName = null;
        if (AcceptSessionException != null)
            throw AcceptSessionException;
        return Task.FromResult<ServiceBusSessionReceiver>(SessionReceiver);
    }
}

internal sealed class CreateSenderProbeException : Exception
{
    public CreateSenderProbeException(string message) : base(message) { }
}

[SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Test double avoids disposing uninitialized SDK internals.")]
internal sealed class RecordingServiceBusSender : ServiceBusSender
{
    public List<Azure.Messaging.ServiceBus.ServiceBusMessage> SentMessages { get; } = new();
    public List<(Azure.Messaging.ServiceBus.ServiceBusMessage Message, DateTimeOffset ScheduledEnqueueTime)> ScheduledMessages { get; } = new();

    /// <summary>
    /// Test hook invoked after a message is recorded as sent. Useful for triggering
    /// side effects (e.g. cancelling a CancellationTokenSource) mid-stream.
    /// </summary>
    public Action<Azure.Messaging.ServiceBus.ServiceBusMessage>? OnSent { get; set; }

    public override Task SendMessageAsync(Azure.Messaging.ServiceBus.ServiceBusMessage message, CancellationToken cancellationToken = default)
    {
        SentMessages.Add(message);
        OnSent?.Invoke(message);
        return Task.CompletedTask;
    }

    public override Task SendMessagesAsync(IEnumerable<Azure.Messaging.ServiceBus.ServiceBusMessage> messages, CancellationToken cancellationToken = default)
    {
        SentMessages.AddRange(messages);
        return Task.CompletedTask;
    }

    public override Task<long> ScheduleMessageAsync(Azure.Messaging.ServiceBus.ServiceBusMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        ScheduledMessages.Add((message, scheduledEnqueueTime));
        return Task.FromResult(1L);
    }

    public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

[SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Test double avoids disposing uninitialized SDK internals.")]
internal sealed class RecordingServiceBusSessionReceiver : ServiceBusSessionReceiver
{
    public IReadOnlyList<ServiceBusReceivedMessage> DeferredMessagesToReturn { get; set; } = Array.Empty<ServiceBusReceivedMessage>();
    public IReadOnlyList<long> LastDeferredSequenceNumbers { get; private set; } = Array.Empty<long>();

    // Batch-receive support for DeferredMessageProcessor tests
    public List<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveBatches { get; set; } = new();
    private int _receiveBatchIndex;

    /// <summary>
    /// Messages that become receivable while a test runs, such as a replay the endpoint
    /// parks again mid-drain. Once the configured batches are used up, a receive returns
    /// what is available, like Service Bus does.
    /// </summary>
    public List<ServiceBusReceivedMessage> Available { get; } = new();
    public List<ServiceBusReceivedMessage> CompletedMessages { get; } = new();
    public List<ServiceBusReceivedMessage> AbandonedMessages { get; } = new();
    public Exception? ReceiveMessagesException { get; set; }

    /// <summary>
    /// The subscription's lock duration: what the session is locked for when it is accepted
    /// and after each renewal.
    /// </summary>
    public TimeSpan LockDuration { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Simulated time each completion takes. The session lock runs down by this much per
    /// completion, and once it has run out, receives and settlements fail with
    /// <see cref="ServiceBusFailureReason.SessionLockLost"/>, like they do on Service Bus.
    /// </summary>
    public TimeSpan ElapsedPerCompletion { get; set; } = TimeSpan.Zero;

    public int SessionLockRenewals { get; private set; }

    private TimeSpan? _lockRemaining;

    private TimeSpan LockRemaining => _lockRemaining ?? LockDuration;

    public override DateTimeOffset SessionLockedUntil => DateTimeOffset.UtcNow + LockRemaining;

    public override Task RenewSessionLockAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfSessionLockLost();
        _lockRemaining = LockDuration;
        SessionLockRenewals++;
        return Task.CompletedTask;
    }

    private void ThrowIfSessionLockLost()
    {
        if (LockRemaining <= TimeSpan.Zero)
            throw new ServiceBusException("The session lock was lost.", ServiceBusFailureReason.SessionLockLost);
    }

    public override Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveDeferredMessagesAsync(IEnumerable<long> sequenceNumbers, CancellationToken cancellationToken = default)
    {
        LastDeferredSequenceNumbers = sequenceNumbers.ToArray();
        return Task.FromResult(DeferredMessagesToReturn);
    }

    public override Task<IReadOnlyList<ServiceBusReceivedMessage>> ReceiveMessagesAsync(int maxMessages, TimeSpan? maxWaitTime, CancellationToken cancellationToken = default)
    {
        ThrowIfSessionLockLost();
        if (_receiveBatchIndex >= ReceiveBatches.Count && ReceiveMessagesException != null)
            throw ReceiveMessagesException;
        if (_receiveBatchIndex < ReceiveBatches.Count)
            return Task.FromResult(ReceiveBatches[_receiveBatchIndex++]);
        if (Available.Count > 0)
        {
            var batch = Available.Take(maxMessages).ToList();
            Available.RemoveRange(0, batch.Count);
            return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(batch);
        }
        return Task.FromResult<IReadOnlyList<ServiceBusReceivedMessage>>(Array.Empty<ServiceBusReceivedMessage>());
    }

    public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default)
    {
        _lockRemaining = LockRemaining - ElapsedPerCompletion;
        ThrowIfSessionLockLost();
        CompletedMessages.Add(message);
        return Task.CompletedTask;
    }

    public override Task AbandonMessageAsync(
        ServiceBusReceivedMessage message,
        IDictionary<string, object>? propertiesToModify = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfSessionLockLost();
        AbandonedMessages.Add(message);
        return Task.CompletedTask;
    }

    public override Task CloseAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public override ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class TestEvent : Event
{
    public string SessionIdValue { get; set; } = "session-1";
    public string Payload { get; set; } = "payload";

    public override string GetSessionId() => SessionIdValue;
}
