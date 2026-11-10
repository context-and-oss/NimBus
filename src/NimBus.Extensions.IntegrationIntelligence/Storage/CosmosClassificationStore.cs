using System.Net;
using System.Runtime.CompilerServices;
using Microsoft.Azure.Cosmos;

namespace NimBus.Extensions.IntegrationIntelligence.Storage;

/// <summary>Atomic per-failure state and results using Cosmos conditional writes.</summary>
public sealed class CosmosClassificationStore : AtomicClassificationStore
{
    /// <summary>Container the store uses unless the host passes another name; the deployment template provisions it.</summary>
    public const string DefaultContainerName = "failureclassifications";

    /// <summary>
    /// How long one container check answers for the bulk-read batches that follow it. A bulk read
    /// is many batches and most of them miss items, so checking per batch would spend a throttled
    /// metadata request on each. A container removed while the app runs is noticed this much later.
    /// </summary>
    internal static readonly TimeSpan ContainerCheckLifetime = TimeSpan.FromSeconds(30);

    private readonly Container _container;
    private readonly RecentCheck _containerCheck;

    public CosmosClassificationStore(CosmosClient client, string databaseName, string containerName = DefaultContainerName, TimeProvider? clock = null) : base(clock)
    {
        _container = client.GetContainer(databaseName, containerName);
        // Shared by concurrent batches, so it never takes one caller's token.
        _containerCheck = new RecentCheck(() => _container.ReadContainerAsync(cancellationToken: CancellationToken.None),
            ContainerCheckLifetime, clock ?? TimeProvider.System);
    }

    protected override async Task<(ClassificationDocument Document, string? Version)> ReadAsync(string failureId, CancellationToken ct)
    {
        try
        {
            var response = await _container.ReadItemAsync<ClassificationDocument>("state", new PartitionKey(failureId), cancellationToken: ct).ConfigureAwait(false);
            return (response.Resource, response.ETag);
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            await _container.ReadContainerAsync(cancellationToken: ct).ConfigureAwait(false);
            return (new ClassificationDocument { FailureMessageId = failureId }, null);
        }
    }

    protected override async Task<bool> TryWriteAsync(ClassificationDocument document, string? version, CancellationToken ct)
    {
        try
        {
            if (version is null)
                await _container.CreateItemAsync(document, new PartitionKey(document.FailureMessageId), cancellationToken: ct).ConfigureAwait(false);
            else
                await _container.ReplaceItemAsync(document, "state", new PartitionKey(document.FailureMessageId), new ItemRequestOptions { IfMatchEtag = version }, ct).ConfigureAwait(false);
            return true;
        }
        catch (CosmosException exception) when (exception.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    /// <summary>
    /// Point reads of the per-failure <c>state</c> items through <c>ReadManyItemsAsync</c>; never a
    /// cross-partition query. ReadMany omits missing items silently, so when any id is absent the
    /// container itself is checked, and a missing container raises instead of reading as empty.
    /// The batches of one bulk read share a single check (<see cref="ContainerCheckLifetime"/>).
    /// </summary>
    protected override async Task<IReadOnlyList<ClassificationDocument>> ReadManyAsync(IReadOnlyList<string> failureIds, CancellationToken ct)
    {
        if (failureIds.Count == 0) return [];
        var items = failureIds.Select(id => ("state", new PartitionKey(id))).ToList();
        var response = await _container.ReadManyItemsAsync<ClassificationDocument>(items, cancellationToken: ct).ConfigureAwait(false);
        var documents = response.Resource.ToList();
        if (documents.Count < failureIds.Count)
            await _containerCheck.EnsureAsync().WaitAsync(ct).ConfigureAwait(false);
        return documents;
    }

    protected override async IAsyncEnumerable<ClassificationDocument> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
    {
        using var iterator = _container.GetItemQueryIterator<ClassificationDocument>("SELECT * FROM c WHERE c.id='state' AND c.Deleted=false",
            requestOptions: new QueryRequestOptions { MaxItemCount = 100 });
        while (iterator.HasMoreResults)
            foreach (var document in await iterator.ReadNextAsync(ct).ConfigureAwait(false)) yield return document;
    }
}
