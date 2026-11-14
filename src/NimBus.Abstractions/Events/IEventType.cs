using System;
using System.Collections.Generic;

namespace NimBus.Core.Events;

public interface IEventType
{
    string Id { get; }
    string Name { get; }
    string? Description { get; }
    string? Namespace { get; }
    IEnumerable<IProperty> Properties { get; }
    Type GetEventClassType();
    IEvent? GetEventExample();

    /// <summary>
    /// The payload property that holds the routing key (Spec 045), or null when the event type
    /// isn't routed by key.
    /// </summary>
    string? RoutingKeyProperty => null;

    /// <summary>
    /// The payload property named by <see cref="SessionKeyAttribute"/>, whose value is the default
    /// session key, or null when the class has no attribute.
    /// </summary>
    string? SessionKeyProperty => null;
}
