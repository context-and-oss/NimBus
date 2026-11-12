using System;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services;

/// <summary>
/// Short-TTL cache for read-only message-store results that back high-frequency,
/// user-facing views (Monitor wall status counts, Insights metrics aggregates).
///
/// <para>Deliberately a targeted service rather than a store decorator: operational
/// code paths (e.g. <see cref="AdminService"/> resubmit decisions) must always see
/// live store data, so callers opt in per call site. Cache below the authorization
/// layer only — keys must identify the data (endpoint id, period), never the user,
/// and cached values must not contain per-user filtering.</para>
/// </summary>
public interface IStoreResultCache
{
    /// <summary>
    /// Returns the cached value for <paramref name="key"/>, or runs
    /// <paramref name="factory"/> (single-flight: concurrent callers share one
    /// in-flight invocation) and caches its result for <paramref name="ttl"/>.
    /// Faulted factory results are evicted immediately — exceptions propagate
    /// to every awaiting caller but are never cached. A zero or negative
    /// <paramref name="ttl"/> caches nothing: the factory runs for every call.
    /// </summary>
    Task<T> GetOrCreateAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory);

    /// <summary>
    /// Returns a value whose read began after this call, and caches it for <paramref name="ttl"/>
    /// like <see cref="GetOrCreateAsync"/>. A completed value is replaced. While a read is still
    /// running, one follow-up read is queued behind it and shared by every refresh that arrives
    /// before the follow-up starts, so concurrent refreshes cost at most one extra read instead
    /// of one each; the running read still completes for the callers already awaiting it.
    /// </summary>
    Task<T> RefreshAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory);

    /// <summary>
    /// Drops the cached value for <paramref name="key"/> so the next <see cref="GetOrCreateAsync"/>
    /// runs its factory. An in-flight creation already awaited by other callers completes for them.
    /// </summary>
    void Remove(string key);
}

/// <summary>
/// <see cref="IMemoryCache"/>-backed implementation. Registered as a singleton
/// (controllers are transient — a per-request instance would never hit).
/// </summary>
public sealed class StoreResultCache : IStoreResultCache
{
    private readonly IMemoryCache _cache;
    private readonly object _creationLock = new();

    public StoreResultCache(IMemoryCache cache)
    {
        _cache = cache ?? throw new ArgumentNullException(nameof(cache));
    }

    public async Task<T> GetOrCreateAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        // IMemoryCache rejects a non-positive lifetime; "cache for no time" means don't cache.
        if (ttl <= TimeSpan.Zero) return await factory();

        // A Flight gives single-flight semantics: the first caller creates and
        // starts the read, concurrent callers await the same instance. The
        // lock only guards cache-entry creation, never the factory itself.
        Flight<T> flight;
        lock (_creationLock)
        {
            flight = _cache.GetOrCreate(key, entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = ttl;
                return new Flight<T>(factory, null);
            })!;
        }

        return await AwaitAsync(key, flight);
    }

    public async Task<T> RefreshAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(factory);
        if (ttl <= TimeSpan.Zero) return await factory();

        Flight<T> flight;
        lock (_creationLock)
        {
            var current = _cache.TryGetValue(key, out Flight<T>? cached) ? cached : null;
            if (current is { ReadStarted: false })
            {
                // Not reading yet (just created, or queued behind a running read): its read
                // begins after this call, so it is fresh enough to share.
                flight = current;
            }
            else
            {
                // Replace a completed value; queue behind a read that is still running.
                flight = new Flight<T>(factory, current is { IsCompleted: false } ? current : null);
                _cache.Set(key, flight, ttl);
            }
        }

        return await AwaitAsync(key, flight);
    }

    public void Remove(string key)
    {
        ArgumentNullException.ThrowIfNull(key);
        lock (_creationLock) _cache.Remove(key);
    }

    private async Task<T> AwaitAsync<T>(string key, Flight<T> flight)
    {
        try
        {
            return await flight.Task;
        }
        catch
        {
            // Never cache failures — the next caller must retry the store. Only this flight is
            // evicted: a follow-up queued behind it is a separate read.
            lock (_creationLock)
            {
                if (_cache.TryGetValue(key, out Flight<T>? cached) && ReferenceEquals(cached, flight))
                    _cache.Remove(key);
            }
            throw;
        }
    }

    /// <summary>One shared factory invocation, optionally queued behind the read it replaces.</summary>
    private sealed class Flight<T>
    {
        private readonly Func<Task<T>> _factory;
        private readonly Lazy<Task<T>> _task;
        private Flight<T>? _after;
        private volatile bool _readStarted;

        public Flight(Func<Task<T>> factory, Flight<T>? after)
        {
            _factory = factory;
            _after = after;
            _task = new Lazy<Task<T>>(RunAsync);
        }

        /// <summary>The shared invocation; the first access starts it. Never read it under the creation lock.</summary>
        public Task<T> Task => _task.Value;

        /// <summary>True once the factory has been called; a refresh may no longer join.</summary>
        public bool ReadStarted => _readStarted;

        public bool IsCompleted => _task.IsValueCreated && _task.Value.IsCompleted;

        private async Task<T> RunAsync()
        {
            if (_after is { } running)
            {
                // Its outcome belongs to its own callers; this read runs either way.
                await ((Task)running.Task).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
                _after = null;
            }

            _readStarted = true;
            return await _factory().ConfigureAwait(false);
        }
    }
}

/// <summary>
/// The cached per-endpoint status count shared by the endpoint status API and the
/// Monitor acknowledgement policy, so both read the same entry.
/// </summary>
public static class EndpointStateCountCache
{
    /// <summary>
    /// The Monitor wall polls every few seconds per client; a 5 s TTL collapses that
    /// fan-in to at most one store round-trip per endpoint per window without
    /// visibly staling the UI.
    /// </summary>
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Returns the endpoint's status count, cached for <see cref="Ttl"/>. The value's
    /// <see cref="EndpointStateCount.EventTime"/> is when the store was actually queried.
    /// </summary>
    public static Task<EndpointStateCount> GetEndpointStateCountAsync(
        this IStoreResultCache cache, IMessageTrackingStore store, string endpointId)
        => cache.GetOrCreateAsync(
            $"endpoint-state-count:{endpointId}",
            Ttl,
            () => store.DownloadEndpointStateCount(endpointId));
}
