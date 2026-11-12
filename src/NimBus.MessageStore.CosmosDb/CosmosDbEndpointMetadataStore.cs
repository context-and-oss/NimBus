using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore;

internal sealed partial class CosmosDbEndpointMetadataStore : IEndpointMetadataStore
{
    private readonly Func<Task<ICosmosContainerAdapter>> _getMetadataContainer;
    private readonly Func<Task<ICosmosContainerAdapter>> _getSettingsContainer;
    private readonly ILogger _logger;

    public CosmosDbEndpointMetadataStore(
        Func<Task<ICosmosContainerAdapter>> getMetadataContainer,
        Func<Task<ICosmosContainerAdapter>> getSettingsContainer,
        ILogger? logger)
    {
        _getMetadataContainer = getMetadataContainer;
        _getSettingsContainer = getSettingsContainer;
        _logger = logger ?? NullLogger.Instance;
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

    public async Task<List<EndpointMetadata>?> GetMetadatas(IEnumerable<string> endpointIds)
    {
        var container = await _getMetadataContainer();
        try
        {
            var rel = await container.ReadManyItemsAsync<EndpointMetadata>(endpointIds.Select(x => (x, new PartitionKey(x))).ToList());
            return rel.Count != 0 ? rel.Resource.ToList() : null;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            LogCosmosMetadatasSomeEndpointsNotFound(_logger);
            return null;
        }
        catch (Exception e)
        {
            LogCosmosMetadatasError(_logger, e);
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
            LogCosmosUpsertResponse(_logger, endpointMetadata.EndpointId, response.StatusCode);
            return true;
        }
        catch (CosmosException e)
        {
            LogCosmosUpsertError(_logger, e, endpointMetadata.EndpointId, e.StatusCode);
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
            LogCosmosUpsertResponseHeartbeatUpsertId(_logger, endpointId, response.StatusCode);
            return true;
        }
        catch (CosmosException e)
        {
            LogCosmosUpsertErrorHeartbeatUpsertId(_logger, e, endpointId, e.StatusCode);
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
                LogCosmosUpsertErrorHeartbeatSweepId(_logger, e, metadata.EndpointId, e.StatusCode);
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
            LogCosmosUpsertResponseHeartbeatSettingsUpsertId(_logger, settings.Id, response.StatusCode);
            return true;
        }
        catch (CosmosException e)
        {
            LogCosmosUpsertErrorHeartbeatSettingsUpsertId(_logger, e, settings.Id, e.StatusCode);
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
            LogCosmosUpsertResponseAuditSettingsUpsertId(_logger, settings.Id, response.StatusCode);
            return true;
        }
        catch (CosmosException e)
        {
            LogCosmosUpsertErrorAuditSettingsUpsertId(_logger, e, settings.Id, e.StatusCode);
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

    public async Task<DashboardTokenSettings> GetDashboardTokenSettings()
    {
        var container = await _getSettingsContainer();
        try
        {
            var response = await container.ReadItemAsync<DashboardTokenSettings>(
                DashboardTokenSettings.SingletonId,
                new PartitionKey(DashboardTokenSettings.SingletonId));
            return response.Resource ?? new DashboardTokenSettings();
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return new DashboardTokenSettings();
        }
    }

    public async Task<bool> TrySetDashboardTokenSettings(DashboardTokenSettings settings, string? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.Revision))
            throw new ArgumentException("The tokens must carry their new revision.", nameof(settings));
        settings.Id = DashboardTokenSettings.SingletonId;
        var partitionKey = new PartitionKey(DashboardTokenSettings.SingletonId);

        // Same compare-and-set shape as TrySetAgentApiKeySettings.
        var container = await _getSettingsContainer();
        try
        {
            if (expectedRevision is null)
            {
                await container.CreateItemAsync(settings, partitionKey);
                return true;
            }

            var current = await container.ReadItemAsync<DashboardTokenSettings>(DashboardTokenSettings.SingletonId, partitionKey);
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

    public async Task<List<DashboardTokenUsage>> GetDashboardTokenUsage()
    {
        var container = await _getSettingsContainer();
        var query = new QueryDefinition("SELECT * FROM c WHERE STARTSWITH(c.id, @prefix)")
            .WithParameter("@prefix", DashboardTokenUsageDocument.IdPrefix);
        var usage = new List<DashboardTokenUsage>();
        using var iterator = container.GetItemQueryIterator<DashboardTokenUsageDocument>(query);
        while (iterator.HasMoreResults)
        {
            foreach (var document in await iterator.ReadNextAsync())
            {
                usage.Add(new DashboardTokenUsage { TokenId = document.TokenId, LastUsedAtUtc = document.LastUsedAtUtc });
            }
        }
        return usage;
    }

    public async Task RecordDashboardTokenUse(string tokenId, DateTime usedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(tokenId);
        // One small document per token in the settings container (partitioned on /id).
        var document = new DashboardTokenUsageDocument
        {
            Id = DashboardTokenUsageDocument.IdPrefix + tokenId,
            TokenId = tokenId,
            LastUsedAtUtc = usedAtUtc,
        };
        var container = await _getSettingsContainer();
        await container.UpsertItemAsync(document, new PartitionKey(document.Id));
    }

    private sealed class DashboardTokenUsageDocument
    {
        public const string IdPrefix = "DashboardTokenUsage-";

        [Newtonsoft.Json.JsonProperty("id")]
        public string Id { get; set; } = string.Empty;

        public string TokenId { get; set; } = string.Empty;

        public DateTime LastUsedAtUtc { get; set; }
    }

    public async Task<IntegratorApiKeySettings> GetIntegratorApiKeySettings()
    {
        var container = await _getSettingsContainer();
        try
        {
            var response = await container.ReadItemAsync<IntegratorApiKeySettings>(
                IntegratorApiKeySettings.SingletonId,
                new PartitionKey(IntegratorApiKeySettings.SingletonId));
            return response.Resource ?? new IntegratorApiKeySettings();
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return new IntegratorApiKeySettings();
        }
    }

    public async Task<bool> TrySetIntegratorApiKeySettings(IntegratorApiKeySettings settings, string? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.Revision))
            throw new ArgumentException("The keys must carry their new revision.", nameof(settings));
        settings.Id = IntegratorApiKeySettings.SingletonId;
        var partitionKey = new PartitionKey(IntegratorApiKeySettings.SingletonId);

        // Same compare-and-set shape as TrySetAgentApiKeySettings.
        var container = await _getSettingsContainer();
        try
        {
            if (expectedRevision is null)
            {
                await container.CreateItemAsync(settings, partitionKey);
                return true;
            }

            var current = await container.ReadItemAsync<IntegratorApiKeySettings>(IntegratorApiKeySettings.SingletonId, partitionKey);
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

    public async Task<List<IntegratorApiKeyUsage>> GetIntegratorApiKeyUsage()
    {
        var container = await _getSettingsContainer();
        var query = new QueryDefinition("SELECT * FROM c WHERE STARTSWITH(c.id, @prefix)")
            .WithParameter("@prefix", IntegratorApiKeyUsageDocument.IdPrefix);
        var usage = new List<IntegratorApiKeyUsage>();
        using var iterator = container.GetItemQueryIterator<IntegratorApiKeyUsageDocument>(query);
        while (iterator.HasMoreResults)
        {
            foreach (var document in await iterator.ReadNextAsync())
            {
                usage.Add(new IntegratorApiKeyUsage { KeyId = document.KeyId, LastUsedAtUtc = document.LastUsedAtUtc });
            }
        }
        return usage;
    }

    public async Task RecordIntegratorApiKeyUse(string keyId, DateTime usedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        // One small document per key in the settings container (partitioned on /id).
        var document = new IntegratorApiKeyUsageDocument
        {
            Id = IntegratorApiKeyUsageDocument.IdPrefix + keyId,
            KeyId = keyId,
            LastUsedAtUtc = usedAtUtc,
        };
        var container = await _getSettingsContainer();
        await container.UpsertItemAsync(document, new PartitionKey(document.Id));
    }

    private sealed class IntegratorApiKeyUsageDocument
    {
        public const string IdPrefix = "IntegratorApiKeyUsage-";

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



    [LoggerMessage(Level = LogLevel.Information, Message = "COSMOS METADATAS: Some endpoints not found in metadata container")]
    private static partial void LogCosmosMetadatasSomeEndpointsNotFound(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "COSMOS METADATAS-ERROR: Failed to get metadatas for endpoints")]
    private static partial void LogCosmosMetadatasError(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Trace, Message = "COSMOS UPSERT-RESPONSE: Metadata upsert. Id: {EndpointId}, HttpStatusCode: {StatusCode}")]
    private static partial void LogCosmosUpsertResponse(ILogger logger, string endpointId, HttpStatusCode statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "COSMOS UPSERT-ERROR: Metadata upsert. Id: {EndpointId}, HttpStatusCode: {StatusCode}")]
    private static partial void LogCosmosUpsertError(ILogger logger, Exception exception, string endpointId, HttpStatusCode statusCode);

    [LoggerMessage(Level = LogLevel.Trace, Message = "COSMOS UPSERT-RESPONSE: Heartbeat upsert. Id: {EndpointId}, HttpStatusCode: {StatusCode}")]
    private static partial void LogCosmosUpsertResponseHeartbeatUpsertId(ILogger logger, string endpointId, HttpStatusCode statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "COSMOS UPSERT-ERROR: Heartbeat upsert. Id: {EndpointId}, HttpStatusCode: {StatusCode}")]
    private static partial void LogCosmosUpsertErrorHeartbeatUpsertId(ILogger logger, Exception exception, string endpointId, HttpStatusCode statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "COSMOS UPSERT-ERROR: Heartbeat sweep. Id: {EndpointId}, HttpStatusCode: {StatusCode}")]
    private static partial void LogCosmosUpsertErrorHeartbeatSweepId(ILogger logger, Exception exception, string endpointId, HttpStatusCode statusCode);

    [LoggerMessage(Level = LogLevel.Trace, Message = "COSMOS UPSERT-RESPONSE: HeartbeatSettings upsert. Id: {Id}, HttpStatusCode: {StatusCode}")]
    private static partial void LogCosmosUpsertResponseHeartbeatSettingsUpsertId(ILogger logger, string id, HttpStatusCode statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "COSMOS UPSERT-ERROR: HeartbeatSettings upsert. Id: {Id}, HttpStatusCode: {StatusCode}")]
    private static partial void LogCosmosUpsertErrorHeartbeatSettingsUpsertId(ILogger logger, Exception exception, string id, HttpStatusCode statusCode);

    [LoggerMessage(Level = LogLevel.Trace, Message = "COSMOS UPSERT-RESPONSE: AuditSettings upsert. Id: {Id}, HttpStatusCode: {StatusCode}")]
    private static partial void LogCosmosUpsertResponseAuditSettingsUpsertId(ILogger logger, string id, HttpStatusCode statusCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "COSMOS UPSERT-ERROR: AuditSettings upsert. Id: {Id}, HttpStatusCode: {StatusCode}")]
    private static partial void LogCosmosUpsertErrorAuditSettingsUpsertId(ILogger logger, Exception exception, string id, HttpStatusCode statusCode);
}
