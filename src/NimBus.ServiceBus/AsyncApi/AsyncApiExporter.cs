using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using YamlDotNet.Serialization;
using CoreAsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;
using Map = System.Collections.Generic.Dictionary<string, object>;

namespace NimBus.ServiceBus.AsyncApi;

/// <summary>
/// Generates an AsyncAPI 3.0 document from an <see cref="IPlatform"/>.
/// <para>
/// The document is a <em>hybrid</em> view: portable logical channels/operations for developer
/// portals, enriched with Azure Service Bus specifics (the auto-forward hops, subscription rules,
/// session/dead-letter settings and <c>user.*</c> application-property conventions) carried via
/// AsyncAPI specification extensions (<c>x-servicebus*</c> / <c>x-nimbus*</c>) because no official
/// AsyncAPI Service Bus binding exists. This bridges the logical event contract and NimBus's real
/// topic-per-endpoint topology.
/// </para>
/// </summary>
public static class AsyncApiExporter
{
    // Kept in lock-step with ServiceBusTopologyProvisioner so the documented rules match what
    // provisioning actually creates. If those SQL expressions change, change them here too.
    private static string ForwardFilter(string eventTypeId) =>
        $"user.EventTypeId = '{eventTypeId}' AND user.From IS NULL";

    private static string ForwardAction(string from, string to) =>
        $"SET user.From = '{from}'; SET user.EventId = newid(); SET user.To = '{to}';";

    private static string DeliveryFilter(string endpointId) => $"user.To = '{endpointId}'";

    /// <summary>Exports an arbitrary platform (WebApp export endpoint, external integration repos, samples, tests).</summary>
    public static async Task ExportAsync(
        IPlatform platform,
        string outputPath,
        CoreAsyncApiFormat format,
        AsyncApiEnrichmentRegistry? enrichment = null)
    {
        if (platform is null) throw new ArgumentNullException(nameof(platform));

        var content = Serialize(platform, format, enrichment);
        await File.WriteAllTextAsync(outputPath, content);

        var endpointCount = platform.Endpoints.Count();
        var eventCount = platform.EventTypes.Count() + platform.DynamicForwards.Select(f => f.EventTypeId).Distinct().Count();
        Console.WriteLine($"AsyncAPI 3.0 spec exported to: {outputPath}");
        Console.WriteLine($"  {endpointCount} endpoints, {eventCount} event types ({format.ToString().ToUpperInvariant()})");
    }

    /// <summary>Builds the AsyncAPI document for <paramref name="platform"/> and serializes it.</summary>
    /// <param name="platform">The platform whose topology and event contracts are exported.</param>
    /// <param name="format">YAML or JSON.</param>
    /// <param name="enrichment">
    /// Optional fluent enrichment (from <c>Publish&lt;T&gt;(o =&gt; o.AsyncApi…)</c>). Merged with any
    /// <see cref="AsyncApiMessageAttribute"/> on each contract (fluent wins scalars; tags/examples union;
    /// deprecated OR-ed). When null, only attribute enrichment applies.
    /// </param>
    public static string Serialize(IPlatform platform, CoreAsyncApiFormat format, AsyncApiEnrichmentRegistry? enrichment = null)
    {
        if (platform is null) throw new ArgumentNullException(nameof(platform));

        var document = BuildDocument(platform, enrichment);
        return format == CoreAsyncApiFormat.Json
            ? JsonConvert.SerializeObject(document, Formatting.Indented)
            // WithQuotingNecessaryStrings so values that look like numbers/bools/null (e.g. a
            // protocolVersion of "1.0", or an event/field literally named "123" or "true") round-trip
            // as strings instead of being reinterpreted by a YAML parser.
            : new SerializerBuilder().WithQuotingNecessaryStrings().Build().Serialize(document);
    }

    private static Map BuildDocument(IPlatform platform, AsyncApiEnrichmentRegistry? enrichment = null)
    {
        var endpointsById = platform.Endpoints
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var events = platform.EventTypes
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

        var dynamicForwards = platform.DynamicForwards
            .OrderBy(f => f.EventTypeId, StringComparer.Ordinal)
            .ThenBy(f => f.TargetEndpoint, StringComparer.Ordinal)
            .ToList();

        // Which event types appear on each endpoint's topic (produced here, or auto-forwarded in).
        var topicMessages = new SortedDictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        void OnTopic(string endpointId, string eventId)
        {
            if (!topicMessages.TryGetValue(endpointId, out var set))
            {
                set = new SortedSet<string>(StringComparer.Ordinal);
                topicMessages[endpointId] = set;
            }
            set.Add(eventId);
        }

        foreach (var evt in events)
        {
            foreach (var producer in platform.GetProducers(evt)) OnTopic(producer.Id, evt.Id);
            foreach (var consumer in platform.GetConsumers(evt)) OnTopic(consumer.Id, evt.Id);
        }
        foreach (var fwd in dynamicForwards)
        {
            OnTopic(fwd.SourceEndpoint, fwd.EventTypeId);
            OnTopic(fwd.TargetEndpoint, fwd.EventTypeId);
        }

        var channels = BuildChannels(topicMessages, endpointsById);
        var operations = BuildOperations(platform, events, dynamicForwards, endpointsById);
        // CloudEvents-enabled endpoints (those implementing ICloudEventsAware) get an
        // x-cloudevents channel extension and a shared CloudEventsMessageHeaders schema.
        // Native endpoints are untouched, so the export is unchanged when none opt in.
        var anyCloudEvents = endpointsById.Values.Any(e => e is ICloudEventsAware);
        var components = BuildComponents(events, dynamicForwards, enrichment, anyCloudEvents);

        return new Map
        {
            ["asyncapi"] = "3.0.0",
            ["info"] = new Map
            {
                ["title"] = "NimBus Platform",
                ["version"] = "1.0.0",
                ["description"] =
                    "Event contracts and Azure Service Bus topology generated from NimBus. " +
                    "Each endpoint is a Service Bus topic; events are routed by SQL rules on application " +
                    "properties and auto-forwarded between topics. See the x-servicebus* extensions for the " +
                    "physical delivery details behind each logical operation.",
            },
            ["servers"] = BuildServers(),
            ["channels"] = channels,
            ["operations"] = operations,
            ["components"] = components,
        };
    }

    private static Map BuildServers() => new()
    {
        ["production"] = new Map
        {
            ["host"] = "{namespace}.servicebus.windows.net",
            ["protocol"] = "amqp",
            ["protocolVersion"] = "1.0",
            ["description"] = "Azure Service Bus namespace (AMQP 1.0).",
            // No official AsyncAPI Service Bus binding exists; declare the closest standard binding.
            ["bindings"] = new Map { ["amqp1"] = new Map() },
            ["x-nimbus-topology"] = new Map
            {
                ["pattern"] = "topic-per-endpoint",
                ["routing"] = "sql-rules-on-application-properties",
                ["autoForwarding"] = true,
                ["resolverTopic"] = Constants.ResolverId,
                ["auditSubscription"] = Constants.ResolverId,
                ["deferredSubscriptions"] = new List<object> { Constants.DeferredSubscriptionName, Constants.DeferredProcessorId },
                ["routingProperties"] = new[]
                    {
                        UserPropertyName.To, UserPropertyName.From, UserPropertyName.EventTypeId,
                        UserPropertyName.MessageType, UserPropertyName.EventId, UserPropertyName.RetryCount,
                        UserPropertyName.OriginatingFrom, UserPropertyName.OriginalSessionId,
                    }
                    .Select(p => (object)p.ToString())
                    .ToList(),
            },
        },
    };

    private static Map BuildChannels(
        SortedDictionary<string, SortedSet<string>> topicMessages,
        IReadOnlyDictionary<string, IEndpoint> endpointsById)
    {
        var channels = new Map();
        foreach (var (endpointId, eventIds) in topicMessages)
        {
            var name = endpointsById.TryGetValue(endpointId, out var ep) ? ep.Name : endpointId;
            var messages = new Map();
            foreach (var eventId in eventIds)
            {
                messages[eventId] = new Map { ["$ref"] = $"#/components/messages/{eventId}" };
            }

            var channel = new Map
            {
                ["address"] = endpointId,
                ["description"] = $"Azure Service Bus topic for {name}.",
                ["messages"] = messages,
                ["bindings"] = new Map { ["amqp1"] = new Map() },
                ["x-servicebus"] = new Map
                {
                    ["resourceType"] = "topic",
                    ["topic"] = endpointId,
                    ["supportsOrdering"] = true,
                    ["duplicateDetectionHistoryTimeWindow"] = "PT10M",
                },
            };

            if (ep is ICloudEventsAware cloudEvents)
            {
                channel["x-cloudevents"] = BuildCloudEventsChannelExtension(cloudEvents);
            }

            channels[endpointId] = channel;
        }

        return channels;
    }

    // Documents that this endpoint's topic carries CloudEvents 1.0 messages (content
    // mode + attribute set), pointing at the shared CloudEventsMessageHeaders schema.
    private static Map BuildCloudEventsChannelExtension(ICloudEventsAware cloudEvents) => new()
    {
        ["specversion"] = "1.0",
        ["contentMode"] = string.IsNullOrEmpty(cloudEvents.CloudEventsContentMode) ? "binary" : cloudEvents.CloudEventsContentMode,
        ["source"] = cloudEvents.CloudEventsSource!,
        ["attributes"] = new List<object> { "id", "source", "type", "specversion", "subject", "time", "datacontenttype", "dataschema" },
        ["headers"] = new Map { ["$ref"] = "#/components/schemas/CloudEventsMessageHeaders" },
    };

    private static Map BuildOperations(
        IPlatform platform,
        IReadOnlyList<IEventType> events,
        IReadOnlyList<DynamicForward> dynamicForwards,
        IReadOnlyDictionary<string, IEndpoint> endpointsById)
    {
        var operations = new Map();

        foreach (var evt in events)
        {
            var producers = platform.GetProducers(evt).OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
            var consumers = platform.GetConsumers(evt).OrderBy(e => e.Id, StringComparer.Ordinal).ToList();

            foreach (var producer in producers)
            {
                operations[$"{producer.Id}_send_{OpKey(evt.Id)}"] = SendOperation(producer, evt.Id, evt.Name);
            }

            foreach (var consumer in consumers)
            {
                // Auto-forward sources are every OTHER producer's topic (the provisioner never
                // creates a self-forward for an event a single endpoint both produces and consumes).
                var forwards = producers
                    .Where(p => !string.Equals(p.Id, consumer.Id, StringComparison.Ordinal))
                    .Select(p => ForwardSubscription(p.Id, consumer.Id, evt.Id, subscriptionName: consumer.Id))
                    .ToList();

                operations[$"{consumer.Id}_receive_{OpKey(evt.Id)}"] =
                    ReceiveOperation(consumer, evt.Id, evt.Name, forwards);
            }
        }

        foreach (var fwd in dynamicForwards)
        {
            var sourceName = endpointsById.TryGetValue(fwd.SourceEndpoint, out var s) ? s.Name : fwd.SourceEndpoint;
            var targetName = endpointsById.TryGetValue(fwd.TargetEndpoint, out var t) ? t.Name : fwd.TargetEndpoint;

            operations[$"{fwd.SourceEndpoint}_send_{OpKey(fwd.EventTypeId)}"] =
                SendOperation(fwd.SourceEndpoint, sourceName, fwd.EventTypeId);

            var forward = ForwardSubscription(
                fwd.SourceEndpoint, fwd.TargetEndpoint, fwd.EventTypeId,
                subscriptionName: $"AgentDyn-{fwd.TargetEndpoint}");

            operations[$"{fwd.TargetEndpoint}_receive_{OpKey(fwd.EventTypeId)}"] =
                ReceiveOperation(fwd.TargetEndpoint, targetName, fwd.EventTypeId, new List<Map> { forward });
        }

        return operations;
    }

    private static Map SendOperation(IEndpoint producer, string eventId, string eventName) =>
        SendOperation(producer.Id, producer.Name, eventId, eventName);

    private static Map SendOperation(string producerId, string producerName, string eventId, string? eventName = null) =>
        new()
        {
            ["action"] = "send",
            ["title"] = $"{producerName} publishes {eventName ?? eventId}",
            ["channel"] = new Map { ["$ref"] = $"#/channels/{producerId}" },
            ["messages"] = new List<object> { new Map { ["$ref"] = $"#/channels/{producerId}/messages/{eventId}" } },
        };

    private static Map ReceiveOperation(IEndpoint consumer, string eventId, string eventName, List<Map> forwards) =>
        ReceiveOperation(consumer.Id, consumer.Name, eventId, forwards, eventName);

    private static Map ReceiveOperation(string consumerId, string consumerName, string eventId, List<Map> forwards, string? eventName = null) =>
        new()
        {
            ["action"] = "receive",
            ["title"] = $"{consumerName} consumes {eventName ?? eventId}",
            ["channel"] = new Map { ["$ref"] = $"#/channels/{consumerId}" },
            ["messages"] = new List<object> { new Map { ["$ref"] = $"#/channels/{consumerId}/messages/{eventId}" } },
            ["x-servicebus-delivery"] = new Map
            {
                ["deliverySubscription"] = new Map
                {
                    ["topic"] = consumerId,
                    ["subscription"] = consumerId,
                    ["requiresSession"] = true,
                    ["filter"] = DeliveryFilter(consumerId),
                },
                ["forwardSubscriptions"] = forwards.Cast<object>().ToList(),
            },
        };

    private static Map ForwardSubscription(string sourceTopic, string target, string eventId, string subscriptionName) =>
        new()
        {
            ["topic"] = sourceTopic,
            ["subscription"] = subscriptionName,
            ["forwardTo"] = target,
            ["requiresSession"] = false,
            ["filter"] = ForwardFilter(eventId),
            ["action"] = ForwardAction(sourceTopic, target),
        };

    private static Map BuildComponents(
        IReadOnlyList<IEventType> events,
        IReadOnlyList<DynamicForward> dynamicForwards,
        AsyncApiEnrichmentRegistry? enrichment,
        bool includeCloudEventsHeaders)
    {
        var messages = new Map();
        var schemas = new Map { ["NimBusMessageHeaders"] = BuildHeadersSchema() };
        if (includeCloudEventsHeaders)
        {
            schemas["CloudEventsMessageHeaders"] = BuildCloudEventsHeadersSchema();
        }
        var building = new HashSet<Type>();

        foreach (var evt in events)
        {
            var clrType = evt.GetEventClassType();

            // Merge attribute + fluent enrichment once so the message and the payload schema
            // (both keyed by evt.Id == clrType.Name) surface the same resolved values.
            var resolved = AsyncApiEnrichmentResolver.Resolve(evt, clrType, enrichment);

            if (clrType != null)
            {
                EnsureObjectSchema(clrType, schemas, building);

                // The custom name surfaces as the payload schema's JSON-Schema title, and the
                // deprecated marker belongs on the Schema Object (AsyncAPI 3.0 has no Message-level
                // deprecated field). Key stays clrType.Name so the payload $ref never dangles.
                if (schemas.TryGetValue(clrType.Name, out var schemaObj) && schemaObj is Map schema)
                {
                    if (!string.IsNullOrEmpty(resolved.Name)) schema["title"] = resolved.Name;
                    if (resolved.Deprecated) schema["deprecated"] = true;
                }
            }

            messages[evt.Id] = BuildMessage(evt, clrType, resolved);
        }

        foreach (var eventTypeId in dynamicForwards.Select(f => f.EventTypeId).Distinct(StringComparer.Ordinal))
        {
            if (!messages.ContainsKey(eventTypeId))
            {
                messages[eventTypeId] = BuildDynamicMessage(eventTypeId);
            }
        }

        return new Map
        {
            ["messages"] = messages,
            ["schemas"] = schemas,
        };
    }

    private static Map BuildMessage(IEventType evt, Type? clrType, ResolvedAsyncApiEnrichment resolved)
    {
        var serviceBus = new Map
        {
            ["contentType"] = "application/json",
            ["requiresSession"] = true,
            ["messageIdConvention"] = "{EventTypeId}-{deterministicHash(payload)}",
            ["correlationIdConvention"] = "new GUID per publish unless supplied by the caller",
            ["maxDeliveryCount"] = Constants.ServiceBusMaxDeliveryCount,
            ["deadLetterOnFilterEvaluationExceptions"] = true,
        };

        var sessionKey = clrType?.GetCustomAttribute<SessionKeyAttribute>()?.PropertyName;
        if (!string.IsNullOrEmpty(sessionKey))
        {
            serviceBus["sessionKeyProperty"] = sessionKey;
        }

        var message = new Map
        {
            // Custom name -> message.name (falls back to the event id, which is also the component key).
            ["name"] = string.IsNullOrEmpty(resolved.Name) ? evt.Id : resolved.Name,
            ["title"] = resolved.Title,
            ["summary"] = resolved.Summary,
            ["contentType"] = "application/json",
            ["headers"] = new Map { ["$ref"] = "#/components/schemas/NimBusMessageHeaders" },
            ["payload"] = new Map { ["$ref"] = $"#/components/schemas/{evt.Id}" },
            ["bindings"] = new Map { ["amqp1"] = new Map() },
            ["x-servicebus"] = serviceBus,
        };

        if (!string.IsNullOrEmpty(resolved.Description))
        {
            message["description"] = resolved.Description;
        }

        if (resolved.Tags.Count > 0)
        {
            message["tags"] = resolved.Tags.Select(tag => (object)new Map { ["name"] = tag }).ToList();
        }

        if (!string.IsNullOrEmpty(resolved.ExternalDocsUrl))
        {
            var externalDocs = new Map { ["url"] = resolved.ExternalDocsUrl! };
            if (!string.IsNullOrEmpty(resolved.ExternalDocsDescription))
            {
                externalDocs["description"] = resolved.ExternalDocsDescription!;
            }

            message["externalDocs"] = externalDocs;
        }

        var governance = BuildGovernance(resolved);
        if (governance.Count > 0)
        {
            message["x-nimbus-governance"] = governance;
        }

        var examples = BuildExamples(evt, resolved);
        if (examples.Count > 0)
        {
            message["examples"] = examples;
        }

        return message;
    }

    // owner/team/businessCapability/version have no standard AsyncAPI slot -> x-* extension.
    // deprecated is mirrored here for discoverability; the authoritative marker lives on the schema.
    private static Map BuildGovernance(ResolvedAsyncApiEnrichment resolved)
    {
        var governance = new Map();
        if (!string.IsNullOrEmpty(resolved.Owner)) governance["owner"] = resolved.Owner!;
        if (!string.IsNullOrEmpty(resolved.Team)) governance["team"] = resolved.Team!;
        if (!string.IsNullOrEmpty(resolved.BusinessCapability)) governance["businessCapability"] = resolved.BusinessCapability!;
        if (!string.IsNullOrEmpty(resolved.Version)) governance["version"] = resolved.Version!;
        if (resolved.Deprecated) governance["deprecated"] = true;
        return governance;
    }

    private static List<object> BuildExamples(IEventType evt, ResolvedAsyncApiEnrichment resolved)
    {
        var examples = new List<object>();

        var derived = TryBuildExample(evt);
        if (derived != null)
        {
            examples.Add(new Map { ["name"] = "sample", ["payload"] = derived });
        }

        foreach (var example in resolved.Examples)
        {
            var map = new Map();
            if (!string.IsNullOrEmpty(example.Name)) map["name"] = example.Name!;
            if (!string.IsNullOrEmpty(example.Summary)) map["summary"] = example.Summary!;
            map["payload"] = ToPlainValue(example.Payload);
            examples.Add(map);
        }

        return examples;
    }

    private static Map BuildDynamicMessage(string eventTypeId) => new()
    {
        ["name"] = eventTypeId,
        ["title"] = eventTypeId,
        ["summary"] = "Dynamically-typed event (spec 022) with no compiled .NET contract; payload schema is not known at build time.",
        ["contentType"] = "application/json",
        ["headers"] = new Map { ["$ref"] = "#/components/schemas/NimBusMessageHeaders" },
        ["bindings"] = new Map { ["amqp1"] = new Map() },
        ["x-nimbus-dynamic"] = true,
        ["x-servicebus"] = new Map
        {
            ["contentType"] = "application/json",
            ["requiresSession"] = true,
            ["maxDeliveryCount"] = Constants.ServiceBusMaxDeliveryCount,
            ["deadLetterOnFilterEvaluationExceptions"] = true,
        },
    };

    private static Map BuildHeadersSchema() => new()
    {
        ["type"] = "object",
        ["description"] =
            "Service Bus application properties (user.*) that NimBus stamps and routes on, plus native " +
            "SessionId/MessageId/CorrelationId. Set on publish and rewritten by forwarding rules.",
        ["properties"] = new Map
        {
            ["To"] = HeaderProp("Routing target: the event type id on an original publish, rewritten to the destination endpoint id by the forwarding rule."),
            ["From"] = HeaderProp("Source endpoint id. Null on an original publish; set by the forwarding rule (the 'From IS NULL' guard prevents forward loops)."),
            ["EventTypeId"] = HeaderProp("Unqualified CLR event class name; the routing key for subscription rules."),
            ["MessageType"] = HeaderProp("Message kind, e.g. EventRequest."),
            ["EventId"] = HeaderProp("Assigned by the forwarding rule (newid())."),
            ["RetryCount"] = new Map { ["type"] = "integer", ["description"] = "Delivery retry counter." },
            ["OriginatingFrom"] = HeaderProp("Publishing endpoint name captured at publish time."),
            ["OriginalSessionId"] = HeaderProp("Session id preserved for deferred/parked messages."),
        },
    };

    private static Map HeaderProp(string description) => new() { ["type"] = "string", ["description"] = description };

    // CloudEvents 1.0 AMQP binding context attributes (cloudEvents:* application
    // properties in binary mode / top-level fields in structured mode) carried on a
    // CloudEvents-enabled endpoint's topic, alongside the native NimBusMessageHeaders.
    private static Map BuildCloudEventsHeadersSchema() => new()
    {
        ["type"] = "object",
        ["description"] =
            "CloudEvents 1.0 context attributes carried on this endpoint (binary mode: cloudEvents:* AMQP " +
            "application properties; structured mode: top-level JSON fields). Present only on endpoints that " +
            "enable CloudEvents; native NimBus routing headers (NimBusMessageHeaders) are still stamped.",
        ["properties"] = new Map
        {
            ["id"] = HeaderProp("CloudEvents id (maps from NimBus MessageId)."),
            ["source"] = HeaderProp("CloudEvents source (the producing endpoint/system identity)."),
            ["type"] = HeaderProp("CloudEvents type (the event contract name; the dispatch key)."),
            ["specversion"] = HeaderProp("CloudEvents spec version; always \"1.0\"."),
            ["subject"] = HeaderProp("Optional CloudEvents subject."),
            ["time"] = new Map { ["type"] = "string", ["format"] = "date-time", ["description"] = "Optional CloudEvents time." },
            ["datacontenttype"] = HeaderProp("Content type of the data payload (default application/json)."),
            ["dataschema"] = HeaderProp("Optional CloudEvents dataschema reference."),
            ["correlationid"] = HeaderProp("Extension attribute carrying the NimBus CorrelationId (default mapping)."),
            ["sessionid"] = HeaderProp("Extension attribute carrying the NimBus SessionId (default mapping)."),
        },
    };

    // JSON Schema generation lives in JsonSchemaBuilder (shared with the EventCatalog exporter).

    private static void EnsureObjectSchema(Type type, Map schemas, HashSet<Type> building) =>
        JsonSchemaBuilder.EnsureObjectSchema(type, schemas, building);

    private static Map? TryBuildExample(IEventType evt)
    {
        try
        {
            if (evt.GetEventExample() is not { } example) return null;
            return ToPlainValue(example) as Map;
        }
        catch (Exception)
        {
            // An example is a nice-to-have; never let it break the export.
            return null;
        }
    }

    private static object ToPlainValue(object? value)
    {
        if (value is null) return null!;

        var type = value.GetType();
        if (value is string s) return s;
        if (value is bool || value is decimal || value is double || value is float ||
            value is byte || value is sbyte || value is short || value is ushort ||
            value is int || value is uint || value is long || value is ulong)
        {
            return value;
        }
        if (value is Guid guid) return guid.ToString();
        if (value is DateTime dt) return dt.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        if (value is DateTimeOffset dto) return dto.ToString("o", System.Globalization.CultureInfo.InvariantCulture);
        if (type.IsEnum) return value.ToString()!;

        if (value is JValue jValue)
        {
            return jValue.Value is null ? null! : ToPlainValue(jValue.Value);
        }

        if (value is JObject jObject)
        {
            return ToPlainObject(jObject);
        }

        if (TryConvertDictionary(value, out var dictionary))
        {
            return dictionary;
        }

        if (value is System.Collections.IEnumerable enumerable)
        {
            var items = new List<object>();
            foreach (var item in enumerable) items.Add(ToPlainValue(item));
            return items;
        }

        var map = new Map();
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            map[ToCamelCase(property.Name)] = ToPlainValue(property.GetValue(value));
        }
        return map;
    }

    private static Map ToPlainObject(JObject value)
    {
        var map = new Map();
        foreach (var property in value.Properties())
        {
            map[property.Name] = ToPlainValue(property.Value);
        }

        return map;
    }

    private static bool TryConvertDictionary(object value, out Map map)
    {
        if (value is System.Collections.IDictionary dictionary)
        {
            map = new Map();
            foreach (System.Collections.DictionaryEntry entry in dictionary)
            {
                var key = Convert.ToString(entry.Key, System.Globalization.CultureInfo.InvariantCulture);
                if (!string.IsNullOrEmpty(key))
                {
                    map[key] = ToPlainValue(entry.Value);
                }
            }

            return true;
        }

        var dictionaryInterface = value.GetType().GetInterfaces()
            .FirstOrDefault(i => i.IsGenericType &&
                (i.GetGenericTypeDefinition() == typeof(IDictionary<,>) ||
                 i.GetGenericTypeDefinition() == typeof(IReadOnlyDictionary<,>)));
        if (dictionaryInterface == null)
        {
            map = new Map();
            return false;
        }

        map = new Map();
        foreach (var entry in (System.Collections.IEnumerable)value)
        {
            if (entry is null) continue;
            var entryType = entry.GetType();
            var key = entryType.GetProperty("Key")?.GetValue(entry);
            var entryValue = entryType.GetProperty("Value")?.GetValue(entry);
            var keyText = Convert.ToString(key, System.Globalization.CultureInfo.InvariantCulture);
            if (!string.IsNullOrEmpty(keyText))
            {
                map[keyText] = ToPlainValue(entryValue);
            }
        }

        return true;
    }

    private static string OpKey(string id)
    {
        var chars = id.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray();
        return new string(chars);
    }

    private static string ToCamelCase(string name) => JsonSchemaBuilder.ToCamelCase(name);
}
