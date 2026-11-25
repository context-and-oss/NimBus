using System.Data;
using Dapper;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore.SqlServer;

// Erasure of one event copy (Spec 049). Each place is one statement, so each commits on its own:
// one transaction across the tables would hold locks on all of them while the audits are redacted.
internal sealed partial class SqlServerMessageTrackingStore
{
    // AuditType is persisted as the enum name, so the payload list is matched on names. They are
    // enum identifiers, safe to inline.
    private static readonly string PayloadAuditTypeList = string.Join(",",
        ErasureRules.PayloadAuditTypes.Select(t => $"'{t}'"));

    // Ids per UPDATE ... WHERE Id IN @Ids, well under SQL Server's 2,100-parameter limit.
    private const int ComposeUpdateChunk = 1000;

    public Task<EventErasureResult> EraseEventCopyAsync(EventErasureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EventErasureRunner.RunAsync(request, new ErasureSteps(this, request.EndpointId, request.EventId), cancellationToken);
    }

    private sealed class ErasureSteps : IEventErasureSteps
    {
        private readonly SqlServerMessageTrackingStore _store;
        private readonly string _endpointId;
        private readonly string _eventId;

        public ErasureSteps(SqlServerMessageTrackingStore store, string endpointId, string eventId)
        {
            _store = store;
            _endpointId = endpointId;
            _eventId = eventId;
        }

        private object Pair => new { E = _endpointId, V = _eventId };

        public async Task<ErasureSources> ReadSourcesAsync(CancellationToken cancellationToken)
        {
            // No Deleted filter: archived and soft-deleted rows keep their payload too.
            var sql = $@"
SELECT EventId, SessionId, Status, PendingSubStatus, Deleted, MessageContentJson
FROM {_store.T("UnresolvedEvents")} WHERE EndpointId = @E AND EventId = @V;
SELECT MessageContentJson
FROM {_store.T("Messages")} WHERE EventId = @V AND EndpointId = @E;";

            await using var conn = await _store.OpenAsync();
            using var grid = await conn.QueryMultipleAsync(sql, Pair, commandTimeout: _store._context.CommandTimeout);
            var rows = (await grid.ReadAsync()).Select(r => new ErasureTrackingRow(
                new UnresolvedEvent
                {
                    EventId = r.EventId,
                    SessionId = r.SessionId,
                    EndpointId = _endpointId,
                    ResolutionStatus = Enum.TryParse((string)r.Status, out ResolutionStatus status) ? status : ResolutionStatus.Pending,
                    PendingSubStatus = r.PendingSubStatus,
                    MessageContent = ParseContent((string?)r.MessageContentJson),
                },
                Hidden: (bool)r.Deleted)).ToList();
            var history = (await grid.ReadAsync<string?>()).ToList();

            var payloads = rows.Select(r => r.Event.MessageContent.EventContent?.EventJson)
                .Concat(history.Select(json => ParseContent(json).EventContent?.EventJson))
                .OfType<string>()
                .ToList();
            return new ErasureSources(rows, history.Count, payloads);
        }

        private static MessageContent ParseContent(string? json)
        {
            if (string.IsNullOrEmpty(json))
                return new MessageContent();
            try
            {
                return JsonConvert.DeserializeObject<MessageContent>(json) ?? new MessageContent();
            }
            catch (JsonException)
            {
                return new MessageContent();
            }
        }

        public async Task<int> RedactEventAuditsAsync(string marker, CancellationToken cancellationToken)
        {
            // Mirrors ErasureRules.AuditNeedsRedaction and RedactedAuditData. Entries written
            // without an endpoint belong to the event too. EraseEvent records are never touched.
            var sql = $@"
UPDATE {_store.T("MessageAudits")}
SET Data = CASE WHEN AuditType IN ({PayloadAuditTypeList}) AND Data IS NOT NULL THEN @Marker ELSE Data END,
    Comment = NULL
WHERE EventId = @V
  AND (EndpointId = @E OR EndpointId IS NULL OR EndpointId = N'')
  AND AuditType <> N'{nameof(MessageAuditType.EraseEvent)}'
  AND (Comment IS NOT NULL
       OR (AuditType IN ({PayloadAuditTypeList}) AND Data IS NOT NULL AND Data NOT LIKE @MarkerPattern ESCAPE '\'));
SELECT @@ROWCOUNT;";

            await using var conn = await _store.OpenAsync();
            return await conn.QuerySingleAsync<int>(sql, new
            {
                E = _endpointId,
                V = _eventId,
                Marker = marker,
                MarkerPattern = LikePrefix(ErasureRules.RedactionMarkerPrefix),
            }, commandTimeout: _store._context.CommandTimeout);
        }

        public async Task<int> RedactComposeAuditsAsync(
            string producerEndpointId,
            string eventTypeId,
            Func<string?, string?, bool> matches,
            string marker,
            CancellationToken cancellationToken)
        {
            // Compose entries carry no event id; IX_MessageAudits_Endpoint_EventType_Created serves
            // the lookup, and the content is matched in process by the shared rule.
            var select = $@"
SELECT Id, Data, EventTypeId
FROM {_store.T("MessageAudits")}
WHERE EndpointId = @P AND EventTypeId = @T AND AuditType = N'{nameof(MessageAuditType.Compose)}' AND Data IS NOT NULL";

            await using var conn = await _store.OpenAsync();
            var candidates = await conn.QueryAsync(select, new { P = producerEndpointId, T = eventTypeId },
                commandTimeout: _store._context.CommandTimeout);
            var ids = candidates
                .Where(r => matches((string?)r.Data, (string?)r.EventTypeId))
                .Select(r => (long)r.Id)
                .ToList();

            var update = $@"
UPDATE {_store.T("MessageAudits")} SET Data = @Marker, Comment = NULL WHERE Id IN @Ids;
SELECT @@ROWCOUNT;";
            var redacted = 0;
            foreach (var chunk in ids.Chunk(ComposeUpdateChunk))
            {
                redacted += await conn.QuerySingleAsync<int>(update, new { Marker = marker, Ids = chunk },
                    commandTimeout: _store._context.CommandTimeout);
            }

            return redacted;
        }

        public Task<int> DeleteHistoryAsync(CancellationToken cancellationToken) =>
            DeleteAsync($"DELETE FROM {_store.T("Messages")} WHERE EventId = @V AND EndpointId = @E;");

        public Task<int> DeleteEventReportAsync(CancellationToken cancellationToken) =>
            DeleteAsync($"DELETE FROM {_store.T("EventReports")} WHERE EndpointId = @E AND EventId = @V;");

        // Every session and every Deleted value: a row left behind would keep its payload.
        public Task<int> DeleteTrackingRowsAsync(CancellationToken cancellationToken) =>
            DeleteAsync($"DELETE FROM {_store.T("UnresolvedEvents")} WHERE EndpointId = @E AND EventId = @V;");

        private async Task<int> DeleteAsync(string statement)
        {
            await using var conn = await _store.OpenAsync();
            return await conn.QuerySingleAsync<int>(statement + " SELECT @@ROWCOUNT;", Pair,
                commandTimeout: _store._context.CommandTimeout);
        }
    }

    // ───────── Retention (Spec 050) ─────────

    private const string OpenStatusList = "'Pending','Failed','Deferred','DeadLettered','Unsupported'";
    private const string ResolvedStatusList = "'Completed','Skipped'";

    public async Task<RetentionPurgeResult> PurgeExpiredData(RetentionPeriods periods, DateTime nowUtc, int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(periods);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        cancellationToken.ThrowIfCancellationRequested();

        await using var conn = await OpenAsync();

        // UpdatedAtUtc is the row's last write, as Cosmos DB counts ttl from the last write.
        var open = await DeleteOlderThan(conn, periods.OpenEventsDays, nowUtc, batchSize,
            $"DELETE TOP (@Batch) FROM {T("UnresolvedEvents")} WHERE Deleted = 0 AND Status IN ({OpenStatusList}) AND UpdatedAtUtc < @Cutoff;");

        // Two statements, so each predicate seeks its own index: archived and removed rows
        // (IX_UnresolvedEvents_Deleted_UpdatedAtUtc), and visible Completed and Skipped rows
        // (IX_UnresolvedEvents_UpdatedAtUtc).
        var resolved = await DeleteOlderThan(conn, periods.ResolvedEventsDays, nowUtc, batchSize,
            $"DELETE TOP (@Batch) FROM {T("UnresolvedEvents")} WHERE Deleted = 1 AND UpdatedAtUtc < @Cutoff;");
        if (resolved < batchSize)
        {
            resolved += await DeleteOlderThan(conn, periods.ResolvedEventsDays, nowUtc, batchSize - resolved,
                $"DELETE TOP (@Batch) FROM {T("UnresolvedEvents")} WHERE Deleted = 0 AND Status IN ({ResolvedStatusList}) AND UpdatedAtUtc < @Cutoff;");
        }

        var history = await DeleteOlderThan(conn, periods.MessageHistoryDays, nowUtc, batchSize,
            $"DELETE TOP (@Batch) FROM {T("Messages")} WHERE CreatedAtUtc < @Cutoff;");
        var audits = await DeleteOlderThan(conn, periods.AuditTrailDays, nowUtc, batchSize,
            $"DELETE TOP (@Batch) FROM {T("MessageAudits")} WHERE CreatedAtUtc < @Cutoff;");

        return new RetentionPurgeResult(open, resolved, history, audits);
    }

    private async Task<int> DeleteOlderThan(Microsoft.Data.SqlClient.SqlConnection conn, int days, DateTime nowUtc, int batchSize, string statement)
    {
        if (days == RetentionPeriods.Unlimited)
            return 0;

        var parameters = new DynamicParameters();
        parameters.Add("Batch", batchSize);
        // DateTime2: a legacy datetime parameter would round the cutoff to ~3 ms.
        parameters.Add("Cutoff", nowUtc.AddDays(-days), DbType.DateTime2);
        return await conn.QuerySingleAsync<int>(statement + " SELECT @@ROWCOUNT;", parameters, commandTimeout: _context.CommandTimeout);
    }
}
