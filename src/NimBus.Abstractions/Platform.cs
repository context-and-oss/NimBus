using System;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using System.Collections.Generic;
using System.Linq;

namespace NimBus.Core;

public interface IPlatform
{
    /// <summary>
    /// Endpoints connected to the platform.
    /// </summary>
    IEnumerable<IEndpoint> Endpoints { get; }

    IEnumerable<IEventType> EventTypes { get; }

    IEnumerable<IEndpoint> GetConsumers(IEventType eventType);

    IEnumerable<IEndpoint> GetProducers(IEventType eventType);

    /// <summary>
    /// Dynamically-typed event forwards (spec 022 D5). Empty for platforms with no dynamic events.
    /// Provisioning creates a forward subscription + EventTypeId rule for each.
    /// </summary>
    IReadOnlyList<NimBus.Core.Endpoints.DynamicForward> DynamicForwards
        => Array.Empty<NimBus.Core.Endpoints.DynamicForward>();

    /// <summary>
    /// Every routing key a consumer of <paramref name="eventType"/> claims (Spec 045), distinct
    /// and in ordinal order. The unmatched consumer's forward rule excludes exactly these keys.
    /// </summary>
    /// <param name="eventType">The event type.</param>
    IReadOnlyCollection<string> GetClaimedRoutingKeys(IEventType eventType) =>
        RoutingKeys.Canonical(GetConsumers(eventType).SelectMany(consumer => consumer.GetRoutingKeys(eventType)));
}

public abstract class Platform : IPlatform
{
    private Dictionary<string, IEndpoint> _endpoints;

    // Topology is static once endpoints are registered, but the WebApp asks for
    // producers/consumers of an event type repeatedly (per event type, per request),
    // and each call used to scan every endpoint — O(endpoints) per lookup, O(n*m)
    // across a full event-type listing. Build the inverted indexes once, lazily,
    // and invalidate if an endpoint is added afterwards. Equality matches the old
    // `.Contains(eventType)` (default IEventType comparer), so results are identical.
    private List<IEventType>? _eventTypesCache;
    private Dictionary<IEventType, List<IEndpoint>>? _consumersByType;
    private Dictionary<IEventType, List<IEndpoint>>? _producersByType;

    protected Platform()
    {
        _endpoints = new Dictionary<string, IEndpoint>();
    }

    protected void AddEndpoint(IEndpoint endpoint)
    {
#if NET6_0_OR_GREATER
        ArgumentNullException.ThrowIfNull(endpoint);
#else
        if (endpoint is null) throw new ArgumentNullException(nameof(endpoint));
#endif
        _endpoints.Add(endpoint.Id, endpoint);
        // Topology changed — drop the derived indexes so they rebuild on next read.
        _eventTypesCache = null;
        _consumersByType = null;
        _producersByType = null;
    }

    public IEnumerable<IEndpoint> Endpoints => _endpoints.Values;

    /// <inheritdoc cref="IPlatform.DynamicForwards"/>
    public virtual IReadOnlyList<NimBus.Core.Endpoints.DynamicForward> DynamicForwards =>
        Array.Empty<NimBus.Core.Endpoints.DynamicForward>();

    public IEnumerable<IEventType> EventTypes =>
        _eventTypesCache ??= Endpoints
            .SelectMany(ep => ep.EventTypesConsumed.Union(ep.EventTypesProduced))
            .Distinct()
            .ToList();

    public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) =>
        (_consumersByType ??= BuildIndex(ep => ep.EventTypesConsumed))
            .TryGetValue(eventType, out var consumers) ? consumers : Enumerable.Empty<IEndpoint>();

    public IEnumerable<IEndpoint> GetProducers(IEventType eventType) =>
        (_producersByType ??= BuildIndex(ep => ep.EventTypesProduced))
            .TryGetValue(eventType, out var producers) ? producers : Enumerable.Empty<IEndpoint>();

    private Dictionary<IEventType, List<IEndpoint>> BuildIndex(
        Func<IEndpoint, IEnumerable<IEventType>> eventTypesOf)
    {
        var index = new Dictionary<IEventType, List<IEndpoint>>();
        foreach (var endpoint in _endpoints.Values)
        {
            foreach (var eventType in eventTypesOf(endpoint))
            {
                if (!index.TryGetValue(eventType, out var endpoints))
                {
                    endpoints = new List<IEndpoint>();
                    index[eventType] = endpoints;
                }
                endpoints.Add(endpoint);
            }
        }
        return index;
    }
}
