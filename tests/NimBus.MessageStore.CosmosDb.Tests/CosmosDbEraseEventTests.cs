#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>Spec 049 on Cosmos DB: failed writes and source retention, which the conformance suite can't reach.</summary>
[TestClass]
public sealed class CosmosDbEraseEventTests
{
    [TestMethod]
    public async Task Reads_every_tracking_row_whatever_its_deleted_flag_and_maps_a_failed_write()
    {
        // The recording adapter answers every query with no rows and throws on deletes.
        var adapter = new RecordingCosmosClientAdapter();
        var store = new CosmosDbClient(adapter);

        var result = await store.EraseEventCopyAsync(new EventErasureRequest("Billing", "event-1", "erasure-1", "Crm", "CustomerUpdated", true));

        Assert.AreEqual(ErasurePlaceState.Outstanding, result[ErasurePlace.EventReport].State);
        Assert.AreEqual(ErasureOutstandingReasons.WriteFailed, result[ErasurePlace.EventReport].OutstandingReason);
        Assert.AreEqual(ErasurePlaceState.AlreadyAbsent, result[ErasurePlace.TrackingRow].State);
        Assert.AreEqual(ErasurePlaceState.AlreadyAbsent, result[ErasurePlace.MessageHistory].State);
        var rowQuery = adapter.Container("Billing").Queries.First().QueryText;
        StringAssert.Contains(rowQuery, "c.event.EventId = @eventId");
        Assert.IsFalse(rowQuery.Contains("deleted", StringComparison.OrdinalIgnoreCase), "Archived and removed rows keep their payload too.");
    }

    [TestMethod]
    public async Task Failed_compose_redaction_retains_history_and_row_until_a_healed_rerun()
    {
        var faults = new FaultingCosmosClient(new CosmosClientAdapter(CosmosDbStoreTestHarness.RawClient));
        var store = new CosmosDbClient(faults);
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var endpoint = $"ep-erase-{suffix}";
        var producer = $"producer-{suffix}";
        var eventId = $"event-{suffix}";
        const string payload = "{\"email\":\"leaked@example.com\"}";
        var content = new MessageContent { EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = payload } };
        await store.UploadDeadletteredMessage(eventId, "s1", endpoint, new UnresolvedEvent
        {
            EventId = eventId, SessionId = "s1", EndpointId = endpoint, EnqueuedTimeUtc = DateTime.UtcNow, MessageContent = content,
        });
        await store.StoreMessage(new MessageEntity
        {
            EventId = eventId, MessageId = $"m-{suffix}", EndpointId = endpoint, SessionId = "s1",
            EventTypeId = "OrderPlaced", To = endpoint, EnqueuedTimeUtc = DateTime.UtcNow, MessageContent = content,
        });
        await store.StoreMessageAudit(string.Empty, new MessageAuditEntity
        {
            AuditorName = "op", AuditTimestamp = DateTime.UtcNow, AuditType = MessageAuditType.Compose,
            Data = JsonConvert.SerializeObject(new { eventTypeId = "OrderPlaced", eventContent = payload }),
        }, producer, "OrderPlaced");
        var request = new EventErasureRequest(endpoint, eventId, "erasure-1", producer, "OrderPlaced", false);

        faults.FailComposeReplace = true;
        var first = await store.EraseEventCopyAsync(request);

        Assert.AreEqual(ErasureOutstandingReasons.WriteFailed, first[ErasurePlace.ComposeAudits].OutstandingReason);
        Assert.AreEqual(ErasureOutstandingReasons.RetainedForRetry, first[ErasurePlace.MessageHistory].OutstandingReason);
        Assert.AreEqual(ErasureOutstandingReasons.RetainedForRetry, first[ErasurePlace.TrackingRow].OutstandingReason);
        Assert.IsNotNull(await store.GetEvent(endpoint, eventId));

        faults.FailComposeReplace = false;
        var rerun = await store.EraseEventCopyAsync(request);

        Assert.IsTrue(rerun.IsComplete, string.Join(", ", rerun.Places));
        Assert.AreEqual(ErasurePlaceState.Redacted, rerun[ErasurePlace.ComposeAudits].State);
        Assert.AreEqual(ErasurePlaceState.Removed, rerun[ErasurePlace.TrackingRow].State);
        Assert.IsNull(await store.GetEvent(endpoint, eventId));
        Assert.IsEmpty(await store.GetEventHistory(eventId));
    }

    /// <summary>Forwards to a real adapter, and refuses replacing a Compose audit document on request.</summary>
    private sealed class FaultingCosmosClient : ICosmosClientAdapter, ICosmosDatabaseAdapter
    {
        private readonly ICosmosClientAdapter _client;
        private ICosmosDatabaseAdapter? _database;

        public FaultingCosmosClient(ICosmosClientAdapter client) => _client = client;

        public bool FailComposeReplace { get; set; }

        public ICosmosDatabaseAdapter GetDatabase(string id)
        {
            _database = _client.GetDatabase(id);
            return this;
        }

        public ICosmosContainerAdapter GetContainer(string id) => new Container(this, _database!.GetContainer(id));

        public async Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(string id, string partitionKeyPath) =>
            new Container(this, await _database!.CreateContainerIfNotExistsAsync(id, partitionKeyPath));

        public async Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(ContainerProperties containerProperties, CancellationToken cancellationToken = default) =>
            new Container(this, await _database!.CreateContainerIfNotExistsAsync(containerProperties, cancellationToken));

        private sealed class Container : ICosmosContainerAdapter
        {
            private readonly FaultingCosmosClient _owner;
            private readonly ICosmosContainerAdapter _inner;

            public Container(FaultingCosmosClient owner, ICosmosContainerAdapter inner)
            {
                _owner = owner;
                _inner = inner;
            }

            public Task<ItemResponse<T>> ReplaceItemAsync<T>(T item, string id, PartitionKey partitionKey, ItemRequestOptions requestOptions)
            {
                if (_owner.FailComposeReplace && item is AuditDocument { Audit.AuditType: MessageAuditType.Compose })
                    throw new CosmosException("compose redaction refused", HttpStatusCode.ServiceUnavailable, 0, string.Empty, 0);
                return _inner.ReplaceItemAsync(item, id, partitionKey, requestOptions);
            }

            public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition) => _inner.GetItemQueryIterator<T>(queryDefinition);
            public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition, string? continuationToken = null, QueryRequestOptions? requestOptions = null) => _inner.GetItemQueryIterator<T>(queryDefinition, continuationToken, requestOptions);
            public FeedIterator<T> GetItemQueryIterator<T>(string queryText) => _inner.GetItemQueryIterator<T>(queryText);
            public FeedIterator<T> GetItemQueryIterator<T>(string queryText, string? continuationToken = null, QueryRequestOptions? requestOptions = null) => _inner.GetItemQueryIterator<T>(queryText, continuationToken, requestOptions);
            public IOrderedQueryable<T> GetItemLinqQueryable<T>(bool allowSynchronousQueryExecution = false, string? continuationToken = null, QueryRequestOptions? requestOptions = null) => _inner.GetItemLinqQueryable<T>(allowSynchronousQueryExecution, continuationToken, requestOptions);
            public Task<ItemResponse<T>> CreateItemAsync<T>(T item, PartitionKey partitionKey = default) => _inner.CreateItemAsync(item, partitionKey);
            public Task<ItemResponse<T>> CreateItemAsync<T>(T item, PartitionKey partitionKey, CancellationToken cancellationToken) => _inner.CreateItemAsync(item, partitionKey, cancellationToken);
            public Task<ItemResponse<T>> UpsertItemAsync<T>(T item, PartitionKey partitionKey = default, ItemRequestOptions? requestOptions = null) => _inner.UpsertItemAsync(item, partitionKey, requestOptions);
            public Task<ItemResponse<T>> DeleteItemAsync<T>(string id, PartitionKey partitionKey) => _inner.DeleteItemAsync<T>(id, partitionKey);
            public Task<ItemResponse<T>> DeleteItemAsync<T>(string id, PartitionKey partitionKey, CancellationToken cancellationToken) => _inner.DeleteItemAsync<T>(id, partitionKey, cancellationToken);
            public Task<ItemResponse<T>> DeleteItemAsync<T>(string id, PartitionKey partitionKey, ItemRequestOptions requestOptions, CancellationToken cancellationToken) => _inner.DeleteItemAsync<T>(id, partitionKey, requestOptions, cancellationToken);
            public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey) => _inner.ReadItemAsync<T>(id, partitionKey);
            public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey, ItemRequestOptions requestOptions, CancellationToken cancellationToken) => _inner.ReadItemAsync<T>(id, partitionKey, requestOptions, cancellationToken);
            public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey, ItemRequestOptions requestOptions) => _inner.ReadItemAsync<T>(id, partitionKey, requestOptions);
            public Task<ItemResponse<T>> PatchItemAsync<T>(string id, PartitionKey partitionKey, IReadOnlyList<PatchOperation> patchOperations) => _inner.PatchItemAsync<T>(id, partitionKey, patchOperations);
            public Task<ContainerResponse> DeleteContainerAsync() => _inner.DeleteContainerAsync();
            public Task<FeedResponse<T>> ReadManyItemsAsync<T>(IReadOnlyList<(string id, PartitionKey partitionKey)> items) => _inner.ReadManyItemsAsync<T>(items);
        }
    }
}
