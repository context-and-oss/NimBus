using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using System.Net;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore;

internal sealed class CosmosDbServiceHealthStore : IServiceHealthStore
{
    private readonly Func<Task<ICosmosContainerAdapter>> _getServiceHealthContainer;
    private readonly ILogger? _logger;

    public CosmosDbServiceHealthStore(Func<Task<ICosmosContainerAdapter>> getServiceHealthContainer, ILogger? logger)
    {
        _getServiceHealthContainer = getServiceHealthContainer;
        _logger = logger;
    }
    // ── Service health (platform services, not endpoints) ──
    //
    // One document per service in its own container. Deliberately not in Metadata,
    // whose "SELECT * FROM c" reads would surface the row as a phantom endpoint.

    public async Task<List<ServiceHealth>> GetServiceHealth()
    {
        var container = await _getServiceHealthContainer();
        var results = new List<ServiceHealth>();
        var iterator = container.GetItemQueryIterator<ServiceHealth>("SELECT * FROM c ORDER BY c.id");
        while (iterator.HasMoreResults)
        {
            foreach (var item in await iterator.ReadNextAsync())
            {
                results.Add(item);
            }
        }

        return results;
    }

    public async Task<bool> TryClaimServiceProbe(string serviceId, DateTime dueBefore, string probeMessageId)
    {
        if (string.IsNullOrWhiteSpace(serviceId)) throw new ArgumentNullException(nameof(serviceId));

        var container = await _getServiceHealthContainer();
        try
        {
            var response = await container.ReadItemAsync<ServiceHealth>(serviceId, new PartitionKey(serviceId));
            var health = response.Resource;

            if (health.LastProbeSentUtc.HasValue && health.LastProbeSentUtc.Value > dueBefore)
            {
                return false;
            }

            health.LastProbeSentUtc = DateTime.UtcNow;
            health.LastProbeMessageId = probeMessageId;
            await container.UpsertItemAsync(
                health,
                new PartitionKey(serviceId),
                new ItemRequestOptions { IfMatchEtag = response.ETag });
            return true;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // First probe ever. A concurrent creator loses the race on the
            // pre-condition and simply skips this interval.
            try
            {
                await container.CreateItemAsync(
                    new ServiceHealth
                    {
                        ServiceId = serviceId,
                        LastProbeSentUtc = DateTime.UtcNow,
                        LastProbeMessageId = probeMessageId,
                    },
                    new PartitionKey(serviceId));
                return true;
            }
            catch (CosmosException conflict) when (conflict.StatusCode == HttpStatusCode.Conflict)
            {
                return false;
            }
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    public async Task<bool> SetServiceHealth(ServiceHealth serviceHealth)
    {
        ArgumentNullException.ThrowIfNull(serviceHealth);
        if (string.IsNullOrWhiteSpace(serviceHealth.ServiceId)) throw new ArgumentNullException(nameof(serviceHealth));

        var container = await _getServiceHealthContainer();
        var serviceId = serviceHealth.ServiceId;

        // The claim owns LastProbeSentUtc; preserve it so a response never resets
        // the send schedule, and clear the in-flight marker.
        DateTime? lastProbeSentUtc = null;
        try
        {
            var existing = await container.ReadItemAsync<ServiceHealth>(serviceId, new PartitionKey(serviceId));
            lastProbeSentUtc = existing.Resource?.LastProbeSentUtc;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
        }

        serviceHealth.LastProbeSentUtc = lastProbeSentUtc;
        serviceHealth.LastProbeMessageId = null;

        try
        {
            var response = await container.UpsertItemAsync(serviceHealth, new PartitionKey(serviceId));
            _logger?.LogTrace(
                "COSMOS UPSERT-RESPONSE: ServiceHealth upsert. Id: {ServiceId}, HttpStatusCode: {StatusCode}", serviceId, response.StatusCode);
            return true;
        }
        catch (CosmosException e)
        {
            _logger?.LogError(e,
                "COSMOS UPSERT-ERROR: ServiceHealth upsert. Id: {ServiceId}, HttpStatusCode: {StatusCode}", serviceId, e.StatusCode);
            throw;
        }
    }

    public async Task<List<string>> SweepTimedOutServiceProbes(DateTime cutoffUtc)
    {
        var container = await _getServiceHealthContainer();
        var swept = new List<string>();

        foreach (var health in await GetServiceHealth())
        {
            if (health.LastProbeMessageId == null ||
                !health.LastProbeSentUtc.HasValue ||
                health.LastProbeSentUtc.Value > cutoffUtc)
            {
                continue;
            }

            health.Status = HeartbeatStatus.Off;
            health.LastProbeMessageId = null;
            await container.UpsertItemAsync(health, new PartitionKey(health.ServiceId));
            swept.Add(health.ServiceId);
        }

        return swept;
    }


}
