using Dapper;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore.SqlServer;

internal sealed partial class SqlServerMessageTrackingStore
{
    // ───────── Audit trail ─────────

    public async Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null)
    {
        var sql = $@"
INSERT INTO {T("MessageAudits")} (
    EventId, EndpointId, EventTypeId, AuditorName, AuditTimestamp, AuditType, Comment, AccessDenied, Data,
    CloudEventId, CloudEventSource, CloudEventType, CloudEventSubject)
VALUES (
    @EventId, @EndpointId, @EventTypeId, @AuditorName, @AuditTimestamp, @AuditType, @Comment, @AccessDenied, @Data,
    @CloudEventId, @CloudEventSource, @CloudEventType, @CloudEventSubject)";
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(sql, new
        {
            EventId = eventId,
            EndpointId = endpointId,
            EventTypeId = eventTypeId,
            auditEntity.AuditorName,
            auditEntity.AuditTimestamp,
            AuditType = auditEntity.AuditType.ToString(),
            auditEntity.Comment,
            auditEntity.AccessDenied,
            auditEntity.Data,
            auditEntity.CloudEventId,
            auditEntity.CloudEventSource,
            auditEntity.CloudEventType,
            auditEntity.CloudEventSubject,
        }, commandTimeout: _context.CommandTimeout);
    }

    public async Task<IEnumerable<MessageAuditEntity>> GetMessageAudits(string eventId)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("MessageAudits")} WHERE EventId = @EventId ORDER BY AuditTimestamp",
            new { EventId = eventId }, commandTimeout: _context.CommandTimeout);
        return rows.Select(r => new MessageAuditEntity
        {
            AuditorName = r.AuditorName,
            AuditTimestamp = r.AuditTimestamp,
            AuditType = Enum.TryParse((string)r.AuditType, out MessageAuditType at) ? at : MessageAuditType.Comment,
            Comment = r.Comment,
            AccessDenied = r.AccessDenied is bool b ? b : false,
            Data = r.Data,
            EventId = r.EventId,
            EndpointId = r.EndpointId,
            CloudEventId = TryReadString(r, "CloudEventId"),
            CloudEventSource = TryReadString(r, "CloudEventSource"),
            CloudEventType = TryReadString(r, "CloudEventType"),
            CloudEventSubject = TryReadString(r, "CloudEventSubject"),
        }).ToList();
    }

    public async Task<AuditSearchResult> SearchAudits(AuditFilter filter, string? continuationToken, int maxItemCount)
    {
        var pageSize = PaginationLimits.Resolve(maxItemCount);

        var where = new List<string> { "1 = 1" };
        var p = new DynamicParameters();

        // Prefix matching on ID-like fields — see SearchMessages for the
        // cross-provider semantics and collation note.
        if (!string.IsNullOrEmpty(filter.EventId)) { where.Add(@"EventId LIKE @EventId ESCAPE '\'"); p.Add("EventId", LikePrefix(filter.EventId)); }
        if (!string.IsNullOrEmpty(filter.EndpointId))
        {
            // Exact scope (authorization-sensitive callers) vs. the historical
            // prefix match — see AuditFilter.EndpointIdExact. The comparison is
            // pinned to a fixed case-insensitive, accent-sensitive collation so
            // the semantics don't drift with the deployment's database collation
            // (a CS database would miss authorized case variants; a linguistic
            // AI collation could equate identifiers authorization treats as
            // distinct). The explicit COLLATE costs an index seek on this
            // predicate — acceptable for the endpoint-scoped audit page sizes.
            if (filter.EndpointIdExact) { where.Add("EndpointId COLLATE Latin1_General_100_CI_AS = @EndpointId"); p.Add("EndpointId", filter.EndpointId); }
            else { where.Add(@"EndpointId LIKE @EndpointId ESCAPE '\'"); p.Add("EndpointId", LikePrefix(filter.EndpointId)); }
        }
        if (!string.IsNullOrEmpty(filter.AuditorName)) { where.Add(@"AuditorName LIKE @AuditorName ESCAPE '\'"); p.Add("AuditorName", LikePrefix(filter.AuditorName)); }
        if (!string.IsNullOrEmpty(filter.EventTypeId)) { where.Add(@"EventTypeId LIKE @EventTypeId ESCAPE '\'"); p.Add("EventTypeId", LikePrefix(filter.EventTypeId)); }
        if (filter.AuditType.HasValue) { where.Add("AuditType = @AuditType"); p.Add("AuditType", filter.AuditType.Value.ToString()); }
        if (filter.CreatedAtFrom.HasValue) { where.Add("CreatedAtUtc >= @CreatedAtFrom"); p.Add("CreatedAtFrom", filter.CreatedAtFrom.Value); }
        if (filter.CreatedAtTo.HasValue) { where.Add("CreatedAtUtc <= @CreatedAtTo"); p.Add("CreatedAtTo", filter.CreatedAtTo.Value); }

        SqlKeysetCursor.Apply(continuationToken, "CreatedAtUtc", where, p);
        p.Add("PageSize", pageSize);

        var sql = $@"
SELECT Id, EventId, EndpointId, EventTypeId, AuditorName, AuditTimestamp, AuditType, Comment, AccessDenied, Data,
       CloudEventId, CloudEventSource, CloudEventType, CloudEventSubject, CreatedAtUtc
FROM {T("MessageAudits")}
WHERE {string.Join(" AND ", where)}
ORDER BY CreatedAtUtc DESC, Id DESC
OFFSET 0 ROWS FETCH NEXT @PageSize ROWS ONLY";

        await using var conn = await OpenAsync();
        var rows = (await conn.QueryAsync(sql, p, commandTimeout: _context.CommandTimeout)).ToList();

        var items = rows.Select(r => new AuditSearchItem
        {
            EventId = r.EventId,
            EndpointId = r.EndpointId,
            EventTypeId = r.EventTypeId,
            CreatedAt = r.CreatedAtUtc,
            Audit = new MessageAuditEntity
            {
                AuditorName = r.AuditorName,
                AuditTimestamp = r.AuditTimestamp,
                AuditType = Enum.TryParse((string)r.AuditType, out MessageAuditType at) ? at : MessageAuditType.Comment,
                Comment = r.Comment,
                AccessDenied = r.AccessDenied is bool b ? b : false,
                Data = r.Data,
                EventId = r.EventId,
                EndpointId = r.EndpointId,
                CloudEventId = TryReadString(r, "CloudEventId"),
                CloudEventSource = TryReadString(r, "CloudEventSource"),
                CloudEventType = TryReadString(r, "CloudEventType"),
                CloudEventSubject = TryReadString(r, "CloudEventSubject"),
            },
        }).ToList();

        return new AuditSearchResult
        {
            Audits = items,
            ContinuationToken = items.Count == pageSize ? KeysetToken((object)rows[^1], "CreatedAtUtc") : null,
        };
    }

    public async Task<IReadOnlyDictionary<string, int>> GetResubmitCounts(string endpointId, IReadOnlyCollection<string> eventIds)
    {
        var ids = (eventIds ?? Array.Empty<string>())
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct()
            .ToList();

        var result = new Dictionary<string, int>();
        if (string.IsNullOrEmpty(endpointId) || ids.Count == 0)
            return result;

        // AuditType is persisted as the enum *name* (see StoreMessageAudit), so
        // match on the string names. AccessDenied = 0 excludes denied resubmit
        // attempts (the WebApp logs those audit rows before returning
        // Unauthorized) — they never resubmitted.
        var sql = $@"
SELECT EventId, COUNT(*) AS Cnt
FROM {T("MessageAudits")}
WHERE EndpointId = @EndpointId
  AND AuditType IN @AuditTypes
  AND EventId IN @EventIds
  AND AccessDenied = 0
GROUP BY EventId";

        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(sql, new
        {
            EndpointId = endpointId,
            AuditTypes = new[]
            {
                nameof(MessageAuditType.Resubmit),
                nameof(MessageAuditType.ResubmitWithChanges),
            },
            EventIds = ids,
        }, commandTimeout: _context.CommandTimeout);

        foreach (var row in rows)
        {
            string eventId = (string)row.EventId;
            if (!string.IsNullOrEmpty(eventId))
                result[eventId] = Convert.ToInt32(row.Cnt);
        }

        return result;
    }
}
