#pragma warning disable CA1707, CA2007
extern alias AppHost;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CrmErpDemo.AppHost.Tests;

/// <summary>
/// The ERP adapter's MCP server in the AppHost: reachable from outside, and given its Entra tenant
/// and audience only when the deployment supplies them, so a local run starts without them.
/// </summary>
[TestClass]
public sealed class ErpAdapterMcpAppHostTests
{
    [TestMethod]
    public async Task The_erp_adapter_is_reachable_from_outside()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.CrmErpDemo_AppHost>(
            ["--operation", "publish"]);

        var adapter = builder.Resources.OfType<IResourceWithEndpoints>().Single(r => r.Name == "erp-adapter");
        var endpoints = adapter.Annotations.OfType<EndpointAnnotation>().Where(e => e.UriScheme is "http" or "https").ToList();

        Assert.IsNotEmpty(endpoints);
        Assert.IsTrue(endpoints.All(e => e.IsExternal), "Agents connect to the adapter's /api/mcp directly.");
    }

    [TestMethod]
    public async Task Without_the_parameters_the_adapter_gets_no_MCP_settings()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.CrmErpDemo_AppHost>(
            ["--operation", "publish"]);

        var environment = await EnvironmentOf(builder, "erp-adapter");

        Assert.IsFalse(environment.Keys.Any(k => k.StartsWith("AdapterMcp__", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task With_the_parameters_the_adapter_gets_its_tenant_and_audience()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.CrmErpDemo_AppHost>(
            ["--operation", "publish", "--Parameters:adapter-tenant-id=tenant", "--Parameters:adapter-audience=audience"]);

        var environment = await EnvironmentOf(builder, "erp-adapter");

        Assert.IsInstanceOfType<ParameterResource>(environment["AdapterMcp__TenantId"]);
        Assert.AreEqual("adapter-tenant-id", ((ParameterResource)environment["AdapterMcp__TenantId"]).Name);
        Assert.AreEqual("adapter-audience", ((ParameterResource)environment["AdapterMcp__Audience"]).Name);
    }

    private static async Task<Dictionary<string, object>> EnvironmentOf(IDistributedApplicationTestingBuilder builder, string resourceName)
    {
        var resource = builder.Resources.OfType<IResourceWithEnvironment>().Single(r => r.Name == resourceName);
        var context = new EnvironmentCallbackContext(builder.ExecutionContext, resource);
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            try
            {
                await annotation.Callback(context);
            }
            catch (InvalidOperationException)
            {
                // Callbacks that need a running app are irrelevant here.
            }
        }

        return context.EnvironmentVariables;
    }
}
