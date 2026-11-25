using System;
using System.Collections.Generic;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Asks a store to erase one event copy, the pair (<see cref="EndpointId"/>, <see cref="EventId"/>),
/// through <see cref="IMessageTrackingStore.EraseEventCopyAsync"/> (Spec 049).
/// </summary>
/// <param name="EndpointId">The endpoint the event copy is tracked on.</param>
/// <param name="EventId">The event id of the copy.</param>
/// <param name="ErasureId">The erasure run, written into the redaction markers.</param>
/// <param name="ProducerEndpointId">
/// The endpoint that produced the event. Compose audit entries are stored under it, so it is
/// needed to find the ones that hold the event's payload. Null when unknown.
/// </param>
/// <param name="EventTypeId">The event type, needed for the same Compose search. Null when unknown.</param>
/// <param name="ComposeConfirmed">
/// True when the Compose audit entries of the pair already ended <see cref="ErasurePlaceState.Redacted"/>
/// or <see cref="ErasurePlaceState.AlreadyAbsent"/>: earlier in the same run (the verify call), or in an
/// earlier finished erasure. The store then reports the Compose place absent when it finds no payload
/// left to match against, instead of <see cref="ErasureOutstandingReasons.RetryContextMissing"/>.
/// </param>
public sealed record EventErasureRequest(
    string EndpointId,
    string EventId,
    string ErasureId,
    string? ProducerEndpointId,
    string? EventTypeId,
    bool ComposeConfirmed);

/// <summary>A kind of place in the message store that can hold a copy of an event's data.</summary>
public enum ErasurePlace
{
    /// <summary>Tracking rows of the pair, in every status and session, archived and soft-deleted included.</summary>
    TrackingRow,

    /// <summary>The event's "reported" marker.</summary>
    EventReport,

    /// <summary>Every message-history entry of the event on the endpoint.</summary>
    MessageHistory,

    /// <summary>Audit entries of the event whose data or comment can carry payload or free text.</summary>
    EventAudits,

    /// <summary>Compose audit entries, stored without an event id, that hold the event's payload.</summary>
    ComposeAudits,
}

/// <summary>How one place ended in an erasure.</summary>
public enum ErasurePlaceState
{
    /// <summary>The items found were deleted.</summary>
    Removed,

    /// <summary>The items found were kept, with every field that can hold payload overwritten.</summary>
    Redacted,

    /// <summary>Nothing was left to remove or redact.</summary>
    AlreadyAbsent,

    /// <summary>The place was neither cleaned nor confirmed absent; see the reason.</summary>
    Outstanding,
}

/// <summary>The reasons a store reports a place <see cref="ErasurePlaceState.Outstanding"/>.</summary>
public static class ErasureOutstandingReasons
{
    /// <summary>A visible tracking row of the pair blocks its session, so nothing was changed.</summary>
    public const string BlocksSession = "BlocksSession";

    /// <summary>The write (or the read before it) failed with a provider error.</summary>
    public const string WriteFailed = "WriteFailed";

    /// <summary>The place was kept on purpose, because the Compose audits still need it to be matched.</summary>
    public const string RetainedForRetry = "RetainedForRetry";

    /// <summary>The identifiers or the payload needed to search the place are not available.</summary>
    public const string RetryContextMissing = "RetryContextMissing";
}

/// <summary>The result of one place in an erasure.</summary>
/// <param name="Place">The place.</param>
/// <param name="State">How it ended.</param>
/// <param name="Count">Items removed or redacted; 0 unless <paramref name="State"/> is Removed or Redacted.</param>
/// <param name="OutstandingReason">One of <see cref="ErasureOutstandingReasons"/>; null unless Outstanding.</param>
public sealed record ErasurePlaceResult(
    ErasurePlace Place,
    ErasurePlaceState State,
    int Count,
    string? OutstandingReason)
{
    /// <summary>A place that was cleaned: <paramref name="count"/> items, or none (already absent).</summary>
    public static ErasurePlaceResult Done(ErasurePlace place, ErasurePlaceState cleaned, int count) =>
        count > 0
            ? new ErasurePlaceResult(place, cleaned, count, null)
            : new ErasurePlaceResult(place, ErasurePlaceState.AlreadyAbsent, 0, null);

    /// <summary>A place left outstanding for <paramref name="reason"/>.</summary>
    public static ErasurePlaceResult Outstanding(ErasurePlace place, string reason) =>
        new(place, ErasurePlaceState.Outstanding, 0, reason);
}

/// <summary>
/// The result of <see cref="IMessageTrackingStore.EraseEventCopyAsync"/>: exactly one entry per
/// <see cref="ErasurePlace"/>, in the enum's order.
/// </summary>
/// <param name="EndpointId">The endpoint of the erased copy.</param>
/// <param name="EventId">The event id of the erased copy.</param>
/// <param name="Places">One result per place.</param>
public sealed record EventErasureResult(
    string EndpointId,
    string EventId,
    IReadOnlyList<ErasurePlaceResult> Places)
{
    /// <summary>The result for <paramref name="place"/>.</summary>
    public ErasurePlaceResult this[ErasurePlace place]
    {
        get
        {
            foreach (var result in Places)
            {
                if (result.Place == place)
                    return result;
            }

            throw new KeyNotFoundException($"No result for {place}.");
        }
    }

    /// <summary>True when no place is outstanding.</summary>
    public bool IsComplete
    {
        get
        {
            foreach (var result in Places)
            {
                if (result.State == ErasurePlaceState.Outstanding)
                    return false;
            }

            return true;
        }
    }

    /// <summary>A result with every place outstanding for <paramref name="reason"/>.</summary>
    public static EventErasureResult AllOutstanding(string endpointId, string eventId, string reason)
    {
        var places = new List<ErasurePlaceResult>();
        foreach (var place in Enum.GetValues<ErasurePlace>())
            places.Add(ErasurePlaceResult.Outstanding(place, reason));
        return new EventErasureResult(endpointId, eventId, places);
    }
}
