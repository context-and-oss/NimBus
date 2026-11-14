using NimBus.Core.Extensions;
using NimBus.Core.CircuitBreaker;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Extensions.Notifications;

/// <summary>
/// Lifecycle observer that sends notifications to registered channels when messages fail,
/// are dead-lettered, or hit a blocked session. When an <see cref="INotificationRouter"/> is
/// registered, notifications are routed through it (per-channel severity filtering, rate
/// limiting, dedup); otherwise they fan out to every channel (legacy behaviour). In both cases
/// channel exceptions are swallowed so notification delivery never affects message processing.
/// </summary>
public class NotificationLifecycleObserver : IMessageLifecycleObserver
{
    private readonly IEnumerable<INotificationChannel> _channels;
    private readonly NotificationOptions _options;
    private readonly INotificationRouter? _router;

    public NotificationLifecycleObserver(IEnumerable<INotificationChannel> channels, NotificationOptions options)
        : this(channels, options, null)
    {
    }

    public NotificationLifecycleObserver(IEnumerable<INotificationChannel> channels, NotificationOptions options, INotificationRouter? router)
    {
        _channels = channels ?? throw new ArgumentNullException(nameof(channels));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _router = router;
    }

    public async Task OnMessageReceived(MessageLifecycleContext context, CancellationToken cancellationToken = default)
    {
        if (!_options.NotifyOnReceived) return;

        var notification = new Notification
        {
            Severity = NotificationSeverity.Information,
            Title = $"Message received: {context.EventTypeId}",
            Message = $"Message {context.MessageId} received for event {context.EventId}.",
            EventId = context.EventId,
            EventTypeId = context.EventTypeId,
            MessageId = context.MessageId,
            CorrelationId = context.CorrelationId,
        };

        await Dispatch(notification, cancellationToken);
    }

    public async Task OnMessageCompleted(MessageLifecycleContext context, CancellationToken cancellationToken = default)
    {
        if (!_options.NotifyOnCompleted) return;

        var notification = new Notification
        {
            Severity = NotificationSeverity.Information,
            Title = $"Message completed: {context.EventTypeId}",
            Message = $"Message {context.MessageId} for event {context.EventId} completed successfully.",
            EventId = context.EventId,
            EventTypeId = context.EventTypeId,
            MessageId = context.MessageId,
            CorrelationId = context.CorrelationId,
        };

        await Dispatch(notification, cancellationToken);
    }

    public async Task OnMessageFailed(MessageLifecycleContext context, Exception exception, CancellationToken cancellationToken = default)
    {
        if (!_options.NotifyOnFailure) return;

        var notification = new Notification
        {
            Severity = NotificationSeverity.Error,
            Title = $"Message failed: {context.EventTypeId}",
            Message = $"Message {context.MessageId} for event {context.EventId} failed with error: {exception?.Message}",
            EventId = context.EventId,
            EventTypeId = context.EventTypeId,
            MessageId = context.MessageId,
            CorrelationId = context.CorrelationId,
            ErrorDetails = exception?.ToString(),
        };

        await Dispatch(notification, cancellationToken);
    }

    public async Task OnMessageDeadLettered(MessageLifecycleContext context, string reason, Exception? exception = null, CancellationToken cancellationToken = default)
    {
        if (!_options.NotifyOnDeadLetter) return;

        var notification = new Notification
        {
            Severity = NotificationSeverity.Critical,
            Title = $"Message dead-lettered: {context.EventTypeId}",
            Message = $"Message {context.MessageId} for event {context.EventId} was dead-lettered. Reason: {reason}",
            EventId = context.EventId,
            EventTypeId = context.EventTypeId,
            MessageId = context.MessageId,
            CorrelationId = context.CorrelationId,
            ErrorDetails = exception?.ToString(),
        };

        await Dispatch(notification, cancellationToken);
    }

    public async Task OnSessionBlocked(MessageLifecycleContext context, string? blockedByEventId, CancellationToken cancellationToken = default)
    {
        if (!_options.NotifyOnSessionBlock) return;

        // Key the notification on the blocking event id so repeated arrivals on the same blocked
        // session collapse to a single alert via the router's (EventId, Severity) dedup.
        var keyEventId = !string.IsNullOrEmpty(blockedByEventId) ? blockedByEventId : context.EventId;

        var notification = new Notification
        {
            Severity = NotificationSeverity.Critical,
            Title = $"Session blocked: {context.SessionId}",
            Message = $"Session {context.SessionId} is blocked by event {blockedByEventId}. " +
                      $"Message {context.MessageId} ({context.EventTypeId}) was deferred until the blocking event is resolved.",
            EventId = keyEventId,
            EventTypeId = context.EventTypeId,
            MessageId = context.MessageId,
            CorrelationId = context.CorrelationId,
        };

        await Dispatch(notification, cancellationToken);
    }

    /// <inheritdoc />
    public async Task OnCircuitStateChanged(CircuitStateChangeContext context, CancellationToken cancellationToken = default)
    {
        if (!_options.NotifyOnCircuitOpen
            || (context.To != CircuitState.Open && context.To != CircuitState.Closed))
        {
            return;
        }

        var opened = context.To == CircuitState.Open;
        var notification = new Notification
        {
            Severity = opened ? NotificationSeverity.Critical : NotificationSeverity.Information,
            Title = opened
                ? $"Circuit opened: {context.Endpoint}"
                : $"Circuit recovered: {context.Endpoint}",
            Message = $"Endpoint {context.Endpoint} circuit changed from {context.From} to {context.To}. {context.Reason}",
            EventId = $"circuit:{context.Endpoint}:{context.To}",
        };

        await Dispatch(notification, cancellationToken);
    }

    private async Task Dispatch(Notification notification, CancellationToken cancellationToken)
    {
        if (_router != null)
        {
            try
            {
                await _router.RouteAsync(notification, cancellationToken);
            }
            catch
            {
                // Don't let notification failures affect message processing.
            }

            return;
        }

        await SendToAllChannels(notification, cancellationToken);
    }

    private async Task SendToAllChannels(Notification notification, CancellationToken cancellationToken)
    {
        foreach (var channel in _channels)
        {
            try
            {
                await channel.SendAsync(notification, cancellationToken);
            }
            catch
            {
                // Don't let notification failures affect message processing.
            }
        }
    }
}
