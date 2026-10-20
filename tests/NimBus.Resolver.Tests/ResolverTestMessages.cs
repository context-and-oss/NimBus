#pragma warning disable CA1707, CA1515, CA2007
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;

namespace NimBus.Resolver.Tests;

/// <summary>
/// The message context the Resolver tests drive, and the factory that builds one. Hoisted out
/// of <see cref="ResolverServiceTests"/> so the Spec 030 stale-copy tests can reuse them, and
/// extended with the identity fields that decide a stale write: the message id and the parent
/// message id an outcome would have answered.
/// </summary>
internal static class ResolverTestMessages
{
    internal static FakeMessageContext CreateMessageContext(
        MessageType messageType,
        string to = "AnalyticsEndpoint",
        string from = "StorefrontEndpoint",
        int throttleRetryCount = 0,
        int deliveryCount = 1,
        string eventTypeId = "OrderPlaced",
        string messageId = "message-1",
        string parentMessageId = Constants.Self,
        string eventId = "event-1",
        string sessionId = "session-1")
    {
        return new FakeMessageContext
        {
            EventId = eventId,
            MessageId = messageId,
            CorrelationId = "correlation-1",
            SessionId = sessionId,
            ParentMessageId = parentMessageId,
            OriginatingMessageId = "self",
            OriginatingFrom = from,
            From = from,
            To = to,
            MessageType = messageType,
            MessageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = eventTypeId,
                    EventJson = "{}",
                },
            },
            EventTypeId = eventTypeId,
            EnqueuedTimeUtc = new DateTime(2026, 03, 06, 12, 00, 00, DateTimeKind.Utc),
            ThrottleRetryCount = throttleRetryCount,
            DeliveryCount = deliveryCount,
        };
    }
}

internal sealed class FakeMessageContext : IMessageContext, IMessageDeliveryContext
{
    public string EventId { get; set; } = string.Empty;
    public string To { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public string CorrelationId { get; set; } = string.Empty;
    public string MessageId { get; set; } = string.Empty;
    public MessageType MessageType { get; set; }
    public MessageContent MessageContent { get; set; } = new();
    public string ParentMessageId { get; set; } = string.Empty;
    public string OriginatingMessageId { get; set; } = string.Empty;
    public int? RetryCount { get; set; }
    public string OriginatingFrom { get; set; } = string.Empty;
    public string EventTypeId { get; set; } = string.Empty;
    public string OriginalSessionId { get; set; } = string.Empty;
    public int? DeferralSequence { get; set; }
    public DateTime EnqueuedTimeUtc { get; set; }
    private string _from = string.Empty;

    // Mirrors the Service Bus MessageContext, which throws when From is absent on the wire.
    public bool FromIsMissing { get; set; }

    public string From
    {
        get => FromIsMissing ? throw new InvalidMessageException("Message.UserProperties[From] is not defined.") : _from;
        set => _from = value;
    }

    public string DeadLetterReason { get; set; } = null!;
    public string DeadLetterErrorDescription { get; set; } = null!;
    public string HandoffReason { get; set; }
    public string ExternalJobId { get; set; }
    public DateTime? ExpectedBy { get; set; }
    public bool IsDeferred { get; set; }
    public int ThrottleRetryCount { get; set; }
    public int DeliveryCount { get; set; } = 1;
    public long? QueueTimeMs { get; set; }
    public long? ProcessingTimeMs { get; set; }
    public DateTime? HandlerStartedAtUtc { get; set; }
    public HandlerOutcome HandlerOutcome { get; set; }
    public HandoffMetadata? HandoffMetadata { get; set; }
    public string CloudEventId { get; set; }
    public string CloudEventSource { get; set; }
    public string CloudEventType { get; set; }
    public string CloudEventSubject { get; set; }

    public int CompletedCalls { get; private set; }
    public int AbandonCalls { get; private set; }
    public int DeadLetterCalls { get; private set; }
    public int ScheduleRedeliveryCalls { get; private set; }
    public TimeSpan? LastScheduledDelay { get; private set; }
    public int? LastScheduledRetryCount { get; private set; }
    public string? LastDeadLetterReason { get; private set; }
    public Exception? CompleteException { get; set; }
    public Exception? ScheduleRedeliveryException { get; set; }

    public Task Complete(CancellationToken cancellationToken = default)
    {
        CompletedCalls++;
        return CompleteException is null
            ? Task.CompletedTask
            : Task.FromException(CompleteException);
    }

    public Task Abandon(TransientException exception)
    {
        AbandonCalls++;
        return Task.CompletedTask;
    }

    public Task DeadLetter(string reason, Exception? exception = null, CancellationToken cancellationToken = default)
    {
        DeadLetterCalls++;
        LastDeadLetterReason = reason;
        return Task.CompletedTask;
    }

    public Task BlockSession(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task UnblockSession(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<bool> IsSessionBlocked(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> IsSessionBlockedByThis(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<bool> IsSessionBlockedByEventId(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<string> GetBlockedByEventId(CancellationToken cancellationToken = default) => Task.FromResult(string.Empty);
    public Task<int> GetNextDeferralSequenceAndIncrement(CancellationToken cancellationToken = default) => Task.FromResult(0);
    public Task IncrementDeferredCount(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task DecrementDeferredCount(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task<int> GetDeferredCount(CancellationToken cancellationToken = default) => Task.FromResult(0);
    public Task<bool> HasDeferredMessages(CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task ResetDeferredCount(CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task ScheduleRedelivery(TimeSpan delay, int throttleRetryCount, CancellationToken cancellationToken = default)
    {
        ScheduleRedeliveryCalls++;
        LastScheduledDelay = delay;
        LastScheduledRetryCount = throttleRetryCount;
        return ScheduleRedeliveryException is null
            ? Task.CompletedTask
            : Task.FromException(ScheduleRedeliveryException);
    }
}
