using Newtonsoft.Json;
using NimBus.Core.Messages;

namespace NimBus.MessageStore.SqlServer;

internal sealed partial class SqlServerMessageTrackingStore
{
    // ───────── Per-message persistence (StoreMessage / history) ─────────

    public async Task StoreMessage(MessageEntity message)
    {
        var sql = $@"
IF NOT EXISTS (SELECT 1 FROM {T("Messages")} WHERE EventId = @EventId AND MessageId = @MessageId)
INSERT INTO {T("Messages")} (
    EventId, MessageId, EndpointId, SessionId, CorrelationId, EventTypeId,
    OriginatingMessageId, ParentMessageId, FromAddress, ToAddress, OriginatingFrom, OriginalSessionId,
    MessageType, EndpointRole, EnqueuedTimeUtc, RetryCount, RetryLimit, DeferralSequence,
    QueueTimeMs, ProcessingTimeMs, CloudEventId, CloudEventSource, CloudEventType, CloudEventSubject,
    PendingSubStatus, HandoffReason, ExternalJobId, ExpectedBy,
    DeadLetterReason, DeadLetterErrorDescription, MessageContentJson)
VALUES (
    @EventId, @MessageId, @EndpointId, @SessionId, @CorrelationId, @EventTypeId,
    @OriginatingMessageId, @ParentMessageId, @FromAddress, @ToAddress, @OriginatingFrom, @OriginalSessionId,
    @MessageType, @EndpointRole, @EnqueuedTimeUtc, @RetryCount, @RetryLimit, @DeferralSequence,
    @QueueTimeMs, @ProcessingTimeMs, @CloudEventId, @CloudEventSource, @CloudEventType, @CloudEventSubject,
    @PendingSubStatus, @HandoffReason, @ExternalJobId, @ExpectedBy,
    @DeadLetterReason, @DeadLetterErrorDescription, @MessageContentJson);";

        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(sql, new
        {
            message.EventId,
            message.MessageId,
            message.EndpointId,
            message.SessionId,
            message.CorrelationId,
            message.EventTypeId,
            message.OriginatingMessageId,
            message.ParentMessageId,
            FromAddress = message.From,
            ToAddress = message.To,
            message.OriginatingFrom,
            message.OriginalSessionId,
            MessageType = message.MessageType.ToString(),
            EndpointRole = message.EndpointRole.ToString(),
            message.EnqueuedTimeUtc,
            message.RetryCount,
            message.RetryLimit,
            message.DeferralSequence,
            message.QueueTimeMs,
            message.ProcessingTimeMs,
            message.CloudEventId,
            message.CloudEventSource,
            message.CloudEventType,
            message.CloudEventSubject,
            message.PendingSubStatus,
            message.HandoffReason,
            message.ExternalJobId,
            message.ExpectedBy,
            message.DeadLetterReason,
            message.DeadLetterErrorDescription,
            MessageContentJson = JsonConvert.SerializeObject(message.MessageContent),
        }, commandTimeout: _context.CommandTimeout);
    }

    public async Task<MessageEntity?> GetMessage(string eventId, string messageId)
    {
        await using var conn = await OpenAsync();
        var row = await conn.QueryFirstOrDefaultAsync(
            $"SELECT * FROM {T("Messages")} WHERE EventId = @EventId AND MessageId = @MessageId",
            new { EventId = eventId, MessageId = messageId }, commandTimeout: _context.CommandTimeout);
        return row == null ? null : MapMessageRow(row);
    }

    public async Task<IEnumerable<MessageEntity>> GetEventHistory(string eventId)
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("Messages")} WHERE EventId = @EventId ORDER BY EnqueuedTimeUtc",
            new { EventId = eventId }, commandTimeout: _context.CommandTimeout);
        return rows.Select(MapMessageRow).ToList();
    }

    public async Task<MessageEntity?> GetLatestEventRequestMessage(string eventId)
    {
        await using var conn = await OpenAsync();
        // Narrow to the request-bearing message types in SQL and order newest-first.
        // EventJson lives inside the serialized MessageContent column, so the
        // non-empty-payload check happens after mapping. Stream the reader
        // unbuffered so it stops at the first payload-bearing row instead of
        // materialising the full request history.
        var rows = conn.QueryUnbufferedAsync(
            $@"SELECT * FROM {T("Messages")}
                WHERE EventId = @EventId
                  AND MessageType IN ('EventRequest', 'ResubmissionRequest')
                ORDER BY EnqueuedTimeUtc DESC",
            new { EventId = eventId }, commandTimeout: _context.CommandTimeout);

        await foreach (var row in rows)
        {
            var message = (MessageEntity)MapMessageRow(row);
            if (!string.IsNullOrEmpty(message.MessageContent?.EventContent?.EventJson))
            {
                return message;
            }
        }

        return null;
    }

    public async Task<MessageEntity?> GetFailedMessage(string eventId, string endpointId)
    {
        await using var conn = await OpenAsync();
        // ErrorContent lives inside the serialized MessageContent column, so the check
        // happens after mapping. Stream newest-first and stop at the first match.
        var rows = conn.QueryUnbufferedAsync(
            $@"SELECT m.* FROM {T("Messages")} m
                WHERE m.EventId = @EventId AND m.EndpointId = @EndpointId
                ORDER BY m.EnqueuedTimeUtc DESC",
            new { EventId = eventId, EndpointId = endpointId }, commandTimeout: _context.CommandTimeout);

        await foreach (var row in rows)
        {
            var message = (MessageEntity)MapMessageRow(row);
            if (message.MessageContent?.ErrorContent != null)
            {
                return message;
            }
        }

        return null;
    }

    public async Task<MessageEntity?> GetDeadletteredMessage(string eventId, string endpointId)
    {
        await using var conn = await OpenAsync();
        var row = await conn.QueryFirstOrDefaultAsync(
            $@"SELECT TOP 1 m.* FROM {T("Messages")} m
                WHERE m.EventId = @EventId AND m.EndpointId = @EndpointId
                ORDER BY m.EnqueuedTimeUtc DESC",
            new { EventId = eventId, EndpointId = endpointId }, commandTimeout: _context.CommandTimeout);
        return row == null ? null : MapMessageRow(row);
    }

    public async Task RemoveStoredMessage(string eventId, string messageId)
    {
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(
            $"DELETE FROM {T("Messages")} WHERE EventId = @EventId AND MessageId = @MessageId",
            new { EventId = eventId, MessageId = messageId }, commandTimeout: _context.CommandTimeout);
    }

    public async Task<int> PurgeStoredMessages(string endpointId)
    {
        await using var conn = await OpenAsync();
        return await conn.ExecuteAsync(
            $"DELETE FROM {T("Messages")} WHERE EndpointId = @EndpointId",
            new { EndpointId = endpointId }, commandTimeout: _context.CommandTimeout);
    }

    private static MessageEntity MapMessageRow(dynamic row)
    {
        return new MessageEntity
        {
            EventId = row.EventId,
            MessageId = row.MessageId,
            EndpointId = row.EndpointId,
            SessionId = row.SessionId,
            CorrelationId = row.CorrelationId,
            EventTypeId = row.EventTypeId,
            OriginatingMessageId = row.OriginatingMessageId,
            ParentMessageId = row.ParentMessageId,
            From = row.FromAddress,
            To = row.ToAddress,
            OriginatingFrom = row.OriginatingFrom,
            OriginalSessionId = row.OriginalSessionId,
            MessageType = Enum.TryParse((string?)row.MessageType, out MessageType mt) ? mt : MessageType.EventRequest,
            EndpointRole = Enum.TryParse((string?)row.EndpointRole, out EndpointRole er) ? er : EndpointRole.Subscriber,
            EnqueuedTimeUtc = row.EnqueuedTimeUtc,
            RetryCount = row.RetryCount,
            RetryLimit = row.RetryLimit,
            DeferralSequence = row.DeferralSequence,
            QueueTimeMs = row.QueueTimeMs,
            ProcessingTimeMs = row.ProcessingTimeMs,
            CloudEventId = TryReadString(row, "CloudEventId"),
            CloudEventSource = TryReadString(row, "CloudEventSource"),
            CloudEventType = TryReadString(row, "CloudEventType"),
            CloudEventSubject = TryReadString(row, "CloudEventSubject"),
            PendingSubStatus = TryReadString(row, "PendingSubStatus"),
            HandoffReason = TryReadString(row, "HandoffReason"),
            ExternalJobId = TryReadString(row, "ExternalJobId"),
            ExpectedBy = TryReadDateTime(row, "ExpectedBy"),
            DeadLetterReason = row.DeadLetterReason,
            DeadLetterErrorDescription = row.DeadLetterErrorDescription,
            MessageContent = JsonConvert.DeserializeObject<MessageContent>((string)row.MessageContentJson) ?? new MessageContent(),
        };
    }
}
