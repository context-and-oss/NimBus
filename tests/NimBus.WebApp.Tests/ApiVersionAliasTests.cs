#pragma warning disable CA1707, CA2007
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Extensions.Identity.Controllers;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.RateLimiting;
using NimBus.WebApp.Versioning;

namespace NimBus.WebApp.Tests;

/// <summary>
/// Every API route is served at <c>/api/v1/…</c> and, deprecated, at the unversioned
/// <c>/api/…</c> it had before 1.0.0. Only the unversioned form announces its successor, and
/// both forms carry the same rate-limiting policy.
/// </summary>
[TestClass]
public class ApiVersionAliasTests
{
    private static IHost _host = null!;
    private static List<RouteEndpoint> _endpoints = null!;

    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        _host = await BuildHostAsync();
        _endpoints = _host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [TestMethod]
    public void Every_generated_controller_declares_the_alias_convention()
    {
        var generated = typeof(ApplicationApiController).Assembly.GetTypes()
            .Where(t => t.Namespace == typeof(ApplicationApiController).Namespace
                        && typeof(Controller).IsAssignableFrom(t)
                        && t.GetCustomAttribute<GeneratedCodeAttribute>() is not null)
            .ToList();

        Assert.IsTrue(generated.Count >= 19, "The generated controllers were not found.");
        var missing = generated.Where(t => t.GetCustomAttribute<ApiVersionAliasesAttribute>() is null).Select(t => t.Name).ToList();
        Assert.AreEqual(0, missing.Count, "Add these to Controllers/ApiVersionAliases.cs: " + string.Join(", ", missing));
    }

    [TestMethod]
    public void Every_versioned_route_has_a_deprecated_unversioned_twin_and_back()
    {
        var api = _endpoints.Where(e => Route(e).StartsWith("api/", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.IsTrue(api.Count > 200, "The API endpoints were not discovered.");

        foreach (var endpoint in api)
        {
            var route = Route(endpoint);
            var versioned = route.StartsWith("api/v1/", StringComparison.OrdinalIgnoreCase);
            var twin = versioned ? "api/" + route["api/v1/".Length..] : "api/v1/" + route["api/".Length..];
            var counterpart = api.SingleOrDefault(e => string.Equals(Route(e), twin, StringComparison.OrdinalIgnoreCase)
                                                       && SameMethods(e, endpoint));

            Assert.IsNotNull(counterpart, $"{Methods(endpoint)} {route} has no {twin}.");
            Assert.AreEqual(!versioned, IsLegacy(endpoint), $"{route}: only the unversioned route is deprecated.");
        }
    }

    [TestMethod]
    public void Hand_written_api_controllers_gain_a_versioned_route()
    {
        foreach (var route in new[] { "api/v1/auth/me", "api/v1/admin/failure-intelligence" })
            Assert.IsTrue(_endpoints.Any(e => Route(e) == route && !IsLegacy(e)), $"{route} is missing.");

        foreach (var route in new[] { "api/auth/me", "api/admin/failure-intelligence" })
            Assert.IsTrue(_endpoints.Any(e => Route(e) == route && IsLegacy(e)), $"{route} must stay, deprecated.");
    }

    [TestMethod]
    public void Both_forms_carry_the_same_rate_limiting_policy()
    {
        foreach (var (v1, legacy) in new[]
                 {
                     ("api/v1/agent/receive", "api/agent/receive"),
                     ("api/v1/messages/search", "api/messages/search"),
                     ("api/v1/admin/endpoint/{endpointId}/purge", "api/admin/endpoint/{endpointId}/purge"),
                     ("api/v1/admin/failure-intelligence", "api/admin/failure-intelligence"),
                 })
        {
            var expected = Policy(v1);
            Assert.IsNotNull(expected, $"{v1} should carry a policy.");
            Assert.AreEqual(expected, Policy(legacy), $"{legacy} must carry the same policy as {v1}.");
        }
    }

    [TestMethod]
    public void Non_api_routes_are_left_alone()
    {
        Assert.IsTrue(_endpoints.Any(e => Route(e) == "account/login"));
        Assert.IsFalse(_endpoints.Any(e => Route(e).Contains("v1/account", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task A_legacy_route_announces_its_successor_and_the_versioned_route_does_not()
    {
        using var client = _host.GetTestServer().CreateClient();

        using var legacy = await client.GetAsync("/api/app/stats?probe=1");
        Assert.AreEqual(HttpStatusCode.OK, legacy.StatusCode);
        Assert.AreEqual(LegacyApiRouteHeadersMiddleware.DeprecationValue, legacy.Headers.GetValues("Deprecation").Single());
        Assert.AreEqual("</api/v1/app/stats>; rel=\"successor-version\"", legacy.Headers.GetValues("Link").Single());

        using var current = await client.GetAsync("/api/v1/app/stats");
        Assert.AreEqual(HttpStatusCode.OK, current.StatusCode);
        Assert.IsFalse(current.Headers.Contains("Deprecation"));
        Assert.IsFalse(current.Headers.Contains("Link"));
    }

    [TestMethod]
    public void The_successor_keeps_the_path_base_and_the_rest_of_the_path()
    {
        Assert.AreEqual("/api/v1/event/e-1/m%20x", LegacyApiRouteHeadersMiddleware.SuccessorPath(PathString.Empty, new PathString("/api/event/e-1/m x")));
        Assert.AreEqual("/nimbus/api/v1/me", LegacyApiRouteHeadersMiddleware.SuccessorPath(new PathString("/nimbus"), new PathString("/api/me")));
    }

    [TestMethod]
    public void The_production_pipeline_registers_versioning()
    {
        var web = File.ReadAllText(LocateWebAppFile("Startup.Web.cs"));
        Assert.IsTrue(web.Contains("services.AddNimBusApiVersioning()", StringComparison.Ordinal));

        var pipeline = File.ReadAllText(LocateWebAppFile("Startup.Pipeline.cs"));
        var routing = pipeline.IndexOf("app.UseRouting()", StringComparison.Ordinal);
        var headers = pipeline.IndexOf("app.UseNimBusLegacyApiRouteHeaders()", StringComparison.Ordinal);
        var endpoints = pipeline.IndexOf("app.UseEndpoints(", StringComparison.Ordinal);
        Assert.IsTrue(routing > 0 && routing < headers && headers < endpoints, "The deprecation headers need the routed endpoint.");
    }

    private static string Route(RouteEndpoint endpoint) => endpoint.RoutePattern.RawText ?? string.Empty;

    private static bool IsLegacy(Endpoint endpoint) => endpoint.Metadata.GetMetadata<LegacyApiRouteMetadata>() is not null;

    private static string Methods(Endpoint endpoint)
        => string.Join(",", endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["ANY"]);

    private static bool SameMethods(Endpoint a, Endpoint b) => Methods(a) == Methods(b);

    private static string? Policy(string route)
    {
        var policies = _endpoints.Where(e => Route(e) == route)
            .Select(e => e.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName)
            .Where(p => p is not null)
            .Distinct()
            .ToList();
        return policies.SingleOrDefault();
    }

    private static string LocateWebAppFile(string relativePath)
    {
        var dir = Path.GetDirectoryName(typeof(ApiVersionAliasTests).Assembly.Location);
        while (!string.IsNullOrEmpty(dir))
        {
            var candidate = Path.Combine(dir, "src", "NimBus.WebApp", relativePath);
            if (File.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }

        throw new FileNotFoundException(relativePath);
    }

    private static async Task<IHost> BuildHostAsync()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();

        return await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                    services.AddRouting();
                    services.AddAuthorization();
                    services.AddSingleton<IApplicationApiController>(new StubApplicationApi());
                    services.AddNimBusRateLimiting(configuration);
                    NimBus.Extensions.IntegrationIntelligence.IntegrationIntelligenceRegistration.AddNimBusIntegrationIntelligence(services, configuration);
                    services.AddNimBusApiVersioning();
                    services.AddControllers()
                        .AddApplicationPart(typeof(ApplicationApiController).Assembly)
                        .AddApplicationPart(typeof(AccountController).Assembly);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseNimBusLegacyApiRouteHeaders();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .StartAsync();
    }

    private sealed class StubApplicationApi : IApplicationApiController
    {
        public Task<ActionResult<ApplicationStatus>> GetApiAppStatsAsync()
            => Task.FromResult<ActionResult<ApplicationStatus>>(new OkObjectResult(new ApplicationStatus()));

        public Task<ActionResult<UserInfo>> GetMeAsync() => throw new NotSupportedException();
    }
}
