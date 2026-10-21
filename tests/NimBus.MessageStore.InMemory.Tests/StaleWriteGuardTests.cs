#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;

namespace NimBus.MessageStore.InMemory.Tests;

/// <summary>
/// One case per row of the Spec 030 decision table. The conformance suite pins that every
/// provider applies the same rule; these tests pin the rule itself.
/// </summary>
[TestClass]
public class StaleWriteGuardTests
{
    private static UnresolvedEvent Row(
        ResolutionStatus status,
        MessageType messageType,
        string lastMessageId = "m-1",
        string? parentMessageId = null) => new()
        {
            ResolutionStatus = status,
            MessageType = messageType,
            LastMessageId = lastMessageId,
            ParentMessageId = parentMessageId,
        };

    [TestMethod]
    [DataRow(ResolutionStatus.Completed, true)]
    [DataRow(ResolutionStatus.Skipped, true)]
    [DataRow(ResolutionStatus.Failed, true)]
    [DataRow(ResolutionStatus.DeadLettered, true)]
    [DataRow(ResolutionStatus.Unsupported, true)]
    [DataRow(ResolutionStatus.Pending, false)]
    [DataRow(ResolutionStatus.Deferred, false)]
    [DataRow(ResolutionStatus.Published, false)]
    [DataRow(ResolutionStatus.TooManyRequests, false)]
    public void IsTerminal_matches_the_settled_statuses(ResolutionStatus status, bool expected)
    {
        Assert.AreEqual(expected, StaleWriteGuard.IsTerminal(status));
    }

    [TestMethod]
    [DataRow(MessageType.EventRequest, true)]
    [DataRow(MessageType.DeferralResponse, true)]
    [DataRow(MessageType.Unknown, true)]
    [DataRow(MessageType.ResubmissionRequest, false)]
    [DataRow(MessageType.PendingHandoffResponse, false)]
    [DataRow(MessageType.ResolutionResponse, false)]
    public void IsRequestStage_is_lenient_about_Unknown(MessageType type, bool expected)
    {
        Assert.AreEqual(expected, StaleWriteGuard.IsRequestStage(type));
    }

    [TestMethod]
    [DataRow(MessageType.ResubmissionRequest, true)]
    [DataRow(MessageType.SkipRequest, true)]
    [DataRow(MessageType.RetryRequest, true)]
    [DataRow(MessageType.ContinuationRequest, true)]
    [DataRow(MessageType.HandoffCompletedRequest, true)]
    [DataRow(MessageType.HandoffFailedRequest, true)]
    [DataRow(MessageType.EventRequest, false)]
    [DataRow(MessageType.PendingHandoffResponse, false)]
    [DataRow(MessageType.ProcessDeferredRequest, false)]
    public void IsControlRequest_covers_operator_and_manager_intent(MessageType type, bool expected)
    {
        Assert.AreEqual(expected, StaleWriteGuard.IsControlRequest(type));
    }

    [TestMethod]
    public void IsGuarded_exempts_terminal_writes_and_unguarded_types()
    {
        // Terminal writes never read the row first.
        Assert.IsFalse(StaleWriteGuard.IsGuarded(ResolutionStatus.Completed, MessageType.ResolutionResponse));
        Assert.IsFalse(StaleWriteGuard.IsGuarded(ResolutionStatus.Failed, MessageType.ErrorResponse));

        // Nor do CLI and test seeds, which carry no message type.
        Assert.IsFalse(StaleWriteGuard.IsGuarded(ResolutionStatus.Pending, MessageType.Unknown));

        Assert.IsTrue(StaleWriteGuard.IsGuarded(ResolutionStatus.Pending, MessageType.EventRequest));
        Assert.IsTrue(StaleWriteGuard.IsGuarded(ResolutionStatus.Deferred, MessageType.DeferralResponse));
        Assert.IsTrue(StaleWriteGuard.IsGuarded(ResolutionStatus.Pending, MessageType.PendingHandoffResponse));
        Assert.IsTrue(StaleWriteGuard.IsGuarded(ResolutionStatus.Pending, MessageType.ResubmissionRequest));
    }

    [TestMethod]
    public void Allows_an_absent_row()
    {
        Assert.IsTrue(StaleWriteGuard.Allows(
            ResolutionStatus.Pending, Row(ResolutionStatus.Pending, MessageType.EventRequest), current: null));
    }

    [TestMethod]
    public void Allows_every_terminal_write_over_any_row()
    {
        var row = Row(ResolutionStatus.Completed, MessageType.ResolutionResponse);

        Assert.IsTrue(StaleWriteGuard.Allows(
            ResolutionStatus.Failed, Row(ResolutionStatus.Failed, MessageType.ErrorResponse), row));
    }

    [TestMethod]
    public void Allows_an_unguarded_message_type_over_any_row()
    {
        var row = Row(ResolutionStatus.Completed, MessageType.ResolutionResponse);

        Assert.IsTrue(StaleWriteGuard.Allows(
            ResolutionStatus.Pending, Row(ResolutionStatus.Pending, MessageType.Unknown), row));
    }

    [TestMethod]
    public void Refuses_a_write_the_row_already_answers()
    {
        // ResponseService stamps a response's ParentMessageId with the message it answers, so a
        // row naming this message as its parent already holds this message's outcome.
        var row = Row(ResolutionStatus.Completed, MessageType.ResolutionResponse, "rsp-1", parentMessageId: "rs-1");
        var rescheduled = Row(ResolutionStatus.Pending, MessageType.ResubmissionRequest, "rs-1");

        Assert.IsFalse(StaleWriteGuard.Allows(ResolutionStatus.Pending, rescheduled, row));
    }

    [TestMethod]
    public void The_ancestor_check_ignores_empty_ids()
    {
        // Original requests arrive with ParentMessageId = "self"; first writes must be unaffected,
        // and an empty id on either side must never be read as a match.
        var emptyParent = Row(ResolutionStatus.Pending, MessageType.EventRequest, "req-1", parentMessageId: "");
        Assert.IsTrue(StaleWriteGuard.Allows(
            ResolutionStatus.Pending, Row(ResolutionStatus.Pending, MessageType.EventRequest, ""), emptyParent));

        var selfParent = Row(ResolutionStatus.Pending, MessageType.EventRequest, "req-1", parentMessageId: "self");
        Assert.IsTrue(StaleWriteGuard.Allows(
            ResolutionStatus.Pending, Row(ResolutionStatus.Pending, MessageType.EventRequest, "req-2"), selfParent));
    }

    [TestMethod]
    [DataRow(MessageType.EventRequest)]
    [DataRow(MessageType.DeferralResponse)]
    public void A_request_stage_write_refreshes_a_request_stage_row(MessageType incoming)
    {
        foreach (var rowType in new[] { MessageType.EventRequest, MessageType.DeferralResponse, MessageType.Unknown })
        {
            foreach (var rowStatus in new[] { ResolutionStatus.Pending, ResolutionStatus.Deferred })
            {
                Assert.IsTrue(
                    StaleWriteGuard.Allows(
                        ResolutionStatus.Pending,
                        Row(ResolutionStatus.Pending, incoming, "m-2"),
                        Row(rowStatus, rowType)),
                    $"{incoming} over {rowStatus}/{rowType}");
            }
        }
    }

    [TestMethod]
    [DataRow(ResolutionStatus.Completed)]
    [DataRow(ResolutionStatus.Skipped)]
    [DataRow(ResolutionStatus.Failed)]
    [DataRow(ResolutionStatus.DeadLettered)]
    [DataRow(ResolutionStatus.Unsupported)]
    public void A_request_stage_write_is_refused_over_a_terminal_row(ResolutionStatus settled)
    {
        Assert.IsFalse(StaleWriteGuard.Allows(
            ResolutionStatus.Pending,
            Row(ResolutionStatus.Pending, MessageType.EventRequest, "m-2"),
            Row(settled, MessageType.ResolutionResponse)));

        Assert.IsFalse(StaleWriteGuard.Allows(
            ResolutionStatus.Deferred,
            Row(ResolutionStatus.Deferred, MessageType.DeferralResponse, "m-2"),
            Row(settled, MessageType.ResolutionResponse)));
    }

    [TestMethod]
    [DataRow(MessageType.ResubmissionRequest)]
    [DataRow(MessageType.SkipRequest)]
    [DataRow(MessageType.HandoffCompletedRequest)]
    [DataRow(MessageType.HandoffFailedRequest)]
    [DataRow(MessageType.PendingHandoffResponse)]
    public void A_request_stage_write_is_refused_over_a_non_request_stage_Pending_row(MessageType rowType)
    {
        Assert.IsFalse(StaleWriteGuard.Allows(
            ResolutionStatus.Pending,
            Row(ResolutionStatus.Pending, MessageType.EventRequest, "m-2"),
            Row(ResolutionStatus.Pending, rowType)));
    }

    [TestMethod]
    [DataRow(ResolutionStatus.Completed, false)]
    [DataRow(ResolutionStatus.Skipped, false)]
    [DataRow(ResolutionStatus.Failed, true)]
    [DataRow(ResolutionStatus.DeadLettered, true)]
    [DataRow(ResolutionStatus.Unsupported, true)]
    [DataRow(ResolutionStatus.Deferred, true)]
    [DataRow(ResolutionStatus.Pending, true)]
    public void A_handoff_park_is_refused_only_over_Completed_and_Skipped(ResolutionStatus rowStatus, bool expected)
    {
        Assert.AreEqual(expected, StaleWriteGuard.Allows(
            ResolutionStatus.Pending,
            Row(ResolutionStatus.Pending, MessageType.PendingHandoffResponse, "ho-1"),
            Row(rowStatus, MessageType.ResolutionResponse)));
    }

    [TestMethod]
    [DataRow(MessageType.ResubmissionRequest)]
    [DataRow(MessageType.SkipRequest)]
    [DataRow(MessageType.RetryRequest)]
    [DataRow(MessageType.ContinuationRequest)]
    [DataRow(MessageType.HandoffCompletedRequest)]
    [DataRow(MessageType.HandoffFailedRequest)]
    public void A_control_request_reopens_any_row(MessageType control)
    {
        foreach (var rowStatus in new[]
        {
            ResolutionStatus.Completed, ResolutionStatus.Skipped, ResolutionStatus.Failed,
            ResolutionStatus.DeadLettered, ResolutionStatus.Unsupported, ResolutionStatus.Pending,
            ResolutionStatus.Deferred,
        })
        {
            Assert.IsTrue(
                StaleWriteGuard.Allows(
                    ResolutionStatus.Pending,
                    Row(ResolutionStatus.Pending, control, "ctl-1"),
                    Row(rowStatus, MessageType.ResolutionResponse, parentMessageId: "req-1")),
                $"{control} over {rowStatus}");
        }
    }

    [TestMethod]
    [DataRow(ResolutionStatus.Completed, false)]
    [DataRow(ResolutionStatus.Skipped, true)]
    [DataRow(ResolutionStatus.Failed, true)]
    [DataRow(ResolutionStatus.Pending, true)]
    [DataRow(ResolutionStatus.Deferred, true)]
    public void A_duplicate_skip_is_refused_only_over_Completed(ResolutionStatus rowStatus, bool expected)
    {
        // The inbox answers a delivery it already processed with a SkipResponse. A Completed row
        // already records that processing; the duplicate must not rewrite it as Skipped.
        var duplicateSkip = Row(ResolutionStatus.Skipped, MessageType.SkipResponse, "dup-1");
        duplicateSkip.Reason = NimBus.Core.Inbox.InboxMiddleware.DuplicateReason;

        Assert.AreEqual(expected, StaleWriteGuard.Allows(
            ResolutionStatus.Skipped,
            duplicateSkip,
            Row(rowStatus, MessageType.ResolutionResponse, parentMessageId: "req-1")));
    }

    [TestMethod]
    public void An_operator_skip_still_replaces_a_Completed_row()
    {
        Assert.IsTrue(StaleWriteGuard.Allows(
            ResolutionStatus.Skipped,
            Row(ResolutionStatus.Skipped, MessageType.SkipResponse, "skip-1"),
            Row(ResolutionStatus.Completed, MessageType.ResolutionResponse, parentMessageId: "req-1")));
    }
}
