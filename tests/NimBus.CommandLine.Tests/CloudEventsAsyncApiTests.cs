using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.Linq;
using System.Text.Json.Nodes;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using Xunit;
using CoreAsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;
using ServiceBusAsyncApiExporter = NimBus.ServiceBus.AsyncApi.AsyncApiExporter;

namespace NimBus.CommandLine.Tests;

/// <summary>
/// AC16: the AsyncAPI export reflects CloudEvents-enabled endpoints (content mode +
/// CloudEvents attribute headers) while leaving native-endpoint output unchanged.
/// </summary>
public sealed class CloudEventsAsyncApiTests
{
    private static JsonNode Json(IPlatform platform) =>
        JsonNode.Parse(ServiceBusAsyncApiExporter.Serialize(platform, CoreAsyncApiFormat.Json))!;

    [Fact]
    public void CloudEventsEnabledEndpoint_EmitsChannelExtensionAndHeadersSchema()
    {
        var platform = new CeFakePlatform(
            new CeFakeEndpoint("BillingEndpoint", produces: new[] { typeof(InvoiceCreated) }, contentMode: "binary", source: "urn:customer:billing"));

        var root = Json(platform);

        var ce = root["channels"]!["BillingEndpoint"]!["x-cloudevents"]!;
        Assert.Equal("1.0", ce["specversion"]!.GetValue<string>());
        Assert.Equal("binary", ce["contentMode"]!.GetValue<string>());
        Assert.Equal("urn:customer:billing", ce["source"]!.GetValue<string>());
        Assert.Equal(
            "#/components/schemas/CloudEventsMessageHeaders",
            ce["headers"]!["$ref"]!.GetValue<string>());

        // The shared CloudEvents headers schema is present with the core attributes.
        var schema = root["components"]!["schemas"]!["CloudEventsMessageHeaders"]!;
        Assert.NotNull(schema["properties"]!["id"]);
        Assert.NotNull(schema["properties"]!["source"]);
        Assert.NotNull(schema["properties"]!["type"]);
        Assert.NotNull(schema["properties"]!["specversion"]);
    }

    [Fact]
    public void NativePlatform_HasNoCloudEventsExtensionsOrSchema()
    {
        var root = Json(new NimBus.PlatformConfiguration());

        Assert.Null(root["components"]!["schemas"]!["CloudEventsMessageHeaders"]);
        foreach (var channel in root["channels"]!.AsObject())
        {
            Assert.Null(channel.Value!["x-cloudevents"]);
        }
    }

    // ---------------- Test doubles ----------------

    private sealed class CeFakePlatform : Platform
    {
        public CeFakePlatform(params IEndpoint[] endpoints)
        {
            foreach (var endpoint in endpoints) AddEndpoint(endpoint);
        }

        public override IReadOnlyList<DynamicForward> DynamicForwards => Array.Empty<DynamicForward>();
    }

    private sealed class CeFakeSystem : ISystem
    {
        public CeFakeSystem(string id) => SystemId = id;
        public string SystemId { get; }
    }

    private sealed class CeFakeEndpoint : IEndpoint, ICloudEventsAware
    {
        public CeFakeEndpoint(string id, Type[]? produces = null, Type[]? consumes = null, string contentMode = "binary", string? source = null)
        {
            Id = id;
            Name = id;
            EventTypesProduced = (produces ?? Array.Empty<Type>()).Select(t => (IEventType)new EventType(t)).ToList();
            EventTypesConsumed = (consumes ?? Array.Empty<Type>()).Select(t => (IEventType)new EventType(t)).ToList();
            CloudEventsContentMode = contentMode;
            CloudEventsSource = source!;
        }

        public string Id { get; }
        public string Name { get; }
        public string Description => $"{Id} description";
        public string Namespace => "Tests";
        public string SecurityGroupName => string.Empty;
        public ISystem System => new CeFakeSystem(Id);
        public IEnumerable<IEventType> EventTypesProduced { get; }
        public IEnumerable<IEventType> EventTypesConsumed { get; }
        public IEnumerable<IRoleAssignment> RoleAssignments => Array.Empty<IRoleAssignment>();

        public string CloudEventsContentMode { get; }
        public string CloudEventsSource { get; }
    }

    private sealed class InvoiceCreated : Event
    {
        [Required]
        public Guid InvoiceId { get; set; }
    }
}
