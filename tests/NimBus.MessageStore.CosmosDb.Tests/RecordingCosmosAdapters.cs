#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>
/// Shared test double for the Cosmos adapter chain. Records every container creation
/// (and <em>which</em> overload created it), every upserted document and every patch, so
/// tests can assert on the wire shape the store produces without a live account.
/// One container instance per id, so a test can tell an endpoint container apart from a
/// shared one.
/// </summary>
internal sealed class RecordingCosmosClientAdapter : ICosmosClientAdapter, ICosmosDatabaseAdapter
{
    private readonly Dictionary<string, RecordingCosmosContainerAdapter> _containers = new(StringComparer.Ordinal);

    /// <summary>Every container creation in order: the id, and the properties when the
    /// <see cref="ContainerProperties"/> overload was used (null when the id/partition-key one was).</summary>
    public List<(string Id, ContainerProperties? Properties)> ContainerCreations { get; } = new();

    public ICosmosDatabaseAdapter GetDatabase(string id) => this;

    public ICosmosContainerAdapter GetContainer(string id) => Container(id);

    public RecordingCosmosContainerAdapter Container(string id)
    {
        if (!_containers.TryGetValue(id, out var container))
        {
            container = new RecordingCosmosContainerAdapter();
            _containers[id] = container;
        }

        return container;
    }

    /// <summary>The properties a container was created with, or null when it was created
    /// through the id/partition-key overload. Throws when the container was never created.</summary>
    public ContainerProperties? CreationPropertiesFor(string id) =>
        ContainerCreations.Single(c => string.Equals(c.Id, id, StringComparison.Ordinal)).Properties;

    public bool WasCreated(string id) =>
        ContainerCreations.Exists(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    public Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(string id, string partitionKeyPath)
    {
        ContainerCreations.Add((id, null));
        return Task.FromResult<ICosmosContainerAdapter>(Container(id));
    }

    public Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(
        ContainerProperties containerProperties,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(containerProperties);
        ContainerCreations.Add((containerProperties.Id, containerProperties));
        return Task.FromResult<ICosmosContainerAdapter>(Container(containerProperties.Id));
    }
}

/// <summary>
/// A database adapter that predates the <see cref="ContainerProperties"/> overload — it
/// implements only the original id/partition-key method, so the fail-closed default
/// interface implementation is what runs.
/// </summary>
internal sealed class LegacyCosmosClientAdapter : ICosmosClientAdapter, ICosmosDatabaseAdapter
{
    private readonly RecordingCosmosContainerAdapter _container = new();

    public List<string> ContainerCreations { get; } = new();

    public ICosmosDatabaseAdapter GetDatabase(string id) => this;

    public ICosmosContainerAdapter GetContainer(string id) => _container;

    public Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(string id, string partitionKeyPath)
    {
        ContainerCreations.Add(id);
        return Task.FromResult<ICosmosContainerAdapter>(_container);
    }
}

internal sealed class RecordingCosmosContainerAdapter : ICosmosContainerAdapter
{
    public List<string> ReplacedIds { get; } = new();

    public Task<ItemResponse<T>> ReplaceItemAsync<T>(T item, string id, PartitionKey partitionKey, ItemRequestOptions requestOptions)
    {
        ReplacedIds.Add(id);
        return UpsertItemAsync(item, partitionKey, requestOptions);
    }

    /// <summary>Every upserted document, in order.</summary>
    public List<object?> UpsertedItems { get; } = new();

    public List<ItemRequestOptions?> CapturedRequestOptions { get; } = new();

    /// <summary>Every patch, in order.</summary>
    public List<IReadOnlyList<PatchOperation>> CapturedPatches { get; } = new();

    public int QueryCount { get; private set; }

    public List<QueryDefinition> Queries { get; } = new();

    /// <summary>Every created document, in order. Unlike the unconditional upsert path, the
    /// guarded write (Spec 030) creates the first projection of an event.</summary>
    public List<object?> CreatedItems { get; } = new();

    /// <summary>Scripted <see cref="ReadItemAsync{T}(string, PartitionKey)"/> results, consumed in
    /// order: a document (with its ETag) to return, or an exception to throw. When the queue runs
    /// dry the read throws <see cref="NotSupportedException"/>, so an unscripted read is a test bug
    /// rather than a silent pass. Documents are held as JSON because the store's row type is
    /// private to it; the read materialises them into whatever type the caller asked for.</summary>
    public Queue<object> ScriptedReads { get; } = new();

    /// <summary>Scripted upsert failures, consumed in order; a null entry lets that upsert
    /// succeed and be recorded.</summary>
    public Queue<Exception?> ScriptedUpsertFailures { get; } = new();

    /// <summary>Scripted create failures, consumed in order; a null entry lets that create
    /// succeed and be recorded.</summary>
    public Queue<Exception?> ScriptedCreateFailures { get; } = new();

    public int ReadCount { get; private set; }

    /// <summary>Scripts one read of the audit row as the store's own document shape.</summary>
    public void EnqueueRead(string id, string status, UnresolvedEvent @event, string etag, bool deleted = false) =>
        ScriptedReads.Enqueue((JObject.FromObject(new
        {
            id,
            status,
            eventType = @event?.EventTypeId,
            sessionId = @event?.SessionId,
            @event,
            deleted,
        }), etag));

    /// <summary>Scripts one read that returns a row whose status the store cannot parse.</summary>
    public void EnqueueUnparseableRead(string id, UnresolvedEvent @event, string etag) =>
        EnqueueRead(id, "NotAStatus", @event, etag);

    public void EnqueueReadFailure(Exception exception) => ScriptedReads.Enqueue(exception);

    public static CosmosException NotFound() =>
        new("not found", System.Net.HttpStatusCode.NotFound, 0, activityId: string.Empty, requestCharge: 0);

    public static CosmosException Conflict() =>
        new("conflict", System.Net.HttpStatusCode.Conflict, 0, activityId: string.Empty, requestCharge: 0);

    public static CosmosException PreconditionFailed() =>
        new("etag mismatch", System.Net.HttpStatusCode.PreconditionFailed, 0, activityId: string.Empty, requestCharge: 0);

    /// <summary>The upserted document as it goes on the wire. <c>EventDbo</c> is private to
    /// <c>CosmosDbClient</c>, so serializing is the only way to read its <c>ttl</c>.</summary>
    public JObject UpsertedDocument(int index) =>
        JObject.Parse(JsonConvert.SerializeObject(UpsertedItems[index]));

    public JObject SingleUpsertedDocument() =>
        JObject.Parse(JsonConvert.SerializeObject(UpsertedItems.Single()));

    public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition)
    {
        Queries.Add(queryDefinition);
        QueryCount++;
        return new EmptyFeedIterator<T>();
    }

    public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
    {
        Queries.Add(queryDefinition);
        QueryCount++;
        return new EmptyFeedIterator<T>();
    }

    public FeedIterator<T> GetItemQueryIterator<T>(string queryText)
    {
        QueryCount++;
        return new EmptyFeedIterator<T>();
    }

    public FeedIterator<T> GetItemQueryIterator<T>(string queryText, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
    {
        QueryCount++;
        return new EmptyFeedIterator<T>();
    }

    public IOrderedQueryable<T> GetItemLinqQueryable<T>(bool allowSynchronousQueryExecution = false, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
        => throw new NotSupportedException();

    public Task<ItemResponse<T>> UpsertItemAsync<T>(T item, PartitionKey partitionKey = default, ItemRequestOptions? requestOptions = null)
    {
        if (ScriptedUpsertFailures.Count > 0 && ScriptedUpsertFailures.Dequeue() is { } failure)
        {
            CapturedRequestOptions.Add(requestOptions);
            return Task.FromException<ItemResponse<T>>(failure);
        }

        UpsertedItems.Add(item);
        CapturedRequestOptions.Add(requestOptions);
        // Like the Cosmos SDK, a successful upsert always returns a response: the stores log
        // its status code.
        return Task.FromResult<ItemResponse<T>>(new FakeItemResponse<T>(item, "\"etag\""));
    }

    public Task<ItemResponse<T>> PatchItemAsync<T>(string id, PartitionKey partitionKey, IReadOnlyList<PatchOperation> patchOperations)
    {
        CapturedPatches.Add(patchOperations);
        return Task.FromResult<ItemResponse<T>>(new FakeItemResponse<T>(default!, "\"etag\""));
    }

    public Task<ItemResponse<T>> CreateItemAsync<T>(T item, PartitionKey partitionKey = default)
    {
        if (ScriptedCreateFailures.Count > 0 && ScriptedCreateFailures.Dequeue() is { } failure)
        {
            return Task.FromException<ItemResponse<T>>(failure);
        }

        CreatedItems.Add(item);
        return Task.FromResult<ItemResponse<T>>(new FakeItemResponse<T>(item, "\"etag\""));
    }

    public Task<ItemResponse<T>> DeleteItemAsync<T>(string id, PartitionKey partitionKey)
        => throw new NotSupportedException();

    public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey)
    {
        ReadCount++;
        if (ScriptedReads.Count == 0)
        {
            throw new NotSupportedException($"No scripted read for '{id}'.");
        }

        var next = ScriptedReads.Dequeue();
        if (next is Exception failure)
        {
            return Task.FromException<ItemResponse<T>>(failure);
        }

        var (document, etag) = ((JObject Document, string ETag))next;
        return Task.FromResult<ItemResponse<T>>(new FakeItemResponse<T>(document.ToObject<T>()!, etag));
    }

    public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey, ItemRequestOptions requestOptions)
        => ReadItemAsync<T>(id, partitionKey);

    public Task<ContainerResponse> DeleteContainerAsync()
        => throw new NotSupportedException();

    public Task<FeedResponse<T>> ReadManyItemsAsync<T>(IReadOnlyList<(string id, PartitionKey partitionKey)> items)
        => throw new NotSupportedException();
}

/// <summary>
/// The minimum of <see cref="ItemResponse{T}"/> the guarded write reads: the document and its
/// ETag. The SDK keeps its own constructors internal, so a subclass is the only way to hand a
/// store a synthetic read result.
/// </summary>
internal sealed class FakeItemResponse<T> : ItemResponse<T>
{
    private readonly T _resource;
    private readonly string _etag;

    public FakeItemResponse(T resource, string etag)
    {
        _resource = resource;
        _etag = etag;
    }

    public override T Resource => _resource;

    public override string ETag => _etag;

    public override System.Net.HttpStatusCode StatusCode => System.Net.HttpStatusCode.OK;

    public override double RequestCharge => 1;

    public override Headers Headers => new();

    public override string ActivityId => string.Empty;

    public override CosmosDiagnostics Diagnostics => null!;
}

internal sealed class EmptyFeedIterator<T> : FeedIterator<T>
{
    public override bool HasMoreResults => false;

    public override Task<FeedResponse<T>> ReadNextAsync(CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Iterator is empty.");
}
