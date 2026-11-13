using System;
using System.Threading;
using System.Threading.Tasks;
using NimBus.Core.Events;
using NimBus.Core.Messages.Exceptions;

namespace NimBus.SDK.EventHandlers;

/// <summary>
/// The built-in handler of an unmatched consumer (Spec 045 §6): every message it receives has a
/// routing key no endpoint claims, or no key, so it is dead-lettered with a reason naming the
/// key. The message then shows in the WebApp like any other dead-lettered message. Register it
/// with <c>subscriber.AddUnmatchedHandler&lt;TEvent&gt;()</c>.
/// </summary>
/// <typeparam name="TEvent">The keyed event type.</typeparam>
public sealed class UnmatchedRoutingKeyHandler<TEvent> : IEventHandler<TEvent>
    where TEvent : IEvent
{
    /// <inheritdoc />
    /// <exception cref="PermanentFailureException">Always, wrapping a <see cref="RoutingKeyRejectedException"/>.</exception>
    public Task Handle(TEvent message, IEventHandlerContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        var key = (message as IRoutingKeyProvider)?.GetRoutingKey();
        var keyText = key is null ? "with no routing key" : $"with routing key '{key}'";
        throw new PermanentFailureException(
            new RoutingKeyRejectedException($"No endpoint consumes {typeof(TEvent).Name} {keyText}."));
    }
}
