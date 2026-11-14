using NimBus.Core.Events;
using System.Collections.Generic;

namespace NimBus.Core.Endpoints;

public interface IEndpoint
{
    string Id { get; }
    string Name { get; }
    string? Description { get; }
    string? Namespace { get; }
    string SecurityGroupName { get; }
    ISystem? System { get; }

    /// <summary>
    /// Event types produced by the endpoint.
    /// </summary>
    IEnumerable<IEventType> EventTypesProduced { get; }

    /// <summary>
    /// Event types consumed by the endpoint.
    /// </summary>
    IEnumerable<IEventType> EventTypesConsumed { get; }

    IEnumerable<IRoleAssignment> RoleAssignments { get; }

    /// <summary>
    /// The routing keys the endpoint takes <paramref name="eventType"/> for (Spec 045), in
    /// ordinal order. Empty means every key: an unkeyed consumer, as before routing keys.
    /// </summary>
    /// <param name="eventType">A consumed event type.</param>
    IReadOnlyCollection<string> GetRoutingKeys(IEventType eventType) => global::System.Array.Empty<string>();

    /// <summary>
    /// True when the endpoint is the unmatched consumer of <paramref name="eventType"/>: it
    /// receives the messages whose routing key no consumer claims, and those with no key.
    /// </summary>
    /// <param name="eventType">A consumed event type.</param>
    bool IsUnmatchedConsumer(IEventType eventType) => false;
}
