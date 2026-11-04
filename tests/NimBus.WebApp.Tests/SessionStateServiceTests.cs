#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.Testing.Conformance;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using StoreResolutionStatus = NimBus.MessageStore.ResolutionStatus;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Operations → Session state and Clean up session state (plan 2026-11-03): read a session's
/// state under its lock, change it only at the version the operator saw, and reset idle state.
/// </summary>
[TestClass]
public sealed class SessionStateServiceTests
{
    private const string Endpoint = "Crm";
    private const string Deferred = NimBus.Core.Messages.Constants.DeferredSubscriptionName;

    private readonly FakeSessionStateBus _bus = new();
    private readonly InMemoryMessageStore _store = new();
    private readonly List<SessionStateWrite> _audited = new();

    private SessionStateService Sut => new(_bus, _store, NullLogger<SessionStateService>.Instance);

    // ───────── Lookup ─────────

    [TestMethod]
    public async Task Get_SessionWithoutState_IsEmptyAndHasAVersion()
    {
        var view = await Sut.GetAsync(Endpoint, "s-1");

        Assert.IsTrue(view.IsEmpty);
        Assert.IsTrue(view.Readable);
        Assert.IsFalse(view.Locked);
        Assert.IsNull(view.StateJson);
        Assert.AreEqual(SessionStateService.VersionOf(null), view.StateVersion);
    }

    [TestMethod]
    public async Task Get_BlockedSession_ShowsTheStateQueuedMessagesAndTheBlockingEvent()
    {
        Store("s-1", new SessionState { BlockedByEventId = "evt-1", BlockedByMessageId = "msg-1", DeferredCount = 2, NextDeferralSequence = 2 });
        _bus.AddMessages(Endpoint, "s-1", 1);
        _bus.AddMessages(Deferred, "s-1", 2);
        await OperatorCommandTestRows.SeedFailedRowAsync(_store, Endpoint, "evt-1", "s-1", "msg-1");

        var view = await Sut.GetAsync(Endpoint, "s-1");

        Assert.IsFalse(view.IsEmpty);
        Assert.IsTrue(view.Blocked);
        Assert.AreEqual("evt-1", view.BlockedByEventId);
        Assert.AreEqual(2, view.DeferredCount);
        Assert.IsTrue(view.HasUnreplayedMessages);
        Assert.AreEqual(1, view.ActiveMessageCount);
        Assert.AreEqual(2, view.DeferredSubscriptionCount);
        Assert.AreEqual("Failed", view.BlockingEventStatus);
        StringAssert.Contains(view.StateJson, "evt-1");
    }

    [TestMethod]
    public async Task Get_SessionAReceiverHolds_ReportsItLockedWithoutState()
    {
        _bus.Locked.Add((Endpoint, "s-1"));

        var view = await Sut.GetAsync(Endpoint, "s-1");

        Assert.IsTrue(view.Locked);
        Assert.IsFalse(view.IsEmpty);
        Assert.IsNull(view.StateVersion);
        Assert.IsNotNull(view.Message);
    }

    [TestMethod]
    public async Task Get_UnreadableState_IsShownAsText()
    {
        _bus.SetState(Endpoint, "s-1", BinaryData.FromString("not json"));

        var view = await Sut.GetAsync(Endpoint, "s-1");

        Assert.IsFalse(view.Readable);
        Assert.AreEqual("not json", view.StateJson);
    }

    // ───────── Change ─────────

    [TestMethod]
    public async Task Change_AtAStaleVersion_IsAConflictAndWritesNothing()
    {
        Store("s-1", new SessionState { BlockedByEventId = "evt-1" });

        var outcome = await Change("s-1", SessionStateChangeRequestAction.ClearBlock, expectedVersion: "stale");

        Assert.AreEqual(SessionStateChangeStatus.Conflict, outcome.Status);
        Assert.AreEqual("evt-1", outcome.View!.BlockedByEventId);
        Assert.AreEqual(0, _bus.Writes.Count);
        Assert.AreEqual(0, _audited.Count);
    }

    [TestMethod]
    public async Task Change_OfALockedSession_IsRefused()
    {
        _bus.Locked.Add((Endpoint, "s-1"));

        var outcome = await Change("s-1", SessionStateChangeRequestAction.Reset, expectedVersion: SessionStateService.VersionOf(null));

        Assert.AreEqual(SessionStateChangeStatus.Locked, outcome.Status);
        Assert.AreEqual(0, _bus.Writes.Count);
    }

    [TestMethod]
    public async Task Change_ClearBlock_KeepsTheReplayPositionAndRecordsBeforeAndAfter()
    {
        Store("s-1", new SessionState { BlockedByEventId = "evt-1", BlockedByMessageId = "msg-1", NextDeferralSequence = 3, ReplayedThroughSequence = 2 });

        var outcome = await Change("s-1", SessionStateChangeRequestAction.ClearBlock);

        Assert.AreEqual(SessionStateChangeStatus.Changed, outcome.Status);
        var written = SessionState.FromBinaryData(_bus.GetState(Endpoint, "s-1"));
        Assert.IsNull(written.BlockedByEventId);
        Assert.IsNull(written.BlockedByMessageId);
        Assert.AreEqual(3, written.NextDeferralSequence);
        Assert.AreEqual(2, written.ReplayedThroughSequence);
        Assert.IsFalse(outcome.View!.Blocked);
        Assert.AreEqual(SessionStateService.VersionOf(_bus.GetState(Endpoint, "s-1")), outcome.View.StateVersion);

        var audit = _audited.Single();
        Assert.AreEqual("clearBlock", audit.Action);
        StringAssert.Contains(audit.Before, "evt-1");
        Assert.IsFalse(audit.After!.Contains("evt-1", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task Change_ClearBlock_OfASessionThatIsNotBlocked_IsInvalid()
    {
        Store("s-1", new SessionState { NextDeferralSequence = 1, ReplayedThroughSequence = 0 });

        var outcome = await Change("s-1", SessionStateChangeRequestAction.ClearBlock);

        Assert.AreEqual(SessionStateChangeStatus.Invalid, outcome.Status);
        Assert.AreEqual(0, _bus.Writes.Count);
    }

    [TestMethod]
    public async Task Change_MarkReplayed_IsRefusedWhileMessagesAreParked()
    {
        Store("s-1", new SessionState { DeferredCount = 1, NextDeferralSequence = 1 });
        _bus.AddMessages(Deferred, "s-1", 1);

        var outcome = await Change("s-1", SessionStateChangeRequestAction.MarkReplayed);

        Assert.AreEqual(SessionStateChangeStatus.Invalid, outcome.Status);
        StringAssert.Contains(string.Join(" ", outcome.Errors), "parked");
        Assert.AreEqual(0, _bus.Writes.Count);
    }

    [TestMethod]
    public async Task Change_MarkReplayed_WithNothingParked_CountsEverySequenceReplayed()
    {
        Store("s-1", new SessionState { DeferredCount = 1, NextDeferralSequence = 4 });

        var outcome = await Change("s-1", SessionStateChangeRequestAction.MarkReplayed);

        Assert.AreEqual(SessionStateChangeStatus.Changed, outcome.Status);
        var written = SessionState.FromBinaryData(_bus.GetState(Endpoint, "s-1"));
        Assert.AreEqual(0, written.DeferredCount);
        Assert.AreEqual(3, written.ReplayedThroughSequence);
    }

    [TestMethod]
    public async Task Change_Reset_ClearsTheState()
    {
        Store("s-1", new SessionState { BlockedByEventId = "evt-1", NextDeferralSequence = 2 });

        var outcome = await Change("s-1", SessionStateChangeRequestAction.Reset);

        Assert.AreEqual(SessionStateChangeStatus.Changed, outcome.Status);
        Assert.AreEqual(0, _bus.GetState(Endpoint, "s-1")!.ToArray().Length);
        Assert.IsTrue(outcome.View!.IsEmpty);
        Assert.IsNull(_audited.Single().After);
    }

    [TestMethod]
    public async Task Change_Reset_OfUnreadableState_IsAllowed()
    {
        _bus.SetState(Endpoint, "s-1", BinaryData.FromString("not json"));

        var outcome = await Change("s-1", SessionStateChangeRequestAction.Reset);

        Assert.AreEqual(SessionStateChangeStatus.Changed, outcome.Status);
    }

    [TestMethod]
    public async Task Change_Replace_WritesValidatedJson()
    {
        Store("s-1", new SessionState { BlockedByEventId = "evt-1" });

        var outcome = await Change("s-1", SessionStateChangeRequestAction.Replace,
            stateJson: "{\"DeferredCount\":0,\"NextDeferralSequence\":5,\"ReplayedThroughSequence\":4}");

        Assert.AreEqual(SessionStateChangeStatus.Changed, outcome.Status);
        var written = SessionState.FromBinaryData(_bus.GetState(Endpoint, "s-1"));
        Assert.IsNull(written.BlockedByEventId);
        Assert.AreEqual(5, written.NextDeferralSequence);
        Assert.AreEqual(4, written.ReplayedThroughSequence);
    }

    [TestMethod]
    [DataRow("{\"Unknown\":1}")]
    [DataRow("{\"NextDeferralSequence\":1,\"ReplayedThroughSequence\":7}")]
    [DataRow("")]
    public async Task Change_Replace_WithInvalidJson_IsInvalidAndWritesNothing(string json)
    {
        Store("s-1", new SessionState { BlockedByEventId = "evt-1" });

        var outcome = await Change("s-1", SessionStateChangeRequestAction.Replace, stateJson: json);

        Assert.AreEqual(SessionStateChangeStatus.Invalid, outcome.Status);
        Assert.AreNotEqual(0, outcome.Errors.Count);
        Assert.AreEqual(0, _bus.Writes.Count);
    }

    [TestMethod]
    public async Task Change_WhoseAuditFails_LeavesTheStateUnchanged()
    {
        Store("s-1", new SessionState { BlockedByEventId = "evt-1" });
        var version = SessionStateService.VersionOf(_bus.GetState(Endpoint, "s-1"));

        await Assert.ThrowsExactlyAsync<AuditUnavailableException>(() => Sut.ChangeAsync(Endpoint, "s-1",
            new SessionStateChangeRequest { Action = SessionStateChangeRequestAction.Reset, ExpectedVersion = version },
            _ => throw new AuditUnavailableException("down")));

        Assert.AreEqual(0, _bus.Writes.Count);
    }

    // ───────── Cleanup ─────────

    [TestMethod]
    public async Task Preview_ClassifiesTheSessionsOfStoredEvents()
    {
        await Seed("idle", new SessionState { NextDeferralSequence = 2, ReplayedThroughSequence = 1 });
        await Seed("legacy-idle", new SessionState { NextDeferralSequence = 2 });
        await Seed("stale", new SessionState { BlockedByEventId = "done", NextDeferralSequence = 1, ReplayedThroughSequence = 0 });
        await Seed("blocked", new SessionState { BlockedByEventId = "failing" });
        await Seed("queued", new SessionState { NextDeferralSequence = 1, ReplayedThroughSequence = 0 });
        _bus.AddMessages(Endpoint, "queued", 3);
        await Seed("parked", new SessionState { DeferredCount = 1, NextDeferralSequence = 1 });
        _bus.AddMessages(Deferred, "parked", 1);
        await Seed("pending", new SessionState { NextDeferralSequence = 1, ReplayedThroughSequence = 0 }, StoreResolutionStatus.Pending);
        await Seed("locked", new SessionState { NextDeferralSequence = 1 });
        _bus.Locked.Add((Endpoint, "locked"));
        await Seed("empty", state: null);
        await OperatorCommandTestRows.SeedFailedRowAsync(_store, Endpoint, "done", "stale", "m", StoreResolutionStatus.Completed);
        await OperatorCommandTestRows.SeedFailedRowAsync(_store, Endpoint, "failing", "blocked", "m");

        var preview = await Sut.PreviewCleanupAsync(Endpoint, new SessionCleanupPreviewRequest());

        Assert.AreEqual(9, preview.InspectedCount);
        Assert.AreEqual(1, preview.EmptyCount);
        Assert.IsFalse(preview.Truncated);
        var byId = preview.Sessions.ToDictionary(s => s.SessionId);
        Assert.AreEqual(SessionCleanupCandidateClassification.Idle, byId["idle"].Classification);
        Assert.AreEqual(SessionCleanupCandidateClassification.Idle, byId["legacy-idle"].Classification);
        Assert.AreEqual(SessionCleanupCandidateClassification.StaleBlock, byId["stale"].Classification);
        StringAssert.Contains(byId["stale"].Reason, "Completed");
        foreach (var inUse in new[] { "blocked", "queued", "parked", "pending", "locked" })
            Assert.AreEqual(SessionCleanupCandidateClassification.InUse, byId[inUse].Classification, inUse);
        StringAssert.Contains(byId["queued"].Reason, "3 messages queued");
        StringAssert.Contains(byId["parked"].Reason, "Deferred subscription");
        StringAssert.Contains(byId["pending"].Reason, "pending event");
        Assert.IsFalse(byId.ContainsKey("empty"));
    }

    [TestMethod]
    public async Task Preview_StopsAtMaxSessions_AndSaysSo()
    {
        for (var i = 0; i < 3; i++)
            await Seed($"s-{i}", new SessionState { NextDeferralSequence = 1, ReplayedThroughSequence = 0 });

        var preview = await Sut.PreviewCleanupAsync(Endpoint, new SessionCleanupPreviewRequest { MaxSessions = 2 });

        Assert.AreEqual(2, preview.InspectedCount);
        Assert.IsTrue(preview.Truncated);
    }

    [TestMethod]
    public async Task Preview_OnlyLooksAtEventsUpdatedSinceTheWindow()
    {
        await Seed("s-1", new SessionState { NextDeferralSequence = 1, ReplayedThroughSequence = 0 });

        var preview = await Sut.PreviewCleanupAsync(Endpoint, new SessionCleanupPreviewRequest { UpdatedSince = DateTime.UtcNow.AddHours(1) });

        Assert.AreEqual(0, preview.InspectedCount);
    }

    [TestMethod]
    public async Task Cleanup_ResetsIdleSessions_AndSkipsTheOthersWithAReason()
    {
        await Seed("idle", new SessionState { NextDeferralSequence = 2, ReplayedThroughSequence = 1 });
        await Seed("stale", new SessionState { BlockedByEventId = "gone", NextDeferralSequence = 1, ReplayedThroughSequence = 0 });
        await Seed("changed", new SessionState { NextDeferralSequence = 1, ReplayedThroughSequence = 0 });
        await Seed("busy", new SessionState { NextDeferralSequence = 1, ReplayedThroughSequence = 0 });
        var preview = await Sut.PreviewCleanupAsync(Endpoint, new SessionCleanupPreviewRequest());
        var selections = preview.Sessions
            .Select(s => new SessionCleanupSelection { SessionId = s.SessionId, StateVersion = s.StateVersion })
            .ToList();
        Store("changed", new SessionState { NextDeferralSequence = 2, ReplayedThroughSequence = 1 });
        _bus.AddMessages(Endpoint, "busy", 1);

        var result = await Sut.CleanupAsync(Endpoint, new SessionCleanupRequest { Sessions = selections });

        Assert.AreEqual(1, result.ResetCount);
        Assert.AreEqual(0, _bus.GetState(Endpoint, "idle")!.ToArray().Length);
        var skipped = result.Skipped.ToDictionary(s => s.SessionId, s => s.Reason);
        StringAssert.Contains(skipped["changed"], "changed since the preview");
        StringAssert.Contains(skipped["busy"], "queued");
        StringAssert.Contains(skipped["stale"], "gone");
        Assert.AreEqual(0, result.Errors.Count);
        Assert.IsTrue(SessionState.FromBinaryData(_bus.GetState(Endpoint, "stale")).BlockedByEventId == "gone");
    }

    [TestMethod]
    public async Task Cleanup_ResetsStaleBlocks_WhenIncluded()
    {
        await Seed("stale", new SessionState { BlockedByEventId = "gone", NextDeferralSequence = 1, ReplayedThroughSequence = 0 });
        var preview = await Sut.PreviewCleanupAsync(Endpoint, new SessionCleanupPreviewRequest { IncludeStaleBlocks = true });

        var result = await Sut.CleanupAsync(Endpoint, new SessionCleanupRequest
        {
            IncludeStaleBlocks = true,
            Sessions = preview.Sessions.Select(s => new SessionCleanupSelection { SessionId = s.SessionId, StateVersion = s.StateVersion }).ToList(),
        });

        Assert.AreEqual(1, result.ResetCount);
        Assert.AreEqual(0, _bus.GetState(Endpoint, "stale")!.ToArray().Length);
    }

    // ───────── Helpers ─────────

    private void Store(string sessionId, SessionState state) => _bus.SetState(Endpoint, sessionId, state.ToBinaryData());

    /// <summary>A completed event on the session, so the cleanup finds it, and the session's state.</summary>
    private async Task Seed(string sessionId, SessionState? state, StoreResolutionStatus status = StoreResolutionStatus.Completed)
    {
        await OperatorCommandTestRows.SeedFailedRowAsync(_store, Endpoint, $"evt-{sessionId}", sessionId, $"msg-{sessionId}", status);
        if (state is not null)
            Store(sessionId, state);
    }

    private Task<SessionStateChangeOutcome> Change(
        string sessionId,
        SessionStateChangeRequestAction action,
        string? expectedVersion = null,
        string? stateJson = null) =>
        Sut.ChangeAsync(Endpoint, sessionId, new SessionStateChangeRequest
        {
            Action = action,
            ExpectedVersion = expectedVersion ?? SessionStateService.VersionOf(_bus.GetState(Endpoint, sessionId)),
            StateJson = stateJson,
        }, write =>
        {
            _audited.Add(write);
            return Task.CompletedTask;
        });
}
