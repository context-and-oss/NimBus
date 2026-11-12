#pragma warning disable CA1707, CA2007
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.Services.DashboardTokens;

namespace NimBus.WebApp.Tests.DashboardTokens;

/// <summary>
/// Every sign-in mode registers the dashboard token scheme, keeps its own default authenticate
/// scheme, and names that scheme in the Monitor feed policy next to the token scheme.
/// </summary>
[TestClass]
public class DashboardTokenStartupWiringTests
{
    [TestMethod]
    [DataRow("entra", "OpenIdConnect")]
    [DataRow("identity", "Identity.Application")]
    [DataRow("dual", "Identity.Application")]
    [DataRow("local", LocalDevAuthHandler.SchemeName)]
    public async Task The_feed_policy_names_the_sign_in_scheme_and_the_token_scheme(string method, string signInScheme)
    {
        using var host = BuildHost(method);
        var schemes = host.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        var policies = host.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        Assert.IsNotNull(await schemes.GetSchemeAsync(DashboardTokenDefaults.SchemeName));
        Assert.AreEqual(signInScheme, (await schemes.GetDefaultAuthenticateSchemeAsync())!.Name);

        var policy = await policies.GetPolicyAsync(DashboardTokenDefaults.PolicyName);
        Assert.IsNotNull(policy);
        CollectionAssert.AreEquivalent(new[] { signInScheme, DashboardTokenDefaults.SchemeName }, policy.AuthenticationSchemes.ToArray());
    }

    private static IHost BuildHost(string method)
    {
        var settings = new Dictionary<string, string?>
        {
            ["NimBus:StorageProvider"] = "sqlserver",
            ["SqlConnection"] = "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true",
            ["ServiceBusNamespace"] = "unit-test",
        };
        if (method is "entra" or "dual")
        {
            settings["AzureAd:Instance"] = "https://login.microsoftonline.com/";
            settings["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000002";
            settings["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000001";
        }

        if (method is "identity" or "dual")
            settings["NimBusIdentity:ConnectionString"] = "Server=localhost;Database=unused;Integrated Security=true";

        if (method == "local")
            settings["EnableLocalDevAuthentication"] = "true";

        return new HostBuilder()
            .UseEnvironment(method == "local" ? "Development" : "Production")
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
