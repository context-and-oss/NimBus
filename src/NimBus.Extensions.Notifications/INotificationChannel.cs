using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Extensions.Notifications;

/// <summary>
/// Defines a notification channel that can send alerts (e.g. email, Teams, Slack, webhook).
/// Implement this interface to add custom notification channels.
/// </summary>
public interface INotificationChannel
{
    /// <summary>
    /// Sends a notification with the given context.
    /// </summary>
    Task SendAsync(Notification notification, CancellationToken cancellationToken = default);
}

/// <summary>
/// Represents a notification to be sent via a channel.
/// </summary>
public class Notification
{
    /// <summary>
    /// Severity level of the notification.
    /// </summary>
    public NotificationSeverity Severity { get; init; }

    /// <summary>
    /// Short title/subject for the notification.
    /// </summary>
    public string Title { get; init; } = null!;

    /// <summary>
    /// Detailed message body.
    /// </summary>
    public string Message { get; init; } = null!;

    /// <summary>
    /// The event ID that triggered this notification.
    /// </summary>
    public string? EventId { get; init; }

    /// <summary>
    /// The event type ID.
    /// </summary>
    public string? EventTypeId { get; init; }

    /// <summary>
    /// The message ID.
    /// </summary>
    public string? MessageId { get; init; }

    /// <summary>
    /// Correlation ID for tracing.
    /// </summary>
    public string? CorrelationId { get; init; }

    /// <summary>
    /// Exception details, if applicable.
    /// </summary>
    public string? ErrorDetails { get; init; }
}

public enum NotificationSeverity
{
    Information,
    Warning,
    Error,
    Critical
}
