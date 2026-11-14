#pragma warning disable CA1707, CA1515, CA2007
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.Core.Inbox;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.Testing;
using Newtonsoft.Json;
using System.Text;

namespace NimBus.ServiceBus.Tests;

/// <summary>
/// Proves the inbox missing-MessageId bypass on the real Service Bus
/// <see cref="MessageContext"/>, whose <c>MessageId</c> getter throws
/// <see cref="InvalidMessageException"/> for a native message without one — the application
/// handler must still run, deduplication must be skipped, and lifecycle notification and the
/// response/settlement path must not throw.
/// </summary>
[TestClass]
public class InboxMissingMessageIdTests
{
    [TestMethod]
    public void MessageContext_without_message_id_throws_on_direct_access()
    {
        var context = CreateContextWithoutMessageId();

        Assert.ThrowsExactly<InvalidMessageException>(() => context.MessageId);
        Assert.IsNull(context.GetMessageIdOrDefault());
    }

    [TestMethod]
    public async Task Middleware_bypasses_deduplication_and_runs_the_handler()
    {
        var store = new CountingInboxStore();
        var inner = new CountingHandler();
        var middleware = new InboxMiddleware(inner, store);
        var context = CreateContextWithoutMessageId();

        await middleware.Handle(context);

        Assert.AreEqual(1, inner.HandleCalls);
        Assert.AreEqual(0, store.CheckCalls);
        Assert.AreEqual(0, store.RecordCalls);
        Assert.AreEqual(HandlerOutcome.Default, context.HandlerOutcome);
    }

    [TestMethod]
    public async Task Full_strict_handler_pipeline_runs_the_handler_and_completes_the_message()
    {
        var store = new CountingInboxStore();
        var inner = new CountingHandler();
        var observer = new CountingObserver();
        var notifier = new MessageLifecycleNotifier([observer]);
        var session = new InertSession();
        var handler = new StrictMessageHandler(
            new InboxMiddleware(inner, store, notifier),
            new ResponseService(new InMemoryMessageBus()),
            NullLogger.Instance,
            retryPolicyProvider: null,
            pipeline: null,
            lifecycleNotifier: notifier,
            failureDispositionClassifier: null,
            inboxDuplicateDetector: new InboxDuplicateDetector(store, notifier));
        var context = new MessageContext(CreateMessageWithoutMessageId(), session);

        await handler.Handle(context);

        Assert.AreEqual(1, inner.HandleCalls, "The handler must run despite the missing MessageId.");
        Assert.AreEqual(0, store.CheckCalls);
        Assert.AreEqual(0, store.RecordCalls);
        Assert.AreEqual(1, session.CompleteCalls, "The message must settle normally, not dead-letter.");
        Assert.AreEqual(0, session.DeadLetterCalls);
        Assert.AreEqual(1, observer.ReceivedCalls);
        Assert.IsNull(observer.LastReceived!.MessageId);
    }

    [TestMethod]
    public void Lifecycle_context_creation_does_not_throw_for_missing_message_id()
    {
        var context = CreateContextWithoutMessageId();

        var lifecycleContext = MessageLifecycleContext.FromMessageContext(context);

        Assert.IsNull(lifecycleContext.MessageId);
        Assert.AreEqual("AnalyticsEndpoint", lifecycleContext.EndpointId);
        Assert.AreEqual("evt-1", lifecycleContext.EventId);
    }

    private static MessageContext CreateContextWithoutMessageId() =>
        new(CreateMessageWithoutMessageId(), new InertSession());

    private static NoIdServiceBusMessage CreateMessageWithoutMessageId()
    {
        var content = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" },
        };
        var message = new NoIdServiceBusMessage
        {
            Body = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(content)),
        };
        message.UserProperties[UserPropertyName.From.ToString()] = "StorefrontEndpoint";
        message.UserProperties[UserPropertyName.To.ToString()] = "AnalyticsEndpoint";
        message.UserProperties[UserPropertyName.EventId.ToString()] = "evt-1";
        message.UserProperties[UserPropertyName.MessageType.ToString()] = MessageType.EventRequest.ToString();
        return message;
    }

    private sealed class CountingInboxStore : IInboxStore
    {
        public int CheckCalls { get; private set; }
        public int RecordCalls { get; private set; }

        public Task<bool> HasProcessedAsync(
            string endpointId,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            CheckCalls++;
            return Task.FromResult(false);
        }

        public Task RecordProcessedAsync(
            string endpointId,
            string messageId,
            CancellationToken cancellationToken = default)
        {
            RecordCalls++;
            return Task.CompletedTask;
        }

        public Task<int> PurgeExpiredAsync(
            string endpointId,
            DateTimeOffset olderThan,
            CancellationToken cancellationToken = default) => Task.FromResult(0);
    }

    private sealed class CountingHandler : IEventContextHandler
    {
        public int HandleCalls { get; private set; }

        public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
        {
            HandleCalls++;
            return Task.CompletedTask;
        }
    }

    private sealed class CountingObserver : IMessageLifecycleObserver
    {
        public int ReceivedCalls { get; private set; }
        public MessageLifecycleContext? LastReceived { get; private set; }

        public Task OnMessageReceived(
            MessageLifecycleContext context,
            CancellationToken cancellationToken = default)
        {
            ReceivedCalls++;
            LastReceived = context;
            return Task.CompletedTask;
        }
    }

    private sealed class NoIdServiceBusMessage : IServiceBusMessage
    {
        public Dictionary<string, string> UserProperties { get; } = new();

        public byte[] Body { get; set; } = Array.Empty<byte>();
        public string LockToken { get; set; } = "lock-1";
        public string SessionId { get; set; } = "session-1";
        public string MessageId => null!;
        public string CorrelationId => null!;
        public int DeliveryCount { get; set; } = 1;
        public long SequenceNumber { get; set; } = 1;
        public DateTime EnqueuedTimeUtc { get; set; } = DateTime.UtcNow;

        ServiceBusReceivedMessage IServiceBusMessage.Message => null!;

        public string GetUserProperty(UserPropertyName name) => GetUserProperty(name.ToString());

        public string GetUserProperty(string name) =>
            UserProperties.TryGetValue(name, out var value) ? value : null!;
    }

    private sealed class InertSession : IServiceBusSession
    {
        public SessionState State { get; set; } = new();

        public int CompleteCalls { get; private set; }
        public int DeadLetterCalls { get; private set; }

        public Task CompleteAsync(IServiceBusMessage message, CancellationToken ct = default)
        {
            CompleteCalls++;
            return Task.CompletedTask;
        }

        public Task DeadLetterAsync(IServiceBusMessage message, string reason, string? description, CancellationToken ct = default)
        {
            DeadLetterCalls++;
            return Task.CompletedTask;
        }

        public Task SetStateAsync(SessionState state, CancellationToken ct = default)
        {
            State = state;
            return Task.CompletedTask;
        }

        public Task<SessionState> GetStateAsync(CancellationToken ct = default) => Task.FromResult(State);

        public Task SendScheduledMessageAsync(Azure.Messaging.ServiceBus.ServiceBusMessage message, DateTimeOffset scheduledTime, CancellationToken ct = default) =>
            Task.CompletedTask;
    }
}
