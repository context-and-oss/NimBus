using System.Collections.Concurrent;
using NimBus.MessageStore.Abstractions;

namespace NimBus.WebApp.Services.AgentApiKeys;

/// <summary>Records when agent API keys are used, for the Settings page.</summary>
public interface IAgentApiKeyUsageRecorder
{
    /// <summary>Records a use of the key. Never throws.</summary>
    Task RecordAsync(string keyId);
}

/// <summary>
/// Writes a key's last use at most once per <see cref="Interval"/> per instance, so an agent's
/// long-poll loop costs one store write every few minutes. Best effort: a failed write is
/// logged and only leaves "last used" stale.
/// </summary>
public sealed class AgentApiKeyUsageRecorder : IAgentApiKeyUsageRecorder
{
    /// <summary>Minimum time between two writes for the same key.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly IEndpointMetadataStore _store;
    private readonly TimeProvider _time;
    private readonly ILogger<AgentApiKeyUsageRecorder> _logger;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastWritten = new(StringComparer.Ordinal);

    /// <summary>Creates the recorder.</summary>
    public AgentApiKeyUsageRecorder(IEndpointMetadataStore store, TimeProvider time, ILogger<AgentApiKeyUsageRecorder> logger)
    {
        _store = store;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task RecordAsync(string keyId)
    {
        var now = _time.GetUtcNow();
        if (_lastWritten.TryGetValue(keyId, out var last) && now - last < Interval)
            return;
        _lastWritten[keyId] = now;

        try
        {
            await _store.RecordAgentApiKeyUse(keyId, now.UtcDateTime).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Recording the use of agent API key {KeyId} failed", keyId);
        }
    }
}
