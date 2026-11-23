using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// SQL Server implementation of <see cref="IAlertSignalStore"/> (Spec 053 Phase 2): failed attempts
/// and completions from <c>Messages</c>, blocking events from <c>UnresolvedEvents</c>.
/// </summary>
/// <remarks>
/// Every read is for one endpoint (<c>EndpointId = @EndpointId</c>), so it seeks the endpoint's
/// rows by <c>IX_Messages_EndpointId_EnqueuedTimeUtc</c> or
/// <c>IX_UnresolvedEvents_EndpointId_UpdatedAtUtc</c>, and the count stops at the cap.
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

        var parameters = new DynamicParameters();
        parameters.Add("EndpointId", endpointId);
        parameters.Add("Cap", cap);
        // Explicit DateTime2: SqlClient otherwise binds DateTime as legacy DATETIME (~3.33 ms rounding).
        parameters.Add("From", fromUtc, DbType.DateTime2);
        parameters.Add("To", toUtc, DbType.DateTime2);

        var apply = string.Empty;
        var where = new List<string>
        {
            "m.EndpointId = @EndpointId",
            "m.MessageType = 'ErrorResponse'",
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
            apply = $@"
CROSS APPLY (SELECT (SELECT TOP (1) j.ErrorText
                     FROM OPENJSON(m.MessageContentJson, '$.ErrorContent')
                          WITH (ErrorText NVARCHAR(MAX) '$.ErrorText') AS j) AS ErrorText) AS e";
            where.Add(@"(CASE WHEN DATALENGTH(e.ErrorText) > 0 THEN e.ErrorText ELSE m.DeadLetterErrorDescription END)
    COLLATE Latin1_General_100_CI_AS LIKE @ErrorText ESCAPE '\'");
            parameters.Add("ErrorText", "%" + EscapeLike(errorTextContains) + "%");
        }

        var sql = $@"
SELECT COUNT(*) FROM (
    SELECT TOP (@Cap) 1 AS One
    FROM {Messages} AS m{apply}
    WHERE {string.Join(" AND ", where)}
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
}
