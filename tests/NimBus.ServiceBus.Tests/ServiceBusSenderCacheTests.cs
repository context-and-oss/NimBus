#pragma warning disable CA1707, CA2007
using Azure.Messaging.ServiceBus;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.ServiceBus;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus.Tests;

/// <summary>
/// Senders are cached per entity and disposed with their owner, instead of opening an AMQP
/// link per call (review follow-up P8).
/// </summary>
[TestClass]
public sealed class ServiceBusSenderCacheTests
{
    // ── The shared cache ────────────────────────────────────────────────

    [TestMethod]
    public void Same_entity_reuses_one_sender_and_a_different_entity_gets_its_own()
    {
        var client = new CountingServiceBusClient();
        var cache = new ServiceBusSenderCache(client);

        var first = cache.GetSender("orders");
        var second = cache.GetSender("orders");
        var other = cache.GetSender("billing");

        Assert.AreSame(first, second);
        Assert.AreNotSame(first, other);
        Assert.AreEqual(1, client.CreateSenderCountFor("orders"));
        Assert.AreEqual(1, client.CreateSenderCountFor("billing"));
    }

    [TestMethod]
    public void Entity_names_are_compared_ordinally()
    {
        var client = new CountingServiceBusClient();
        var cache = new ServiceBusSenderCache(client);

        Assert.AreNotSame(cache.GetSender("Orders"), cache.GetSender("orders"));
        Assert.AreEqual(2, client.CreateSenderCount);
    }

    [TestMethod]
    public async Task Concurrent_first_calls_for_one_entity_create_one_sender()
    {
        var client = new CountingServiceBusClient { CreateDelay = TimeSpan.FromMilliseconds(20) };
        var cache = new ServiceBusSenderCache(client);
        using var start = new ManualResetEventSlim();

        var calls = Enumerable.Range(0, 20)
            .Select(_ => Task.Run(() => { start.Wait(); return cache.GetSender("orders"); }))
            .ToArray();
        start.Set();
        var senders = await Task.WhenAll(calls);

        Assert.AreEqual(1, client.CreateSenderCount);
        Assert.AreEqual(1, senders.Distinct().Count());
    }

    [TestMethod]
    public void A_failed_creation_reaches_the_caller_and_is_not_cached()
    {
        var failure = new CreateSenderProbeException("link refused");
        var client = new CountingServiceBusClient { FailNextCreate = failure };
        var cache = new ServiceBusSenderCache(client);

        var thrown = Assert.ThrowsExactly<CreateSenderProbeException>(() => cache.GetSender("orders"));
        Assert.AreSame(failure, thrown);

        var sender = cache.GetSender("orders");

        Assert.IsNotNull(sender);
        Assert.AreEqual(2, client.CreateSenderCount);
    }

    [TestMethod]
    public async Task Disposing_disposes_each_sender_once_and_not_the_client()
    {
        var client = new CountingServiceBusClient();
        var cache = new ServiceBusSenderCache(client);
        cache.GetSender("orders");
        cache.GetSender("billing");

        await cache.DisposeAsync();
        await cache.DisposeAsync();

        Assert.IsTrue(client.Created.All(c => c.Sender.DisposeCount == 1));
        Assert.AreEqual(0, client.DisposeCount);
    }

    [TestMethod]
    public async Task A_synchronous_dispose_releases_the_senders_once_too()
    {
        var client = new CountingServiceBusClient();
        var cache = new ServiceBusSenderCache(client);
        cache.GetSender("orders");

        cache.Dispose();
        await cache.DisposeAsync();

        Assert.AreEqual(1, client.Created.Single().Sender.DisposeCount);
        Assert.ThrowsExactly<ObjectDisposedException>(() => cache.GetSender("orders"));
    }

    [TestMethod]
    public async Task After_disposal_no_sender_is_created()
    {
        var client = new CountingServiceBusClient();
        var cache = new ServiceBusSenderCache(client);
        cache.GetSender("orders");
        await cache.DisposeAsync();

        Assert.ThrowsExactly<ObjectDisposedException>(() => cache.GetSender("orders"));
        Assert.ThrowsExactly<ObjectDisposedException>(() => cache.GetSender("billing"));
        Assert.AreEqual(1, client.CreateSenderCount);
    }

    // ── ServiceBusAdapter / ServiceBusSession ───────────────────────────

    [TestMethod]
    public async Task Sessions_built_by_one_adapter_share_the_scheduled_message_sender()
    {
        var client = new RecordingServiceBusClient();
        var handler = new SchedulingHandler();
        var adapter = new ServiceBusAdapter(handler, client, "orders/subscription-a");

        await adapter.Handle(Received("m-1"), new CompletingMessageActions(), ServiceBusTestDoubles.CreateSessionActions());
        await adapter.Handle(Received("m-2"), new CompletingMessageActions(), ServiceBusTestDoubles.CreateSessionActions());

        Assert.AreEqual(1, client.CreateSenderCount);
        Assert.AreEqual("orders", client.LastSenderEntityPath);
        CollectionAssert.AreEqual(new[] { "m-1", "m-2" }, client.Sender.ScheduledMessages.Select(s => s.Message.MessageId).ToArray());
        Assert.AreEqual(0, client.Sender.DisposeCount, "The adapter keeps the sender between messages.");
    }

    [TestMethod]
    public async Task A_scheduled_send_keeps_its_message_and_enqueue_time_on_the_cached_sender()
    {
        var client = new RecordingServiceBusClient();
        var cache = new ServiceBusSenderCache(client);
        var first = new ServiceBusSession(ServiceBusTestDoubles.CreateMessageActions(), ServiceBusTestDoubles.CreateSessionActions(), client, "orders/subscription-a", "s") { SenderCache = cache };
        var second = new ServiceBusSession(ServiceBusTestDoubles.CreateMessageActions(), ServiceBusTestDoubles.CreateSessionActions(), client, "orders/subscription-a", "s") { SenderCache = cache };
        var at1 = DateTimeOffset.UtcNow.AddMinutes(1);
        var at2 = DateTimeOffset.UtcNow.AddMinutes(2);
        var message1 = new Azure.Messaging.ServiceBus.ServiceBusMessage("one");
        var message2 = new Azure.Messaging.ServiceBus.ServiceBusMessage("two");

        await first.SendScheduledMessageAsync(message1, at1);
        await second.SendScheduledMessageAsync(message2, at2);

        Assert.AreEqual(1, client.CreateSenderCount);
        Assert.AreSame(message1, client.Sender.ScheduledMessages[0].Message);
        Assert.AreEqual(at1, client.Sender.ScheduledMessages[0].ScheduledEnqueueTime);
        Assert.AreSame(message2, client.Sender.ScheduledMessages[1].Message);
        Assert.AreEqual(at2, client.Sender.ScheduledMessages[1].ScheduledEnqueueTime);
    }

    [TestMethod]
    public async Task A_session_without_a_cache_still_disposes_its_per_call_sender()
    {
        var client = new RecordingServiceBusClient();
        var session = new ServiceBusSession(ServiceBusTestDoubles.CreateMessageActions(), ServiceBusTestDoubles.CreateSessionActions(), client, "orders/subscription-a", "s");

        await session.SendScheduledMessageAsync(new Azure.Messaging.ServiceBus.ServiceBusMessage("x"), DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.AreEqual(1, client.Sender.DisposeCount);
    }

    [TestMethod]
    public async Task Disposing_the_adapter_disposes_its_senders_once_and_not_the_client()
    {
        var client = new RecordingServiceBusClient();
        var adapter = new ServiceBusAdapter(new SchedulingHandler(), client, "orders/subscription-a");
        await adapter.Handle(Received("m-1"), new CompletingMessageActions(), ServiceBusTestDoubles.CreateSessionActions());

        await adapter.DisposeAsync();
        await adapter.DisposeAsync();

        Assert.AreEqual(1, client.Sender.DisposeCount);
        Assert.AreEqual(0, client.DisposeCount);
    }

    [TestMethod]
    public async Task A_scheduled_send_after_the_adapter_is_disposed_creates_no_sender()
    {
        var client = new RecordingServiceBusClient();
        var handler = new RecordingSessionHandler();
        var adapter = new ServiceBusAdapter(handler, client, "orders/subscription-a");
        await adapter.Handle(Received("m-1"), ServiceBusTestDoubles.CreateMessageActions(), ServiceBusTestDoubles.CreateSessionActions());
        await adapter.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() =>
            handler.Session!.SendScheduledMessageAsync(new Azure.Messaging.ServiceBus.ServiceBusMessage("x"), DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.AreEqual(0, client.CreateSenderCount);
    }

    [TestMethod]
    public async Task A_failed_scheduled_sender_creation_is_retried_on_the_next_message()
    {
        var failure = new CreateSenderProbeException("link refused");
        var client = new RecordingServiceBusClient { CreateSenderException = failure };
        var cache = new ServiceBusSenderCache(client);
        var session = new ServiceBusSession(ServiceBusTestDoubles.CreateMessageActions(), ServiceBusTestDoubles.CreateSessionActions(), client, "orders/subscription-a", "s") { SenderCache = cache };

        var thrown = await Assert.ThrowsExactlyAsync<CreateSenderProbeException>(() =>
            session.SendScheduledMessageAsync(new Azure.Messaging.ServiceBus.ServiceBusMessage("x"), DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.AreSame(failure, thrown);

        client.CreateSenderException = null;
        await session.SendScheduledMessageAsync(new Azure.Messaging.ServiceBus.ServiceBusMessage("y"), DateTimeOffset.UtcNow.AddMinutes(1));

        Assert.AreEqual(2, client.CreateSenderCount);
        Assert.AreEqual(1, client.Sender.ScheduledMessages.Count);
    }

    // ── DeferredMessageProcessor ────────────────────────────────────────

    [TestMethod]
    public async Task Two_drains_for_one_topic_share_one_sender()
    {
        var client = new RecordingServiceBusClient();
        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage> { Deferred(1) });
        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage>());
        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage> { Deferred(2) });
        var processor = new DeferredMessageProcessor(client);

        await processor.ProcessDeferredMessagesAsync("session-1", "billing");
        await processor.ProcessDeferredMessagesAsync("session-1", "billing");

        Assert.AreEqual(1, client.CreateSenderCount);
        Assert.AreEqual(2, client.Sender.SentMessages.Count);
        Assert.AreEqual(0, client.Sender.DisposeCount, "The drain no longer disposes the sender.");
    }

    [TestMethod]
    public async Task Disposing_the_processor_disposes_its_senders_once_and_not_the_client()
    {
        var client = new RecordingServiceBusClient();
        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage> { Deferred(1) });
        var processor = new DeferredMessageProcessor(client);
        await processor.ProcessDeferredMessagesAsync("session-1", "billing");

        await processor.DisposeAsync();
        await processor.DisposeAsync();

        Assert.AreEqual(1, client.Sender.DisposeCount);
        Assert.AreEqual(0, client.DisposeCount);
    }

    [TestMethod]
    public async Task A_drain_after_the_processor_is_disposed_creates_no_sender()
    {
        var client = new RecordingServiceBusClient();
        var processor = new DeferredMessageProcessor(client);
        await processor.DisposeAsync();

        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => processor.ProcessDeferredMessagesAsync("session-1", "billing"));
        Assert.AreEqual(0, client.CreateSenderCount);
    }

    [TestMethod]
    public async Task A_failed_drain_sender_creation_is_retried_by_the_next_drain()
    {
        var failure = new CreateSenderProbeException("link refused");
        var client = new RecordingServiceBusClient { CreateSenderException = failure };
        client.SessionReceiver.ReceiveBatches.Add(new List<ServiceBusReceivedMessage> { Deferred(1) });
        var processor = new DeferredMessageProcessor(client);

        var thrown = await Assert.ThrowsExactlyAsync<CreateSenderProbeException>(() => processor.ProcessDeferredMessagesAsync("session-1", "billing"));
        Assert.AreSame(failure, thrown);

        client.CreateSenderException = null;
        await processor.ProcessDeferredMessagesAsync("session-1", "billing");

        Assert.AreEqual(2, client.CreateSenderCount);
        Assert.AreEqual(1, client.Sender.SentMessages.Count);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static ServiceBusReceivedMessage Received(string messageId) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(messageId: messageId, sessionId: "session-1");

    private static ServiceBusReceivedMessage Deferred(int sequence) =>
        ServiceBusModelFactory.ServiceBusReceivedMessage(
            body: BinaryData.FromString("{}"),
            messageId: $"deferred-{sequence}",
            sessionId: "session-1",
            properties: new Dictionary<string, object> { [UserPropertyName.DeferralSequence.ToString()] = sequence });

    // Completes the original delivery, which ScheduleRedelivery does after scheduling the copy.
    private sealed class CompletingMessageActions : Microsoft.Azure.Functions.Worker.ServiceBusMessageActions
    {
        public override Task CompleteMessageAsync(ServiceBusReceivedMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    // Schedules a redelivery of every message it handles, as the Resolver's throttle path does.
    private sealed class SchedulingHandler : IMessageHandler
    {
        public Task Handle(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
            messageContext.ScheduleRedelivery(TimeSpan.FromMinutes(1), throttleRetryCount: 1, cancellationToken);
    }

    // Captures the session the adapter built, so a test can use it after the adapter is disposed.
    private sealed class RecordingSessionHandler : IMessageHandler
    {
        public IServiceBusSession? Session { get; private set; }

        public Task Handle(IMessageContext messageContext, CancellationToken cancellationToken = default)
        {
            Session = (IServiceBusSession)typeof(MessageContext)
                .GetField("_sbSession", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(messageContext)!;
            return Task.CompletedTask;
        }
    }
}
