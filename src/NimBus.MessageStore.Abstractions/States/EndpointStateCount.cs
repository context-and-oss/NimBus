using System;
using System.Collections.Generic;
using System.Text;

namespace NimBus.MessageStore.States;

public class EndpointStateCount
{
    public string EndpointId { get; set; } = null!;
    public int FailedCount { get; set; }
    public int DeferredCount { get; set; }
    public int PendingCount { get; set; }
    public int DeadletterCount { get; set; }
    public int UnsupportedCount { get; set; }
    public DateTime EventTime { get; set; }

    /// <summary>
    /// UTC time the longest-standing open failure was recorded: the earliest
    /// <see cref="UnresolvedEvent.UpdatedAt"/> of a non-deleted message whose status is
    /// Failed or DeadLettered. Null when the endpoint has no open failures.
    /// </summary>
    public DateTime? OldestFailureAt { get; set; }
}
