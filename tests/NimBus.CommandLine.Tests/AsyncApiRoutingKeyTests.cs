#pragma warning disable CA1707, CA2007
using System.Text.Json.Nodes;
using NimBus.Core;
using NimBus.Core.Events;
using NimBus.ServiceBus.Provisioning;
using Xunit;
using CoreAsyncApiFormat = NimBus.Core.Events.AsyncApiFormat;
using ServiceBusAsyncApiExporter = NimBus.ServiceBus.AsyncApi.AsyncApiExporter;

namespace NimBus.CommandLine.Tests;

// Spec 045 §10: the AsyncAPI export documents the routing-key filter each consumer actually
// gets, the routing shape of each receive operation, and the routing-key property of the message.
public sealed class AsyncApiRoutingKeyTests
{
    [RoutingKey(nameof(Instance))]
    public sealed class ApiRoutedEvent : Event
    {
        public string Instance { get; set; } = "";
    }

    public sealed class ApiCrm : NimBus.Core.Endpoints.Endpoint
    {
        public ApiCrm() => Produces<ApiRoutedEvent>();
    }

    public sealed class ApiErp01 : NimBus.Core.Endpoints.Endpoint
    {
        public ApiErp01() => Consumes<ApiRoutedEvent>("01");
    }

    public sealed class ApiUnrouted : NimBus.Core.Endpoints.Endpoint
    {
        public ApiUnrouted() => ConsumesUnmatched<ApiRoutedEvent>();
    }

    private sealed class ApiPlatform : Platform
    {
        public ApiPlatform()
        {
            AddEndpoint(new ApiCrm());
            AddEndpoint(new ApiErp01());
            AddEndpoint(new ApiUnrouted());
        }
    }

    private static JsonNode Operations() =>
        JsonNode.Parse(ServiceBusAsyncApiExporter.Serialize(new ApiPlatform(), CoreAsyncApiFormat.Json))!["operations"]!;

    [Fact]
    public void KeyedConsumer_DocumentsItsFilterAndKeys()
    {
        var operation = Operations()[$"{nameof(ApiErp01)}_receive_{nameof(ApiRoutedEvent)}"]!;
        var forward = operation["x-servicebus-delivery"]!["forwardSubscriptions"]!.AsArray().Single()!;

        Assert.Equal(
            TopologyDescriptor.KeyedForwardFilter(nameof(ApiRoutedEvent), ["01"]),
            forward["filter"]!.GetValue<string>());
        Assert.Equal("01", operation["x-nimbus-routing"]!["routingKeys"]!.AsArray().Single()!.GetValue<string>());
    }

    [Fact]
    public void UnmatchedConsumer_DocumentsTheExclusions()
    {
        var operation = Operations()[$"{nameof(ApiUnrouted)}_receive_{nameof(ApiRoutedEvent)}"]!;
        var forward = operation["x-servicebus-delivery"]!["forwardSubscriptions"]!.AsArray().Single()!;

        Assert.Equal(
            TopologyDescriptor.UnmatchedForwardFilter(nameof(ApiRoutedEvent), ["01"]),
            forward["filter"]!.GetValue<string>());
        Assert.True(operation["x-nimbus-routing"]!["unmatched"]!.GetValue<bool>());
    }

    [Fact]
    public void Message_NamesTheRoutingKeyProperty()
    {
        var root = JsonNode.Parse(ServiceBusAsyncApiExporter.Serialize(new ApiPlatform(), CoreAsyncApiFormat.Json))!;

        Assert.Equal(
            nameof(ApiRoutedEvent.Instance),
            root["components"]!["messages"]![nameof(ApiRoutedEvent)]!["x-nimbus-routing-key"]!.GetValue<string>());
    }
}
