using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using Newtonsoft.Json;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.EventHandlers;


public class EventJsonHandler<TEvent> : IEventJsonHandler
    where TEvent : IEvent
{
    public EventJsonHandler(IEventHandler<TEvent> eventHandler)
    {
        _eventHandler = eventHandler;
    }

    private readonly IEventHandler<TEvent> _eventHandler;

    public Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        TEvent @event;
        try
        {
            var eventJson = context.MessageContent?.EventContent?.EventJson
                ?? throw new JsonSerializationException(
                    $"Event payload for '{context.EventTypeId}' is missing.");
            @event = JsonConvert.DeserializeObject<TEvent>(
                    eventJson,
                    Constants.CreateSafeJsonSettings())
                ?? throw new JsonSerializationException(
                    $"Event payload for '{context.EventTypeId}' deserialized to null.");
        }
        catch (JsonException exception)
        {
            // Invalid wire payloads cannot become valid through retry. Normalize
            // every Newtonsoft parse/serialization failure to the lifecycle's
            // provider-independent permanent-failure signal.
            throw new PermanentFailureException(exception);
        }

        var eventHandlercontext = new EventHandlerContext(context)
        {
            CorrelationId = context.CorrelationId,
            EventId = context.EventId,
            EventType = context.EventTypeId,
            MessageId = context.MessageId,
            SessionId = context.SessionId,
            ParentMessageId = context.ParentMessageId,
            // "self" is an internal wire sentinel ("this message IS the
            // origin"). Resolve it here so handler authors can persist
            // OriginatingMessageId as handoff settlement coordinates and
            // keep true lineage across retry/resubmission replays.
            OriginatingMessageId = string.Equals(context.OriginatingMessageId, Constants.Self, StringComparison.OrdinalIgnoreCase)
                ? context.MessageId
                : context.OriginatingMessageId,
        };
        return _eventHandler.Handle(@event, eventHandlercontext, cancellationToken);
    }
}
