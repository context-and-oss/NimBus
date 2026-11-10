using Azure.Messaging.ServiceBus;
using NimBus.Core.CloudEvents;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using Microsoft.Azure.Functions.Worker;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.ServiceBus;

public interface IServiceBusAdapter
{
    /// <summary>
    /// Handles a session message using only <see cref="ServiceBusSessionMessageActions"/>.
    /// </summary>
    /// <remarks>
    /// <see cref="ServiceBusSessionMessageActions"/> can read and write session state but cannot
    /// settle messages. After the handler runs, completing the message throws; the failure is
    /// reported to the Resolver as a dead-letter ErrorResponse, the dead-letter call also fails and
    /// is swallowed, and the message stays unsettled (and is redelivered) unless the host
    /// auto-completes it. Bind <see cref="ServiceBusMessageActions"/> in the trigger and call
    /// <see cref="Handle(ServiceBusReceivedMessage, ServiceBusMessageActions, ServiceBusSessionMessageActions, CancellationToken)"/>
    /// instead.
    /// </remarks>
    /// <param name="message">The received message.</param>
    /// <param name="sessionActions">The session actions used for session state.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    [Obsolete("ServiceBusSessionMessageActions cannot complete or dead-letter messages. Bind ServiceBusMessageActions in the trigger and call Handle(message, messageActions, sessionActions, cancellationToken). This overload will be removed in v5.")]
    Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles a session message from an Azure Functions isolated-worker Service Bus trigger. This is
    /// the overload to use from the isolated worker: the message is settled (completed or
    /// dead-lettered) through <paramref name="messageActions"/>, while session state is read and
    /// written through <paramref name="sessionActions"/>.
    /// </summary>
    /// <param name="message">The received message.</param>
    /// <param name="messageActions">The message actions used to complete or dead-letter the message.</param>
    /// <param name="sessionActions">The session actions used for session state.</param>
    /// <param name="cancellationToken">A token to cancel the operation.</param>
    Task Handle(ServiceBusReceivedMessage message, ServiceBusMessageActions messageActions, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default);
    Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionReceiver sessionReceiver, CancellationToken cancellationToken = default);
    Task Handle(ProcessSessionMessageEventArgs args, CancellationToken cancellationToken = default);
}

public class ServiceBusAdapter : IServiceBusAdapter
{
    private readonly IMessageHandler _messageHandler;
    private readonly ServiceBusClient? _serviceBusClient;
    private readonly string? _entityPath;
    private readonly CloudEventReadOptions? _cloudEventReadOptions;

    /// <summary>
    /// Creates a new ServiceBusAdapter.
    /// </summary>
    /// <param name="messageHandler">The message handler to process messages.</param>
    /// <param name="serviceBusClient">Optional ServiceBusClient used to schedule retries in the isolated worker model.
    /// Inject it via dependency injection.</param>
    /// <param name="entityPath">Optional entity path (queue name or topic/subscription path) used to schedule retries.</param>
    /// <param name="cloudEventReadOptions">Optional CloudEvents consume options. When set, inbound
    /// CloudEvents are detected and normalized; when null (default) the adapter is pure native NimBus.</param>
    public ServiceBusAdapter(IMessageHandler messageHandler, ServiceBusClient? serviceBusClient = null, string? entityPath = null, CloudEventReadOptions? cloudEventReadOptions = null)
    {
        _messageHandler = messageHandler ?? throw new ArgumentNullException(nameof(messageHandler));
        _serviceBusClient = serviceBusClient;
        _entityPath = entityPath;
        _cloudEventReadOptions = cloudEventReadOptions;
    }

    /// <inheritdoc />
    [Obsolete("ServiceBusSessionMessageActions cannot complete or dead-letter messages. Bind ServiceBusMessageActions in the trigger and call Handle(message, messageActions, sessionActions, cancellationToken). This overload will be removed in v5.")]
    public async Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default)
    {
        var messageWrapper = new ServiceBusMessage(message);
        var sessionWrapper = new ServiceBusSession(sessionActions, _serviceBusClient, _entityPath, message.SessionId);
        var messageContext = new MessageContext(messageWrapper, sessionWrapper, isDeferred: false, _cloudEventReadOptions);

        await HandleWithLatencyTracking(message, messageContext, cancellationToken);
    }

    /// <inheritdoc />
    public async Task Handle(ServiceBusReceivedMessage message, ServiceBusMessageActions messageActions, ServiceBusSessionMessageActions sessionActions, CancellationToken cancellationToken = default)
    {
        var messageWrapper = new ServiceBusMessage(message);
        var sessionWrapper = new ServiceBusSession(messageActions, sessionActions, _serviceBusClient, _entityPath, message.SessionId);
        var messageContext = new MessageContext(messageWrapper, sessionWrapper, isDeferred: false, _cloudEventReadOptions);

        await HandleWithLatencyTracking(message, messageContext, cancellationToken);
    }

    public async Task Handle(ServiceBusReceivedMessage message, ServiceBusSessionReceiver sessionReceiver, CancellationToken cancellationToken = default)
    {
        var messageWrapper = new ServiceBusMessage(message);
        var sessionWrapper = new ServiceBusSession(sessionReceiver);
        var messageContext = new MessageContext(messageWrapper, sessionWrapper, isDeferred: false, _cloudEventReadOptions);

        await HandleWithLatencyTracking(message, messageContext, cancellationToken);
    }

    public async Task Handle(ProcessSessionMessageEventArgs args, CancellationToken cancellationToken = default)
    {
        var messageWrapper = new ServiceBusMessage(args.Message);
        var sessionWrapper = new ServiceBusSession(args, _serviceBusClient, _entityPath);
        var messageContext = new MessageContext(messageWrapper, sessionWrapper, isDeferred: false, _cloudEventReadOptions);

        await HandleWithLatencyTracking(args.Message, messageContext, cancellationToken);
    }

    private async Task HandleWithLatencyTracking(ServiceBusReceivedMessage message, MessageContext messageContext, CancellationToken cancellationToken)
    {
        var eventType = message.ApplicationProperties.TryGetValue("EventTypeId", out var et) ? et?.ToString() ?? "unknown" : "unknown";
        var destination = message.ApplicationProperties.TryGetValue("To", out var to) ? to?.ToString() ?? "unknown" : "unknown";

        // Extract W3C trace context from inbound message and stash it on the
        // context. NimBusConsumerInstrumentation reads this and starts the
        // consumer span with it as the parent.
        var traceParent = message.ApplicationProperties.TryGetValue(W3CMessagePropagator.TraceParentHeader, out var tp)
            ? tp?.ToString()
            : null;
        var traceState = message.ApplicationProperties.TryGetValue(W3CMessagePropagator.TraceStateHeader, out var ts)
            ? ts?.ToString()
            : null;
        messageContext.ParentTraceContext = W3CMessagePropagator.TryParse(traceParent, traceState);

        var queueWaitMs = Math.Max(0, (DateTime.UtcNow - message.EnqueuedTime.UtcDateTime).TotalMilliseconds);
        var transportTags = new System.Diagnostics.TagList
        {
            { MessagingAttributes.System, MessagingSystem.ServiceBus },
            { MessagingAttributes.DestinationName, destination },
            { MessagingAttributes.NimBusEventType, eventType },
        };
        NimBusMeters.QueueWait.Record(queueWaitMs, transportTags);

        // Stash on the context so ResponseService can copy it onto the
        // outgoing response message and the Resolver can persist it.
        messageContext.QueueTimeMs = (long)queueWaitMs;
        // Marker for processing-time computation. The terminal handler sends
        // the response INSIDE the pipeline, so any "set ProcessingTimeMs in
        // middleware after await next()" approach happens after the response
        // already left. Reading "now − HandlerStartedAtUtc" at response-build
        // time gives the correct elapsed without that ordering issue.
        messageContext.HandlerStartedAtUtc = DateTime.UtcNow;

        try
        {
            // Consumer span + received/processed counters + duration histogram
            // are owned at the transport boundary so every subscriber path is
            // covered, regardless of whether a host has registered a pipeline.
            // The span lifetime covers any broker-settle calls the handler
            // makes (FR-012) because the handler runs inside the helper.
            await NimBusConsumerInstrumentation.RunAsync(
                messageContext,
                MessagingSystem.ServiceBus,
                ct => _messageHandler.Handle(messageContext, ct),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            var e2eMs = Math.Max(0, (DateTime.UtcNow - message.EnqueuedTime.UtcDateTime).TotalMilliseconds);
            NimBusMeters.EndToEndLatency.Record(e2eMs, transportTags);
        }
    }
}
