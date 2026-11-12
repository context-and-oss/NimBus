using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.ServiceBus.AsyncApi;
using YamlDotNet.Serialization;
using CoreAsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;
using Map = System.Collections.Generic.Dictionary<string, object>;

namespace NimBus.CommandLine;

/// <summary>
/// Builds an EventCatalog (https://www.eventcatalog.dev/) catalog from an <see cref="IPlatform"/>
/// in EventCatalog's native MDX format — the free path; the official AsyncAPI generator requires a
/// paid Scale license. Domains come from <see cref="ISystem"/>, services from endpoints (with
/// <c>sends</c>/<c>receives</c> routed through the endpoint's topic channel), events/commands from
/// the event contracts (a <see cref="Command"/>-derived contract lands in <c>commands/</c>), each
/// with a standalone <c>schema.json</c>, and every service gets a filtered AsyncAPI 3.0 document
/// attached via the <c>specifications</c> frontmatter so the spec renders in the EventCatalog UI.
/// </summary>
public static class EventCatalogExporter
{
    private const string DefaultVersion = "1.0.0";

    /// <summary>
    /// Builds the full catalog as relative path (forward slashes) → file content. Pure and
    /// deterministic: no I/O, ordinal ordering throughout, so output is diffable in CI.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Build(
        IPlatform platform, AsyncApiEnrichmentRegistry? enrichment = null)
    {
        ArgumentNullException.ThrowIfNull(platform);

        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);

        var endpoints = platform.Endpoints
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

        var events = platform.EventTypes
            .GroupBy(e => e.Id, StringComparer.Ordinal)
            .Select(g => g.First())
            .OrderBy(e => e.Id, StringComparer.Ordinal)
            .ToList();

        var dynamicForwards = platform.DynamicForwards
            .OrderBy(f => f.EventTypeId, StringComparer.Ordinal)
            .ThenBy(f => f.TargetEndpoint, StringComparer.Ordinal)
            .ToList();

        var clrEventIds = new HashSet<string>(events.Select(e => e.Id), StringComparer.Ordinal);
        var dynamicEventIds = dynamicForwards
            .Select(f => f.EventTypeId)
            .Where(id => !clrEventIds.Contains(id))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        // Resolve enrichment once per contract; the message's own version and every service pin
        // use the same value so pointers never dangle.
        var resolvedById = events.ToDictionary(
            e => e.Id,
            e => AsyncApiEnrichmentResolver.Resolve(e, e.GetEventClassType(), enrichment),
            StringComparer.Ordinal);

        string VersionOf(string eventId) =>
            resolvedById.TryGetValue(eventId, out var r) && !string.IsNullOrEmpty(r.Version) ? r.Version! : DefaultVersion;

        WriteDomains(files, endpoints);
        foreach (var endpoint in endpoints)
        {
            WriteService(files, platform, endpoint, dynamicForwards, enrichment, VersionOf);
            WriteChannel(files, endpoint);
        }

        foreach (var evt in events)
        {
            var collection = evt.GetEventClassType() is { } clr && typeof(Command).IsAssignableFrom(clr)
                ? "commands"
                : "events";
            WriteMessage(files, platform, evt, resolvedById[evt.Id], collection, VersionOf(evt.Id));
        }

        foreach (var dynId in dynamicEventIds)
        {
            WriteDynamicMessage(files, dynId, dynamicForwards);
        }

        return files;
    }

    // ---- Domains ----

    private static void WriteDomains(SortedDictionary<string, string> files, IReadOnlyList<IEndpoint> endpoints)
    {
        var byDomain = endpoints
            .GroupBy(e => e.System?.SystemId is { Length: > 0 } id ? id : "platform", StringComparer.Ordinal)
            .OrderBy(g => g.Key, StringComparer.Ordinal);

        foreach (var domain in byDomain)
        {
            var name = string.Equals(domain.Key, "platform", StringComparison.Ordinal) ? "Platform" : domain.Key;
            var frontmatter = new Map
            {
                ["id"] = domain.Key,
                ["name"] = name,
                ["version"] = DefaultVersion,
                ["summary"] = $"Domain for the {name} system.",
                ["services"] = domain
                    .OrderBy(e => e.Id, StringComparer.Ordinal)
                    .Select(e => (object)new Map { ["id"] = e.Id })
                    .ToList(),
            };

            var body = $"## Overview\n\nEndpoints in the {name} domain.\n\n<NodeGraph />\n";
            files[$"domains/{domain.Key}/index.mdx"] = Mdx(frontmatter, body);
        }
    }

    // ---- Services ----

    private static void WriteService(
        SortedDictionary<string, string> files,
        IPlatform platform,
        IEndpoint endpoint,
        IReadOnlyList<DynamicForward> dynamicForwards,
        AsyncApiEnrichmentRegistry? enrichment,
        Func<string, string> versionOf)
    {
        var channelId = ChannelId(endpoint.Id);

        var sendIds = endpoint.EventTypesProduced.Select(e => e.Id)
            .Concat(dynamicForwards.Where(f => string.Equals(f.SourceEndpoint, endpoint.Id, StringComparison.Ordinal)).Select(f => f.EventTypeId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var receiveIds = endpoint.EventTypesConsumed.Select(e => e.Id)
            .Concat(dynamicForwards.Where(f => string.Equals(f.TargetEndpoint, endpoint.Id, StringComparison.Ordinal)).Select(f => f.EventTypeId))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(id => id, StringComparer.Ordinal)
            .ToList();

        var frontmatter = new Map
        {
            ["id"] = endpoint.Id,
            ["name"] = endpoint.Name,
            ["version"] = DefaultVersion,
            ["summary"] = endpoint.Description is { Length: > 0 } d ? d : $"{endpoint.Name} endpoint.",
        };

        if (sendIds.Count > 0)
        {
            // Producers publish onto their own topic.
            frontmatter["sends"] = sendIds.Select(id => (object)new Map
            {
                ["id"] = id,
                ["version"] = versionOf(id),
                ["to"] = new List<object> { new Map { ["id"] = channelId } },
            }).ToList();
        }

        if (receiveIds.Count > 0)
        {
            // Delivery happens from the consumer's OWN topic (post auto-forward), so the
            // routing edge points at this endpoint's channel, not the producer's.
            frontmatter["receives"] = receiveIds.Select(id => (object)new Map
            {
                ["id"] = id,
                ["version"] = versionOf(id),
                ["from"] = new List<object> { new Map { ["id"] = channelId } },
            }).ToList();
        }

        frontmatter["specifications"] = new List<object>
        {
            new Map { ["type"] = "asyncapi", ["path"] = "asyncapi.yaml", ["name"] = $"{endpoint.Name} AsyncAPI" },
        };

        var body = $"## Overview\n\n{(endpoint.Description is { Length: > 0 } desc ? desc : $"The {endpoint.Name} endpoint.")}\n\n<NodeGraph />\n";
        files[$"services/{endpoint.Id}/index.mdx"] = Mdx(frontmatter, body);

        // The endpoint's own AsyncAPI 3.0 view, rendered by EventCatalog's spec tab. Filtered to
        // this endpoint; cross-endpoint forward-subscription detail stays in `nb asyncapi export`.
        files[$"services/{endpoint.Id}/asyncapi.yaml"] = ServiceBus.AsyncApi.AsyncApiExporter.Serialize(
            new SingleEndpointPlatformView(platform, endpoint), CoreAsyncApiFormat.Yaml, enrichment);
    }

    // ---- Channels ----

    private static void WriteChannel(SortedDictionary<string, string> files, IEndpoint endpoint)
    {
        var frontmatter = new Map
        {
            ["id"] = ChannelId(endpoint.Id),
            ["name"] = $"{endpoint.Name} topic",
            ["version"] = DefaultVersion,
            ["summary"] = $"Azure Service Bus topic for {endpoint.Name}.",
            ["address"] = endpoint.Id,
            ["protocols"] = new List<object> { "amqp" },
            ["deliveryGuarantee"] = "at-least-once",
        };

        var body =
            $"## Overview\n\nAzure Service Bus topic `{endpoint.Id}`. Carries events produced by " +
            $"{endpoint.Name} and events auto-forwarded in for its session-ordered delivery subscription.\n";
        files[$"channels/{ChannelId(endpoint.Id)}/index.mdx"] = Mdx(frontmatter, body);
    }

    // ---- Messages (events/ and commands/) ----

    private static void WriteMessage(
        SortedDictionary<string, string> files,
        IPlatform platform,
        IEventType evt,
        ResolvedAsyncApiEnrichment resolved,
        string collection,
        string version)
    {
        var clrType = evt.GetEventClassType();

        var frontmatter = new Map
        {
            ["id"] = evt.Id,
            ["name"] = resolved.Title,
            ["version"] = version,
            ["summary"] = resolved.Summary,
        };

        if (clrType != null)
        {
            frontmatter["schemaPath"] = "schema.json";
        }

        if (resolved.Deprecated)
        {
            frontmatter["deprecated"] = true;
        }

        var badges = BuildBadges(resolved, isDynamic: false);
        if (badges.Count > 0)
        {
            frontmatter["badges"] = badges;
        }

        var body = BuildMessageBody(platform, evt, resolved, clrType);
        files[$"{collection}/{evt.Id}/index.mdx"] = Mdx(frontmatter, body);

        if (clrType != null)
        {
            files[$"{collection}/{evt.Id}/schema.json"] = JsonSchemaBuilder.BuildStandaloneJson(clrType);
        }
    }

    private static void WriteDynamicMessage(
        SortedDictionary<string, string> files, string eventTypeId, IReadOnlyList<DynamicForward> forwards)
    {
        var frontmatter = new Map
        {
            ["id"] = eventTypeId,
            ["name"] = eventTypeId,
            ["version"] = DefaultVersion,
            ["summary"] = "Dynamically-typed event (spec 022) with no compiled .NET contract; the payload schema is defined at runtime in the schema registry.",
            ["badges"] = new List<object>
            {
                new Map { ["content"] = "Dynamic event", ["backgroundColor"] = "purple", ["textColor"] = "purple" },
            },
        };

        var routes = forwards
            .Where(f => string.Equals(f.EventTypeId, eventTypeId, StringComparison.Ordinal))
            .Select(f => $"- `{f.SourceEndpoint}` → `{f.TargetEndpoint}`");
        var body = $"## Overview\n\nRouted by dynamic forwards:\n\n{string.Join("\n", routes)}\n\n<NodeGraph />\n";
        files[$"events/{eventTypeId}/index.mdx"] = Mdx(frontmatter, body);
    }

    private static List<object> BuildBadges(ResolvedAsyncApiEnrichment resolved, bool isDynamic)
    {
        var badges = new List<object>();
        // Owner/Team surface as badges rather than `owners:` — the exporter generates no
        // users/teams resources, and owner pointers at nonexistent resources would dangle.
        if (!string.IsNullOrEmpty(resolved.Owner))
        {
            badges.Add(new Map { ["content"] = $"Owner: {resolved.Owner}", ["backgroundColor"] = "blue", ["textColor"] = "blue" });
        }

        if (!string.IsNullOrEmpty(resolved.Team))
        {
            badges.Add(new Map { ["content"] = $"Team: {resolved.Team}", ["backgroundColor"] = "green", ["textColor"] = "green" });
        }

        foreach (var tag in resolved.Tags)
        {
            badges.Add(new Map { ["content"] = tag, ["backgroundColor"] = "gray", ["textColor"] = "gray" });
        }

        if (isDynamic)
        {
            badges.Add(new Map { ["content"] = "Dynamic event", ["backgroundColor"] = "purple", ["textColor"] = "purple" });
        }

        return badges;
    }

    private static string BuildMessageBody(
        IPlatform platform, IEventType evt, ResolvedAsyncApiEnrichment resolved, Type? clrType)
    {
        var lines = new List<string> { "## Overview", string.Empty };
        lines.Add(resolved.Description is { Length: > 0 } d ? d : resolved.Summary);
        lines.Add(string.Empty);

        var sessionKey = clrType?.GetCustomAttribute<SessionKeyAttribute>()?.PropertyName;
        if (!string.IsNullOrEmpty(sessionKey))
        {
            lines.Add($"Processed in session order by session key `{sessionKey}`.");
            lines.Add(string.Empty);
        }

        lines.Add("<NodeGraph />");
        lines.Add(string.Empty);

        if (clrType != null)
        {
            lines.Add("## Schema");
            lines.Add(string.Empty);
            lines.Add("<SchemaViewer file=\"schema.json\" />");
            lines.Add(string.Empty);
        }

        return string.Join("\n", lines);
    }

    private static string ChannelId(string endpointId) => $"{endpointId}.topic";

    // Frontmatter is serialized through YamlDotNet (same quoting settings as the AsyncAPI
    // exporter) so descriptions containing `:`/`#`/quotes can never break the document.
    private static string Mdx(Map frontmatter, string body)
    {
        var yaml = new SerializerBuilder().WithQuotingNecessaryStrings().Build().Serialize(frontmatter)
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        return $"---\n{yaml}---\n\n{body}";
    }
}
