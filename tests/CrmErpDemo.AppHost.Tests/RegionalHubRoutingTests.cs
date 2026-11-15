#pragma warning disable CA1707, CA2007
using CrmErpDemo.Contracts;
using CrmErpDemo.Contracts.Endpoints;
using CrmErpDemo.Contracts.Events;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.ServiceBus.Provisioning;

namespace CrmErpDemo.AppHost.Tests;

/// <summary>
/// The routing-keys showcase (Spec 045): CRM account events carry the country as their routing
/// key, and the regional hubs each take the accounts of their own countries.
/// </summary>
[TestClass]
public sealed class RegionalHubRoutingTests
{
    private static readonly CrmErpPlatformConfiguration Platform = new();

    [TestMethod]
    public void RealCatalog_PassesRoutingKeyValidation()
    {
        var errors = PlatformValidation.ValidateRoutingKeys(Platform);

        Assert.AreEqual(0, errors.Count, string.Join("; ", errors));
        PlatformValidation.EnsureValid(Platform);
    }

    [TestMethod]
    public void AccountEvents_CarryTheCountryAsRoutingKey()
    {
        Assert.AreEqual("DK", new CrmAccountCreated { CountryCode = "DK" }.GetRoutingKey());
        Assert.AreEqual("DE", new CrmAccountUpdated { CountryCode = "DE" }.GetRoutingKey());
        Assert.IsNull(new CrmAccountCreated { CountryCode = "" }.GetRoutingKey(), "No country means no key.");
        Assert.IsNull(new CrmAccountDeleted().GetRoutingKey(), "A deletion carries no country, so it is not routed by key.");
    }

    [TestMethod]
    [DataRow(nameof(NordicHubEndpoint), "DK,FI,NO,SE")]
    [DataRow(nameof(DachHubEndpoint), "AT,CH,DE")]
    public void RegionalHub_TakesAccountEventsOnlyForItsCountries(string endpointId, string expectedKeys)
    {
        var hub = Platform.Endpoints.Single(e => e.Id == endpointId);

        foreach (var eventTypeId in new[] { nameof(CrmAccountCreated), nameof(CrmAccountUpdated) })
        {
            var eventType = hub.EventTypesConsumed.Single(t => t.Id == eventTypeId);
            var keys = string.Join(",", hub.GetRoutingKeys(eventType).Order(StringComparer.Ordinal));

            Assert.AreEqual(expectedKeys, keys, $"{endpointId} / {eventTypeId}");
            Assert.IsFalse(hub.IsUnmatchedConsumer(eventType));
        }

        Assert.AreEqual(2, hub.EventTypesConsumed.Count(), "A hub takes created and updated accounts, nothing else.");
        Assert.IsFalse(hub.EventTypesProduced.Any());
    }

    [TestMethod]
    public void UnroutedHub_TakesTheAccountsNoHubClaims()
    {
        var unrouted = Platform.Endpoints.Single(e => e.Id == nameof(UnroutedHubEndpoint));

        foreach (var eventType in unrouted.EventTypesConsumed)
        {
            Assert.IsTrue(unrouted.IsUnmatchedConsumer(eventType), eventType.Id);
            Assert.AreEqual(0, unrouted.GetRoutingKeys(eventType).Count);
        }

        CollectionAssert.AreEquivalent(
            new[] { nameof(CrmAccountCreated), nameof(CrmAccountUpdated) },
            unrouted.EventTypesConsumed.Select(t => t.Id).ToList());
    }

    [TestMethod]
    public void ErpEndpoint_StillTakesEveryAccount()
    {
        var erp = Platform.Endpoints.Single(e => e.Id == nameof(ErpEndpoint));
        var created = erp.EventTypesConsumed.Single(t => t.Id == nameof(CrmAccountCreated));

        Assert.AreEqual(0, erp.GetRoutingKeys(created).Count, "ERP declares no keys, so it receives every account as before.");
        Assert.IsFalse(erp.IsUnmatchedConsumer(created));
    }

    // What `nb topology apply` (the demo's provisioner) writes on the CrmEndpoint topic: one
    // forward rule per consumer and event type, and the hubs' rules filter on the country.
    [TestMethod]
    [DataRow(nameof(CrmAccountCreated))]
    [DataRow(nameof(CrmAccountUpdated))]
    public void Topology_FiltersTheCrmTopicByCountryForEachHub(string eventTypeId)
    {
        var crm = Platform.Endpoints.Single(e => e.Id == nameof(CrmEndpoint));
        var subscriptions = TopologyDescriptor.ForEndpointTopic(crm, Platform);

        Assert.AreEqual(
            new RoutingRule(RoutingRuleMode.Keyed, ["DK", "FI", "NO", "SE"]),
            RoutingOf(subscriptions, nameof(NordicHubEndpoint), eventTypeId));
        Assert.AreEqual(
            new RoutingRule(RoutingRuleMode.Keyed, ["AT", "CH", "DE"]),
            RoutingOf(subscriptions, nameof(DachHubEndpoint), eventTypeId));
        Assert.AreEqual(
            new RoutingRule(RoutingRuleMode.Unmatched, ["AT", "CH", "DE", "DK", "FI", "NO", "SE"]),
            RoutingOf(subscriptions, nameof(UnroutedHubEndpoint), eventTypeId),
            "The unmatched rule lets through every country no hub claims, and accounts with none.");
        Assert.IsNull(
            RoutingOf(subscriptions, nameof(ErpEndpoint), eventTypeId),
            "ERP's forward rule carries no key filter: it takes every country.");
    }

    private static RoutingRule? RoutingOf(IReadOnlyList<ExpectedSubscription> subscriptions, string consumerId, string eventTypeId)
    {
        var forwarder = subscriptions.Single(s => s.ForwardTo == consumerId);
        var rule = forwarder.Rules.Single(r => r.Filter.Contains($"user.EventTypeId = '{eventTypeId}'", StringComparison.Ordinal));
        Assert.IsTrue(TopologyDescriptor.TryReadRoutingKeys(rule.Filter, eventTypeId, out var routing), rule.Filter);
        return routing;
    }

    [TestMethod]
    [DataRow("DK", nameof(NordicHubEndpoint))]
    [DataRow("FI", nameof(NordicHubEndpoint))]
    [DataRow("DE", nameof(DachHubEndpoint))]
    [DataRow("CH", nameof(DachHubEndpoint))]
    [DataRow("FR", nameof(UnroutedHubEndpoint))]
    [DataRow("US", nameof(UnroutedHubEndpoint))]
    [DataRow("", nameof(UnroutedHubEndpoint))]
    [DataRow("dk", nameof(UnroutedHubEndpoint))]
    public void RegionalHubs_ForCountry_NamesTheHubThatReceivesIt(string country, string expectedEndpointId)
    {
        var hub = RegionalHubs.ForCountry(country);

        Assert.AreEqual(expectedEndpointId, hub.EndpointId);
        Assert.AreEqual(expectedEndpointId == nameof(UnroutedHubEndpoint), hub.IsUnrouted);
    }

    [TestMethod]
    public void RegionalHubs_All_MatchesTheHubEndpointsInTheCatalog()
    {
        var declared = Platform.Endpoints.Where(e => e.System?.SystemId == "RegionalHub").Select(e => e.Id).ToList();

        CollectionAssert.AreEquivalent(declared, RegionalHubs.All.Select(h => h.EndpointId).ToList());
        Assert.AreEqual(3, declared.Count);
        Assert.IsNull(RegionalHubs.Find("ErpEndpoint"));
        Assert.AreEqual("Nordic hub", RegionalHubs.Find(nameof(NordicHubEndpoint))!.Name);
        CollectionAssert.AreEqual(
            new[] { "DK", "FI", "NO", "SE" },
            RegionalHubs.Find(nameof(NordicHubEndpoint))!.Countries.ToList());
    }
}
