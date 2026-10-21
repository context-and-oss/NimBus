using System;
using System.Collections.Generic;

namespace NimBus.Core.Messages;

public class SessionState
{
    private List<long> _legacyDeferredSequenceNumbers = new();

    /// <summary>
    /// Legacy: sequence numbers of messages parked with the Azure Service Bus defer API, which
    /// NimBus no longer writes or drains (spec 027 §3). The value still round-trips so legacy
    /// state is not lost, but it no longer blocks the session.
    /// </summary>
    [Obsolete("Legacy Service Bus defer state; no longer written or drained (spec 027 §3). Drain legacy deferred messages with the nb CLI. Removed in the next major version.")]
    public List<long> DeferredSequenceNumbers
    {
        get => _legacyDeferredSequenceNumbers;
        set => _legacyDeferredSequenceNumbers = value ?? new List<long>();
    }

    public string? BlockedByEventId { get; set; }

    /// <summary>
    /// The source MessageId of the delivery that blocked the session, stored and cleared
    /// together with <see cref="BlockedByEventId"/>. A successful resubmission of the blocking
    /// event records it in the inbox, so a later delivery of the original is a duplicate.
    /// </summary>
    public string? BlockedByMessageId { get; set; }

    /// <summary>
    /// Count of messages deferred to the separate deferred subscription.
    /// </summary>
    public int DeferredCount { get; set; }

    /// <summary>
    /// Next sequence number to assign for ordering deferred messages.
    /// </summary>
    public int NextDeferralSequence { get; set; }

    /// <summary>
    /// The highest deferral sequence whose replay has reached the endpoint; every message
    /// parked with a sequence up to it has been replayed. <see langword="null"/> until the
    /// session parks or replays a message after this field was introduced.
    /// </summary>
    public int? ReplayedThroughSequence { get; set; }

    /// <summary>
    /// The deferral sequence of the message, parked behind replays, for which a replay was
    /// last requested; cleared when that message's replay arrives.
    /// </summary>
    public int? DrainRequestedForSequence { get; set; }

    /// <summary>
    /// When the drain recorded in <see cref="DrainRequestedForSequence"/> was requested.
    /// </summary>
    public DateTime? DrainRequestedAtUtc { get; set; }

    /// <summary>
    /// Takes the next deferral sequence. The first message parked without a recorded replay
    /// position starts it just below its own sequence, so the guard covers that message.
    /// </summary>
    /// <returns>The deferral sequence for the message being parked.</returns>
    public int TakeDeferralSequence()
    {
        int sequence = NextDeferralSequence++;
        ReplayedThroughSequence ??= sequence - 1;
        return sequence;
    }

    /// <summary>
    /// Records the replay of the message parked with <paramref name="deferralSequence"/>. A
    /// sequence at or above <see cref="NextDeferralSequence"/> was assigned before the state
    /// was cleared and is ignored.
    /// </summary>
    /// <param name="deferralSequence">The replay's deferral sequence.</param>
    public void MarkReplayed(int deferralSequence)
    {
        if (deferralSequence < NextDeferralSequence)
            ReplayedThroughSequence = Math.Max(ReplayedThroughSequence ?? deferralSequence, deferralSequence);
    }

    /// <summary>
    /// Whether a message parked in this session has not been replayed yet.
    /// </summary>
    /// <returns><see langword="true"/> while a parked message awaits its replay.</returns>
    public bool HasUnreplayedMessages() =>
        ReplayedThroughSequence is { } replayedThrough && replayedThrough < NextDeferralSequence - 1;

    public bool IsEmpty() =>
        BlockedByEventId == null &&
        _legacyDeferredSequenceNumbers.Count == 0 &&
        DeferredCount == 0;

    /// <summary>
    /// Returns true if there are any deferred messages (legacy or deferred-subscription).
    /// </summary>
    public bool HasDeferredMessages() =>
        DeferredCount > 0 || _legacyDeferredSequenceNumbers.Count > 0;
}
