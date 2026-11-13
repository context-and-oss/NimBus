#pragma warning disable CA1707, CA2007
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using Xunit;

namespace NimBus.CommandLine.Tests;

// The per-service AsyncAPI document narrows consumers to one endpoint, but the unmatched
// consumer's filter excludes every key claimed on the platform (Spec 045 §3).
public sealed class SingleEndpointPlatformViewTests
{
    [RoutingKey(nameof(Instance))]
    public sealed class ViewKeyedEvent : Event
    {
        public string Instance { get; set; } = "";
    }

    public sealed class ViewInstance01 : NimBus.Core.Endpoints.Endpoint
    {
        public ViewInstance01() => Consumes<ViewKeyedEvent>("01");
    }

    public sealed class ViewInstance02 : NimBus.Core.Endpoints.Endpoint
    {
        public ViewInstance02() => Consumes<ViewKeyedEvent>("02");
    }

    public sealed class ViewUnrouted : NimBus.Core.Endpoints.Endpoint
    {
        public ViewUnrouted() => ConsumesUnmatched<ViewKeyedEvent>();
    }

    private sealed class ViewPlatform : Platform
    {
        public ViewPlatform()
        {
            AddEndpoint(new ViewInstance01());
            AddEndpoint(new ViewInstance02());
            AddEndpoint(new ViewUnrouted());
        }
    }

    [Fact]
    public void GetClaimedRoutingKeys_ForwardsToTheWholePlatform()
    {
        var platform = new ViewPlatform();
        var unrouted = platform.Endpoints.Single(endpoint => endpoint.Id == nameof(ViewUnrouted));
        IPlatform view = new SingleEndpointPlatformView(platform, unrouted);

        var claimed = view.GetClaimedRoutingKeys(new EventType<ViewKeyedEvent>());

        Assert.Equal(new[] { "01", "02" }, claimed);
        Assert.Single(view.GetConsumers(new EventType<ViewKeyedEvent>()));
    }
}
