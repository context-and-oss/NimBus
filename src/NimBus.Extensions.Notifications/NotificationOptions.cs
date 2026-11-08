namespace NimBus.Extensions.Notifications;

/// <summary>
/// Configuration options for the Notifications extension.
/// </summary>
public class NotificationOptions
{
    /// <summary>
    /// Whether to send notifications on message failures (handler exceptions). Default: true.
    /// </summary>
    public bool NotifyOnFailure { get; set; } = true;

    /// <summary>
    /// Whether to send notifications when messages are dead-lettered. Default: true.
    /// </summary>
    public bool NotifyOnDeadLetter { get; set; } = true;

    /// <summary>
    /// Whether to send notifications when messages are received (for monitoring). Default: false.
    /// </summary>
    public bool NotifyOnReceived { get; set; }

    /// <summary>
    /// Whether to send notifications on successful completion. Default: false.
    /// </summary>
    public bool NotifyOnCompleted { get; set; }

    /// <summary>
    /// Whether to send a notification when a message arrives for a session that is blocked by an
    /// earlier failed event. Default: false on the legacy registration paths; the fluent
    /// channel-builder API (<see cref="NotificationsBuilderExtensions.AddNotifications(NimBus.Core.Extensions.INimBusBuilder, System.Action{NotificationChannelBuilder}, System.Action{NotificationOptions})"/>
    /// and <c>AddNimBusNotifications</c>) defaults it to <c>true</c> so production deployments
    /// alert on session blocks out of the box.
    /// </summary>
    public bool NotifyOnSessionBlock { get; set; }

    /// <summary>Whether to notify when an endpoint circuit opens or recovers. Default: true.</summary>
    public bool NotifyOnCircuitOpen { get; set; } = true;
}
