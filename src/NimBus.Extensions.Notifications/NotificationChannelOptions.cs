using System;

namespace NimBus.Extensions.Notifications;

/// <summary>
/// Base options shared by all notification channels. Carries the per-channel severity
/// threshold used by <see cref="INotificationRouter"/> and an optional payload template.
/// </summary>
public abstract class NotificationChannelOptions
{
    /// <summary>
    /// The minimum severity a notification must have to be delivered to this channel.
    /// A notification whose <see cref="NotificationSeverity"/> is below this value is filtered
    /// out by the router and never reaches the channel. Default: <see cref="NotificationSeverity.Warning"/>.
    /// </summary>
    public NotificationSeverity MinSeverity { get; set; } = NotificationSeverity.Warning;

    /// <summary>
    /// Optional payload template with <c>{Placeholder}</c> tokens resolving to
    /// <see cref="Notification"/> properties (e.g. <c>{Severity}</c>, <c>{Title}</c>, <c>{Message}</c>,
    /// <c>{EventId}</c>, <c>{ErrorDetails}</c>). When <c>null</c>, the channel uses its default body.
    /// </summary>
    public string? Template { get; set; }

    /// <summary>
    /// Per-send timeout for network delivery (HTTP/SendGrid). Default: 10 seconds.
    /// </summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Validates the required options for this channel. Called at registration time (fail fast).
    /// Implementations throw <see cref="ArgumentException"/> when a required value is missing.
    /// </summary>
    internal abstract void Validate();
}
