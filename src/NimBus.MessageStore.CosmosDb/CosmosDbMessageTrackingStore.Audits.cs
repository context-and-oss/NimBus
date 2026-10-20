using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore;

internal sealed partial class CosmosDbMessageTrackingStore
{
    public async Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null)
    {
        var container = await _getAuditsContainer();
        var doc = new AuditDocument
        {
            Id = Guid.NewGuid().ToString(),
            EventId = eventId,
            EndpointId = endpointId,
            EventTypeId = eventTypeId,
            Audit = auditEntity,
            CreatedAt = DateTime.UtcNow,
            TimeToLive = 60 * 60 * 24 * 365 // 1-year TTL
        };

        try
        {
            await container.UpsertItemAsync(doc, new PartitionKey(doc.EventId), SuppressContentOnWrite);
        }
        catch (CosmosException e)
        {
            _logger?.LogError(e, "COSMOS STORE-AUDIT-ERROR: EventId: {EventId}", eventId);
            throw;
        }
    }

    public async Task<IEnumerable<MessageAuditEntity>> GetMessageAudits(string eventId)
    {
        var container = await _getAuditsContainer();
        var query = new QueryDefinition("SELECT * FROM c WHERE c.eventId = @eventId ORDER BY c.createdAt DESC")
            .WithParameter("@eventId", eventId);
        // Bound page size so heavily-audited events stream in pages rather than
        // one huge response. The loop still drains every match.
        var result = container.GetItemQueryIterator<AuditDocument>(query, null,
            new QueryRequestOptions { MaxItemCount = 1000 });
        var audits = new List<MessageAuditEntity>();

        while (result.HasMoreResults)
        {
            var feed = await result.ReadNextAsync();
            foreach (var doc in feed)
            {
                audits.Add(doc.Audit);
            }
        }

        return audits;
    }

    public async Task<AuditSearchResult> SearchAudits(AuditFilter filter, string? continuationToken, int maxItemCount)
    {
        var container = await _getAuditsContainer();
        var conditions = new List<string>();
        var parameters = new Dictionary<string, object>();
        var paramIndex = 0;

        string NextParam() => $"@p{paramIndex++}";

        // Case-insensitive PREFIX matching — index-served, unlike CONTAINS/LOWER
        // which force a full container scan. See SearchMessages.
        if (!string.IsNullOrEmpty(filter.EventId))
        {
            var p = NextParam();
            conditions.Add($"STARTSWITH(c.eventId, {p}, true)");
            parameters[p] = filter.EventId;
        }

        if (!string.IsNullOrEmpty(filter.EndpointId))
        {
            var p = NextParam();
            // Exact scope (authorization-sensitive callers) vs. the historical
            // prefix match — see AuditFilter.EndpointIdExact. STRINGEQUALS with
            // ignoreCase matches the other providers' case-insensitive equality.
            conditions.Add(filter.EndpointIdExact
                ? $"STRINGEQUALS(c.endpointId, {p}, true)"
                : $"STARTSWITH(c.endpointId, {p}, true)");
            parameters[p] = filter.EndpointId;
        }

        if (!string.IsNullOrEmpty(filter.AuditorName))
        {
            var p = NextParam();
            // MessageAuditEntity serializes with PascalCase property names (no
            // [JsonProperty] attributes) - the path is c.audit.AuditorName; the
            // previous lowercase path never matched anything on Cosmos.
            conditions.Add($"STARTSWITH(c.audit.AuditorName, {p}, true)");
            parameters[p] = filter.AuditorName;
        }

        if (!string.IsNullOrEmpty(filter.EventTypeId))
        {
            var p = NextParam();
            conditions.Add($"STARTSWITH(c.eventTypeId, {p}, true)");
            parameters[p] = filter.EventTypeId;
        }

        if (filter.AuditType != null)
        {
            var p = NextParam();
            // PascalCase path (see AuditorName above); the enum serializes as its
            // NUMERIC value (no StringEnumConverter on MessageAuditType), so the
            // comparison must be numeric too - the previous string compare never
            // matched anything on Cosmos.
            conditions.Add($"c.audit.AuditType = {p}");
            parameters[p] = (int)filter.AuditType.Value;
        }

        // Widened to whole seconds: Cosmos compares datetimes as strings (see CosmosDateBounds).
        if (filter.CreatedAtFrom != null)
        {
            var p = NextParam();
            conditions.Add($"c.createdAt >= {p}");
            parameters[p] = CosmosDateBounds.WidenFrom(filter.CreatedAtFrom.Value);
        }

        if (filter.CreatedAtTo != null)
        {
            var p = NextParam();
            conditions.Add($"c.createdAt <= {p}");
            parameters[p] = CosmosDateBounds.WidenTo(filter.CreatedAtTo.Value);
        }

        var sql = "SELECT * FROM c";
        if (conditions.Any())
            sql += " WHERE " + string.Join(" AND ", conditions);
        sql += " ORDER BY c.createdAt DESC";

        var queryDef = new QueryDefinition(sql);
        foreach (var kvp in parameters)
            queryDef = queryDef.WithParameter(kvp.Key, kvp.Value);

        var requestOptions = new QueryRequestOptions { MaxItemCount = PaginationLimits.Resolve(maxItemCount) };
        var result = container.GetItemQueryIterator<AuditDocument>(
            queryDef,
            string.IsNullOrEmpty(continuationToken) ? null : continuationToken,
            requestOptions);

        var audits = new List<AuditSearchItem>();
        string? token = null;

        // One page per call, applying the exact date bounds; a page holding only rows just
        // outside them would come back empty, so read on past it.
        while (result.HasMoreResults)
        {
            var feed = await result.ReadNextAsync();
            token = feed.ContinuationToken;
            foreach (var doc in feed)
            {
                if ((filter.CreatedAtFrom != null && doc.CreatedAt < filter.CreatedAtFrom) ||
                    (filter.CreatedAtTo != null && doc.CreatedAt > filter.CreatedAtTo))
                    continue;

                audits.Add(new AuditSearchItem
                {
                    EventId = doc.EventId,
                    EndpointId = doc.EndpointId,
                    EventTypeId = doc.EventTypeId,
                    Audit = doc.Audit,
                    CreatedAt = doc.CreatedAt
                });
            }

            if (audits.Count > 0 || feed.Count == 0)
                break;
        }

        return new AuditSearchResult { Audits = audits, ContinuationToken = token };
    }

    public async Task<IReadOnlyDictionary<string, int>> GetResubmitCounts(string endpointId, IReadOnlyCollection<string> eventIds)
    {
        var ids = (eventIds ?? Array.Empty<string>())
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct()
            .ToList();

        var counts = new Dictionary<string, int>();
        if (string.IsNullOrEmpty(endpointId) || ids.Count == 0)
            return counts;

        // Document-level ids are camelCase ([JsonProperty] on AuditDocument);
        // the nested audit entity serializes with PascalCase names and a NUMERIC
        // AuditType (no attributes / no StringEnumConverter) — see SearchAudits.
        // AccessDenied=false-or-undefined excludes denied resubmit attempts while
        // keeping legacy documents (written before the field existed) counted.
        // The audits container is partitioned by /eventId, so this GROUP BY is
        // cross-partition — but bounded by the explicit event-id list, it stays a
        // single cheap fan-out instead of one round-trip per row on the page.
        var resubmitTypes = new[]
        {
            (int)MessageAuditType.Resubmit,
            (int)MessageAuditType.ResubmitWithChanges,
        };

        var query = new QueryDefinition(
                "SELECT c.eventId AS EventId, COUNT(1) AS Count FROM c " +
                "WHERE c.endpointId = @endpointId " +
                "AND ARRAY_CONTAINS(@types, c.audit.AuditType) " +
                "AND ARRAY_CONTAINS(@eventIds, c.eventId) " +
                "AND (NOT IS_DEFINED(c.audit.AccessDenied) OR c.audit.AccessDenied = false) " +
                "GROUP BY c.eventId")
            .WithParameter("@endpointId", endpointId)
            .WithParameter("@types", resubmitTypes)
            .WithParameter("@eventIds", ids);

        var container = await _getAuditsContainer();
        var iterator = container.GetItemQueryIterator<AuditCountRow>(query);
        while (iterator.HasMoreResults)
        {
            foreach (var row in await iterator.ReadNextAsync())
            {
                if (!string.IsNullOrEmpty(row.EventId))
                    counts[row.EventId] = row.Count;
            }
        }

        return counts;
    }

    private sealed class AuditCountRow
    {
        public string? EventId { get; set; }

        public int Count { get; set; }
    }
}
