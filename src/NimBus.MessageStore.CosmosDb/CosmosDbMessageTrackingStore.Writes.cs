using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using NimBus.MessageStore.Abstractions;
using System.Net;

namespace NimBus.MessageStore;

internal sealed partial class CosmosDbMessageTrackingStore
{
    public Task<bool> UploadDeferredMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content) =>
        StaleWriteGuard.IsGuarded(ResolutionStatus.Deferred, content.MessageType)
            ? UploadGuarded(eventId, sessionId, endpointId, content, DeferredStatus)
            : UploadMessage(eventId, sessionId, endpointId, content, DeferredStatus);

    public Task<bool> UploadFailedMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content) =>
        UploadMessage(eventId, sessionId, endpointId, content, FailedStatus);

    public Task<bool> UploadPendingMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content) =>
        StaleWriteGuard.IsGuarded(ResolutionStatus.Pending, content.MessageType)
            ? UploadGuarded(eventId, sessionId, endpointId, content, PendingStatus)
            : UploadMessage(eventId, sessionId, endpointId, content, PendingStatus);

    public Task<bool> UploadDeadletteredMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent contet) =>
        UploadMessage(eventId, sessionId, endpointId, contet, DLQStatus);

    public Task<bool> UploadUnsupportedMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content) =>
        UploadMessage(eventId, sessionId, endpointId, content, UnsupportedStatus);

    public Task<bool> UploadSkippedMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content) =>
        UploadCompletedMessage(eventId, sessionId, endpointId, content, SkippedStatus);

    public Task<bool> UploadCompletedMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content) =>
        UploadCompletedMessage(eventId, sessionId, endpointId, content, CompletedStatus);

    public async Task<bool> TrySkipDeferredMessage(string eventId, string sessionId, string endpointId,
        string? expectedLastMessageId, DateTime expectedUpdatedAt)
    {
        var container = await _getEndpointContainer(endpointId);
        var id = $"{eventId}_{sessionId}";
        try
        {
            var current = await container.ReadItemAsync<EventDbo>(id, new PartitionKey(id));
            if (current.Resource.Status != DeferredStatus || current.Resource.Deleted == true
                || current.Resource.Event?.UpdatedAt != expectedUpdatedAt
                || !string.Equals(current.Resource.Event?.LastMessageId, expectedLastMessageId, StringComparison.Ordinal))
            {
                return false;
            }

            current.Resource.Event!.ResolutionStatus = ResolutionStatus.Skipped;
            current.Resource.Event.UpdatedAt = DateTime.UtcNow;
            var replacement = CreateCompletedDbo(eventId, sessionId, current.Resource.Event, SkippedStatus);
            await container.ReplaceItemAsync(replacement, id, new PartitionKey(id),
                new ItemRequestOptions { IfMatchEtag = current.ETag, EnableContentResponseOnWrite = false });
            return true;
        }
        catch (CosmosException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    public Task<bool> TryArchiveUnresolvedEvent(string eventId, string sessionId, string endpointId,
        ResolutionStatus expectedStatus, string? expectedLastMessageId, DateTime expectedUpdatedAt) =>
        TrySetArchived(eventId, sessionId, endpointId, expectedStatus, expectedLastMessageId, expectedUpdatedAt, archive: true);

    public Task<bool> TryRestoreArchivedEvent(string eventId, string sessionId, string endpointId,
        ResolutionStatus expectedStatus, string? expectedLastMessageId, DateTime expectedUpdatedAt) =>
        TrySetArchived(eventId, sessionId, endpointId, expectedStatus, expectedLastMessageId, expectedUpdatedAt, archive: false);

    /// <summary>
    /// Flips the archive flag of one row under its ETag, after checking the inspected version.
    /// Archiving sets the same 30-day TTL as <see cref="ArchiveFailedEvent"/>; restoring puts back
    /// the TTL that live unresolved rows are written with.
    /// </summary>
    private async Task<bool> TrySetArchived(string eventId, string sessionId, string endpointId,
        ResolutionStatus expectedStatus, string? expectedLastMessageId, DateTime expectedUpdatedAt, bool archive)
    {
        var container = await _getEndpointContainer(endpointId);
        var id = $"{eventId}_{sessionId}";
        try
        {
            var current = await container.ReadItemAsync<EventDbo>(id, new PartitionKey(id));
            var row = current.Resource;
            if ((row.Deleted == true) == archive
                ||!string.Equals(row.Status, expectedStatus.ToString(), StringComparison.Ordinal)
                || row.Event?.UpdatedAt != expectedUpdatedAt
                || !string.Equals(row.Event?.LastMessageId, expectedLastMessageId, StringComparison.Ordinal))
            {
                return false;
            }

            row.Deleted = archive;
            row.TimeToLive = archive ? 60 * 60 * 24 * 30 : _unresolvedTtlSeconds;
            await container.ReplaceItemAsync(row, id, new PartitionKey(id),
                new ItemRequestOptions { IfMatchEtag = current.ETag, EnableContentResponseOnWrite = false });
            return true;
        }
        catch (CosmosException exception) when (exception.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    public async Task<bool> TryCompletePendingMessage(
        string eventId,
        string sessionId,
        string endpointId,
        string? expectedLastMessageId,
        UnresolvedEvent content)
    {
        var container = await _getEndpointContainer(endpointId);
        var id = $"{eventId}_{sessionId}";
        ItemResponse<EventDbo> current;
        try
        {
            current = await container.ReadItemAsync<EventDbo>(id, new PartitionKey(id));
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            return false;
        }

        if (!string.Equals(current.Resource.Status, PendingStatus, StringComparison.Ordinal)
            || current.Resource.Deleted == true
            || !string.Equals(current.Resource.Event?.LastMessageId, expectedLastMessageId, StringComparison.Ordinal))
        {
            return false;
        }

        var completed = CreateCompletedDbo(eventId, sessionId, content, CompletedStatus);
        try
        {
            await container.UpsertItemAsync(
                completed,
                new PartitionKey(id),
                new ItemRequestOptions
                {
                    IfMatchEtag = current.ETag,
                    EnableContentResponseOnWrite = false,
                });
            return true;
        }
        catch (CosmosException exception) when (exception.StatusCode == HttpStatusCode.PreconditionFailed)
        {
            _logger?.LogInformation(
                "COSMOS COMPLETE-IF-PENDING refused for {EventId}/{SessionId}: the row changed after it was read",
                eventId,
                sessionId);
            return false;
        }
    }

    private async Task<bool> UploadCompletedMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content, string status)
    {
        var container = await _getEndpointContainer(endpointId);
        var eventDbo = CreateCompletedDbo(eventId, sessionId, content, status);

        try
        {
            var response = await container.UpsertItemAsync(eventDbo, new PartitionKey(eventDbo.Id), SuppressContentOnWrite);
            _logger?.LogTrace(
                "COSMOS UPSERT-RESPONSE: EventId: {EventId}, SessionId: {SessionId}, HttpStatusCode: {StatusCode}, Status: {Status}", eventId, sessionId, response.StatusCode, CompletedStatus);
            return true;
        }
        catch (CosmosException e)
        {
            _logger?.LogError(e,
                "COSMOS UPSERT-ERROR: EventId: {EventId}, SessionId: {SessionId}, Status: {Status}", eventId, sessionId, CompletedStatus);
            throw;
        }
    }

    private static EventDbo CreateCompletedDbo(
        string eventId,
        string sessionId,
        UnresolvedEvent content,
        string status) => new()
    {
        Id = $"{eventId}_{sessionId}",
        Event = content,
        SessionId = sessionId,
        Status = status,
        EventType = content.EventTypeId,
        Deleted = true,
        TimeToLive = 60 * 60 * 24 * 30,
    };

    public async Task<bool> RemoveMessage(string eventId, string sessionId, string endpointId)
    {
        var container = await _getEndpointContainer(endpointId);
        var id = $"{eventId}_{sessionId}";

        try
        {
            // The doc id (eventId_sessionId) fully identifies the document and the
            // container is partitioned by /id, so soft-delete it with a single patch
            // (mirrors ArchiveFailedEvent) instead of a query + full-document upsert
            // that would drag the heavy EventJson payload across the wire 3× over 2
            // round-trips.
            var response = await container.PatchItemAsync<EventDbo>(id, new PartitionKey(id), new[]
            {
                PatchOperation.Set("/deleted", true),
                PatchOperation.Set("/ttl", 60) // 1 Minute
            });
            _logger?.LogTrace(
                "COSMOS REMOVE-MESSAGE: EventId: {EventId}, SessionId: {SessionId}, HttpStatusCode: {StatusCode}", eventId, sessionId, response.StatusCode);
            return true;
        }
        catch (CosmosException e)
        {
            _logger?.LogError(e,
                "COSMOS REMOVE-MESSAGE: EventId: {EventId}, SessionId: {SessionId}, HttpStatusCode: {StatusCode}", eventId, sessionId, e.StatusCode);

            if (e.StatusCode == HttpStatusCode.NotFound)
            {
                // Missing document — nothing to remove (matches the previous
                // empty-query-result path returning false).
                return false;
            }

            throw;
        }
    }

    public async Task<bool> PurgeMessages(string endpointId, string sessionId)
    {
        try
        {
            var container = await _getEndpointContainer(endpointId);
            // Only the id is needed for the deletes — don't pull whole EventDbo
            // documents (the EventJson payload dominates the response size).
            var queryDefinition = new QueryDefinition("SELECT c.id FROM c WHERE c.sessionId = @sessionId")
                .WithParameter("@sessionId", sessionId);
            var result = container.GetItemQueryIterator<IdProjection>(queryDefinition);

            _logger?.LogInformation(
                "COSMOS PURGE: Deleted all messages on endpoint {EndpointId} in session {SessionId}", endpointId, sessionId);
            // The container is partitioned by /id, so each document lives in its own
            // logical partition and a TransactionalBatch (single-partition only) can't
            // apply. The deletes are independent — run them concurrently (bounded)
            // instead of one round-trip at a time.
            var deleteOptions = new ParallelOptions { MaxDegreeOfParallelism = 8 };
            while (result.HasMoreResults)
            {
                var page = await result.ReadNextAsync();
                await Parallel.ForEachAsync(page, deleteOptions, async (item, _) =>
                {
                    await container.DeleteItemAsync<EventDbo>(item.Id, new PartitionKey(item.Id));
                });
            }

            return true;
        }
        catch (Exception e)
        {
            _logger?.LogError(e,
                "COSMOS PURGE: Couldn't delete all messages on endpoint {EndpointId} in session {SessionId}", endpointId, sessionId);
            return false;
        }
    }

    public async Task<bool> PurgeMessages(string endpointId)
    {
        try
        {
            var container = await _getEndpointContainer(endpointId);

            await container.DeleteContainerAsync();
            // Drop the cached handle so the next access re-runs "ensure exists"
            // and recreates the now-deleted container instead of reusing a stale
            // handle that would only throw NotFound.
            _removeEndpointContainerCache(endpointId);
            _logger?.LogInformation("COSMOS PURGE: Deleted all messages on endpoint {EndpointId}", endpointId);

            return true;
        }
        catch (Exception e)
        {
            _logger?.LogError(e, "COSMOS PURGE: Couldn't delete all messages on endpoint {EndpointId}", endpointId);
            return false;
        }
    }

    public async Task ArchiveFailedEvent(string eventId, string sessionId, string endpointId)
    {
        var container = await _getEndpointContainer(endpointId);
        var id = $"{eventId}_{sessionId}";
        try
        {
            await container.PatchItemAsync<EventDbo>(id, new PartitionKey(id), new[]
            {
                PatchOperation.Set("/deleted", true),
                PatchOperation.Set("/ttl", 60 * 60 * 24 * 30) // 30-day TTL
            });
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            _logger?.LogTrace("COSMOS ARCHIVE-FAILED: Event not found. EventId: {EventId}, SessionId: {SessionId}, EndpointId: {EndpointId}", eventId, sessionId, endpointId);
        }
    }

    /// <summary>
    /// Non-terminal write that <see cref="StaleWriteGuard"/> governs (Spec 030): read the row,
    /// evaluate the rule against it, and replace it only under its ETag so a concurrent writer
    /// cannot slip between the read and the write.
    ///
    /// <para>Returns <see langword="true"/> when the row was created or replaced,
    /// <see langword="false"/> when the rule refused a stale copy. A lost compare-and-swap is a
    /// transient failure, not a refusal: it throws
    /// <see cref="StorageProviderTransientException"/> so the Resolver reschedules and
    /// re-evaluates idempotently.</para>
    ///
    /// <para>404, 409 and 412 are not in <c>CosmosExceptionTranslation.IsTransient</c>, so they
    /// are handled here — a raw <see cref="CosmosException"/> would dead-letter the message in
    /// the Resolver. A 429 still translates to the throttle path as before.</para>
    ///
    /// <para><strong>Cost.</strong> Every message type the Resolver projects as Pending or
    /// Deferred is guarded, so this is the path for all of its non-terminal writes: one extra
    /// point read each, and two round-trips (404 + create) for the first projection of an event.
    /// The read returns the whole document, <c>MessageContent</c> included. Terminal writes are
    /// unguarded and unaffected. Narrowing the read to the fields the rule needs would require
    /// new adapter overloads (Spec 030 §11).</para>
    /// </summary>
    private async Task<bool> UploadGuarded(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content, string status)
    {
        var container = await _getEndpointContainer(endpointId);
        var eventDbo = new EventDbo
        {
            Id = $"{eventId}_{sessionId}",
            Event = content,
            SessionId = sessionId,
            Status = status,
            EventType = content.EventTypeId,
            Deleted = false,
            TimeToLive = _unresolvedTtlSeconds
        };
        var partitionKey = new PartitionKey(eventDbo.Id);
        var incomingStatus = Enum.Parse<ResolutionStatus>(status);

        for (var attempt = 0; attempt < 3; attempt++)
        {
            ItemResponse<EventDbo> current;
            try
            {
                // Point read by id: unlike the status-scoped lookups it also sees soft-deleted
                // (deleted = true) documents, which is exactly what a Completed row looks like.
                current = await container.ReadItemAsync<EventDbo>(eventDbo.Id, partitionKey);
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                try
                {
                    // First projection of this event. The adapter has no CreateItemAsync overload
                    // taking ItemRequestOptions, so this one write echoes the document body back
                    // (bandwidth only, RU unchanged).
                    await container.CreateItemAsync(eventDbo, partitionKey);
                    return true;
                }
                catch (CosmosException conflict) when (conflict.StatusCode == HttpStatusCode.Conflict)
                {
                    // Created concurrently between the read and the create: re-read and re-evaluate.
                    continue;
                }
            }

            var row = HydrateResolutionStatus(current.Resource);
            if (row is null || !Enum.TryParse<ResolutionStatus>(current.Resource?.Status, out _))
            {
                // Fail closed: never treat an unreadable row as still in flight. This is a
                // refusal, not a failure, so the message is completed and the projection is
                // dropped — deliberate (Spec 030 §5.1), because an unreadable row is the one
                // state where applying the write could silently undo a recorded outcome. The
                // warning plus the Resolver's Comment audit are the operator's signal.
                _logger?.LogWarning(
                    "COSMOS UPSERT-REFUSED: row {Id} has unparseable status {Status}", eventDbo.Id, current.Resource?.Status);
                return false;
            }

            if (!StaleWriteGuard.Allows(incomingStatus, content, row))
            {
                _logger?.LogInformation(
                    "COSMOS UPSERT-REFUSED: stale {MessageType} {MessageId}; row {Id} already {Status} written by {RowMessageType} {RowMessageId}",
                    content.MessageType, content.LastMessageId, eventDbo.Id, current.Resource.Status, row.MessageType, row.LastMessageId);
                return false;
            }

            try
            {
                await container.UpsertItemAsync(eventDbo, partitionKey,
                    new ItemRequestOptions { IfMatchEtag = current.ETag, EnableContentResponseOnWrite = false });
                return true;
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.PreconditionFailed)
            {
                // The row changed between the read and the replace (a WebApp patch, a lock-loss
                // twin): re-read and re-evaluate against what is there now.
            }
        }

        throw new StorageProviderTransientException($"Audit row {eventDbo.Id} changed concurrently three times.");
    }

    private async Task<bool> UploadMessage(string eventId, string sessionId, string endpointId,
        UnresolvedEvent content, string status)
    {
        var container = await _getEndpointContainer(endpointId);

        var eventDbo = new EventDbo
        {
            Id = $"{eventId}_{sessionId}",
            Event = content,
            SessionId = sessionId,
            Status = status,
            EventType = content.EventTypeId,
            Deleted = false,
            // Configurable retention (CosmosDbMessageStoreOptions.UnresolvedRetentionDays);
            // -1 is "TTL disabled" and remains the default. UpsertItemAsync writes the whole
            // document, so every retry re-stamps this and the window slides forward.
            TimeToLive = _unresolvedTtlSeconds
        };

        try
        {
            var response = await container.UpsertItemAsync(eventDbo, new PartitionKey(eventDbo.Id), SuppressContentOnWrite);
            _logger?.LogTrace(
                "COSMOS UPSERT-RESPONSE: EventId: {EventId}, SessionId: {SessionId}, HttpStatusCode: {StatusCode}, Status: {Status}", eventId, sessionId, response.StatusCode, status);
            return true;
        }
        catch (CosmosException e)
        {
            _logger?.LogError(e,
                "COSMOS UPSERT-ERROR: EventId: {EventId}, SessionId: {SessionId}, Status: {Status}, HttpStatusCode: {StatusCode}", eventId, sessionId, status, e.StatusCode);
            throw;
        }
    }
}
