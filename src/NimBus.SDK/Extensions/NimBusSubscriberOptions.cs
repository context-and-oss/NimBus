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
    /// Store the failing handler exception's stack trace on its error response (Spec 044 Phase 3),
    /// so operators and, when a site Owner allows it, failure-handling agents can locate the
    /// defect in the adapter's code. Frames only, cut at 8,000 characters. Off by default.
    /// </summary>
    public bool IncludeStackTraceInErrorResponses { get; set; }

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
        ArgumentNullException.ThrowIfNull(configure);
        var cloudEvents = new CloudEventSubscriberOptions();
        configure(cloudEvents);
        CloudEvents = cloudEvents;
    }
}
