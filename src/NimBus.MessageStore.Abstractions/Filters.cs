using System;
using System.Collections.Generic;
using System.Linq;
using NimBus.Core.Messages;

namespace NimBus.MessageStore;

public class MessageFilter
{
    public string? EndpointId { get; set; }
    public string? EventId { get; set; }
    public string? MessageId { get; set; }
    public string? SessionId { get; set; }
    public List<string>? EventTypeId { get; set; }
    public string? From { get; set; }
    public string? To { get; set; }
    public MessageType? MessageType { get; set; }
    public DateTime? EnqueuedAtFrom { get; set; }
    public DateTime? EnqueuedAtTo { get; set; }
}

public class MessageSearchResult
{
    public IEnumerable<MessageEntity> Messages { get; set; } = Enumerable.Empty<MessageEntity>();
    public string? ContinuationToken { get; set; }
}

public class AuditFilter
{
    public string? EventId { get; set; }
    public string? EndpointId { get; set; }

    /// <summary>
    /// When true, <see cref="EndpointId"/> matches by case-insensitive EQUALITY
    /// instead of the default case-insensitive prefix. Used by authorization-
    /// scoped queries (the endpoint Audit tab), where a prefix match would let
    /// a manager of "Orders" read "OrdersArchive" rows. Default false preserves
    /// the historical prefix semantics for existing callers.
    /// </summary>
    public bool EndpointIdExact { get; set; }

    public string? AuditorName { get; set; }
    public string? EventTypeId { get; set; }
    public MessageAuditType? AuditType { get; set; }
    public DateTime? CreatedAtFrom { get; set; }
    public DateTime? CreatedAtTo { get; set; }
}

public class AuditSearchResult
{
    public IEnumerable<AuditSearchItem> Audits { get; set; } = Enumerable.Empty<AuditSearchItem>();
    public string? ContinuationToken { get; set; }
}

public class AuditSearchItem
{
    public string EventId { get; set; } = null!;
    public string? EndpointId { get; set; }
    public string? EventTypeId { get; set; }
    public MessageAuditEntity Audit { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}

public class EventFilter
{
    /// <summary>
    /// Exact endpoint id, never a prefix match (unlike the other ID-like filters).
    /// See <see cref="Abstractions.IMessageTrackingStore.GetEventsByFilter"/>.
    /// </summary>
    public string? EndPointId { get; set; }

    public DateTime? UpdatedAtFrom { get; set; }
    public DateTime? UpdatedAtTo { get; set; }
    public DateTime? EnqueuedAtFrom { get; set; }
    public DateTime? EnqueuedAtTo { get; set; }

    public string? EventId { get; set; }
    public List<string>? EventTypeId { get; set; }
    public string? SessionId { get; set; }
    public string? To { get; set; }
    public string? From { get; set; }
    public List<string>? ResolutionStatus { get; set; }
    public string? Payload { get; set; }
    public MessageType? MessageType { get; set; }

    /// <summary>
    /// Case-insensitive substring match on <c>MessageContent.ErrorContent.ErrorText</c>.
    /// </summary>
    public string? ErrorText { get; set; }

    /// <summary>
    /// Case-insensitive prefix match on <see cref="States.UnresolvedEvent.LastMessageId"/>,
    /// the id of the message the event's current state was recorded for.
    /// </summary>
    public string? LastMessageId { get; set; }
}
