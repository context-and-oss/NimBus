#pragma warning disable CA1707, CA2007
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.WebApp.Controllers;
using NimBus.WebApp.Mcp.Tools;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Simulation;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Spec 045 §10 in the WebApp: the event-type API, the MCP endpoint list and the simulator know
/// which consumer takes which routing keys.
/// </summary>
[TestClass]
public sealed class RoutingKeyWebAppTests
{
    [RoutingKey(nameof(Instance))]
    public sealed class WebRoutedEvent : Event
    {
        public string Instance { get; set; } = "";
        public string CustomerNo { get; set; } = "C-1";
    }

    public sealed class WebCrm : NimBus.Core.Endpoints.Endpoint
    {
        public WebCrm() => Produces<WebRoutedEvent>();
    }

    public sealed class WebErp01 : NimBus.Core.Endpoints.Endpoint
    {
        public WebErp01() => Consumes<WebRoutedEvent>("01");
    }

    public sealed class WebErp02 : NimBus.Core.Endpoints.Endpoint
    {
        public WebErp02() => Consumes<WebRoutedEvent>("02", "03");
    }

    public sealed class WebUnrouted : NimBus.Core.Endpoints.Endpoint
    {
        public WebUnrouted() => ConsumesUnmatched<WebRoutedEvent>();
    }

    public sealed class WebReporting : NimBus.Core.Endpoints.Endpoint
    {
        public WebReporting() => Consumes<WebRoutedEvent>();
    }

    private sealed class WebPlatform : Platform
    {
        public WebPlatform()
        {
            AddEndpoint(new WebCrm());
            AddEndpoint(new WebErp01());
            AddEndpoint(new WebErp02());
            AddEndpoint(new WebUnrouted());
            AddEndpoint(new WebReporting());
        }
    }

    private static readonly IEventType Routed = new EventType<WebRoutedEvent>();

    [TestMethod]
    public void ConsumerRouting_ListsEveryConsumerInConsumerOrder()
    {
        var platform = new WebPlatform();

        var routing = Mapper.ConsumerRoutingFor(platform, Routed);

        CollectionAssert.AreEqual(
            platform.GetConsumers(Routed).Select(consumer => consumer.Name).ToArray(),
            routing.Select(entry => entry.Endpoint).ToArray());
        CollectionAssert.AreEqual(new[] { "01" }, routing.Single(entry => entry.Endpoint == nameof(WebErp01)).RoutingKeys.ToArray());
        CollectionAssert.AreEqual(new[] { "02", "03" }, routing.Single(entry => entry.Endpoint == nameof(WebErp02)).RoutingKeys.ToArray());
        Assert.IsTrue(routing.Single(entry => entry.Endpoint == nameof(WebUnrouted)).Unmatched);
        var reporting = routing.Single(entry => entry.Endpoint == nameof(WebReporting));
        Assert.IsFalse(reporting.Unmatched);
        Assert.AreEqual(0, reporting.RoutingKeys.Count);
    }

    [TestMethod]
    public void EventTypeMapping_CarriesTheRoutingKeyProperty()
    {
        Assert.AreEqual(nameof(WebRoutedEvent.Instance), Mapper.EventTypeFromIEventType(Routed).RoutingKeyProperty);
        Assert.AreEqual(nameof(WebRoutedEvent.Instance), Mapper.EventTypeFromIEventType(Routed, 1, 4).RoutingKeyProperty);
    }

    [TestMethod]
    public void McpEndpointSummary_ListsKeysAndUnmatchedTypes()
    {
        Assert.AreEqual("02, 03", string.Join(", ", OperatorDiscoveryTools.RoutingKeysOf(new WebErp02())![nameof(WebRoutedEvent)]));
        Assert.IsNull(OperatorDiscoveryTools.UnmatchedOf(new WebErp02()));
        CollectionAssert.AreEqual(new[] { nameof(WebRoutedEvent) }, OperatorDiscoveryTools.UnmatchedOf(new WebUnrouted())!.ToArray());
        Assert.IsNull(OperatorDiscoveryTools.RoutingKeysOf(new WebReporting()));
    }

    [TestMethod]
    public void Simulator_PicksAClaimedRoutingKey()
    {
        var factory = new FakePayloadSimulationEventFactory(new FakeEventPayloadGenerator(), new WebPlatform());

        var keys = Enumerable.Range(0, 30)
            .Select(_ => ((WebRoutedEvent)factory.Create(Routed)).Instance)
            .ToHashSet(StringComparer.Ordinal);

        Assert.IsTrue(keys.IsSubsetOf(["01", "02", "03"]), string.Join(", ", keys));
        Assert.IsTrue(keys.Count > 1, "30 draws should not all pick the same key.");
    }

    [TestMethod]
    public void Simulator_WithoutAPlatform_LeavesTheGeneratedValue()
    {
        var factory = new FakePayloadSimulationEventFactory(new FakeEventPayloadGenerator());

        var @event = (WebRoutedEvent)factory.Create(Routed);

        Assert.IsFalse(new[] { "01", "02", "03" }.Contains(@event.Instance));
    }
}
