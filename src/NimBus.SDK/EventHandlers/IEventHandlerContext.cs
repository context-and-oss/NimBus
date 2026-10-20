using System;
using NimBus.Core.CloudEvents;
using NimBus.Core.Messages;

namespace NimBus.SDK.EventHandlers;

public interface IEventHandlerContext
{
    /// <summary>
    /// The Id of the currently processed message.
    /// </summary>
    string MessageId { get; }

    string EventId { get; }

    string EventType { get; }

    string CorrelationId { get; }

    /// <summary>
    /// Gets the inbound session identifier used for ordered processing.
    /// The default implementation returns <c>null</c> so existing custom
    /// context implementations remain compatible.
    /// </summary>
    string SessionId => null;

    /// <summary>
    /// Gets the identifier of the message that caused the inbound message.
    /// A value of <see cref="Constants.Self"/> indicates that legacy inbound
    /// metadata did not identify a parent. The default implementation returns
    /// <c>null</c> so existing custom context implementations remain compatible.
    /// </summary>
    string ParentMessageId => null;

    /// <summary>
    /// Gets the identifier of the message that initiated the inbound message's
    /// lineage. A value of <see cref="Constants.Self"/> indicates that legacy
    /// inbound metadata did not identify an origin. The default implementation
    /// returns <c>null</c> so existing custom context implementations remain
    /// compatible.
    /// </summary>
    string OriginatingMessageId => null;

    /// <summary>
    /// The outcome signalled by the handler. Defaults to
    /// <see cref="HandlerOutcome.Default"/>; flips to
    /// <see cref="HandlerOutcome.PendingHandoff"/> after
    /// <see cref="MarkPendingHandoff"/> is called.
    /// </summary>
    HandlerOutcome Outcome { get; }

    /// <summary>
    /// Metadata supplied by the handler via <see cref="MarkPendingHandoff"/>.
    /// Null when <see cref="Outcome"/> is <see cref="HandlerOutcome.Default"/>.
    /// </summary>
    HandoffMetadata? HandoffMetadata { get; }

    /// <summary>
    /// Signals that the handler has handed work off to a long-running
    /// external system. The subscriber will send a PendingHandoffResponse
    /// to the Resolver and block the session until the Manager settles
    /// the message via IHandoffClient.CompleteAsync or FailAsync. Idempotent — if
    /// called multiple times, the last call wins.
    /// </summary>
    /// <param name="reason">Free-text reason describing why the handler is handing off (required).</param>
    /// <param name="externalJobId">Optional external-system identifier (e.g. a DMF job id).</param>
    /// <param name="expectedBy">Optional duration after which the work is expected to settle.</param>
    void MarkPendingHandoff(string reason, string? externalJobId = null, TimeSpan? expectedBy = null);

    /// <summary>
    /// Returns the inbound CloudEvent when this message was received as a
    /// CloudEvent (via a CloudEvents-enabled or AutoDetect subscriber), exposing
    /// its <c>id</c>, <c>source</c>, <c>type</c>, <c>subject</c>, <c>time</c>,
    /// <c>datacontenttype</c> and extension attributes. Returns <c>null</c> for a
    /// native NimBus message. The default implementation returns <c>null</c> so
    /// existing context implementers are forward-compatible.
    /// </summary>
    CloudEvent GetCloudEvent() => null;
}

public class EventHandlerContext : IEventHandlerContext
{
    private readonly IMessageContext _messageContext;

    public EventHandlerContext()
    {
    }

    public EventHandlerContext(IMessageContext messageContext)
    {
        _messageContext = messageContext;
    }

    /// <summary>
    /// The Id of the currently processed message.
    /// </summary>
    public string MessageId { get; set; }

    public string EventId { get; set; }

    public string EventType { get; set; }

    public string CorrelationId { get; set; }

    /// <inheritdoc/>
    public string SessionId { get; set; }

    /// <inheritdoc/>
    public string ParentMessageId { get; set; }

    /// <inheritdoc/>
    public string OriginatingMessageId { get; set; }

    public HandlerOutcome Outcome { get; private set; }

    public HandoffMetadata? HandoffMetadata { get; private set; }

    public void MarkPendingHandoff(string reason, string? externalJobId = null, TimeSpan? expectedBy = null)
    {
        Outcome = HandlerOutcome.PendingHandoff;
        HandoffMetadata = new HandoffMetadata(reason, externalJobId, expectedBy);

        if (_messageContext != null)
        {
            _messageContext.HandlerOutcome = HandlerOutcome.PendingHandoff;
            _messageContext.HandoffMetadata = HandoffMetadata;
        }
    }

    /// <inheritdoc/>
    public CloudEvent GetCloudEvent() => _messageContext?.GetCloudEvent();
}
