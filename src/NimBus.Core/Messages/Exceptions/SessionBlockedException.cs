using System;

namespace NimBus.Core.Messages;

public class SessionBlockedException : Exception
{
    /// <summary>
    /// The event id of the message that originally blocked the session, when known.
    /// Surfaced so lifecycle observers (e.g. notifications) can reference the blocking event.
    /// </summary>
    public string? BlockedByEventId { get; }

    public SessionBlockedException(string message) : base(message)
    {

    }

    public SessionBlockedException(string message, string? blockedByEventId) : base(message)
    {
        BlockedByEventId = blockedByEventId;
    }

    public SessionBlockedException() : base("Session is blocked.")
    {

    }
}
