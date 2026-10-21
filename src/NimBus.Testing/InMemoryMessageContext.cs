using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Testing;

public class InMemoryMessageContext : IMessageContext
{
    private readonly IMessage _message;
    private readonly InMemorySessionState _sessionState;

    public InMemoryMessageContext(IMessage message, InMemorySessionState sessionState)
    {
        _message = message ?? throw new ArgumentNullException(nameof(message));
        _sessionState = sessionState ?? throw new ArgumentNullException(nameof(sessionState));
        EnqueuedTimeUtc = DateTime.UtcNow;
    }

    // IMessage properties - delegate to wrapped message
    public string EventId => _message.EventId;
    public string To => _message.To;
    public string SessionId => _message.SessionId;
    public string? CorrelationId => _message.CorrelationId;
    public string MessageId => _message.MessageId!;
    public MessageType MessageType => _message.MessageType;
    public MessageContent MessageContent => _message.MessageContent;
    public string? ParentMessageId => _message.ParentMessageId;
    public string? OriginatingMessageId => _message.OriginatingMessageId;
    public int? RetryCount => _message.RetryCount;
    public string? From => _message.From;
    public string? OriginatingFrom => _message.OriginatingFrom;
    public string EventTypeId => _message.EventTypeId;
    public string? OriginalSessionId => _message.OriginalSessionId;
    public int? DeferralSequence => _message.DeferralSequence;
    public string? ReplyTo => _message.ReplyTo;
    public string? ReplyToSessionId => _message.ReplyToSessionId;
    public string? InboxMessageId => _message.InboxMessageId;

    // IReceivedMessage
    public DateTime EnqueuedTimeUtc { get; }
    public string? DeadLetterReason => DeadLetterReasonRecorded;
    public string? DeadLetterErrorDescription => DeadLetterErrorDescriptionRecorded;
    public string? HandoffReason => _message.HandoffReason;
    public string? ExternalJobId => _message.ExternalJobId;
    public DateTime? ExpectedBy => _message.ExpectedBy;

    // Observable state for test assertions
    public bool IsCompleted { get; private set; }
    public bool IsAbandoned { get; private set; }
    public bool IsDeadLettered { get; private set; }
    public string? DeadLetterReasonRecorded { get; private set; }
    public string? DeadLetterErrorDescriptionRecorded { get; private set; }

    // IMessageContext
    public bool IsDeferred => _message.DeferralSequence.HasValue;
    public long? QueueTimeMs { get; set; }
    public long? ProcessingTimeMs { get; set; }
    public DateTime? HandlerStartedAtUtc { get; set; }
    public HandlerOutcome HandlerOutcome { get; set; }
    public HandoffMetadata? HandoffMetadata { get; set; }
    public Exception? HandledFailure { get; set; }
    public System.Diagnostics.ActivityContext ParentTraceContext { get; set; }

    public Task Complete(CancellationToken cancellationToken = default)
    {
        IsCompleted = true;
        return Task.CompletedTask;
    }

    public Task Abandon(TransientException exception)
    {
        IsAbandoned = true;
        return Task.CompletedTask;
    }

    public Task DeadLetter(string reason, Exception? exception = null, CancellationToken cancellationToken = default)
    {
        IsDeadLettered = true;
        DeadLetterReasonRecorded = reason;
        DeadLetterErrorDescriptionRecorded = exception?.Message;
        return Task.CompletedTask;
    }

    public Task BlockSession(CancellationToken cancellationToken = default)
    {
        _sessionState.BlockedByEventId = EventId;
        _sessionState.BlockedByMessageId = this.GetSourceMessageIdOrDefault();
        return Task.CompletedTask;
    }

    public Task UnblockSession(CancellationToken cancellationToken = default)
    {
        _sessionState.BlockedByEventId = null;
        _sessionState.BlockedByMessageId = null;
        return Task.CompletedTask;
    }

    public Task<bool> IsSessionBlocked(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(!string.IsNullOrEmpty(_sessionState.BlockedByEventId));
    }

    public Task<bool> IsSessionBlockedByThis(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_sessionState.BlockedByEventId == EventId);
    }

    public Task<bool> IsSessionBlockedByEventId(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(!string.IsNullOrEmpty(_sessionState.BlockedByEventId));
    }

    public Task<string?> GetBlockedByEventId(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_sessionState.BlockedByEventId);
    }

    public Task<string?> GetBlockedByMessageId(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_sessionState.BlockedByMessageId);
    }

    public Task<int> GetNextDeferralSequenceAndIncrement(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_sessionState.NextDeferralSequence++);
    }

    public Task MarkDeferredMessageReplayed(int deferralSequence, CancellationToken cancellationToken = default)
    {
        if (deferralSequence < _sessionState.NextDeferralSequence)
            _sessionState.ReplayedThroughSequence = Math.Max(_sessionState.ReplayedThroughSequence ?? deferralSequence, deferralSequence);
        return Task.CompletedTask;
    }

    public Task<bool> HasUnreplayedDeferredMessages(CancellationToken cancellationToken = default)
    {
        // As the Service Bus SessionState: without a recorded position, every assigned sequence waits.
        return Task.FromResult((_sessionState.ReplayedThroughSequence ?? -1) < _sessionState.NextDeferralSequence - 1);
    }

    public Task<DeferredDrainRequest?> GetDeferredDrainRequest(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_sessionState.DrainRequest);
    }

    public Task SetDeferredDrainRequest(DeferredDrainRequest? request, CancellationToken cancellationToken = default)
    {
        _sessionState.DrainRequest = request;
        return Task.CompletedTask;
    }

    public Task IncrementDeferredCount(CancellationToken cancellationToken = default)
    {
        _sessionState.DeferredCount++;
        return Task.CompletedTask;
    }

    public Task DecrementDeferredCount(CancellationToken cancellationToken = default)
    {
        _sessionState.DeferredCount = Math.Max(0, _sessionState.DeferredCount - 1);
        return Task.CompletedTask;
    }

    public Task<int> GetDeferredCount(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_sessionState.DeferredCount);
    }

    public Task<bool> HasDeferredMessages(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(_sessionState.DeferredCount > 0 || _sessionState.DeferredMessages.Count > 0);
    }

    public Task ResetDeferredCount(CancellationToken cancellationToken = default)
    {
        _sessionState.DeferredCount = 0;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the CloudEvent carried by the wrapped message (publish context), or
    /// <c>null</c> for a native message — so in-memory tests can assert CloudEvents
    /// handler access and round-trips.
    /// </summary>
    public NimBus.Core.CloudEvents.CloudEvent? GetCloudEvent() => _message.CloudEvent?.CloudEvent;

    public int ThrottleRetryCount => 0;

    public Task ScheduleRedelivery(TimeSpan delay, int throttleRetryCount, CancellationToken cancellationToken = default)
    {
        // In tests, just complete the original (tests rarely exercise throttle path)
        IsCompleted = true;
        return Task.CompletedTask;
    }
}
