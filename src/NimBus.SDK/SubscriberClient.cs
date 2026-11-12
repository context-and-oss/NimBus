using Azure.Messaging.ServiceBus;
using NimBus.Core.Diagnostics;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.OpenTelemetry;
using NimBus.SDK.EventHandlers;
using NimBus.ServiceBus;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK;

public class SubscriberClient : ISubscriberClient
{
    private readonly IServiceBusAdapter _serviceBusAdapter;
    private readonly EventHandlerProvider _eventHandlerProvider;

    /// <summary>
    /// Creates a new SubscriberClient with the specified adapter and handler provider.
    /// Used internally by DI registration via <see cref="Extensions.ServiceCollectionExtensions.AddNimBusSubscriber(Microsoft.Extensions.DependencyInjection.IServiceCollection, string, System.Action{Extensions.NimBusSubscriberBuilder})"/>.
    /// For manual creation, use <see cref="CreateAsync"/> instead.
    /// </summary>
    internal SubscriberClient(IServiceBusAdapter serviceBusAdapter, EventHandlerProvider eventHandlerProvider)
    {
        _serviceBusAdapter = serviceBusAdapter ?? throw new ArgumentNullException(nameof(serviceBusAdapter));
        _eventHandlerProvider = eventHandlerProvider ?? throw new ArgumentNullException(nameof(eventHandlerProvider));
    }

    /// <summary>
    /// Creates a new SubscriberClient asynchronously.
    /// </summary>
    /// <param name="client">The ServiceBusClient to use for sending responses.</param>
    /// <param name="endpoint">The endpoint (topic name) to send responses to.</param>
    /// <param name="entityPath">Optional entity path (queue name or topic/subscription). Required in the
    /// isolated worker model for scheduling retries.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A new SubscriberClient instance.</returns>
    public static Task<SubscriberClient> CreateAsync(
        ServiceBusClient client,
        string endpoint,
        string? entityPath = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        if (string.IsNullOrEmpty(endpoint)) throw new ArgumentException("Endpoint cannot be null or empty.", nameof(endpoint));

        // Wrap with the publisher instrumentation decorator so responses
        // sent by ResponseService (ResolutionResponse, ErrorResponse, etc.)
        // emit NimBus.Publish spans and the publisher metric surface.
        // Without this wrapper the response leg of every consumer would
        // be invisible to OTel even when the consumer span is open.
        var serviceBusSender = client.CreateSender(endpoint);
        ISender sender = NimBusOpenTelemetryDecorators.InstrumentSender(
            new Sender(serviceBusSender), MessagingSystem.ServiceBus);
        var responseService = new ResponseService(sender);
        var eventHandlerProvider = new EventHandlerProvider();

        IMessageHandler strictMessageHandler = new StrictMessageHandler(eventHandlerProvider, responseService, NullLogger.Instance);

        var serviceBusAdapter = new ServiceBusAdapter(strictMessageHandler, client, entityPath);

        return Task.FromResult(new SubscriberClient(serviceBusAdapter, eventHandlerProvider));
    }

    /// <inheritdoc />
    [Obsolete("ServiceBusSessionMessageActions cannot complete or dead-letter messages. Bind ServiceBusMessageActions in the trigger and call Handle(message, messageActions, sessionActions, cancellationToken). This overload will be removed in v5.")]
    public Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default) =>
        _serviceBusAdapter.Handle(message, sessionActions, cancellationToken);

    public Task Handle(ServiceBusReceivedMessage message, ServiceBusMessageActions messageActions, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default) =>
        _serviceBusAdapter.Handle(message, messageActions, sessionActions, cancellationToken);

    public Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionReceiver sessionReceiver, CancellationToken cancellationToken = default) =>
        _serviceBusAdapter.Handle(message, sessionReceiver, cancellationToken);

    public Task Handle(ProcessSessionMessageEventArgs args, CancellationToken cancellationToken = default) =>
        _serviceBusAdapter.Handle(args, cancellationToken);

    public void RegisterHandler<TEvent>(Func<IEventHandler<TEvent>> eventHandlerFactory)
        where TEvent : IEvent
    {
        _eventHandlerProvider.RegisterHandler(eventHandlerFactory);
    }
}
