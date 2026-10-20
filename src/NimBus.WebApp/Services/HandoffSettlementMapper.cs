using NimBus.MessageStore;
using NimBus.SDK;

namespace NimBus.WebApp.Services;

/// <summary>
/// Maps a pending-handoff row to <see cref="HandoffSettlement"/> coordinates
/// exactly as the legacy <c>ManagerClient.CoordsFor</c> did: the row's
/// MessageId becomes the control message's ParentMessageId (HandoffClient
/// passes MessageId through as ParentMessageId); lineage fields may be null
/// on legacy rows and fall back on the wire.
/// </summary>
internal static class HandoffSettlementMapper
{
    internal static HandoffSettlement ToSettlement(this MessageEntity pendingEntry) => new(
        EventId: pendingEntry.EventId,
        SessionId: pendingEntry.SessionId,
        MessageId: pendingEntry.MessageId,
        EventTypeId: pendingEntry.EventTypeId,
        CorrelationId: pendingEntry.CorrelationId,
        OriginatingMessageId: pendingEntry.OriginatingMessageId);
}
