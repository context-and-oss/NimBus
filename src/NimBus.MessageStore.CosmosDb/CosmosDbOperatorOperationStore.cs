using Microsoft.Azure.Cosmos;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.MessageStore;

/// <summary>
/// Cosmos DB implementation of <see cref="IOperatorOperationStore"/>: one document per operation
/// (id = operation id, partition key <c>/id</c>) in the <c>operatoroperations</c> container. The
/// document's <c>_etag</c> is the version; item TTL expires operations without a purge.
/// </summary>
/// <remarks>
/// Cosmos compares DateTime strings lexically, which is wrong across differing fractional digits.
/// Date filters therefore query with bounds widened to whole seconds (<see cref="CosmosDateBounds"/>)
/// and the exact predicate is applied to the documents read.
/// </remarks>
internal sealed class CosmosDbOperatorOperationStore : IOperatorOperationStore
{
    private readonly Func<Task<ICosmosContainerAdapter>> _getContainer;

    public CosmosDbOperatorOperationStore(Func<Task<ICosmosContainerAdapter>> getContainer)
    {
        _getContainer = getContainer;
    }

    public async Task<OperatorOperationCreateResult> TryCreateOperatorOperation(OperatorOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.OperationId);

        var container = await _getContainer();
        try
        {
            var response = await container.CreateItemAsync(operation, new PartitionKey(operation.OperationId), CancellationToken.None);
            operation.Version = response.ETag;
            var stored = operation.Clone();
            return new OperatorOperationCreateResult(true, stored);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.Conflict)
        {
            var existing = await GetOperatorOperation(operation.OperationId)
                ?? throw new InvalidOperationException($"Operation '{operation.OperationId}' collided on create but cannot be read.");
            return new OperatorOperationCreateResult(false, existing);
        }
    }

    public async Task<OperatorOperation?> GetOperatorOperation(string operationId)
    {
        var container = await _getContainer();
        try
        {
            var response = await container.ReadItemAsync<OperatorOperation>(operationId, new PartitionKey(operationId));
            var operation = Normalize(response.Resource);
            operation.Version = response.ETag;
            return operation;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<bool> TryUpdateOperatorOperation(OperatorOperation operation, string expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (string.IsNullOrEmpty(expectedVersion))
            return false;

        var container = await _getContainer();
        try
        {
            var response = await container.ReplaceItemAsync(
                operation,
                operation.OperationId,
                new PartitionKey(operation.OperationId),
                new ItemRequestOptions { IfMatchEtag = expectedVersion });
            operation.Version = response.ETag;
            return true;
        }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<IReadOnlyList<OperatorOperation>> ListDueOperatorOperations(
        IReadOnlyCollection<OperatorOperationStatus> statuses, DateTime updatedBeforeUtc, int limit)
    {
        if (statuses.Count == 0 || limit <= 0)
            return [];

        var query = new QueryDefinition(
                "SELECT * FROM c WHERE ARRAY_CONTAINS(@statuses, c.Status) AND c.UpdatedAtUtc < @before")
            .WithParameter("@statuses", statuses.Select(s => s.ToString()).ToArray())
            .WithParameter("@before", CosmosDateBounds.WidenTo(updatedBeforeUtc));
        var read = await ReadAllAsync(query);
        return read
            .Where(o => o.UpdatedAtUtc.Ticks < updatedBeforeUtc.Ticks)
            .OrderBy(o => o.UpdatedAtUtc)
            .Take(limit)
            .ToList();
    }

    public async Task<int> CountOperatorOperations(OperatorOperationQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        // String filters narrow the read; the shared predicate decides exactly.
        var text = "SELECT * FROM c WHERE 1 = 1";
        var parameters = new List<(string Name, object Value)>();
        void Filter(string? value, string property)
        {
            if (value is null) return;
            text += $" AND c.{property} = @{property}";
            parameters.Add(("@" + property, value));
        }

        Filter(query.Action?.ToString(), nameof(OperatorOperation.Action));
        Filter(query.TenantId, nameof(OperatorOperation.TenantId));
        Filter(query.ClientId, nameof(OperatorOperation.ClientId));
        Filter(query.EndpointId, nameof(OperatorOperation.EndpointId));
        Filter(query.EventId, nameof(OperatorOperation.EventId));
        if (query.CreatedFromUtc is { } from)
        {
            text += " AND c.CreatedAtUtc >= @from";
            parameters.Add(("@from", CosmosDateBounds.WidenFrom(from)));
        }

        var definition = new QueryDefinition(text);
        foreach (var (name, value) in parameters)
            definition = definition.WithParameter(name, value);

        var read = await ReadAllAsync(definition);
        return read.Count(query.Matches);
    }

    public async Task<int> PurgeExpiredOperatorOperations(DateTime nowUtc, int limit)
    {
        if (limit <= 0)
            return 0;

        var query = new QueryDefinition("SELECT * FROM c WHERE c.ExpiresAtUtc < @now")
            .WithParameter("@now", CosmosDateBounds.WidenTo(nowUtc));
        var expired = (await ReadAllAsync(query))
            .Where(o => o.ExpiresAtUtc.Ticks < nowUtc.Ticks)
            .Take(limit)
            .ToList();

        var container = await _getContainer();
        var deleted = 0;
        foreach (var operation in expired)
        {
            try
            {
                await container.DeleteItemAsync<OperatorOperation>(operation.OperationId, new PartitionKey(operation.OperationId), CancellationToken.None);
                deleted++;
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                // Expired by TTL or purged by another instance meanwhile.
            }
        }

        return deleted;
    }

    private async Task<List<OperatorOperation>> ReadAllAsync(QueryDefinition query)
    {
        var container = await _getContainer();
        var results = new List<OperatorOperation>();
        using var iterator = container.GetItemQueryIterator<OperatorOperation>(query);
        while (iterator.HasMoreResults)
        {
            foreach (var operation in await iterator.ReadNextAsync())
                results.Add(Normalize(operation));
        }

        return results;
    }

    private static OperatorOperation Normalize(OperatorOperation operation)
    {
        operation.CreatedAtUtc = AsUtc(operation.CreatedAtUtc);
        operation.UpdatedAtUtc = AsUtc(operation.UpdatedAtUtc);
        operation.ExpiresAtUtc = AsUtc(operation.ExpiresAtUtc);
        operation.ObservedAtUtc = AsUtc(operation.ObservedAtUtc);
        operation.LeaseUntilUtc = AsUtc(operation.LeaseUntilUtc);
        operation.DispatchedAtUtc = AsUtc(operation.DispatchedAtUtc);
        return operation;
    }

    private static DateTime? AsUtc(DateTime? value) => value is null ? null : AsUtc(value.Value);

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };
}
