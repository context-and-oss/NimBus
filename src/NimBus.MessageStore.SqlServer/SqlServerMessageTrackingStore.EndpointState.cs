using NimBus.MessageStore.States;

namespace NimBus.MessageStore.SqlServer;

internal sealed partial class SqlServerMessageTrackingStore
{
    // ───────── State counts ─────────

    public async Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId)
    {
        var sql = $@"
SELECT Status, COUNT(*) AS Count, MIN(UpdatedAtUtc) AS OldestUpdatedAtUtc
FROM {T("UnresolvedEvents")}
WHERE EndpointId = @EndpointId AND Deleted = 0
  AND Status IN ('Pending','Deferred','Failed','DeadLettered','Unsupported')
GROUP BY Status";
        await using var conn = await OpenAsync();
        var rows = (await conn.QueryAsync<(string Status, int Count, DateTime OldestUpdatedAtUtc)>(
            sql, new { EndpointId = endpointId }, commandTimeout: _context.CommandTimeout)).ToList();
        var dict = rows.ToDictionary(r => r.Status, r => r.Count);
        // datetime2 reads back as DateTimeKind.Unspecified; the column is UTC.
        var oldestFailure = rows
            .Where(r => r.Status is "Failed" or "DeadLettered")
            .Select(r => (DateTime?)DateTime.SpecifyKind(r.OldestUpdatedAtUtc, DateTimeKind.Utc))
            .Min();
        return new EndpointStateCount
        {
            EndpointId = endpointId,
            EventTime = DateTime.UtcNow,
            OldestFailureAt = oldestFailure,
            PendingCount = dict.GetValueOrDefault("Pending"),
            DeferredCount = dict.GetValueOrDefault("Deferred"),
            FailedCount = dict.GetValueOrDefault("Failed"),
            DeadletterCount = dict.GetValueOrDefault("DeadLettered"),
            UnsupportedCount = dict.GetValueOrDefault("Unsupported"),
        };
    }

    public async Task<SessionStateCount> DownloadEndpointSessionStateCount(string endpointId, string sessionId)
    {
        var sql = $@"
SELECT EventId, SessionId, Status
FROM {T("UnresolvedEvents")}
WHERE EndpointId = @EndpointId AND SessionId = @SessionId
  AND Status IN ('Pending','Deferred') AND Deleted = 0
ORDER BY UpdatedAtUtc DESC, Id DESC";
        await using var conn = await OpenAsync();
        var rows = (await conn.QueryAsync<(string EventId, string? SessionId, string Status)>(
            sql,
            new { EndpointId = endpointId, SessionId = sessionId },
            commandTimeout: _context.CommandTimeout)).ToList();

        return new SessionStateCount
        {
            SessionId = sessionId,
            PendingEvents = rows.Where(r => r.Status == "Pending").Select(CompositeEventId),
            DeferredEvents = rows.Where(r => r.Status == "Deferred").Select(CompositeEventId),
        };
    }

    public async Task<IEnumerable<SessionStateCount>> DownloadEndpointSessionStateCountBatch(string endpointId, IEnumerable<string> sessionIds)
    {
        var ids = sessionIds.ToArray();
        if (ids.Length == 0) return Array.Empty<SessionStateCount>();
        var sql = $@"
SELECT EventId, SessionId, Status
FROM {T("UnresolvedEvents")}
WHERE EndpointId = @EndpointId AND SessionId IN @Ids
  AND Status IN ('Pending','Deferred') AND Deleted = 0
ORDER BY SessionId, UpdatedAtUtc DESC, Id DESC";
        await using var conn = await OpenAsync();
        var rows = (await conn.QueryAsync<(string EventId, string? SessionId, string Status)>(
            sql,
            new { EndpointId = endpointId, Ids = ids },
            commandTimeout: _context.CommandTimeout)).ToList();

        var grouped = rows
            .Where(r => !string.IsNullOrEmpty(r.SessionId))
            .GroupBy(r => r.SessionId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        return ids.Select(sessionId =>
        {
            grouped.TryGetValue(sessionId, out var sessionRows);
            sessionRows ??= new List<(string EventId, string? SessionId, string Status)>();
            return new SessionStateCount
            {
                SessionId = sessionId,
                PendingEvents = sessionRows.Where(r => r.Status == "Pending").Select(CompositeEventId),
                DeferredEvents = sessionRows.Where(r => r.Status == "Deferred").Select(CompositeEventId),
            };
        }).ToList();
    }

    public async Task<EndpointState?> DownloadEndpointStatePaging(string endpointId, int pageSize, string continuationToken)
    {
        var offset = DecodeOffset(continuationToken);
        var effectivePageSize = pageSize > 0 ? pageSize : 100;

        var sql = $@"
SELECT *
FROM {T("UnresolvedEvents")}
WHERE EndpointId = @EndpointId
  AND Status IN ('Pending','Deferred','Failed','DeadLettered','Unsupported')
  AND Deleted = 0
ORDER BY UpdatedAtUtc DESC, Id DESC
OFFSET @Offset ROWS FETCH NEXT @PageSize ROWS ONLY";

        await using var conn = await OpenAsync();
        var rows = (await conn.QueryAsync(
            sql,
            new { EndpointId = endpointId, Offset = offset, PageSize = effectivePageSize },
            commandTimeout: _context.CommandTimeout)).ToList();

        var events = rows.Select(MapUnresolvedEventRow).ToList();
        return new EndpointState
        {
            EndpointId = endpointId,
            EventTime = DateTime.UtcNow,
            EnrichedUnresolvedEvents = events,
            PendingEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Pending).Select(CompositeEventId).ToList(),
            DeferredEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Deferred).Select(CompositeEventId).ToList(),
            FailedEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Failed).Select(CompositeEventId).ToList(),
            DeadletteredEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.DeadLettered).Select(CompositeEventId).ToList(),
            UnsupportedEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Unsupported).Select(CompositeEventId).ToList(),
            ContinuationToken = events.Count == effectivePageSize ? EncodeOffset(offset + effectivePageSize) : string.Empty,
        };
    }
}
