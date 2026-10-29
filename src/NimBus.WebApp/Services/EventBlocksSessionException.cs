using System;

namespace NimBus.WebApp.Services;

/// <summary>
/// Thrown when an operator asks to delete the stored event that blocks its session (#288). Deleting
/// it would leave the session blocked by an event no one can see, so the event has to be skipped
/// (or resubmitted) first.
/// </summary>
public class EventBlocksSessionException : InvalidOperationException
{
    public EventBlocksSessionException(string eventId)
        : base($"Event '{eventId}' blocks its session, so deleting it would leave the session blocked. " +
            "Skip it first (or resubmit it); then it can be deleted.")
    {
        EventId = eventId;
    }

    /// <summary>The event that blocks its session.</summary>
    public string EventId { get; }
}
