using System;
using System.Collections.Concurrent;

namespace NimBus.WebApp.Services;

/// <summary>
/// Process-wide record of hand-off settlements this WebApp instance has sent but the
/// Resolver has not yet projected. Settling only publishes a control message: the parked
/// row stays Pending+Handoff until the Resolver processes it, so without this record a
/// receive or a second settle in that window would see the hand-off as still open
/// (ADR-012). Registered as a singleton so receive and settle share it across requests.
/// </summary>
/// <remarks>
/// A claim is keyed by endpoint, event id and the parked row's message id, so an event
/// parked again by a new message (resubmit) is a new hand-off. Claims expire after
/// <see cref="HoldTime"/> so a settlement the Resolver never projects cannot hide the
/// hand-off forever. The record is per process: with several WebApp instances, a
/// receive or settle that lands on another instance inside the projection window is
/// still possible, and delivery stays at-least-once there.
/// </remarks>
public sealed class HandoffSettlementTracker
{
    /// <summary>How long a sent settlement keeps its hand-off claimed.</summary>
    public static readonly TimeSpan HoldTime = TimeSpan.FromMinutes(5);

    private readonly ConcurrentDictionary<(string EndpointId, string EventId, string MessageId), DateTimeOffset> _claims = new();
    private readonly TimeProvider _time;

    /// <summary>Initialises the tracker with the clock used to expire claims.</summary>
    public HandoffSettlementTracker(TimeProvider timeProvider)
    {
        _time = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    /// <summary>
    /// Claims the parked hand-off for settlement. Returns <c>false</c> when another
    /// settlement of the same parked message already holds the claim.
    /// </summary>
    public bool TryBegin(string endpointId, string eventId, string? parkedMessageId)
    {
        var now = _time.GetUtcNow();
        PruneExpired(now);

        var key = Key(endpointId, eventId, parkedMessageId);
        var expiresAt = now + HoldTime;
        while (true)
        {
            if (_claims.TryAdd(key, expiresAt))
                return true;
            if (!_claims.TryGetValue(key, out var current))
                continue;
            if (current > now)
                return false;
            if (_claims.TryUpdate(key, expiresAt, current))
                return true;
        }
    }

    /// <summary>Releases a claim whose settlement was never sent, so a retry can settle.</summary>
    public void Abandon(string endpointId, string eventId, string? parkedMessageId)
        => _claims.TryRemove(Key(endpointId, eventId, parkedMessageId), out _);

    /// <summary>Whether a settlement of the parked hand-off is in flight.</summary>
    public bool IsInFlight(string endpointId, string eventId, string? parkedMessageId)
        => _claims.TryGetValue(Key(endpointId, eventId, parkedMessageId), out var expiresAt)
            && expiresAt > _time.GetUtcNow();

    private void PruneExpired(DateTimeOffset now)
    {
        foreach (var claim in _claims)
        {
            if (claim.Value <= now)
                _claims.TryRemove(claim);
        }
    }

    private static (string, string, string) Key(string endpointId, string eventId, string? parkedMessageId)
        => (endpointId, eventId, parkedMessageId ?? string.Empty);
}
