using Microsoft.Azure.Cosmos;
using System.Net;

namespace NimBus.MessageStore;

internal sealed partial class CosmosDbMessageTrackingStore
{
    /// <summary>
    /// <c>c.status</c> (the write-side authority) is not mirrored into the
    /// embedded event document on upsert, so reads must hydrate
    /// <see cref="UnresolvedEvent.ResolutionStatus"/> from it — otherwise every
    /// Cosmos read reports the enum default. The SQL and in-memory providers
    /// map status on read the same way.
    /// </summary>
    private static UnresolvedEvent HydrateResolutionStatus(EventDbo dbo)
    {
        if (dbo?.Event != null && Enum.TryParse<ResolutionStatus>(dbo.Status, out var status))
        {
            dbo.Event.ResolutionStatus = status;
        }

        return dbo?.Event!;
    }

    // Completed and Skipped rows carry deleted=true by design (see CreateCompletedDbo); a
    // deleted row in any other status was removed or archived.
    private static bool IsRemoved(EventDbo dbo)
        => (dbo.Deleted ?? false) && dbo.Status is not (CompletedStatus or SkippedStatus);

    public Task<UnresolvedEvent?> GetPendingEvent(string endpointId, string eventId, string sessionId) =>
        GetEvent(endpointId, eventId, sessionId, PendingStatus);

    public async Task<UnresolvedEvent?> GetPendingHandoffByExternalJobId(string endpointId, string externalJobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(externalJobId)) return null;
        var container = await _getEndpointContainer(endpointId);
        // Filter on event.ExternalJobId (the persisted location) plus the
        // pending-handoff discriminator. Container is partitioned per endpoint
        // already so this query stays within one partition.
        var queryDefinition = new QueryDefinition(
                @"SELECT TOP 1 * FROM c
                  WHERE c.event.ExternalJobId = @x
                    AND c.event.PendingSubStatus = 'Handoff'
                    AND c.status = @status
                    AND (NOT IS_DEFINED(c.deleted) OR c.deleted != true)")
            .WithParameter("@x", externalJobId)
            .WithParameter("@status", PendingStatus);
        var result = container.GetItemQueryIterator<EventDbo>(queryDefinition);

        if (result.HasMoreResults)
        {
            var eventDbo = await result.ReadNextAsync(cancellationToken);
            if (eventDbo.Any())
            {
                return HydrateResolutionStatus(eventDbo.First());
            }
        }

        return null;
    }

    public async Task<UnresolvedEvent?> GetNextPendingHandoffEvent(string endpointId, IReadOnlyCollection<string>? eventTypeIds)
    {
        var container = await _getEndpointContainer(endpointId);
        // Bound to a single row and filter status/sub-status/event-type server-side so the agent
        // receive long-poll no longer materialises every pending event. Container is partitioned
        // per endpoint, so this stays within one partition. Event types are passed via
        // ARRAY_CONTAINS over a parameter to keep the SQL parameterised.
        var sql = @"SELECT TOP 1 * FROM c
                    WHERE c.event.PendingSubStatus = 'Handoff'
                      AND c.status = @status
                      AND (NOT IS_DEFINED(c.deleted) OR c.deleted != true)";
        var types = eventTypeIds?.Where(t => !string.IsNullOrEmpty(t)).ToArray();
        if (types is { Length: > 0 })
            sql += " AND ARRAY_CONTAINS(@eventTypeIds, c.event.EventTypeId)";
        sql += " ORDER BY c.event.EnqueuedTimeUtc ASC";

        var queryDefinition = new QueryDefinition(sql).WithParameter("@status", PendingStatus);
        if (types is { Length: > 0 })
            queryDefinition = queryDefinition.WithParameter("@eventTypeIds", types);

        var result = container.GetItemQueryIterator<EventDbo>(
            queryDefinition,
            requestOptions: new QueryRequestOptions { MaxItemCount = 1 });

        if (result.HasMoreResults)
        {
            var eventDbo = await result.ReadNextAsync();
            if (eventDbo.Any())
                return eventDbo.First().Event;
        }

        return null;
    }

    public Task<UnresolvedEvent?> GetFailedEvent(string endpointId, string eventId, string sessionId) =>
        GetEvent(endpointId, eventId, sessionId, FailedStatus);

    public Task<UnresolvedEvent?> GetDeferredEvent(string endpointId, string eventId, string sessionId) =>
        GetEvent(endpointId, eventId, sessionId, DeferredStatus);

    public Task<UnresolvedEvent?> GetDeadletteredEvent(string endpointId, string eventId, string sessionId) =>
        GetEvent(endpointId, eventId, sessionId, DLQStatus);

    public Task<UnresolvedEvent?> GetUnsupportedEvent(string endpointId, string eventId, string sessionId) =>
        GetEvent(endpointId, eventId, sessionId, UnsupportedStatus);


    public async Task<UnresolvedEvent?> GetEvent(string endpointId, string eventId)
    {
        var container = await _getEndpointContainer(endpointId);
        // Removed and archived rows are invisible, and the most recently updated session row
        // wins, matching the SQL Server and in-memory providers (conformance-pinned). Completed
        // and Skipped rows are also written with deleted=true (to leave the counts and expire
        // by TTL), so only a deleted row in a non-terminal status counts as removed.
        var queryDefinition = new QueryDefinition(
                "SELECT * FROM c WHERE c.event.EventId = @eventId " +
                "AND (NOT IS_DEFINED(c.deleted) OR c.deleted != true OR c.status IN (@completedStatus, @skippedStatus)) " +
                "ORDER BY c.event.UpdatedAt DESC")
            .WithParameter("@eventId", eventId)
            .WithParameter("@completedStatus", CompletedStatus)
            .WithParameter("@skippedStatus", SkippedStatus);
        // Lookup-by-eventId on a container partitioned by /id (eventId_sessionId), so it
        // necessarily fans across partitions. Cap the fetch to the single document the
        // caller actually reads so RU and payload don't scale with how many session-events
        // share the eventId.
        var result = container.GetItemQueryIterator<EventDbo>(queryDefinition, null,
            new QueryRequestOptions { MaxItemCount = 1 });

        // A filtered cross-partition query can return empty pages before the first match,
        // so keep reading until a page carries a document or the feed is exhausted.
        while (result.HasMoreResults)
        {
            var eventDbo = await result.ReadNextAsync();
            if (eventDbo.Any())
            {
                return HydrateResolutionStatus(eventDbo.First());
            }
        }

        return null;
    }


    private async Task<UnresolvedEvent?> GetEvent(string endpointId, string eventId, string sessionId, string status)
    {
        var container = await _getEndpointContainer(endpointId);
        var id = $"{eventId}_{sessionId}";
        try
        {
            // The doc id (eventId_sessionId) fully identifies the document and the
            // container is partitioned by /id, so a point read (mirroring
            // GetEventById) costs ~1/3 the RU of the equivalent query. The
            // status + not-deleted filters move in-memory with identical semantics.
            var rel = await container.ReadItemAsync<EventDbo>(id, new PartitionKey(id));
            var dbo = rel.Resource;
            if (dbo.Status != status || (dbo.Deleted ?? false))
            {
                return null;
            }

            return HydrateResolutionStatus(dbo);
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public async Task<UnresolvedEvent?> GetEventById(string endpointId, string id)
    {
        var container = await _getEndpointContainer(endpointId);
        try
        {
            var rel = await container.ReadItemAsync<EventDbo>(id, new PartitionKey(id), new ItemRequestOptions() { });
            return IsRemoved(rel.Resource) ? null : HydrateResolutionStatus(rel.Resource);
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

    public async Task<List<UnresolvedEvent>> GetEventsByIds(string endpointId, IEnumerable<string> eventIds)
    {
        var idList = eventIds.ToList();
        if (idList.Count == 0)
            return new List<UnresolvedEvent>();

        var container = await _getEndpointContainer(endpointId);
        const int batchSize = 50;
        var results = new List<UnresolvedEvent>();

        try
        {
            foreach (var batch in idList.Chunk(batchSize))
            {
                var items = batch.Select(id => (id, new PartitionKey(id))).ToList();
                var response = await container.ReadManyItemsAsync<EventDbo>(items);
                if (response.Resource != null)
                    results.AddRange(response.Resource.Select(HydrateResolutionStatus));
            }
            return results;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return results;
        }
    }

    public async Task<IEnumerable<UnresolvedEvent>> GetCompletedEventsOnEndpoint(string endpointId)
    {
        var container = await _getEndpointContainer(endpointId);
        // Parameterized rather than interpolated so the SDK can cache the query plan.
        var queryDefinition = new QueryDefinition("SELECT * FROM c WHERE c.status = @status")
            .WithParameter("@status", CompletedStatus);
        var result = container.GetItemQueryIterator<EventDbo>(queryDefinition, null, new QueryRequestOptions { });
        var unresolvedEvents = new List<UnresolvedEvent>();

        while (result.HasMoreResults)
        {
            var eventDbo = await result.ReadNextAsync();
            foreach (var queryResult in eventDbo)
            {
                unresolvedEvents.Add(HydrateResolutionStatus(queryResult));
            }
        }

        return unresolvedEvents;
    }
}
