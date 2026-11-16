using CrmErpDemo.Contracts.Demo;

namespace Erp.Api;

/// <summary>
/// Demo-only knob: when enabled, ERP adapter handlers throw before doing any work.
/// <see cref="ErpFailureReasons"/> lists the failures <c>Reason</c> can select, including
/// <see cref="ErpFailureReasons.Random"/>, so the Integration Intelligence card in the NimBus
/// WebApp has something realistic to classify. <c>FailureRate</c> is the share of messages that
/// fail; it defaults to 1, every message.
/// </summary>
public sealed class ErrorModeState
{
    private readonly object _gate = new();
    private bool _enabled;
    private string _reason = ErpFailureReasons.Default;
    private double _failureRate = 1.0;
    private DateTimeOffset _changedAt = DateTimeOffset.UtcNow;

    public (bool Enabled, string Reason, double FailureRate, DateTimeOffset ChangedAt) Snapshot()
    {
        lock (_gate)
        {
            return (_enabled, _reason, _failureRate, _changedAt);
        }
    }

    /// <summary>
    /// Updates the switch and, when given, the selected failure and the failure rate. A null
    /// <paramref name="reason"/> or <paramref name="failureRate"/> keeps the current value.
    /// The caller validates both; an unknown reason is ignored here so the state can never hold
    /// a value the adapter cannot map.
    /// </summary>
    public (bool Enabled, string Reason, double FailureRate, DateTimeOffset ChangedAt) Set(
        bool enabled,
        string? reason = null,
        double? failureRate = null)
    {
        var resolved = ErpFailureReasons.Find(reason)?.Id;
        lock (_gate)
        {
            var reasonChanged = resolved is not null && !string.Equals(_reason, resolved, StringComparison.Ordinal);
            var rateChanged = failureRate is { } rate && Math.Abs(_failureRate - rate) > double.Epsilon;
            if (_enabled != enabled || reasonChanged || rateChanged)
            {
                _enabled = enabled;
                if (resolved is not null) _reason = resolved;
                if (failureRate is { } newRate) _failureRate = newRate;
                _changedAt = DateTimeOffset.UtcNow;
            }

            return (_enabled, _reason, _failureRate, _changedAt);
        }
    }
}
