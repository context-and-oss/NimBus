using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Core.Outbox;

/// <summary>
/// An ISender decorator that writes messages to the transactional outbox
/// instead of sending them directly to Service Bus.
/// When configured, PublisherClient uses this sender transparently.
/// </summary>
public class OutboxSender : ISender
{
    private readonly IOutbox _outbox;

    public OutboxSender(IOutbox outbox)
    {
        _outbox = outbox ?? throw new ArgumentNullException(nameof(outbox));
    }

    public async Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        var outboxMessage = ToOutboxMessage(message, messageEnqueueDelay);
        using var activity = StartEnqueueSpan(outboxMessage.To, outboxMessage.EventTypeId, count: 1);
        await _outbox.StoreAsync(outboxMessage, cancellationToken);
        NimBusMeters.OutboxEnqueued.Add(1, BuildEnqueueTags(outboxMessage.To, outboxMessage.EventTypeId));
    }

    public async Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
    {
        var outboxMessages = messages.Select(m => ToOutboxMessage(m, messageEnqueueDelay)).ToList();
        var representative = outboxMessages.FirstOrDefault();
        using var activity = StartEnqueueSpan(representative?.To, representative?.EventTypeId, count: outboxMessages.Count);
        await _outbox.StoreBatchAsync(outboxMessages, cancellationToken);
        // Group by (endpoint, event type) so counter increments carry both
        // the nimbus.endpoint and nimbus.event_type dimensions correctly when
        // a single batch crosses endpoints or event types.
        foreach (var grouped in outboxMessages.GroupBy(m => (m.To, m.EventTypeId)))
        {
            NimBusMeters.OutboxEnqueued.Add(grouped.Count(), BuildEnqueueTags(grouped.Key.To, grouped.Key.EventTypeId));
        }
    }

    public async Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
    {
        var outboxMessage = ToOutboxMessage(message, 0);
        outboxMessage.ScheduledEnqueueTimeUtc = scheduledEnqueueTime.UtcDateTime;
        using var activity = StartEnqueueSpan(outboxMessage.To, outboxMessage.EventTypeId, count: 1);
        await _outbox.StoreAsync(outboxMessage, cancellationToken);
        NimBusMeters.OutboxEnqueued.Add(1, BuildEnqueueTags(outboxMessage.To, outboxMessage.EventTypeId));
        // Outbox returns 0 because the real sequence number is only assigned by
        // Service Bus when OutboxDispatcher forwards the message. This means
        // CancelScheduledMessage cannot work in outbox mode.
        return 0L;
    }

    public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException(
            "Cancelling scheduled messages is not supported when using the transactional outbox. " +
            "The sequence number is only assigned by Service Bus after the outbox dispatches the message.");
    }

    private static OutboxMessage ToOutboxMessage(IMessage message, int messageEnqueueDelay)
    {
        var (traceParent, traceState) = W3CMessagePropagator.CaptureCurrent();
        return new OutboxMessage
        {
            Id = Guid.NewGuid().ToString(),
            MessageId = message.MessageId,
            To = message.To,
            EventTypeId = message.EventTypeId ?? message.MessageContent?.EventContent?.EventTypeId,
            SessionId = message.SessionId,
            CorrelationId = message.CorrelationId,
            Payload = JsonConvert.SerializeObject(message),
            EnqueueDelayMinutes = messageEnqueueDelay,
            CreatedAtUtc = DateTime.UtcNow,
            DispatchedAtUtc = null,
            TraceParent = traceParent,
            TraceState = traceState
        };
    }

    private static Activity? StartEnqueueSpan(string? endpoint, string? eventTypeId, int count)
    {
        var activity = NimBusActivitySources.Outbox.StartActivity("NimBus.Outbox.Enqueue", ActivityKind.Internal);
        if (activity is null) return null;
        if (!string.IsNullOrEmpty(endpoint))
            activity.SetTag(MessagingAttributes.NimBusEndpoint, endpoint);
        if (!string.IsNullOrEmpty(eventTypeId))
            activity.SetTag(MessagingAttributes.NimBusEventType, eventTypeId);
        if (count > 1)
            activity.SetTag(MessagingAttributes.NimBusOutboxBatchSize, count);
        return activity;
    }

    private static KeyValuePair<string, object?>[] BuildEnqueueTags(string? endpoint, string? eventTypeId)
    {
        var tags = new List<KeyValuePair<string, object?>>(2);
        if (!string.IsNullOrEmpty(endpoint))
            tags.Add(new KeyValuePair<string, object?>(MessagingAttributes.NimBusEndpoint, endpoint));
        if (!string.IsNullOrEmpty(eventTypeId))
            tags.Add(new KeyValuePair<string, object?>(MessagingAttributes.NimBusEventType, eventTypeId));
        return tags.ToArray();
    }
}
