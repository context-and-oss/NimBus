#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.ServiceBus;
using ServiceBusMessage = Azure.Messaging.ServiceBus.ServiceBusMessage;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Heartbeat;

namespace NimBus.WebApp.Tests;

/// <summary>
/// The heartbeat sender, the Manager client and AdminService take their senders from a
/// per-entity cache that outlives a single call, and the cache's owner disposes them
/// (review follow-up P8).
/// </summary>
[TestClass]
public sealed class SenderCacheOwnerTests
{
    // ── Heartbeat ───────────────────────────────────────────────────────

    [TestMethod]
    public async Task Heartbeats_to_one_topic_share_a_sender_and_another_topic_gets_its_own()
    {
        var client = new CountingClient();
        var sut = new ServiceBusHeartbeatMessageSender(client);

        await sut.SendAsync("WorkerA", Probe());
        await sut.SendAsync("WorkerA", Probe());
        await sut.SendAsync("WorkerB", Probe());

        Assert.AreEqual(1, client.CreateCountFor("WorkerA"));
        Assert.AreEqual(1, client.CreateCountFor("WorkerB"));
        Assert.AreEqual(2, client.SenderFor("WorkerA").Sent.Count);
        Assert.AreEqual(0, client.SenderFor("WorkerA").DisposeCount);
    }

    [TestMethod]
    public async Task Disposing_the_heartbeat_sender_disposes_its_senders_once_and_not_the_client()
    {
        var client = new CountingClient();
        var sut = new ServiceBusHeartbeatMessageSender(client);
        await sut.SendAsync("WorkerA", Probe());
        await sut.SendAsync("WorkerB", Probe());

        await sut.DisposeAsync();
        await sut.DisposeAsync();

        Assert.IsTrue(client.Created.All(c => c.Sender.DisposeCount == 1));
        Assert.AreEqual(0, client.DisposeCount);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => sut.SendAsync("WorkerC", Probe()));
        Assert.AreEqual(2, client.Created.Count);
    }

    [TestMethod]
    public async Task A_failed_heartbeat_sender_creation_reaches_the_caller_and_is_retried()
    {
        var failure = new InvalidOperationException("link refused");
        var client = new CountingClient { FailNextCreate = failure };
        var sut = new ServiceBusHeartbeatMessageSender(client);

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => sut.SendAsync("WorkerA", Probe()));
        Assert.AreSame(failure, thrown);

        await sut.SendAsync("WorkerA", Probe());

        Assert.AreEqual(2, client.CreateCalls);
        Assert.AreEqual(1, client.SenderFor("WorkerA").Sent.Count);
    }

    // ── ManagerClient ───────────────────────────────────────────────────

    [TestMethod]
    public async Task Resubmits_and_skips_to_one_endpoint_share_a_sender_through_both_interfaces()
    {
        var client = new CountingClient();
        var manager = new ManagerClient(client);
        IManagerClient legacy = manager;
        IManagerCommandClient commands = manager;

        await legacy.Resubmit(Failure(), "Crm", "CrmAccountCreated", "{}");
        await legacy.Skip(Failure(), "Crm", "CrmAccountCreated");
        await commands.Resubmit(Failure(), "Crm", "CrmAccountCreated", "{}", "op-1");
        await commands.Skip(Failure(), "Crm", "CrmAccountCreated", "op-2");

        Assert.AreEqual(1, client.CreateCountFor("Crm"));
        Assert.AreEqual(4, client.SenderFor("Crm").Sent.Count);
        CollectionAssert.AreEqual(new[] { "op-1", "op-2" }, client.SenderFor("Crm").Sent.Skip(2).Select(m => m.MessageId).ToArray());
    }

    [TestMethod]
    public async Task Disposing_the_manager_client_disposes_its_senders_once_and_not_the_client()
    {
        var client = new CountingClient();
        var manager = new ManagerClient(client);
        await manager.Skip(Failure(), "Crm", "T");
        await manager.Skip(Failure(), "Erp", "T");

        await manager.DisposeAsync();
        await manager.DisposeAsync();

        Assert.IsTrue(client.Created.All(c => c.Sender.DisposeCount == 1));
        Assert.AreEqual(0, client.DisposeCount);
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => manager.Skip(Failure(), "Crm", "T"));
        Assert.AreEqual(2, client.CreateCalls);
    }

    [TestMethod]
    public async Task A_failed_manager_sender_creation_reaches_the_caller_and_is_retried()
    {
        var failure = new InvalidOperationException("link refused");
        var client = new CountingClient { FailNextCreate = failure };
        var manager = new ManagerClient(client);

        var thrown = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => manager.Skip(Failure(), "Crm", "T"));
        Assert.AreSame(failure, thrown);

        await manager.Skip(Failure(), "Crm", "T");

        Assert.AreEqual(2, client.CreateCalls);
        Assert.AreEqual(1, client.SenderFor("Crm").Sent.Count);
    }

    // ── AdminService and the WebApp singleton cache ─────────────────────

    [TestMethod]
    public async Task Reprocess_requests_from_two_scopes_share_the_singleton_sender()
    {
        var client = new CountingClient();
        await using var cache = new ServiceBusSenderCache(client);

        var first = await NewAdminService(client, cache).ReprocessDeferredAsync("Crm", "session-1");
        var second = await NewAdminService(client, cache).ReprocessDeferredAsync("Crm", "session-2");

        Assert.AreEqual(1, client.CreateCountFor("Crm"));
        Assert.AreEqual(2, client.SenderFor("Crm").Sent.Count);
        foreach (var result in new[] { first, second })
        {
            Assert.IsTrue(result.ProcessRequestSent);
            Assert.AreEqual(0, result.Errors.Count);
        }
        CollectionAssert.AreEqual(new[] { "session-1", "session-2" }, client.SenderFor("Crm").Sent.Select(m => m.SessionId).ToArray());
    }

    [TestMethod]
    public async Task A_failed_reprocess_sender_creation_is_reported_and_retried_by_the_next_request()
    {
        var client = new CountingClient { FailNextCreate = new InvalidOperationException("link refused") };
        await using var cache = new ServiceBusSenderCache(client);

        var failed = await NewAdminService(client, cache).ReprocessDeferredAsync("Crm", "session-1");
        var retried = await NewAdminService(client, cache).ReprocessDeferredAsync("Crm", "session-1");

        Assert.IsFalse(failed.ProcessRequestSent);
        CollectionAssert.AreEqual(new[] { "Send ProcessDeferredRequest: link refused" }, failed.Errors);
        Assert.IsTrue(retried.ProcessRequestSent);
        Assert.AreEqual(0, retried.Errors.Count);
        Assert.AreEqual(2, client.CreateCalls);
    }

    [TestMethod]
    public async Task Concurrent_reprocess_requests_create_one_sender()
    {
        var client = new CountingClient { CreateDelay = TimeSpan.FromMilliseconds(20) };
        await using var cache = new ServiceBusSenderCache(client);

        var results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(i => Task.Run(() => NewAdminService(client, cache).ReprocessDeferredAsync("Crm", $"session-{i}"))));

        Assert.AreEqual(1, client.CreateCalls);
        Assert.IsTrue(results.All(r => r.ProcessRequestSent));
    }

    [TestMethod]
    public async Task The_container_disposes_the_singleton_cache_once_and_not_the_client()
    {
        var client = new CountingClient();
        var services = new ServiceCollection();
        services.AddSingleton<ServiceBusClient>(client);
        Startup.AddServiceBusSenderCache(services);
        var provider = services.BuildServiceProvider();

        var cache = provider.GetRequiredService<ServiceBusSenderCache>();
        await NewAdminService(client, cache).ReprocessDeferredAsync("Crm", "session-1");
        await NewAdminService(client, provider.GetRequiredService<ServiceBusSenderCache>()).ReprocessDeferredAsync("Crm", "session-2");

        await provider.DisposeAsync();
        await cache.DisposeAsync();

        Assert.AreEqual(1, client.CreateCalls);
        Assert.AreEqual(1, client.SenderFor("Crm").DisposeCount);
        Assert.AreEqual(0, client.DisposeCount);
        var afterDispose = await NewAdminService(client, cache).ReprocessDeferredAsync("Crm", "session-3");
        Assert.IsFalse(afterDispose.ProcessRequestSent);
        Assert.AreEqual(1, client.CreateCalls);
    }

    [TestMethod]
    public void The_container_builds_AdminService_with_the_singleton_cache()
    {
        var ctor = typeof(AdminService).GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();

        Assert.AreEqual(typeof(ServiceBusSenderCache), ctor.GetParameters()[0].ParameterType,
            "The longest constructor, which the container prefers, takes the shared cache.");
    }

    [TestMethod]
    public async Task An_AdminService_built_without_the_cache_still_sends_and_disposes_its_per_call_sender()
    {
        var client = new CountingClient();
        var sut = new AdminService(
            platform: null!,
            messageStore: null!,
            capabilities: null!,
            sbAdmin: null!,
            sbClient: client,
            managerClient: null!,
            subscriptionAdmin: null!,
            logger: NullLogger<AdminService>.Instance);

        var result = await sut.ReprocessDeferredAsync("Crm", "session-1");

        Assert.IsTrue(result.ProcessRequestSent);
        Assert.AreEqual(1, client.SenderFor("Crm").Sent.Count);
        Assert.AreEqual(1, client.SenderFor("Crm").DisposeCount);
    }

    // ── Helpers ─────────────────────────────────────────────────────────

    private static AdminService NewAdminService(ServiceBusClient client, ServiceBusSenderCache cache) => new(
        senderCache: cache,
        platform: null!,
        messageStore: null!,
        capabilities: null!,
        sbAdmin: null!,
        sbClient: client,
        managerClient: null!,
        subscriptionAdmin: null!,
        logger: NullLogger<AdminService>.Instance);

    private static Message Probe() => new()
    {
        To = "WorkerA",
        SessionId = Guid.NewGuid().ToString(),
        MessageType = MessageType.EventRequest,
        MessageContent = new MessageContent(),
    };

    private static MessageEntity Failure() => new()
    {
        EventId = "event-1",
        SessionId = "session-1",
        MessageId = "failed-attempt",
        CorrelationId = "correlation-1",
    };

    /// <summary>Hands out a new sender per <see cref="CreateSender(string)"/> call and counts calls.</summary>
    private sealed class CountingClient : ServiceBusClient
    {
        private int _createCalls;
        private Exception? _failNextCreate;

        public ConcurrentQueue<(string Entity, CountingSender Sender)> Created { get; } = new();

        public Exception? FailNextCreate
        {
            get => Volatile.Read(ref _failNextCreate);
            set => Volatile.Write(ref _failNextCreate, value);
        }

        public TimeSpan CreateDelay { get; set; }

        public int CreateCalls => Volatile.Read(ref _createCalls);

        public int DisposeCount { get; private set; }

        public int CreateCountFor(string entity) => Created.Count(c => c.Entity == entity);

        public CountingSender SenderFor(string entity) => Created.Single(c => c.Entity == entity).Sender;

        public override ServiceBusSender CreateSender(string queueOrTopicName)
        {
            Interlocked.Increment(ref _createCalls);
            if (CreateDelay > TimeSpan.Zero)
                Thread.Sleep(CreateDelay);
            var failure = Interlocked.Exchange(ref _failNextCreate, null);
            if (failure is not null)
                throw failure;
            var sender = new CountingSender();
            Created.Enqueue((queueOrTopicName, sender));
            return sender;
        }

        // No session to clear: AdminService treats this as already cleared.
        public override Task<ServiceBusSessionReceiver> AcceptSessionAsync(string topicName, string subscriptionName, string sessionId, ServiceBusSessionReceiverOptions? options = null, CancellationToken cancellationToken = default) =>
            throw new ServiceBusException("no session", ServiceBusFailureReason.SessionCannotBeLocked);

        [SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Test double avoids disposing uninitialized SDK internals.")]
        public override ValueTask DisposeAsync()
        {
            DisposeCount++;
            return ValueTask.CompletedTask;
        }
    }

    [SuppressMessage("Usage", "CA2215:Dispose methods should call base class dispose", Justification = "Test double avoids disposing uninitialized SDK internals.")]
    private sealed class CountingSender : ServiceBusSender
    {
        private int _disposeCount;

        public ConcurrentQueue<ServiceBusMessage> SentQueue { get; } = new();

        public List<ServiceBusMessage> Sent => SentQueue.ToList();

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public override Task SendMessageAsync(ServiceBusMessage message, CancellationToken cancellationToken = default)
        {
            SentQueue.Enqueue(message);
            return Task.CompletedTask;
        }

        public override ValueTask DisposeAsync()
        {
            Interlocked.Increment(ref _disposeCount);
            return ValueTask.CompletedTask;
        }
    }
}
