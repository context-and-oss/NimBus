using NimBus.Core.Messages;
using System.Collections.Generic;

namespace NimBus.Testing;

public class InMemorySessionState
{
    public string? BlockedByEventId { get; set; }

    /// <summary>The source MessageId of the delivery that blocked the session.</summary>
    public string? BlockedByMessageId { get; set; }

    public int DeferredCount { get; set; }
    public int NextDeferralSequence { get; set; }

    /// <summary>The highest deferral sequence whose replay has reached the endpoint.</summary>
    public int? ReplayedThroughSequence { get; set; }

    /// <summary>The drain last requested for a message parked behind replays, until its replay arrives.</summary>
    public DeferredDrainRequest? DrainRequest { get; set; }
    public List<IMessage> DeferredMessages { get; } = new();
}
