using Dapper;
using Newtonsoft.Json;
using NimBus.Core.Inbox;

namespace NimBus.MessageStore.SqlServer;

internal sealed partial class SqlServerMessageTrackingStore
{
    // ───────── Resolver state writes (status transitions) ─────────

    public Task<bool> UploadPendingMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        => UpsertStatus(eventId, sessionId, endpointId, "Pending", content);

    public Task<bool> UploadDeferredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        => UpsertStatus(eventId, sessionId, endpointId, "Deferred", content);

    public Task<bool> UploadFailedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        => UpsertStatus(eventId, sessionId, endpointId, "Failed", content);

    public Task<bool> UploadDeadletteredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        => UpsertStatus(eventId, sessionId, endpointId, "DeadLettered", content);

    public Task<bool> UploadUnsupportedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        => UpsertStatus(eventId, sessionId, endpointId, "Unsupported", content);

    public Task<bool> UploadSkippedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        => UpsertStatus(eventId, sessionId, endpointId, "Skipped", content);

    public Task<bool> UploadCompletedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content)
        => UpsertStatus(eventId, sessionId, endpointId, "Completed", content);

    public async Task<bool> TrySkipDeferredMessage(string eventId, string sessionId, string endpointId,
        string? expectedLastMessageId, DateTime expectedUpdatedAt)
    {
        var sql = $@"
UPDATE {T("UnresolvedEvents")}
SET Status = 'Skipped', UpdatedAtUtc = @Now
WHERE EndpointId = @EndpointId AND EventId = @EventId
  AND ((SessionId IS NULL AND @SessionId IS NULL) OR SessionId = @SessionId)
  AND Status = 'Deferred' AND Deleted = 0 AND UpdatedAtUtc = @ExpectedUpdatedAt
  AND ((LastMessageId IS NULL AND @ExpectedLastMessageId IS NULL)
       OR LastMessageId COLLATE Latin1_General_BIN2 = @ExpectedLastMessageId COLLATE Latin1_General_BIN2);
SELECT @@ROWCOUNT;";
        await using var connection = await OpenAsync();
        var parameters = new DynamicParameters(new
        {
            EventId = eventId, SessionId = sessionId, EndpointId = endpointId,
            ExpectedLastMessageId = expectedLastMessageId,
        });
        // The row version is DATETIME2. A DateTime parameter can round it to SQL
        // datetime precision and spuriously reject the very row that was inspected.
        parameters.Add("ExpectedUpdatedAt", expectedUpdatedAt, System.Data.DbType.DateTime2);
        parameters.Add("Now", DateTime.UtcNow, System.Data.DbType.DateTime2);
        return await connection.QuerySingleAsync<int>(sql, parameters, commandTimeout: _context.CommandTimeout) == 1;
    }

    public Task<bool> TryArchiveUnresolvedEvent(string eventId, string sessionId, string endpointId,
        ResolutionStatus expectedStatus, string? expectedLastMessageId, DateTime expectedUpdatedAt) =>
        TrySetDeleted(eventId, sessionId, endpointId, expectedStatus, expectedLastMessageId, expectedUpdatedAt, archive: true);

    public Task<bool> TryRestoreArchivedEvent(string eventId, string sessionId, string endpointId,
        ResolutionStatus expectedStatus, string? expectedLastMessageId, DateTime expectedUpdatedAt) =>
        TrySetDeleted(eventId, sessionId, endpointId, expectedStatus, expectedLastMessageId, expectedUpdatedAt, archive: false);

    private async Task<bool> TrySetDeleted(string eventId, string sessionId, string endpointId,
        ResolutionStatus expectedStatus, string? expectedLastMessageId, DateTime expectedUpdatedAt, bool archive)
    {
        var sql = $@"
UPDATE {T("UnresolvedEvents")}
SET Deleted = @Deleted
WHERE EndpointId = @EndpointId AND EventId = @EventId
  AND ((SessionId IS NULL AND @SessionId IS NULL) OR SessionId = @SessionId)
  AND Status = @ExpectedStatus AND Deleted = @CurrentDeleted AND UpdatedAtUtc = @ExpectedUpdatedAt
  AND ((LastMessageId IS NULL AND @ExpectedLastMessageId IS NULL)
       OR LastMessageId COLLATE Latin1_General_BIN2 = @ExpectedLastMessageId COLLATE Latin1_General_BIN2);
SELECT @@ROWCOUNT;";
        await using var connection = await OpenAsync();
        var parameters = new DynamicParameters(new
        {
            EventId = eventId, SessionId = sessionId, EndpointId = endpointId,
            ExpectedStatus = expectedStatus.ToString(),
            ExpectedLastMessageId = expectedLastMessageId,
            Deleted = archive,
            CurrentDeleted = !archive,
        });
        // See TrySkipDeferredMessage: the row version is DATETIME2.
        parameters.Add("ExpectedUpdatedAt", expectedUpdatedAt, System.Data.DbType.DateTime2);
        return await connection.QuerySingleAsync<int>(sql, parameters, commandTimeout: _context.CommandTimeout) == 1;
    }

    public async Task<bool> TryCompletePendingMessage(
        string eventId,
        string sessionId,
        string endpointId,
        string? expectedLastMessageId,
        UnresolvedEvent content)
    {
        var sql = $@"
UPDATE {T("UnresolvedEvents")}
SET {StatusUpdateSet}
WHERE EndpointId = @EndpointId
  AND EventId = @EventId
  AND ((SessionId IS NULL AND @SessionId IS NULL) OR SessionId = @SessionId)
  AND Status = 'Pending'
  AND Deleted = 0
  AND ((LastMessageId IS NULL AND @ExpectedLastMessageId IS NULL)
       OR LastMessageId COLLATE Latin1_General_BIN2 = @ExpectedLastMessageId COLLATE Latin1_General_BIN2);
SELECT @@ROWCOUNT;";

        await using var conn = await OpenAsync();
        var rows = await conn.QuerySingleAsync<int>(
            sql,
            StatusParameters(eventId, sessionId, endpointId, "Completed", content, expectedLastMessageId),
            commandTimeout: _context.CommandTimeout);
        return rows == 1;
    }

    private const string StatusUpdateSet = @"
    Status = @Status,
    UpdatedAtUtc = @UpdatedAt,
    EnqueuedTimeUtc = @EnqueuedTimeUtc,
    CorrelationId = @CorrelationId,
    EndpointRole = @EndpointRole,
    MessageType = @MessageType,
    RetryCount = @RetryCount,
    RetryLimit = @RetryLimit,
    LastMessageId = @LastMessageId,
    OriginatingMessageId = @OriginatingMessageId,
    ParentMessageId = @ParentMessageId,
    OriginatingFrom = @OriginatingFrom,
    Reason = @Reason,
    DeadLetterReason = @DeadLetterReason,
    DeadLetterErrorDescription = @DeadLetterErrorDescription,
    EventTypeId = @EventTypeId,
    ToAddress = @ToAddress,
    FromAddress = @FromAddress,
    QueueTimeMs = @QueueTimeMs,
    ProcessingTimeMs = @ProcessingTimeMs,
    CloudEventId = @CloudEventId,
    CloudEventSource = @CloudEventSource,
    CloudEventType = @CloudEventType,
    CloudEventSubject = @CloudEventSubject,
    PendingSubStatus = @PendingSubStatus,
    HandoffReason = @HandoffReason,
    ExternalJobId = @ExternalJobId,
    ExpectedBy = @ExpectedBy,
    MessageContentJson = @MessageContentJson,
    Deleted = 0";

    /// <summary>
    /// Writes one status transition. The MATCHED branch carries the Spec 030 stale-write guard,
    /// transliterated from <see cref="StaleWriteGuard.Allows"/>: keep the two in step — the
    /// conformance suite runs the same cases against this provider and against the rule.
    /// <para>HOLDLOCK (a serializable range lock) makes the write serializable per key. It is the
    /// hint the EventReports MERGE below already uses, and it also closes the pre-existing
    /// first-insert race where two concurrent first writes both miss MATCHED and collide on the
    /// primary key.</para>
    /// <para>Returns whether a row was inserted or updated. The count comes from an explicit
    /// <c>@@ROWCOUNT</c> rather than Dapper's records-affected, which <c>SET NOCOUNT ON</c> turns
    /// into -1.</para>
    /// </summary>
    private async Task<bool> UpsertStatus(string eventId, string sessionId, string endpointId, string status, UnresolvedEvent content)
    {
        var sql = $@"
MERGE {T("UnresolvedEvents")} WITH (HOLDLOCK) AS target
USING (SELECT @EventId AS EventId, @SessionId AS SessionId, @EndpointId AS EndpointId) AS source
ON target.EndpointId = source.EndpointId AND target.EventId = source.EventId
   AND ((target.SessionId IS NULL AND source.SessionId IS NULL) OR target.SessionId = source.SessionId)
WHEN MATCHED AND NOT (
        @MessageType = 'SkipResponse'
    AND COALESCE(@Reason, N'') COLLATE Latin1_General_BIN2 = N'{InboxMiddleware.DuplicateReason}'
    AND DATALENGTH(COALESCE(@Reason, N'')) = DATALENGTH(N'{InboxMiddleware.DuplicateReason}')        -- = ignores trailing spaces; IsDuplicateSkip is ordinal
    AND target.Status IN ('Completed','Skipped','Failed','DeadLettered','Unsupported')               -- a duplicate skip never rewrites a settled row (StaleWriteGuard.IsDuplicateSkip)
) AND (
        @Status IN ('Completed','Skipped','Failed','DeadLettered','Unsupported')                     -- other terminal writes are unguarded
     OR @MessageType NOT IN ('EventRequest','DeferralResponse','PendingHandoffResponse',
                             'ResubmissionRequest','SkipRequest','RetryRequest','ContinuationRequest',
                             'HandoffCompletedRequest','HandoffFailedRequest')                       -- so are unguarded message types
     OR (
            (NULLIF(target.ParentMessageId, '') IS NULL OR NULLIF(@LastMessageId, '') IS NULL
             OR target.ParentMessageId COLLATE Latin1_General_BIN2
                <> @LastMessageId COLLATE Latin1_General_BIN2)                                       -- the row does not already answer this message
        AND CASE
              WHEN @MessageType IN ('EventRequest','DeferralResponse') THEN
                   -- Negated terminal list, not IN ('Pending','Deferred'): StaleWriteGuard tests
                   -- !IsTerminal, and ResolutionStatus also has TooManyRequests and Published.
                   -- No upload method writes those today, but the two rules must not drift.
                   CASE WHEN target.Status NOT IN ('Completed','Skipped','Failed','DeadLettered','Unsupported')
                         AND (target.MessageType IS NULL
                              OR target.MessageType IN ('EventRequest','DeferralResponse','Unknown')) THEN 1 ELSE 0 END
              WHEN @MessageType = 'PendingHandoffResponse' THEN
                   CASE WHEN target.Status NOT IN ('Completed','Skipped') THEN 1 ELSE 0 END
              ELSE 1                                                                                 -- control requests
            END = 1
        )
) THEN UPDATE SET
{StatusUpdateSet}
WHEN NOT MATCHED THEN INSERT (
    EventId, SessionId, EndpointId, Status, UpdatedAtUtc, EnqueuedTimeUtc, CorrelationId, EndpointRole,
    MessageType, RetryCount, RetryLimit, LastMessageId, OriginatingMessageId, ParentMessageId,
    OriginatingFrom, Reason, DeadLetterReason, DeadLetterErrorDescription, EventTypeId,
    ToAddress, FromAddress, QueueTimeMs, ProcessingTimeMs,
    CloudEventId, CloudEventSource, CloudEventType, CloudEventSubject,
    PendingSubStatus, HandoffReason, ExternalJobId, ExpectedBy,
    MessageContentJson)
VALUES (
    @EventId, @SessionId, @EndpointId, @Status, @UpdatedAt, @EnqueuedTimeUtc, @CorrelationId, @EndpointRole,
    @MessageType, @RetryCount, @RetryLimit, @LastMessageId, @OriginatingMessageId, @ParentMessageId,
    @OriginatingFrom, @Reason, @DeadLetterReason, @DeadLetterErrorDescription, @EventTypeId,
    @ToAddress, @FromAddress, @QueueTimeMs, @ProcessingTimeMs,
    @CloudEventId, @CloudEventSource, @CloudEventType, @CloudEventSubject,
    @PendingSubStatus, @HandoffReason, @ExternalJobId, @ExpectedBy,
    @MessageContentJson);
SELECT @@ROWCOUNT;";

        await using var conn = await OpenAsync();
        var rows = await conn.QuerySingleAsync<int>(
            sql,
            StatusParameters(eventId, sessionId, endpointId, status, content, expectedLastMessageId: null),
            commandTimeout: _context.CommandTimeout);

        return rows > 0;
    }

    private static DynamicParameters StatusParameters(
        string eventId,
        string sessionId,
        string endpointId,
        string status,
        UnresolvedEvent content,
        string? expectedLastMessageId)
    {
        var parameters = new DynamicParameters();
        parameters.Add("EventId", eventId);
        parameters.Add("SessionId", sessionId);
        parameters.Add("EndpointId", endpointId);
        parameters.Add("Status", status);
        // A DateTime parameter defaults to legacy datetime (1/300 s), which lets back-to-back
        // writes tie on UpdatedAtUtc so "latest row" reads such as GetEvent pick arbitrarily.
        // EnqueuedTimeUtc deliberately keeps the default: StalePendingReconciler compares it with
        // Messages.EnqueuedTimeUtc, which is stored at the same datetime precision.
        parameters.Add("UpdatedAt", DateTime.UtcNow, System.Data.DbType.DateTime2);
        parameters.Add("EnqueuedTimeUtc", content.EnqueuedTimeUtc);
        parameters.Add("CorrelationId", content.CorrelationId);
        parameters.Add("EndpointRole", content.EndpointRole.ToString());
        parameters.Add("MessageType", content.MessageType.ToString());
        parameters.Add("RetryCount", content.RetryCount);
        parameters.Add("RetryLimit", content.RetryLimit);
        parameters.Add("LastMessageId", content.LastMessageId);
        parameters.Add("ExpectedLastMessageId", expectedLastMessageId);
        parameters.Add("OriginatingMessageId", content.OriginatingMessageId);
        parameters.Add("ParentMessageId", content.ParentMessageId);
        parameters.Add("OriginatingFrom", content.OriginatingFrom);
        parameters.Add("Reason", content.Reason);
        parameters.Add("DeadLetterReason", content.DeadLetterReason);
        parameters.Add("DeadLetterErrorDescription", content.DeadLetterErrorDescription);
        parameters.Add("EventTypeId", content.EventTypeId);
        parameters.Add("ToAddress", content.To);
        parameters.Add("FromAddress", content.From);
        parameters.Add("QueueTimeMs", content.QueueTimeMs);
        parameters.Add("ProcessingTimeMs", content.ProcessingTimeMs);
        parameters.Add("CloudEventId", content.CloudEventId);
        parameters.Add("CloudEventSource", content.CloudEventSource);
        parameters.Add("CloudEventType", content.CloudEventType);
        parameters.Add("CloudEventSubject", content.CloudEventSubject);
        parameters.Add("PendingSubStatus", content.PendingSubStatus);
        parameters.Add("HandoffReason", content.HandoffReason);
        parameters.Add("ExternalJobId", content.ExternalJobId);
        parameters.Add("ExpectedBy", content.ExpectedBy);
        parameters.Add("MessageContentJson", JsonConvert.SerializeObject(content.MessageContent));
        return parameters;
    }

    // ───────── Lifecycle / cleanup ─────────

    public async Task<bool> RemoveMessage(string eventId, string sessionId, string endpointId)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(
            $"UPDATE {T("UnresolvedEvents")} SET Deleted = 1 WHERE EndpointId = @E AND EventId = @V AND SessionId = @S",
            new { E = endpointId, V = eventId, S = sessionId }, commandTimeout: _context.CommandTimeout);
        return rows > 0;
    }

    public async Task<bool> PurgeMessages(string endpointId, string sessionId)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(
            $"UPDATE {T("UnresolvedEvents")} SET Deleted = 1 WHERE EndpointId = @E AND SessionId = @S",
            new { E = endpointId, S = sessionId }, commandTimeout: _context.CommandTimeout);
        return rows > 0;
    }

    public async Task<bool> PurgeMessages(string endpointId)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(
            $"UPDATE {T("UnresolvedEvents")} SET Deleted = 1 WHERE EndpointId = @E",
            new { E = endpointId }, commandTimeout: _context.CommandTimeout);
        return rows > 0;
    }

    public async Task ArchiveFailedEvent(string eventId, string sessionId, string endpointId)
    {
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(
            $"UPDATE {T("UnresolvedEvents")} SET Deleted = 1 WHERE EndpointId = @E AND EventId = @V AND SessionId = @S",
            new { E = endpointId, V = eventId, S = sessionId }, commandTimeout: _context.CommandTimeout);
    }
}
