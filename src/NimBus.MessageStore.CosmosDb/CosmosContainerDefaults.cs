using System;
using System.Collections.Generic;
using Microsoft.Azure.Cosmos;

namespace NimBus.MessageStore;

/// <summary>
/// Container-level defaults for the Cosmos DB message store. Per-endpoint tracking
/// containers must be created with TTL enabled: Cosmos honours a document's <c>ttl</c>
/// only when the container's <see cref="ContainerProperties.DefaultTimeToLive"/> is set.
/// </summary>
public static class CosmosContainerDefaults
{
    /// <summary>Partition key path of a per-endpoint tracking container.</summary>
    public const string EndpointPartitionKeyPath = "/id";

    /// <summary>
    /// "TTL on, no container default": item-level <c>ttl</c> values take effect, while documents
    /// with no <c>ttl</c> — or <c>ttl = -1</c> — are never expired.
    /// </summary>
    public const int EndpointContainerDefaultTimeToLive = -1;

    /// <summary>
    /// Container ids owned by the message store itself. An endpoint may not use one of these as
    /// its id: the endpoint container and the shared container would be the same physical
    /// container and the same cache entry, so whichever is resolved first would decide the
    /// container's partition key path and its TTL mode for the whole process.
    /// Comparison is ordinal because Cosmos container ids are case-sensitive — <c>Messages</c>
    /// and <c>messages</c> are different containers and do not collide.
    /// </summary>
    public static IReadOnlyCollection<string> ReservedContainerIds { get; } =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "subscriptions", "messages", "audits", "eventschemas",
            "eventreports", "accesscontrol", "Metadata", "inbox",
            "settings", "servicehealth", "heartbeatuptimedays", "heartbeatgaps",
            "endpointacknowledgements", "operatoroperations",
        };

    /// <summary>Throws when <paramref name="endpointId"/> is null, empty, or a reserved container id.</summary>
    /// <param name="endpointId">The endpoint id, which is also the container id.</param>
    /// <exception cref="ArgumentNullException">The id is null.</exception>
    /// <exception cref="ArgumentException">The id is empty or reserved.</exception>
    public static void EnsureNotReservedEndpointId(string endpointId)
    {
        ArgumentException.ThrowIfNullOrEmpty(endpointId);

        if (ReservedContainerIds.Contains(endpointId))
        {
            throw new ArgumentException(
                $"'{endpointId}' is reserved by the NimBus Cosmos message store and cannot be used as an "
                + "endpoint id; it would share a container with the store's own data. Reserved ids: "
                + $"{string.Join(", ", ReservedContainerIds)}.",
                nameof(endpointId));
        }
    }

    /// <summary>Builds the <see cref="ContainerProperties"/> for a per-endpoint tracking container.</summary>
    /// <param name="endpointId">The endpoint id, which is also the container id.</param>
    /// <returns>Container properties with TTL enabled at container level.</returns>
    /// <exception cref="ArgumentNullException">The id is null.</exception>
    /// <exception cref="ArgumentException">The id is empty or reserved.</exception>
    public static ContainerProperties EndpointContainer(string endpointId)
    {
        EnsureNotReservedEndpointId(endpointId);
        return new ContainerProperties(endpointId, EndpointPartitionKeyPath)
        {
            DefaultTimeToLive = EndpointContainerDefaultTimeToLive,
        };
    }
}
