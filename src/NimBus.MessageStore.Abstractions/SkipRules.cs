using System.Collections.Generic;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Which tracked events a bulk skip may act on. Shared by the WebApp's Operations page and
/// <c>nb container skip</c>, which both send the endpoint one SkipRequest per event.
/// </summary>
public static class SkipRules
{
    /// <summary>
    /// The source statuses a bulk skip accepts: events whose outcome a SkipRequest settles.
    /// A Deferred event is parked behind another event's block, and its parked copy replays and
    /// runs once that block clears, whatever a skip recorded. Pending, TooManyRequests and
    /// Published messages are still on their way and are processed anyway. Completed and Skipped
    /// are terminal.
    /// </summary>
    public static IReadOnlySet<ResolutionStatus> BulkSkipSourceStatuses { get; } = new HashSet<ResolutionStatus>
    {
        ResolutionStatus.Failed,
        ResolutionStatus.DeadLettered,
        ResolutionStatus.Unsupported,
    };
}
