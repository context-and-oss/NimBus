#pragma warning disable CA1707, CA2007
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Tests;

/// <summary>
/// A reload or deep link to a client-side route must reach the SPA fallback. When an MVC
/// controller in the WebApp assembly claims the same path, the browser gets that controller's
/// response instead (a legacy Razor page for <c>/EventTypes</c>) and the React app never loads.
/// Mirrors the production order: <c>MapControllers()</c> first, then the fallback.
/// </summary>
[TestClass]
public class SpaDeepLinkRoutingTests
{
    private const string SpaMarker = "spa-index";

    private static IHost _host = null!;
    private static HttpClient _client = null!;

    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                    services.AddControllersWithViews()
                        .AddApplicationPart(typeof(ApplicationApiController).Assembly);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapControllers();
                        endpoints.MapFallback(context => context.Response.WriteAsync(SpaMarker));
                    });
                });
            });

        _host = await builder.StartAsync();
        _client = _host.GetTestClient();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    // The page routes declared in ClientApp/src/app.tsx.
    [TestMethod]
    [DataRow("/")]
    [DataRow("/Endpoints")]
    [DataRow("/Endpoints/Details/BillingEndpoint")]
    [DataRow("/EventTypes")]
    [DataRow("/EventTypes/Details/OrderPlaced")]
    [DataRow("/Message/Index/BillingEndpoint/00000000-0000-0000-0000-000000000001")]
    [DataRow("/Message/Index/BillingEndpoint/00000000-0000-0000-0000-000000000001/0")]
    [DataRow("/Messages")]
    [DataRow("/Failed")]
    [DataRow("/Metrics")]
    [DataRow("/Flow")]
    [DataRow("/Heartbeat")]
    [DataRow("/Monitor")]
    [DataRow("/Insights")]
    [DataRow("/Audits")]
    [DataRow("/Operations")]
    [DataRow("/Topology")]
    [DataRow("/Settings")]
    [DataRow("/Admin")]
    [DataRow("/Simulate")]
    [DataRow("/AccessControl")]
    public async Task Client_route_reaches_the_spa_fallback(string path)
    {
        using var response = await _client.GetAsync(path);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, $"{path} did not reach the SPA fallback.");
        Assert.AreEqual(SpaMarker, await response.Content.ReadAsStringAsync(), $"{path} was served by an MVC controller.");
    }
}
