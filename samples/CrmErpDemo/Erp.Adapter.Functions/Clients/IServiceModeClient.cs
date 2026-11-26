namespace Erp.Adapter.Functions.Clients;

public interface IServiceModeClient
{
    Task<bool> IsServiceModeEnabledAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The current error-mode switch, the failure reason selected in erp-web and the share of
    /// messages that fail. Fail-open: when erp-api cannot be read the mode reports disabled.
    /// </summary>
    Task<ErrorModeSnapshot> GetErrorModeAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Whether erp-api answers and whether it is in service mode, for the adapter's health. Unlike
    /// <see cref="IsServiceModeEnabledAsync"/>, an erp-api that does not answer is reported as such.
    /// </summary>
    Task<ErpModeProbe> ProbeAsync(CancellationToken cancellationToken);
}

/// <param name="Reachable">Whether erp-api answered.</param>
/// <param name="ServiceMode">Whether ERP is in service mode; false when it did not answer.</param>
/// <param name="Problem">Why erp-api could not be read, when it could not: the kind of failure, no detail.</param>
public sealed record ErpModeProbe(bool Reachable, bool ServiceMode, string? Problem);

/// <param name="Enabled">Whether handlers should throw.</param>
/// <param name="Reason">An <c>ErpFailureReasons</c> id, possibly <c>random</c>; null means the default generic failure.</param>
/// <param name="FailureRate">The share of messages that fail, from 0 to 1; 1 fails every message.</param>
public sealed record ErrorModeSnapshot(bool Enabled, string? Reason, double FailureRate = 1.0)
{
    public static readonly ErrorModeSnapshot Disabled = new(false, null);
}
