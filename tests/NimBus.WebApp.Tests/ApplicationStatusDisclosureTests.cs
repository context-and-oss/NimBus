#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// GH#93: /api/app/stats stays anonymous for liveness probes, but its payload is
/// deployment detail (exact version, backend topology, environment name) that lets
/// an unauthenticated scanner fingerprint the install. These tests pin both shapes:
/// anonymous callers get an empty status object, authenticated callers get the full
/// one. Unlike <see cref="AnonymousEndpointsTests"/> (which stubs the controller to
/// test routing/authorization only), this harness registers the real
/// <see cref="ApplicationImplementation"/> so the response body is the real one.
/// </summary>
[TestClass]
public class ApplicationStatusDisclosureTests
{
    private const string TestAuthScheme = "TestAuthenticated";
    private const string TestAuthHeader = "X-Test-Auth";

    private const string TestEnvironment = "ProdCanary";
    private const string TestStorageProvider = "Cosmos DB";
    private const string TestTicketLinkTemplate = "https://tickets.example.com/{ticket}";

    private static IHost _host = null!;
    private static TestServer _server = null!;

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

                    var config = new ConfigurationBuilder()
                        .AddInMemoryCollection(new Dictionary<string, string?>
                        {
                            ["Environment"] = TestEnvironment,
                            ["TicketLinkTemplate"] = TestTicketLinkTemplate,
                        })
                        .Build();
                    services.AddSingleton<IConfiguration>(config);

                    services.AddHttpContextAccessor();
                    services.AddSingleton<IStorageProviderRegistration>(new StubStorageProvider());
                    services.AddSingleton<IPlatform>(new PlatformConfiguration());
                    services.AddSingleton<IEndpointAuthorizationService>(new StubAuthorizationService());
                    services.AddTransient<IApplicationApiController, ApplicationImplementation>();

                    services
                        .AddAuthentication(TestAuthScheme)
                        .AddScheme<AuthenticationSchemeOptions, HeaderAuthHandler>(TestAuthScheme, _ => { });
                    services.AddAuthorization();

                    services.AddControllers(options =>
                    {
                        var policy = new AuthorizationPolicyBuilder()
                            .RequireAuthenticatedUser()
                            .Build();
                        options.Filters.Add(new AuthorizeFilter(policy));
                        options.Conventions.Add(new AllowAnonymousActionsConvention());
                    })
                    .AddApplicationPart(typeof(ApplicationApiController).Assembly);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            });

        _host = await builder.StartAsync();
        _server = _host.GetTestServer();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        await _host.StopAsync();
        _host.Dispose();
    }

    [TestMethod]
    public async Task Anonymous_stats_omits_deployment_detail()
    {
        using var client = _server.CreateClient();
        using var response = await client.GetAsync("/api/app/stats");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body)!.AsObject();

        foreach (var field in new[] { "env", "nimbusVersion", "platformName", "platformVersion", "storageProvider", "ticketLinkTemplate" })
        {
            Assert.IsTrue(
                !json.TryGetPropertyValue(field, out var value) || value is null,
                $"Anonymous /api/app/stats must not populate '{field}', but the body was: {body}");
        }

        // Substring guard: survives a renamed or newly added field that would
        // otherwise smuggle the same detail out under a different key.
        foreach (var secret in new[] { TestEnvironment, TestStorageProvider, "tickets.example.com" })
        {
            StringAssert.DoesNotMatch(
                body,
                new System.Text.RegularExpressions.Regex(System.Text.RegularExpressions.Regex.Escape(secret)),
                $"Anonymous /api/app/stats leaked '{secret}': {body}");
        }
    }

    [TestMethod]
    public async Task Authenticated_stats_returns_full_detail()
    {
        using var client = _server.CreateClient();
        client.DefaultRequestHeaders.Add(TestAuthHeader, "yes");
        using var response = await client.GetAsync("/api/app/stats");
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        var json = JsonNode.Parse(body)!.AsObject();

        Assert.AreEqual(TestEnvironment, (string?)json["env"]);
        Assert.AreEqual(TestStorageProvider, (string?)json["storageProvider"]);
        Assert.AreEqual(TestTicketLinkTemplate, (string?)json["ticketLinkTemplate"]);
        Assert.IsFalse(
            string.IsNullOrWhiteSpace((string?)json["platformVersion"]),
            "Authenticated /api/app/stats must still report the platform version.");
        Assert.IsFalse(
            string.IsNullOrWhiteSpace((string?)json["nimbusVersion"]),
            "Authenticated /api/app/stats must report the NimBus version.");
        // The bundled sample catalog lives in the NimBus assembly; a customer
        // package (e.g. EET.Platform) reports its own assembly name here.
        Assert.AreEqual(typeof(PlatformConfiguration).Assembly.GetName().Name, (string?)json["platformName"]);
        Assert.IsFalse(
            ((string?)json["platformVersion"] ?? string.Empty).Contains('+', StringComparison.Ordinal),
            "platformVersion must not carry the '+<sha>' build suffix.");
    }

    [TestMethod]
    public async Task Anonymous_stats_body_deserializes_as_ApplicationStatus()
    {
        using var client = _server.CreateClient();
        using var response = await client.GetAsync("/api/app/stats");
        var body = await response.Content.ReadAsStringAsync();

        // The generated DTO must tolerate the trimmed body: with the contract's
        // pre-GH#93 Required.DisallowNull on env/platformVersion/storageProvider,
        // Newtonsoft throws on the explicit nulls System.Text.Json emits here.
        var status = ApplicationStatus.FromJson(body);

        Assert.IsNull(status.Env);
        Assert.IsNull(status.NimbusVersion);
        Assert.IsNull(status.PlatformName);
        Assert.IsNull(status.PlatformVersion);
        Assert.IsNull(status.StorageProvider);
        Assert.IsNull(status.TicketLinkTemplate);
    }

    private sealed class StubStorageProvider : IStorageProviderRegistration
    {
        public string ProviderName => TestStorageProvider;
    }

    private sealed class StubAuthorizationService : IEndpointAuthorizationService
    {
        // The /api/app/stats path calls none of these.
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null)
            => throw new NotSupportedException();

        public Task<bool> CanReadPiiAsync() => throw new NotSupportedException();

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => throw new NotSupportedException();

        public string? GetCurrentUserName() => throw new NotSupportedException();
    }

    private sealed class HeaderAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public HeaderAuthHandler(
            IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger,
            UrlEncoder encoder) : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(TestAuthHeader))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(new[]
            {
                new Claim(ClaimTypes.NameIdentifier, "test-user"),
                new Claim(ClaimTypes.Name, "Test User"),
            }, Scheme.Name);
            var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(ticket));
        }
    }
}
