using System;
using System.Collections.Generic;
using System.Linq;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;

namespace NimBus.CommandLine;

/// <summary>
/// A single-endpoint window over a platform, fed to the AsyncAPI exporter to produce the
/// per-service document EventCatalog attaches via <c>specifications</c>. Producer/consumer
/// lookups are intersected with the endpoint so the document only describes this service's
/// operations; cross-endpoint forward-subscription detail stays in the platform-wide export.
/// </summary>
internal sealed class SingleEndpointPlatformView : IPlatform
{
    private readonly IPlatform _inner;
    private readonly IEndpoint _endpoint;

    public SingleEndpointPlatformView(IPlatform inner, IEndpoint endpoint)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _endpoint = endpoint ?? throw new ArgumentNullException(nameof(endpoint));
    }

    public IEnumerable<IEndpoint> Endpoints => new[] { _endpoint };

    public IEnumerable<IEventType> EventTypes => _endpoint.EventTypesProduced
        .Concat(_endpoint.EventTypesConsumed)
        .GroupBy(e => e.Id, StringComparer.Ordinal)
        .Select(g => g.First());

    public IReadOnlyList<DynamicForward> DynamicForwards => _inner.DynamicForwards
        .Where(f => string.Equals(f.SourceEndpoint, _endpoint.Id, StringComparison.Ordinal)
                    || string.Equals(f.TargetEndpoint, _endpoint.Id, StringComparison.Ordinal))
        .ToList();

    public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) =>
        _inner.GetConsumers(eventType).Where(e => string.Equals(e.Id, _endpoint.Id, StringComparison.Ordinal));

    // The unmatched consumer's filter excludes the keys every consumer on the platform claims,
    // not only this endpoint's, so this lookup must not be narrowed like GetConsumers.
    public IReadOnlyCollection<string> GetClaimedRoutingKeys(IEventType eventType) =>
        _inner.GetClaimedRoutingKeys(eventType);

    public IEnumerable<IEndpoint> GetProducers(IEventType eventType) =>
        _inner.GetProducers(eventType).Where(e => string.Equals(e.Id, _endpoint.Id, StringComparison.Ordinal));
}
