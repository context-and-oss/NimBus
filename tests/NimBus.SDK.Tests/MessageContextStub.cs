#pragma warning disable CA1707, CA2007, CS8618, CS8625, CS8603
using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;

namespace NimBus.SDK.Tests;

/// <summary>
/// Minimal reusable stub for <see cref="IMessageContext"/> used across SDK unit tests.
/// Only <c>MessageContent.EventContent.EventTypeId</c> and <c>EventContent.EventJson</c>
/// are populated; all other members return harmless defaults or empty values.
/// </summary>
public static class MessageContextStub
{
    public static IMessageContext ForEventType(string eventTypeId, string eventJson)
        => new StubMessageContext(eventTypeId, eventJson);

    public static IMessageContext ForEventTypes(string eventTypeId, string bodyEventTypeId, string eventJson)
        => new StubMessageContext(eventTypeId, bodyEventTypeId, eventJson);

    public static IMessageContext ForWorkflowEvent(
        string eventTypeId,
        string eventJson,
        string messageId,
        string sessionId,
        string correlationId,
        string parentMessageId,
        string originatingMessageId)
        => new StubMessageContext(
            eventTypeId,
            eventJson,
            messageId,
            sessionId,
            correlationId,
            parentMessageId,
            originatingMessageId);

    public static IMessageContext WithInvalidContent(string eventTypeId, InvalidMessageException exception)
        => new StubMessageContext(eventTypeId, exception);

    private sealed class StubMessageContext : IMessageContext
    {
        private readonly MessageContent? _messageContent;
        private readonly InvalidMessageException? _contentException;

        public StubMessageContext(string eventTypeId, string eventJson)
            : this(eventTypeId, eventTypeId, eventJson)
        {
        }

        public StubMessageContext(string eventTypeId, string bodyEventTypeId, string eventJson)
        {
            _messageContent = new MessageContent
            {
                EventContent = new EventContent
                {
                    EventTypeId = bodyEventTypeId,
                    EventJson = eventJson,
                }
            };
            EventTypeId = eventTypeId;
        }

        public StubMessageContext(
            string eventTypeId,
            string eventJson,
            string messageId,
            string sessionId,
            string correlationId,
            string parentMessageId,
            string originatingMessageId)
            : this(eventTypeId, eventJson)
        {
            MessageId = messageId;
            SessionId = sessionId;
            CorrelationId = correlationId;
            ParentMessageId = parentMessageId;
            OriginatingMessageId = originatingMessageId;
        }

        public StubMessageContext(string eventTypeId, InvalidMessageException contentException)
        {
            EventTypeId = eventTypeId;
            _contentException = contentException;
        }

        public MessageContent MessageContent => _contentException is null
            ? _messageContent!
            : throw _contentException;
        public string EventTypeId { get; }

        // Minimal stubs — unused by dispatch path
        public string EventId => string.Empty;
        public string To => string.Empty;
        public string SessionId { get; } = string.Empty;
        public string CorrelationId { get; } = string.Empty;
        public string MessageId { get; } = string.Empty;
        public MessageType MessageType => MessageType.EventRequest;
        public string ParentMessageId { get; } = string.Empty;
        public string OriginatingMessageId { get; } = string.Empty;
        public int? RetryCount => null;
        public string From => string.Empty;
        public string OriginatingFrom => string.Empty;
        public string OriginalSessionId => string.Empty;
        public int? DeferralSequence => null;
        public DateTime EnqueuedTimeUtc => DateTime.UtcNow;
        public string DeadLetterReason => null;
        public string DeadLetterErrorDescription => null;
        public string HandoffReason => null;
        public string ExternalJobId => null;
        public DateTime? ExpectedBy => null;
        public bool IsDeferred => false;
        public int ThrottleRetryCount => 0;
        public long? QueueTimeMs { get; set; }
        public long? ProcessingTimeMs { get; set; }
        public DateTime? HandlerStartedAtUtc { get; set; }
        public HandlerOutcome HandlerOutcome { get; set; }
        public HandoffMetadata? HandoffMetadata { get; set; }
        public ActivityContext ParentTraceContext { get; set; }

        public Task Complete(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task Abandon(TransientException exception) => Task.CompletedTask;
        public Task DeadLetter(string reason, Exception? exception = null, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task BlockSession(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task UnblockSession(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<bool> IsSessionBlocked(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> IsSessionBlockedByThis(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<bool> IsSessionBlockedByEventId(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task<string?> GetBlockedByEventId(CancellationToken cancellationToken = default) => Task.FromResult<string?>(string.Empty);
        public Task<int> GetNextDeferralSequenceAndIncrement(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task IncrementDeferredCount(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task DecrementDeferredCount(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<int> GetDeferredCount(CancellationToken cancellationToken = default) => Task.FromResult(0);
        public Task<bool> HasDeferredMessages(CancellationToken cancellationToken = default) => Task.FromResult(false);
        public Task ResetDeferredCount(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ScheduleRedelivery(TimeSpan delay, int throttleRetryCount, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
