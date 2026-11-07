#pragma warning disable CA1707, CA2007
extern alias AppHost;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CrmErpDemo.AppHost.Tests;

/// <summary>
/// Endpoint alert mail in the demo: a local run catches the Resolver's alert mail in Mailpit,
/// and the Azure deployment leaves alert mail off.
/// </summary>
[TestClass]
public sealed class AlertMailTests
{
    private static readonly string[] AlertMailSettings =
    [
        "NimBus__Alerts__Mail__Smtp__Host",
        "NimBus__Alerts__Mail__Smtp__Port",
        "NimBus__Alerts__Mail__Smtp__UseSsl",
        "NimBus__Alerts__Mail__From",
        "NimBus__Alerts__Mail__WebAppUrl",
    ];

    [TestMethod]
    public async Task A_local_run_adds_Mailpit_with_its_SMTP_and_web_endpoints()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.CrmErpDemo_AppHost>();

        var mailpit = builder.Resources.OfType<ContainerResource>().Single(r => r.Name == "mailpit");

        var image = mailpit.Annotations.OfType<ContainerImageAnnotation>().Single();
        Assert.AreEqual("axllent/mailpit", image.Image);
        var endpoints = mailpit.Annotations.OfType<EndpointAnnotation>().ToDictionary(e => e.Name);
        Assert.AreEqual(1025, endpoints["smtp"].TargetPort);
        Assert.AreEqual(8025, endpoints["http"].TargetPort);
    }

    [TestMethod]
    public async Task A_local_run_points_the_Resolver_at_Mailpit()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.CrmErpDemo_AppHost>();
        var resolver = builder.Resources.OfType<IResourceWithEnvironment>().Single(r => r.Name == "resolver");

        var environment = await EnvironmentOf(resolver, builder.ExecutionContext);

        foreach (var setting in AlertMailSettings)
        {
            Assert.IsTrue(environment.ContainsKey(setting), $"missing {setting}");
        }

        Assert.AreEqual("false", environment["NimBus__Alerts__Mail__Smtp__UseSsl"]);
    }

    [TestMethod]
    public async Task The_Azure_deployment_has_no_Mailpit_and_no_alert_mail()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.CrmErpDemo_AppHost>(
            ["--operation", "publish"]);
        var resolver = builder.Resources.OfType<IResourceWithEnvironment>().Single(r => r.Name == "resolver");

        Assert.IsFalse(builder.Resources.Any(r => r.Name == "mailpit"));
        var environment = await EnvironmentOf(resolver, builder.ExecutionContext);
        Assert.IsFalse(environment.Keys.Any(k => k.StartsWith("NimBus__Alerts__", StringComparison.Ordinal)));
    }

    // Runs only the plain environment callbacks: endpoint references stay unresolved objects,
    // so the model can be inspected without starting anything.
    private static async Task<Dictionary<string, object>> EnvironmentOf(
        IResourceWithEnvironment resource,
        DistributedApplicationExecutionContext executionContext)
    {
        var context = new EnvironmentCallbackContext(executionContext, resource);
        foreach (var annotation in resource.Annotations.OfType<EnvironmentCallbackAnnotation>())
        {
            try
            {
                await annotation.Callback(context);
            }
            catch (InvalidOperationException)
            {
                // Callbacks that need a running app (service discovery, connection strings)
                // are irrelevant here.
            }
        }

        return context.EnvironmentVariables;
    }
}
