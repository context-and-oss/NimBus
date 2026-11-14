using System;
using System.Collections.Generic;
using NimBus.Core.CloudEvents;
using NimBus.Core.Events;

namespace NimBus.SDK.Extensions;

/// <summary>
/// Opt-in CloudEvents configuration for a NimBus publisher. Set via
/// <see cref="NimBusPublisherOptions.UseCloudEvents"/>. When configured, every
/// published event is emitted as a CloudEvent in the chosen content mode.
/// </summary>
public sealed class CloudEventPublisherOptions
{
    /// <summary>
    /// CloudEvents <c>source</c> attribute (required for interoperability, e.g.
    /// <c>urn:customer:billing</c>). Identifies the producing system/adapter.
    /// </summary>
    public Uri? Source { get; set; }

    /// <summary>
    /// Strategy for deriving the CloudEvents <c>type</c> from the event class.
    /// Defaults to <see cref="CloudEventTypeNameStrategy.UnqualifiedName"/>.
    /// </summary>
    public CloudEventTypeNameStrategy TypeNameStrategy { get; set; } = CloudEventTypeNameStrategy.UnqualifiedName;

    /// <summary>
    /// Optional override for the CloudEvents <c>type</c>. When set it takes
    /// precedence over <see cref="TypeNameStrategy"/>.
    /// </summary>
    public Func<IEvent, string>? TypeOverride { get; set; }

    /// <summary>Content mode. Defaults to <see cref="CloudEventContentMode.Binary"/>.</summary>
    public CloudEventContentMode ContentMode { get; set; } = CloudEventContentMode.Binary;

    /// <summary>Data content type. Defaults to <c>application/json</c>.</summary>
    public string DataContentType { get; set; } = "application/json";

    /// <summary>Optional factory for the CloudEvents <c>subject</c> attribute.</summary>
    public Func<IEvent, string>? Subject { get; set; }

    /// <summary>Optional factory for the CloudEvents <c>time</c> attribute (defaults to publish time).</summary>
    public Func<IEvent, DateTimeOffset>? Time { get; set; }

    /// <summary>Optional CloudEvents <c>dataschema</c> attribute (schema reference).</summary>
    public Uri? DataSchema { get; set; }

    /// <summary>
    /// Optional hook to add custom CloudEvents extension attributes per event.
    /// The dictionary passed in is the CloudEvent's extension collection.
    /// </summary>
    public Action<IEvent, IDictionary<string, string>>? Extensions { get; set; }

    /// <summary>Configurable NimBus ↔ CloudEvents attribute mapping.</summary>
    public CloudEventMapping Mapping { get; set; } = new CloudEventMapping();
}
