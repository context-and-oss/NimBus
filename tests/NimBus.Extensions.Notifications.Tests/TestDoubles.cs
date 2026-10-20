#pragma warning disable CA1707, CA2007
using NimBus.Core.Messages;
using NimBus.Core.Messages.Exceptions;
using NimBus.Extensions.Notifications;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Extensions.Notifications.Tests;

/// <summary>An in-memory notification channel that records what it received.</summary>
internal sealed class FakeNotificationChannel : INotificationChannel
{
    public List<Notification> Received { get; } = [];
    public bool ThrowOnSend { get; set; }

    public Task SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        if (ThrowOnSend)
        {
            throw new InvalidOperationException("Simulated channel failure.");
        }

        Received.Add(notification);
        return Task.CompletedTask;
    }
}

/// <summary>An <see cref="HttpMessageHandler"/> that captures the request and returns a fixed status.</summary>
internal sealed class CapturingHttpMessageHandler : HttpMessageHandler
{
    private readonly HttpStatusCode _status;

    public CapturingHttpMessageHandler(HttpStatusCode status = HttpStatusCode.OK)
    {
        _status = status;
    }

    public int CallCount { get; private set; }
    public HttpRequestMessage? LastRequest { get; private set; }
    public string? LastBody { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CallCount++;
        LastRequest = request;
        if (request.Content != null)
        {
            LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
        }

        return new HttpResponseMessage(_status);
    }
}

/// <summary>A controllable <see cref="TimeProvider"/> for deterministic rate-limit / dedup tests.</summary>
internal sealed class MutableTimeProvider : TimeProvider
{
    private DateTimeOffset _now;

    public MutableTimeProvider(DateTimeOffset start)
    {
        _now = start;
    }

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now = _now.Add(delta);
}

/// <summary>Lifecycle observer that records session-block callbacks.</summary>
internal sealed class RecordingSessionObserver : NimBus.Core.Extensions.IMessageLifecycleObserver
{
    public List<(string? SessionId, string? BlockedByEventId)> SessionBlocks { get; } = [];

    public Task OnSessionBlocked(NimBus.Core.Extensions.MessageLifecycleContext context, string? blockedByEventId, CancellationToken cancellationToken = default)
    {
        SessionBlocks.Add((context.SessionId, blockedByEventId));
        return Task.CompletedTask;
    }
}

/// <summary>A <see cref="MessageHandler"/> whose event handling throws a <see cref="SessionBlockedException"/>.</summary>
internal sealed class SessionBlockingHandler : MessageHandler
{
    private readonly string _blockedByEventId;

    public SessionBlockingHandler(NimBus.Core.Extensions.MessageLifecycleNotifier notifier, string blockedByEventId)
        : base(null!, null, notifier)
    {
        _blockedByEventId = blockedByEventId;
    }

    public override Task HandleEventRequest(IMessageContext messageContext, CancellationToken cancellationToken = default) =>
        throw new SessionBlockedException("Session is blocked.", _blockedByEventId);
}

/// <summary>Minimal in-memory <see cref="IMessageContext"/> for handler tests.</summary>
internal sealed class FakeMessageContext : IMessageContext
{
    public string EventId { get; set; } = "evt-1";
    public string To { get; set; } = "test-endpoint";
    public string SessionId { get; set; } = "session-1";
    public string CorrelationId { get; set; } = "corr-1";
    public string MessageId { get; set; } = "msg-1";
    public MessageType MessageType { get; set; } = MessageType.EventRequest;
    public MessageContent MessageContent { get; set; } = new();
    public string ParentMessageId { get; set; } = string.Empty;
    public string OriginatingMessageId { get; set; } = string.Empty;
    public int? RetryCount { get; set; }
    public string OriginatingFrom { get; set; } = string.Empty;
    public string EventTypeId { get; set; } = "TestEvent";
    public string OriginalSessionId { get; set; } = string.Empty;
    public int? DeferralSequence { get; set; }
    public DateTime EnqueuedTimeUtc { get; set; } = DateTime.UtcNow;
    public string From { get; set; } = string.Empty;
    public string? DeadLetterReason { get; set; }
    public string? DeadLetterErrorDescription { get; set; }
    public string? HandoffReason { get; set; }
    public string? ExternalJobId { get; set; }
    public DateTime? ExpectedBy { get; set; }
    public bool IsDeferred { get; set; }
    public int ThrottleRetryCount { get; set; }
    public long? QueueTimeMs { get; set; }
    public long? ProcessingTimeMs { get; set; }
    public DateTime? HandlerStartedAtUtc { get; set; }
    public HandlerOutcome HandlerOutcome { get; set; }
    public HandoffMetadata? HandoffMetadata { get; set; }

    public Task Complete(CancellationToken ct = default) => Task.CompletedTask;
    public Task Abandon(TransientException ex) => Task.CompletedTask;
    public Task DeadLetter(string reason, Exception? ex = null, CancellationToken ct = default) => Task.CompletedTask;
    public Task BlockSession(CancellationToken ct = default) => Task.CompletedTask;
    public Task UnblockSession(CancellationToken ct = default) => Task.CompletedTask;
    public Task<bool> IsSessionBlocked(CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> IsSessionBlockedByThis(CancellationToken ct = default) => Task.FromResult(false);
    public Task<bool> IsSessionBlockedByEventId(CancellationToken ct = default) => Task.FromResult(false);
    public Task<string?> GetBlockedByEventId(CancellationToken ct = default) => Task.FromResult<string?>(string.Empty);
    public Task<int> GetNextDeferralSequenceAndIncrement(CancellationToken ct = default) => Task.FromResult(0);
    public Task IncrementDeferredCount(CancellationToken ct = default) => Task.CompletedTask;
    public Task DecrementDeferredCount(CancellationToken ct = default) => Task.CompletedTask;
    public Task<int> GetDeferredCount(CancellationToken ct = default) => Task.FromResult(0);
    public Task<bool> HasDeferredMessages(CancellationToken ct = default) => Task.FromResult(false);
    public Task ResetDeferredCount(CancellationToken ct = default) => Task.CompletedTask;
    public Task ScheduleRedelivery(TimeSpan delay, int throttleRetryCount, CancellationToken ct = default) => Task.CompletedTask;
}

/// <summary>Shared notification builders.</summary>
internal static class TestNotifications
{
    public static Notification Build(
        NotificationSeverity severity = NotificationSeverity.Critical,
        string title = "Test title",
        string message = "Test message",
        string eventId = "evt-1",
        string eventTypeId = "TestEvent",
        string messageId = "msg-1",
        string correlationId = "corr-1",
        string? errorDetails = null) => new()
        {
            Severity = severity,
            Title = title,
            Message = message,
            EventId = eventId,
            EventTypeId = eventTypeId,
            MessageId = messageId,
            CorrelationId = correlationId,
            ErrorDetails = errorDetails,
        };
}
