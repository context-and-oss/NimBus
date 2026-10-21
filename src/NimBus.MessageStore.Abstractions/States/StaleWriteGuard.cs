using System;
using NimBus.Core.Messages;

namespace NimBus.MessageStore;

/// <summary>
/// Store-side rule that stops a late copy of a message (auto-forward lag, a
/// <c>ScheduleRedelivery</c> copy, a dead-letter replay, a double deferred drain) from
/// reopening or downgrading an audit row that already holds a later outcome.
///
/// <para>Applied by every provider <em>inside</em> its non-terminal status write, atomically
/// against the row it is about to replace, and pinned for all providers by
/// <c>MessageTrackingStoreConformanceTests</c>. See Spec 030 for the incident and the
/// decision table.</para>
///
/// <para><strong>Maintenance:</strong> any future <see cref="MessageType"/> that the Resolver
/// projects as <see cref="ResolutionStatus.Pending"/> or <see cref="ResolutionStatus.Deferred"/>
/// must be classified here — as a request-stage write, a control request, or a handoff park —
/// otherwise it silently falls through as unguarded and can overwrite a settled row.</para>
/// </summary>
public static class StaleWriteGuard
{
    /// <summary>True for statuses that settle a row: a later non-terminal write is suspect.</summary>
    public static bool IsTerminal(ResolutionStatus s) =>
        s is ResolutionStatus.Completed or ResolutionStatus.Skipped or ResolutionStatus.Failed
          or ResolutionStatus.DeadLettered or ResolutionStatus.Unsupported;

    /// <summary>
    /// True for the message types that represent the request stage of an event's life.
    /// <see cref="MessageType.Unknown"/> is included deliberately (lenient): CLI and test seeds
    /// write rows without a message type and must stay refreshable.
    /// </summary>
    public static bool IsRequestStage(MessageType t) =>
        t is MessageType.EventRequest or MessageType.DeferralResponse or MessageType.Unknown;

    /// <summary>
    /// True for operator or manager intent — the message types that may reopen a settled row.
    /// </summary>
    public static bool IsControlRequest(MessageType t) =>
        t is MessageType.ResubmissionRequest or MessageType.SkipRequest or MessageType.RetryRequest
          or MessageType.ContinuationRequest or MessageType.HandoffCompletedRequest
          or MessageType.HandoffFailedRequest;

    /// <summary>
    /// True when the incoming write is subject to the rule, and so needs the current row to be
    /// read before it can be applied. Terminal writes and unguarded message types are exempt,
    /// which keeps the hot terminal path free of an extra read. The one terminal exception, a
    /// duplicate skip, is recognised by its reason: see <see cref="IsDuplicateSkip"/>.
    /// </summary>
    public static bool IsGuarded(ResolutionStatus incomingStatus, MessageType incomingType) =>
        !IsTerminal(incomingStatus)
        && (incomingType is MessageType.EventRequest or MessageType.DeferralResponse
                or MessageType.PendingHandoffResponse
            || IsControlRequest(incomingType));

    /// <summary>
    /// True for the SkipResponse an endpoint's inbox sends for a delivery it already processed
    /// (reason <see cref="NimBus.Core.Inbox.InboxMiddleware.DuplicateReason"/>). Unlike an
    /// operator's skip it records no decision of its own, so it must not rewrite a Completed row:
    /// a deferred replay redelivered after its handler and inbox record committed would otherwise
    /// turn the replay's Completed outcome into Skipped (Spec 041 §2.7). It is the one terminal
    /// write the rule guards; providers read the current row for it.
    /// </summary>
    public static bool IsDuplicateSkip(UnresolvedEvent incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        return incoming.MessageType == MessageType.SkipResponse
            && string.Equals(incoming.Reason, NimBus.Core.Inbox.InboxMiddleware.DuplicateReason, StringComparison.Ordinal);
    }

    /// <summary>
    /// True when the incoming write may replace <paramref name="current"/>.
    /// </summary>
    /// <param name="incomingStatus">The status the write would leave on the row.</param>
    /// <param name="incoming">The projection the caller wants to store.</param>
    /// <param name="current">The row as it exists today; <c>null</c> when absent.</param>
    public static bool Allows(ResolutionStatus incomingStatus, UnresolvedEvent incoming, UnresolvedEvent? current)
    {
        ArgumentNullException.ThrowIfNull(incoming);

        if (current is not null && IsDuplicateSkip(incoming))
        {
            return current.ResolutionStatus != ResolutionStatus.Completed;
        }

        if (current is null || !IsGuarded(incomingStatus, incoming.MessageType))
        {
            return true;
        }

        // Ancestor check: responses stamp ParentMessageId with the message they answer, so a row
        // naming this message as its parent already holds this message's outcome. Original
        // requests arrive with ParentMessageId = "self", so first writes are unaffected.
        if (!string.IsNullOrEmpty(current.ParentMessageId)
            && !string.IsNullOrEmpty(incoming.LastMessageId)
            && string.Equals(current.ParentMessageId, incoming.LastMessageId, StringComparison.Ordinal))
        {
            return false;
        }

        return incoming.MessageType switch
        {
            // A request copy may refresh its own projection or a deferral of itself; it may never
            // replace a row written by a control request, a handoff park or a terminal response.
            MessageType.EventRequest or MessageType.DeferralResponse =>
                !IsTerminal(current.ResolutionStatus) && IsRequestStage(current.MessageType),

            // Failed / DeadLettered / Unsupported must stay open: a policy retry parks a handoff
            // straight from a Failed row, and a resubmission's park can overtake the
            // resubmission's own throttled Pending write.
            MessageType.PendingHandoffResponse =>
                current.ResolutionStatus is not (ResolutionStatus.Completed or ResolutionStatus.Skipped),

            // Control requests: operator intent stays unconditional past the ancestor check.
            _ => true,
        };
    }
}
