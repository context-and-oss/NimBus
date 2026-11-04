#pragma warning disable CA1707, CA2007

using System;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Messages;
using NimBus.ServiceBus.Provisioning;
using NimBus.ServiceBusEmulator.Tests;
using NimBus.Testing.Conformance;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using StoreResolutionStatus = NimBus.MessageStore.ResolutionStatus;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Operations → Session state and Clean up session state against the NimBus Service Bus
/// emulator, over the topology <see cref="ServiceBusTopologyProvisioner"/> creates: session
/// locks, session state, peeks on the endpoint and Deferred subscriptions, and lock contention
/// with a receiver that holds the session.
/// </summary>
[TestClass]
[TestCategory("Emulator")]
public sealed class SessionStateEmulatorTests
{
    private const string Endpoint = "Crm";
    private const string Deferred = NimBus.Core.Messages.Constants.DeferredSubscriptionName;

    private static EmulatorProcess? _emulator;
    private static ServiceBusClient? _client;

    private readonly InMemoryMessageStore _store = new();

    private static ServiceBusClient Client => _client ?? throw new InvalidOperationException("The emulator isn't running.");

    private SessionStateService Sut => new(Client, _store, NullLogger<SessionStateService>.Instance);

    [ClassInitialize]
    public static async Task StartEmulatorAsync(TestContext _)
    {
        _emulator = await EmulatorProcess.StartAsync();
        using var provisioning = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await new ServiceBusTopologyProvisioner(_emulator.ConnectionString, () => new SingleEndpointPlatform(), _ => { })
            .ApplyAsync(provisioning.Token);
        _client = new ServiceBusClient(_emulator.ConnectionString, new ServiceBusClientOptions
        {
            RetryOptions = new ServiceBusRetryOptions { TryTimeout = TimeSpan.FromSeconds(10), MaxRetries = 1 },
        });
    }

    [ClassCleanup]
    public static async Task StopEmulatorAsync()
    {
        if (_client is not null)
            await _client.DisposeAsync();
        if (_emulator is not null)
            await _emulator.DisposeAsync();
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Lookup_of_a_session_with_no_messages_or_state_is_empty()
    {
        var view = await Sut.GetAsync(Endpoint, NewSession());

        Assert.IsFalse(view.Locked);
        Assert.IsTrue(view.IsEmpty);
        Assert.IsNull(view.StateJson);
        Assert.AreEqual(SessionStateService.VersionOf(null), view.StateVersion);
        Assert.AreEqual(0, view.ActiveMessageCount);
        Assert.AreEqual(0, view.DeferredSubscriptionCount);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Lookup_reads_the_state_and_counts_queued_and_parked_messages_without_consuming_them()
    {
        var session = NewSession();
        await WriteStateAsync(session, new SessionState { BlockedByEventId = "evt-1", BlockedByMessageId = "msg-1", DeferredCount = 1, NextDeferralSequence = 1 });
        await SendAsync(Endpoint, session, count: 2);
        await SendDeferredAsync(session, count: 1);
        await OperatorCommandTestRows.SeedFailedRowAsync(_store, Endpoint, "evt-1", session, "msg-1");

        var view = await Sut.GetAsync(Endpoint, session);

        Assert.IsFalse(view.Locked);
        Assert.IsTrue(view.Blocked);
        Assert.AreEqual("evt-1", view.BlockedByEventId);
        Assert.AreEqual("Failed", view.BlockingEventStatus);
        Assert.AreEqual(1, view.DeferredCount);
        Assert.AreEqual(2, view.ActiveMessageCount);
        Assert.AreEqual(1, view.DeferredSubscriptionCount);

        // A lookup peeks: the messages are still there, never delivered.
        await using var receiver = await Client.AcceptSessionAsync(Endpoint, Endpoint, session);
        var received = await receiver.ReceiveMessagesAsync(10, TimeSpan.FromSeconds(5));
        Assert.HasCount(2, received);
        Assert.IsTrue(received.All(m => m.DeliveryCount == 1), "A lookup must not count as a delivery.");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Lookup_of_a_session_a_receiver_holds_reports_it_locked_without_waiting_out_the_lock()
    {
        var session = NewSession();
        await WriteStateAsync(session, new SessionState { BlockedByEventId = "evt-1" });
        await using var holder = await Client.AcceptSessionAsync(Endpoint, Endpoint, session);

        var stopwatch = Stopwatch.StartNew();
        var view = await Sut.GetAsync(Endpoint, session);
        var change = await Sut.ChangeAsync(Endpoint, session,
            new SessionStateChangeRequest { Action = SessionStateChangeRequestAction.Reset, ExpectedVersion = "any" },
            _ => Task.CompletedTask);
        stopwatch.Stop();

        Assert.IsTrue(view.Locked);
        Assert.AreEqual(SessionStateChangeStatus.Locked, change.Status);
        Assert.IsTrue(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"Lookup and change took {stopwatch.Elapsed}.");
        Assert.AreEqual("evt-1", SessionState.FromBinaryData(await holder.GetSessionStateAsync()).BlockedByEventId,
            "The held session's state must be untouched.");
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Clear_block_then_reset_round_trip_through_the_broker()
    {
        var session = NewSession();
        await WriteStateAsync(session, new SessionState { BlockedByEventId = "evt-1", BlockedByMessageId = "msg-1", NextDeferralSequence = 3, ReplayedThroughSequence = 2 });
        var before = await Sut.GetAsync(Endpoint, session);

        var cleared = await Sut.ChangeAsync(Endpoint, session,
            new SessionStateChangeRequest { Action = SessionStateChangeRequestAction.ClearBlock, ExpectedVersion = before.StateVersion },
            _ => Task.CompletedTask);

        Assert.AreEqual(SessionStateChangeStatus.Changed, cleared.Status);
        var stored = SessionState.FromBinaryData(await ReadStateAsync(session));
        Assert.IsNull(stored.BlockedByEventId);
        Assert.AreEqual(3, stored.NextDeferralSequence);
        Assert.AreEqual(2, stored.ReplayedThroughSequence);
        Assert.AreEqual(cleared.View!.StateVersion, (await Sut.GetAsync(Endpoint, session)).StateVersion,
            "The version returned by a change matches what a fresh lookup reads back.");

        var reset = await Sut.ChangeAsync(Endpoint, session,
            new SessionStateChangeRequest { Action = SessionStateChangeRequestAction.Reset, ExpectedVersion = cleared.View.StateVersion },
            _ => Task.CompletedTask);

        Assert.AreEqual(SessionStateChangeStatus.Changed, reset.Status);
        Assert.IsTrue(SessionState.FromBinaryData(await ReadStateAsync(session)).IsEmpty());
        Assert.IsTrue((await Sut.GetAsync(Endpoint, session)).IsEmpty);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Replace_writes_validated_json_that_the_endpoint_reads_back()
    {
        var session = NewSession();
        var before = await Sut.GetAsync(Endpoint, session);

        var outcome = await Sut.ChangeAsync(Endpoint, session, new SessionStateChangeRequest
        {
            Action = SessionStateChangeRequestAction.Replace,
            ExpectedVersion = before.StateVersion,
            StateJson = "{\"BlockedByEventId\":\"evt-9\",\"NextDeferralSequence\":2,\"ReplayedThroughSequence\":1}",
        }, _ => Task.CompletedTask);

        Assert.AreEqual(SessionStateChangeStatus.Changed, outcome.Status);
        var stored = SessionState.FromBinaryData(await ReadStateAsync(session));
        Assert.AreEqual("evt-9", stored.BlockedByEventId);
        Assert.AreEqual(2, stored.NextDeferralSequence);
    }

    [TestMethod]
    [Timeout(60_000, CooperativeCancellation = true)]
    public async Task Change_after_the_endpoint_moved_the_state_is_a_conflict_and_keeps_the_new_state()
    {
        var session = NewSession();
        await WriteStateAsync(session, new SessionState { BlockedByEventId = "evt-1" });
        var seen = await Sut.GetAsync(Endpoint, session);
        await WriteStateAsync(session, new SessionState { BlockedByEventId = "evt-2" });

        var outcome = await Sut.ChangeAsync(Endpoint, session,
            new SessionStateChangeRequest { Action = SessionStateChangeRequestAction.Reset, ExpectedVersion = seen.StateVersion },
            _ => Task.CompletedTask);

        Assert.AreEqual(SessionStateChangeStatus.Conflict, outcome.Status);
        Assert.AreEqual("evt-2", outcome.View!.BlockedByEventId);
        Assert.AreEqual("evt-2", SessionState.FromBinaryData(await ReadStateAsync(session)).BlockedByEventId);
    }

    [TestMethod]
    [Timeout(90_000, CooperativeCancellation = true)]
    public async Task Cleanup_resets_idle_sessions_and_leaves_busy_and_held_ones()
    {
        var idle = NewSession();
        var queued = NewSession();
        var held = NewSession();
        var stale = NewSession();
        foreach (var session in new[] { idle, queued, held, stale })
            await OperatorCommandTestRows.SeedFailedRowAsync(_store, Endpoint, $"evt-{session}", session, $"msg-{session}", StoreResolutionStatus.Completed);
        await WriteStateAsync(idle, new SessionState { NextDeferralSequence = 2, ReplayedThroughSequence = 1 });
        await WriteStateAsync(queued, new SessionState { NextDeferralSequence = 1, ReplayedThroughSequence = 0 });
        await SendAsync(Endpoint, queued, count: 1);
        await WriteStateAsync(held, new SessionState { NextDeferralSequence = 1, ReplayedThroughSequence = 0 });
        await WriteStateAsync(stale, new SessionState { BlockedByEventId = $"evt-{stale}", NextDeferralSequence = 1, ReplayedThroughSequence = 0 });
        await using var holder = await Client.AcceptSessionAsync(Endpoint, Endpoint, held);

        var preview = await Sut.PreviewCleanupAsync(Endpoint, new SessionCleanupPreviewRequest { IncludeStaleBlocks = true });

        var byId = preview.Sessions.ToDictionary(s => s.SessionId);
        Assert.AreEqual(SessionCleanupCandidateClassification.Idle, byId[idle].Classification);
        Assert.AreEqual(SessionCleanupCandidateClassification.InUse, byId[queued].Classification);
        Assert.AreEqual(SessionCleanupCandidateClassification.InUse, byId[held].Classification);
        Assert.AreEqual(SessionCleanupCandidateClassification.StaleBlock, byId[stale].Classification);

        var result = await Sut.CleanupAsync(Endpoint, new SessionCleanupRequest
        {
            IncludeStaleBlocks = true,
            Sessions = preview.Sessions.Select(s => new SessionCleanupSelection { SessionId = s.SessionId, StateVersion = s.StateVersion ?? "" }).ToList(),
        });

        Assert.AreEqual(2, result.ResetCount, string.Join("; ", result.Skipped.Select(s => $"{s.SessionId}: {s.Reason}").Concat(result.Errors)));
        Assert.IsTrue(SessionState.FromBinaryData(await ReadStateAsync(idle)).IsEmpty());
        Assert.IsTrue(SessionState.FromBinaryData(await ReadStateAsync(stale)).IsEmpty());
        Assert.AreEqual(1, SessionState.FromBinaryData(await ReadStateAsync(queued)).NextDeferralSequence);
        Assert.AreEqual(1, SessionState.FromBinaryData(await holder.GetSessionStateAsync()).NextDeferralSequence);
        CollectionAssert.AreEquivalent(new[] { queued, held }, result.Skipped.Select(s => s.SessionId).ToList());
    }

    // ───────── Helpers ─────────

    private static string NewSession() => $"s-{Guid.NewGuid():N}";

    private static async Task WriteStateAsync(string sessionId, SessionState state)
    {
        await using var receiver = await Client.AcceptSessionAsync(Endpoint, Endpoint, sessionId);
        await receiver.SetSessionStateAsync(state.ToBinaryData());
    }

    private static async Task<BinaryData?> ReadStateAsync(string sessionId)
    {
        await using var receiver = await Client.AcceptSessionAsync(Endpoint, Endpoint, sessionId);
        return await receiver.GetSessionStateAsync();
    }

    /// <summary>Sends messages the endpoint subscription's <c>user.To</c> rule accepts.</summary>
    private static async Task SendAsync(string to, string sessionId, int count)
    {
        await using var sender = Client.CreateSender(Endpoint);
        for (var i = 0; i < count; i++)
        {
            var message = new ServiceBusMessage($"m{i}") { SessionId = sessionId, MessageId = $"{sessionId}-{to}-{i}" };
            message.ApplicationProperties["To"] = to;
            await sender.SendMessageAsync(message);
        }
    }

    /// <summary>Parks messages on the Deferred subscription the way the endpoint does.</summary>
    private static async Task SendDeferredAsync(string sessionId, int count)
    {
        await using var sender = Client.CreateSender(Endpoint);
        for (var i = 0; i < count; i++)
        {
            var message = new ServiceBusMessage($"d{i}") { SessionId = sessionId, MessageId = $"{sessionId}-deferred-{i}" };
            message.ApplicationProperties["To"] = Deferred;
            message.ApplicationProperties["OriginalSessionId"] = sessionId;
            await sender.SendMessageAsync(message);
        }
    }

    private sealed class SingleEndpointPlatform : Platform
    {
        public SingleEndpointPlatform() => AddEndpoint(new Crm());
    }

    private sealed class Crm : NimBus.Core.Endpoints.Endpoint
    {
    }
}
