#pragma warning disable CA1707, CA2007

using System;
using System.Collections.Generic;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace NimBus.WebApp.Tests;

/// <summary>
/// A WebApp with no sign-in method used to start and then fail every request, anonymous ones
/// included, with Microsoft.Identity.Web's IDW10106 ("The 'ClientId' option must be provided").
/// It now refuses to start and names the settings that choose a sign-in method.
/// </summary>
[TestClass]
public sealed class AuthenticationConfigurationTests
{
    [TestMethod]
    [DataRow("Development")]
    [DataRow("Production")]
    public void Startup_without_a_sign_in_method_fails_with_an_actionable_message(string environment)
    {
        var exception = Assert.ThrowsExactly<InvalidOperationException>(() => BuildHost(environment, new()));

        StringAssert.Contains(exception.Message, "AzureAd:ClientId", StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, "NimBusIdentity:ConnectionString", StringComparison.Ordinal);
        StringAssert.Contains(exception.Message, "EnableLocalDevAuthentication", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Startup_with_a_blank_client_id_fails_with_an_actionable_message()
    {
        // The shipped appsettings.json carries an empty AzureAd:ClientId.
        var exception = Assert.ThrowsExactly<InvalidOperationException>(
            () => BuildHost("Production", new() { ["AzureAd:ClientId"] = "" }));

        StringAssert.Contains(exception.Message, "AzureAd:ClientId", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("entra")]
    [DataRow("identity")]
    [DataRow("dual")]
    [DataRow("local")]
    public void Startup_with_a_sign_in_method_starts(string method)
    {
        var settings = new Dictionary<string, string?>();
        if (method is "entra" or "dual")
        {
            settings["AzureAd:Instance"] = "https://login.microsoftonline.com/";
            settings["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000002";
            settings["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000001";
        }

        if (method is "identity" or "dual")
        {
            settings["NimBusIdentity:ConnectionString"] = "Server=localhost;Database=unused;Integrated Security=true";
        }

        if (method == "local")
        {
            settings["EnableLocalDevAuthentication"] = "true";
        }

        using var host = BuildHost(method == "local" ? "Development" : "Production", settings);
    }

    private static IHost BuildHost(string environment, Dictionary<string, string?> authSettings)
    {
        var settings = new Dictionary<string, string?>
        {
            ["NimBus:StorageProvider"] = "sqlserver",
            ["SqlConnection"] = "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true",
            ["ServiceBusNamespace"] = "unit-test",
        };
        foreach (var (key, value) in authSettings)
        {
            settings[key] = value;
        }

        return new HostBuilder()
            .UseEnvironment(environment)
            .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(settings))
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices((context, services) =>
                    new Startup(context.Configuration, context.HostingEnvironment).ConfigureServices(services));
                web.Configure(_ => { });
            })
            .Build();
    }
}
