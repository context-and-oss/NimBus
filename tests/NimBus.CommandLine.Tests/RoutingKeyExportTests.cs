#pragma warning disable CA1707, CA2007
using System.Text.Json.Nodes;
using NimBus.Core;
using NimBus.Core.Events;
using Xunit;

namespace NimBus.CommandLine.Tests;

// Spec 045 §10: `nb topology export` and `nb catalog export` show the routing-key property of
// an event type and the keys (or the unmatched role) each consumer declares.
public sealed class RoutingKeyExportTests
{
    [RoutingKey(nameof(Instance))]
    public sealed class ExportRoutedEvent : Event
    {
        public string Instance { get; set; } = "";
    }

    public sealed class ExportCrm : NimBus.Core.Endpoints.Endpoint
    {
        public ExportCrm() => Produces<ExportRoutedEvent>();
    }

    public sealed class ExportErp01 : NimBus.Core.Endpoints.Endpoint
    {
        public ExportErp01() => Consumes<ExportRoutedEvent>("02", "01");
    }

    public sealed class ExportUnrouted : NimBus.Core.Endpoints.Endpoint
    {
        public ExportUnrouted() => ConsumesUnmatched<ExportRoutedEvent>();
    }

    private sealed class ExportPlatform : Platform
    {
        public ExportPlatform()
        {
            AddEndpoint(new ExportCrm());
            AddEndpoint(new ExportErp01());
            AddEndpoint(new ExportUnrouted());
        }
    }

    private static JsonNode Endpoint(JsonNode root, string id) =>
        root["endpoints"]!.AsArray().Single(endpoint => endpoint!["id"]!.GetValue<string>() == id)!;

    [Fact]
    public void PlatformConfig_ListsRoutingKeysAndTheUnmatchedRole()
    {
        var root = JsonNode.Parse(PlatformConfigExporter.Serialize(PlatformConfigExporter.Build(new ExportPlatform())))!;

        var keyed = Endpoint(root, nameof(ExportErp01))["eventTypesConsumed"]!.AsArray().Single()!;
        Assert.Equal(new[] { "01", "02" }, keyed["routingKeys"]!.AsArray().Select(key => key!.GetValue<string>()));
        Assert.Equal(nameof(ExportRoutedEvent.Instance), keyed["routingKeyProperty"]!.GetValue<string>());
        Assert.Null(keyed["unmatched"]);

        var unmatched = Endpoint(root, nameof(ExportUnrouted))["eventTypesConsumed"]!.AsArray().Single()!;
        Assert.True(unmatched["unmatched"]!.GetValue<bool>());
        Assert.Null(unmatched["routingKeys"]);

        var produced = Endpoint(root, nameof(ExportCrm))["eventTypesProduced"]!.AsArray().Single()!;
        Assert.Equal(nameof(ExportRoutedEvent.Instance), produced["routingKeyProperty"]!.GetValue<string>());
    }

    [Fact]
    public void EventCatalog_DescribesTheKeyAndEachConsumersKeys()
    {
        var files = EventCatalogExporter.Build(new ExportPlatform(), null);

        Assert.Contains(
            "- `ExportRoutedEvent`: routing keys `01`, `02`",
            files[$"services/{nameof(ExportErp01)}/index.mdx"],
            StringComparison.Ordinal);
        Assert.Contains(
            "- `ExportRoutedEvent`: unmatched consumer",
            files[$"services/{nameof(ExportUnrouted)}/index.mdx"],
            StringComparison.Ordinal);
        Assert.DoesNotContain("## Routing keys", files[$"services/{nameof(ExportCrm)}/index.mdx"], StringComparison.Ordinal);

        var eventPage = files.Single(file => file.Key.EndsWith($"{nameof(ExportRoutedEvent)}/index.mdx", StringComparison.Ordinal)).Value;
        Assert.Contains("Routed to consumers by routing key `Instance`.", eventPage, StringComparison.Ordinal);
    }
}
