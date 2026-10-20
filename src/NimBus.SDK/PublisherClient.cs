using Azure.Messaging.ServiceBus;
using NimBus.Core.CloudEvents;
using NimBus.Core.Diagnostics;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.OpenTelemetry;
using NimBus.SDK.EventHandlers;
using NimBus.SDK.Extensions;
using NimBus.ServiceBus;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.SDK;

public class PublisherClient : IPublisherClient
{
    private readonly ISender _sender;
    private readonly string? _publisherEndpoint;
    private readonly CloudEventPublisherOptions? _cloudEvents;
    private ServiceBusClient? _serviceBusClient;

    /// <summary>
    /// The raw client used by <see cref="Request{TRequest,TResponse}"/> to accept the
    /// reply session. Set by <c>AddNimBusPublisher</c> (and by <see cref="CreateAsync"/> /
    /// the obsolete client ctor) — internal so the DI factory can enable request/reply
    /// without widening the public constructor surface.
    /// </summary>
    internal ServiceBusClient? ReplyServiceBusClient
    {
        get => _serviceBusClient;
        set => _serviceBusClient = value;
    }

    // There is a 256 KB limit per message sent on Azure Service Bus (Standard
    // tier; 1 MB on Premium) and a 64 KB header limit. Our user properties are
    // significant, so half the remaining size is reserved for them as well.
    // https://docs.microsoft.com/en-us/azure/service-bus-messaging/service-bus-quotas
    internal const long MaxBatchBodyBytes = 256000 - 64000 - 128000;

    /// <summary>
    /// Creates a new PublisherClient with the specified sender.
    /// Preferred for DI registration via <see cref="Extensions.ServiceCollectionExtensions.AddNimBusPublisher(Microsoft.Extensions.DependencyInjection.IServiceCollection, string)"/>.
    /// </summary>
    /// <param name="sender">The sender to use for publishing messages.</param>
    /// <param name="publisherEndpoint">
    /// The publisher's own endpoint name (the value passed to <c>AddNimBusPublisher</c>).
    /// Stamped onto every outgoing message as <c>OriginatingFrom</c> so audit rows
    /// carry the publishing endpoint identity through the Resolver. When null, the
    /// wire default <c>"self"</c> applies.
    /// </param>
    /// <param name="cloudEvents">
    /// Optional CloudEvents publish options. When non-null every published event is
    /// emitted as a CloudEvent in the configured content mode; when null (the
    /// default) the native NimBus wire format is used.
    /// </param>
    public PublisherClient(ISender sender, string? publisherEndpoint = null, CloudEventPublisherOptions? cloudEvents = null)
    {
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _publisherEndpoint = publisherEndpoint;
        _cloudEvents = cloudEvents;
    }

    /// <summary>
    /// Creates a new PublisherClient asynchronously.
    /// </summary>
    /// <param name="client">The ServiceBusClient to use for publishing messages.</param>
    /// <param name="endpoint">The endpoint (topic name) to publish messages to.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A new PublisherClient instance.</returns>
    public static Task<PublisherClient> CreateAsync(
        ServiceBusClient client,
        string endpoint,
        CancellationToken cancellationToken = default)
    {
        if (client == null) throw new ArgumentNullException(nameof(client));
        if (string.IsNullOrEmpty(endpoint)) throw new ArgumentException("Endpoint cannot be null or empty.", nameof(endpoint));

        // Wrap with the publisher instrumentation decorator so callers using
        // the manual factory get the same NimBus.Publish span / metric surface
        // as the DI-registered AddNimBusPublisher path. The endpoint name
        // doubles as the publisher's identity for OriginatingFrom stamping.
        var serviceBusSender = client.CreateSender(endpoint);
        ISender sender = NimBusOpenTelemetryDecorators.InstrumentSender(
            new Sender(serviceBusSender), MessagingSystem.ServiceBus);

        return Task.FromResult(new PublisherClient(sender, endpoint) { _serviceBusClient = client });
    }

    public async Task Publish(IEvent @event)
    {
        await Publish(@event, @event.GetSessionId(), Guid.NewGuid().ToString());
    }

    public async Task Publish(IEvent @event, string sessionId, string correlationId)
    {
        // Pass messageId: null so GetMessageStatic derives the deterministic id
        // (and serializes the event) exactly once. Passing the same formula here
        // would serialize the event a second time to no effect.
        await Publish(@event, sessionId, correlationId, null);
    }

    public async Task Publish(IEvent @event, string sessionId, string correlationId, string? messageId)
    {
        // The publisher span is emitted by the InstrumentingSenderDecorator that
        // wraps ISender (registered via AddNimBusInstrumentation). PublisherClient
        // no longer opens its own activity — the decorator's span carries the
        // canonical messaging.* attributes and parents to Activity.Current.
        var message = GetMessage(@event, correlationId, messageId, sessionId);
        await _sender.Send(message);
    }

    /// <inheritdoc/>
    public async Task Publish(IEvent @event, string sessionId, string correlationId, string messageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var message = GetMessage(@event, correlationId, messageId, sessionId);
        await _sender.Send(message, 0, cancellationToken);
    }

    /// <inheritdoc/>
    public async Task PublishFromContext(
        IEvent @event,
        IEventHandlerContext context,
        string messageId,
        CancellationToken cancellationToken = default)
    {
        if (@event == null) throw new ArgumentNullException(nameof(@event));
        if (context == null) throw new ArgumentNullException(nameof(context));
        if (string.IsNullOrWhiteSpace(messageId))
            throw new ArgumentException("A deterministic outgoing message ID is required.", nameof(messageId));
        if (string.IsNullOrWhiteSpace(context.MessageId))
            throw new ArgumentException("The inbound context must have a message ID.", nameof(context));
        if (string.IsNullOrWhiteSpace(context.SessionId))
            throw new ArgumentException("The inbound context must have a session ID.", nameof(context));
        if (string.IsNullOrWhiteSpace(context.CorrelationId))
            throw new ArgumentException("The inbound context must have a correlation ID.", nameof(context));

        var message = (Message)GetMessage(
            @event,
            context.CorrelationId,
            messageId,
            context.SessionId);
        message.ParentMessageId = context.MessageId;
        message.OriginatingMessageId = string.IsNullOrWhiteSpace(context.OriginatingMessageId)
            || string.Equals(context.OriginatingMessageId, Constants.Self, StringComparison.OrdinalIgnoreCase)
                ? context.MessageId
                : context.OriginatingMessageId;

        await _sender.Send(message, 0, cancellationToken);
    }

    /// <inheritdoc/>
    public Task Publish(IMessage message, CancellationToken cancellationToken = default)
    {
        return _sender.Send(message, 0, cancellationToken);
    }

    /// <summary>
    /// Pre-release - use with care!
    /// Publish multiple messages at once. Make sure batch size enforced by Azure Service Bus is taken into account.
    /// </summary>
    /// <param name="events">List of events you want to publish. Make sure to make them before publishing</param>
    /// <param name="correlationId"></param>
    /// <returns></returns>
    public async Task PublishBatch(IEnumerable<IEvent> events, string? correlationId = null)
    {
        // Span emission happens in InstrumentingSenderDecorator (see Publish above).
        if (correlationId == null)
        {
            correlationId = Guid.NewGuid().ToString();
        }

        var messages = events.Select(@event => GetMessage(@event, correlationId)).ToList();
        await _sender.Send(messages);
    }

    /// <summary>
    /// Publishes any number of events, automatically split into pages that fit
    /// the Azure Service Bus batch size. Preferred over
    /// <see cref="GetBatches"/> + <see cref="PublishBatch"/>: each event is
    /// built and serialized exactly once — the serialized body is used for
    /// page sizing and stashed on the message so the transport reuses it.
    /// </summary>
    /// <param name="events">Events to publish, in order.</param>
    /// <param name="correlationId">Correlation id applied to every message; a new GUID when null.</param>
    public async Task PublishBatches(IEnumerable<IEvent> events, string? correlationId = null)
    {
        correlationId ??= Guid.NewGuid().ToString();

        var page = new List<IMessage>();
        var pageSize = 0L;
        foreach (var @event in events)
        {
            // GetMessage validates the event and serializes it once; the body
            // (serialized MessageContent) is serialized once more here and
            // reused for both sizing and the wire payload.
            var message = (Message)GetMessage(@event, correlationId);
            var serializedContent = JsonConvert.SerializeObject(message.MessageContent);
            message.SerializedMessageContent = serializedContent;
            var size = (long)System.Text.Encoding.UTF8.GetByteCount(serializedContent);

            // Greedy first-fit paging. An oversized single event still goes out
            // as its own page — Service Bus rejects it at send time, matching
            // GetBatchesStatic's contract.
            if (page.Count > 0 && pageSize + size > MaxBatchBodyBytes)
            {
                await _sender.Send(page);
                page = new List<IMessage>();
                pageSize = 0;
            }

            page.Add(message);
            pageSize += size;
        }

        if (page.Count > 0)
        {
            await _sender.Send(page);
        }
    }

    /// <summary>
    /// Schedules an event for delivery at the specified time.
    /// Returns a sequence number that can be used to cancel the scheduled message.
    /// </summary>
    public async Task<long> Schedule(IEvent @event, DateTimeOffset scheduledEnqueueTime)
    {
        var message = GetMessage(@event);
        return await _sender.ScheduleMessage(message, scheduledEnqueueTime);
    }

    /// <summary>
    /// Cancels a previously scheduled message using the sequence number returned by <see cref="Schedule"/>.
    /// </summary>
    public async Task CancelScheduled(long sequenceNumber)
    {
        await _sender.CancelScheduledMessage(sequenceNumber);
    }

    /// <summary>
    /// Sends a request and awaits a typed response with timeout.
    /// Uses Azure Service Bus sessions for reply correlation: the reply arrives on this
    /// publisher's own <c>{endpoint}-reply</c> subscription (provisioned by
    /// <c>nb topology apply</c>). Requires a PublisherClient created via
    /// <c>AddNimBusPublisher</c> or <see cref="CreateAsync"/>.
    /// </summary>
    /// <exception cref="TimeoutException">No reply arrived within <paramref name="timeout"/>.</exception>
    /// <exception cref="Core.Messages.Exceptions.RequestReplyException">The responder's handler threw; the error reply carries its type and message.</exception>
    public async Task<TResponse> Request<TRequest, TResponse>(TRequest request, TimeSpan timeout, CancellationToken cancellationToken = default)
        where TRequest : IEvent
        where TResponse : class
    {
        if (_serviceBusClient == null)
            throw new InvalidOperationException(
                "Request/response requires a ServiceBusClient. Use AddNimBusPublisher or PublisherClient.CreateAsync(client, endpoint).");
        if (string.IsNullOrEmpty(_publisherEndpoint))
            throw new InvalidOperationException(
                "Request/response requires the publisher's endpoint identity: the reply is received on the '{endpoint}-reply' subscription. Construct the client with an endpoint (AddNimBusPublisher / CreateAsync).");

        var replySessionId = Guid.NewGuid().ToString();
        var msg = (Message)GetMessage(request);
        // The reply address is this publisher's OWN topic — msg.To carries the event
        // type id for routing and would point the responder at a non-existent topic.
        msg.ReplyTo = _publisherEndpoint;
        msg.ReplyToSessionId = replySessionId;
        var message = (IMessage)msg;

        await _sender.Send(message, cancellationToken: cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        var replySubscription = ReplyConstants.ReplySubscriptionName(_publisherEndpoint);
        ServiceBusSessionReceiver? receiver = null;
        try
        {
            try
            {
                receiver = await _serviceBusClient.AcceptSessionAsync(
                    _publisherEndpoint, replySubscription, replySessionId, cancellationToken: cts.Token);
            }
            catch (ServiceBusException ex) when (ex.Reason == ServiceBusFailureReason.MessagingEntityNotFound)
            {
                throw new InvalidOperationException(
                    $"Reply subscription '{_publisherEndpoint}/{replySubscription}' does not exist. " +
                    "Provision the topology (nb topology apply) with a NimBus version that creates reply subscriptions.", ex);
            }

            var reply = await receiver.ReceiveMessageAsync(timeout, cts.Token);
            if (reply == null)
                throw new TimeoutException($"No response received within {timeout}");

            await receiver.CompleteMessageAsync(reply, cts.Token);

            if (reply.ApplicationProperties.TryGetValue(ReplyConstants.ReplyStatusProperty, out var status)
                && ReplyConstants.StatusError.Equals(status as string, StringComparison.Ordinal))
            {
                reply.ApplicationProperties.TryGetValue(ReplyConstants.ErrorTypeProperty, out var errorType);
                reply.ApplicationProperties.TryGetValue(ReplyConstants.ErrorTextProperty, out var errorText);
                throw new Core.Messages.Exceptions.RequestReplyException(errorType as string, errorText as string);
            }

            var body = reply.Body.ToString();
            return Newtonsoft.Json.JsonConvert.DeserializeObject<TResponse>(body, Constants.CreateSafeJsonSettings())!;
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"No response received within {timeout}");
        }
        finally
        {
            if (receiver != null)
                await receiver.DisposeAsync();
        }
    }

    /// <summary>
    /// Use to get batch of maximum possible size supported by Azure Service Bus
    /// </summary>
    /// <param name="events">Events you want to split into multiple batches</param>
    /// <returns>Batches of events</returns>
    public IEnumerable<IEnumerable<IEvent>> GetBatches(List<IEvent> events)
    {
        return GetBatchesStatic(events);
    }

    /// <summary>
    /// Use to get batch of maximum possible size supported by Azure Service Bus
    /// </summary>
    /// <param name="events">Events you want to split into multiple batches</param>
    /// <returns>Batches of events</returns>
    public static IEnumerable<IEnumerable<IEvent>> GetBatchesStatic(List<IEvent> events)
    {
        // Each probe builds + serializes the event once (validation included,
        // preserving the per-event Validate contract). The size of the event
        // that overflows a page is carried to the next page so no event is
        // measured twice, and no ServiceBusMessage/body-copy is materialized
        // just for sizing.
        var carriedSize = -1L;
        do
        {
            var pageSize = 0L;
            var page = events.TakeWhile(@event =>
            {
                var size = carriedSize >= 0 ? carriedSize : MeasureBodyBytes(@event);
                carriedSize = -1L;
                if (pageSize + size > MaxBatchBodyBytes)
                {
                    carriedSize = size;
                    return false;
                }

                pageSize += size;
                return true;
            }).ToList();

            if (page.Count == 0 && events.Count > 0)
            {
                // Oversized event — yield as single-item batch, let Service Bus reject at send time.
                // The carried measurement belongs to this event, so drop it.
                page = new List<IEvent> { events[0] };
                carriedSize = -1L;
            }

            events.RemoveRange(0, page.Count);
            yield return page;
        } while (events.Any());
    }

    private static long MeasureBodyBytes(IEvent @event)
    {
        var message = GetMessageStatic(@event);
        return System.Text.Encoding.UTF8.GetByteCount(JsonConvert.SerializeObject(message.MessageContent));
    }

    private IMessage GetMessage(IEvent @event, string? correlationId = null, string? messageId = null, string? sessionId = null)
    {
        var message = (Message)GetMessageStatic(@event, correlationId, messageId, sessionId);
        if (!string.IsNullOrEmpty(_publisherEndpoint))
            message.OriginatingFrom = _publisherEndpoint;

        // Opt-in CloudEvents: attach the CloudEvent publish context so the transport
        // binding emits this message as a CloudEvent. Native routing metadata built
        // above is untouched, so a NimBus round-trip keeps working.
        if (_cloudEvents != null)
            message.CloudEvent = BuildCloudEvent(@event, message);

        return message;
    }

    // Projects native NimBus identity/metadata onto a CloudEvent per the configured
    // publisher options and mapping. The CloudEvent's data is the domain-event JSON
    // (EventContent.EventJson), NOT the NimBus MessageContent envelope.
    private CloudEventPublishContext BuildCloudEvent(IEvent @event, Message message)
    {
        var eventType = @event.GetType();
        // Only called when _cloudEvents is configured (see GetMessage).
        var type = _cloudEvents!.TypeOverride?.Invoke(@event)
            ?? (_cloudEvents.TypeNameStrategy == CloudEventTypeNameStrategy.FullName
                ? eventType.FullName!
                : eventType.Name);

        var cloudEvent = new CloudEvent
        {
            Id = message.MessageId!,
            Source = _cloudEvents.Source?.ToString()!,
            Type = type,
            Subject = _cloudEvents.Subject?.Invoke(@event),
            Time = _cloudEvents.Time?.Invoke(@event) ?? DateTimeOffset.UtcNow,
            DataContentType = _cloudEvents.DataContentType,
            DataSchema = _cloudEvents.DataSchema?.ToString(),
            Data = message.MessageContent?.EventContent?.EventJson,
        };

        // Custom extension attributes first, so the mapped correlation/session ids
        // below deterministically win if the caller reused those names.
        _cloudEvents.Extensions?.Invoke(@event, cloudEvent.Extensions);

        var mapping = _cloudEvents.Mapping ?? new CloudEventMapping();
        mapping.WriteCorrelationId(cloudEvent, message.CorrelationId);
        mapping.WriteSessionId(cloudEvent, message.SessionId);

        return new CloudEventPublishContext(cloudEvent, _cloudEvents.ContentMode);
    }

    private static IMessage GetMessageStatic(IEvent @event, string? correlationId = null, string? messageId = null, string? sessionId = null)
    {
        @event.Validate();

        var eventType = @event.GetEventType().Id;
        var messagePayload = JsonConvert.SerializeObject(@event);
        messageId ??= $"{eventType}-{DeterministicHash(messagePayload)}";
        sessionId ??= @event.GetSessionId();
        correlationId ??= Guid.NewGuid().ToString();
        var message = new Message()
        {
            To = eventType,
            EventTypeId = eventType,
            SessionId = sessionId,
            CorrelationId = correlationId,
            MessageId = messageId,
            RetryCount = 0,
            MessageType = MessageType.EventRequest,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventType,
                    EventJson = messagePayload
                }
            }
        };
        // Trace context is captured by MessageHelper.ToServiceBusMessage at send
        // time via W3CMessagePropagator (traceparent / tracestate). The legacy
        // IMessage.DiagnosticId property is no longer populated or read.
        return message;
    }

    private static string DeterministicHash(string input)
    {
        var hash = System.IO.Hashing.XxHash64.HashToUInt64(
            System.Text.Encoding.UTF8.GetBytes(input));
        return hash.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }
}
