using System;

namespace NimBus.Core.Messages.Exceptions;

/// <summary>
/// An endpoint received a message whose routing key it doesn't accept, or whose routing-key
/// property disagrees with its payload (Spec 045 §5), or the unmatched consumer received a
/// message no endpoint claims (§6). Raised inside a <see cref="PermanentFailureException"/>,
/// so the message is dead-lettered with this text as its reason and its session is not blocked.
/// </summary>
public sealed class RoutingKeyRejectedException : Exception
{
    /// <summary>Creates the exception with <paramref name="message"/> as the reason.</summary>
    /// <param name="message">Why the routing key was rejected.</param>
    public RoutingKeyRejectedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception with a reason and the error that caused it.</summary>
    /// <param name="message">Why the routing key was rejected.</param>
    /// <param name="innerException">The error that caused it.</param>
    public RoutingKeyRejectedException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
