using System;

namespace NimBus.SDK.Extensions;

/// <summary>
/// Configuration options for a NimBus publisher registered via DI.
/// </summary>
public class NimBusPublisherOptions
{
    /// <summary>
    /// The endpoint (topic name) to publish messages to.
    /// </summary>
    public string Endpoint { get; set; } = null!;

    /// <summary>
    /// Opt-in CloudEvents publish configuration, or <c>null</c> when the
    /// publisher emits native NimBus messages (the default). Set via
    /// <see cref="UseCloudEvents"/>.
    /// </summary>
    public CloudEventPublisherOptions? CloudEvents { get; private set; }

    /// <summary>
    /// Enables CloudEvents 1.0 emission on this publisher. Every published
    /// event is wrapped as a CloudEvent in the configured content mode. Not
    /// calling this method keeps the native NimBus wire format unchanged.
    /// </summary>
    /// <param name="configure">Configures the CloudEvents publish options.</param>
    public void UseCloudEvents(Action<CloudEventPublisherOptions> configure)
    {
        if (configure is null) throw new ArgumentNullException(nameof(configure));
        var cloudEvents = new CloudEventPublisherOptions();
        configure(cloudEvents);
        CloudEvents = cloudEvents;
    }
}
