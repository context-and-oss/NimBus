namespace NimBus.Extensions.IntegrationIntelligence;

/// <summary>Machine-readable service failure returned by the API layer.</summary>
public sealed class ClassificationServiceException : Exception
{
    /// <summary>Creates a service exception.</summary>
    public ClassificationServiceException(string code, int statusCode, string? message = null, Exception? inner = null, string? telemetryOutcome = null)
        : base(message ?? code, inner)
    {
        Code = code;
        StatusCode = statusCode;
        TelemetryOutcome = telemetryOutcome;
    }

    /// <summary>Error code.</summary>
    public string Code { get; }

    /// <summary>HTTP status code.</summary>
    public int StatusCode { get; }

    /// <summary>Optional low-cardinality telemetry outcome for this failure.</summary>
    public string? TelemetryOutcome { get; }
}
