#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Hubs;
using NimBus.WebApp.Services.Heartbeat;
using CoreConstants = NimBus.Core.Messages.Constants;
using CoreHeartbeat = NimBus.Core.Events.Heartbeat;
using SignalNames = NimBus.WebApp.Constants.EventSignalNames;
using StoreHeartbeat = NimBus.MessageStore.States.Heartbeat;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Behaviour of the platform heartbeat sender, ported from the DIS suite that
/// covers its bug history: the fan-out is opt-out rather than opt-in, duplicate
/// metadata must not take out the Health tab, the Pending row is written before
/// the send, the Resolver probe rides its own session and ignores the Enabled
/// switch, and both sweeps share one cutoff.
/// </summary>
[TestClass]
public class HeartbeatServiceTests
{
    private const string WorkerA = "WorkerAEndpoint";
    private const string WorkerB = "WorkerBEndpoint";

    [TestMethod]
    public async Task SendHeartbeatsAsync_SkipsEndpointsThatConsumeNothing()
    {
        // Only a subscriber can answer a probe, and an endpoint that consumes
        // nothing runs none. Probing one records a miss every interval and reports
        // a permanent outage against an endpoint that was never reachable by
        // design — so the fan-out leaves producers alone.
        var store = new RecordingStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings { Enabled = true, IntervalSeconds = 300, TimeoutSeconds = 60 });
        var sender = new RecordingSender();
        var platform = new FakePlatform(
            new FakeEndpoint(WorkerA),
            new FakeEndpoint("PublisherOnlyEndpoint", consumes: false));
        var service = CreateService(store, sender, platform: platform);

        var count = await service.SendHeartbeatsAsync();

        Assert.AreEqual(1, count);
        Assert.AreEqual(WorkerA, sender.Sent.Single().Topic);
        // Never written to at all: the store only knows endpoints something has
        // stored against. A pending row here would be settled to missing by the
        // next sweep, which is the outage the skip exists to avoid.
        Assert.IsNull(await store.GetEndpointMetadata("PublisherOnlyEndpoint"));
    }

    [TestMethod]
    public async Task GetOverviewAsync_OmitsEndpointsThatConsumeNothing()
    {
        // Listing an endpoint the fan-out never probes would strand it at Unknown
        // forever and count it against fleet reachability.
        var store = new RecordingStore();
        var platform = new FakePlatform(
            new FakeEndpoint(WorkerA),
            new FakeEndpoint("PublisherOnlyEndpoint", consumes: false));
        var service = CreateService(store, platform: platform);

        var overview = await service.GetOverviewAsync();

        CollectionAssert.AreEqual(
            new[] { WorkerA },
            overview.Select(row => row.EndpointId).ToArray());
    }

    [TestMethod]
    public async Task SendHeartbeatsAsync_WritesPendingAndSendsOnlyEndpointsNotOptedOut()
    {
        var store = new RecordingStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings { Enabled = true, IntervalSeconds = 300, TimeoutSeconds = 60 });
        await store.EnableHeartbeatOnEndpoint(WorkerB, enable: false);
        var sender = new RecordingSender();
        var service = CreateService(store, sender);

        var count = await service.SendHeartbeatsAsync();

        Assert.AreEqual(1, count);
        var (topic, message) = sender.Sent.Single();
        Assert.AreEqual(WorkerA, topic, "Probes go straight to the endpoint's own topic, never via the Manager topic.");
        Assert.AreEqual(MessageType.EventRequest, message.MessageType);
        Assert.AreEqual(CoreConstants.ManagerId, message.From);
        Assert.AreEqual(WorkerA, message.To);
        Assert.AreEqual("Heartbeat", message.SessionId);
        Assert.AreEqual("NimBus.Platform.Heartbeat", message.EventTypeId);
        Assert.AreEqual("NimBus.Platform.Heartbeat", message.MessageContent.EventContent!.EventTypeId);
        Assert.AreEqual(CoreConstants.Self, message.ParentMessageId);
        Assert.AreEqual(CoreConstants.Self, message.OriginatingMessageId);
        Assert.AreEqual(CoreConstants.ManagerId, message.OriginatingFrom);
        Assert.AreEqual(message.MessageId, message.CorrelationId);
        Assert.IsFalse(string.IsNullOrEmpty(message.EventId));

        var content = JsonConvert.DeserializeObject<CoreHeartbeat>(message.MessageContent.EventContent.EventJson!);
        Assert.IsNotNull(content);
        Assert.AreNotEqual(default(DateTime), content.ForwardSendTime);

        var pending = await SingleHeartbeatAsync(store, WorkerA);
        Assert.AreEqual(message.MessageId, pending.MessageId);
        Assert.AreEqual(HeartbeatStatus.Pending, pending.EndpointHeartbeatStatus);
        Assert.AreEqual(pending.StartTime, pending.ReceivedTime);
        Assert.AreEqual(pending.StartTime, pending.EndTime);
        Assert.AreEqual(300, pending.IntervalSeconds);

        var optedOut = await store.GetEndpointMetadata(WorkerB);
        Assert.IsNull(optedOut!.Heartbeats, "The opted-out endpoint must not get a Pending row either.");
        // Sweeping is owned by the background tick, not the send path — a fresh
        // send must not race its own sweep.
        Assert.AreEqual(0, store.HeartbeatSweepCutoffs.Count);
    }

    [TestMethod]
    public async Task SendHeartbeatsAsync_WritesThePendingRowBeforeTheSend()
    {
        // The answer can arrive before SendAsync returns; if the row were written
        // afterwards it would overwrite the settled state with Pending.
        var store = new RecordingStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings { Enabled = true, IntervalSeconds = 300, TimeoutSeconds = 60 });
        StoreHeartbeat? rowAtSendTime = null;
        var sender = new RecordingSender
        {
            OnSend = async (_, message) => rowAtSendTime = await FindHeartbeatAsync(store, message.To, message.MessageId!),
        };
        var service = CreateService(store, sender);

        await service.SendHeartbeatsAsync();

        Assert.IsNotNull(rowAtSendTime, "The Pending row must already exist when the probe goes out.");
        Assert.AreEqual(HeartbeatStatus.Pending, rowAtSendTime.EndpointHeartbeatStatus);
    }

    [TestMethod]
    public async Task SendHeartbeatsAsync_ProbesEndpointsWithNoStoredPreference()
    {
        // The fan-out is opt-OUT: an endpoint nobody ever configured is probed.
        // Driving it off the store's "opted in" query would probe nothing at all
        // on a fresh install.
        var store = new RecordingStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings { Enabled = true, IntervalSeconds = 300, TimeoutSeconds = 60 });
        Assert.AreEqual(0, (await store.GetMetadatasWithEnabledHeartbeat()).Count);
        var sender = new RecordingSender();
        var service = CreateService(store, sender);

        Assert.AreEqual(2, await service.SendHeartbeatsAsync());
        CollectionAssert.AreEquivalent(new[] { WorkerA, WorkerB }, sender.Sent.Select(s => s.Topic).ToArray());
    }

    [TestMethod]
    public async Task SendHeartbeatsAsync_ReturnsZeroWhenDisabledAndNotForced()
    {
        var store = new RecordingStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings { Enabled = false, IntervalSeconds = 300, TimeoutSeconds = 60 });
        var sender = new RecordingSender();
        var service = CreateService(store, sender);

        Assert.AreEqual(0, await service.SendHeartbeatsAsync());
        Assert.AreEqual(0, sender.Sent.Count);
        // "Send now" is exactly this call with force — the switch must not block it.
        Assert.AreEqual(2, await service.SendHeartbeatsAsync(force: true));
    }

    [TestMethod]
    public async Task SendHeartbeatsAsync_ToleratesDuplicateMetadata_AndAnExplicitOptOutStillWins()
    {
        // Two records for WorkerB, only one of which carries the opt-out. Losing it
        // would start probing an endpoint someone deliberately excluded, so the
        // opt-out must win regardless of which row is seen first. Cosmos ids are
        // case-sensitive while every lookup here is not, which is how the pair
        // arises in the first place.
        var store = new DuplicateMetadataStore(
            new EndpointMetadata { EndpointId = WorkerA },
            new EndpointMetadata { EndpointId = WorkerB, IsHeartbeatEnabled = false },
            new EndpointMetadata { EndpointId = WorkerB.ToLowerInvariant() });
        await store.SetHeartbeatSettings(new HeartbeatSettings { Enabled = true, IntervalSeconds = 300, TimeoutSeconds = 60 });
        var sender = new RecordingSender();
        var service = CreateService(store, sender);

        Assert.AreEqual(1, await service.SendHeartbeatsAsync());
        Assert.AreEqual(WorkerA, sender.Sent.Single().Message.To);
    }

    [TestMethod]
    public async Task SweepTimeoutsAsync_SettlesTimedOutProbesViaStore_EvenWhenDisabled()
    {
        var store = new RecordingStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings { Enabled = false, IntervalSeconds = 300, TimeoutSeconds = 60 });
        await store.SetHeartbeat(
            new StoreHeartbeat
            {
                MessageId = "probe-1",
                StartTime = DateTime.UtcNow.AddMinutes(-10),
                EndpointHeartbeatStatus = HeartbeatStatus.Pending,
            },
            WorkerA);
        var service = CreateService(store);

        var before = DateTime.UtcNow;
        var count = await service.SweepTimeoutsAsync();
        var after = DateTime.UtcNow;

        Assert.AreEqual(1, count);
        var cutoff = store.HeartbeatSweepCutoffs.Single();
        AssertBetween(before.AddSeconds(-60), after.AddSeconds(-60), cutoff);
        var settled = await SingleHeartbeatAsync(store, WorkerA);
        Assert.AreEqual(HeartbeatStatus.Off, settled.EndpointHeartbeatStatus);
    }

    [TestMethod]
    public async Task SweepTimeoutsAsync_ReturnsZeroWhenNothingTimedOut()
    {
        var store = new RecordingStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings { Enabled = true, IntervalSeconds = 300, TimeoutSeconds = 60 });
        var service = CreateService(store);

        Assert.AreEqual(0, await service.SweepTimeoutsAsync());
    }

    [TestMethod]
    public async Task SweepTimeoutsAsync_PassesOneCutoffToBothSweeps()
    {
        var store = new RecordingStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings { Enabled = false, IntervalSeconds = 300, TimeoutSeconds = 60 });
        var service = CreateService(store);

        var before = DateTime.UtcNow;
        await service.SweepTimeoutsAsync();
        var after = DateTime.UtcNow;

        var endpointCutoff = store.HeartbeatSweepCutoffs.Single();
        Assert.AreEqual(
            endpointCutoff,
            store.ServiceSweepCutoffs.Single(),
            "Endpoints and platform services must time out on one clock, not two.");
        AssertBetween(before.AddSeconds(-60), after.AddSeconds(-60), endpointCutoff);
    }

    [TestMethod]
    public async Task SetSettingsAsync_ClampsTimeoutToInterval()
    {
        var store = new RecordingStore();
        var service = CreateService(store);

        // A timeout longer than the interval could never elapse between probes.
        var result = await service.SetSettingsAsync(new HeartbeatSettings
        {
            Enabled = true,
            IntervalSeconds = 60,
            TimeoutSeconds = 600,
        });

        Assert.AreEqual(60, result.TimeoutSeconds);
        Assert.AreEqual(60, (await store.GetHeartbeatSettings()).TimeoutSeconds);
    }

    [TestMethod]
    public async Task SetSettingsAsync_ClampsTimeoutToMinimumAndIntervalToThirtySeconds()
    {
        var store = new RecordingStore();
        var service = CreateService(store);

        var result = await service.SetSettingsAsync(new HeartbeatSettings
        {
            IntervalSeconds = 1,
            TimeoutSeconds = 0,
        });

        Assert.AreEqual(30, result.IntervalSeconds, "The scheduler ticks every 30s, so a shorter interval is meaningless.");
        Assert.AreEqual(5, result.TimeoutSeconds);
    }

    [TestMethod]
    public async Task SetSettingsAsync_KeepsTheStoredLastSentAt()
    {
        // LastSentAtUtc is owned by the send claim. An operator editing the
        // schedule must not rewind it and hand out a second fan-out this interval.
        var store = new RecordingStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings { Enabled = true, IntervalSeconds = 300, TimeoutSeconds = 60 });
        Assert.IsTrue(await store.TryClaimHeartbeatSend(DateTime.UtcNow));
        var claimed = (await store.GetHeartbeatSettings()).LastSentAtUtc;
        Assert.IsNotNull(claimed);
        var service = CreateService(store);

        var result = await service.SetSettingsAsync(new HeartbeatSettings
        {
            Enabled = true,
            IntervalSeconds = 300,
            TimeoutSeconds = 60,
        });

        Assert.AreEqual(claimed, result.LastSentAtUtc);
    }

    [TestMethod]
    public async Task SetSettingsAsync_BroadcastsHeartbeatUpdateToAllClients()
    {
        // The NimBus hub has no endpoint groups (unlike DIS), so the signal goes to
        // every connected operator and the client re-reads the view.
        var proxy = new RecordingClientProxy();
        var service = CreateService(new RecordingStore(), hubContext: new FakeHubContext(proxy));

        await service.SetSettingsAsync(new HeartbeatSettings { IntervalSeconds = 300, TimeoutSeconds = 60 });

        CollectionAssert.AreEqual(new[] { SignalNames.HeartbeatUpdate }, proxy.SentMethods);
    }

    [TestMethod]
    public async Task GetOverviewAsync_ToleratesDuplicateEndpointRows_KeepingTheMostRecentlyProbed()
    {
        // Regression: nothing enforces one metadata record per endpoint, and Cosmos
        // ids are case-sensitive while these lookups are not — so "WorkerAEndpoint"
        // and "workeraendpoint" arrive as two rows. ToDictionary threw
        // ArgumentException and took out the whole Health tab.
        var older = new DateTime(2026, 8, 11, 10, 0, 0, DateTimeKind.Utc);
        var newer = older.AddMinutes(5);
        var store = new FixedOverviewStore(
            new HeartbeatOverviewItem
            {
                EndpointId = WorkerA.ToLowerInvariant(),
                Status = HeartbeatStatus.Off,
                LastStartTime = older,
                RoundTripMs = 999,
            },
            new HeartbeatOverviewItem
            {
                EndpointId = WorkerA,
                Status = HeartbeatStatus.On,
                LastStartTime = newer,
                RoundTripMs = 42,
            });
        var service = CreateService(store);

        var rows = await service.GetOverviewAsync();

        // One row per platform endpoint, and the surviving duplicate is the newer probe.
        Assert.AreEqual(2, rows.Count);
        var workerA = rows.Single(row => row.EndpointId == WorkerA);
        Assert.AreEqual(HeartbeatStatus.On, workerA.Status);
        Assert.AreEqual(42, workerA.RoundTripMs);
        // The endpoint with no stored row still renders, as Unknown.
        var workerB = rows.Single(row => row.EndpointId == WorkerB);
        Assert.AreEqual(HeartbeatStatus.Unknown, workerB.Status);
    }

    [TestMethod]
    public async Task GetOverviewAsync_OrdersCaseInsensitivelyByEndpointId()
    {
        var service = CreateService(new RecordingStore(), platform: new FakePlatform("zebra", "Alpha", "beta"));

        var rows = await service.GetOverviewAsync();

        CollectionAssert.AreEqual(
            new[] { "Alpha", "beta", "zebra" },
            rows.Select(row => row.EndpointId).ToArray());
    }

    [TestMethod]
    public async Task ProbeResolverAsync_SendsToResolverOnItsOwnSession_EvenWhenHeartbeatDisabled()
    {
        // The endpoint fan-out is switched off; Resolver liveness must not depend on
        // it, or the Health tab reads Unknown out of the box.
        var store = new RecordingStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings { Enabled = false, IntervalSeconds = 300, TimeoutSeconds = 60 });
        var sender = new RecordingSender();
        var service = CreateService(store, sender);

        Assert.IsTrue(await service.ProbeResolverAsync());

        var (topic, message) = sender.Sent.Single();
        Assert.AreEqual(CoreConstants.ResolverId, topic, "The probe goes straight to the Resolver topic.");
        Assert.AreEqual(MessageType.EventRequest, message.MessageType);
        Assert.AreEqual(CoreConstants.ResolverId, message.To);
        Assert.AreEqual("NimBus.Platform.Heartbeat", message.EventTypeId);
        // Its own session: sharing "Heartbeat" would queue the probe behind every
        // endpoint reply on the session-enabled Resolver subscription.
        Assert.AreEqual("Heartbeat-Resolver", message.SessionId);

        var claimed = (await store.GetServiceHealth()).Single();
        Assert.AreEqual(CoreConstants.ResolverId, claimed.ServiceId);
        Assert.AreEqual(message.MessageId, claimed.LastProbeMessageId);

        var content = JsonConvert.DeserializeObject<CoreHeartbeat>(message.MessageContent.EventContent!.EventJson!);
        Assert.IsNotNull(content);
        Assert.AreNotEqual(default(DateTime), content.ForwardSendTime);
        // Nothing was fanned out to the endpoints on the back of a probe.
        Assert.AreEqual(0, (await store.GetHeartbeatOverview()).Count);
    }

    [TestMethod]
    public async Task ProbeResolverAsync_SendsNothingWhenAnotherInstanceHoldsTheClaim()
    {
        var store = new RecordingStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings { Enabled = true, IntervalSeconds = 300, TimeoutSeconds = 60 });
        var sender = new RecordingSender();
        var service = CreateService(store, sender);

        Assert.IsTrue(await service.ProbeResolverAsync());
        // The interval has not elapsed, so the second caller loses the claim and
        // must send nothing at all — not even a duplicate probe.
        Assert.IsFalse(await service.ProbeResolverAsync());

        Assert.AreEqual(1, sender.Sent.Count);
    }

    [TestMethod]
    public async Task GetServiceHealthAsync_SynthesizesResolverRowBeforeTheFirstProbe()
    {
        var service = CreateService(new RecordingStore());

        var row = (await service.GetServiceHealthAsync()).Single();

        Assert.AreEqual(CoreConstants.ResolverId, row.ServiceId);
        Assert.AreEqual(HeartbeatStatus.Unknown, row.Status);
    }

    [TestMethod]
    public async Task SetEndpointEnabledAsync_TogglesTheOptOutAndBroadcasts()
    {
        var store = new RecordingStore();
        var proxy = new RecordingClientProxy();
        var service = CreateService(store, hubContext: new FakeHubContext(proxy));

        await service.SetEndpointEnabledAsync(WorkerB, enabled: false);

        Assert.AreEqual(false, (await store.GetEndpointMetadata(WorkerB))!.IsHeartbeatEnabled);
        CollectionAssert.AreEqual(new[] { SignalNames.HeartbeatUpdate }, proxy.SentMethods);
        Assert.AreEqual(1, await service.SendHeartbeatsAsync(force: true), "The opted-out endpoint drops out of the fan-out.");
    }

    // ───────────── Scheduled tick ─────────────

    [TestMethod]
    public async Task RunScheduledTickAsync_SweepsAndProbesEvenWhenTheFanOutIsDisabled()
    {
        var store = new RecordingStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings { Enabled = false, IntervalSeconds = 300, TimeoutSeconds = 60 });
        var sender = new RecordingSender();
        var service = CreateService(store, sender);

        Assert.IsFalse(await service.RunScheduledTickAsync(), "A disabled schedule must not win the fan-out claim.");

        Assert.AreEqual(1, store.HeartbeatSweepCutoffs.Count, "The sweep runs on every tick.");
        Assert.AreEqual(1, store.ServiceSweepCutoffs.Count);
        var (topic, _) = sender.Sent.Single();
        Assert.AreEqual(CoreConstants.ResolverId, topic, "Only the Resolver probe goes out while the fan-out is off.");
    }

    [TestMethod]
    public async Task RunScheduledTickAsync_FansOutOnceThenYieldsTheClaimUntilTheIntervalElapses()
    {
        var store = new RecordingStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings { Enabled = true, IntervalSeconds = 300, TimeoutSeconds = 60 });
        var sender = new RecordingSender();
        var service = CreateService(store, sender);

        Assert.IsTrue(await service.RunScheduledTickAsync());
        var afterFirst = sender.Sent.Select(s => s.Topic).ToList();
        CollectionAssert.AreEquivalent(new[] { CoreConstants.ResolverId, WorkerA, WorkerB }, afterFirst);

        Assert.IsFalse(await service.RunScheduledTickAsync(), "The interval has not elapsed, so nothing is due.");
        CollectionAssert.AreEqual(afterFirst, sender.Sent.Select(s => s.Topic).ToList());
        Assert.AreEqual(2, store.HeartbeatSweepCutoffs.Count, "The sweep still runs on a tick that claims nothing.");
    }

    [TestMethod]
    public async Task RunScheduledTickAsync_FoldsDefaultOptOutFleetWhileSendingIsDisabled()
    {
        var store = new RecordingStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings { Enabled = false, IntervalSeconds = 300, TimeoutSeconds = 60 });
        var start = DateTime.UtcNow.AddMinutes(-5);
        await store.SetHeartbeat(new StoreHeartbeat
        {
            MessageId = "settled",
            StartTime = start,
            ReceivedTime = start,
            EndTime = start.AddSeconds(1),
            IntervalSeconds = 300,
            EndpointHeartbeatStatus = HeartbeatStatus.On,
            SdkVersion = "1.2.3",
        }, WorkerA);
        var service = CreateService(store);

        Assert.IsFalse(await service.RunScheduledTickAsync());

        var day = (await store.GetHeartbeatUptimeDays(start.Date)).Single(row => row.EndpointId == WorkerA);
        Assert.AreEqual(1, day.Received);
        Assert.AreEqual(300, day.ObservedSeconds);
    }

    [TestMethod]
    public async Task RunScheduledTickAsync_persists_gaps_before_advancing_day_watermarks()
    {
        var store = new RecordingStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings { Enabled = false, IntervalSeconds = 300, TimeoutSeconds = 60 });
        var start = DateTime.UtcNow.AddMinutes(-5);
        await store.SetHeartbeat(new StoreHeartbeat
        {
            MessageId = "missed",
            StartTime = start,
            ReceivedTime = start,
            EndTime = start,
            IntervalSeconds = 300,
            EndpointHeartbeatStatus = HeartbeatStatus.Off,
        }, WorkerA);
        var history = new RecordingHistoryStore(store);
        var service = CreateService(store, historyStore: history);

        await service.RunScheduledTickAsync();

        string[] expectedWrites = ["gaps", "days", "prune"];
        CollectionAssert.AreEqual(expectedWrites, history.Writes);
    }

    [TestMethod]
    public async Task RunScheduledTickAsync_skips_explicit_prune_for_automatic_retention_store()
    {
        var store = new RecordingStore();
        await store.SetHeartbeatSettings(new HeartbeatSettings { Enabled = false, IntervalSeconds = 300, TimeoutSeconds = 60 });
        var history = new RecordingHistoryStore(store, prunesAutomatically: true);
        var service = CreateService(store, historyStore: history);

        await service.RunScheduledTickAsync();

        CollectionAssert.DoesNotContain(history.Writes, "prune");
    }

    private static HeartbeatService CreateService(
        InMemoryMessageStore store,
        IHeartbeatMessageSender? sender = null,
        IHubContext<GridEventsHub>? hubContext = null,
        IPlatform? platform = null,
        IHeartbeatHistoryStore? historyStore = null)
        => new(
            platform ?? new FakePlatform(WorkerA, WorkerB),
            store,
            store,
            sender ?? new RecordingSender(),
            NullLogger<HeartbeatService>.Instance,
            hubContext,
            historyStore ?? store);

    private static async Task<StoreHeartbeat> SingleHeartbeatAsync(INimBusMessageStore store, string endpointId)
    {
        var metadata = await store.GetEndpointMetadata(endpointId);
        Assert.IsNotNull(metadata?.Heartbeats);
        return metadata.Heartbeats.Single();
    }

    private static async Task<StoreHeartbeat?> FindHeartbeatAsync(INimBusMessageStore store, string endpointId, string messageId)
    {
        var metadata = await store.GetEndpointMetadata(endpointId);
        return metadata?.Heartbeats?.SingleOrDefault(heartbeat => heartbeat.MessageId == messageId);
    }

    private static void AssertBetween(DateTime lower, DateTime upper, DateTime actual)
        => Assert.IsTrue(actual >= lower && actual <= upper, $"Expected {actual:O} within [{lower:O}, {upper:O}].");

    /// <summary>
    /// The real in-memory store, with the two sweep cutoffs and the fan-out claim
    /// recorded. Explicit interface re-implementation is what puts these on the
    /// dispatch path — the base members are not virtual.
    /// </summary>
    private class RecordingStore : InMemoryMessageStore, INimBusMessageStore
    {
        public List<DateTime> HeartbeatSweepCutoffs { get; } = new();

        public List<DateTime> ServiceSweepCutoffs { get; } = new();

        Task<List<string>> IEndpointMetadataStore.SweepTimedOutHeartbeats(DateTime cutoffUtc)
        {
            HeartbeatSweepCutoffs.Add(cutoffUtc);
            return SweepTimedOutHeartbeats(cutoffUtc);
        }

        Task<List<string>> IServiceHealthStore.SweepTimedOutServiceProbes(DateTime cutoffUtc)
        {
            ServiceSweepCutoffs.Add(cutoffUtc);
            return SweepTimedOutServiceProbes(cutoffUtc);
        }
    }

    private sealed class RecordingHistoryStore(
        InMemoryMessageStore inner,
        bool prunesAutomatically = false) : IHeartbeatHistoryStore
    {
        public List<string> Writes { get; } = new();

        public bool PrunesHeartbeatHistoryAutomatically => prunesAutomatically;

        public Task<List<HeartbeatUptimeDay>> GetHeartbeatUptimeDays(DateTime fromDayUtc)
            => inner.GetHeartbeatUptimeDays(fromDayUtc);

        public Task<bool> UpsertHeartbeatUptimeDays(IEnumerable<HeartbeatUptimeDay> days)
        {
            Writes.Add("days");
            return inner.UpsertHeartbeatUptimeDays(days);
        }

        public Task<List<HeartbeatGap>> GetHeartbeatGaps(DateTime fromUtc)
            => inner.GetHeartbeatGaps(fromUtc);

        public Task<bool> UpsertHeartbeatGaps(IEnumerable<HeartbeatGap> gaps)
        {
            Writes.Add("gaps");
            return inner.UpsertHeartbeatGaps(gaps);
        }

        public Task<bool> TryClaimHeartbeatHistoryFold(DateTime dueBefore)
            => inner.TryClaimHeartbeatHistoryFold(dueBefore);

        public Task PruneHeartbeatHistory(DateTime cutoffUtc)
        {
            Writes.Add("prune");
            return inner.PruneHeartbeatHistory(cutoffUtc);
        }
    }

    /// <summary>
    /// Returns metadata the real store cannot hold: several records for one
    /// endpoint id, differing only by case. Cosmos allows exactly this.
    /// </summary>
    private sealed class DuplicateMetadataStore : RecordingStore, INimBusMessageStore
    {
        private readonly List<EndpointMetadata> _metadatas;

        public DuplicateMetadataStore(params EndpointMetadata[] metadatas) => _metadatas = metadatas.ToList();

        Task<List<EndpointMetadata>?> IEndpointMetadataStore.GetMetadatas(IEnumerable<string> endpointIds)
            => Task.FromResult<List<EndpointMetadata>?>(_metadatas);
    }

    /// <summary>Returns a fixed overview, including rows the real store deduplicates away.</summary>
    private sealed class FixedOverviewStore : RecordingStore, INimBusMessageStore
    {
        private readonly List<HeartbeatOverviewItem> _rows;

        public FixedOverviewStore(params HeartbeatOverviewItem[] rows) => _rows = rows.ToList();

        Task<List<HeartbeatOverviewItem>> IEndpointMetadataStore.GetHeartbeatOverview() => Task.FromResult(_rows);
    }

    private sealed class RecordingSender : IHeartbeatMessageSender
    {
        public List<(string Topic, Message Message)> Sent { get; } = new();

        /// <summary>Runs inside the send, so a test can observe store state mid-flight.</summary>
        public Func<string, Message, Task>? OnSend { get; set; }

        public async Task SendAsync(string topicName, Message message, CancellationToken cancellationToken = default)
        {
            if (OnSend is not null)
            {
                await OnSend(topicName, message);
            }

            Sent.Add((topicName, message));
        }
    }

    private sealed class FakeHubContext : IHubContext<GridEventsHub>
    {
        public FakeHubContext(RecordingClientProxy proxy) => Clients = new FakeHubClients(proxy);

        public IHubClients Clients { get; }

        public IGroupManager Groups => throw new NotSupportedException();
    }

    private sealed class FakeHubClients : IHubClients
    {
        private readonly RecordingClientProxy _proxy;

        public FakeHubClients(RecordingClientProxy proxy) => _proxy = proxy;

        public IClientProxy All => _proxy;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Client(string connectionId) => _proxy;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _proxy;
        public IClientProxy Group(string groupName) => _proxy;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _proxy;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => _proxy;
        public IClientProxy User(string userId) => _proxy;
        public IClientProxy Users(IReadOnlyList<string> userIds) => _proxy;
    }

    private sealed class RecordingClientProxy : IClientProxy
    {
        public List<string> SentMethods { get; } = new();

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            SentMethods.Add(method);
            return Task.CompletedTask;
        }
    }

    private sealed class FakePlatform : IPlatform
    {
        private readonly List<IEndpoint> _endpoints;

        // Endpoints consume by default: only consumers run a subscriber, and only
        // a subscriber can answer a probe, so a consumer is what the fan-out is
        // about. Producer-only endpoints are built explicitly by the tests that
        // need one.
        public FakePlatform(params string[] endpointIds)
            => _endpoints = endpointIds.Select(id => (IEndpoint)new FakeEndpoint(id)).ToList();

        public FakePlatform(params IEndpoint[] endpoints) => _endpoints = endpoints.ToList();

        public IEnumerable<IEndpoint> Endpoints => _endpoints;
        public IEnumerable<IEventType> EventTypes => Enumerable.Empty<IEventType>();
        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
    }

    private sealed class FakeEndpoint : IEndpoint
    {
        private readonly bool _consumes;

        public FakeEndpoint(string id, bool consumes = true)
        {
            Id = id;
            _consumes = consumes;
        }

        public string Id { get; }
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => string.Empty;
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced => Enumerable.Empty<IEventType>();

        public IEnumerable<IEventType> EventTypesConsumed => _consumes
            ? new IEventType[] { new FakeEventType() }
            : Enumerable.Empty<IEventType>();

        public IEnumerable<IRoleAssignment> RoleAssignments => Enumerable.Empty<IRoleAssignment>();
    }

    private sealed class FakeEventType : IEventType
    {
        public string Id => "FakeEvent";
        public string Name => "FakeEvent";
        public string Description => string.Empty;
        public string Namespace => string.Empty;
        public IEnumerable<IProperty> Properties => Enumerable.Empty<IProperty>();
        public Type GetEventClassType() => typeof(object);
        public IEvent GetEventExample() => null!;
    }
}
