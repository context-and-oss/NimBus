using NimBus.Core.CloudEvents;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;

namespace NimBus.ServiceBus;

public static class MessageHelper
{
    public static Azure.Messaging.ServiceBus.ServiceBusMessage ToServiceBusMessage(IMessage message, int messageEnqueueDelay = 0)
    {
        var result = new Azure.Messaging.ServiceBus.ServiceBusMessage();
        result.ApplicationProperties[UserPropertyName.To.ToString()] = message.To;
        result.ApplicationProperties[UserPropertyName.MessageType.ToString()] = message.MessageType.ToString();
        result.ApplicationProperties[UserPropertyName.EventId.ToString()] = message.EventId;
        result.ApplicationProperties[UserPropertyName.OriginatingMessageId.ToString()] = message.OriginatingMessageId ?? Constants.Self;
        result.ApplicationProperties[UserPropertyName.ParentMessageId.ToString()] = message.ParentMessageId ?? Constants.Self;
        result.ApplicationProperties[UserPropertyName.RetryCount.ToString()] = message.RetryCount ?? 0;
        result.ApplicationProperties[UserPropertyName.OriginatingFrom.ToString()] = message.OriginatingFrom ?? Constants.Self;
        if (!string.IsNullOrEmpty(message.From))
            result.ApplicationProperties[UserPropertyName.From.ToString()] = message.From;
        result.ApplicationProperties[UserPropertyName.EventTypeId.ToString()] =  message.EventTypeId ?? message.MessageContent?.EventContent?.EventTypeId;

        // Spec 045: forward rules filter on the routing key, so it is written only when the
        // message has one. A keyless message looks exactly as it did before routing keys.
        if (!string.IsNullOrEmpty(message.RoutingKey))
            result.ApplicationProperties[UserPropertyName.RoutingKey.ToString()] = message.RoutingKey;

        // Add OriginalSessionId and DeferralSequence if present (for deferred messages)
        if (!string.IsNullOrEmpty(message.OriginalSessionId))
        {
            result.ApplicationProperties[UserPropertyName.OriginalSessionId.ToString()] = message.OriginalSessionId;
        }
        if (message.DeferralSequence.HasValue)
        {
            result.ApplicationProperties[UserPropertyName.DeferralSequence.ToString()] = message.DeferralSequence.Value;
        }

        // Only a RetryRequest or a parked copy carries the delivery it stands in for.
        var inboxMessageId = message.GetInboxMessageIdOrDefault();
        if (inboxMessageId is not null)
        {
            result.ApplicationProperties[UserPropertyName.InboxMessageId.ToString()] = inboxMessageId;
        }

        // Per-message timings: only set on response messages produced by the
        // receive pipeline (subscriber → Resolver). Original publishes leave
        // these null so they're absent on the wire.
        if (message.QueueTimeMs.HasValue)
        {
            result.ApplicationProperties[UserPropertyName.QueueTimeMs.ToString()] = message.QueueTimeMs.Value;
        }
        if (message.ProcessingTimeMs.HasValue)
        {
            result.ApplicationProperties[UserPropertyName.ProcessingTimeMs.ToString()] = message.ProcessingTimeMs.Value;
        }

        // Dead-letter notification messages carry the SB dead-letter properties as
        // user properties so the Resolver can mirror them into the audit record and
        // classify the resolution status as DeadLettered.
        if (!string.IsNullOrEmpty(message.DeadLetterReason))
        {
            result.ApplicationProperties[UserPropertyName.DeadLetterReason.ToString()] = message.DeadLetterReason;
        }
        if (!string.IsNullOrEmpty(message.DeadLetterErrorDescription))
        {
            result.ApplicationProperties[UserPropertyName.DeadLetterErrorDescription.ToString()] = message.DeadLetterErrorDescription;
        }

        // PendingHandoff metadata: carried on the PendingHandoffResponse to the
        // Resolver so the audit row records reason / external job id / deadline.
        if (!string.IsNullOrEmpty(message.HandoffReason))
        {
            result.ApplicationProperties[UserPropertyName.HandoffReason.ToString()] = message.HandoffReason;
        }
        if (!string.IsNullOrEmpty(message.ExternalJobId))
        {
            result.ApplicationProperties[UserPropertyName.ExternalJobId.ToString()] = message.ExternalJobId;
        }
        if (message.ExpectedBy.HasValue)
        {
            result.ApplicationProperties[UserPropertyName.ExpectedBy.ToString()] =
                message.ExpectedBy.Value.ToUniversalTime().ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        }

        // CloudEvents identity carried to the Resolver on a response message so the
        // tracking/audit record preserves the inbound CloudEvent's id/source/type/
        // subject. Only stamped when present (i.e. the message originated from a
        // CloudEvents-consuming subscriber); native messages leave these null, so
        // their wire form is byte-identical.
        if (!string.IsNullOrEmpty(message.CloudEventId))
            result.ApplicationProperties[UserPropertyName.CloudEventId.ToString()] = message.CloudEventId;
        if (!string.IsNullOrEmpty(message.CloudEventSource))
            result.ApplicationProperties[UserPropertyName.CloudEventSource.ToString()] = message.CloudEventSource;
        if (!string.IsNullOrEmpty(message.CloudEventType))
            result.ApplicationProperties[UserPropertyName.CloudEventType.ToString()] = message.CloudEventType;
        if (!string.IsNullOrEmpty(message.CloudEventSubject))
            result.ApplicationProperties[UserPropertyName.CloudEventSubject.ToString()] = message.CloudEventSubject;

        // W3C trace propagation: traceparent (and optional tracestate) is the
        // canonical format on every transport (FR-030). Legacy Diagnostic-Id is
        // not written or read by NimBus.
        var (traceParent, traceState) = W3CMessagePropagator.CaptureCurrent();
        if (!string.IsNullOrEmpty(traceParent))
            result.ApplicationProperties[W3CMessagePropagator.TraceParentHeader] = traceParent;
        if (!string.IsNullOrEmpty(traceState))
            result.ApplicationProperties[W3CMessagePropagator.TraceStateHeader] = traceState;

        result.ScheduledEnqueueTime = DateTime.UtcNow.AddMinutes(messageEnqueueDelay);

        // CloudEvents opt-in layer: when a CloudEvents publish context is present
        // the body/content-type are shaped per the CloudEvents 1.0 AMQP binding.
        // All native routing/trace/id stamping above is UNCHANGED, so a NimBus
        // round-trip keeps working; only the body shape differs. When absent
        // (the default), the native MessageContent envelope is the body —
        // byte-identical to pre-CloudEvents behavior.
        if (message.CloudEvent is { } cloudEventPublish)
        {
            var domainEventJson = message.MessageContent?.EventContent?.EventJson ?? string.Empty;
            if (cloudEventPublish.ContentMode == CloudEventContentMode.StructuredJson)
            {
                var envelope = CloudEventsServiceBusBinding.WriteStructured(result, cloudEventPublish.CloudEvent, domainEventJson);
                result.Body = new BinaryData(Encoding.UTF8.GetBytes(envelope));
            }
            else
            {
                CloudEventsServiceBusBinding.WriteBinary(result, cloudEventPublish.CloudEvent);
                result.Body = new BinaryData(Encoding.UTF8.GetBytes(domainEventJson));
            }
        }
        else
        {
            // Publish paths that already serialized the content (batch sizing)
            // stash it on the message so it isn't serialized a second time here.
            var messageContentSerialized = message.SerializedMessageContent
                ?? JsonConvert.SerializeObject(message.MessageContent);
            result.Body = new BinaryData(Encoding.UTF8.GetBytes(messageContentSerialized));
        }
        if (!string.IsNullOrWhiteSpace(message.MessageId))
            result.MessageId = message.MessageId;

        result.SessionId = message.SessionId;
        result.CorrelationId = message.CorrelationId;

        if (!string.IsNullOrEmpty(message.ReplyTo))
            result.ReplyTo = message.ReplyTo;
        if (!string.IsNullOrEmpty(message.ReplyToSessionId))
            result.ReplyToSessionId = message.ReplyToSessionId;

        return result;
    }

    /// <summary>
    /// Creates a ServiceBus message for the session-enabled deferred subscription.
    /// The message is routed via To="Deferred" and uses SessionId for session affinity.
    /// OriginalSessionId and DeferralSequence are kept for backward compatibility and ordering.
    /// </summary>
    public static Azure.Messaging.ServiceBus.ServiceBusMessage CreateDeferredMessage(IMessage message, string originalSessionId, int deferralSequence)
    {
        var result = new Azure.Messaging.ServiceBus.ServiceBusMessage();
        result.ApplicationProperties[UserPropertyName.To.ToString()] = Constants.DeferredSubscriptionName;
        result.ApplicationProperties[UserPropertyName.MessageType.ToString()] = message.MessageType.ToString();
        result.ApplicationProperties[UserPropertyName.EventId.ToString()] = message.EventId;
        result.ApplicationProperties[UserPropertyName.OriginatingMessageId.ToString()] = message.OriginatingMessageId ?? Constants.Self;
        result.ApplicationProperties[UserPropertyName.ParentMessageId.ToString()] = message.ParentMessageId ?? Constants.Self;
        result.ApplicationProperties[UserPropertyName.RetryCount.ToString()] = message.RetryCount ?? 0;
        result.ApplicationProperties[UserPropertyName.OriginatingFrom.ToString()] = message.OriginatingFrom ?? Constants.Self;
        result.ApplicationProperties[UserPropertyName.EventTypeId.ToString()] = message.EventTypeId ?? message.MessageContent?.EventContent?.EventTypeId;
        result.ApplicationProperties[UserPropertyName.OriginalSessionId.ToString()] = originalSessionId;
        result.ApplicationProperties[UserPropertyName.DeferralSequence.ToString()] = deferralSequence;
        if (!string.IsNullOrEmpty(message.RoutingKey))
            result.ApplicationProperties[UserPropertyName.RoutingKey.ToString()] = message.RoutingKey;
        var inboxMessageId = message.GetInboxMessageIdOrDefault();
        if (inboxMessageId is not null)
            result.ApplicationProperties[UserPropertyName.InboxMessageId.ToString()] = inboxMessageId;

        var (traceParent, traceState) = W3CMessagePropagator.CaptureCurrent();
        if (!string.IsNullOrEmpty(traceParent))
            result.ApplicationProperties[W3CMessagePropagator.TraceParentHeader] = traceParent;
        if (!string.IsNullOrEmpty(traceState))
            result.ApplicationProperties[W3CMessagePropagator.TraceStateHeader] = traceState;

        var messageContentSerialized = JsonConvert.SerializeObject(message.MessageContent);
        result.Body = new BinaryData(Encoding.UTF8.GetBytes(messageContentSerialized));
        if (!string.IsNullOrWhiteSpace(message.MessageId))
            result.MessageId = message.MessageId;

        result.CorrelationId = message.CorrelationId;
        result.SessionId = originalSessionId;  // Session-enabled deferred subscription
        return result;
    }
}
