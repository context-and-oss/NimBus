using System;
using System.Collections.Generic;
using System.Globalization;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Provider-neutral rules for erasing an event copy (Spec 049). Every storage provider applies
/// them, through <see cref="EventErasureRunner"/>, so an erasure removes the same data whatever
/// store is configured.
/// </summary>
public static class ErasureRules
{
    /// <summary>
    /// The suffix the WebApp's audit writer appends when it truncates an audit entry's
    /// <see cref="MessageAuditEntity.Data"/>.
    /// </summary>
    public const string AuditTruncationSuffix = "… [truncated]";

    /// <summary>The start of every redaction marker; see <see cref="RedactionMarker"/>.</summary>
    public const string RedactionMarkerPrefix = "{\"erased\":true";

    /// <summary>
    /// Audit types whose <see cref="MessageAuditEntity.Data"/> can carry the event's payload or
    /// free text tied to the event (the payload list of Spec 049's audit type inventory). Their
    /// data is replaced with a <see cref="RedactionMarker"/>.
    /// </summary>
    public static IReadOnlySet<MessageAuditType> PayloadAuditTypes { get; } = new HashSet<MessageAuditType>
    {
        MessageAuditType.Resubmit,
        MessageAuditType.ResubmitWithChanges,
        MessageAuditType.Skip,
        MessageAuditType.CompleteHandoff,
        MessageAuditType.FailHandoff,
        MessageAuditType.ReportEvent,
    };

    /// <summary>The <see cref="MessageAuditEntity.Data"/> written in place of redacted data.</summary>
    public static string RedactionMarker(string erasureId) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{RedactionMarkerPrefix},\"erasureId\":{JsonConvert.ToString(erasureId)}}}");

    /// <summary>True when <paramref name="data"/> is a redaction marker written by an erasure.</summary>
    public static bool IsRedactionMarker(string? data) =>
        data is not null && data.StartsWith(RedactionMarkerPrefix, StringComparison.Ordinal);

    /// <summary>
    /// True when an audit entry of <paramref name="auditType"/>, with <paramref name="data"/> and
    /// <paramref name="comment"/>, still holds something the erasure of its event clears: payload
    /// data on a payload-list type, or any comment. Erasure records are never touched.
    /// </summary>
    public static bool AuditNeedsRedaction(MessageAuditType auditType, string? data, string? comment)
    {
        if (auditType == MessageAuditType.EraseEvent)
            return false;
        if (comment is not null)
            return true;
        return PayloadAuditTypes.Contains(auditType) && data is not null && !IsRedactionMarker(data);
    }

    /// <summary>
    /// The data an audit entry of <paramref name="auditType"/> keeps after redaction: the marker for
    /// a payload-list type, the original data otherwise.
    /// </summary>
    public static string? RedactedAuditData(MessageAuditType auditType, string? data, string marker) =>
        PayloadAuditTypes.Contains(auditType) && data is not null ? marker : data;

    /// <summary>
    /// True when a Compose audit entry's <paramref name="data"/> holds one of
    /// <paramref name="payloads"/>, the erased event's payloads.
    /// <para>The data is the serialised compose request, <c>{"eventTypeId":…,"eventContent":"…"}</c>.
    /// It matches when its <c>eventContent</c> is JSON-equal to a payload. When the audit writer
    /// truncated it (it ends with <see cref="AuditTruncationSuffix"/>), it matches when the part
    /// before the suffix is a prefix of the request rebuilt from a payload with
    /// <paramref name="storedEventTypeId"/>, the event type stored on the entry, and reaches into
    /// the content. Entries written since #415 hold a hash instead of the content and never match.</para>
    /// </summary>
    public static bool ComposeDataMatches(string? data, string? storedEventTypeId, IReadOnlyCollection<string> payloads)
    {
        ArgumentNullException.ThrowIfNull(payloads);
        if (string.IsNullOrEmpty(data) || IsRedactionMarker(data) || payloads.Count == 0)
            return false;

        if (data.EndsWith(AuditTruncationSuffix, StringComparison.Ordinal))
            return TruncatedComposeMatches(data[..^AuditTruncationSuffix.Length], storedEventTypeId, payloads);

        JToken? content;
        try
        {
            content = JToken.Parse(data) is JObject request
                ? request.GetValue("eventContent", StringComparison.OrdinalIgnoreCase)
                : null;
        }
        catch (JsonException)
        {
            return false;
        }

        if (content is not { Type: JTokenType.String })
            return false;

        var composed = content.Value<string>();
        if (string.IsNullOrEmpty(composed))
            return false;

        var composedJson = TryParse(composed);
        foreach (var payload in payloads)
        {
            if (string.Equals(composed, payload, StringComparison.Ordinal))
                return true;
            if (composedJson is not null && TryParse(payload) is { } payloadJson && JToken.DeepEquals(composedJson, payloadJson))
                return true;
        }

        return false;
    }

    private static bool TruncatedComposeMatches(string prefix, string? storedEventTypeId, IReadOnlyCollection<string> payloads)
    {
        // The rebuilt request, in the property order of the serialised compose body.
        var header = "{\"eventTypeId\":" + JsonConvert.ToString(storedEventTypeId ?? string.Empty) + ",\"eventContent\":\"";
        if (prefix.Length <= header.Length)
            return false;

        foreach (var payload in payloads)
        {
            var rebuilt = JsonConvert.SerializeObject(new ComposeBody(storedEventTypeId ?? string.Empty, payload));
            if (rebuilt.StartsWith(prefix, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static JToken? TryParse(string json)
    {
        try
        {
            return JToken.Parse(json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ComposeBody(
        [property: JsonProperty("eventTypeId")] string EventTypeId,
        [property: JsonProperty("eventContent")] string EventContent);
}
