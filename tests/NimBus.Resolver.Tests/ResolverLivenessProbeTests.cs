#pragma warning disable CA1707, CA1515, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Resolver.Services;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using CoreHeartbeat = NimBus.Core.Events.Heartbeat;
using FakeCosmosDbClient = NimBus.Resolver.Tests.ResolverServiceTests.FakeCosmosDbClient;

namespace NimBus.Resolver.Tests;

/// <summary>
/// The Resolver receives two kinds of heartbeat <see cref="MessageType.EventRequest"/>:
/// the WebApp's liveness probe addressed to itself, and the copy of every endpoint
/// heartbeat request its fan-out subscription also picks up. Mixing them up would
/// either lose the probe or invent liveness from unrelated traffic.
/// </summary>
[TestClass]
public class ResolverLivenessProbeTests
{
    [TestMethod]
    public async Task Handle_HeartbeatRequestAddressedToResolver_RecordsLivenessAndCompletes()
    {
        var store = new FakeCosmosDbClient();
        var notifier = new ResolverHeartbeatTests.RecordingNotifier();
        var service = CreateService(store, notifier);

        var sentAt = DateTime.UtcNow.AddMilliseconds(-250);
        var message = CreateProbeContext(Constants.ResolverId, sentAt);

        await service.Handle(message);

        Assert.AreEqual(1, store.WrittenServiceHealth.Count);
        var written = store.WrittenServiceHealth[0];
        Assert.AreEqual(Constants.ResolverId, written.ServiceId);
        Assert.AreEqual(HeartbeatStatus.On, written.Status);
        Assert.IsFalse(string.IsNullOrWhiteSpace(written.Version), "The Resolver reports its own assembly version.");
        Assert.IsFalse(written.Version.Contains('+', StringComparison.Ordinal), "Version must be the bare package version without the '+<sha>' build suffix");
        Assert.IsNotNull(written.LastSeenUtc);
        Assert.IsNotNull(written.RoundTripMs);
        Assert.IsTrue(written.RoundTripMs >= 0 && written.RoundTripMs <= 60_000,
            $"Round trip should be a plausible millisecond span; got {written.RoundTripMs}.");
        Assert.IsNull(written.LastProbeMessageId, "Settling the probe clears the in-flight claim.");
        Assert.AreEqual(1, notifier.ServiceIds.Count);
        Assert.AreEqual(Constants.ResolverId, notifier.ServiceIds[0]);
        Assert.AreEqual(1, message.CompletedCalls);

        // A probe is not platform traffic: it must not reach the event store, the
        // Flow pages, or the latency aggregates.
        Assert.AreEqual(0, store.StoredMessages.Count);
        Assert.AreEqual(0, store.PendingUploads.Count);
    }

    [TestMethod]
    public async Task Handle_HeartbeatRequestCopyForAnEndpoint_IsDroppedWithoutRecordingLiveness()
    {
        var store = new FakeCosmosDbClient();
        var notifier = new ResolverHeartbeatTests.RecordingNotifier();
        var service = CreateService(store, notifier);

        var message = CreateProbeContext("WorkerA", DateTime.UtcNow);

        await service.Handle(message);

        Assert.AreEqual(0, store.WrittenServiceHealth.Count);
        Assert.AreEqual(0, store.WrittenHeartbeats.Count);
        Assert.AreEqual(0, notifier.ServiceIds.Count);
        Assert.AreEqual(0, store.StoredMessages.Count);
        Assert.AreEqual(1, message.CompletedCalls);
    }

    [TestMethod]
    public async Task Handle_SelfProbe_TransientStorageFailure_LeavesMessageUnsettled()
    {
        var store = new FakeCosmosDbClient
        {
            SetServiceHealthException = new StorageProviderTransientException("throttled", retryAfter: null),
        };
        var service = CreateService(store);

        var message = CreateProbeContext(Constants.ResolverId, DateTime.UtcNow);

        await service.Handle(message);

        Assert.AreEqual(0, message.CompletedCalls, "The session must redeliver the probe.");
        Assert.AreEqual(0, message.ScheduleRedeliveryCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_SelfProbe_CosmosThrottle_RetriesInPlaceInsteadOfRescheduling()
    {
        // A rescheduled probe would report the backoff as round-trip time, and a later probe
        // supersedes it anyway.
        var store = new FakeCosmosDbClient
        {
            SetServiceHealthException = new RequestLimitException(TimeSpan.FromSeconds(1)),
        };
        var service = CreateService(store);
        var message = CreateProbeContext(Constants.ResolverId, DateTime.UtcNow);

        await service.Handle(message);

        Assert.AreEqual(0, message.ScheduleRedeliveryCalls);
        Assert.AreEqual(0, message.CompletedCalls, "The session must redeliver the probe.");
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    [TestMethod]
    public async Task Handle_SelfProbe_FinalCosmosThrottle_UsesStableDeadLetterReason()
    {
        var store = new FakeCosmosDbClient
        {
            SetServiceHealthException = new RequestLimitException(TimeSpan.FromSeconds(1)),
        };
        var service = CreateService(store);
        var message = CreateProbeContext(Constants.ResolverId, DateTime.UtcNow);
        message.ThrottleRetryCount = 9;

        await service.Handle(message);

        Assert.AreEqual(0, message.CompletedCalls);
        Assert.AreEqual(0, message.ScheduleRedeliveryCalls);
        Assert.AreEqual(1, message.DeadLetterCalls);
        Assert.AreEqual("CosmosDbThrottled", message.LastDeadLetterReason);
    }

    [TestMethod]
    public async Task Handle_SelfProbe_WithoutServiceHealthStore_CompletesWithoutThrowing()
    {
        var store = new FakeCosmosDbClient();
        var service = new ResolverService(store, new NoopMessageStateChangeNotifier());

        var message = CreateProbeContext(Constants.ResolverId, DateTime.UtcNow);

        await service.Handle(message);

        Assert.AreEqual(0, store.WrittenServiceHealth.Count);
        Assert.AreEqual(1, message.CompletedCalls);
        Assert.AreEqual(0, message.DeadLetterCalls);
    }

    private static ResolverService CreateService(
        FakeCosmosDbClient store,
        IMessageStateChangeNotifier? notifier = null) =>
        new(store, notifier ?? new NoopMessageStateChangeNotifier(), logger: null, metadataStore: store, serviceHealthStore: store);

    private static FakeMessageContext CreateProbeContext(string to, DateTime forwardSendTime) =>
        ResolverHeartbeatTests.CreateHeartbeatContext(
            MessageType.EventRequest,
            to: to,
            from: Constants.ManagerId,
            payload: new CoreHeartbeat { ForwardSendTime = forwardSendTime });
}
