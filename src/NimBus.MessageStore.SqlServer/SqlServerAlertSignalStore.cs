using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// SQL Server implementation of <see cref="IAlertSignalStore"/> (Spec 053 Phases 2 and 3): failed
/// attempts, per-minute counts and completions from <c>Messages</c>, blocking events from
/// <c>UnresolvedEvents</c>.
/// </summary>
/// <remarks>
/// Every read is for one endpoint (<c>EndpointId = @EndpointId</c>). The <c>Messages</c> reads seek
/// the endpoint and message type by <c>IX_Messages_EndpointId_MessageType_EnqueuedTimeUtc</c>
/// (migration 0030, which covers them unless an error-text filter needs the JSON), the blocking
/// event read seeks <c>IX_UnresolvedEvents_EndpointId_UpdatedAtUtc</c>, and the count stops at the cap.
/// </remarks>
internal sealed class SqlServerAlertSignalStore : IAlertSignalStore
{
    private readonly SqlServerStoreContext _context;

    public SqlServerAlertSignalStore(SqlServerStoreContext context) => _context = context;

    private string Messages => _context.Table("Messages");

    private string UnresolvedEvents => _context.Table("UnresolvedEvents");

    public async Task<int> CountFailedAttempts(string endpointId, DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? eventTypes, string? errorTextContains, int cap)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);
        ArgumentOutOfRangeException.ThrowIfLessThan(cap, 1);
        if (toUtc <= fromUtc)
            return 0;

        var (apply, where, parameters) = MessageFilter(endpointId, MessageType.ErrorResponse, fromUtc, toUtc, eventTypes, errorTextContains);
        parameters.Add("Cap", cap);

        var sql = $@"
SELECT COUNT(*) FROM (
    SELECT TOP (@Cap) 1 AS One
    FROM {Messages} AS m{apply}
    WHERE {where}
) AS capped";

        await using var conn = await _context.Open();
        return await conn.QuerySingleAsync<int>(sql, parameters, commandTimeout: _context.CommandTimeout);
    }

    public async Task<BlockingEvent?> GetOldestBlockingEvent(string endpointId)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);

        var sql = $@"
SELECT TOP (1) EventId, SessionId, Status, UpdatedAtUtc
FROM {UnresolvedEvents}
WHERE EndpointId = @EndpointId AND Deleted = 0
  AND (Status = 'Failed' OR (Status = 'Pending' AND PendingSubStatus = 'Handoff'))
ORDER BY UpdatedAtUtc ASC, Id ASC";

        await using var conn = await _context.Open();
        var row = await conn.QuerySingleOrDefaultAsync<BlockingRow>(sql, new { EndpointId = endpointId }, commandTimeout: _context.CommandTimeout);
        return row is null
            ? null
            : new BlockingEvent(
                row.EventId,
                row.SessionId ?? string.Empty,
                Enum.Parse<ResolutionStatus>(row.Status),
                // datetime2 reads back as DateTimeKind.Unspecified; the column is UTC.
                DateTime.SpecifyKind(row.UpdatedAtUtc, DateTimeKind.Utc));
    }

    public async Task<bool> HasCompletedMessageSince(string endpointId, DateTime sinceUtc)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);

        var parameters = new DynamicParameters();
        parameters.Add("EndpointId", endpointId);
        parameters.Add("Since", sinceUtc, DbType.DateTime2);

        var sql = $@"
SELECT CASE WHEN EXISTS (
    SELECT 1 FROM {Messages}
    WHERE EndpointId = @EndpointId AND MessageType = 'ResolutionResponse' AND EnqueuedTimeUtc >= @Since
) THEN 1 ELSE 0 END";

        await using var conn = await _context.Open();
        return await conn.QuerySingleAsync<int>(sql, parameters, commandTimeout: _context.CommandTimeout) == 1;
    }

    public async Task<IReadOnlyList<MinuteCount>> CountMessagesPerMinute(string endpointId, MessageType type, DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? eventTypes, string? errorTextContains)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);
        ValidateMinuteBounds(fromUtc, toUtc);

        var (apply, where, parameters) = MessageFilter(endpointId, type, fromUtc, toUtc, eventTypes, errorTextContains);

        // Minutes since @From, a whole minute: DATEDIFF counts the minute boundaries crossed, so a
        // row's offset is the minute it is in. The minute itself is computed here, in UTC.
        var sql = $@"
SELECT DATEDIFF_BIG(minute, @From, m.EnqueuedTimeUtc) AS MinuteOffset, COUNT(*) AS [Count]
FROM {Messages} AS m{apply}
WHERE {where}
GROUP BY DATEDIFF_BIG(minute, @From, m.EnqueuedTimeUtc)
ORDER BY MinuteOffset";

        await using var conn = await _context.Open();
        var rows = await conn.QueryAsync<MinuteRow>(sql, parameters, commandTimeout: _context.CommandTimeout);
        var from = DateTime.SpecifyKind(fromUtc, DateTimeKind.Utc);
        return rows.Select(r => new MinuteCount(from.AddMinutes(r.MinuteOffset), r.Count)).ToList();
    }

    public async Task<FailedAttemptPage> ListFailedAttempts(string endpointId, DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? eventTypes, string? errorTextContains, int limit)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        if (toUtc <= fromUtc)
            return new FailedAttemptPage([], false);

        var (apply, where, parameters) = MessageFilter(endpointId, MessageType.ErrorResponse, fromUtc, toUtc, eventTypes, errorTextContains);
        // One more than the limit tells whether more matched; bigint, so int.MaxValue doesn't overflow.
        parameters.Add("Take", (long)limit + 1, DbType.Int64);

        // Ties broken by the ids in ordinal (BIN2) order, as on the other providers.
        var sql = $@"
SELECT TOP (@Take) m.MessageId, m.EventId, m.EnqueuedTimeUtc
FROM {Messages} AS m{apply}
WHERE {where}
ORDER BY m.EnqueuedTimeUtc DESC, m.MessageId COLLATE Latin1_General_100_BIN2, m.EventId COLLATE Latin1_General_100_BIN2";

        await using var conn = await _context.Open();
        var rows = (await conn.QueryAsync<AttemptRow>(sql, parameters, commandTimeout: _context.CommandTimeout)).ToList();
        var truncated = rows.Count > limit;
        var items = rows
            .Take(limit)
            // datetime2 reads back as DateTimeKind.Unspecified; the column is UTC.
            .Select(r => new FailedAttempt(r.MessageId, r.EventId, DateTime.SpecifyKind(r.EnqueuedTimeUtc, DateTimeKind.Utc)))
            .ToList();
        return new FailedAttemptPage(items, truncated);
    }

    // The endpoint's messages of one type in [fromUtc, toUtc) that pass the event-type and
    // error-text filters: the CROSS APPLY the error text needs (or empty), the WHERE clause over
    // alias m, and its parameters.
    private static (string Apply, string Where, DynamicParameters Parameters) MessageFilter(
        string endpointId, MessageType type, DateTime fromUtc, DateTime toUtc, IReadOnlyCollection<string>? eventTypes, string? errorTextContains)
    {
        var parameters = new DynamicParameters();
        parameters.Add("EndpointId", endpointId);
        parameters.Add("MessageType", type.ToString());
        // Explicit DateTime2: SqlClient otherwise binds DateTime as legacy DATETIME (~3.33 ms rounding).
        parameters.Add("From", fromUtc, DbType.DateTime2);
        parameters.Add("To", toUtc, DbType.DateTime2);

        var apply = string.Empty;
        var where = new List<string>
        {
            "m.EndpointId = @EndpointId",
            "m.MessageType = @MessageType",
            "m.EnqueuedTimeUtc >= @From",
            "m.EnqueuedTimeUtc < @To",
        };

        if (eventTypes is { Count: > 0 })
        {
            where.Add("m.EventTypeId IN @EventTypes");
            parameters.Add("EventTypes", eventTypes.Distinct(StringComparer.Ordinal).ToArray());
        }

        if (!string.IsNullOrEmpty(errorTextContains))
        {
            // OPENJSON ... WITH NVARCHAR(MAX) rather than JSON_VALUE, which returns NULL for values
            // over 4000 characters. DATALENGTH, not NULLIF(x, ''), decides emptiness: '' = '  ' in
            // T-SQL. The explicit collation makes the match case-insensitive whatever the database's.
            apply = @"
CROSS APPLY (SELECT (SELECT TOP (1) j.ErrorText
                     FROM OPENJSON(m.MessageContentJson, '$.ErrorContent')
                          WITH (ErrorText NVARCHAR(MAX) '$.ErrorText') AS j) AS ErrorText) AS e";
            where.Add(@"(CASE WHEN DATALENGTH(e.ErrorText) > 0 THEN e.ErrorText ELSE m.DeadLetterErrorDescription END)
    COLLATE Latin1_General_100_CI_AS LIKE @ErrorText ESCAPE '\'");
            parameters.Add("ErrorText", "%" + EscapeLike(errorTextContains) + "%");
        }

        return (apply, string.Join(" AND ", where), parameters);
    }

    private static void ValidateMinuteBounds(DateTime fromUtc, DateTime toUtc)
    {
        if (fromUtc.Ticks % TimeSpan.TicksPerMinute != 0)
            throw new ArgumentException("The start must be a whole minute.", nameof(fromUtc));
        if (toUtc.Ticks % TimeSpan.TicksPerMinute != 0)
            throw new ArgumentException("The end must be a whole minute.", nameof(toUtc));
        if (toUtc <= fromUtc)
            throw new ArgumentException("The end must be after the start.", nameof(toUtc));
    }

    // Escapes the LIKE wildcards (\ % _ [) for LIKE ... ESCAPE '\'.
    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal)
        .Replace("[", "\\[", StringComparison.Ordinal);

    private sealed class BlockingRow
    {
        public string EventId { get; set; } = null!;
        public string? SessionId { get; set; }
        public string Status { get; set; } = null!;
        public DateTime UpdatedAtUtc { get; set; }
    }

    private sealed class MinuteRow
    {
        public long MinuteOffset { get; set; }
        public int Count { get; set; }
    }

    private sealed class AttemptRow
    {
        public string MessageId { get; set; } = null!;
        public string EventId { get; set; } = null!;
        public DateTime EnqueuedTimeUtc { get; set; }
    }
}
