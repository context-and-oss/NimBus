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
using NimBus.WebApp.Services.IntegratorApiKeys;

namespace NimBus.WebApp.Tests.IntegratorApiKeys;

/// <summary>
/// Every sign-in mode registers the integrator API key scheme without making it a default, so a
/// person's sign-in is untouched (Spec 045).
/// </summary>
[TestClass]
public class IntegratorApiKeyStartupWiringTests
{
    [TestMethod]
    [DataRow("entra", "OpenIdConnect")]
    [DataRow("identity", "Identity.Application")]
    [DataRow("dual", "Identity.Application")]
    [DataRow("local", LocalDevAuthHandler.SchemeName)]
    public async Task The_key_scheme_is_registered_and_never_the_default(string method, string signInScheme)
    {
        using var host = BuildHost(method);
        var schemes = host.Services.GetRequiredService<IAuthenticationSchemeProvider>();

        Assert.IsNotNull(await schemes.GetSchemeAsync(IntegratorApiKeyDefaults.SchemeName));
        Assert.AreEqual(signInScheme, (await schemes.GetDefaultAuthenticateSchemeAsync())!.Name);
        Assert.IsNotNull(host.Services.GetService<IIntegratorApiKeyProvider>());
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
