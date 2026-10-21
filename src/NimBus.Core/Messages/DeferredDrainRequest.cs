using System;

namespace NimBus.Core.Messages;

/// <summary>
/// A drain of a session's parked messages, requested by the first event parked behind replays
/// that had not reached the endpoint yet. It stays pending until the replay of that event
/// arrives.
/// </summary>
/// <param name="DeferralSequence">The deferral sequence of the event that requested the drain.</param>
/// <param name="RequestedAtUtc">When the drain was requested.</param>
public sealed record DeferredDrainRequest(int DeferralSequence, DateTime RequestedAtUtc);
