using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Nodes;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using Xunit;
using CoreAsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;
using ServiceBusAsyncApiExporter = NimBus.ServiceBus.AsyncApi.AsyncApiExporter;

namespace NimBus.CommandLine.Tests;

// Covers issue #69: the AsyncAPI export must faithfully represent NimBus's real
// Service Bus topology (topic-per-endpoint, auto-forwarding, subscription rules,
// sessions) rather than a naive channel-per-event model, and generate correct
// JSON Schema from the .NET event contracts.
public sealed class AsyncApiExporterTests
{
    private static JsonNode Json(IPlatform platform) =>
        JsonNode.Parse(ServiceBusAsyncApiExporter.Serialize(platform, CoreAsyncApiFormat.Json))!;

    // ---- Built-in platform (Storefront produces OrderPlaced; Billing + Warehouse consume) ----

    [Fact]
    public void Document_HasAsyncApi30HeaderAndAmqpServerWithTopologyExtension()
    {
        var root = Json(new NimBus.PlatformConfiguration());

        Assert.Equal("3.0.0", root["asyncapi"]!.GetValue<string>());
        var server = root["servers"]!["production"]!;
        Assert.Equal("amqp", server["protocol"]!.GetValue<string>());
        Assert.NotNull(server["bindings"]!["amqp1"]);
        // Topology facts that the issue's simplified example omits.
        Assert.NotNull(server["x-nimbus-topology"]);
        Assert.Equal("topic-per-endpoint", server["x-nimbus-topology"]!["pattern"]!.GetValue<string>());
    }

    [Fact]
    public void Channel_IsCreatedPerEndpointTopic_ForProducersAndConsumers()
    {
        var root = Json(new NimBus.PlatformConfiguration());
        var channels = root["channels"]!;

        // Every participating endpoint has its own topic (channel), not one channel per event.
        Assert.NotNull(channels["StorefrontEndpoint"]);
        Assert.NotNull(channels["BillingEndpoint"]);
        Assert.NotNull(channels["WarehouseEndpoint"]);

        Assert.Equal("StorefrontEndpoint", channels["StorefrontEndpoint"]!["address"]!.GetValue<string>());
        Assert.Equal("topic", channels["StorefrontEndpoint"]!["x-servicebus"]!["resourceType"]!.GetValue<string>());
        // The produced message appears on the producer's topic.
        Assert.Equal(
            "#/components/messages/OrderPlaced",
            channels["StorefrontEndpoint"]!["messages"]!["OrderPlaced"]!["$ref"]!.GetValue<string>());
        // The consumed message also appears on the consumer's own topic (post auto-forward).
        Assert.Equal(
            "#/components/messages/OrderPlaced",
            channels["BillingEndpoint"]!["messages"]!["OrderPlaced"]!["$ref"]!.GetValue<string>());
    }

    [Fact]
    public void Operations_HaveSendPerProducerAndReceivePerConsumer()
    {
        var ops = Json(new NimBus.PlatformConfiguration())["operations"]!;

        Assert.Equal("send", ops["StorefrontEndpoint_send_OrderPlaced"]!["action"]!.GetValue<string>());
        Assert.Equal("receive", ops["BillingEndpoint_receive_OrderPlaced"]!["action"]!.GetValue<string>());
        Assert.Equal("receive", ops["WarehouseEndpoint_receive_OrderPlaced"]!["action"]!.GetValue<string>());
    }

    [Fact]
    public void ReceiveOperation_DocumentsAutoForwardPathAndSessionDelivery()
    {
        var op = Json(new NimBus.PlatformConfiguration())["operations"]!["BillingEndpoint_receive_OrderPlaced"]!;
        var delivery = op["x-servicebus-delivery"]!;

        // The message is delivered from the consumer's OWN session subscription.
        var deliverySub = delivery["deliverySubscription"]!;
        Assert.Equal("BillingEndpoint", deliverySub["topic"]!.GetValue<string>());
        Assert.Equal("BillingEndpoint", deliverySub["subscription"]!.GetValue<string>());
        Assert.True(deliverySub["requiresSession"]!.GetValue<bool>());
        Assert.Equal("user.To = 'BillingEndpoint'", deliverySub["filter"]!.GetValue<string>());

        // It got there via an auto-forward subscription on the producer's topic.
        var fwd = delivery["forwardSubscriptions"]!.AsArray();
        Assert.Single(fwd);
        Assert.Equal("StorefrontEndpoint", fwd[0]!["topic"]!.GetValue<string>());
        Assert.Equal("BillingEndpoint", fwd[0]!["subscription"]!.GetValue<string>());
        // The forward subscription lives on the producer topic but forwards INTO the consumer's topic.
        Assert.Equal("BillingEndpoint", fwd[0]!["forwardTo"]!.GetValue<string>());
        Assert.Equal("user.EventTypeId = 'OrderPlaced' AND user.From IS NULL", fwd[0]!["filter"]!.GetValue<string>());
        Assert.Equal(
            "SET user.From = 'StorefrontEndpoint'; SET user.EventId = newid(); SET user.To = 'BillingEndpoint';",
            fwd[0]!["action"]!.GetValue<string>());
    }

    [Fact]
    public void Message_ReferencesHeadersSchemaAndCarriesServiceBusBindings()
    {
        var root = Json(new NimBus.PlatformConfiguration());
        var msg = root["components"]!["messages"]!["OrderPlaced"]!;

        Assert.Equal("application/json", msg["contentType"]!.GetValue<string>());
        Assert.Equal("#/components/schemas/OrderPlaced", msg["payload"]!["$ref"]!.GetValue<string>());
        Assert.Equal("#/components/schemas/NimBusMessageHeaders", msg["headers"]!["$ref"]!.GetValue<string>());
        Assert.True(msg["x-servicebus"]!["requiresSession"]!.GetValue<bool>());
        Assert.Equal("OrderId", msg["x-servicebus"]!["sessionKeyProperty"]!.GetValue<string>());

        // Headers schema documents the user.* application-property routing conventions.
        var headers = root["components"]!["schemas"]!["NimBusMessageHeaders"]!["properties"]!;
        Assert.NotNull(headers["To"]);
        Assert.NotNull(headers["From"]);
        Assert.NotNull(headers["EventTypeId"]);
    }

    [Fact]
    public void Schema_MapsClrTypesFormatsRequiredAndRange()
    {
        var schema = Json(new NimBus.PlatformConfiguration())["components"]!["schemas"]!["OrderPlaced"]!;
        var props = schema["properties"]!;

        Assert.Equal("object", schema["type"]!.GetValue<string>());
        Assert.Equal("string", props["orderId"]!["type"]!.GetValue<string>());
        Assert.Equal("uuid", props["orderId"]!["format"]!.GetValue<string>());
        Assert.Equal("number", props["totalAmount"]!["type"]!.GetValue<string>());
        Assert.Equal("boolean", props["simulateFailure"]!["type"]!.GetValue<string>());

        var required = schema["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("orderId", required);
        Assert.Contains("customerId", required);
        Assert.Contains("currencyCode", required);
        // TotalAmount has [Range] but not [Required] and is a value type -> still required (non-nullable).
        Assert.Contains("totalAmount", required);
    }

    // ---- Multi-producer: same EventTypeId produced by two endpoints (G2, collision hazard) ----

    [Fact]
    public void MultipleProducers_OfSameEvent_AllGetSendOperations()
    {
        var platform = new FakePlatform(
            new FakeEndpoint("Alpha", produces: new[] { typeof(SharedEvent) }),
            new FakeEndpoint("Beta", produces: new[] { typeof(SharedEvent) }),
            new FakeEndpoint("Gamma", consumes: new[] { typeof(SharedEvent) }));

        var root = Json(platform);
        var ops = root["operations"]!;

        Assert.Equal("send", ops["Alpha_send_SharedEvent"]!["action"]!.GetValue<string>());
        Assert.Equal("send", ops["Beta_send_SharedEvent"]!["action"]!.GetValue<string>());

        // One receive op for the consumer, listing BOTH producer topics as forward sources.
        var fwd = ops["Gamma_receive_SharedEvent"]!["x-servicebus-delivery"]!["forwardSubscriptions"]!.AsArray();
        var topics = fwd.Select(f => f!["topic"]!.GetValue<string>()).OrderBy(x => x).ToList();
        Assert.Equal(new[] { "Alpha", "Beta" }, topics);
    }

    // ---- Consumed event with no in-config producer must not be dropped (G3) ----

    [Fact]
    public void ConsumedEventWithoutProducer_StillEmitsReceiveOperationAndMessage()
    {
        var platform = new FakePlatform(
            new FakeEndpoint("Lonely", consumes: new[] { typeof(OrphanEvent) }));

        var root = Json(platform);

        Assert.NotNull(root["components"]!["messages"]!["OrphanEvent"]);
        var op = root["operations"]!["Lonely_receive_OrphanEvent"]!;
        Assert.Equal("receive", op["action"]!.GetValue<string>());
        // No producer -> no forward sources, but the operation is documented, not silently dropped.
        Assert.Empty(op["x-servicebus-delivery"]!["forwardSubscriptions"]!.AsArray());
    }

    // ---- Dynamic forwards (spec 022) must appear (G3) ----

    [Fact]
    public void DynamicForward_ProducesChannelsMessageAndForwardSubscription()
    {
        const string dynId = "crm.contact.enriched.v1";
        var platform = new FakePlatform(
            new[] { new DynamicForward("AgentZone", dynId, "DataPlatform") },
            new FakeEndpoint("AgentZone"),
            new FakeEndpoint("DataPlatform"));

        var root = Json(platform);

        Assert.NotNull(root["channels"]!["AgentZone"]);
        Assert.NotNull(root["channels"]!["DataPlatform"]);

        var msg = root["components"]!["messages"]![dynId]!;
        Assert.True(msg["x-nimbus-dynamic"]!.GetValue<bool>());

        Assert.Equal("send", root["operations"]!["AgentZone_send_crm_contact_enriched_v1"]!["action"]!.GetValue<string>());
        var recv = root["operations"]!["DataPlatform_receive_crm_contact_enriched_v1"]!;
        var fwd = recv["x-servicebus-delivery"]!["forwardSubscriptions"]!.AsArray();
        Assert.Single(fwd);
        Assert.Equal("AgentDyn-DataPlatform", fwd[0]!["subscription"]!.GetValue<string>());
        Assert.Equal("AgentZone", fwd[0]!["topic"]!.GetValue<string>());
        Assert.Equal($"user.EventTypeId = '{dynId}' AND user.From IS NULL", fwd[0]!["filter"]!.GetValue<string>());
    }

    // ---- Rich schema generation: enum / collection / nested / nullable (G4) ----

    [Fact]
    public void Schema_HandlesEnumCollectionNestedAndNullable()
    {
        var platform = new FakePlatform(new FakeEndpoint("Rich", produces: new[] { typeof(RichEvent) }));
        var schemas = Json(platform)["components"]!["schemas"]!;
        var props = schemas["RichEvent"]!["properties"]!;

        // Enum -> string with enum values.
        Assert.Equal("string", props["status"]!["type"]!.GetValue<string>());
        var enumVals = props["status"]!["enum"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        Assert.Contains("Active", enumVals);

        // Collection -> array with item type.
        Assert.Equal("array", props["tags"]!["type"]!.GetValue<string>());
        Assert.Equal("string", props["tags"]!["items"]!["type"]!.GetValue<string>());

        // Nested complex type -> $ref, and the nested schema is registered.
        Assert.Equal("#/components/schemas/RichAddress", props["address"]!["$ref"]!.GetValue<string>());
        Assert.NotNull(schemas["RichAddress"]);
        Assert.Equal("string", schemas["RichAddress"]!["properties"]!["city"]!["type"]!.GetValue<string>());

        // Nullable reference type is optional.
        var required = schemas["RichEvent"]!["required"]?.AsArray().Select(n => n!.GetValue<string>()).ToList()
            ?? new List<string>();
        Assert.DoesNotContain("note", required);
    }

    // ---- YAML with special characters must stay valid (G5: scalar escaping) ----

    [Fact]
    public void SpecialCharactersInDescription_ProduceValidYamlAndJson()
    {
        var platform = new FakePlatform(new FakeEndpoint("Weird", produces: new[] { typeof(WeirdEvent) }));

        var yaml = ServiceBusAsyncApiExporter.Serialize(platform, CoreAsyncApiFormat.Yaml);
        var parsedYaml = new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<object>(yaml);
        Assert.NotNull(parsedYaml);

        var json = ServiceBusAsyncApiExporter.Serialize(platform, CoreAsyncApiFormat.Json);
        Assert.NotNull(JsonNode.Parse(json));
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
        public FakeEndpoint(string id, Type[]? produces = null, Type[]? consumes = null)
        {
            Id = id;
            Name = id;
            EventTypesProduced = (produces ?? Array.Empty<Type>()).Select(t => (IEventType)new EventType(t)).ToList();
            EventTypesConsumed = (consumes ?? Array.Empty<Type>()).Select(t => (IEventType)new EventType(t)).ToList();
        }

        public string Id { get; }
        public string Name { get; }
        public string Description => $"{Id} description";
        public string Namespace => "Tests";
        public string SecurityGroupName => string.Empty;
        public ISystem System => new FakeSystem(Id);
        public IEnumerable<IEventType> EventTypesProduced { get; }
        public IEnumerable<IEventType> EventTypesConsumed { get; }
        public IEnumerable<IRoleAssignment> RoleAssignments => Array.Empty<IRoleAssignment>();
    }

    // Same EventTypeId produced by two endpoints (collision hazard).
    private sealed class SharedEvent : Event
    {
        [Required]
        public Guid Id { get; set; }
    }

    private sealed class OrphanEvent : Event
    {
        [Required]
        public string Value { get; set; } = null!;
    }

    private enum RichStatus
    {
        Active,
        Retired,
    }

    private sealed class RichAddress
    {
        public string Street { get; set; } = null!;
        public string City { get; set; } = null!;
    }

    private sealed class RichEvent : Event
    {
        public RichStatus Status { get; set; }
        public List<string> Tags { get; set; } = null!;
        public RichAddress Address { get; set; } = null!;
        public string? Note { get; set; }
    }

    private sealed class WeirdEvent : Event
    {
        [Description("Weird: value # hash, with: colons - and dashes")]
        public string Field { get; set; } = null!;
    }
}
