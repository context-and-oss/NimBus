using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Text;

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
    /// parked with a sequence up to it has been replayed. <see langword="null"/> when no replay
    /// has been recorded: in a new session, or in state written before this field existed (or
    /// rewritten by such a version). Then every assigned sequence counts as waiting, because
    /// nothing says which parked messages were replayed.
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
    /// Takes the next deferral sequence.
    /// </summary>
    /// <returns>The deferral sequence for the message being parked.</returns>
    public int TakeDeferralSequence() => NextDeferralSequence++;

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
    /// Whether a message parked in this session has not been replayed yet. Without a recorded
    /// position every assigned sequence counts as waiting. If the parked messages were in fact
    /// replayed before the position existed, the first event parked behind them requests a
    /// drain, and its replay advances the position past them.
    /// </summary>
    /// <returns><see langword="true"/> while a parked message awaits its replay.</returns>
    public bool HasUnreplayedMessages() =>
        (ReplayedThroughSequence ?? -1) < NextDeferralSequence - 1;

    /// <summary>
    /// Clears the block.
    /// </summary>
    public void ClearBlock()
    {
        BlockedByEventId = null;
        BlockedByMessageId = null;
    }

    /// <summary>
    /// Clears what an operator's "reprocess deferred" resets: the block, the legacy deferred
    /// sequence numbers and any pending drain request. The deferral sequence and the replay
    /// position stay, so events arriving before the reprocess drain's replays still park behind
    /// them, and new parks don't reuse the sequences of replays already in flight.
    /// </summary>
    public void ClearForReprocess()
    {
        ClearBlock();
        _legacyDeferredSequenceNumbers = new List<long>();
        DrainRequestedForSequence = null;
        DrainRequestedAtUtc = null;
    }

    /// <summary>
    /// Clears the state after an operator purged the session's messages. As
    /// <see cref="ClearForReprocess"/>, and every sequence assigned so far counts as replayed,
    /// because the parked messages are gone. The deferral sequence stays, so a replay still in
    /// flight can't be mistaken for a message parked after the purge.
    /// </summary>
    public void ClearAfterPurge()
    {
        ClearForReprocess();
        DeferredCount = 0;
        if (NextDeferralSequence > 0)
            ReplayedThroughSequence = NextDeferralSequence - 1;
    }

    /// <summary>
    /// Reads session state as Service Bus stores it; empty or missing data is a new state.
    /// </summary>
    /// <param name="data">The stored session state, or <see langword="null"/>.</param>
    /// <returns>The session state.</returns>
    public static SessionState FromBinaryData(BinaryData? data) =>
        data == null || data.ToMemory().Length == 0
            ? new SessionState()
            : data.ToObjectFromJson<SessionState>()!;

    /// <summary>
    /// Serializes the session state for Service Bus; an empty state becomes empty data, which
    /// clears the stored state.
    /// </summary>
    /// <returns>The data to store.</returns>
    public BinaryData ToBinaryData() =>
        IsEmpty()
            // Empty BinaryData instead of null: the Azure Functions Worker SDK throws a
            // NullReferenceException when SetSessionStateAsync is passed null.
            ? new BinaryData(Array.Empty<byte>())
            : new BinaryData(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(this)));

    public bool IsEmpty() =>
        BlockedByEventId == null &&
        _legacyDeferredSequenceNumbers.Count == 0 &&
        DeferredCount == 0 &&
        NextDeferralSequence == 0 &&
        ReplayedThroughSequence == null &&
        DrainRequestedForSequence == null;

    /// <summary>
    /// Returns true if there are any deferred messages (legacy or deferred-subscription).
    /// </summary>
    public bool HasDeferredMessages() =>
        DeferredCount > 0 || _legacyDeferredSequenceNumbers.Count > 0;
}
