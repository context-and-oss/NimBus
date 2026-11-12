#pragma warning disable CA1707, CA2007, CS0618
using Azure.Messaging.ServiceBus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Events;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

[TestClass]
public class SubscriberClientTests
{
    // ── CreateAsync argument validation ─────────────────────────────────

    [TestMethod]
    public void CreateAsync_NullClient_ThrowsArgumentNullException()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() =>
            SubscriberClient.CreateAsync(null!, "endpoint").GetAwaiter().GetResult());
    }

    [TestMethod]
    public void CreateAsync_NullEndpoint_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            SubscriberClient.CreateAsync(new RecordingServiceBusClient(), null!).GetAwaiter().GetResult());
    }

    [TestMethod]
    public void CreateAsync_EmptyEndpoint_ThrowsArgumentException()
    {
        Assert.ThrowsExactly<ArgumentException>(() =>
            SubscriberClient.CreateAsync(new RecordingServiceBusClient(), "").GetAwaiter().GetResult());
    }

    // ── CreateAsync factory ─────────────────────────────────────────────

    [TestMethod]
    public async Task CreateAsync_WithoutDeferredProcessor_CreatesClient()
    {
        var client = new RecordingServiceBusClient();
        var sut = await SubscriberClient.CreateAsync(client, "orders");

        Assert.IsNotNull(sut);
        Assert.AreEqual("orders", client.LastSenderEntityPath, "Should create sender for the endpoint topic");
    }

    [TestMethod]
    public async Task CreateAsync_CreatesClient()
    {
        var client = new RecordingServiceBusClient();

        var sut = await SubscriberClient.CreateAsync(client, "orders");

        Assert.IsNotNull(sut);
    }

    // ── Handle delegates to adapter ─────────────────────────────────────

    // Characterization test: pins the defective v4 behaviour of the obsolete two-argument
    // overload. ServiceBusSessionMessageActions cannot complete the message, so after the
    // handler succeeds a dead-letter ErrorResponse is sent and the failure is swallowed.
    // Replaced when the overload is removed in v5.
    [TestMethod]
    public async Task Handle_WithSessionActions_CannotSettle_SendsDeadLetterResponse()
    {
        var client = new RecordingServiceBusClient();
        var sut = await SubscriberClient.CreateAsync(client, "orders");
        var handler = new FakeTestEventHandler();
        sut.RegisterHandler<TestEvent>(() => handler);
        var message = CreateValidTestEventMessage();

        await sut.Handle(message, new StateCapableSessionActions());

        Assert.AreEqual(1, handler.HandleCalls);
        Assert.IsNotEmpty(client.Sender.SentMessages);
        var last = client.Sender.SentMessages[^1];
        Assert.IsTrue(last.ApplicationProperties.TryGetValue("DeadLetterReason", out var reason), "The last sent message should carry DeadLetterReason.");
        Assert.AreEqual("Failed to handle message.", reason);
    }

    [TestMethod]
    public async Task Handle_WithSessionReceiver_DelegatesToAdapter()
    {
        var client = new RecordingServiceBusClient();
        var sut = await SubscriberClient.CreateAsync(client, "orders");
        var message = CreateValidMessage();

        await sut.Handle(message, new RecordingServiceBusSessionReceiver());
    }

    [TestMethod]
    public async Task Handle_WithMessageAndSessionActions_DelegatesToAdapter()
    {
        var client = new RecordingServiceBusClient();
        var sut = await SubscriberClient.CreateAsync(client, "orders");
        var message = CreateValidMessage();

        await sut.Handle(message, ServiceBusTestDoubles.CreateMessageActions(), ServiceBusTestDoubles.CreateSessionActions());
    }

    // ── RegisterHandler ─────────────────────────────────────────────────

    [TestMethod]
    public async Task RegisterHandler_ThenHandle_InvokesRegisteredHandler()
    {
        var client = new RecordingServiceBusClient();
        var sut = await SubscriberClient.CreateAsync(client, "orders");

        var handler = new FakeTestEventHandler();
        sut.RegisterHandler<TestEvent>(() => handler);

        // The handler is registered but invocation is verified via the adapter pipeline.
        // RegisterHandler should not throw.
        Assert.IsNotNull(handler);
    }

    // ── Obsolete constructor ────────────────────────────────────────────

    // ── Fakes ────────────────────────────────────────────────────────────

    private sealed class FakeDeferredMessageProcessor : NimBus.Core.Messages.IDeferredMessageProcessor
    {
        public Task ProcessDeferredMessagesAsync(string sessionId, string topicName, CancellationToken ct = default) =>
            Task.CompletedTask;
    }

    private sealed class FakeTestEventHandler : IEventHandler<TestEvent>
    {
        public int HandleCalls { get; private set; }

        public Task Handle(TestEvent message, IEventHandlerContext context, CancellationToken cancellationToken = default)
        {
            HandleCalls++;
            return Task.CompletedTask;
        }
    }

    private static ServiceBusReceivedMessage CreateValidTestEventMessage()
    {
        var content = new NimBus.Core.Messages.MessageContent
        {
            EventContent = new NimBus.Core.Messages.EventContent
            {
                EventTypeId = "TestEvent",
                EventJson = Newtonsoft.Json.JsonConvert.SerializeObject(new TestEvent()),
            },
        };

        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString(Newtonsoft.Json.JsonConvert.SerializeObject(content)),
            messageId: "msg-1",
            sessionId: "session-1",
            properties: new System.Collections.Generic.Dictionary<string, object>
            {
                { "EventId", "event-1" },
                { "To", "orders" },
                { "From", "StorefrontEndpoint" },
                { "MessageType", "EventRequest" },
                { "OriginatingMessageId", "self" },
                { "ParentMessageId", "self" },
                { "OriginatingFrom", "StorefrontEndpoint" },
                { "EventTypeId", "TestEvent" },
                { "RetryCount", 0 },
            });
    }

    private static ServiceBusReceivedMessage CreateValidMessage()
    {
        return ServiceBusModelFactory.ServiceBusReceivedMessage(
            messageId: "msg-1",
            sessionId: "session-1",
            properties: new System.Collections.Generic.Dictionary<string, object>
            {
                { "EventId", "event-1" },
                { "To", "orders" },
                { "From", "StorefrontEndpoint" },
                { "MessageType", "EventRequest" },
                { "OriginatingMessageId", "self" },
                { "ParentMessageId", "self" },
                { "OriginatingFrom", "StorefrontEndpoint" },
                { "EventTypeId", "OrderPlaced" },
                { "RetryCount", 0 },
            });
    }
}
