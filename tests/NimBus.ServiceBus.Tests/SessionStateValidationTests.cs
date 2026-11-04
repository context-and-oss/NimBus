#pragma warning disable CA1707, CA2007
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;

namespace NimBus.ServiceBus.Tests;

/// <summary>
/// Operators replace a session's state with JSON from Operations → Session state, so the JSON
/// is parsed strictly and checked against the invariants the endpoint's ordering guard relies on.
/// </summary>
[TestClass]
public class SessionStateValidationTests
{
    [TestMethod]
    public void TryParseJson_RoundTripsStoredState()
    {
        var stored = new SessionState
        {
            BlockedByEventId = "evt-1",
            BlockedByMessageId = "msg-1",
            DeferredCount = 2,
            NextDeferralSequence = 3,
            ReplayedThroughSequence = 0,
        };
        var json = stored.ToBinaryData().ToString();

        Assert.IsTrue(SessionState.TryParseJson(json, out var parsed, out var errors), string.Join("; ", errors));
        Assert.AreEqual("evt-1", parsed!.BlockedByEventId);
        Assert.AreEqual("msg-1", parsed.BlockedByMessageId);
        Assert.AreEqual(2, parsed.DeferredCount);
        Assert.AreEqual(3, parsed.NextDeferralSequence);
        Assert.AreEqual(0, parsed.ReplayedThroughSequence);
    }

    [TestMethod]
    public void TryParseJson_EmptyObject_IsAnEmptyState()
    {
        Assert.IsTrue(SessionState.TryParseJson("{}", out var parsed, out _));
        Assert.IsTrue(parsed!.IsEmpty());
    }

    [TestMethod]
    [DataRow("{\"BlockedBy\":\"evt-1\"}", "BlockedBy")]
    [DataRow("not json", "JSON")]
    [DataRow("null", "object")]
    [DataRow("[]", "object")]
    [DataRow("{\"DeferredCount\":\"many\"}", "DeferredCount")]
    public void TryParseJson_RejectsJsonThatIsNotSessionState(string json, string expectedInError)
    {
        Assert.IsFalse(SessionState.TryParseJson(json, out var parsed, out var errors));
        Assert.IsNull(parsed);
        StringAssert.Contains(string.Join("; ", errors), expectedInError);
    }

    [TestMethod]
    public void Validate_AcceptsAConsistentState()
    {
        var state = new SessionState
        {
            DeferredCount = 1,
            NextDeferralSequence = 2,
            ReplayedThroughSequence = -1,
            DrainRequestedForSequence = 1,
            DrainRequestedAtUtc = DateTime.UtcNow,
        };

        Assert.AreEqual(0, state.Validate().Count);
    }

    [TestMethod]
    public void Validate_RejectsNegativeCounts()
    {
        var errors = new SessionState { DeferredCount = -1, NextDeferralSequence = -2 }.Validate();

        Assert.AreEqual(2, errors.Count);
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(-2)]
    public void Validate_RejectsAReplayPositionOutsideTheAssignedSequences(int replayedThrough)
    {
        var errors = new SessionState { NextDeferralSequence = 2, ReplayedThroughSequence = replayedThrough }.Validate();

        StringAssert.Contains(string.Join("; ", errors), nameof(SessionState.ReplayedThroughSequence));
    }

    [TestMethod]
    public void Validate_RejectsADrainForASequenceNeverAssigned()
    {
        var errors = new SessionState { NextDeferralSequence = 1, DrainRequestedForSequence = 1 }.Validate();

        StringAssert.Contains(string.Join("; ", errors), nameof(SessionState.DrainRequestedForSequence));
    }

    [TestMethod]
    public void Validate_RejectsADrainTimestampWithoutASequence()
    {
        var errors = new SessionState { DrainRequestedAtUtc = DateTime.UtcNow }.Validate();

        StringAssert.Contains(string.Join("; ", errors), nameof(SessionState.DrainRequestedAtUtc));
    }

    [TestMethod]
    [DataRow(null, "msg-1")]
    [DataRow(" ", null)]
    public void Validate_RejectsAnIncompleteBlock(string? eventId, string? messageId)
    {
        var errors = new SessionState { BlockedByEventId = eventId, BlockedByMessageId = messageId }.Validate();

        StringAssert.Contains(string.Join("; ", errors), nameof(SessionState.BlockedByEventId));
    }

    [TestMethod]
    public void MarkAllReplayed_CountsEveryAssignedSequenceAsReplayedAndKeepsTheBlock()
    {
        var state = new SessionState
        {
            BlockedByEventId = "evt-1",
            DeferredCount = 2,
            NextDeferralSequence = 3,
            ReplayedThroughSequence = 0,
            DrainRequestedForSequence = 1,
            DrainRequestedAtUtc = DateTime.UtcNow,
        };

        state.MarkAllReplayed();

        Assert.AreEqual("evt-1", state.BlockedByEventId);
        Assert.AreEqual(0, state.DeferredCount);
        Assert.AreEqual(3, state.NextDeferralSequence);
        Assert.AreEqual(2, state.ReplayedThroughSequence);
        Assert.IsNull(state.DrainRequestedForSequence);
        Assert.IsNull(state.DrainRequestedAtUtc);
        Assert.IsFalse(state.HasUnreplayedMessages());
    }

    [TestMethod]
    public void TryParseJson_RejectsJsonThatBreaksAnInvariant()
    {
        Assert.IsFalse(SessionState.TryParseJson("{\"DeferredCount\":-1}", out var parsed, out var errors));
        Assert.IsNull(parsed);
        StringAssert.Contains(string.Join("; ", errors), nameof(SessionState.DeferredCount));
    }
}
