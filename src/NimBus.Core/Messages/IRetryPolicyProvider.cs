using System;

namespace NimBus.Core.Messages;

/// <summary>
/// Provides retry policies for failed messages.
/// Implementations can source policies from configuration, code, or external stores.
/// </summary>
public interface IRetryPolicyProvider
{
    /// <summary>
    /// Gets the retry policy for a given event type and failure context.
    /// Returns null if no retry should be attempted.
    /// </summary>
    /// <param name="eventTypeId">The event type that failed.</param>
    /// <param name="exceptionMessage">The exception message from the failure.</param>
    /// <param name="endpoint">The endpoint that failed (optional).</param>
    /// <returns>A retry policy, or null if no retry is configured.</returns>
    RetryPolicy? GetRetryPolicy(string eventTypeId, string exceptionMessage, string? endpoint = null);

    /// <summary>
    /// Gets the retry policy for a given event type and the exception the handler failed with.
    /// Returns null if no retry should be attempted.
    /// </summary>
    /// <remarks>
    /// The message handler calls this overload. The default implementation forwards to
    /// <see cref="GetRetryPolicy(string, string, string?)"/> with the text
    /// <c>"{exception.InnerException} {exception}"</c>, so existing providers keep working.
    /// Override it to match on exception types.
    /// </remarks>
    /// <param name="eventTypeId">The event type that failed.</param>
    /// <param name="exception">The exception the handler failed with, including its inner exceptions.</param>
    /// <param name="endpoint">The endpoint that failed (optional).</param>
    /// <returns>A retry policy, or null if no retry is configured.</returns>
    RetryPolicy? GetRetryPolicy(string eventTypeId, Exception exception, string? endpoint = null) =>
        GetRetryPolicy(eventTypeId, $"{exception?.InnerException} {exception}", endpoint);
}
