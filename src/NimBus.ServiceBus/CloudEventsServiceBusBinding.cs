using System.Diagnostics.CodeAnalysis;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using NimBus.Core.CloudEvents;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace NimBus.ServiceBus;

/// <summary>
/// Azure Service Bus (AMQP) binding for CloudEvents 1.0. Maps a provider-neutral
/// <see cref="CloudEvent"/> to/from a Service Bus message in either binary or
/// structured content mode, following the CloudEvents AMQP protocol binding
/// (<c>cloudEvents:</c> property prefix) with an accepted alternate prefix
/// (<c>ce-</c>) for external-producer compatibility.
/// </summary>
public static class CloudEventsServiceBusBinding
{
    /// <summary>Canonical AMQP application-property prefix NimBus writes on publish.</summary>
    public const string CanonicalPrefix = "cloudEvents:";

    /// <summary>Alternate accepted prefix on consume.</summary>
    public const string AlternatePrefix = "ce-";

    /// <summary>Content-type identifying a structured CloudEvents JSON envelope.</summary>
    public const string StructuredContentType = "application/cloudevents+json";

    // Core context attribute names (without prefix).
    private const string AttrSpecVersion = "specversion";
    private const string AttrId = "id";
    private const string AttrSource = "source";
    private const string AttrType = "type";
    private const string AttrSubject = "subject";
    private const string AttrTime = "time";
    private const string AttrDataSchema = "dataschema";
    private const string AttrDataContentType = "datacontenttype";
    private const string AttrData = "data";

    private static readonly string[] CoreAttributeNames =
    {
        AttrSpecVersion, AttrId, AttrSource, AttrType, AttrSubject, AttrTime,
        AttrDataSchema, AttrDataContentType, AttrData,
    };

    /// <summary>
    /// Writes CloudEvents context attributes as AMQP application properties
    /// (binary content mode) and sets the AMQP content-type to the data content
    /// type. The caller is responsible for setting the body to the raw domain event.
    /// </summary>
    public static void WriteBinary(Azure.Messaging.ServiceBus.ServiceBusMessage message, CloudEvent cloudEvent)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        if (cloudEvent is null) throw new ArgumentNullException(nameof(cloudEvent));

        SetProp(message, AttrSpecVersion, cloudEvent.SpecVersion ?? CloudEvent.CloudEventsSpecVersion);
        SetProp(message, AttrId, cloudEvent.Id);
        SetProp(message, AttrSource, cloudEvent.Source);
        SetProp(message, AttrType, cloudEvent.Type);
        SetProp(message, AttrSubject, cloudEvent.Subject);
        if (cloudEvent.Time.HasValue)
            SetProp(message, AttrTime, cloudEvent.Time.Value.ToString("o", CultureInfo.InvariantCulture));
        SetProp(message, AttrDataSchema, cloudEvent.DataSchema);

        foreach (var extension in cloudEvent.Extensions)
            SetProp(message, extension.Key, extension.Value);

        // In binary mode the CloudEvents datacontenttype maps to the AMQP content-type.
        message.ContentType = cloudEvent.DataContentType ?? "application/json";
    }

    /// <summary>
    /// Builds a CloudEvents 1.0 structured JSON envelope, sets the AMQP
    /// content-type to <see cref="StructuredContentType"/>, and returns the
    /// serialized envelope for the caller to use as the message body.
    /// </summary>
    public static string WriteStructured(
        Azure.Messaging.ServiceBus.ServiceBusMessage message,
        CloudEvent cloudEvent,
        string domainEventJson)
    {
        if (message is null) throw new ArgumentNullException(nameof(message));
        if (cloudEvent is null) throw new ArgumentNullException(nameof(cloudEvent));

        var envelope = new JObject
        {
            [AttrSpecVersion] = cloudEvent.SpecVersion ?? CloudEvent.CloudEventsSpecVersion,
            [AttrId] = cloudEvent.Id,
            [AttrSource] = cloudEvent.Source,
            [AttrType] = cloudEvent.Type,
        };

        if (!string.IsNullOrEmpty(cloudEvent.Subject)) envelope[AttrSubject] = cloudEvent.Subject;
        if (cloudEvent.Time.HasValue) envelope[AttrTime] = cloudEvent.Time.Value.ToString("o", CultureInfo.InvariantCulture);
        envelope[AttrDataContentType] = cloudEvent.DataContentType ?? "application/json";
        if (!string.IsNullOrEmpty(cloudEvent.DataSchema)) envelope[AttrDataSchema] = cloudEvent.DataSchema;

        foreach (var extension in cloudEvent.Extensions)
            envelope[extension.Key] = extension.Value;

        // data carries the domain event as embedded JSON, not a string.
        envelope[AttrData] = string.IsNullOrEmpty(domainEventJson)
            ? JValue.CreateNull()
            : JToken.Parse(domainEventJson);

        message.ContentType = StructuredContentType;
        return envelope.ToString(Formatting.None);
    }

    /// <summary>
    /// Detects and parses a CloudEvent from an inbound Service Bus message.
    /// Returns <c>true</c> when the message carries CloudEvents markers (a
    /// structured content-type, or any accepted-prefix context attribute) —
    /// even when required attributes are missing, so the consume pipeline can
    /// dead-letter an invalid CloudEvent with a clear reason rather than
    /// mis-parsing it as native.
    /// </summary>
    public static bool TryParse(IServiceBusMessage message, CloudEventReadOptions options, [NotNullWhen(true)] out CloudEvent? cloudEvent)
    {
        cloudEvent = null;
        if (message is null || options is null) return false;

        var prefixes = options.AcceptedPrefixes ?? CloudEventReadOptions.DefaultAcceptedPrefixes;
        var contentType = message.ContentType;
        var structured = contentType != null &&
            contentType.StartsWith(StructuredContentType, StringComparison.OrdinalIgnoreCase);

        // Binary detection: any core context attribute present under any accepted prefix.
        string? specVersion = Probe(message, prefixes, AttrSpecVersion);
        string? id = Probe(message, prefixes, AttrId);
        string? source = Probe(message, prefixes, AttrSource);
        string? type = Probe(message, prefixes, AttrType);
        var binaryDetected = specVersion != null || id != null || source != null || type != null;

        if (!structured && !binaryDetected) return false;

        cloudEvent = structured
            ? ParseStructured(message.Body, options)
            : ParseBinary(message, prefixes, specVersion, id, source, type, contentType);
        return true;
    }

    private static CloudEvent ParseBinary(
        IServiceBusMessage message,
        IReadOnlyList<string> prefixes,
        string? specVersion,
        string? id,
        string? source,
        string? type,
        string? contentType)
    {
        // Id/Source/Type stay null when the producer omitted them: the event is
        // detected-but-invalid and is dead-lettered downstream.
        var cloudEvent = new CloudEvent
        {
            SpecVersion = specVersion,
            Id = id!,
            Source = source!,
            Type = type!,
            Subject = Probe(message, prefixes, AttrSubject),
            DataSchema = Probe(message, prefixes, AttrDataSchema),
            DataContentType = contentType,
            Data = message.Body is { Length: > 0 } body ? Encoding.UTF8.GetString(body) : null,
        };

        var time = Probe(message, prefixes, AttrTime);
        if (!string.IsNullOrEmpty(time) &&
            DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedTime))
        {
            cloudEvent.Time = parsedTime;
        }

        // Every prefixed application property whose suffix is not a core context
        // attribute is a CloudEvents extension attribute — mapping-configured
        // names (correlationid/sessionid) as well as arbitrary custom extensions
        // written by an external producer or NimBus's own Extensions hook. Copy
        // them all so they survive round-trip and are visible via GetCloudEvent().
        foreach (var propertyName in message.GetUserPropertyNames())
        {
            foreach (var prefix in prefixes)
            {
                if (!propertyName.StartsWith(prefix, StringComparison.Ordinal)) continue;

                var attribute = propertyName.Substring(prefix.Length);
                if (attribute.Length == 0 || Array.IndexOf(CoreAttributeNames, attribute) >= 0) break;

                var value = message.GetUserProperty(propertyName);
                if (value != null) cloudEvent.Extensions[attribute] = value;
                break;
            }
        }

        return cloudEvent;
    }

    private static CloudEvent ParseStructured(byte[] body, CloudEventReadOptions options)
    {
        var cloudEvent = new CloudEvent { SpecVersion = null };
        if (body is null || body.Length == 0) return cloudEvent;

        JObject envelope;
        try
        {
            envelope = JObject.Parse(Encoding.UTF8.GetString(body));
        }
        catch (JsonException)
        {
            return cloudEvent; // detected-but-invalid → dead-letter downstream
        }

        // Id/Source/Type stay null when absent (detected-but-invalid, see ParseBinary).
        cloudEvent.SpecVersion = (string?)envelope[AttrSpecVersion];
        cloudEvent.Id = ((string?)envelope[AttrId])!;
        cloudEvent.Source = ((string?)envelope[AttrSource])!;
        cloudEvent.Type = ((string?)envelope[AttrType])!;
        cloudEvent.Subject = (string?)envelope[AttrSubject];
        cloudEvent.DataContentType = (string?)envelope[AttrDataContentType];
        cloudEvent.DataSchema = (string?)envelope[AttrDataSchema];

        var time = (string?)envelope[AttrTime];
        if (!string.IsNullOrEmpty(time) &&
            DateTimeOffset.TryParse(time, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedTime))
        {
            cloudEvent.Time = parsedTime;
        }

        var data = envelope[AttrData];
        if (data != null && data.Type != JTokenType.Null)
        {
            cloudEvent.Data = data.Type == JTokenType.String
                ? (string?)data
                : data.ToString(Formatting.None);
        }

        // Any non-core top-level property is a CloudEvents extension attribute.
        foreach (var property in envelope.Properties())
        {
            if (Array.IndexOf(CoreAttributeNames, property.Name) >= 0) continue;
            if (property.Value.Type == JTokenType.String)
                cloudEvent.Extensions[property.Name] = ((string?)property.Value)!;
            else if (property.Value.Type is not (JTokenType.Object or JTokenType.Array or JTokenType.Null))
                cloudEvent.Extensions[property.Name] = property.Value.ToString(Formatting.None);
        }

        _ = options; // reserved for future structured-mode mapping hooks
        return cloudEvent;
    }

    private static string? Probe(IServiceBusMessage message, IReadOnlyList<string> prefixes, string attribute)
    {
        foreach (var prefix in prefixes)
        {
            var value = message.GetUserProperty(prefix + attribute);
            if (value != null) return value;
        }
        return null;
    }

    private static void SetProp(Azure.Messaging.ServiceBus.ServiceBusMessage message, string attribute, string? value)
    {
        if (!string.IsNullOrEmpty(value))
            message.ApplicationProperties[CanonicalPrefix + attribute] = value;
    }
}
