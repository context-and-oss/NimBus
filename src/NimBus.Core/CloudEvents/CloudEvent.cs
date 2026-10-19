using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace NimBus.Core.CloudEvents;

/// <summary>
/// Provider-neutral representation of a CloudEvents 1.0 context object.
/// <para>
/// This models the CloudEvents 1.0 core attributes plus arbitrary extension
/// attributes. It is transport-agnostic: the Azure Service Bus binding
/// (<c>NimBus.ServiceBus</c>) maps it to/from an AMQP message, and it is
/// exposed to handlers/middleware via <c>IMessageContext.GetCloudEvent()</c>.
/// </para>
/// <para>
/// CloudEvents is an <em>opt-in interoperability layer</em> over NimBus's
/// native messaging — a message is only ever represented as a
/// <see cref="CloudEvent"/> when the publisher/subscriber has explicitly
/// enabled it. Native messaging is unaffected.
/// </para>
/// </summary>
public sealed class CloudEvent
{
    /// <summary>The only CloudEvents spec version NimBus emits and validates.</summary>
    public const string CloudEventsSpecVersion = "1.0";

    /// <summary>CloudEvents <c>specversion</c> attribute (required, must be "1.0").</summary>
    public string? SpecVersion { get; set; } = CloudEventsSpecVersion;

    /// <summary>CloudEvents <c>id</c> attribute (required).</summary>
    public string Id { get; set; } = null!;

    /// <summary>CloudEvents <c>source</c> attribute (required).</summary>
    public string Source { get; set; } = null!;

    /// <summary>CloudEvents <c>type</c> attribute (required).</summary>
    public string Type { get; set; } = null!;

    /// <summary>CloudEvents <c>subject</c> attribute (optional).</summary>
    public string? Subject { get; set; }

    /// <summary>CloudEvents <c>time</c> attribute (optional).</summary>
    public DateTimeOffset? Time { get; set; }

    /// <summary>CloudEvents <c>datacontenttype</c> attribute (optional).</summary>
    public string? DataContentType { get; set; }

    /// <summary>CloudEvents <c>dataschema</c> attribute (optional).</summary>
    public string? DataSchema { get; set; }

    /// <summary>
    /// The event payload as a JSON string. For NimBus this is the serialized
    /// domain event (<c>EventContent.EventJson</c>), NOT the NimBus
    /// <c>MessageContent</c> envelope.
    /// </summary>
    public string? Data { get; set; }

    /// <summary>
    /// CloudEvents extension attributes (e.g. <c>correlationid</c>,
    /// <c>sessionid</c>) carried alongside the core attributes.
    /// </summary>
    public IDictionary<string, string> Extensions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Attempts to read an extension attribute value.</summary>
    public bool TryGetExtension(string name, [MaybeNullWhen(false)] out string value) =>
        Extensions.TryGetValue(name, out value);
}
