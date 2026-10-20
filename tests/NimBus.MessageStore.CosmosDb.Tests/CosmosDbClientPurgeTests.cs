#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>
/// Fake-adapter tests for <see cref="CosmosDbClient.PurgeMessages(string, string)"/>:
/// the lookup must project only the document id (not whole EventDbo documents with
/// their EventJson payloads), and the per-document deletes — each its own partition
/// on a /id-partitioned container — must run concurrently rather than one-by-one.
/// </summary>
[TestClass]
public sealed class CosmosDbClientPurgeTests
{
    [TestMethod]
    public async Task PurgeMessages_projects_ids_and_deletes_concurrently()
    {
        var container = new RecordingContainerAdapter(
            Enumerable.Range(1, 8).Select(i => $"{{\"id\": \"event-{i}\"}}").ToList());
        var client = new CosmosDbClient(new SingleContainerClientAdapter(container));

        var purged = await client.PurgeMessages("endpoint-1", "session-1");

        Assert.IsTrue(purged);
        CollectionAssert.AreEquivalent(
            Enumerable.Range(1, 8).Select(i => $"event-{i}").ToList(),
            container.DeletedIds.ToList());
        StringAssert.Contains(container.LastQueryText, "SELECT c.id",
            "Purge only needs document ids — it must not fetch whole documents.");
        Assert.IsTrue(container.MaxObservedDeleteConcurrency >= 2,
            $"Expected deletes to overlap, but max observed concurrency was {container.MaxObservedDeleteConcurrency} (serial execution).");
    }

    private sealed class SingleContainerClientAdapter : ICosmosClientAdapter, ICosmosDatabaseAdapter
    {
        private readonly ICosmosContainerAdapter _container;

        public SingleContainerClientAdapter(ICosmosContainerAdapter container) => _container = container;

        public ICosmosDatabaseAdapter GetDatabase(string id) => this;

        public ICosmosContainerAdapter GetContainer(string id) => _container;

        public Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(string id, string partitionKeyPath)
            => Task.FromResult(_container);

        // Endpoint containers are created through the ContainerProperties overload, which
        // fails closed unless an adapter implements it.
        public Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(
            ContainerProperties containerProperties,
            CancellationToken cancellationToken = default)
            => Task.FromResult(_container);
    }

    private sealed class RecordingContainerAdapter : ICosmosContainerAdapter
    {
        private readonly IReadOnlyList<string> _documentJson;
        private int _inFlightDeletes;
        private int _maxObservedDeleteConcurrency;

        public RecordingContainerAdapter(IReadOnlyList<string> documentJson) => _documentJson = documentJson;

        public string? LastQueryText { get; private set; }

        public ConcurrentBag<string> DeletedIds { get; } = new();

        public int MaxObservedDeleteConcurrency => Volatile.Read(ref _maxObservedDeleteConcurrency);

        public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition)
        {
            LastQueryText = queryDefinition.QueryText;
            return new SingleBatchFeedIterator<T>(
                _documentJson.Select(json => JsonConvert.DeserializeObject<T>(json)!).ToList());
        }

        public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
            => GetItemQueryIterator<T>(queryDefinition);

        public FeedIterator<T> GetItemQueryIterator<T>(string queryText)
            => throw new NotSupportedException();

        public FeedIterator<T> GetItemQueryIterator<T>(string queryText, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
            => throw new NotSupportedException();

        public IOrderedQueryable<T> GetItemLinqQueryable<T>(bool allowSynchronousQueryExecution = false, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
            => throw new NotSupportedException();

        public Task<ItemResponse<T>> UpsertItemAsync<T>(T item, PartitionKey partitionKey = default, ItemRequestOptions? requestOptions = null)
            => throw new NotSupportedException();

        public Task<ItemResponse<T>> CreateItemAsync<T>(T item, PartitionKey partitionKey = default)
            => throw new NotSupportedException();

        public async Task<ItemResponse<T>> DeleteItemAsync<T>(string id, PartitionKey partitionKey)
        {
            var inFlight = Interlocked.Increment(ref _inFlightDeletes);
            try
            {
                int seen;
                while (inFlight > (seen = Volatile.Read(ref _maxObservedDeleteConcurrency)) &&
                       Interlocked.CompareExchange(ref _maxObservedDeleteConcurrency, inFlight, seen) != seen)
                {
                }

                // Hold the call open long enough for overlapping deletes to be observable.
                await Task.Delay(50);
                DeletedIds.Add(id);
                return null!;
            }
            finally
            {
                Interlocked.Decrement(ref _inFlightDeletes);
            }
        }

        public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey)
            => throw new NotSupportedException();

        public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey, ItemRequestOptions requestOptions)
            => throw new NotSupportedException();

        public Task<ItemResponse<T>> PatchItemAsync<T>(string id, PartitionKey partitionKey, IReadOnlyList<PatchOperation> patchOperations)
            => throw new NotSupportedException();

        public Task<ContainerResponse> DeleteContainerAsync()
            => throw new NotSupportedException();

        public Task<FeedResponse<T>> ReadManyItemsAsync<T>(IReadOnlyList<(string id, PartitionKey partitionKey)> items)
            => throw new NotSupportedException();
    }

    private sealed class SingleBatchFeedIterator<T> : FeedIterator<T>
    {
        private readonly IReadOnlyList<T> _items;
        private bool _consumed;

        public SingleBatchFeedIterator(IReadOnlyList<T> items) => _items = items;

        public override bool HasMoreResults => !_consumed;

        public override Task<FeedResponse<T>> ReadNextAsync(CancellationToken cancellationToken = default)
        {
            _consumed = true;
            return Task.FromResult<FeedResponse<T>>(new ListFeedResponse<T>(_items));
        }
    }

    private sealed class ListFeedResponse<T> : FeedResponse<T>
    {
        private readonly IReadOnlyList<T> _items;

        public ListFeedResponse(IReadOnlyList<T> items) => _items = items;

        public override string ContinuationToken => null!;
        public override int Count => _items.Count;
        public override string IndexMetrics => null!;
        public override Headers Headers { get; } = new();
        public override IEnumerable<T> Resource => _items;
        public override HttpStatusCode StatusCode => HttpStatusCode.OK;
        public override CosmosDiagnostics Diagnostics => null!;
        public override IEnumerator<T> GetEnumerator() => _items.GetEnumerator();
    }
}
