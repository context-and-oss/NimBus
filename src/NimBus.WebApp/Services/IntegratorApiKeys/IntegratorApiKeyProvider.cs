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
    /// (before the first read, or while the store is unreachable), so every key is refused then.
    /// </summary>
    Task<IntegratorApiKeySettings?> GetAsync();

    /// <summary>Replaces this instance's snapshot at once, after a save through the Admin API.</summary>
    void Accept(IntegratorApiKeySettings settings);

    /// <summary>
    /// Reads the keys from the store now, bypassing the cache, for a fresh role check before a
    /// change. Null when the read fails: a change is never authorized from a cached snapshot.
    /// </summary>
    Task<IntegratorApiKeySettings?> GetFreshAsync();
}

/// <summary>
/// Singleton cache over <see cref="IIntegratorApiKeyStore.GetIntegratorApiKeySettings"/> (Spec 045).
/// It fails closed: a snapshot is trusted for at most <see cref="MaxSnapshotAge"/>, also while reads
/// keep failing, so a key revoked on another instance stops working within 30 seconds even during
/// a store outage, and a fresh check that cannot read the store denies. A read that started before
/// a local save never replaces the saved snapshot.
/// </summary>
public sealed partial class IntegratorApiKeyProvider : IIntegratorApiKeyProvider, IDisposable
{
    /// <summary>How long a successful read is reused; also how long a revoked key may keep
    /// working on another instance.</summary>
    public static readonly TimeSpan SuccessTtl = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait before retrying a failed read.</summary>
    public static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How old a snapshot may get before every key is refused. Equal to <see cref="SuccessTtl"/>:
    /// a failed refresh never extends a key's life past the revocation bound.
    /// </summary>
    public static readonly TimeSpan MaxSnapshotAge = SuccessTtl;

    private readonly IIntegratorApiKeyStore? _store;
    private readonly ILogger<IntegratorApiKeyProvider> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private readonly object _publish = new();
    private Entry? _entry;

    // Bumped by every local save, so a read that started earlier cannot publish over it.
    private long _generation;

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

            var generation = Interlocked.Read(ref _generation);
            try
            {
                var settings = await _store.GetIntegratorApiKeySettings().ConfigureAwait(false);
                var now = _time.GetUtcNow();
                return Usable(Publish(new Entry(settings, now, now + SuccessTtl), generation));
            }
            catch (Exception ex)
            {
                LogReadFailed(_logger, ex);
                // Keep the last snapshot's read time: a failure never makes it younger.
                var kept = new Entry(entry?.Settings, entry?.ReadAt ?? DateTimeOffset.MinValue, _time.GetUtcNow() + FailureTtl);
                return Usable(Publish(kept, generation));
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

        var generation = Interlocked.Read(ref _generation);
        try
        {
            var settings = await _store.GetIntegratorApiKeySettings().ConfigureAwait(false);
            var now = _time.GetUtcNow();
            return Publish(new Entry(settings, now, now + SuccessTtl), generation).Settings;
        }
        catch (Exception ex)
        {
            LogReadFailed(_logger, ex);
            return null;
        }
    }

    /// <inheritdoc/>
    public void Accept(IntegratorApiKeySettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var now = _time.GetUtcNow();
        lock (_publish)
        {
            Interlocked.Increment(ref _generation);
            Volatile.Write(ref _entry, new Entry(settings.Clone(), now, now + SuccessTtl));
        }
    }

    /// <inheritdoc/>
    public void Dispose() => _refresh.Dispose();

    // Publishes a read unless a local save happened since it started; then the save's snapshot,
    // which is newer, stays and is returned instead.
    private Entry Publish(Entry read, long startedAtGeneration)
    {
        lock (_publish)
        {
            if (Interlocked.Read(ref _generation) == startedAtGeneration)
            {
                Volatile.Write(ref _entry, read);
                return read;
            }

            return Volatile.Read(ref _entry) ?? read;
        }
    }

    // Fail closed: a snapshot older than MaxSnapshotAge is not trusted.
    private IntegratorApiKeySettings? Usable(Entry entry)
        => entry.Settings is not null && _time.GetUtcNow() - entry.ReadAt <= MaxSnapshotAge ? entry.Settings : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Integrator API key read failed; keys are refused once the last snapshot is 30 seconds old")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);

    private sealed record Entry(IntegratorApiKeySettings? Settings, DateTimeOffset ReadAt, DateTimeOffset NextReadAt);
}
