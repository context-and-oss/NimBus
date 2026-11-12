#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Authorization;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.AgentApiKeys;
using NimBus.WebApp.Services.DashboardTokens;
using NimBus.WebApp.Services.IntegratorApiKeys;

namespace NimBus.WebApp.Tests.IntegratorApiKeys;

/// <summary>
/// Spec 045 over HTTP: an integrator API key in <c>X-NimBus-Api-Key</c> authenticates on the REST
/// API through the global policy, the role and endpoint scope it was issued are enforced, and the
/// surfaces with their own credential (the agent API, the Monitor feed) never accept it. Mirrors
/// production: a default sign-in scheme, the global policy, the real authorization service.
/// </summary>
[TestClass]
public class IntegratorApiKeyAuthenticationTests
{
    private const string SignInScheme = "TestSignIn";
    private const string SignInHeader = "X-Test-Sign-In";

    private static IHost _host = null!;
    private static HttpClient _client = null!;
    private static InMemoryMessageStore _store = null!;
    private static readonly Dictionary<string, string> Keys = new();
    private static readonly Dictionary<string, string> KeyIds = new();

    [ClassInitialize]
    public static async Task ClassInit(TestContext _)
    {
        _store = new InMemoryMessageStore();
        var settings = new IntegratorApiKeySettings { Revision = "r1" };
        void Issue(string name, IntegratorApiKeyRole role, List<string>? endpoints = null, bool pii = false, DateTime? revoked = null, DateTime? expires = null)
        {
            var generated = SecretTokenFormat.Generate(IntegratorApiKeyDefaults.Prefix);
            Keys[name] = generated.Value;
            KeyIds[name] = generated.Id;
            settings.Keys.Add(new IntegratorApiKey
            {
                KeyId = generated.Id, Name = name, SecretHash = generated.SecretHash, Role = role, Endpoints = endpoints,
                CanReadPii = pii, CreatedAtUtc = DateTime.UtcNow, RevokedAtUtc = revoked, ExpiresAtUtc = expires,
            });
        }

        Issue("reader", IntegratorApiKeyRole.Reader);
        Issue("billing-contributor", IntegratorApiKeyRole.Contributor, ["Billing"]);
        Issue("contributor", IntegratorApiKeyRole.Contributor, pii: true);
        Issue("revoked", IntegratorApiKeyRole.Contributor, revoked: DateTime.UtcNow);
        Issue("expired", IntegratorApiKeyRole.Contributor, expires: DateTime.UtcNow.AddMinutes(-1));
        Assert.IsTrue(await _store.TrySetIntegratorApiKeySettings(settings, null));

        _host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddLogging(b => b.SetMinimumLevel(LogLevel.Critical));
                    services.AddSingleton<IIntegratorApiKeyStore>(_store);
                    services.AddSingleton<IAgentApiKeyStoreMarker>(new IAgentApiKeyStoreMarker());
                    services.AddHttpContextAccessor();
                    services.AddSingleton<IConfiguration>(new ConfigurationBuilder().Build());
                    services.AddSingleton<IPlatform>(new EmptyPlatform());
                    services.AddSingleton<IAccessControlSnapshotProvider>(new AccessControlSnapshotProvider(new InMemoryMessageStore(), NullLogger<AccessControlSnapshotProvider>.Instance));
                    services.AddScoped<IEndpointAuthorizationService, EndpointAuthorizationService>();
                    services.AddSingleton<IEndpointMetadataStore>(new InMemoryMessageStore());

                    services
                        .AddAuthentication(SignInScheme)
                        .AddScheme<AuthenticationSchemeOptions, HeaderSignInHandler>(SignInScheme, _ => { });
                    services.AddNimBusAgentApiKeys();
                    services.AddNimBusDashboardTokens();
                    services.AddNimBusIntegratorApiKeys();

                    services.AddControllersWithViews(options => options.Filters.Add(new AuthorizeFilter(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build())))
                        .AddApplicationPart(typeof(AgentApiController).Assembly)
                        .AddApplicationPart(typeof(KeyProbeController).Assembly);
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseAuthentication();
                    app.UseNimBusIntegratorApiKeys();
                    app.UseAuthorization();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            })
            .StartAsync();
        _client = _host.GetTestServer().CreateClient();
    }

    [ClassCleanup]
    public static async Task ClassCleanup()
    {
        _client.Dispose();
        await _host.StopAsync();
        _host.Dispose();
    }

    [TestMethod]
    public async Task A_reader_key_reads_and_is_refused_a_change()
    {
        using var read = await SendAsync(HttpMethod.Get, "/api/v1/keyprobe/read/Billing", "reader");
        using var write = await SendAsync(HttpMethod.Post, "/api/v1/keyprobe/write/Billing", "reader");

        Assert.AreEqual(HttpStatusCode.OK, read.StatusCode);
        Assert.AreEqual("integrator:" + KeyIds["reader"], await read.Content.ReadAsStringAsync(), "The caller is named by its key id.");
        Assert.AreEqual(HttpStatusCode.Forbidden, write.StatusCode);
    }

    [TestMethod]
    public async Task An_endpoint_scoped_key_acts_on_its_endpoint_only()
    {
        using var own = await SendAsync(HttpMethod.Post, "/api/v1/keyprobe/write/Billing", "billing-contributor");
        using var other = await SendAsync(HttpMethod.Get, "/api/v1/keyprobe/read/Warehouse", "billing-contributor");

        Assert.AreEqual(HttpStatusCode.OK, own.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, other.StatusCode);
    }

    [TestMethod]
    public async Task No_key_reaches_an_owner_operation()
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/v1/keyprobe/owner", "contributor");

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task The_pii_flag_reaches_the_service()
    {
        using var withPii = await SendAsync(HttpMethod.Get, "/api/v1/keyprobe/pii", "contributor");
        using var without = await SendAsync(HttpMethod.Get, "/api/v1/keyprobe/pii", "reader");

        Assert.AreEqual("true", await withPii.Content.ReadAsStringAsync());
        Assert.AreEqual("false", await without.Content.ReadAsStringAsync());
    }

    [TestMethod]
    [DataRow("revoked")]
    [DataRow("expired")]
    public async Task Revoked_and_expired_keys_are_refused(string name)
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/v1/keyprobe/read/Billing", name);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Unknown_and_malformed_keys_are_refused()
    {
        foreach (var key in new[] { SecretTokenFormat.Generate(IntegratorApiKeyDefaults.Prefix).Value, "nbik_not-a-key", Keys["reader"].Replace("nbik_", "nbak_", StringComparison.Ordinal) })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/keyprobe/read/Billing");
            request.Headers.Add(IntegratorApiKeyDefaults.HeaderName, key);
            using var response = await _client.SendAsync(request);
            Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, key);
        }
    }

    [TestMethod]
    [DataRow("GET", "/api/v1/agent/catalog")]
    [DataRow("POST", "/api/v1/agent/publish")]
    [DataRow("GET", "/api/v1/monitor/feed/status")]
    public async Task The_agent_api_and_the_monitor_feed_never_accept_a_key(string method, string path)
    {
        using var response = await SendAsync(new HttpMethod(method), path, "contributor");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, $"{method} {path}");
    }

    [TestMethod]
    public async Task A_key_counts_only_under_api()
    {
        using var response = await SendAsync(HttpMethod.Get, "/outside/keyprobe/whoami", "reader");

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, "The hub, /mcp and the SPA never see a key.");
    }

    [TestMethod]
    public async Task A_refused_key_is_refused_even_with_a_sign_in()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/keyprobe/whoami");
        request.Headers.Add(SignInHeader, "1");
        request.Headers.Add(IntegratorApiKeyDefaults.HeaderName, Keys["revoked"]);
        using var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode, "A caller that sends a key means the key.");
    }

    [TestMethod]
    public void The_production_pipeline_adds_keys_between_authentication_and_authorization()
    {
        var dir = System.IO.Path.GetDirectoryName(typeof(IntegratorApiKeyAuthenticationTests).Assembly.Location);
        string? file = null;
        while (!string.IsNullOrEmpty(dir) && file is null)
        {
            var candidate = System.IO.Path.Combine(dir, "src", "NimBus.WebApp", "Startup.Pipeline.cs");
            if (System.IO.File.Exists(candidate)) file = candidate;
            dir = System.IO.Path.GetDirectoryName(dir);
        }

        var source = System.IO.File.ReadAllText(file!);
        var authentication = source.IndexOf("app.UseAuthentication()", StringComparison.Ordinal);
        var keys = source.IndexOf("app.UseNimBusIntegratorApiKeys()", StringComparison.Ordinal);
        var authorization = source.IndexOf("app.UseAuthorization()", StringComparison.Ordinal);
        Assert.IsTrue(authentication > 0 && authentication < keys && keys < authorization);
    }

    [TestMethod]
    public async Task A_signed_in_person_still_gets_in()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/keyprobe/whoami");
        request.Headers.Add(SignInHeader, "1");
        using var response = await _client.SendAsync(request);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task A_successful_call_records_the_key_use()
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/v1/keyprobe/whoami", "reader");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsTrue((await _store.GetIntegratorApiKeyUsage()).Any(u => u.KeyId == KeyIds["reader"]));
    }

    private static Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, string keyName)
    {
        var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get && method != HttpMethod.Delete)
            request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        request.Headers.Add(IntegratorApiKeyDefaults.HeaderName, Keys[keyName]);
        return _client.SendAsync(request);
    }

    private sealed class IAgentApiKeyStoreMarker;

    private sealed class EmptyPlatform : IPlatform
    {
        public IEnumerable<IEndpoint> Endpoints => Enumerable.Empty<IEndpoint>();

        public IEnumerable<IEventType> EventTypes => Enumerable.Empty<IEventType>();

        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => Enumerable.Empty<IEndpoint>();

        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => Enumerable.Empty<IEndpoint>();
    }

    private sealed class HeaderSignInHandler(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            if (!Request.Headers.ContainsKey(SignInHeader))
                return Task.FromResult(AuthenticateResult.NoResult());

            var identity = new ClaimsIdentity(
                new[] { new Claim(ClaimTypes.NameIdentifier, "test-user"), new Claim(ClaimTypes.Name, "Test User") },
                Scheme.Name);
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name)));
        }
    }
}

/// <summary>Routes that ask the authorization service what an integrator key may do.</summary>
public sealed class KeyProbeController(IEndpointAuthorizationService authorization) : Controller
{
    [HttpGet("outside/keyprobe/whoami")]
    public IActionResult OutsideApi() => Content(authorization.GetCurrentUserName() ?? string.Empty);

    [HttpGet("api/v1/keyprobe/whoami")]
    public IActionResult WhoAmI() => Content(authorization.GetCurrentUserName() ?? string.Empty);

    [HttpGet("api/v1/keyprobe/read/{endpointId}")]
    public async Task<IActionResult> Read(string endpointId)
        => await authorization.HasRoleAsync(AccessRole.Reader, endpointId) ? Content(authorization.GetCurrentUserName() ?? string.Empty) : new ForbidResult();

    [HttpPost("api/v1/keyprobe/write/{endpointId}")]
    public async Task<IActionResult> Write(string endpointId)
        => await authorization.HasRoleFreshAsync(AccessRole.Contributor, endpointId) ? Ok() : new ForbidResult();

    [HttpGet("api/v1/keyprobe/owner")]
    public async Task<IActionResult> OwnerOnly()
        => await authorization.HasRoleAsync(AccessRole.Owner) ? Ok() : new ForbidResult();

    [HttpGet("api/v1/keyprobe/pii")]
    public async Task<IActionResult> Pii() => Content(await authorization.CanReadPiiAsync() ? "true" : "false");
}
