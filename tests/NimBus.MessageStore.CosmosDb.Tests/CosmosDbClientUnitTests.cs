#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>
/// Pure unit tests for <see cref="CosmosDbClient"/> backed by fake adapters.
/// Unlike the conformance suite these run without a live Cosmos account.
/// </summary>
[TestClass]
public sealed class CosmosDbClientUnitTests
{
    private static readonly string[] SingleEndpointId = ["ep-1"];
    private static readonly string[] SensitiveDiagnosticFragments =
        ["secret-host", "secret-db", "activity-1"];

    [TestMethod]
    public async Task DownloadEndpointStateCount_stamps_EventTime_in_utc()
    {
        var client = new CosmosDbClient(new FakeCosmosClientAdapter());

        var state = await client.DownloadEndpointStateCount("endpoint-1");

        Assert.AreEqual(DateTimeKind.Utc, state.EventTime.Kind,
            "EventTime must be UTC — the SQL Server and in-memory stores stamp UtcNow; local time skews cross-provider comparisons.");
        Assert.IsTrue(Math.Abs((DateTime.UtcNow - state.EventTime).TotalMinutes) < 1.0,
            $"EventTime {state.EventTime:O} should be within a minute of DateTime.UtcNow.");
    }

    [TestMethod]
    public async Task GetEndpointContainer_caches_handle_so_ensure_exists_runs_once_per_endpoint()
    {
        var adapter = new FakeCosmosClientAdapter();
        var client = new CosmosDbClient(adapter);

        // Repeated operations on the same endpoint should resolve the container
        // once; the cached handle skips the control-plane round-trip after.
        for (var i = 0; i < 3; i++)
        {
            await client.DownloadEndpointStateCount("endpoint-1");
        }

        Assert.AreEqual(1, adapter.GetCreateContainerCallCount("endpoint-1"),
            "CreateContainerIfNotExistsAsync must run once per container; subsequent accesses reuse the cached handle.");
    }

    [TestMethod]
    public async Task PurgeMessages_evicts_cached_handle_so_next_access_recreates_container()
    {
        var adapter = new FakeCosmosClientAdapter();
        var client = new CosmosDbClient(adapter);

        await client.DownloadEndpointStateCount("endpoint-1");

        // Purge deletes the container and must evict the cache, so the next
        // access re-runs "ensure exists" instead of reusing a stale handle.
        var purged = await client.PurgeMessages("endpoint-1");
        await client.DownloadEndpointStateCount("endpoint-1");

        Assert.IsTrue(purged, "PurgeMessages should succeed against the fake adapter.");
        Assert.AreEqual(2, adapter.GetCreateContainerCallCount("endpoint-1"),
            "After a purge the cached handle is stale; the next access must re-ensure the container exists.");
    }

    [TestMethod]
    public async Task GetEndpointContainer_does_not_cache_faulted_creation_so_next_access_retries()
    {
        var adapter = new FakeCosmosClientAdapter();
        adapter.CreateFailuresRemaining = 1;
        var client = new CosmosDbClient(adapter);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => client.DownloadEndpointStateCount("endpoint-1"));

        // The faulted creation must not be cached — the retry should run
        // "ensure exists" again and succeed.
        await client.DownloadEndpointStateCount("endpoint-1");

        Assert.AreEqual(2, adapter.GetCreateContainerCallCount("endpoint-1"),
            "A failed creation must be evicted so the next caller retries CreateContainerIfNotExistsAsync.");
    }

    [TestMethod]
    public async Task SetEndpointMetadata_reports_status_retry_and_safe_context_without_provider_details()
    {
        var logger = new CapturingLogger();
        var container = new FakeContainerAdapter
        {
            UpsertException = new TestCosmosException(
                "server=tcp:secret-host;database=secret-db",
                HttpStatusCode.TooManyRequests,
                "activity-1",
                TimeSpan.FromSeconds(7)),
        };
        var client = new CosmosDbClient(new FakeCosmosClientAdapter(container), logger);

        await Assert.ThrowsExactlyAsync<RequestLimitException>(
            () => client.SetEndpointMetadata(new EndpointMetadata { EndpointId = "ep-1" }));

        Assert.IsTrue(logger.Entries.Any(e =>
                e.Level == LogLevel.Warning &&
                e.Exception is null &&
                e.Message.Contains("UpsertItemAsync", StringComparison.Ordinal) &&
                e.Message.Contains("429", StringComparison.Ordinal) &&
                e.Message.Contains("00:00:07", StringComparison.Ordinal)),
            "Transient upserts should include safe operation, status, and retry diagnostics without attaching the provider exception.");
        Assert.IsFalse(logger.Entries.Any(e => ContainsSensitiveDetails(e.Message, e.Exception)),
            "Contextual warnings must not expose Cosmos account, database, or activity details.");
    }

    [TestMethod]
    public async Task Read_query_and_read_many_transients_are_logged_at_the_translation_boundary()
    {
        await AssertTransientOperationLogged(
            "ReadItemAsync",
            client => client.GetEndpointMetadata("ep-1"));
        await AssertTransientOperationLogged(
            "ReadManyItemsAsync",
            client => client.GetMetadatas(SingleEndpointId)!);
        await AssertTransientOperationLogged(
            "GetItemQueryIterator",
            client => client.GetMetadatas());
    }

    [TestMethod]
    public async Task StoreMessage_translates_service_unavailable_without_leaking_provider_details()
    {
        var container = new FakeContainerAdapter
        {
            UpsertException = new CosmosException(
                "server=tcp:secret-host;database=secret-db",
                HttpStatusCode.ServiceUnavailable,
                0,
                "activity-1",
                0),
        };
        var client = new CosmosDbClient(new FakeCosmosClientAdapter(container));

        var exception = await Assert.ThrowsExactlyAsync<StorageProviderTransientException>(() =>
            client.StoreMessage(new MessageEntity
            {
                EventId = "event-1",
                MessageId = "message-1",
                EndpointId = "endpoint-1",
                EnqueuedTimeUtc = DateTime.UtcNow,
                MessageContent = new(),
            }));

        Assert.IsNull(exception.RetryAfter);
        Assert.IsFalse(exception.Message.Contains("secret-host", StringComparison.Ordinal));
        Assert.IsFalse(exception.Message.Contains("secret-db", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Transient_translating_feed_iterator_disposes_inner_iterator()
    {
        var inner = new DisposableFeedIterator<object>();

        using (CosmosExceptionTranslation.Wrap(inner))
        {
        }

        Assert.IsTrue(inner.IsDisposed);
    }

    private static async Task AssertTransientOperationLogged(
        string expectedOperation,
        Func<CosmosDbClient, Task> operation)
    {
        var logger = new CapturingLogger();
        var container = new FakeContainerAdapter
        {
            OperationException = new TestCosmosException(
                "server=tcp:secret-host;database=secret-db",
                HttpStatusCode.ServiceUnavailable,
                "activity-1",
                null),
        };
        var client = new CosmosDbClient(new FakeCosmosClientAdapter(container), logger);

        await Assert.ThrowsExactlyAsync<StorageProviderTransientException>(() => operation(client));

        Assert.IsTrue(logger.Entries.Any(e =>
                e.Level == LogLevel.Warning &&
                e.Exception is null &&
                e.Message.Contains(expectedOperation, StringComparison.Ordinal) &&
                e.Message.Contains("503", StringComparison.Ordinal)),
            $"{expectedOperation} should log its operation and status at the shared transient translation boundary.");
        Assert.IsFalse(logger.Entries.Any(e => ContainsSensitiveDetails(e.Message, e.Exception)),
            "Transient diagnostics must not expose Cosmos host, database, activity, or provider-exception details.");
    }

    private static bool ContainsSensitiveDetails(string message, Exception? exception) =>
        SensitiveDiagnosticFragments.Any(fragment =>
            message.Contains(fragment, StringComparison.Ordinal) ||
            exception?.ToString().Contains(fragment, StringComparison.Ordinal) == true);

    private sealed class CapturingLogger : ILogger<CosmosDbClient>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private sealed class FakeCosmosClientAdapter : ICosmosClientAdapter, ICosmosDatabaseAdapter
    {
        private readonly FakeContainerAdapter _container;
        private readonly Dictionary<string, int> _createContainerCalls = new(StringComparer.Ordinal);

        public FakeCosmosClientAdapter(FakeContainerAdapter? container = null) => _container = container ?? new FakeContainerAdapter();

        public int CreateFailuresRemaining { get; set; }

        public int GetCreateContainerCallCount(string id) => _createContainerCalls.GetValueOrDefault(id);

        public ICosmosDatabaseAdapter GetDatabase(string id) => this;

        public ICosmosContainerAdapter GetContainer(string id) => _container;

        public Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(string id, string partitionKeyPath)
        {
            _createContainerCalls[id] = _createContainerCalls.GetValueOrDefault(id) + 1;
            if (CreateFailuresRemaining > 0)
            {
                CreateFailuresRemaining--;
                throw new InvalidOperationException("Simulated container creation failure.");
            }

            return Task.FromResult<ICosmosContainerAdapter>(_container);
        }

        // Endpoint containers are created through the ContainerProperties overload, which
        // fails closed unless an adapter implements it.
        public Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(
            ContainerProperties containerProperties,
            CancellationToken cancellationToken = default)
            => CreateContainerIfNotExistsAsync(containerProperties.Id, containerProperties.PartitionKeyPath);
    }

    private sealed class FakeContainerResponse : ContainerResponse
    {
    }

    private sealed class TestCosmosException : CosmosException
    {
        private readonly TimeSpan? _retryAfter;

        public TestCosmosException(string message, HttpStatusCode statusCode, string activityId, TimeSpan? retryAfter)
            : base(message, statusCode, 0, activityId, 0)
        {
            _retryAfter = retryAfter;
        }

        public override TimeSpan? RetryAfter => _retryAfter;
    }

    private sealed class FakeContainerAdapter : ICosmosContainerAdapter
    {
        public Exception? UpsertException { get; set; }

        public Exception? OperationException { get; set; }

        public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition)
            => CreateQueryIterator<T>();

        public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
            => CreateQueryIterator<T>();

        public FeedIterator<T> GetItemQueryIterator<T>(string queryText)
            => CreateQueryIterator<T>();

        public FeedIterator<T> GetItemQueryIterator<T>(string queryText, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
            => CreateQueryIterator<T>();

        public IOrderedQueryable<T> GetItemLinqQueryable<T>(bool allowSynchronousQueryExecution = false, string? continuationToken = null, QueryRequestOptions? requestOptions = null)
            => throw new NotSupportedException();

        public Task<ItemResponse<T>> UpsertItemAsync<T>(T item, PartitionKey partitionKey = default, ItemRequestOptions? requestOptions = null)
            => Task.FromException<ItemResponse<T>>(UpsertException ?? new NotSupportedException());

        public Task<ItemResponse<T>> CreateItemAsync<T>(T item, PartitionKey partitionKey = default)
            => throw new NotSupportedException();

        public Task<ItemResponse<T>> DeleteItemAsync<T>(string id, PartitionKey partitionKey)
            => throw new NotSupportedException();

        public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey)
            => Task.FromException<ItemResponse<T>>(OperationException ?? new NotSupportedException());

        public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey, ItemRequestOptions requestOptions)
            => ReadItemAsync<T>(id, partitionKey);

        public Task<ItemResponse<T>> PatchItemAsync<T>(string id, PartitionKey partitionKey, IReadOnlyList<PatchOperation> patchOperations)
            => throw new NotSupportedException();

        public Task<ContainerResponse> DeleteContainerAsync()
            => Task.FromResult<ContainerResponse>(new FakeContainerResponse());

        public Task<FeedResponse<T>> ReadManyItemsAsync<T>(IReadOnlyList<(string id, PartitionKey partitionKey)> items)
            => Task.FromException<FeedResponse<T>>(OperationException ?? new NotSupportedException());

        private FeedIterator<T> CreateQueryIterator<T>() => OperationException is null
            ? new EmptyFeedIterator<T>()
            : new ThrowingFeedIterator<T>(OperationException);
    }

    private sealed class EmptyFeedIterator<T> : FeedIterator<T>
    {
        public override bool HasMoreResults => false;

        public override Task<FeedResponse<T>> ReadNextAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Iterator is empty.");
    }

    private sealed class DisposableFeedIterator<T> : FeedIterator<T>
    {
        public bool IsDisposed { get; private set; }

        public override bool HasMoreResults => false;

        public override Task<FeedResponse<T>> ReadNextAsync(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Iterator is empty.");

        protected override void Dispose(bool disposing)
        {
            IsDisposed = disposing;
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingFeedIterator<T> : FeedIterator<T>
    {
        private readonly Exception _exception;

        public ThrowingFeedIterator(Exception exception) => _exception = exception;

        public override bool HasMoreResults => true;

        public override Task<FeedResponse<T>> ReadNextAsync(CancellationToken cancellationToken = default)
            => Task.FromException<FeedResponse<T>>(_exception);
    }
}
