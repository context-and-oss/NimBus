#pragma warning disable CA1707, CA2007, CS0618 // CS0618: these tests cover the deprecated analyze route (Spec 044 §6.10).
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.AspNetCore.Http;
using System.Security.Claims;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.Extensions.IntegrationIntelligence.Controllers;
using NimBus.WebApp.RateLimiting;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.IntegrationIntelligence;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class IntelligenceActivationTests
{
    [TestMethod]
    public async Task Saved_Enable_Is_Applied_Before_Controller_Discovery_And_Payload_Construction()
    {
        var saved = new IntelligenceAdminSettings { Enabled = true, IncludeEventPayload = true };
        var overrides = saved.ApplyTo(new ConfigurationBuilder().Build());
        using var host = await CreateHost("local", false, "20", overrides: overrides);
        Assert.IsTrue(host.Services.GetRequiredService<IntegrationIntelligenceActivation>().Ready);
        Assert.IsTrue(host.Services.GetRequiredService<FailureClassificationOptions>().IncludeEventPayload);
        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText).ToList();
        Assert.IsTrue(routes.Any(route => route!.Contains("/classification", StringComparison.Ordinal)));
        CollectionAssert.Contains(routes, "api/admin/failure-intelligence");
    }

    [TestMethod]
    [DataRow("local")]
    [DataRow("identity")]
    [DataRow("dual")]
    [DataRow("entra")]
    public async Task Disabled_Routes_Are_404_Under_Every_Startup_Authentication_Branch(string branch)
    {
        using var host = await CreateHost(branch, false, "not-an-integer");
        using var client = host.GetTestClient();
        foreach (var route in new[] { "/api/integration-intelligence/status?endpointId=endpoint", "/api/integration-intelligence/failures/event/message/classification" })
            Assert.AreEqual(HttpStatusCode.NotFound, (await client.GetAsync(route)).StatusCode);
        Assert.IsNull(host.Services.GetService<IFailureClassificationStore>());
    }

    [TestMethod]
    public async Task Unbindable_Configuration_Fails_Closed_To_The_Status_Route_Only()
    {
        using var host = await CreateHost("local", true, "not-an-integer");
        var routes = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().Select(e => e.RoutePattern.RawText).ToList();
        CollectionAssert.Contains(routes, "api/integration-intelligence/status");
        Assert.IsFalse(routes.Any(r => r!.Contains("/classification", StringComparison.Ordinal)));
        CollectionAssert.Contains(routes, "api/app/stats");
        var activation = host.Services.GetRequiredService<IntegrationIntelligenceActivation>();
        Assert.IsFalse(activation.Ready);
        Assert.IsFalse(activation.Readable);
        Assert.IsNull(host.Services.GetService<IFailureClassificationStore>());
    }

    [TestMethod]
    [DataRow("999", true)]
    [DataRow("20", false)]
    public async Task Invalid_Provider_Settings_Keep_Saved_Classifications_Readable_And_Prune_Only_Analysis(string timeout, bool withApiKey)
    {
        // Spec 043 §9 read-only mode: an out-of-range timeout or a missing API key blocks analysis,
        // not reading what was already saved.
        using var host = await CreateHost("local", true, timeout, apiKey: withApiKey ? "test-only" : null);

        var activation = host.Services.GetRequiredService<IntegrationIntelligenceActivation>();
        Assert.IsTrue(activation.Readable);
        Assert.IsFalse(activation.Ready);
        var actions = host.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .Select(e => e.Metadata.GetMetadata<ControllerActionDescriptor>())
            .Where(d => d is not null && d.ControllerTypeInfo.Namespace == typeof(IntegrationIntelligenceController).Namespace)
            .Select(d => d!.ActionName)
            .ToList();
        CollectionAssert.IsSubsetOf(new[] { "GetStatus", "GetClassification", "GetHistory" }, actions);
        CollectionAssert.DoesNotContain(actions, nameof(IntegrationIntelligenceController.PostClassification));
        using var scope = host.Services.CreateScope();
        Assert.IsNotNull(scope.ServiceProvider.GetService<FailureClassificationReader>());
        Assert.IsNull(scope.ServiceProvider.GetService<FailureClassificationService>());
    }

    [TestMethod]
    public async Task Read_Only_Mode_Registers_Storage_Schema_And_Retention()
    {
        var hosted = new List<Type>();
        using var host = await CreateHost("local", true, "20", descriptors =>
            hosted.AddRange(descriptors.Where(d => d.ServiceType == typeof(IHostedService)).Select(d => d.ImplementationType ?? typeof(object))),
            apiKey: null);

        Assert.IsNotNull(host.Services.GetService<IFailureClassificationStore>());
        CollectionAssert.Contains(hosted.Select(t => t.Name).ToList(), "ClassificationRetentionWorker");
        CollectionAssert.Contains(hosted.Select(t => t.Name).ToList(), "SqlClassificationSchemaInitializer");
    }

    [TestMethod]
    public async Task Request_Boundary_And_Consumers_Are_Scoped_In_The_Real_Startup()
    {
        using var host = await CreateHost("local", true, "20", descriptors =>
        {
            foreach (var type in new[] { typeof(IIntegrationIntelligenceHost), typeof(IntegrationIntelligenceStatusService), typeof(FailureClassificationService) })
                Assert.AreEqual(ServiceLifetime.Scoped, descriptors.Last(d => d.ServiceType == type).Lifetime, type.Name);
        }, configure: services => services.AddSingleton<IAccessControlSnapshotProvider, EmptySnapshot>());
        using var scope1 = host.Services.CreateScope();
        using var scope2 = host.Services.CreateScope();
        var first = scope1.ServiceProvider.GetRequiredService<IIntegrationIntelligenceHost>();
        var second = scope2.ServiceProvider.GetRequiredService<IIntegrationIntelligenceHost>();
        Assert.AreNotSame(first, second);
        var accessor = host.Services.GetRequiredService<IHttpContextAccessor>();
        accessor.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("groups", "EIP_Management")], "test")) };
        Assert.IsTrue(await first.HasContributorAsync("endpoint"));
        accessor.HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "unprivileged")], "test")) };
        Assert.IsFalse(await second.HasContributorAsync("endpoint"), "The preceding administrator request must not grant this user access.");
        accessor.HttpContext = null;
        scope1.ServiceProvider.GetRequiredService<FailureClassificationService>();
        scope2.ServiceProvider.GetRequiredService<IntegrationIntelligenceStatusService>();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task Enabled_Analyze_Policy_Respects_Global_Kill_Switch(bool rateLimit)
    {
        using var host = await CreateHost("local", true, "20", rateLimit: rateLimit);
        var endpoint = host.Services.GetRequiredService<EndpointDataSource>().Endpoints.Single(e =>
            e.Metadata.GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>()?.ActionName == "PostClassification");
        var policy = endpoint.Metadata.GetMetadata<Microsoft.AspNetCore.RateLimiting.EnableRateLimitingAttribute>()?.PolicyName;
        Assert.AreEqual(rateLimit ? RateLimitPolicyNames.Intelligence : null, policy);
    }

    private static Task<IHost> CreateHost(string branch, bool enabled, string timeout, Action<IServiceCollection>? inspect = null, bool rateLimit = true, Action<IServiceCollection>? configure = null, IConfiguration? overrides = null, string? apiKey = "test-only")
    {
        var settings = new Dictionary<string, string?>
        {
            ["NimBus:StorageProvider"] = "sqlserver", ["SqlConnection"] = "Server=localhost;Database=unused;Integrated Security=true;TrustServerCertificate=true",
            ["ServiceBusNamespace"] = "unit-test", ["EnableLocalDevAuthentication"] = (branch == "local").ToString(),
            ["NimBus:IntegrationIntelligence:Enabled"] = enabled.ToString(),
            ["NimBus:IntegrationIntelligence:FailureClassification:TypeSafe:TimeoutSeconds"] = timeout,
            ["RateLimiting:Enabled"] = rateLimit.ToString(),
        };
        // A chained configuration treats an empty value as missing, so "no key" means omitting it.
        if (apiKey is not null) settings["NimBus:IntegrationIntelligence:FailureClassification:TypeSafe:ApiKey"] = apiKey;
        if (branch is "identity" or "dual") settings["NimBusIdentity:ConnectionString"] = "Server=localhost;Database=unused;Integrated Security=true";
        if (branch is "dual" or "entra")
        {
            settings["AzureAd:ClientId"] = "00000000-0000-0000-0000-000000000001";
            settings["AzureAd:TenantId"] = "00000000-0000-0000-0000-000000000002";
            settings["AzureAd:Instance"] = "https://login.microsoftonline.com/";
        }
        return new HostBuilder().UseEnvironment("Development")
            .UseDefaultServiceProvider(options => options.ValidateScopes = true)
            .ConfigureAppConfiguration(builder => { builder.AddInMemoryCollection(settings); if (overrides is not null) builder.AddConfiguration(overrides); })
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices((context, services) =>
                {
                    new Startup(context.Configuration, context.HostingEnvironment).ConfigureServices(services);
                    inspect?.Invoke(services);
                    configure?.Invoke(services);
                    // Exercise real Startup registrations/discovery without background workers reaching external services.
                    services.RemoveAll<IHostedService>();
                    services.AddControllers().AddApplicationPart(typeof(Startup).Assembly)
                        .AddApplicationPart(typeof(IntegrationIntelligenceController).Assembly);
                });
                web.Configure(app => { app.UseRouting(); app.UseAuthorization(); app.UseEndpoints(e => e.MapControllers()); });
            }).StartAsync();
    }

    private sealed class EmptySnapshot : IAccessControlSnapshotProvider
    {
        public Task<AccessControlSnapshot> GetSnapshotAsync() => Task.FromResult(AccessControlSnapshot.Empty);
        public void Invalidate() { }
    }
}
