using System;
using NimBus.Core.Messages;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Provider-neutral rules for shaping <see cref="States.BlockedMessageEvent"/> results.
/// Every storage provider must apply the same rules so operators see identical
/// blocked-event data regardless of the configured store.
/// </summary>
public static class BlockedEventRules
{
    /// <summary>
    /// True when <paramref name="originatingMessageId"/> is the
    /// <see cref="Constants.Self"/> placeholder — the message originated from
    /// its own publish rather than a forwarded/replayed parent. Case-insensitive
    /// and null-safe: the single platform-wide definition of "self", matching
    /// the OrdinalIgnoreCase comparisons in ResponseService/PublisherClient.
    /// </summary>
    public static bool IsSelfOriginating(string? originatingMessageId)
        => string.Equals(originatingMessageId, Constants.Self, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Resolves the originating message id shown to operators. Messages whose
    /// <paramref name="originatingMessageId"/> is the <see cref="Constants.Self"/>
    /// placeholder originate from their own last message; everything else passes
    /// through unchanged. Null-safe on both inputs.
    /// </summary>
    public static string ResolveOriginatingId(string? originatingMessageId, string? lastMessageId)
        => IsSelfOriginating(originatingMessageId)
            ? lastMessageId ?? string.Empty
            : originatingMessageId ?? string.Empty;

    /// <summary>
    /// True when <paramref name="row"/> is the kind of event that blocks its session: a handler
    /// failure (<see cref="ResolutionStatus.Failed"/>) or a pending hand-off (Pending with
    /// <c>PendingSubStatus</c> <c>"Handoff"</c>). Those are the only two outcomes the strict
    /// handler blocks a session on, and the session stays blocked until a skip, a resubmit or a
    /// settlement reaches the endpoint. Deleting the row alone would leave the session blocked by
    /// an event no one can see any more (#288).
    /// </summary>
    public static bool BlocksItsSession(UnresolvedEvent row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.ResolutionStatus == ResolutionStatus.Failed
            || (row.ResolutionStatus == ResolutionStatus.Pending
                && string.Equals(row.PendingSubStatus, "Handoff", StringComparison.OrdinalIgnoreCase));
    }
}
