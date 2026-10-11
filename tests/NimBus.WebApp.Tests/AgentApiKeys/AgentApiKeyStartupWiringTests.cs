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
using NimBus.WebApp.Services.AgentApiKeys;

namespace NimBus.WebApp.Tests.AgentApiKeys;

/// <summary>
/// Every sign-in mode registers the agent key scheme, keeps its own default authenticate
/// scheme, and names that scheme in the agent API policy next to the key scheme. The expected
/// defaults pin today's behaviour: Identity sets its cookie explicitly, and Microsoft.Identity.Web's
/// web app makes OpenIdConnect the default rather than the "Az" policy scheme.
/// </summary>
[TestClass]
public class AgentApiKeyStartupWiringTests
{
    [TestMethod]
    [DataRow("entra", "OpenIdConnect")]
    [DataRow("identity", "Identity.Application")]
    [DataRow("dual", "Identity.Application")]
    [DataRow("local", LocalDevAuthHandler.SchemeName)]
    public async Task The_agent_api_policy_names_the_sign_in_scheme_and_the_key_scheme(string method, string signInScheme)
    {
        using var host = BuildHost(method);
        var schemes = host.Services.GetRequiredService<IAuthenticationSchemeProvider>();
        var policies = host.Services.GetRequiredService<IAuthorizationPolicyProvider>();

        Assert.IsNotNull(await schemes.GetSchemeAsync(AgentApiKeyDefaults.SchemeName));
        Assert.AreEqual(signInScheme, (await schemes.GetDefaultAuthenticateSchemeAsync())!.Name);

        var policy = await policies.GetPolicyAsync(AgentApiKeyDefaults.PolicyName);
        Assert.IsNotNull(policy);
        CollectionAssert.AreEquivalent(new[] { signInScheme, AgentApiKeyDefaults.SchemeName }, policy.AuthenticationSchemes.ToArray());
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
