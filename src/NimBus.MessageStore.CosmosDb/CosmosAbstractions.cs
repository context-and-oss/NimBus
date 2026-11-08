using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Linq;
using Microsoft.Extensions.Logging;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.MessageStore;

public interface ICosmosClientAdapter
{
    ICosmosDatabaseAdapter GetDatabase(string id);

    /// <summary>
    /// Reports the effective consistency level requests issued through this client observe:
    /// the client-level override when one is configured, otherwise the account default.
    /// The default implementation fails closed: an adapter that cannot report the level cannot
    /// prove that inbox duplicate checks observe the latest committed records, and guessing
    /// would silently reopen the cross-replica duplicate window — so it throws instead.
    /// </summary>
    /// <param name="cancellationToken">A token that can cancel the account read.</param>
    /// <returns>The effective consistency level, or <see langword="null"/> when the provider reports none.</returns>
    Task<ConsistencyLevel?> GetAccountConsistencyLevelAsync(CancellationToken cancellationToken)
        => throw new NotSupportedException(
            $"This {nameof(ICosmosClientAdapter)} implementation does not report the account consistency level. " +
            $"Override {nameof(GetAccountConsistencyLevelAsync)} to report it, or acknowledge relaxed consistency " +
            "where the consuming feature offers that option.");
}

public interface ICosmosDatabaseAdapter
{
    ICosmosContainerAdapter GetContainer(string id);
    Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(string id, string partitionKeyPath);

    /// <summary>
    /// Creates or resolves a container while propagating cancellation to the provider.
    /// The default implementation preserves compatibility with existing adapters.
    /// </summary>
    Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(
        string id,
        string partitionKeyPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return CreateContainerIfNotExistsAsync(id, partitionKeyPath);
    }

    /// <summary>
    /// Creates or resolves a container, applying container-level settings such as
    /// <see cref="ContainerProperties.DefaultTimeToLive"/>. Cosmos honours a document's
    /// <c>ttl</c> only when the container's DefaultTimeToLive is set.
    /// The default implementation fails closed: an adapter that has not overridden this
    /// overload cannot apply container-level settings, and silently dropping a requested
    /// DefaultTimeToLive would create a TTL-disabled container that accepts item TTLs and
    /// never expires anything — so a non-null DefaultTimeToLive throws instead. Properties
    /// that carry no DefaultTimeToLive forward for compatibility.
    /// </summary>
    Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(
        ContainerProperties containerProperties,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(containerProperties);

        if (containerProperties.DefaultTimeToLive is null)
        {
            return CreateContainerIfNotExistsAsync(
                containerProperties.Id, containerProperties.PartitionKeyPath, cancellationToken);
        }

        throw new NotSupportedException(
            $"This {nameof(ICosmosDatabaseAdapter)} implementation cannot apply {nameof(ContainerProperties)}. " +
            $"Override CreateContainerIfNotExistsAsync({nameof(ContainerProperties)}, CancellationToken) to honor " +
            $"container-level settings such as {nameof(ContainerProperties.DefaultTimeToLive)}.");
    }
}

public interface ICosmosContainerAdapter
{
    FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition);
    FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition, string? continuationToken = null, QueryRequestOptions? requestOptions = null);
    FeedIterator<T> GetItemQueryIterator<T>(string queryText);
    FeedIterator<T> GetItemQueryIterator<T>(string queryText, string? continuationToken = null, QueryRequestOptions? requestOptions = null);
    IOrderedQueryable<T> GetItemLinqQueryable<T>(bool allowSynchronousQueryExecution = false, string? continuationToken = null, QueryRequestOptions? requestOptions = null);
    Task<ItemResponse<T>> CreateItemAsync<T>(T item, PartitionKey partitionKey = default);

    /// <summary>
    /// Creates an item while propagating cancellation to the provider.
    /// The default implementation preserves compatibility with existing adapters.
    /// </summary>
    Task<ItemResponse<T>> CreateItemAsync<T>(
        T item,
        PartitionKey partitionKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return CreateItemAsync(item, partitionKey);
    }

    Task<ItemResponse<T>> UpsertItemAsync<T>(T item, PartitionKey partitionKey = default, ItemRequestOptions? requestOptions = null);
    /// <summary>Replaces an existing item with request preconditions; never creates a missing item.</summary>
    Task<ItemResponse<T>> ReplaceItemAsync<T>(T item, string id, PartitionKey partitionKey, ItemRequestOptions requestOptions) =>
        throw new NotSupportedException("This adapter does not support conditional replacements.");
    Task<ItemResponse<T>> DeleteItemAsync<T>(string id, PartitionKey partitionKey);

    /// <summary>
    /// Deletes an item while propagating cancellation to the provider.
    /// The default implementation preserves compatibility with existing adapters.
    /// </summary>
    Task<ItemResponse<T>> DeleteItemAsync<T>(
        string id,
        PartitionKey partitionKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return DeleteItemAsync<T>(id, partitionKey);
    }

    /// <summary>
    /// Deletes an item with request options (e.g. an <see cref="RequestOptions.IfMatchEtag"/>
    /// precondition) while propagating cancellation to the provider.
    /// The default implementation fails closed: an adapter that has not overridden this overload
    /// cannot honor a precondition, and silently dropping it would let a stale delete remove a
    /// freshly recreated document — so non-null options throw instead of degrading to an
    /// unconditional delete. A null options value forwards for compatibility.
    /// </summary>
    Task<ItemResponse<T>> DeleteItemAsync<T>(
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions requestOptions,
        CancellationToken cancellationToken)
    {
        if (requestOptions is null)
        {
            return DeleteItemAsync<T>(id, partitionKey, cancellationToken);
        }

        throw new NotSupportedException(
            $"This {nameof(ICosmosContainerAdapter)} implementation does not support conditional deletes. " +
            "Override DeleteItemAsync(id, partitionKey, requestOptions, cancellationToken) to honor " +
            $"{nameof(ItemRequestOptions)} preconditions such as {nameof(ItemRequestOptions.IfMatchEtag)}.");
    }

    Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey);

    /// <summary>
    /// Reads an item while propagating cancellation to the provider.
    /// The default implementation preserves compatibility with existing adapters.
    /// </summary>
    Task<ItemResponse<T>> ReadItemAsync<T>(
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions requestOptions,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ReadItemAsync<T>(id, partitionKey, requestOptions);
    }

    Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey, ItemRequestOptions requestOptions);
    Task<ItemResponse<T>> PatchItemAsync<T>(string id, PartitionKey partitionKey, IReadOnlyList<PatchOperation> patchOperations);
    Task<ContainerResponse> DeleteContainerAsync();
    Task<FeedResponse<T>> ReadManyItemsAsync<T>(IReadOnlyList<(string id, PartitionKey partitionKey)> items);
}

internal sealed class TransientTranslatingCosmosClientAdapter : ICosmosClientAdapter
{
    private readonly ICosmosClientAdapter _inner;
    private readonly ILogger? _logger;

    public TransientTranslatingCosmosClientAdapter(ICosmosClientAdapter inner, ILogger? logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger;
    }

    public ICosmosDatabaseAdapter GetDatabase(string id)
    {
        var database = CosmosExceptionTranslation.TranslateTransient(
            () => _inner.GetDatabase(id),
            _logger);
        return new TransientTranslatingCosmosDatabaseAdapter(database, _logger);
    }

    // Forward explicitly: default interface dispatch happens on this wrapper's type, so
    // without the forward an inner adapter's override would be unreachable and the
    // fail-closed default would fire even for consistency-aware adapters.
    public Task<ConsistencyLevel?> GetAccountConsistencyLevelAsync(CancellationToken cancellationToken) =>
        CosmosExceptionTranslation.TranslateTransientAsync(
            () => _inner.GetAccountConsistencyLevelAsync(cancellationToken),
            _logger);
}

internal sealed class TransientTranslatingCosmosDatabaseAdapter : ICosmosDatabaseAdapter
{
    private readonly ICosmosDatabaseAdapter _inner;
    private readonly ILogger? _logger;

    public TransientTranslatingCosmosDatabaseAdapter(ICosmosDatabaseAdapter inner, ILogger? logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger;
    }

    public ICosmosContainerAdapter GetContainer(string id)
    {
        var container = CosmosExceptionTranslation.TranslateTransient(
            () => _inner.GetContainer(id),
            _logger);
        return new TransientTranslatingCosmosContainerAdapter(container, _logger);
    }

    /// <inheritdoc />
    public async Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(
        string id,
        string partitionKeyPath)
    {
        var container = await CosmosExceptionTranslation.TranslateTransientAsync(
            () => _inner.CreateContainerIfNotExistsAsync(id, partitionKeyPath),
            _logger);
        return new TransientTranslatingCosmosContainerAdapter(container, _logger);
    }

    public async Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(
        string id,
        string partitionKeyPath,
        CancellationToken cancellationToken)
    {
        var container = await CosmosExceptionTranslation.TranslateTransientAsync(
            () => _inner.CreateContainerIfNotExistsAsync(id, partitionKeyPath, cancellationToken),
            _logger);
        return new TransientTranslatingCosmosContainerAdapter(container, _logger);
    }

    // Forward explicitly: default interface dispatch happens on this wrapper's type, so
    // without the forward an inner adapter's override would be unreachable and the
    // fail-closed default would fire even for TTL-aware adapters.
    public async Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(
        ContainerProperties containerProperties,
        CancellationToken cancellationToken = default)
    {
        var container = await CosmosExceptionTranslation.TranslateTransientAsync(
            () => _inner.CreateContainerIfNotExistsAsync(containerProperties, cancellationToken),
            _logger);
        return new TransientTranslatingCosmosContainerAdapter(container, _logger);
    }
}

internal sealed class TransientTranslatingCosmosContainerAdapter : ICosmosContainerAdapter
{
    private readonly ICosmosContainerAdapter _inner;
    private readonly ILogger? _logger;

    public TransientTranslatingCosmosContainerAdapter(ICosmosContainerAdapter inner, ILogger? logger)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _logger = logger;
    }

    public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition) =>
        WrapQueryIterator(() => _inner.GetItemQueryIterator<T>(queryDefinition));

    public FeedIterator<T> GetItemQueryIterator<T>(
        QueryDefinition queryDefinition,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null) =>
        WrapQueryIterator(() => _inner.GetItemQueryIterator<T>(
            queryDefinition,
            continuationToken,
            requestOptions));

    public FeedIterator<T> GetItemQueryIterator<T>(string queryText) =>
        WrapQueryIterator(() => _inner.GetItemQueryIterator<T>(queryText));

    public FeedIterator<T> GetItemQueryIterator<T>(
        string queryText,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null) =>
        WrapQueryIterator(() => _inner.GetItemQueryIterator<T>(
            queryText,
            continuationToken,
            requestOptions));

    public IOrderedQueryable<T> GetItemLinqQueryable<T>(
        bool allowSynchronousQueryExecution = false,
        string? continuationToken = null,
        QueryRequestOptions? requestOptions = null) =>
        CosmosExceptionTranslation.TranslateTransient(
            () => _inner.GetItemLinqQueryable<T>(
                allowSynchronousQueryExecution,
                continuationToken,
                requestOptions),
            _logger);

    /// <inheritdoc />
    public Task<ItemResponse<T>> CreateItemAsync<T>(
        T item,
        PartitionKey partitionKey = default) =>
        CosmosExceptionTranslation.TranslateTransientAsync(
            () => _inner.CreateItemAsync(item, partitionKey),
            _logger);

    public Task<ItemResponse<T>> CreateItemAsync<T>(
        T item,
        PartitionKey partitionKey,
        CancellationToken cancellationToken) =>
        CosmosExceptionTranslation.TranslateTransientAsync(
            () => _inner.CreateItemAsync(item, partitionKey, cancellationToken),
            _logger);

    public Task<ItemResponse<T>> UpsertItemAsync<T>(
        T item,
        PartitionKey partitionKey = default,
        ItemRequestOptions? requestOptions = null) =>
        CosmosExceptionTranslation.TranslateTransientAsync(
            () => _inner.UpsertItemAsync(item, partitionKey, requestOptions),
            _logger);

    public Task<ItemResponse<T>> ReplaceItemAsync<T>(T item, string id, PartitionKey partitionKey, ItemRequestOptions requestOptions) =>
        CosmosExceptionTranslation.TranslateTransientAsync(
            () => _inner.ReplaceItemAsync(item, id, partitionKey, requestOptions), _logger);

    /// <inheritdoc />
    public Task<ItemResponse<T>> DeleteItemAsync<T>(
        string id,
        PartitionKey partitionKey) =>
        CosmosExceptionTranslation.TranslateTransientAsync(
            () => _inner.DeleteItemAsync<T>(id, partitionKey),
            _logger);

    public Task<ItemResponse<T>> DeleteItemAsync<T>(
        string id,
        PartitionKey partitionKey,
        CancellationToken cancellationToken) =>
        CosmosExceptionTranslation.TranslateTransientAsync(
            () => _inner.DeleteItemAsync<T>(id, partitionKey, cancellationToken),
            _logger);

    public Task<ItemResponse<T>> DeleteItemAsync<T>(
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions requestOptions,
        CancellationToken cancellationToken) =>
        CosmosExceptionTranslation.TranslateTransientAsync(
            () => _inner.DeleteItemAsync<T>(id, partitionKey, requestOptions, cancellationToken),
            _logger);

    /// <inheritdoc />
    public Task<ItemResponse<T>> ReadItemAsync<T>(
        string id,
        PartitionKey partitionKey) =>
        CosmosExceptionTranslation.TranslateTransientAsync(
            () => _inner.ReadItemAsync<T>(id, partitionKey),
            _logger);

    public Task<ItemResponse<T>> ReadItemAsync<T>(
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions requestOptions,
        CancellationToken cancellationToken) =>
        CosmosExceptionTranslation.TranslateTransientAsync(
            () => _inner.ReadItemAsync<T>(id, partitionKey, requestOptions, cancellationToken),
            _logger);

    public Task<ItemResponse<T>> ReadItemAsync<T>(
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions requestOptions) =>
        CosmosExceptionTranslation.TranslateTransientAsync(
            () => _inner.ReadItemAsync<T>(id, partitionKey, requestOptions),
            _logger);

    public Task<ItemResponse<T>> PatchItemAsync<T>(
        string id,
        PartitionKey partitionKey,
        IReadOnlyList<PatchOperation> patchOperations) =>
        CosmosExceptionTranslation.TranslateTransientAsync(
            () => _inner.PatchItemAsync<T>(id, partitionKey, patchOperations),
            _logger);

    public Task<ContainerResponse> DeleteContainerAsync() =>
        CosmosExceptionTranslation.TranslateTransientAsync(
            _inner.DeleteContainerAsync,
            _logger);

    public Task<FeedResponse<T>> ReadManyItemsAsync<T>(
        IReadOnlyList<(string id, PartitionKey partitionKey)> items) =>
        CosmosExceptionTranslation.TranslateTransientAsync(
            () => _inner.ReadManyItemsAsync<T>(items),
            _logger);

    private FeedIterator<T> WrapQueryIterator<T>(Func<FeedIterator<T>> createIterator)
    {
        var iterator = CosmosExceptionTranslation.TranslateTransient(createIterator, _logger);
        return CosmosExceptionTranslation.Wrap(iterator, _logger, "GetItemQueryIterator");
    }
}

public sealed class CosmosClientAdapter : ICosmosClientAdapter
{
    private readonly CosmosClient _client;
    private readonly ILogger? _logger;

    public CosmosClientAdapter(CosmosClient client)
        : this(client, null)
    {
    }

    internal CosmosClientAdapter(CosmosClient client, ILogger? logger)
    {
        _client = client;
        _logger = logger;
    }

    public ICosmosDatabaseAdapter GetDatabase(string id) => new CosmosDatabaseAdapter(_client.GetDatabase(id), _logger);

    /// <inheritdoc />
    public async Task<ConsistencyLevel?> GetAccountConsistencyLevelAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // A client-level override is what reads issued through this client actually request
        // (the SDK only permits weakening the account default); otherwise the account default
        // applies.
        if (_client.ClientOptions?.ConsistencyLevel is { } clientLevel)
        {
            return clientLevel;
        }

        var account = await CosmosExceptionTranslation.TranslateTransientAsync(
            () => _client.ReadAccountAsync(),
            _logger);
        return account?.Consistency?.DefaultConsistencyLevel;
    }
}

public sealed class CosmosDatabaseAdapter : ICosmosDatabaseAdapter
{
    private readonly Database _database;
    private readonly ILogger? _logger;

    public CosmosDatabaseAdapter(Database database)
        : this(database, null)
    {
    }

    internal CosmosDatabaseAdapter(Database database, ILogger? logger)
    {
        _database = database;
        _logger = logger;
    }

    public ICosmosContainerAdapter GetContainer(string id) => new CosmosContainerAdapter(_database.GetContainer(id), _logger);

    public async Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(string id, string partitionKeyPath)
    {
        var response = await CosmosExceptionTranslation.TranslateTransientAsync(
            () => _database.CreateContainerIfNotExistsAsync(id, partitionKeyPath),
            _logger);
        return new CosmosContainerAdapter(response.Container, _logger);
    }

    /// <inheritdoc />
    public async Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(
        string id,
        string partitionKeyPath,
        CancellationToken cancellationToken)
    {
        var response = await CosmosExceptionTranslation.TranslateTransientAsync(
            () => _database.CreateContainerIfNotExistsAsync(
                id,
                partitionKeyPath,
                cancellationToken: cancellationToken),
            _logger);
        return new CosmosContainerAdapter(response.Container, _logger);
    }

    /// <inheritdoc />
    public async Task<ICosmosContainerAdapter> CreateContainerIfNotExistsAsync(
        ContainerProperties containerProperties,
        CancellationToken cancellationToken = default)
    {
        var response = await CosmosExceptionTranslation.TranslateTransientAsync(
            () => _database.CreateContainerIfNotExistsAsync(
                containerProperties,
                cancellationToken: cancellationToken),
            _logger);
        return new CosmosContainerAdapter(response.Container, _logger);
    }
}

public sealed class CosmosContainerAdapter : ICosmosContainerAdapter
{
    private readonly Container _container;
    private readonly ILogger? _logger;

    public CosmosContainerAdapter(Container container)
        : this(container, null)
    {
    }

    internal CosmosContainerAdapter(Container container, ILogger? logger)
    {
        _container = container;
        _logger = logger;
    }

    public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition) =>
        CosmosExceptionTranslation.Wrap(_container.GetItemQueryIterator<T>(queryDefinition), _logger);

    public FeedIterator<T> GetItemQueryIterator<T>(QueryDefinition queryDefinition, string? continuationToken = null, QueryRequestOptions? requestOptions = null) =>
        CosmosExceptionTranslation.Wrap(_container.GetItemQueryIterator<T>(queryDefinition, continuationToken, requestOptions), _logger);

    public FeedIterator<T> GetItemQueryIterator<T>(string queryText) =>
        CosmosExceptionTranslation.Wrap(_container.GetItemQueryIterator<T>(queryText), _logger);

    public FeedIterator<T> GetItemQueryIterator<T>(string queryText, string? continuationToken = null, QueryRequestOptions? requestOptions = null) =>
        CosmosExceptionTranslation.Wrap(_container.GetItemQueryIterator<T>(queryText, continuationToken, requestOptions), _logger);

    public IOrderedQueryable<T> GetItemLinqQueryable<T>(bool allowSynchronousQueryExecution = false, string? continuationToken = null, QueryRequestOptions? requestOptions = null) =>
        _container.GetItemLinqQueryable<T>(allowSynchronousQueryExecution, continuationToken, requestOptions);

    public Task<ItemResponse<T>> CreateItemAsync<T>(T item, PartitionKey partitionKey = default) =>
        CosmosExceptionTranslation.TranslateTransientAsync(() => _container.CreateItemAsync(item, partitionKey), _logger);

    /// <inheritdoc />
    public Task<ItemResponse<T>> CreateItemAsync<T>(
        T item,
        PartitionKey partitionKey,
        CancellationToken cancellationToken) =>
        CosmosExceptionTranslation.TranslateTransientAsync(
            () => _container.CreateItemAsync(item, partitionKey, cancellationToken: cancellationToken),
            _logger);

    public Task<ItemResponse<T>> UpsertItemAsync<T>(T item, PartitionKey partitionKey = default, ItemRequestOptions? requestOptions = null) =>
        CosmosExceptionTranslation.TranslateTransientAsync(() => _container.UpsertItemAsync(item, partitionKey, requestOptions), _logger);

    public Task<ItemResponse<T>> ReplaceItemAsync<T>(T item, string id, PartitionKey partitionKey, ItemRequestOptions requestOptions) =>
        CosmosExceptionTranslation.TranslateTransientAsync(() => _container.ReplaceItemAsync(item, id, partitionKey, requestOptions), _logger);

    public Task<ItemResponse<T>> DeleteItemAsync<T>(string id, PartitionKey partitionKey) =>
        CosmosExceptionTranslation.TranslateTransientAsync(() => _container.DeleteItemAsync<T>(id, partitionKey), _logger);

    /// <inheritdoc />
    public Task<ItemResponse<T>> DeleteItemAsync<T>(
        string id,
        PartitionKey partitionKey,
        CancellationToken cancellationToken) =>
        CosmosExceptionTranslation.TranslateTransientAsync(
            () => _container.DeleteItemAsync<T>(id, partitionKey, cancellationToken: cancellationToken),
            _logger);

    /// <inheritdoc />
    public Task<ItemResponse<T>> DeleteItemAsync<T>(
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions requestOptions,
        CancellationToken cancellationToken) =>
        CosmosExceptionTranslation.TranslateTransientAsync(
            () => _container.DeleteItemAsync<T>(id, partitionKey, requestOptions, cancellationToken),
            _logger);

    public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey) =>
        CosmosExceptionTranslation.TranslateTransientAsync(() => _container.ReadItemAsync<T>(id, partitionKey), _logger);

    /// <inheritdoc />
    public Task<ItemResponse<T>> ReadItemAsync<T>(
        string id,
        PartitionKey partitionKey,
        ItemRequestOptions requestOptions,
        CancellationToken cancellationToken) =>
        CosmosExceptionTranslation.TranslateTransientAsync(
            () => _container.ReadItemAsync<T>(
                id,
                partitionKey,
                requestOptions,
                cancellationToken),
            _logger);

    public Task<ItemResponse<T>> ReadItemAsync<T>(string id, PartitionKey partitionKey, ItemRequestOptions requestOptions) =>
        CosmosExceptionTranslation.TranslateTransientAsync(() => _container.ReadItemAsync<T>(id, partitionKey, requestOptions), _logger);

    public Task<ItemResponse<T>> PatchItemAsync<T>(string id, PartitionKey partitionKey, IReadOnlyList<PatchOperation> patchOperations) =>
        CosmosExceptionTranslation.TranslateTransientAsync(() => _container.PatchItemAsync<T>(id, partitionKey, patchOperations), _logger);

    public Task<ContainerResponse> DeleteContainerAsync() =>
        CosmosExceptionTranslation.TranslateTransientAsync(() => _container.DeleteContainerAsync(), _logger);

    public Task<FeedResponse<T>> ReadManyItemsAsync<T>(IReadOnlyList<(string id, PartitionKey partitionKey)> items) =>
        CosmosExceptionTranslation.TranslateTransientAsync(() => _container.ReadManyItemsAsync<T>(items), _logger);
}
