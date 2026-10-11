using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore;

internal sealed class CosmosDbEndpointMetadataStore : IEndpointMetadataStore
{
    private readonly Func<Task<ICosmosContainerAdapter>> _getMetadataContainer;
    private readonly Func<Task<ICosmosContainerAdapter>> _getSettingsContainer;
    private readonly ILogger _logger;

    public CosmosDbEndpointMetadataStore(
        Func<Task<ICosmosContainerAdapter>> getMetadataContainer,
        Func<Task<ICosmosContainerAdapter>> getSettingsContainer,
        ILogger logger)
    {
        _getMetadataContainer = getMetadataContainer;
        _getSettingsContainer = getSettingsContainer;
        _logger = logger;
    }
    public async Task<EndpointMetadata?> GetEndpointMetadata(string endpointId)
    {
        var container = await _getMetadataContainer();
        try
        {
            var rel = await container.ReadItemAsync<EndpointMetadata>(endpointId, new PartitionKey(endpointId));
            return rel.Resource;
        }
        catch (CosmosException e)
        {
            if (e.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
            throw;
        }
    }

    public async Task<List<EndpointMetadata>>? GetMetadatas(IEnumerable<string> endpointIds)
    {
        var container = await _getMetadataContainer();
        try
        {
            var rel = await container.ReadManyItemsAsync<EndpointMetadata>(endpointIds.Select(x => (x, new PartitionKey(x))).ToList());
            return rel.Any() ? rel.Resource.ToList() : null;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            _logger?.LogInformation("COSMOS METADATAS: Some endpoints not found in metadata container");
            return null;
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "COSMOS METADATAS-ERROR: Failed to get metadatas for endpoints");
            throw;
        }
    }

    public async Task<List<EndpointMetadata>> GetMetadatas()
    {
        var sqlQuery = "SELECT * FROM c";
        var metadatas = await GetMetadatasByFilter(sqlQuery);

        return metadatas;
    }

    private async Task<List<EndpointMetadata>> GetMetadatasByFilter(string sqlQuery)
    {
        var container = await _getMetadataContainer();
        var result = container.GetItemQueryIterator<EndpointMetadata>(sqlQuery);
        var metadatas = new List<EndpointMetadata>();

        while (result.HasMoreResults)
        {
            var subDbo = await result.ReadNextAsync();
            foreach (var queryResult in subDbo)
            {
                metadatas.Add(queryResult);
            }
        }
        return metadatas;
    }

    public async Task<bool> SetEndpointMetadata(EndpointMetadata endpointMetadata)
    {
        var container = await _getMetadataContainer();

        try
        {
            var response =
                await container.UpsertItemAsync(endpointMetadata, new PartitionKey(endpointMetadata.EndpointId));
            _logger?.LogTrace(
                "COSMOS UPSERT-RESPONSE: Metadata upsert. Id: {EndpointId}, HttpStatusCode: {StatusCode}", endpointMetadata.EndpointId, response.StatusCode);
            return true;
        }
        catch (CosmosException e)
        {
            _logger?.LogError(e,
                "COSMOS UPSERT-ERROR: Metadata upsert. Id: {EndpointId}, HttpStatusCode: {StatusCode}", endpointMetadata.EndpointId, e.StatusCode);
            throw;
        }
    }

    // ── Endpoint heartbeat — rows embedded in the endpoint's metadata document ──

    public Task<List<EndpointMetadata>> GetMetadatasWithEnabledHeartbeat() =>
        GetMetadatasByFilter("SELECT * FROM c WHERE c.IsHeartbeatEnabled = true");

    public async Task EnableHeartbeatOnEndpoint(string endpointId, bool enable)
    {
        var container = await _getMetadataContainer();
        var patchOperations = new List<PatchOperation>
        {
            PatchOperation.Set("/IsHeartbeatEnabled", enable),
        };

        try
        {
            await container.PatchItemAsync<EndpointMetadata>(endpointId, new PartitionKey(endpointId), patchOperations);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            await SetEndpointMetadata(new EndpointMetadata
            {
                EndpointId = endpointId,
                IsHeartbeatEnabled = enable,
            });
        }
    }

    public async Task<bool> SetHeartbeat(Heartbeat heartbeat, string endpointId)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);

        var container = await _getMetadataContainer();
        var metadata = await GetEndpointMetadata(endpointId) ?? new EndpointMetadata { EndpointId = endpointId };
        metadata.Heartbeats ??= new List<Heartbeat>();

        // Keyed by MessageId so the Pending probe and the answer that settles it
        // share one row instead of accumulating a duplicate.
        var existing = metadata.Heartbeats.Find(h => h.MessageId == heartbeat.MessageId);
        if (existing != null)
        {
            existing.StartTime = heartbeat.StartTime;
            existing.ReceivedTime = heartbeat.ReceivedTime;
            existing.EndTime = heartbeat.EndTime;
            existing.SdkVersion = heartbeat.SdkVersion;
            existing.EndpointHeartbeatStatus = heartbeat.EndpointHeartbeatStatus;
            if (heartbeat.IntervalSeconds > 0)
            {
                existing.IntervalSeconds = heartbeat.IntervalSeconds;
            }
        }
        else
        {
            metadata.Heartbeats.Add(heartbeat);
        }

        metadata.Heartbeats = HeartbeatRollup.Prune(metadata.Heartbeats);
        HeartbeatRollup.Apply(metadata);

        try
        {
            var response = await container.UpsertItemAsync(metadata, new PartitionKey(endpointId));
            _logger?.LogTrace(
                "COSMOS UPSERT-RESPONSE: Heartbeat upsert. Id: {EndpointId}, HttpStatusCode: {StatusCode}", endpointId, response.StatusCode);
            return true;
        }
        catch (CosmosException e)
        {
            _logger?.LogError(e,
                "COSMOS UPSERT-ERROR: Heartbeat upsert. Id: {EndpointId}, HttpStatusCode: {StatusCode}", endpointId, e.StatusCode);
            throw;
        }
    }

    public async Task<List<string>> SweepTimedOutHeartbeats(DateTime cutoffUtc)
    {
        var container = await _getMetadataContainer();
        var swept = new List<string>();

        foreach (var metadata in await GetMetadatas())
        {
            var timedOut = metadata.Heartbeats?
                .Where(h => h.EndpointHeartbeatStatus == HeartbeatStatus.Pending && h.StartTime <= cutoffUtc)
                .ToList();
            if (timedOut is not { Count: > 0 }) continue;

            foreach (var heartbeat in timedOut)
            {
                heartbeat.EndpointHeartbeatStatus = HeartbeatStatus.Off;
            }

            HeartbeatRollup.Apply(metadata);

            try
            {
                await container.UpsertItemAsync(metadata, new PartitionKey(metadata.EndpointId));
                swept.Add(metadata.EndpointId);
            }
            catch (CosmosException e)
            {
                _logger?.LogError(e,
                    "COSMOS UPSERT-ERROR: Heartbeat sweep. Id: {EndpointId}, HttpStatusCode: {StatusCode}", metadata.EndpointId, e.StatusCode);
                throw;
            }
        }

        return swept;
    }

    public async Task<HeartbeatSettings> GetHeartbeatSettings()
    {
        var container = await _getSettingsContainer();
        try
        {
            var response = await container.ReadItemAsync<HeartbeatSettings>(
                HeartbeatSettings.SingletonId,
                new PartitionKey(HeartbeatSettings.SingletonId));
            return response.Resource ?? new HeartbeatSettings();
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return new HeartbeatSettings();
        }
    }

    public async Task<bool> SetHeartbeatSettings(HeartbeatSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.Id)) settings.Id = HeartbeatSettings.SingletonId;

        // The claim owns LastSentAtUtc: an operator edit that carries no value must
        // not reset the send schedule.
        if (settings.LastSentAtUtc == null || settings.LastHeartbeatFoldAtUtc == null)
        {
            var stored = await GetHeartbeatSettings();
            settings.LastSentAtUtc ??= stored.LastSentAtUtc;
            settings.LastHeartbeatFoldAtUtc ??= stored.LastHeartbeatFoldAtUtc;
        }

        var container = await _getSettingsContainer();
        try
        {
            var response = await container.UpsertItemAsync(settings, new PartitionKey(settings.Id));
            _logger?.LogTrace(
                "COSMOS UPSERT-RESPONSE: HeartbeatSettings upsert. Id: {Id}, HttpStatusCode: {StatusCode}", settings.Id, response.StatusCode);
            return true;
        }
        catch (CosmosException e)
        {
            _logger?.LogError(e,
                "COSMOS UPSERT-ERROR: HeartbeatSettings upsert. Id: {Id}, HttpStatusCode: {StatusCode}", settings.Id, e.StatusCode);
            throw;
        }
    }

    public async Task<AuditSettings> GetAuditSettings()
    {
        var container = await _getSettingsContainer();
        try
        {
            var response = await container.ReadItemAsync<AuditSettings>(
                AuditSettings.SingletonId,
                new PartitionKey(AuditSettings.SingletonId));
            return response.Resource ?? new AuditSettings();
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return new AuditSettings();
        }
    }

    public async Task<bool> SetAuditSettings(AuditSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.Id)) settings.Id = AuditSettings.SingletonId;
        settings.DisabledAuditTypes ??= new();

        var container = await _getSettingsContainer();
        try
        {
            var response = await container.UpsertItemAsync(settings, new PartitionKey(settings.Id));
            _logger?.LogTrace(
                "COSMOS UPSERT-RESPONSE: AuditSettings upsert. Id: {Id}, HttpStatusCode: {StatusCode}", settings.Id, response.StatusCode);
            return true;
        }
        catch (CosmosException e)
        {
            _logger?.LogError(e,
                "COSMOS UPSERT-ERROR: AuditSettings upsert. Id: {Id}, HttpStatusCode: {StatusCode}", settings.Id, e.StatusCode);
            throw;
        }
    }

    public async Task<McpAccessSettings> GetMcpAccessSettings()
    {
        var container = await _getSettingsContainer();
        try
        {
            var response = await container.ReadItemAsync<McpAccessSettings>(
                McpAccessSettings.SingletonId,
                new PartitionKey(McpAccessSettings.SingletonId));
            return response.Resource ?? new McpAccessSettings();
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return new McpAccessSettings();
        }
    }

    public async Task<bool> TrySetMcpAccessSettings(McpAccessSettings settings, string? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.Revision))
            throw new ArgumentException("The policy must carry its new revision.", nameof(settings));
        settings.Id = McpAccessSettings.SingletonId;
        var partitionKey = new PartitionKey(McpAccessSettings.SingletonId);

        var container = await _getSettingsContainer();
        try
        {
            if (expectedRevision is null)
            {
                // Create fails with 409 when a record exists.
                await container.CreateItemAsync(settings, partitionKey);
                return true;
            }

            var current = await container.ReadItemAsync<McpAccessSettings>(McpAccessSettings.SingletonId, partitionKey);
            if (!string.Equals(current.Resource?.Revision, expectedRevision, StringComparison.Ordinal))
                return false;

            // The ETag precondition closes the gap between the revision check and the write.
            await container.UpsertItemAsync(settings, partitionKey, new ItemRequestOptions { IfMatchEtag = current.ETag });
            return true;
        }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    public async Task<AgentApiKeySettings> GetAgentApiKeySettings()
    {
        var container = await _getSettingsContainer();
        try
        {
            var response = await container.ReadItemAsync<AgentApiKeySettings>(
                AgentApiKeySettings.SingletonId,
                new PartitionKey(AgentApiKeySettings.SingletonId));
            return response.Resource ?? new AgentApiKeySettings();
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return new AgentApiKeySettings();
        }
    }

    public async Task<bool> TrySetAgentApiKeySettings(AgentApiKeySettings settings, string? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.Revision))
            throw new ArgumentException("The keys must carry their new revision.", nameof(settings));
        settings.Id = AgentApiKeySettings.SingletonId;
        var partitionKey = new PartitionKey(AgentApiKeySettings.SingletonId);

        // Same compare-and-set shape as TrySetMcpAccessSettings.
        var container = await _getSettingsContainer();
        try
        {
            if (expectedRevision is null)
            {
                await container.CreateItemAsync(settings, partitionKey);
                return true;
            }

            var current = await container.ReadItemAsync<AgentApiKeySettings>(AgentApiKeySettings.SingletonId, partitionKey);
            if (!string.Equals(current.Resource?.Revision, expectedRevision, StringComparison.Ordinal))
                return false;

            await container.UpsertItemAsync(settings, partitionKey, new ItemRequestOptions { IfMatchEtag = current.ETag });
            return true;
        }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    public async Task<List<AgentApiKeyUsage>> GetAgentApiKeyUsage()
    {
        var container = await _getSettingsContainer();
        var query = new QueryDefinition("SELECT * FROM c WHERE STARTSWITH(c.id, @prefix)")
            .WithParameter("@prefix", AgentApiKeyUsageDocument.IdPrefix);
        var usage = new List<AgentApiKeyUsage>();
        using var iterator = container.GetItemQueryIterator<AgentApiKeyUsageDocument>(query);
        while (iterator.HasMoreResults)
        {
            foreach (var document in await iterator.ReadNextAsync())
            {
                usage.Add(new AgentApiKeyUsage { KeyId = document.KeyId, LastUsedAtUtc = document.LastUsedAtUtc });
            }
        }
        return usage;
    }

    public async Task RecordAgentApiKeyUse(string keyId, DateTime usedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        // One small document per key in the settings container (partitioned on /id).
        var document = new AgentApiKeyUsageDocument
        {
            Id = AgentApiKeyUsageDocument.IdPrefix + keyId,
            KeyId = keyId,
            LastUsedAtUtc = usedAtUtc,
        };
        var container = await _getSettingsContainer();
        await container.UpsertItemAsync(document, new PartitionKey(document.Id));
    }

    private sealed class AgentApiKeyUsageDocument
    {
        public const string IdPrefix = "AgentApiKeyUsage-";

        [Newtonsoft.Json.JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        public string KeyId { get; set; } = string.Empty;

        public DateTime LastUsedAtUtc { get; set; }
    }

    public async Task<bool> TryClaimHeartbeatSend(DateTime dueBefore)
    {
        // The ETag precondition is what makes at most one scaled-out instance send
        // per interval: the loser gets 412 and skips this tick.
        var container = await _getSettingsContainer();
        try
        {
            var response = await container.ReadItemAsync<HeartbeatSettings>(
                HeartbeatSettings.SingletonId,
                new PartitionKey(HeartbeatSettings.SingletonId));
            var settings = response.Resource ?? new HeartbeatSettings();

            if (!settings.Enabled || (settings.LastSentAtUtc.HasValue && settings.LastSentAtUtc.Value > dueBefore))
            {
                return false;
            }

            settings.LastSentAtUtc = DateTime.UtcNow;
            await container.UpsertItemAsync(
                settings,
                new PartitionKey(settings.Id),
                new ItemRequestOptions { IfMatchEtag = response.ETag });
            return true;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            // Nothing configured yet: create the (disabled) defaults and skip this tick.
            await SetHeartbeatSettings(new HeartbeatSettings());
            return false;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    public async Task<List<HeartbeatOverviewItem>> GetHeartbeatOverview()
    {
        var metadatas = await GetMetadatas();
        return metadatas
            .OrderBy(m => m.EndpointId, StringComparer.Ordinal)
            .Select(HeartbeatRollup.BuildOverviewItem)
            .ToList();
    }


}
