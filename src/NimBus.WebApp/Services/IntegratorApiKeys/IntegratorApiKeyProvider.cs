using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services.IntegratorApiKeys;

/// <summary>The issued integrator API keys, read from the shared record through a short cache.</summary>
public interface IIntegratorApiKeyProvider
{
    /// <summary>
    /// The keys, re-read at most every <see cref="IntegratorApiKeyProvider.SuccessTtl"/> after a
    /// success and every <see cref="IntegratorApiKeyProvider.FailureTtl"/> after a failure. Null when
    /// no snapshot younger than <see cref="IntegratorApiKeyProvider.MaxSnapshotAge"/> is available
    /// (before the first read, or after a long store outage), so every key is refused then.
    /// </summary>
    Task<IntegratorApiKeySettings?> GetAsync();

    /// <summary>Replaces this instance's snapshot at once, after a save through the Admin API.</summary>
    void Accept(IntegratorApiKeySettings settings);

    /// <summary>
    /// Reads the keys from the store now, bypassing the cache, for a fresh role check before a
    /// change. Falls back to <see cref="GetAsync"/> when the read fails.
    /// </summary>
    Task<IntegratorApiKeySettings?> GetFreshAsync();
}

/// <summary>
/// Singleton cache over <see cref="IIntegratorApiKeyStore.GetIntegratorApiKeySettings"/>. Unlike the
/// agent key cache, a snapshot is trusted for at most <see cref="MaxSnapshotAge"/> while reads
/// keep failing, so a key revoked on another instance stops working within that time even
/// during a store outage.
/// </summary>
public sealed partial class IntegratorApiKeyProvider : IIntegratorApiKeyProvider, IDisposable
{
    /// <summary>How long a successful read is reused; also how long a revoked key may keep
    /// working on another instance.</summary>
    public static readonly TimeSpan SuccessTtl = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait before retrying a failed read.</summary>
    public static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(5);

    /// <summary>How old a snapshot may get while reads keep failing before every key is refused.</summary>
    public static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromMinutes(2);

    private readonly IIntegratorApiKeyStore? _store;
    private readonly ILogger<IntegratorApiKeyProvider> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private Entry? _entry;

    /// <summary>Creates the provider. Without a store every key is refused.</summary>
    public IntegratorApiKeyProvider(ILogger<IntegratorApiKeyProvider> logger, TimeProvider time, IIntegratorApiKeyStore? store = null)
    {
        _store = store;
        _logger = logger;
        _time = time;
    }

    /// <inheritdoc/>
    public async Task<IntegratorApiKeySettings?> GetAsync()
    {
        if (_store is null)
            return null;

        var entry = Volatile.Read(ref _entry);
        if (entry is not null && _time.GetUtcNow() < entry.NextReadAt)
            return Usable(entry);

        // Single-flight: every keyed request passes here, so one read refreshes for all.
        await _refresh.WaitAsync().ConfigureAwait(false);
        try
        {
            entry = Volatile.Read(ref _entry);
            if (entry is not null && _time.GetUtcNow() < entry.NextReadAt)
                return Usable(entry);

            try
            {
                var settings = await _store.GetIntegratorApiKeySettings().ConfigureAwait(false);
                var now = _time.GetUtcNow();
                Volatile.Write(ref _entry, new Entry(settings, now, now + SuccessTtl));
                return settings;
            }
            catch (Exception ex)
            {
                LogReadFailed(_logger, ex);
                var kept = new Entry(entry?.Settings, entry?.ReadAt ?? DateTimeOffset.MinValue, _time.GetUtcNow() + FailureTtl);
                Volatile.Write(ref _entry, kept);
                return Usable(kept);
            }
        }
        finally
        {
            _refresh.Release();
        }
    }

    /// <inheritdoc/>
    public async Task<IntegratorApiKeySettings?> GetFreshAsync()
    {
        if (_store is null)
            return null;

        try
        {
            var settings = await _store.GetIntegratorApiKeySettings().ConfigureAwait(false);
            Accept(settings);
            return settings;
        }
        catch (Exception ex)
        {
            LogReadFailed(_logger, ex);
            return await GetAsync().ConfigureAwait(false);
        }
    }

    /// <inheritdoc/>
    public void Accept(IntegratorApiKeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var now = _time.GetUtcNow();
        Volatile.Write(ref _entry, new Entry(settings.Clone(), now, now + SuccessTtl));
    }

    /// <inheritdoc/>
    public void Dispose() => _refresh.Dispose();

    // Fail closed: a snapshot older than MaxSnapshotAge is not trusted.
    private IntegratorApiKeySettings? Usable(Entry entry)
        => entry.Settings is not null && _time.GetUtcNow() - entry.ReadAt <= MaxSnapshotAge ? entry.Settings : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Integrator API key read failed; keeping the last snapshot for at most two minutes")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);

    private sealed record Entry(IntegratorApiKeySettings? Settings, DateTimeOffset ReadAt, DateTimeOffset NextReadAt);
}
