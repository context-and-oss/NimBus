using System;
using System.Collections.Generic;
using NimBus.Core.CloudEvents;

namespace NimBus.SDK.Extensions;

/// <summary>
/// Opt-in CloudEvents configuration for a NimBus subscriber. Set via
/// <see cref="NimBusSubscriberOptions.UseCloudEvents"/>. When configured, the
/// subscriber detects and normalizes inbound CloudEvents produced by external
/// (non-NimBus) systems, mapping them into the NimBus message context and
/// dispatching to the matching <c>IEventHandler&lt;T&gt;</c>.
/// </summary>
public sealed class CloudEventSubscriberOptions
{
    /// <summary>
    /// Compatibility mode. Defaults to <see cref="CompatibilityMode.AutoDetect"/>
    /// (handle both native and CloudEvents on the same subscription).
    /// </summary>
    public CompatibilityMode Mode { get; set; } = CompatibilityMode.AutoDetect;

    /// <summary>
    /// AMQP application-property prefixes accepted when detecting/parsing binary
    /// CloudEvents. Defaults to <c>cloudEvents:</c> and the alternate <c>ce-</c>.
    /// </summary>
    public IReadOnlyList<string> AcceptedPrefixes { get; set; } = CloudEventReadOptions.DefaultAcceptedPrefixes;

    /// <summary>
    /// Maps a CloudEvents <c>type</c> to a NimBus dispatch key (EventTypeId).
    /// Defaults to the unqualified last dot-segment of the type.
    /// </summary>
    public Func<string, string>? TypeToEventTypeId { get; set; }

    /// <summary>Configurable NimBus ↔ CloudEvents attribute mapping.</summary>
    public CloudEventMapping Mapping { get; set; } = new CloudEventMapping();

    /// <summary>Builds the transport-level read options from these subscriber options.</summary>
    public CloudEventReadOptions ToReadOptions()
    {
        var readOptions = new CloudEventReadOptions
        {
            Mode = Mode,
            AcceptedPrefixes = AcceptedPrefixes ?? CloudEventReadOptions.DefaultAcceptedPrefixes,
            Mapping = Mapping ?? new CloudEventMapping(),
        };
        if (TypeToEventTypeId != null) readOptions.TypeToEventTypeId = TypeToEventTypeId;
        return readOptions;
    }
}
