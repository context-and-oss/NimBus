#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Extensions.Identity.Controllers;
using NimBus.WebApp.Constants;
using NimBus.WebApp.Hubs;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.RateLimiting;
using NimBus.WebApp.Versioning;

namespace NimBus.WebApp.Tests;

/// <summary>
/// AC-2/AC-3/AC-10: which endpoints carry which policy, asserted as exhaustive
/// set equality against the live <see cref="EndpointDataSource"/> built through
/// the production registration. Reading the real endpoint data source is what
/// makes AC-3's "a clean rebuild still leaves every policy applied" real: the
/// controllers under test are the ones NSwag just regenerated, so a renamed
/// action breaks the build (via nameof) and a moved route breaks this test.
/// <para>
/// This class enumerates endpoints and issues no HTTP requests — controller
/// instances are never constructed, so AccountController needs no UserManager,
/// SignInManager or database to appear here.
/// </para>
/// </summary>
[TestClass]
public class RateLimitEndpointMetadataTests
{
    private static IHost _host = null!;
    private static IReadOnlyList<EndpointFacts> _endpoints = null!;

    private sealed record EndpointFacts(string Method, string Route, string? Policy);

    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        _host = await BuildHostAsync(new Dictionary<string, string?>());
        _endpoints = Describe(_host);
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [TestMethod]
    public void Agent_receive_is_the_only_endpoint_on_the_receive_policy()
    {
        CollectionAssert.AreEquivalent(
            new[] { "GET api/v1/agent/receive", "GET api/agent/receive" },
            Routes(RateLimitPolicyNames.AgentReceive),
            "The concurrency limiter must bind to exactly the long-poll receive endpoint.");
    }

    [TestMethod]
    public void Login_policy_binds_to_the_post_only()
    {
        CollectionAssert.AreEquivalent(
            new[] { "POST account/login" },
            Routes(RateLimitPolicyNames.Login),
            "GET account/login renders the sign-in page; throttling it would break sign-in for anyone who reloads.");
    }

    [TestMethod]
    public void Search_policy_binds_to_both_search_endpoints()
    {
        CollectionAssert.AreEquivalent(
            new[]
            {
                "POST api/v1/messages/search", "POST api/v1/audits/search",
                "POST api/messages/search", "POST api/audits/search",
            },
            Routes(RateLimitPolicyNames.Search));
    }

    [TestMethod]
    public void Admin_policy_covers_every_api_admin_route_and_nothing_else()
    {
        var onPolicy = Routes(RateLimitPolicyNames.Admin);
        var underAdminPrefix = _endpoints
            .Where(e => e.Route.StartsWith("api/admin/", StringComparison.OrdinalIgnoreCase)
                        || e.Route.StartsWith("api/v1/admin/", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Method + " " + e.Route)
            .ToArray();

        Assert.IsTrue(underAdminPrefix.Length > 0, "No api/admin/* endpoints were discovered — the host wiring is wrong.");

        // Bidirectional: no admin route outside the controller, and no controller
        // route outside api/admin/. A future route move breaks this rather than
        // silently widening or narrowing the policy.
        CollectionAssert.AreEquivalent(
            underAdminPrefix,
            onPolicy,
            "The admin policy must cover exactly the api/admin/* routes.");
    }

    [TestMethod]
    public void No_other_endpoint_carries_any_policy()
    {
        var total = _endpoints.Count(e => e.Policy is not null);
        var expected = Routes(RateLimitPolicyNames.AgentReceive).Length
                       + Routes(RateLimitPolicyNames.Admin).Length
                       + Routes(RateLimitPolicyNames.Search).Length
                       + Routes(RateLimitPolicyNames.Login).Length;

        Assert.AreEqual(expected, total, "Some endpoint carries a rate-limiting policy that is not one of the four sets.");
    }

    [TestMethod]
    public void Surfaces_that_must_not_be_throttled_carry_no_policy()
    {
        // AC-10 plus the named negatives. Set equality above implies these, but
        // spelling them out is what a reviewer reads.
        string[] mustBeFree =
        [
            "GET api/agent/publish", "POST api/agent/publish",
            "POST api/agent/settle",
            "GET api/agent/catalog",
            "GET account/login",
            "GET account/register", "POST account/register",
            "GET account/forgot-password", "POST account/forgot-password",
            "GET account/reset-password", "POST account/reset-password",
            "GET api/auth/me",
            "POST api/auth/logout",
            "GET api/me",
            "GET " + AppEndpoints.GridEventHub.TrimStart('/'),
            "GET /health", "GET /alive", "GET /ready",
        ];

        foreach (var candidate in mustBeFree)
        {
            var matches = _endpoints
                .Where(e => string.Equals(e.Method + " " + e.Route, candidate, StringComparison.OrdinalIgnoreCase))
                .ToArray();

            foreach (var endpoint in matches)
            {
                Assert.IsNull(endpoint.Policy, $"{candidate} must not be rate limited (AC-10), but carries '{endpoint.Policy}'.");
            }
        }

        // The hub, the health probes and the SPA fallback specifically.
        foreach (var endpoint in _endpoints.Where(e =>
                     e.Route.Contains("hubs/gridevents", StringComparison.OrdinalIgnoreCase)
                     || e.Route is "health" or "alive" or "ready"
                     || e.Route.Length == 0))
        {
            Assert.IsNull(endpoint.Policy, $"'{endpoint.Route}' must stay unthrottled — a real-time client must not consume receive permits.");
        }
    }

    [TestMethod]
    public async Task Kill_switch_attaches_no_metadata_to_any_endpoint()
    {
        using var host = await BuildHostAsync(new Dictionary<string, string?>
        {
            ["RateLimiting:Enabled"] = "false",
        });

        var withPolicy = Describe(host).Where(e => e.Policy is not null).ToArray();

        Assert.AreEqual(
            0,
            withPolicy.Length,
            "With RateLimiting:Enabled = false no endpoint may carry rate-limiting metadata; the policies themselves stay registered.");

        await host.StopAsync();
    }

    private static string[] Routes(string policy)
        => _endpoints.Where(e => e.Policy == policy).Select(e => e.Method + " " + e.Route).ToArray();

    private static IReadOnlyList<EndpointFacts> Describe(IHost host)
    {
        var source = host.Services.GetRequiredService<EndpointDataSource>();
        var facts = new List<EndpointFacts>();

        foreach (var endpoint in source.Endpoints)
        {
            var policy = endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName;
            var route = endpoint is RouteEndpoint routeEndpoint ? routeEndpoint.RoutePattern.RawText ?? string.Empty : string.Empty;
            var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;

            if (methods is null || methods.Count == 0)
            {
                facts.Add(new EndpointFacts("ANY", route, policy));
                continue;
            }

            facts.AddRange(methods.Select(method => new EndpointFacts(method, route, policy)));
        }

        return facts;
    }

    private static async Task<IHost> BuildHostAsync(Dictionary<string, string?> settings)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        return await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                    services.AddRouting();
                    services.AddSignalR();      // required by MapHub<GridEventsHub>
                    services.AddHealthChecks(); // required by MapHealthChecks; bare, no checks registered
                    services.AddAuthorization();

                    // The only rate-limiting call — building through the
                    // production entry point puts the IConfigureOptions<MvcOptions>
                    // hop under test, so a broken registration is RED here rather
                    // than shipping an unattached convention.
                    services.AddNimBusRateLimiting(configuration);
                    NimBus.Extensions.IntegrationIntelligence.IntegrationIntelligenceRegistration.AddNimBusIntegrationIntelligence(services, configuration);

                    // MVC resolves application parts from the entry assembly,
                    // which under `dotnet test` is the test host — so both parts
                    // must be added explicitly. Production is unaffected.
                    services.AddNimBusApiVersioning();
                    services.AddControllers()
                        .AddApplicationPart(typeof(ApplicationApiController).Assembly)
                        .AddApplicationPart(typeof(AccountController).Assembly);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapHealthChecks("/health");
                        endpoints.MapHealthChecks("/alive");
                        endpoints.MapHealthChecks("/ready");
                        endpoints.MapHub<GridEventsHub>(AppEndpoints.GridEventHub);
                        endpoints.MapControllers();
                    });
                });
            })
            .StartAsync();
    }
}
