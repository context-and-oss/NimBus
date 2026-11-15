#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services.Insights;

namespace NimBus.WebApp.Tests;

/// <summary>Spec 046 §4.1: the occurrence read behind the operator worklist.</summary>
public sealed partial class FailedInsightsServiceTests
{
    [TestMethod]
    public async Task Occurrences_for_endpoints_return_only_the_listed_endpoints_with_their_patterns()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "Timeout calling CRM after 30 s", minutesAgo: 30, sessionId: "s1");
        await Fail(store, "billing", "e2", "m2", "Timeout calling CRM after 30 s", minutesAgo: 20);

        var set = await Service(store, new FakeEnrichment()).GetOccurrencesForEndpointsAsync(Period._1d, ["ORDERS"]);

        var row = set.Occurrences.Single();
        Assert.AreEqual("orders", row.EndpointId);
        Assert.AreEqual("e1", row.EventId);
        Assert.AreEqual("s1", row.SessionId);
        Assert.AreEqual("m1", row.MessageId);
        Assert.AreEqual("OrderPlaced", row.EventTypeId);
        Assert.IsFalse(string.IsNullOrEmpty(row.ErrorPattern));
        Assert.AreEqual(Now.AddMinutes(-30), row.FailedAtUtc);
    }

    [TestMethod]
    public async Task Occurrences_for_no_endpoints_read_nothing()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "Timeout", minutesAgo: 30);

        var set = await Service(store, new FakeEnrichment()).GetOccurrencesForEndpointsAsync(Period._1d, []);

        Assert.IsEmpty(set.Occurrences);
        Assert.AreEqual(0, store.Calls);
    }

    [TestMethod]
    public async Task Occurrences_for_endpoints_leave_the_role_check_to_the_caller_and_the_overview_keeps_its_own()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "Timeout", minutesAgo: 30);
        var service = Service(store, new FakeEnrichment(), authorization: new Authorization(siteReader: false));

        var set = await service.GetOccurrencesForEndpointsAsync(Period._1d, ["orders"]);
        var overview = await service.GetOverviewAsync(new FailedInsightsRequest(Period._1d, null, null, null, Enrich: false, Refresh: false));

        Assert.HasCount(1, set.Occurrences);
        Assert.AreEqual(FailedInsightsQueryStatus.Forbidden, overview.Status);
    }

    [TestMethod]
    public async Task Occurrences_for_endpoints_share_the_cached_read_with_the_overview()
    {
        var store = new CountingStore();
        await Fail(store, "orders", "e1", "m1", "Timeout", minutesAgo: 30);
        var cache = NewCache();
        var service = Service(store, new FakeEnrichment(), cache: cache);

        await service.GetOverviewAsync(new FailedInsightsRequest(Period._1d, null, null, null, Enrich: false, Refresh: false));
        await service.GetOccurrencesForEndpointsAsync(Period._1d, ["orders"]);
        await service.GetOccurrencesForEndpointsAsync(Period._1d, ["billing"]);

        Assert.AreEqual(1, store.Calls);
    }
}
