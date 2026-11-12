using NimBus.Core.Messages;
using NimBus.ServiceBus;
using Azure.Messaging.ServiceBus;
using System.Collections.Concurrent;

namespace NimBus.EndToEnd.Tests.Infrastructure;

/// <summary>
/// In-memory ISender that captures published messages and can deliver them
/// to a subscriber via a ServiceBusAdapter, simulating the Azure Service Bus transport.
/// </summary>
internal sealed class InMemoryBus : ISender
{
    private readonly ConcurrentQueue<IMessage> _messages = new();
    private readonly List<IMessage> _allSentMessages = new();
    private readonly List<(IMessage Message, int EnqueueDelay)> _sentMessagesWithDelay = new();
    private readonly ConcurrentDictionary<string, FakeServiceBusSession> _sessionsBySessionId = new();
    private readonly object _lock = new();

    public IReadOnlyList<IMessage> SentMessages
    {
        get { lock (_lock) { return _allSentMessages.ToList(); } }
    }

    /// <summary>Messages with their enqueue delay (in minutes), for verifying retry backoff delays.</summary>
    public IReadOnlyList<(IMessage Message, int EnqueueDelay)> SentMessagesWithDelay
    {
        get { lock (_lock) { return _sentMessagesWithDelay.ToList(); } }
    }

    public int MessageCount => _messages.Count;

    /// <summary>The fake session that deliveries for <paramref name="sessionId"/> use.</summary>
    public FakeServiceBusSession GetSession(string sessionId) => GetOrCreateSession(sessionId);

    // Like the Service Bus sender, a send made with a cancelled token fails without effect.
    public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            _allSentMessages.Add(message);
            _sentMessagesWithDelay.Add((message, messageEnqueueDelay));
        }
        _messages.Enqueue(message);
        return Task.CompletedTask;
    }

    public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var message in messages)
        {
            lock (_lock)
            {
                _allSentMessages.Add(message);
                _sentMessagesWithDelay.Add((message, messageEnqueueDelay));
            }
            _messages.Enqueue(message);
        }
        return Task.CompletedTask;
    }

    /// <summary>
    /// Delivers all pending messages to the given subscriber handler, simulating
    /// the Azure Service Bus → ServiceBusAdapter → MessageHandler pipeline.
    /// </summary>
    public async Task DeliverAll(IMessageHandler messageHandler, CancellationToken cancellationToken = default)
    {
        while (_messages.TryDequeue(out var message))
        {
            var receivedMessage = ToReceivedMessage(message);
            var sbMessage = new NimBus.ServiceBus.ServiceBusMessage(receivedMessage);
            var session = GetOrCreateSession(message.SessionId);
            var context = new MessageContext(sbMessage, session);

            await messageHandler.Handle(context, cancellationToken);
        }
    }

    /// <summary>
    /// Delivers all pending messages to multiple subscribers, simulating topic fan-out.
    /// </summary>
    public async Task DeliverAllToSubscribers(IEnumerable<IMessageHandler> messageHandlers, CancellationToken cancellationToken = default)
    {
        var handlers = messageHandlers?.ToList() ?? throw new ArgumentNullException(nameof(messageHandlers));
        if (handlers.Count == 0)
            throw new ArgumentException("At least one message handler must be provided.", nameof(messageHandlers));
        var sessionsBySubscriberAndSession = new Dictionary<(int subscriberIndex, string sessionId), FakeServiceBusSession>();

        while (_messages.TryDequeue(out var message))
        {
            for (int subscriberIndex = 0; subscriberIndex < handlers.Count; subscriberIndex++)
            {
                var messageHandler = handlers[subscriberIndex];
                var receivedMessage = ToReceivedMessage(message);
                var sbMessage = new NimBus.ServiceBus.ServiceBusMessage(receivedMessage);
                var sessionKey = (subscriberIndex, NormalizeSessionId(message.SessionId));
                if (!sessionsBySubscriberAndSession.TryGetValue(sessionKey, out var session))
                {
                    session = new FakeServiceBusSession();
                    sessionsBySubscriberAndSession[sessionKey] = session;
                }

                var context = new MessageContext(sbMessage, session);

                await messageHandler.Handle(context, cancellationToken);
            }
        }
    }

    /// <summary>
    /// Delivers all pending messages and returns per-message results with session tracking.
    /// </summary>
    public async Task<List<DeliveryResult>> DeliverAllWithResults(IMessageHandler messageHandler, CancellationToken cancellationToken = default)
    {
        var results = new List<DeliveryResult>();

        while (_messages.TryDequeue(out var message))
        {
            var receivedMessage = ToReceivedMessage(message);
            var sbMessage = new NimBus.ServiceBus.ServiceBusMessage(receivedMessage);
            var session = GetOrCreateSession(message.SessionId);
            var context = new MessageContext(sbMessage, session);
            Exception? caughtException = null;

            try
            {
                await messageHandler.Handle(context, cancellationToken);
            }
            catch (Exception ex)
            {
                caughtException = ex;
            }

            results.Add(new DeliveryResult(message, context, session, caughtException));
        }

        return results;
    }

    public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var enqueueDelay = (int)Math.Ceiling((scheduledEnqueueTime - DateTimeOffset.UtcNow).TotalMinutes);
        lock (_lock)
        {
            _allSentMessages.Add(message);
            _sentMessagesWithDelay.Add((message, enqueueDelay));
        }
        _messages.Enqueue(message);
        return Task.FromResult(0L);
    }

    public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    private FakeServiceBusSession GetOrCreateSession(string? sessionId)
    {
        var normalizedSessionId = NormalizeSessionId(sessionId);
        return _sessionsBySessionId.GetOrAdd(normalizedSessionId, _ => new FakeServiceBusSession());
    }

    private static string NormalizeSessionId(string? sessionId) => sessionId ?? string.Empty;

    /// <summary>
    /// Converts an IMessage to a ServiceBusReceivedMessage using MessageHelper and ServiceBusModelFactory.
    /// Simulates Azure Service Bus SQL Rule Actions that inject EventId, From, and To.
    /// </summary>
    private static ServiceBusReceivedMessage ToReceivedMessage(IMessage message)
    {
        var sbOutgoing = MessageHelper.ToServiceBusMessage(message);

        var properties = sbOutgoing.ApplicationProperties.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value);

        // Simulate SQL Rule Action: SET user.EventId = newid()
        var eventIdKey = "EventId";
        if (!properties.TryGetValue(eventIdKey, out object? eventId) || eventId is null or "")
            properties[eventIdKey] = Guid.NewGuid().ToString();

        // Simulate SQL Rule Action: SET user.From = '<topicName>'
        var fromKey = "From";
        if (!properties.TryGetValue(fromKey, out object? fromValue) || fromValue is null or "")
            properties[fromKey] = "test-topic";

        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: sbOutgoing.Body,
            messageId: sbOutgoing.MessageId,
            sessionId: sbOutgoing.SessionId,
            correlationId: sbOutgoing.CorrelationId,
            replyTo: sbOutgoing.ReplyTo,
            replyToSessionId: sbOutgoing.ReplyToSessionId,
            properties: properties,
            enqueuedTime: DateTimeOffset.UtcNow);
    }
}

internal sealed record DeliveryResult(
    IMessage OriginalMessage,
    MessageContext Context,
    FakeServiceBusSession Session,
    Exception? Exception);
