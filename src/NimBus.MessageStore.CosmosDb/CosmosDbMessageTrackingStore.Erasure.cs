using System.Net;
using Microsoft.Azure.Cosmos;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore;

// Erasure of one event copy (Spec 049). Cosmos DB has no cross-container transactions, so each
// document is written on its own; the shared runner orders the places and maps failures.
internal sealed partial class CosmosDbMessageTrackingStore
{
    public Task<EventErasureResult> EraseEventCopyAsync(EventErasureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EventErasureRunner.RunAsync(request, new ErasureSteps(this, request.EndpointId, request.EventId), cancellationToken);
    }

    // Cosmos DB expires data by TTL, stamped on every write; nothing is deleted by age here.
    public Task<RetentionPurgeResult> PurgeExpiredData(RetentionPeriods periods, DateTime nowUtc, int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(periods);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        return Task.FromResult(RetentionPurgeResult.None);
    }

    private static async Task<List<T>> ReadAllAsync<T>(ICosmosContainerAdapter container, QueryDefinition query, QueryRequestOptions? options, CancellationToken cancellationToken)
    {
        var iterator = container.GetItemQueryIterator<T>(query, null, options);
        var items = new List<T>();
        while (iterator.HasMoreResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.AddRange(await iterator.ReadNextAsync(cancellationToken));
        }

        return items;
    }

    private sealed class ErasureSteps : IEventErasureSteps
    {
        private readonly CosmosDbMessageTrackingStore _store;
        private readonly string _endpointId;
        private readonly string _eventId;

        public ErasureSteps(CosmosDbMessageTrackingStore store, string endpointId, string eventId)
        {
            _store = store;
            _endpointId = endpointId;
            _eventId = eventId;
        }

        public async Task<ErasureSources> ReadSourcesAsync(CancellationToken cancellationToken)
        {
            var rows = await ReadTrackingRowsAsync(cancellationToken);
            var history = await ReadHistoryAsync(cancellationToken);
            var payloads = rows.Select(r => r.Event?.MessageContent?.EventContent?.EventJson)
                .Concat(history.Select(m => m.Message?.MessageContent?.EventContent?.EventJson))
                .OfType<string>()
                .ToList();
            return new ErasureSources(
                rows.Select(r => new ErasureTrackingRow(HydrateResolutionStatus(r), Hidden: r.Deleted == true)).ToList(),
                history.Count,
                payloads);
        }

        // The endpoint container is partitioned by /id ({eventId}_{sessionId}), so finding every
        // session's row needs a cross-partition query. No deleted filter: archived and removed
        // documents keep their payload until their TTL.
        private async Task<List<EventDbo>> ReadTrackingRowsAsync(CancellationToken cancellationToken)
        {
            var container = await _store._getEndpointContainer(_endpointId);
            var query = new QueryDefinition("SELECT * FROM c WHERE c.event.EventId = @eventId")
                .WithParameter("@eventId", _eventId);
            return (await ReadAllAsync<EventDbo>(container, query, null, cancellationToken))
                .Where(r => r.Event is not null)
                .ToList();
        }

        private async Task<List<MessageDocument>> ReadHistoryAsync(CancellationToken cancellationToken)
        {
            var container = await _store._getMessagesContainer();
            var query = new QueryDefinition(
                    "SELECT * FROM c WHERE c.eventId = @eventId AND LOWER(c.endpointId) = LOWER(@endpointId)")
                .WithParameter("@eventId", _eventId)
                .WithParameter("@endpointId", _endpointId);
            return await ReadAllAsync<MessageDocument>(container, query,
                new QueryRequestOptions { PartitionKey = new PartitionKey(_eventId), MaxItemCount = 100 }, cancellationToken);
        }

        public async Task<int> RedactEventAuditsAsync(string marker, CancellationToken cancellationToken)
        {
            var container = await _store._getAuditsContainer();
            var query = new QueryDefinition("SELECT * FROM c WHERE c.eventId = @eventId")
                .WithParameter("@eventId", _eventId);
            var audits = await ReadAllAsync<AuditDocument>(container, query,
                new QueryRequestOptions { PartitionKey = new PartitionKey(_eventId) }, cancellationToken);

            var redacted = 0;
            foreach (var doc in audits.Where(d => BelongsToEndpoint(d) && NeedsRedaction(d)))
            {
                if (await RedactAsync(container, doc.Id, _eventId, NeedsRedaction,
                        audit => audit.Data = ErasureRules.RedactedAuditData(audit.AuditType, audit.Data, marker)))
                {
                    redacted++;
                }
            }

            return redacted;
        }

        private bool BelongsToEndpoint(AuditDocument doc) =>
            string.IsNullOrEmpty(doc.EndpointId) || string.Equals(doc.EndpointId, _endpointId, StringComparison.OrdinalIgnoreCase);

        private static bool NeedsRedaction(AuditDocument doc) =>
            doc.Audit is not null && ErasureRules.AuditNeedsRedaction(doc.Audit.AuditType, doc.Audit.Data, doc.Audit.Comment);

        public async Task<int> RedactComposeAuditsAsync(
            string producerEndpointId,
            string eventTypeId,
            Func<string?, string?, bool> matches,
            string marker,
            CancellationToken cancellationToken)
        {
            // Compose entries are written with an empty event id, so they share that partition.
            // The audit type is stored as its numeric value (see SearchAudits).
            var container = await _store._getAuditsContainer();
            var query = new QueryDefinition(
                    "SELECT * FROM c WHERE c.eventId = @empty AND c.audit.AuditType = @compose " +
                    "AND STRINGEQUALS(c.endpointId, @producer, true) AND STRINGEQUALS(c.eventTypeId, @eventTypeId, true)")
                .WithParameter("@empty", string.Empty)
                .WithParameter("@compose", (int)MessageAuditType.Compose)
                .WithParameter("@producer", producerEndpointId)
                .WithParameter("@eventTypeId", eventTypeId);
            var audits = await ReadAllAsync<AuditDocument>(container, query,
                new QueryRequestOptions { PartitionKey = new PartitionKey(string.Empty) }, cancellationToken);

            bool Matches(AuditDocument doc) => doc.Audit is not null && matches(doc.Audit.Data, doc.EventTypeId);

            var redacted = 0;
            foreach (var doc in audits.Where(Matches))
            {
                if (await RedactAsync(container, doc.Id, string.Empty, Matches, audit => audit.Data = marker))
                    redacted++;
            }

            return redacted;
        }

        // Replaces one audit document under its ETag. A conflicting write is re-read once; a
        // second conflict throws, which the runner reports as WriteFailed. Returns false when the
        // document is gone or no longer needs the change.
        private static async Task<bool> RedactAsync(
            ICosmosContainerAdapter container,
            string id,
            string partition,
            Func<AuditDocument, bool> stillApplies,
            Action<MessageAuditEntity> redact)
        {
            for (var attempt = 0; ; attempt++)
            {
                ItemResponse<AuditDocument> current;
                try
                {
                    current = await container.ReadItemAsync<AuditDocument>(id, new PartitionKey(partition));
                }
                catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
                {
                    return false;
                }

                var doc = current.Resource;
                if (doc is null || !stillApplies(doc))
                    return false;

                redact(doc.Audit);
                doc.Audit.Comment = null;
                try
                {
                    await container.ReplaceItemAsync(doc, id, new PartitionKey(partition),
                        new ItemRequestOptions { IfMatchEtag = current.ETag, EnableContentResponseOnWrite = false });
                    return true;
                }
                catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
                {
                    return false;
                }
                catch (CosmosException e) when (e.StatusCode == HttpStatusCode.PreconditionFailed && attempt == 0)
                {
                    // Re-read once and try again.
                }
            }
        }

        public async Task<int> DeleteHistoryAsync(CancellationToken cancellationToken)
        {
            var container = await _store._getMessagesContainer();
            var deleted = 0;
            foreach (var doc in await ReadHistoryAsync(cancellationToken))
            {
                if (await DeleteAsync<MessageDocument>(container, doc.Id, new PartitionKey(doc.EventId)))
                    deleted++;
            }

            return deleted;
        }

        public async Task<int> DeleteEventReportAsync(CancellationToken cancellationToken)
        {
            var container = await _store._getEventReportsContainer();
            return await DeleteAsync<EventReport>(container, $"{_endpointId}_{_eventId}", new PartitionKey(_endpointId)) ? 1 : 0;
        }

        public async Task<int> DeleteTrackingRowsAsync(CancellationToken cancellationToken)
        {
            var container = await _store._getEndpointContainer(_endpointId);
            var deleted = 0;
            foreach (var row in await ReadTrackingRowsAsync(cancellationToken))
            {
                if (await DeleteAsync<EventDbo>(container, row.Id, new PartitionKey(row.Id)))
                    deleted++;
            }

            return deleted;
        }

        // A document already gone (expired by TTL or deleted concurrently) counts as absent.
        private static async Task<bool> DeleteAsync<T>(ICosmosContainerAdapter container, string id, PartitionKey partitionKey)
        {
            try
            {
                await container.DeleteItemAsync<T>(id, partitionKey);
                return true;
            }
            catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
            {
                return false;
            }
        }
    }
}
