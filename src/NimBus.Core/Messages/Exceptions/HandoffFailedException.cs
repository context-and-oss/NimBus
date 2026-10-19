using System;

namespace NimBus.Core.Messages.Exceptions;

/// <summary>
/// Synthesised on the subscriber side when a HandoffFailedRequest is
/// received from the Manager. Carries the operator-supplied error text
/// (and optional error-type tag) so the existing error-response path can
/// flip the audit row Pending → Failed with the failure text preserved
/// verbatim. Never thrown by user code.
/// </summary>
public class HandoffFailedException : Exception
{
    public HandoffFailedException(string message, string? originalErrorType = null) : base(message)
    {
        OriginalErrorType = originalErrorType;
    }

    /// <summary>
    /// Optional error-type tag supplied by the Manager via
    /// <c>ErrorContent.ErrorType</c>. Preserved on the exception for
    /// observability; not currently round-tripped to the response.
    /// </summary>
    public string? OriginalErrorType { get; }
}
