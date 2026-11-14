#pragma warning disable CA1707, CA2007
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>
/// The single definition of "what a per-endpoint tracking container looks like". Every
/// creation path (the store, the CLI copy target, the WebApp copy target) goes through
/// it, so its shape and its reserved-id rejection are asserted here once.
/// </summary>
[TestClass]
public sealed class CosmosContainerDefaultsTests
{
    [TestMethod]
    public void EndpointContainer_enables_container_level_ttl()
    {
        var properties = CosmosContainerDefaults.EndpointContainer("endpoint-1");

        Assert.AreEqual("endpoint-1", properties.Id);
        Assert.AreEqual("/id", properties.PartitionKeyPath);
        Assert.AreEqual(-1, properties.DefaultTimeToLive,
            "Cosmos honours a document ttl only when the container's DefaultTimeToLive is set.");
    }

    [TestMethod]
    [DataRow("subscriptions")]
    [DataRow("messages")]
    [DataRow("audits")]
    [DataRow("eventschemas")]
    [DataRow("eventreports")]
    [DataRow("accesscontrol")]
    [DataRow("Metadata")]
    [DataRow("inbox")]
    [DataRow("settings")]
    [DataRow("servicehealth")]
    [DataRow("heartbeatuptimedays")]
    [DataRow("heartbeatgaps")]
    [DataRow("endpointacknowledgements")]
    public void EnsureNotReservedEndpointId_rejects_the_stores_own_container_ids(string reserved)
    {
        var ex = Assert.ThrowsExactly<ArgumentException>(
            () => CosmosContainerDefaults.EnsureNotReservedEndpointId(reserved));

        StringAssert.Contains(ex.Message, reserved, "The failure must name the offending id.");
    }

    [TestMethod]
    [DataRow("subscriptions")]
    [DataRow("Metadata")]
    public void EndpointContainer_rejects_reserved_ids_too(string reserved)
    {
        Assert.ThrowsExactly<ArgumentException>(() => CosmosContainerDefaults.EndpointContainer(reserved));
    }

    [TestMethod]
    [DataRow("Messages")]
    [DataRow("Subscriptions")]
    [DataRow("INBOX")]
    [DataRow("metadata")]
    public void EnsureNotReservedEndpointId_is_ordinal(string differentlyCased)
    {
        // Cosmos container ids are case-sensitive: "Messages" is a different physical
        // container from "messages" and does not collide, so rejecting it would break
        // working deployments for no safety gain.
        CosmosContainerDefaults.EnsureNotReservedEndpointId(differentlyCased);
    }

    [TestMethod]
    public void EnsureNotReservedEndpointId_accepts_ordinary_ids()
    {
        CosmosContainerDefaults.EnsureNotReservedEndpointId("endpoint-1");
        CosmosContainerDefaults.EnsureNotReservedEndpointId("orders.adapter");
    }

    [TestMethod]
    public void EnsureNotReservedEndpointId_rejects_null_and_empty()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => CosmosContainerDefaults.EnsureNotReservedEndpointId(null!));
        Assert.ThrowsExactly<ArgumentException>(() => CosmosContainerDefaults.EnsureNotReservedEndpointId(string.Empty));
    }

    [TestMethod]
    public void ReservedContainerIds_lists_exactly_the_stores_own_containers()
    {
        CollectionAssert.AreEquivalent(
            new[]
            {
                "subscriptions", "messages", "audits", "eventschemas", "eventreports",
                "accesscontrol", "Metadata", "inbox", "settings", "servicehealth",
                "heartbeatuptimedays", "heartbeatgaps", "endpointacknowledgements", "operatoroperations",
            },
            System.Linq.Enumerable.ToArray(CosmosContainerDefaults.ReservedContainerIds));
    }
}
