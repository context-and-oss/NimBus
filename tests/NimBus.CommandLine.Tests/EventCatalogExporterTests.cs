#pragma warning disable CA1707, CA2007
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using Xunit;

namespace NimBus.CommandLine.Tests;

// The EventCatalog export writes EventCatalog's native MDX format (the free path — the
// official AsyncAPI generator is Scale-licensed) plus a per-service AsyncAPI 3.0 document
// attached via the `specifications` frontmatter. Build() is pure: relative path -> content.
public sealed class EventCatalogExporterTests
{
    private static IReadOnlyDictionary<string, string> Build(
        IPlatform platform, AsyncApiEnrichmentRegistry? enrichment = null) =>
        EventCatalogExporter.Build(platform, enrichment);

    // Parses the YAML frontmatter block between the --- fences through a real YAML parser,
    // proving well-formedness and giving structured assertions.
    private static Dictionary<object, object> Frontmatter(string mdx)
    {
        Assert.StartsWith("---", mdx, StringComparison.Ordinal);
        var end = mdx.IndexOf("\n---", 3, StringComparison.Ordinal);
        Assert.True(end > 0, "frontmatter closing fence not found");
        var yaml = mdx.Substring(3, end - 3);
        var parsed = new YamlDotNet.Serialization.DeserializerBuilder().Build()
            .Deserialize<Dictionary<object, object>>(yaml);
        Assert.NotNull(parsed);
        return parsed;
    }

    private static List<Dictionary<object, object>> MapList(object node) =>
        ((IEnumerable<object>)node).Cast<Dictionary<object, object>>().ToList();

    // ---- Domains ----

    [Fact]
    public void Build_EmitsDomainPerSystem_WithServicesListed()
    {
        var system = new FakeSystem("Commerce");
        var platform = new FakePlatform(
            new FakeEndpoint("Zeta", system: system, produces: new[] { typeof(PlainEvent) }),
            new FakeEndpoint("Alpha", system: system, consumes: new[] { typeof(PlainEvent) }));

        var files = Build(platform);

        var domain = Frontmatter(files["domains/Commerce/index.mdx"]);
        Assert.Equal("Commerce", domain["id"]);
        var services = MapList(domain["services"]).Select(s => s["id"]).ToList();
        Assert.Equal(new object[] { "Alpha", "Zeta" }, services); // ordinal-sorted
    }

    [Fact]
    public void Build_NullSystem_FallsBackToPlatformDomain()
    {
        var platform = new FakePlatform(
            new FakeEndpoint("Loner", hasSystem: false, produces: new[] { typeof(PlainEvent) }));

        var files = Build(platform);

        var domain = Frontmatter(files["domains/platform/index.mdx"]);
        Assert.Equal("platform", domain["id"]);
        Assert.Equal("Loner", MapList(domain["services"]).Single()["id"]);
    }

    // ---- Services ----

    [Fact]
    public void Build_ServiceSendsReceives_WithChannelRouting()
    {
        var platform = new FakePlatform(
            new FakeEndpoint("Producer", produces: new[] { typeof(PlainEvent) }),
            new FakeEndpoint("Consumer", consumes: new[] { typeof(PlainEvent) }));

        var files = Build(platform);

        var producer = Frontmatter(files["services/Producer/index.mdx"]);
        var send = MapList(producer["sends"]).Single();
        Assert.Equal("PlainEvent", send["id"]);
        Assert.Equal("1.0.0", send["version"]);
        Assert.Equal("Producer.topic", MapList(send["to"]).Single()["id"]);

        var consumer = Frontmatter(files["services/Consumer/index.mdx"]);
        var receive = MapList(consumer["receives"]).Single();
        Assert.Equal("PlainEvent", receive["id"]);
        // Delivery happens from the consumer's OWN topic (post auto-forward).
        Assert.Equal("Consumer.topic", MapList(receive["from"]).Single()["id"]);
    }

    [Fact]
    public void Build_MessageVersion_FromEnrichment_ConsistentWithServicePins()
    {
        var platform = new FakePlatform(
            new FakeEndpoint("Ep", produces: new[] { typeof(VersionedEvent), typeof(PlainEvent) }));

        var files = Build(platform);

        Assert.Equal("2.1.0", Frontmatter(files["events/VersionedEvent/index.mdx"])["version"]);
        Assert.Equal("1.0.0", Frontmatter(files["events/PlainEvent/index.mdx"])["version"]);

        var sends = MapList(Frontmatter(files["services/Ep/index.mdx"])["sends"]);
        Assert.Equal("2.1.0", sends.Single(s => (string)s["id"] == "VersionedEvent")["version"]);
        Assert.Equal("1.0.0", sends.Single(s => (string)s["id"] == "PlainEvent")["version"]);
    }

    [Fact]
    public void Build_FluentEnrichment_OverridesAttribute()
    {
        var registry = new AsyncApiEnrichmentRegistry();
        registry.For(typeof(VersionedEvent)).Version = "3.0.0";

        var platform = new FakePlatform(new FakeEndpoint("Ep", produces: new[] { typeof(VersionedEvent) }));

        var files = Build(platform, registry);

        Assert.Equal("3.0.0", Frontmatter(files["events/VersionedEvent/index.mdx"])["version"]);
    }

    // ---- Commands vs events ----

    [Fact]
    public void Build_CommandGoesToCommandsFolder()
    {
        var platform = new FakePlatform(
            new FakeEndpoint("Sender", produces: new[] { typeof(DoWork) }),
            new FakeEndpoint("Handler", consumes: new[] { typeof(DoWork) }));

        var files = Build(platform);

        Assert.Contains("commands/DoWork/index.mdx", files.Keys);
        Assert.DoesNotContain("events/DoWork/index.mdx", files.Keys);
        Assert.Contains("commands/DoWork/schema.json", files.Keys);
    }

    // ---- Dynamic (spec 022) events ----

    [Fact]
    public void Build_DynamicForward_EventWithDynamicBadge_NoSchema()
    {
        const string dynId = "crm.contact.enriched.v1";
        var platform = new FakePlatform(
            new[] { new DynamicForward("AgentZone", dynId, "DataPlatform") },
            new FakeEndpoint("AgentZone"),
            new FakeEndpoint("DataPlatform"));

        var files = Build(platform);

        var evt = Frontmatter(files[$"events/{dynId}/index.mdx"]);
        var badges = MapList(evt["badges"]).Select(b => (string)b["content"]).ToList();
        Assert.Contains("Dynamic event", badges);
        Assert.False(evt.ContainsKey("schemaPath"));
        Assert.DoesNotContain($"events/{dynId}/schema.json", files.Keys);

        // Source sends, target receives.
        var sends = MapList(Frontmatter(files["services/AgentZone/index.mdx"])["sends"]);
        Assert.Contains(sends, s => (string)s["id"] == dynId);
        var receives = MapList(Frontmatter(files["services/DataPlatform/index.mdx"])["receives"]);
        Assert.Contains(receives, r => (string)r["id"] == dynId);
    }

    // ---- Schemas ----

    [Fact]
    public void Build_SchemaJson_SelfContained_WithDefs()
    {
        var platform = new FakePlatform(new FakeEndpoint("Rich", produces: new[] { typeof(RichEvent) }));

        var files = Build(platform);

        var schema = JsonNode.Parse(files["events/RichEvent/schema.json"])!;
        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.Equal("#/$defs/RichAddress", schema["properties"]!["address"]!["$ref"]!.GetValue<string>());
        Assert.Equal("string", schema["$defs"]!["RichAddress"]!["properties"]!["city"]!["type"]!.GetValue<string>());
        Assert.DoesNotContain("#/components/", files["events/RichEvent/schema.json"], StringComparison.Ordinal);

        var evt = Frontmatter(files["events/RichEvent/index.mdx"]);
        Assert.Equal("schema.json", evt["schemaPath"]);
        Assert.Contains("<SchemaViewer", files["events/RichEvent/index.mdx"], StringComparison.Ordinal);
    }

    // ---- Frontmatter safety ----

    [Fact]
    public void Build_FrontmatterIsValidYaml_EscapesSpecials()
    {
        var platform = new FakePlatform(
            new FakeEndpoint("Weird", description: "Weird: value # hash, with: colons - and \"quotes\"",
                produces: new[] { typeof(PlainEvent) }));

        var files = Build(platform);

        var service = Frontmatter(files["services/Weird/index.mdx"]);
        Assert.Equal("Weird: value # hash, with: colons - and \"quotes\"", service["summary"]);
    }

    // ---- Per-service AsyncAPI attach ----

    [Fact]
    public void Build_ServiceAsyncApi_AttachedAndFiltered()
    {
        var platform = new FakePlatform(
            new FakeEndpoint("One", produces: new[] { typeof(PlainEvent) }),
            new FakeEndpoint("Two", consumes: new[] { typeof(PlainEvent) }),
            new FakeEndpoint("Unrelated", produces: new[] { typeof(RichEvent) }));

        var files = Build(platform);

        var service = Frontmatter(files["services/One/index.mdx"]);
        var spec = MapList(service["specifications"]).Single();
        Assert.Equal("asyncapi", spec["type"]);
        Assert.Equal("asyncapi.yaml", spec["path"]);

        var doc = new YamlDotNet.Serialization.DeserializerBuilder().Build()
            .Deserialize<Dictionary<object, object>>(files["services/One/asyncapi.yaml"]);
        var channels = ((Dictionary<object, object>)doc["channels"]).Keys.Cast<string>().ToList();
        Assert.Contains("One", channels);
        Assert.DoesNotContain("Unrelated", channels);
    }

    // ---- Channels ----

    [Fact]
    public void Build_ChannelPerEndpoint_WithServiceBusFacts()
    {
        var platform = new FakePlatform(
            new FakeEndpoint("Producer", produces: new[] { typeof(PlainEvent) }),
            new FakeEndpoint("Consumer", consumes: new[] { typeof(PlainEvent) }));

        var files = Build(platform);

        var channel = Frontmatter(files["channels/Producer.topic/index.mdx"]);
        Assert.Equal("Producer.topic", channel["id"]);
        Assert.Equal("Producer", channel["address"]);
        Assert.Equal("amqp", ((IEnumerable<object>)channel["protocols"]).Single());
        Assert.Equal("at-least-once", channel["deliveryGuarantee"]);
        Assert.Contains("channels/Consumer.topic/index.mdx", files.Keys);
    }

    // ---- Determinism ----

    [Fact]
    public void Build_Deterministic_InputOrderIndependent()
    {
        var forward = new FakePlatform(
            new FakeEndpoint("Aaa", produces: new[] { typeof(PlainEvent) }),
            new FakeEndpoint("Bbb", consumes: new[] { typeof(PlainEvent) }));
        var reverse = new FakePlatform(
            new FakeEndpoint("Bbb", consumes: new[] { typeof(PlainEvent) }),
            new FakeEndpoint("Aaa", produces: new[] { typeof(PlainEvent) }));

        var a = Build(forward);
        var b = Build(reverse);

        Assert.Equal(a.Keys.OrderBy(k => k, StringComparer.Ordinal), b.Keys.OrderBy(k => k, StringComparer.Ordinal));
        foreach (var key in a.Keys)
        {
            Assert.Equal(a[key], b[key]);
        }
    }

    // ---- Governance surface ----

    [Fact]
    public void Build_DeprecatedAndOwnerSurface()
    {
        var platform = new FakePlatform(new FakeEndpoint("Ep", produces: new[] { typeof(GovernedEvent) }));

        var files = Build(platform);

        var evt = Frontmatter(files["events/GovernedEvent/index.mdx"]);
        // Untyped YAML deserialization surfaces scalars as strings; "true" proves an unquoted bool.
        Assert.Equal("true", evt["deprecated"]);
        var badges = MapList(evt["badges"]).Select(b => (string)b["content"]).ToList();
        Assert.Contains("Owner: order-team", badges);
        Assert.Contains("Team: platform", badges);
    }

    [Fact]
    public void Build_SessionKey_DocumentedInBody()
    {
        var platform = new FakePlatform(new FakeEndpoint("Ep", produces: new[] { typeof(SessionEvent) }));

        var files = Build(platform);

        Assert.Contains("CustomerId", files["events/SessionEvent/index.mdx"], StringComparison.Ordinal);
    }

    // ---------------- Test doubles ----------------

    private sealed class FakePlatform : Platform
    {
        private readonly IReadOnlyList<DynamicForward> _forwards;

        public FakePlatform(params FakeEndpoint[] endpoints)
            : this(Array.Empty<DynamicForward>(), endpoints)
        {
        }

        public FakePlatform(IReadOnlyList<DynamicForward> forwards, params FakeEndpoint[] endpoints)
        {
            _forwards = forwards;
            foreach (var endpoint in endpoints) AddEndpoint(endpoint);
        }

        public override IReadOnlyList<DynamicForward> DynamicForwards => _forwards;
    }

    private sealed class FakeSystem : ISystem
    {
        public FakeSystem(string id) => SystemId = id;

        public string SystemId { get; }
    }

    private sealed class FakeEndpoint : IEndpoint
    {
        public FakeEndpoint(
            string id,
            Type[]? produces = null,
            Type[]? consumes = null,
            ISystem? system = null,
            string? description = null,
            bool hasSystem = true)
        {
            Id = id;
            Name = id;
            Description = description ?? $"{id} description";
            System = (system ?? (hasSystem ? new FakeSystem(id) : null))!;
            EventTypesProduced = (produces ?? Array.Empty<Type>()).Select(t => (IEventType)new EventType(t)).ToList();
            EventTypesConsumed = (consumes ?? Array.Empty<Type>()).Select(t => (IEventType)new EventType(t)).ToList();
        }

        public string Id { get; }
        public string Name { get; }
        public string Description { get; }
        public string Namespace => "Tests";
        public string SecurityGroupName => string.Empty;
        // IEndpoint declares a non-nullable System; the fake models "no system"
        // with a null the exporter must tolerate, so suppress the mismatch here.
        public ISystem System { get; }
        public IEnumerable<IEventType> EventTypesProduced { get; }
        public IEnumerable<IEventType> EventTypesConsumed { get; }
        public IEnumerable<IRoleAssignment> RoleAssignments => Array.Empty<IRoleAssignment>();
    }

    private sealed class PlainEvent : Event
    {
        [Required]
        public string Value { get; set; } = string.Empty;
    }

    [AsyncApiMessage(Version = "2.1.0")]
    private sealed class VersionedEvent : Event
    {
        public string Value { get; set; } = string.Empty;
    }

    private sealed class DoWork : Command
    {
        [Required]
        public string JobId { get; set; } = string.Empty;
    }

    private sealed class RichAddress
    {
        public string Street { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
    }

    private sealed class RichEvent : Event
    {
        public List<string> Tags { get; set; } = new();
        public RichAddress Address { get; set; } = new();
        public string? Note { get; set; }
    }

    [AsyncApiMessage(Owner = "order-team", Team = "platform", Deprecated = true)]
    private sealed class GovernedEvent : Event
    {
        public string Value { get; set; } = string.Empty;
    }

    [SessionKey(nameof(CustomerId))]
    private sealed class SessionEvent : Event
    {
        [Required]
        public string CustomerId { get; set; } = string.Empty;
    }
}
