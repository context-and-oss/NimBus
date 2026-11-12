namespace NimBus.Core.Messages;

/// <summary>Options for <see cref="ResponseService"/>.</summary>
public sealed class ResponseServiceOptions
{
    /// <summary>Longest stack trace stored on an error response.</summary>
    public const int MaximumStackTraceLength = 8000;

    /// <summary>
    /// Store the failing handler exception's stack trace (its frames, not its message) on the
    /// error response, cut at <see cref="MaximumStackTraceLength"/> characters (Spec 044 Phase 3).
    /// Off by default: a trace names the adapter's code, which the WebApp shows to endpoint
    /// Readers and, when an Owner allows it, to agents as failure evidence.
    /// </summary>
    public bool IncludeStackTrace { get; set; }
}
