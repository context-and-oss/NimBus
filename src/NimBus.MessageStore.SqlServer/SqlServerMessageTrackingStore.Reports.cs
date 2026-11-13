using NimBus.MessageStore.States;

namespace NimBus.MessageStore.SqlServer;

internal sealed partial class SqlServerMessageTrackingStore
{
    public async Task SetEventReport(string endpointId, string eventId, bool isReported, string? reportedBy, string? ticketId)
    {
        if (string.IsNullOrEmpty(endpointId)) throw new ArgumentNullException(nameof(endpointId));
        if (string.IsNullOrEmpty(eventId)) throw new ArgumentNullException(nameof(eventId));

        // HOLDLOCK (serializable range lock) closes the classic MERGE upsert
        // race: without it two concurrent first writes for the same key can both
        // miss the MATCHED branch and collide on the primary key.
        var sql = $@"
MERGE {T("EventReports")} WITH (HOLDLOCK) AS target
USING (SELECT @EndpointId AS EndpointId, @EventId AS EventId) AS src
ON target.EndpointId = src.EndpointId AND target.EventId = src.EventId
WHEN MATCHED THEN
    UPDATE SET IsReported = @IsReported, ReportedBy = @ReportedBy, ReportedAtUtc = @ReportedAtUtc, TicketId = @TicketId
WHEN NOT MATCHED THEN
    INSERT (EndpointId, EventId, IsReported, ReportedBy, ReportedAtUtc, TicketId)
    VALUES (@EndpointId, @EventId, @IsReported, @ReportedBy, @ReportedAtUtc, @TicketId);";

        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(sql, new
        {
            EndpointId = endpointId,
            EventId = eventId,
            IsReported = isReported,
            ReportedBy = reportedBy,
            ReportedAtUtc = DateTime.UtcNow,
            // Clearing the marker drops the ticket reference too.
            TicketId = isReported ? ticketId : null,
        }, commandTimeout: _context.CommandTimeout);
    }

    public async Task<IReadOnlyList<EventReport>> FindEventReports(string endpointId, string? ticketId, int limit)
    {
        if (string.IsNullOrEmpty(endpointId) || limit <= 0)
            return [];

        var sql = $@"
SELECT TOP (@Limit) EndpointId, EventId, IsReported, ReportedBy, ReportedAtUtc, TicketId
FROM {T("EventReports")}
WHERE EndpointId = @EndpointId AND IsReported = 1 AND (@TicketId IS NULL OR TicketId = @TicketId)
ORDER BY ReportedAtUtc DESC, EventId";

        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync<EventReport>(sql,
            new { EndpointId = endpointId, TicketId = ticketId, Limit = limit },
            commandTimeout: _context.CommandTimeout);
        return rows.Select(r =>
        {
            // datetime2 reads back as DateTimeKind.Unspecified; the column holds UTC.
            if (r.ReportedAtUtc is { } at) r.ReportedAtUtc = DateTime.SpecifyKind(at, DateTimeKind.Utc);
            return r;
        }).ToList();
    }

    public async Task<IReadOnlyDictionary<string, EventReport>> GetEventReports(string endpointId, IReadOnlyCollection<string> eventIds)
    {
        var ids = (eventIds ?? Array.Empty<string>())
            .Where(e => !string.IsNullOrEmpty(e))
            .Distinct()
            .ToList();

        var result = new Dictionary<string, EventReport>();
        if (string.IsNullOrEmpty(endpointId) || ids.Count == 0)
            return result;

        var sql = $@"
SELECT EndpointId, EventId, IsReported, ReportedBy, ReportedAtUtc, TicketId
FROM {T("EventReports")}
WHERE EndpointId = @EndpointId AND EventId IN @EventIds";

        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync<EventReport>(sql,
            new { EndpointId = endpointId, EventIds = ids },
            commandTimeout: _context.CommandTimeout);
        foreach (var r in rows)
        {
            if (!string.IsNullOrEmpty(r.EventId))
                result[r.EventId] = r;
        }

        return result;
    }
}
