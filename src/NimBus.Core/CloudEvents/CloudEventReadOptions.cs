using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;

namespace NimBus.Core.CloudEvents;

/// <summary>
/// Consume-side CloudEvents configuration handed to the transport adapter and
/// message context. When present, a subscriber detects and normalizes inbound
/// CloudEvents; when null, the subscriber is pure native NimBus.
/// </summary>
public sealed class CloudEventReadOptions
{
    /// <summary>Default AMQP application-property prefixes accepted on consume.</summary>
    public static readonly IReadOnlyList<string> DefaultAcceptedPrefixes =
        new[] { "cloudEvents:", "ce-" };

    /// <summary>
    /// Compatibility mode. <see cref="CompatibilityMode.AutoDetect"/> handles a
    /// mix of native and CloudEvents messages on the same subscription;
    /// <see cref="CompatibilityMode.CloudEventsBinary"/>/<see cref="CompatibilityMode.CloudEventsStructuredJson"/>
    /// still detect per message (a native control message is always parsed natively).
    /// </summary>
    public CompatibilityMode Mode { get; set; } = CompatibilityMode.AutoDetect;

    /// <summary>
    /// AMQP application-property prefixes accepted when detecting/parsing binary
    /// CloudEvents. Defaults to <c>cloudEvents:</c> (the standard AMQP binding
    /// prefix) and <c>ce-</c> (a widely-used alternate) for maximum external
    /// producer compatibility.
    /// </summary>
    public IReadOnlyList<string> AcceptedPrefixes { get; set; } = DefaultAcceptedPrefixes;

    /// <summary>
    /// Maps a CloudEvents <c>type</c> to a NimBus dispatch key (EventTypeId).
    /// Defaults to the last dot-delimited segment (so <c>com.acme.OrderPlaced</c>
    /// resolves to <c>OrderPlaced</c>, matching NimBus's unqualified EventTypeId).
    /// </summary>
    public Func<string, string> TypeToEventTypeId { get; set; } = DefaultTypeToEventTypeId;

    /// <summary>NimBus ↔ CloudEvents attribute mapping (correlation id / session id).</summary>
    public CloudEventMapping Mapping { get; set; } = new CloudEventMapping();

    /// <summary>Resolves the NimBus dispatch key for a CloudEvents <c>type</c>.</summary>
    [return: NotNullIfNotNull(nameof(cloudEventType))]
    public string? MapType(string? cloudEventType) =>
        cloudEventType is null ? null : (TypeToEventTypeId ?? DefaultTypeToEventTypeId)(cloudEventType);

    /// <summary>Reads the mapped <c>CorrelationId</c> from a CloudEvent.</summary>
    public string? MapCorrelationId(CloudEvent cloudEvent) => Mapping.ReadCorrelationId(cloudEvent);

    /// <summary>Reads the mapped <c>SessionId</c> from a CloudEvent.</summary>
    public string? MapSessionId(CloudEvent cloudEvent) => Mapping.ReadSessionId(cloudEvent);

    private static string DefaultTypeToEventTypeId(string cloudEventType)
    {
        if (string.IsNullOrEmpty(cloudEventType)) return cloudEventType;
        var lastDot = cloudEventType.LastIndexOf('.');
        return lastDot >= 0 && lastDot < cloudEventType.Length - 1
            ? cloudEventType[(lastDot + 1)..]
            : cloudEventType;
    }
}
