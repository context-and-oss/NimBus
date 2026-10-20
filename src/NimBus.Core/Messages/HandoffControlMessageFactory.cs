using NimBus.Core.Events;

namespace NimBus.Core.Messages;

/// <summary>
/// Audit-row coordinates needed to address a pending-handoff settlement
/// message back to the subscriber that owns the row. Caller assembles these
/// from whatever shape it has handy (UnresolvedEvent, MessageEntity, or
/// hand-built fields) and the factory produces the same on-wire
/// <see cref="Message"/> in every case.
/// <list type="bullet">
/// <item><description><c>To</c> — subscriber endpoint name; destination topic for the control message.</description></item>
/// <item><description><c>EventId</c> — EventId of the original pending message.</description></item>
/// <item><description><c>SessionId</c> — required for Service Bus to route the control message into the same session so the subscriber's FIFO replay holds.</description></item>
/// <item><description><c>CorrelationId</c> — carried through from the original message.</description></item>
/// <item><description><c>ParentMessageId</c> — MessageId of the inbound message that produced the pending row; becomes ParentMessageId on the control message.</description></item>
/// <item><description><c>OriginatingMessageId</c> — carried through from the original message.</description></item>
/// <item><description><c>EventTypeId</c> — EventTypeId of the original message.</description></item>
/// </list>
/// </summary>
public readonly record struct HandoffSettlementCoordinates(
    string To,
    string EventId,
    string SessionId,
    string CorrelationId,
    string ParentMessageId,
    string OriginatingMessageId,
    string EventTypeId);

/// <summary>
/// Builds the Service Bus control messages that drive Pending → Completed /
/// Pending → Failed transitions for handoff entries. Shared between
/// <see cref="NimBus.Manager.IManagerClient"/> (legacy bag-of-fields API) and
/// the new <c>NimBus.SDK.IHandoffClient</c> so on-wire behaviour is identical
/// across both call sites.
/// </summary>
public static class HandoffControlMessageFactory
{
    /// <summary>
    /// Build a <see cref="MessageType.HandoffCompletedRequest"/> message addressed
    /// to the subscriber that owns the pending row. The optional details payload
    /// is carried verbatim on <see cref="MessageContent.EventContent.EventJson"/>
    /// so the audit trail / completion-event consumers can read it later.
    /// </summary>
    public static Message CreateCompleted(HandoffSettlementCoordinates coords, string? detailsJson)
    {
        var content = new MessageContent();
        if (!string.IsNullOrEmpty(detailsJson))
        {
            content.EventContent = new EventContent
            {
                EventTypeId = coords.EventTypeId,
                EventJson = detailsJson,
            };
        }

        return BuildBase(coords, MessageType.HandoffCompletedRequest, content);
    }

    /// <summary>
    /// Build a <see cref="MessageType.HandoffFailedRequest"/> message addressed
    /// to the subscriber that owns the pending row. Error text + optional
    /// error-type classifier are carried on <see cref="ErrorContent"/> and
    /// rendered verbatim on the resulting Failed audit row.
    /// </summary>
    public static Message CreateFailed(HandoffSettlementCoordinates coords, string errorText, string? errorType)
    {
        var content = new MessageContent
        {
            ErrorContent = new ErrorContent
            {
                ErrorText = errorText,
                ErrorType = errorType,
            },
        };

        return BuildBase(coords, MessageType.HandoffFailedRequest, content);
    }

    private static Message BuildBase(HandoffSettlementCoordinates coords, MessageType messageType, MessageContent content) => new()
    {
        CorrelationId = coords.CorrelationId,
        EventId = coords.EventId,
        // Fresh per-message id so the audit row chain is well-formed (parent =
        // the inbound pending message, child = this control message). In the
        // production path ServiceBusMessage would auto-assign one when null,
        // but explicit assignment also makes the in-memory test path correct.
        MessageId = System.Guid.NewGuid().ToString(),
        SessionId = coords.SessionId,
        To = coords.To,
        From = Constants.ManagerId,
        OriginatingMessageId = coords.OriginatingMessageId ?? coords.ParentMessageId,
        ParentMessageId = coords.ParentMessageId,
        MessageType = messageType,
        EventTypeId = coords.EventTypeId,
        MessageContent = content,
    };
}
