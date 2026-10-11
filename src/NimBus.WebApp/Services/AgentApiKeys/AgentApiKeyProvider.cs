using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services.AgentApiKeys;

/// <summary>The issued agent API keys, read from the shared record through a short cache.</summary>
public interface IAgentApiKeyProvider
{
    /// <summary>
    /// The keys, re-read at most every 30 seconds after a success and every 5 seconds after a
    /// failure. A failed read keeps the last list. Null until the first successful read, so
    /// every key is refused rather than trusted blindly while the store is unreachable.
    /// </summary>
    Task<AgentApiKeySettings?> GetAsync();

    /// <summary>Replaces this instance's list at once, after a save through the Admin API.</summary>
    void Accept(AgentApiKeySettings settings);
}

/// <summary>Singleton cache over <see cref="IEndpointMetadataStore.GetAgentApiKeySettings"/>.</summary>
public sealed class AgentApiKeyProvider : IAgentApiKeyProvider, IDisposable
{
    /// <summary>How long a successful read is reused; also how long a revoked key may keep
    /// working on another instance.</summary>
    public static readonly TimeSpan SuccessTtl = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait before retrying a failed read.</summary>
    public static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(5);

    private readonly IEndpointMetadataStore _store;
    private readonly ILogger<AgentApiKeyProvider> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private Entry? _entry;

    /// <summary>Creates the provider.</summary>
    public AgentApiKeyProvider(IEndpointMetadataStore store, ILogger<AgentApiKeyProvider> logger, TimeProvider time)
    {
        _store = store;
        _logger = logger;
        _time = time;
    }

    /// <inheritdoc/>
    public async Task<AgentApiKeySettings?> GetAsync()
    {
        var entry = Volatile.Read(ref _entry);
        if (entry is not null && _time.GetUtcNow() < entry.ExpiresAt)
            return entry.Settings;

        // Single-flight: every agent request passes here, so one read refreshes for all.
        await _refresh.WaitAsync().ConfigureAwait(false);
        try
        {
            entry = Volatile.Read(ref _entry);
            if (entry is not null && _time.GetUtcNow() < entry.ExpiresAt)
                return entry.Settings;

            try
            {
                var settings = await _store.GetAgentApiKeySettings().ConfigureAwait(false);
                Volatile.Write(ref _entry, new Entry(settings, _time.GetUtcNow() + SuccessTtl));
                return settings;
            }
            catch (Exception ex)
            {
                // Fail closed: keep the last list, or none at all.
                _logger.LogWarning(ex, "Agent API key read failed; keeping the last loaded keys");
                Volatile.Write(ref _entry, new Entry(entry?.Settings, _time.GetUtcNow() + FailureTtl));
                return entry?.Settings;
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <inheritdoc/>
    public void Accept(AgentApiKeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        Volatile.Write(ref _entry, new Entry(settings.Clone(), _time.GetUtcNow() + SuccessTtl));
    }

    /// <inheritdoc/>
    public void Dispose() => _refresh.Dispose();

    private sealed record Entry(AgentApiKeySettings? Settings, DateTimeOffset ExpiresAt);
}
