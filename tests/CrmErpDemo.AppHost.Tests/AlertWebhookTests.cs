#pragma warning disable CA1707, CA2007
extern alias AppHost;

using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace CrmErpDemo.AppHost.Tests;

/// <summary>
/// Endpoint alert webhooks in the demo: a local run switches them on for the Resolver and the
/// WebApp with one development key and lets them reach a receiver on this machine; the Azure
/// deployment leaves them off.
/// </summary>
[TestClass]
public sealed class AlertWebhookTests
{
    private const string SigningKey = "NimBus__Alerts__Webhook__SigningKey";

    [TestMethod]
    public async Task A_local_run_gives_the_Resolver_and_the_WebApp_the_same_signing_key()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.CrmErpDemo_AppHost>();

        var resolver = await EnvironmentOf(builder, "resolver");
        var webApp = await EnvironmentOf(builder, "nimbus-ops");

        var key = (string)resolver[SigningKey];
        Assert.IsTrue(key.Length >= 32, "The Resolver rejects a signing key shorter than 32 characters.");
        Assert.AreEqual(key, webApp[SigningKey], "The WebApp shows a secret the Resolver's signature must match.");
    }

    [TestMethod]
    public async Task A_local_run_lets_webhooks_reach_a_receiver_on_this_machine()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.CrmErpDemo_AppHost>();

        var resolver = await EnvironmentOf(builder, "resolver");
        var webApp = await EnvironmentOf(builder, "nimbus-ops");

        Assert.AreEqual("true", resolver["NimBus__Alerts__Webhook__AllowInsecureHttp"]);
        Assert.AreEqual("true", resolver["NimBus__Alerts__Webhook__AllowPrivateNetworks"]);
        Assert.AreEqual("true", webApp["NimBus__Alerts__Webhook__AllowInsecureHttp"]);
    }

    [TestMethod]
    public async Task The_Azure_deployment_has_no_alert_webhook_settings()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<AppHost::Projects.CrmErpDemo_AppHost>(
            ["--operation", "publish"]);

        foreach (var name in new[] { "resolver", "nimbus-ops" })
        {
            var environment = await EnvironmentOf(builder, name);
            Assert.IsFalse(
                environment.Keys.Any(k => k.StartsWith("NimBus__Alerts__Webhook__", StringComparison.Ordinal)),
                $"{name} must not get the development key in Azure.");
        }
    }

    // Runs only the plain environment callbacks: endpoint references stay unresolved objects,
    // so the model can be inspected without starting anything.
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
                // Callbacks that need a running app (service discovery, connection strings)
                // are irrelevant here.
            }
        }

        return context.EnvironmentVariables;
    }
}
