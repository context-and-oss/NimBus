using Microsoft.Extensions.DependencyInjection;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK.EventHandlers;

public class EventHandlerProvider : IEventContextHandler
{
    private readonly ConcurrentDictionary<string, Func<IServiceProvider?, IEventJsonHandler>> _handlerBuilders;
    private readonly IServiceScopeFactory? _scopeFactory;
    private Func<IServiceProvider?, IEventJsonHandler>? _fallbackBuilder;

    public EventHandlerProvider()
    {
        _handlerBuilders = new ConcurrentDictionary<string, Func<IServiceProvider?, IEventJsonHandler>>();
    }

    internal EventHandlerProvider(IServiceScopeFactory scopeFactory)
        : this()
    {
        _scopeFactory = scopeFactory ?? throw new ArgumentNullException(nameof(scopeFactory));
    }

    /// <summary>
    /// Handles an <see cref="IMessageContext"/> regardless of when, how, or why the event message was sent.
    /// </summary>
    public async Task Handle(IMessageContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        var eventTypeId = context.EventTypeId;
        if (string.IsNullOrEmpty(eventTypeId))
        {
            throw new EventHandlerNotFoundException("Message does not define an EventTypeId.");
        }

        string? bodyEventTypeId;
        try
        {
            bodyEventTypeId = context.MessageContent?.EventContent?.EventTypeId;
        }
        catch (InvalidMessageException exception)
        {
            // A known routed type with an unreadable body is malformed input,
            // not a transient handler failure.
            throw new PermanentFailureException(exception);
        }

        if (!string.IsNullOrEmpty(bodyEventTypeId)
            && !string.Equals(eventTypeId, bodyEventTypeId, StringComparison.Ordinal))
        {
            throw new PermanentFailureException(new InvalidOperationException(
                $"Message EventTypeId mismatch: authoritative context value '{eventTypeId}' " +
                $"does not match body value '{bodyEventTypeId}'."));
        }

        if (_scopeFactory == null)
        {
            await GetHandler(eventTypeId, serviceProvider: null).Handle(context, cancellationToken);
            return;
        }

        // Handlers and their scoped dependencies live for exactly one message.
        // Async disposal is required for dependencies that implement only
        // IAsyncDisposable (database contexts and clients commonly do).
        await using var scope = _scopeFactory.CreateAsyncScope();
        await GetHandler(eventTypeId, scope.ServiceProvider).Handle(context, cancellationToken);
    }

    public void RegisterHandler<TEvent>(Func<IEventHandler<TEvent>> eventHandlerFactory)
        where TEvent : IEvent
    {
        RegisterHandler(typeof(TEvent), eventHandlerFactory);
    }

    public void RegisterHandler(Type eventType, Func<object> eventHandlerFactory)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        ArgumentNullException.ThrowIfNull(eventHandlerFactory);
        if (!typeof(IEvent).IsAssignableFrom(eventType))
            throw new ArgumentException($"Event type '{eventType.FullName}' must implement {nameof(IEvent)}.", nameof(eventType));

        RegisterHandlerCore(eventType, _ => eventHandlerFactory.Invoke());
    }

    internal void RegisterHandler(Type eventType, Func<IServiceProvider?, object> eventHandlerFactory)
    {
        ArgumentNullException.ThrowIfNull(eventHandlerFactory);
        RegisterHandlerCore(eventType, eventHandlerFactory);
    }

    /// <summary>
    /// Registers a fallback handler invoked when no <c>EventTypeId</c>-specific handler is
    /// registered. Used by the Mapping Executor (spec 023) to handle every message arriving
    /// at the Mapping Zone and decide from the mapping registry per message.
    /// </summary>
    public void RegisterFallbackHandler(Func<IEventJsonHandler> fallbackFactory)
    {
        ArgumentNullException.ThrowIfNull(fallbackFactory);
        _fallbackBuilder = _ => fallbackFactory.Invoke();
    }

    internal void RegisterFallbackHandler(Func<IServiceProvider?, IEventJsonHandler> fallbackFactory)
    {
        _fallbackBuilder = fallbackFactory ?? throw new ArgumentNullException(nameof(fallbackFactory));
    }

    /// <summary>
    /// Registers a handler for a <em>dynamically-typed</em> event keyed directly by its
    /// <paramref name="eventTypeId"/> string, with no compiled <see cref="IEvent"/> class.
    /// Used for agent-defined event types (e.g. <c>crm.contact.enriched.v1</c>) whose contract
    /// is a registered JSON Schema rather than code. The factory typically returns a
    /// <see cref="DelegateEventJsonHandler"/>.
    /// </summary>
    public void RegisterHandler(string eventTypeId, Func<IEventJsonHandler> eventJsonHandlerFactory)
    {
        if (string.IsNullOrWhiteSpace(eventTypeId))
            throw new ArgumentException("Event type id must not be null or empty.", nameof(eventTypeId));
        ArgumentNullException.ThrowIfNull(eventJsonHandlerFactory);

        _handlerBuilders[eventTypeId] = _ => eventJsonHandlerFactory.Invoke();
    }

    internal void RegisterHandler(string eventTypeId, Func<IServiceProvider?, IEventJsonHandler> eventJsonHandlerFactory)
    {
        if (string.IsNullOrWhiteSpace(eventTypeId))
            throw new ArgumentException("Event type id must not be null or empty.", nameof(eventTypeId));
        ArgumentNullException.ThrowIfNull(eventJsonHandlerFactory);

        _handlerBuilders[eventTypeId] = eventJsonHandlerFactory;
    }

    private void RegisterHandlerCore(Type eventType, Func<IServiceProvider?, object> eventHandlerFactory)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        if (!typeof(IEvent).IsAssignableFrom(eventType))
            throw new ArgumentException($"Event type '{eventType.FullName}' must implement {nameof(IEvent)}.", nameof(eventType));

        IEventJsonHandler BuildEventJsonHandler(IServiceProvider? serviceProvider)
        {
            var eventHandler = eventHandlerFactory.Invoke(serviceProvider);
            var adapterType = typeof(EventJsonHandler<>).MakeGenericType(eventType);
            return (IEventJsonHandler)(Activator.CreateInstance(adapterType, eventHandler)
                ?? throw new InvalidOperationException($"Could not create handler adapter for event type '{eventType.FullName}'."));
        }

        var eventTypeId = new EventType(eventType).Id;
        _handlerBuilders[eventTypeId] = BuildEventJsonHandler;
    }

    private IEventJsonHandler GetHandler(string eventTypeId, IServiceProvider? serviceProvider)
    {
        if (_handlerBuilders.TryGetValue(eventTypeId, out var factory))
            return factory.Invoke(serviceProvider);
        if (_fallbackBuilder != null)
            return _fallbackBuilder.Invoke(serviceProvider);
        throw new EventHandlerNotFoundException($"Event handler not registered for Event type {eventTypeId}");
    }
}
