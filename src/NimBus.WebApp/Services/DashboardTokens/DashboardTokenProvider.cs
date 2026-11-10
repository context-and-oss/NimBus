using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services.DashboardTokens;

/// <summary>The issued dashboard tokens, read from the shared record through a short cache.</summary>
public interface IDashboardTokenProvider
{
    /// <summary>
    /// The tokens, re-read at most every <see cref="DashboardTokenProvider.SuccessTtl"/> after a
    /// success and every <see cref="DashboardTokenProvider.FailureTtl"/> after a failure. Null when
    /// no snapshot younger than <see cref="DashboardTokenProvider.MaxSnapshotAge"/> is available
    /// (before the first read, or after a long store outage), so every token is refused then.
    /// </summary>
    Task<DashboardTokenSettings?> GetAsync();

    /// <summary>Replaces this instance's snapshot at once, after a save through the Admin API.</summary>
    void Accept(DashboardTokenSettings settings);
}

/// <summary>
/// Singleton cache over <see cref="IDashboardTokenStore.GetDashboardTokenSettings"/>. Unlike the
/// agent key cache, a snapshot is trusted for at most <see cref="MaxSnapshotAge"/> while reads
/// keep failing, so a token revoked on another instance stops working within that time even
/// during a store outage.
/// </summary>
public sealed partial class DashboardTokenProvider : IDashboardTokenProvider, IDisposable
{
    /// <summary>How long a successful read is reused; also how long a revoked token may keep
    /// working on another instance.</summary>
    public static readonly TimeSpan SuccessTtl = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait before retrying a failed read.</summary>
    public static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(5);

    /// <summary>How old a snapshot may get while reads keep failing before every token is refused.</summary>
    public static readonly TimeSpan MaxSnapshotAge = TimeSpan.FromMinutes(2);

    private readonly IDashboardTokenStore? _store;
    private readonly ILogger<DashboardTokenProvider> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _refresh = new(1, 1);
    private Entry? _entry;

    /// <summary>Creates the provider. Without a store every token is refused.</summary>
    public DashboardTokenProvider(ILogger<DashboardTokenProvider> logger, TimeProvider time, IDashboardTokenStore? store = null)
    {
        _store = store;
        _logger = logger;
        _time = time;
    }

    /// <inheritdoc/>
    public async Task<DashboardTokenSettings?> GetAsync()
    {
        if (_store is null)
            return null;

        var entry = Volatile.Read(ref _entry);
        if (entry is not null && _time.GetUtcNow() < entry.NextReadAt)
            return Usable(entry);

        // Single-flight: every wall poll passes here, so one read refreshes for all.
        await _refresh.WaitAsync().ConfigureAwait(false);
        try
        {
            entry = Volatile.Read(ref _entry);
            if (entry is not null && _time.GetUtcNow() < entry.NextReadAt)
                return Usable(entry);

            try
            {
                var settings = await _store.GetDashboardTokenSettings().ConfigureAwait(false);
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
    public void Accept(DashboardTokenSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var now = _time.GetUtcNow();
        Volatile.Write(ref _entry, new Entry(settings.Clone(), now, now + SuccessTtl));
    }

    /// <inheritdoc/>
    public void Dispose() => _refresh.Dispose();

    // Fail closed: a snapshot older than MaxSnapshotAge is not trusted.
    private DashboardTokenSettings? Usable(Entry entry)
        => entry.Settings is not null && _time.GetUtcNow() - entry.ReadAt <= MaxSnapshotAge ? entry.Settings : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dashboard token read failed; keeping the last snapshot for at most two minutes")]
    private static partial void LogReadFailed(ILogger logger, Exception exception);

    private sealed record Entry(DashboardTokenSettings? Settings, DateTimeOffset ReadAt, DateTimeOffset NextReadAt);
}
