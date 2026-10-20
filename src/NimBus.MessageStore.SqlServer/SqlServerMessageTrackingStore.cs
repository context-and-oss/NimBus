using System.Globalization;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;

namespace NimBus.MessageStore.SqlServer;

internal sealed partial class SqlServerMessageTrackingStore : IMessageTrackingStore
{
    private readonly SqlServerStoreContext _context;

    public SqlServerMessageTrackingStore(SqlServerStoreContext context) => _context = context;

    private Task<SqlConnection> OpenAsync() => _context.Open();
    private string T(string table) => _context.Table(table);

    private static string CompositeEventId((string EventId, string? SessionId, string Status) row)
        => $"{row.EventId}_{row.SessionId ?? string.Empty}";

    private static string CompositeEventId(UnresolvedEvent @event)
        => $"{@event.EventId}_{@event.SessionId ?? string.Empty}";

    /// <summary>
    /// Escapes LIKE wildcards (<c>\ % _ [</c>) in a user-supplied value and
    /// appends <c>%</c>, producing a safe prefix pattern for
    /// <c>LIKE @p ESCAPE '\'</c> filters. Prepend <c>%</c> to the result for a
    /// contains pattern.
    /// </summary>
    private static string LikePrefix(string value)
    {
        var escaped = value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("%", "\\%", StringComparison.Ordinal)
            .Replace("_", "\\_", StringComparison.Ordinal)
            .Replace("[", "\\[", StringComparison.Ordinal);
        return escaped + "%";
    }

    private static int DecodeOffset(string? token)
    {
        if (string.IsNullOrEmpty(token)) return 0;
        try { return int.Parse(System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(token!)), CultureInfo.InvariantCulture); }
        catch { return 0; }
    }

    private static string EncodeOffset(int offset)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(offset.ToString(CultureInfo.InvariantCulture)));

    private static UnresolvedEvent MapUnresolvedEventRow(dynamic row)
    {
        return new UnresolvedEvent
        {
            EventId = row.EventId,
            SessionId = row.SessionId,
            EndpointId = row.EndpointId,
            ResolutionStatus = Enum.TryParse((string)row.Status, out ResolutionStatus rs) ? rs : ResolutionStatus.Pending,
            UpdatedAt = row.UpdatedAtUtc,
            EnqueuedTimeUtc = row.EnqueuedTimeUtc,
            CorrelationId = row.CorrelationId,
            EndpointRole = Enum.TryParse((string?)row.EndpointRole, out EndpointRole er) ? er : EndpointRole.Subscriber,
            MessageType = Enum.TryParse((string?)row.MessageType, out MessageType mt) ? mt : MessageType.EventRequest,
            RetryCount = row.RetryCount,
            RetryLimit = row.RetryLimit,
            LastMessageId = row.LastMessageId,
            OriginatingMessageId = row.OriginatingMessageId,
            ParentMessageId = row.ParentMessageId,
            OriginatingFrom = row.OriginatingFrom,
            Reason = row.Reason,
            DeadLetterReason = row.DeadLetterReason,
            DeadLetterErrorDescription = row.DeadLetterErrorDescription,
            EventTypeId = row.EventTypeId,
            To = row.ToAddress,
            From = row.FromAddress,
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
            MessageContent = string.IsNullOrEmpty((string?)row.MessageContentJson)
                ? new MessageContent()
                : JsonConvert.DeserializeObject<MessageContent>((string)row.MessageContentJson) ?? new MessageContent(),
        };
    }

    // Dapper exposes rows as DapperRow, which is dictionary-like. Reading a column that
    // does not exist throws — guard with the dictionary view so old rows / older callers
    // don't break when the new nullable columns aren't projected.
    private static string? TryReadString(dynamic row, string columnName)
    {
        var dict = (IDictionary<string, object>)row;
        return dict.TryGetValue(columnName, out var value) ? value as string : null;
    }

    private static DateTime? TryReadDateTime(dynamic row, string columnName)
    {
        var dict = (IDictionary<string, object>)row;
        if (!dict.TryGetValue(columnName, out var value) || value is null) return null;
        return value is DateTime dt ? dt : (DateTime?)null;
    }

}
