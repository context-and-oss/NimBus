#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.ServiceBus.AsyncApi;
using YamlDotNet.Serialization;
using AsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;

namespace NimBus.ServiceBus.Tests;

// Covers the exporter after its extraction from NimBus.CommandLine into
// NimBus.ServiceBus.AsyncApi (so the WebApp can reuse it). These assertions
// verify the canonical implementation still produces the real topic-per-endpoint
// topology and valid YAML/JSON, and that the file-export overload round-trips.
[TestClass]
public sealed class AsyncApiExporterTests
{
    private static JsonNode Json(IPlatform platform) =>
        JsonNode.Parse(AsyncApiExporter.Serialize(platform, AsyncApiFormat.Json))!;

    [TestMethod]
    public void Serialize_ProducesAsyncApi30HeaderAndTopicPerEndpointTopology()
    {
        var platform = new FakePlatform(
            new FakeEndpoint("Alpha", produces: new[] { typeof(OrderPlaced) }),
            new FakeEndpoint("Beta", consumes: new[] { typeof(OrderPlaced) }));

        var root = Json(platform);

        Assert.AreEqual("3.0.0", root["asyncapi"]!.GetValue<string>());
        Assert.AreEqual("topic-per-endpoint",
            root["servers"]!["production"]!["x-nimbus-topology"]!["pattern"]!.GetValue<string>());

        // Each participating endpoint gets its own topic (channel).
        Assert.IsNotNull(root["channels"]!["Alpha"]);
        Assert.IsNotNull(root["channels"]!["Beta"]);

        // Send op for the producer, receive op for the consumer with the auto-forward path.
        Assert.AreEqual("send", root["operations"]!["Alpha_send_OrderPlaced"]!["action"]!.GetValue<string>());
        var recv = root["operations"]!["Beta_receive_OrderPlaced"]!;
        Assert.AreEqual("receive", recv["action"]!.GetValue<string>());
        var fwd = recv["x-servicebus-delivery"]!["forwardSubscriptions"]!.AsArray();
        Assert.AreEqual(1, fwd.Count);
        Assert.AreEqual("Alpha", fwd[0]!["topic"]!.GetValue<string>());
        Assert.AreEqual("Beta", fwd[0]!["forwardTo"]!.GetValue<string>());
        Assert.AreEqual("user.EventTypeId = 'OrderPlaced' AND user.MessageType = 'EventRequest' AND user.From IS NULL", fwd[0]!["filter"]!.GetValue<string>());
    }

    [TestMethod]
    public void Serialize_Yaml_And_Json_BothParse()
    {
        var platform = new FakePlatform(new FakeEndpoint("Alpha", produces: new[] { typeof(OrderPlaced) }));

        var yaml = AsyncApiExporter.Serialize(platform, AsyncApiFormat.Yaml);
        var parsedYaml = new DeserializerBuilder().Build().Deserialize<object>(yaml);
        Assert.IsNotNull(parsedYaml);

        var json = AsyncApiExporter.Serialize(platform, AsyncApiFormat.Json);
        Assert.IsNotNull(JsonNode.Parse(json));
    }

    [TestMethod]
    public async Task ExportAsync_WithPlatform_WritesParseableFile()
    {
        var platform = new FakePlatform(new FakeEndpoint("Alpha", produces: new[] { typeof(OrderPlaced) }));
        var path = Path.Combine(Path.GetTempPath(), $"nimbus-asyncapi-{Guid.NewGuid():N}.yaml");
        try
        {
            await AsyncApiExporter.ExportAsync(platform, path, AsyncApiFormat.Yaml);
            var yaml = await File.ReadAllTextAsync(path);
            var parsed = new DeserializerBuilder().Build().Deserialize<object>(yaml);
            Assert.IsNotNull(parsed);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [TestMethod]
    public void Serialize_Null_Throws()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => AsyncApiExporter.Serialize(null!, AsyncApiFormat.Yaml));
    }

    // ---------------- Test doubles ----------------

    private sealed class FakePlatform : Platform
    {
        public FakePlatform(params FakeEndpoint[] endpoints)
        {
            foreach (var endpoint in endpoints) AddEndpoint(endpoint);
        }
    }

    private sealed class FakeSystem : ISystem
    {
        public FakeSystem(string id) => SystemId = id;

        public string SystemId { get; }
    }

    private sealed class FakeEndpoint : IEndpoint
    {
        public FakeEndpoint(string id, Type[] produces = null!, Type[] consumes = null!)
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

    private sealed class OrderPlaced : Event
    {
        [Required]
        public Guid OrderId { get; set; }
    }
}
