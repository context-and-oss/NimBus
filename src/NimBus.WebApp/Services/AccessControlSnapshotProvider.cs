using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services;

/// <summary>
/// Point-in-time view of every access-control list: the site document plus all
/// endpoint-scoped documents, keyed by endpoint id.
/// </summary>
public sealed class AccessControlSnapshot
{
    public static readonly AccessControlSnapshot Empty = new(null, new Dictionary<string, AccessControlList>(StringComparer.OrdinalIgnoreCase));

    public AccessControlSnapshot(AccessControlList? site, IReadOnlyDictionary<string, AccessControlList> endpoints)
    {
        Site = site;
        Endpoints = endpoints;
    }

    public AccessControlList? Site { get; }

    public IReadOnlyDictionary<string, AccessControlList> Endpoints { get; }
}

/// <summary>
/// Caches the full ACL snapshot so per-request role resolution never hits the
/// store on the hot path. Mutating APIs call <see cref="Invalidate"/> so grants
/// and revocations take effect immediately on this instance; other instances
/// converge within the TTL (DIS accepts the same multi-instance staleness).
/// </summary>
public interface IAccessControlSnapshotProvider
{
    Task<AccessControlSnapshot> GetSnapshotAsync();

    /// <summary>Drops the cached snapshot so the next read reloads from the store.</summary>
    void Invalidate();
}

/// <summary>
/// Singleton snapshot cache (the consuming authorization service is scoped, so a
/// shorter lifetime would never hit). Store faults reuse last-known-good only for
/// an ordinary TTL refresh; an explicitly invalidated generation falls back to an
/// empty snapshot so possible revocations fail closed. Claim-based compat grants
/// (EIP_Management) are unaffected, so configured admins are never locked out by
/// an outage.
/// </summary>
public sealed class AccessControlSnapshotProvider : IAccessControlSnapshotProvider, IDisposable
{
    // 45s mirrors DIS's access-control cache; 5s failure TTL bounds hammering a
    // faulting store while keeping recovery quick.
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(5);

    private readonly IAccessControlStore _store;
    private readonly ILogger<AccessControlSnapshotProvider> _logger;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private CacheEntry? _current;
    private long _generation;

    public AccessControlSnapshotProvider(IAccessControlStore store, ILogger<AccessControlSnapshotProvider> logger)
    {
        _store = store;
        _logger = logger;
    }

    public async Task<AccessControlSnapshot> GetSnapshotAsync()
    {
        var current = Volatile.Read(ref _current);
        var generation = Volatile.Read(ref _generation);
        if (IsFresh(current, generation))
            return current.Snapshot;

        // Single-flight: one loader refreshes, concurrent requests reuse its result.
        await _refreshLock.WaitAsync();
        try
        {
            while (true)
            {
                current = Volatile.Read(ref _current);
                generation = Volatile.Read(ref _generation);
                if (IsFresh(current, generation))
                    return current.Snapshot;

                try
                {
                    var site = await _store.GetSiteAccessControl();
                    var endpointDocs = await _store.GetEndpointAccessControls();
                    var endpoints = new Dictionary<string, AccessControlList>(StringComparer.OrdinalIgnoreCase);
                    foreach (var doc in endpointDocs)
                    {
                        if (!string.IsNullOrEmpty(doc.EndpointId))
                            endpoints[doc.EndpointId] = doc;
                    }

                    var snapshot = new AccessControlSnapshot(site, endpoints);
                    if (generation != Volatile.Read(ref _generation))
                        continue;

                    Volatile.Write(
                        ref _current,
                        new CacheEntry(snapshot, DateTime.UtcNow + SuccessTtl, generation));
                    if (generation == Volatile.Read(ref _generation))
                        return snapshot;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex,
                        "Access-control snapshot refresh failed; store-granted roles are unavailable until the store recovers");
                    // An explicit invalidation may represent a revoke, so only
                    // reuse last-known-good within the same generation.
                    var stale = Volatile.Read(ref _current);
                    var snapshot = stale != null && stale.Generation == generation
                        ? stale.Snapshot
                        : AccessControlSnapshot.Empty;
                    if (generation != Volatile.Read(ref _generation))
                        continue;

                    Volatile.Write(
                        ref _current,
                        new CacheEntry(snapshot, DateTime.UtcNow + FailureTtl, generation));
                    if (generation == Volatile.Read(ref _generation))
                        return snapshot;
                }
            }
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    public void Invalidate() => Interlocked.Increment(ref _generation);

    public void Dispose() => _refreshLock.Dispose();

    private static bool IsFresh([NotNullWhen(true)] CacheEntry? entry, long generation)
        => entry != null && entry.Generation == generation && DateTime.UtcNow < entry.ExpiresAtUtc;

    private sealed record CacheEntry(AccessControlSnapshot Snapshot, DateTime ExpiresAtUtc, long Generation);
}
