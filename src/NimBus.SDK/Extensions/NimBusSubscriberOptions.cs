using System;

namespace NimBus.SDK.Extensions;

/// <summary>
/// Configuration options for a NimBus subscriber registered via DI.
/// </summary>
public class NimBusSubscriberOptions
{
    /// <summary>
    /// The endpoint (topic name) for sending responses.
    /// </summary>
    public string Endpoint { get; set; } = null!;

    /// <summary>
    /// Optional entity path (queue name or topic/subscription) for receiving deferred messages.
    /// </summary>
    public string? EntityPath { get; set; }

    /// <summary>
    /// Opt-in CloudEvents consume configuration, or <c>null</c> when the
    /// subscriber only handles native NimBus messages (the default). Set via
    /// <see cref="UseCloudEvents"/>.
    /// </summary>
    public CloudEventSubscriberOptions? CloudEvents { get; private set; }

    /// <summary>
    /// Enables CloudEvents 1.0 consumption on this subscriber. Inbound
    /// CloudEvents (binary or structured) are detected, normalized into the
    /// NimBus message context, and dispatched to the matching handler by their
    /// <c>type</c>. Not calling this method keeps native NimBus behavior.
    /// </summary>
    /// <param name="configure">Configures the CloudEvents consume options.</param>
    public void UseCloudEvents(Action<CloudEventSubscriberOptions> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        var cloudEvents = new CloudEventSubscriberOptions();
        configure(cloudEvents);
        CloudEvents = cloudEvents;
    }
}
