using NimBus.Core.Messages;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.EventHandlers;

/// <summary>
/// A reusable <see cref="IEventJsonHandler"/> that <em>parks</em> a dynamically-typed event as
/// <see cref="HandlerOutcome.PendingHandoff"/> instead of handling it inline. It is the
/// consumer-side counterpart of <see cref="DelegateEventJsonHandler"/> for the spec 022 Agent
/// Zone: when an agent-targeted event arrives, the subscriber records it pending, blocks the
/// session, and completes the Service Bus message so an external agent can pull it later via
/// REST (<c>/api/agent/receive</c>) and settle it (<c>/settle</c>). No Service Bus lock is held.
/// </summary>
/// <remarks>
/// Reuses the existing pending-handoff plumbing 100%: it constructs an
/// <see cref="EventHandlerContext"/> over the raw <see cref="IMessageContext"/> — exactly as
/// <see cref="EventJsonHandler{TEvent}"/> does for typed handlers — and calls
/// <see cref="IEventHandlerContext.MarkPendingHandoff(string, string, System.TimeSpan?)"/>, which
/// writes the <see cref="HandlerOutcome.PendingHandoff"/> outcome and <see cref="HandoffMetadata"/>
/// through to the underlying <see cref="IMessageContext"/>. <c>StrictMessageHandler</c> observes
/// that outcome after the handler returns and emits a PendingHandoffResponse, blocks the session,
/// and completes the message.
/// </remarks>
public sealed class MarkPendingHandoffJsonHandler : IEventJsonHandler
{
    private readonly string _reason;

    public MarkPendingHandoffJsonHandler(string reason = "Awaiting agent pickup") => _reason = reason;

    public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
    {
        // Mirror EventJsonHandler<T>: wrap the raw IMessageContext in an EventHandlerContext and
        // signal the handoff. MarkPendingHandoff writes HandlerOutcome.PendingHandoff and the
        // HandoffMetadata onto the underlying IMessageContext, which StrictMessageHandler reads.
        new EventHandlerContext(context).MarkPendingHandoff(_reason);
        return Task.CompletedTask;
    }
}
