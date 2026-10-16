#pragma warning disable CA1707, CA2007

using System.Net;
using System.Net.Http.Json;
using CrmErpDemo.Contracts.Security;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Extensions.Identity;

namespace CrmErpDemo.AppHost.Tests;

[TestClass]
public sealed class DemoSignInTests
{
    private const string ServiceKey = "0123456789abcdef0123456789abcdef";

    [TestMethod]
    public void Sign_in_is_enabled_only_by_an_identity_connection_string()
    {
        Assert.IsFalse(DemoSignIn.IsEnabled(Config(new())));
        Assert.IsFalse(DemoSignIn.IsEnabled(Config(new() { ["NimBusIdentity:ConnectionString"] = " " })));
        Assert.IsTrue(DemoSignIn.IsEnabled(Config(new() { ["NimBusIdentity:ConnectionString"] = "Server=sql;Database=nimbus" })));
    }

    [TestMethod]
    public async Task Anonymous_api_call_gets_401()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/api/accounts");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Anonymous_web_ui_redirects_to_sign_in()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/accounts/42");

        Assert.AreEqual(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        StringAssert.StartsWith(location, "http://localhost/account/login?ReturnUrl=");
        StringAssert.Contains(location, Uri.EscapeDataString("/accounts/42"));
    }

    [TestMethod]
    public async Task Sign_in_pages_auth_endpoints_and_health_stay_anonymous()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();

        using var login = await client.GetAsync("/account/login");
        Assert.AreEqual(HttpStatusCode.OK, login.StatusCode);
        using var me = await client.GetAsync("/api/auth/me");
        Assert.AreEqual(HttpStatusCode.OK, me.StatusCode);
        var user = await me.Content.ReadFromJsonAsync<CurrentUser>();
        Assert.IsFalse(user!.IsAuthenticated);
        using var health = await client.GetAsync("/health");
        Assert.AreEqual(HttpStatusCode.OK, health.StatusCode);
    }

    [TestMethod]
    public async Task Service_key_reaches_the_api()
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(DemoSignIn.ServiceKeyHeader, ServiceKey);

        using var response = await client.GetAsync("/api/accounts");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.AreEqual("demo-service", await response.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [DataRow("wrong-key")]
    [DataRow("")]
    public async Task Wrong_service_key_gets_401(string key)
    {
        await using var app = await StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.TryAddWithoutValidation(DemoSignIn.ServiceKeyHeader, key);

        using var response = await client.GetAsync("/api/accounts");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Service_key_is_refused_when_the_api_has_none()
    {
        await using var app = await StartAsync(serviceKey: null);
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add(DemoSignIn.ServiceKeyHeader, ServiceKey);

        using var response = await client.GetAsync("/api/accounts");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Adapter_sends_the_key_only_to_its_api()
    {
        var services = new ServiceCollection();
        services.AddDemoServiceKey(Config(new() { [DemoSignIn.ServiceKeyConfigKey] = ServiceKey }), "https://crm-api.example.test");
        var recorder = new RecordingHandler();
        services.AddHttpClient("test").ConfigurePrimaryHttpMessageHandler(() => recorder);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");

        using var _ = await client.GetAsync("https://crm-api.example.test/api/accounts");
        Assert.AreEqual(ServiceKey, recorder.LastKey);
        using var __ = await client.GetAsync("https://other.example.test/api/accounts");
        Assert.IsNull(recorder.LastKey);
        using var ___ = await client.GetAsync("http://crm-api.example.test/api/accounts");
        Assert.IsNull(recorder.LastKey, "A different scheme is a different origin.");
    }

    [TestMethod]
    public async Task Adapter_sends_no_key_when_none_is_configured()
    {
        var services = new ServiceCollection();
        services.AddDemoServiceKey(Config(new()), "https://crm-api.example.test");
        var recorder = new RecordingHandler();
        services.AddHttpClient("test").ConfigurePrimaryHttpMessageHandler(() => recorder);
        using var provider = services.BuildServiceProvider();
        using var client = provider.GetRequiredService<IHttpClientFactory>().CreateClient("test");

        using var _ = await client.GetAsync("https://crm-api.example.test/api/accounts");

        Assert.IsNull(recorder.LastKey);
    }

    // Mirrors how Crm.Api and Erp.Api compose the sign-in: Identity plus AddDemoSignIn, the API and
    // the web UI fallback in a protected group, health outside it. The Identity database is never
    // reached: anonymous and service-key requests don't touch the user store.
    private static async Task<WebApplication> StartAsync(string? serviceKey = ServiceKey)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            [DemoSignIn.ServiceKeyConfigKey] = serviceKey,
        });
        builder.Services.AddNimBusIdentity("Server=127.0.0.1,1;Database=nimbus;Connect Timeout=1");
        // The initializer would create the Identity schema; there is no database here.
        var initializer = builder.Services.Single(d => d.ImplementationType?.Name == "IdentityInitializerHostedService");
        builder.Services.Remove(initializer);
        builder.Services.AddDemoSignIn(builder.Configuration);
        // The Web SDK registers referenced controller assemblies in the APIs; this test project
        // isn't a web project, so it names the Identity controllers and views itself.
        builder.Services.AddMvcCore().AddApplicationPart(typeof(INimBusIdentityMarker).Assembly);
        builder.Services.AddHealthChecks();

        var app = builder.Build();
        app.UseDemoSignIn();
        app.MapHealthChecks("/health");
        var api = app.MapGroup("").RequireDemoSignIn();
        api.MapGet("/api/accounts", (HttpContext context) => context.User.Identity!.Name);
        api.MapFallback(() => "index.html");
        await app.StartAsync();
        return app;
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private sealed record CurrentUser(bool IsAuthenticated);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public string? LastKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastKey = request.Headers.TryGetValues(DemoSignIn.ServiceKeyHeader, out var values) ? values.Single() : null;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
