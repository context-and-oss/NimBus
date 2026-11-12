using System.Collections.Concurrent;
using NimBus.MessageStore.Abstractions;

namespace NimBus.WebApp.Services.IntegratorApiKeys;

/// <summary>Records when integrator API keys are used, for the Settings page.</summary>
public interface IIntegratorApiKeyUsageRecorder
{
    /// <summary>Records a use of the key. Never throws.</summary>
    Task RecordAsync(string keyId);
}

/// <summary>
/// Writes a key's last use at most once per <see cref="Interval"/> per instance, so a script
/// calling many times a second costs one store write every few minutes. Best effort: a failed write
/// is logged and only leaves "last used" stale.
/// </summary>
public sealed partial class IntegratorApiKeyUsageRecorder : IIntegratorApiKeyUsageRecorder
{
    /// <summary>Minimum time between two writes for the same key.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IIntegratorApiKeyStore? _store;
    private readonly TimeProvider _time;
    private readonly ILogger<IntegratorApiKeyUsageRecorder> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastWritten = new(StringComparer.Ordinal);

    /// <summary>Creates the recorder.</summary>
    public IntegratorApiKeyUsageRecorder(TimeProvider time, ILogger<IntegratorApiKeyUsageRecorder> logger, IIntegratorApiKeyStore? store = null)
    {
        _store = store;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task RecordAsync(string keyId)
    {
        if (_store is null)
            return;

        var now = _time.GetUtcNow();
        if (_lastWritten.TryGetValue(keyId, out var last) && now - last < Interval)
            return;
        _lastWritten[keyId] = now;

        try
        {
            await _store.RecordIntegratorApiKeyUse(keyId, now.UtcDateTime).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogRecordFailed(_logger, ex, keyId);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording the use of integrator API key {KeyId} failed")]
    private static partial void LogRecordFailed(ILogger logger, Exception exception, string keyId);
}
