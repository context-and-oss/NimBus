using Azure.Messaging.ServiceBus;
using Microsoft.Extensions.Configuration;
using NimBus.Core.Messages;
using NimBus.SDK;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.WebApp.Services;

/// <summary>
/// Production <see cref="IAgentEventPublisher"/>: publishes a classless agent event
/// onto the Agent Zone topic via the instrumented SDK <see cref="PublisherClient"/>.
/// The zone endpoint id is read from config key <c>Agent:ZoneEndpointId</c>
/// (default <c>"AgentZoneEndpoint"</c>).
/// </summary>
public sealed class AgentEventPublisher : IAgentEventPublisher
{
    private readonly PublisherClient _publisher;

    public AgentEventPublisher(ServiceBusClient serviceBusClient, IConfiguration config)
    {
        var zoneEndpointId = AgentZone.ResolveEndpointId(config);
        // PublisherClient.CreateAsync completes synchronously (Task.FromResult; the
        // ServiceBusSender/AMQP link is created lazily on first send), so resolving it
        // once eagerly here is safe and avoids allocating a new sender per publish.
        _publisher = PublisherClient.CreateAsync(serviceBusClient, zoneEndpointId).GetAwaiter().GetResult();
    }

    public Task PublishAsync(IMessage message, CancellationToken cancellationToken = default)
        => _publisher.Publish(message, cancellationToken);
}
