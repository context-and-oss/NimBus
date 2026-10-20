using System.Diagnostics.CodeAnalysis;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace NimBus.MessageStore.HealthChecks;

/// <summary>
/// Readiness check for the Cosmos DB account. The account round-trip is cached
/// (healthy and unhealthy alike) for 30 seconds so frequent <c>/ready</c> probes
/// don't each pay a <see cref="CosmosClient.ReadAccountAsync"/> call. Requires a
/// singleton registration (see <see cref="HealthCheckExtensions.AddCosmosDbHealthCheck"/>)
/// — a per-probe instance would defeat the cache.
/// </summary>
[SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "The SemaphoreSlim never allocates a wait handle (AvailableWaitHandle is unused), and the check is a process-lifetime singleton.")]
public class CosmosDbHealthCheck : IHealthCheck
{
    private static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(30);

    private readonly CosmosClient _client;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);
    private CachedResult? _cached;

    public CosmosDbHealthCheck(CosmosClient client)
        : this(client, TimeProvider.System)
    {
    }

    public CosmosDbHealthCheck(CosmosClient client, TimeProvider timeProvider)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var cached = _cached;
        if (cached is not null && !IsExpired(cached))
        {
            return cached.Result;
        }

        // Single-flight: concurrent probes wait for one refresh instead of each
        // issuing their own account read.
        await _refreshLock.WaitAsync(cancellationToken);
        try
        {
            cached = _cached;
            if (cached is not null && !IsExpired(cached))
            {
                return cached.Result;
            }

            HealthCheckResult result;
            try
            {
                var response = await _client.ReadAccountAsync();
                result = HealthCheckResult.Healthy($"Cosmos DB is accessible. Account: {response.Id}");
            }
            catch (Exception ex)
            {
                result = HealthCheckResult.Unhealthy("Cosmos DB is not accessible.", ex);
            }

            _cached = new CachedResult(result, _timeProvider.GetTimestamp());
            return result;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private bool IsExpired(CachedResult cached) =>
        _timeProvider.GetElapsedTime(cached.Timestamp) >= CacheDuration;

    private sealed record CachedResult(HealthCheckResult Result, long Timestamp);
}
