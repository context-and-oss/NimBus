#pragma warning disable CA1707, CA2007
extern alias AppHost;

using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using CrmErpDemo.Contracts.Endpoints;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CrmErpDemo.AppHost.Tests;

[TestClass]
public sealed class RegionalHubAppHostTests
{
    // The routing-keys showcase runs one adapter project once per hub. A hub endpoint without
    // an instance would collect messages nobody handles.
    [TestMethod]
    public async Task Publish_starts_one_adapter_instance_per_regional_hub()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.CrmErpDemo_AppHost>(
            ["--operation", "publish"]);

        var hubResources = builder.Resources
            .OfType<ProjectResource>()
            .Where(resource => resource.Name.StartsWith("hub-", StringComparison.Ordinal))
            .Select(resource => resource.Name)
            .ToList();

        CollectionAssert.AreEquivalent(new[] { "hub-nordic", "hub-dach", "hub-unrouted" }, hubResources);
        Assert.AreEqual(RegionalHubs.All.Count, hubResources.Count, "One instance per hub in the catalog.");
    }
}
