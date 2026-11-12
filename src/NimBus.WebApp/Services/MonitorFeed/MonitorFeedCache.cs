using System.Collections.Concurrent;

namespace NimBus.WebApp.Services.MonitorFeed;

/// <summary>A composed Monitor feed value and when it was read from the store.</summary>
/// <typeparam name="T">The composed value.</typeparam>
/// <param name="Value">The value.</param>
/// <param name="GeneratedAtUtc">When the value was composed from the store.</param>
/// <param name="Stale">True when the latest read failed and this is an earlier value.</param>
public sealed record MonitorFeedSnapshot<T>(T Value, DateTime GeneratedAtUtc, bool Stale);

/// <summary>
/// Caches the Monitor feed's compositions, shared by every caller, and keeps them available when
/// the store is not.
/// </summary>
public interface IMonitorFeedCache
{
    /// <summary>
    /// The value cached under <paramref name="key"/>, or a fresh composition cached for
    /// <paramref name="ttl"/> (single-flight). When composing fails and an earlier composition of
    /// <paramref name="operation"/> exists, that one is returned marked stale and cached for
    /// <paramref name="ttl"/>, so an outage costs one store attempt per TTL however many walls
    /// poll. With nothing earlier, the failure propagates.
    /// </summary>
    /// <param name="operation">Identifies the feed operation across keys (keys may carry a time bucket).</param>
    /// <param name="key">The cache key for this composition.</param>
    /// <param name="ttl">How long a composition, or a stale fallback, is reused.</param>
    /// <param name="compose">Reads the store. Must not depend on the caller.</param>
    Task<MonitorFeedSnapshot<T>> GetAsync<T>(string operation, string key, TimeSpan ttl, Func<Task<T>> compose)
        where T : class;
}

/// <inheritdoc cref="IMonitorFeedCache"/>
public sealed partial class MonitorFeedCache : IMonitorFeedCache
{
    private readonly IStoreResultCache _cache;
    private readonly TimeProvider _time;
    private readonly ILogger<MonitorFeedCache> _logger;
    private readonly ConcurrentDictionary<string, object> _lastGood = new(StringComparer.Ordinal);

    /// <summary>Creates the cache.</summary>
    public MonitorFeedCache(IStoreResultCache cache, TimeProvider time, ILogger<MonitorFeedCache> logger)
    {
        _cache = cache;
        _time = time;
        _logger = logger;
    }

    /// <inheritdoc/>
    public async Task<MonitorFeedSnapshot<T>> GetAsync<T>(string operation, string key, TimeSpan ttl, Func<Task<T>> compose)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(compose);
        try
        {
            return await _cache.GetOrCreateAsync(key, ttl, async () =>
            {
                var value = await compose().ConfigureAwait(false);
                var snapshot = new MonitorFeedSnapshot<T>(value, _time.GetUtcNow().UtcDateTime, Stale: false);
                _lastGood[operation] = snapshot;
                return snapshot;
            }).ConfigureAwait(false);
        }
#pragma warning disable CA1031 // Any store failure falls back to the last good composition.
        catch (Exception ex) when (ex is not OperationCanceledException
            && _lastGood.TryGetValue(operation, out var last) && last is MonitorFeedSnapshot<T> previous)
#pragma warning restore CA1031
        {
            LogServingStale(_logger, ex, operation, previous.GeneratedAtUtc);
            var stale = previous with { Stale = true };
            return await _cache.GetOrCreateAsync(key, ttl, () => Task.FromResult(stale)).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Monitor feed {Operation} could not be read; serving the result from {GeneratedAtUtc:o}")]
    private static partial void LogServingStale(ILogger logger, Exception exception, string operation, DateTime generatedAtUtc);
}
