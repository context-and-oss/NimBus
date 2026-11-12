using Microsoft.Extensions.Logging.Abstractions;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Testing;

public class NimBusTestFixture
{
    private readonly InMemoryMessageBus _publishBus;
    private readonly InMemoryMessageBus _responseBus;
    private readonly EventHandlerProvider _eventHandlerProvider;
    private readonly IMessageHandler _messageHandler;

    public PublisherClient Publisher { get; }
    public InMemoryMessageBus PublishBus => _publishBus;
    public InMemoryMessageBus ResponseBus => _responseBus;

    public NimBusTestFixture()
    {
        _publishBus = new InMemoryMessageBus();
        _responseBus = new InMemoryMessageBus();

        Publisher = new PublisherClient(_publishBus);

        _eventHandlerProvider = new EventHandlerProvider();
        var responseService = new ResponseService(_responseBus);

        _messageHandler = new StrictMessageHandler(
            _eventHandlerProvider,
            responseService,
            NullLogger.Instance);
    }

    public NimBusTestFixture(IRetryPolicyProvider retryPolicyProvider)
    {
        _publishBus = new InMemoryMessageBus();
        _responseBus = new InMemoryMessageBus();

        Publisher = new PublisherClient(_publishBus);

        _eventHandlerProvider = new EventHandlerProvider();
        var responseService = new ResponseService(_responseBus);

        _messageHandler = new StrictMessageHandler(
            _eventHandlerProvider,
            responseService,
            NullLogger.Instance,
            retryPolicyProvider);
    }

    public void RegisterHandler<TEvent>(Func<IEventHandler<TEvent>> handlerFactory) where TEvent : IEvent
    {
        _eventHandlerProvider.RegisterHandler(handlerFactory);
    }

    /// <summary>
    /// Replies captured from request handlers registered via
    /// <see cref="RegisterRequestHandler{TRequest,TResponse}"/>.
    /// </summary>
    public InMemoryReplyDispatcher ReplyDispatcher { get; } = new();

    /// <summary>
    /// Registers a request/reply handler. Requests delivered through the fixture are
    /// dispatched to the handler and its response (or error) is captured on
    /// <see cref="ReplyDispatcher"/> when the request message carries a ReplyTo address.
    /// The live <c>PublisherClient.Request</c> session receive is not supported in-memory —
    /// drive requests by publishing the request event and asserting on captured replies.
    /// </summary>
    /// <typeparam name="TRequest">The request event type.</typeparam>
    /// <typeparam name="TResponse">The response type.</typeparam>
    /// <param name="handlerFactory">Factory invoked per message to create the handler.</param>
    public void RegisterRequestHandler<TRequest, TResponse>(Func<IRequestHandler<TRequest, TResponse>> handlerFactory)
        where TRequest : IEvent
        where TResponse : class
    {
        ArgumentNullException.ThrowIfNull(handlerFactory);

        var eventTypeId = new EventType(typeof(TRequest)).Id;
        _eventHandlerProvider.RegisterHandler(
            eventTypeId,
            () => new RequestJsonHandler<TRequest, TResponse>(handlerFactory(), ReplyDispatcher));
    }

    public Task DeliverAll(CancellationToken cancellationToken = default)
    {
        return _publishBus.DeliverAll(_messageHandler, cancellationToken);
    }

    public Task<List<InMemoryDeliveryResult>> DeliverAllWithResults(CancellationToken cancellationToken = default)
    {
        return _publishBus.DeliverAllWithResults(_messageHandler, cancellationToken);
    }
}
