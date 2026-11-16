namespace Erp.Adapter.Functions.Clients;

public interface IServiceModeClient
{
    Task<bool> IsServiceModeEnabledAsync(CancellationToken cancellationToken);

    /// <summary>
    /// The current error-mode switch, the failure reason selected in erp-web and the share of
    /// messages that fail. Fail-open: when erp-api cannot be read the mode reports disabled.
    /// </summary>
    Task<ErrorModeSnapshot> GetErrorModeAsync(CancellationToken cancellationToken);
}

/// <param name="Enabled">Whether handlers should throw.</param>
/// <param name="Reason">An <c>ErpFailureReasons</c> id, possibly <c>random</c>; null means the default generic failure.</param>
/// <param name="FailureRate">The share of messages that fail, from 0 to 1; 1 fails every message.</param>
public sealed record ErrorModeSnapshot(bool Enabled, string? Reason, double FailureRate = 1.0)
{
    public static readonly ErrorModeSnapshot Disabled = new(false, null);
}
