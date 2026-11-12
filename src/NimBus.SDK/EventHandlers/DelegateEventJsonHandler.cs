using NimBus.Core.Messages;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.EventHandlers;

/// <summary>
/// An <see cref="IEventJsonHandler"/> for a <em>dynamically-typed</em> event — one
/// identified only by its <c>EventTypeId</c> string with a JSON body and no compiled
/// <see cref="NimBus.Core.Events.IEvent"/> class. Unlike <see cref="EventJsonHandler{TEvent}"/>,
/// it does not deserialize into a CLR type; the raw event JSON is available to the
/// callback via <c>context.MessageContent.EventContent.EventJson</c> (and the authoritative
/// type id via <c>context.EventTypeId</c>).
/// </summary>
/// <remarks>
/// Register via <see cref="EventHandlerProvider.RegisterHandler(string, Func{IEventJsonHandler})"/>.
/// This is the consumer-side counterpart to publishing an event whose contract is a
/// registered JSON Schema rather than compiled code.
/// </remarks>
public sealed class DelegateEventJsonHandler : IEventJsonHandler
{
    private readonly Func<IMessageContext, CancellationToken, Task> _handle;

    public DelegateEventJsonHandler(Func<IMessageContext, CancellationToken, Task> handle)
    {
        _handle = handle ?? throw new ArgumentNullException(nameof(handle));
    }

    public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
    {
        return _handle(context, cancellationToken);
    }
}
