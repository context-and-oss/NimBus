#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.IntegratorApiKeys;
using NimBus.WebApp.Tests.AgentApiKeys;

namespace NimBus.WebApp.Tests.IntegratorApiKeys;

[TestClass]
public class IntegratorKeysApiTests
{
    private const string TokenRoute = "/test/csrf";
    private const string Route = "/api/v1/admin/integrator-keys";
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task A_non_owner_cannot_list_keys()
    {
        await using var api = await Api.StartAsync(owner: false);

        using var response = await api.Client.GetAsync(Route);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task A_non_owner_cannot_issue_a_key_and_the_attempt_is_audited()
    {
        await using var api = await Api.StartAsync(owner: false);
        var token = await api.TestTokenAsync();

        using var response = await api.SendAsync(HttpMethod.Post, Route, token, new { name = "Billing sync", role = "reader" });

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        var row = api.Audit.Rows.Single();
        Assert.AreEqual(MessageAuditType.IntegratorApiKeyCreated, row.Type);
        Assert.IsTrue(row.Denied);
        Assert.AreEqual(0, (await api.Store.GetIntegratorApiKeySettings()).Keys.Count);
    }

    [TestMethod]
    public async Task Writes_need_the_antiforgery_token()
    {
        await using var api = await Api.StartAsync(owner: true);

        using var response = await api.Client.PostAsJsonAsync(Route, new { name = "Billing sync", role = "reader" });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(0, (await api.Store.GetIntegratorApiKeySettings()).Keys.Count);
    }

    [TestMethod]
    public async Task Issuing_returns_the_key_once_with_its_scope_and_it_authenticates_at_once()
    {
        await using var api = await Api.StartAsync(owner: true);
        var state = await api.GetStateAsync();

        using var response = await api.SendAsync(HttpMethod.Post, Route, state.Token,
            new { name = "Billing sync", role = "contributor", endpoints = new[] { "billing" }, canReadPii = true, expiresAtUtc = Now.AddDays(365) });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var issued = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var token = issued["key"]!.GetValue<string>();
        Assert.IsTrue(SecretTokenFormat.TryParse(IntegratorApiKeyDefaults.Prefix, token, out var tokenId, out _));
        Assert.AreEqual(tokenId, issued["issued"]!["keyId"]!.GetValue<string>());
        Assert.AreEqual("nbik_" + tokenId, issued["issued"]!["prefix"]!.GetValue<string>());
        Assert.AreEqual("Billing sync", issued["issued"]!["name"]!.GetValue<string>());
        Assert.AreEqual("active", issued["issued"]!["status"]!.GetValue<string>());
        Assert.AreEqual("contributor", issued["issued"]!["role"]!.GetValue<string>());
        Assert.AreEqual("Billing", issued["issued"]!["endpoints"]![0]!.GetValue<string>(), "Endpoint ids keep the platform's spelling.");
        Assert.IsTrue(issued["issued"]!["canReadPii"]!.GetValue<bool>());
        Assert.AreEqual(2, issued["state"]!["endpoints"]!.AsArray().Count, "The state offers the platform's endpoints.");

        var stored = (await api.Store.GetIntegratorApiKeySettings()).Keys.Single();
        Assert.AreNotEqual(token, stored.SecretHash);
        var listed = await api.Client.GetStringAsync(Route);
        Assert.IsFalse(listed.Contains(token[(5 + 12 + 1)..], StringComparison.Ordinal), "the secret must never be listed");

        var provided = await api.Services.GetRequiredService<IIntegratorApiKeyProvider>().GetAsync();
        Assert.AreEqual(tokenId, provided!.Keys.Single().KeyId);

        var row = api.Audit.Rows.Single();
        Assert.AreEqual(MessageAuditType.IntegratorApiKeyCreated, row.Type);
        Assert.IsFalse(row.Denied);
        StringAssert.Contains(row.Data, tokenId, StringComparison.Ordinal);
        Assert.IsFalse(row.Data!.Contains(token, StringComparison.Ordinal), "the key must never be audited");
    }

    [TestMethod]
    public async Task A_missing_name_a_long_name_and_a_past_expiry_are_refused()
    {
        await using var api = await Api.StartAsync(owner: true);
        var state = await api.GetStateAsync();

        using var missing = await api.SendAsync(HttpMethod.Post, Route, state.Token, new { name = " ", role = "reader" });
        using var longName = await api.SendAsync(HttpMethod.Post, Route, state.Token, new { name = new string('x', 101), role = "reader" });
        using var past = await api.SendAsync(HttpMethod.Post, Route, state.Token, new { name = "Billing sync", role = "reader", expiresAtUtc = Now.AddMinutes(-1) });

        Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, longName.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, past.StatusCode);
    }

    [TestMethod]
    public async Task Revoking_stops_the_key_and_is_audited()
    {
        await using var api = await Api.StartAsync(owner: true);
        var state = await api.GetStateAsync();
        var keyId = await api.IssueAsync(state.Token);

        using var response = await api.SendAsync(HttpMethod.Post, $"{Route}/{keyId}/revoke", state.Token);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var listed = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["keys"]!.AsArray().Single()!;
        Assert.AreEqual("revoked", listed["status"]!.GetValue<string>());
        var provided = await api.Services.GetRequiredService<IIntegratorApiKeyProvider>().GetAsync();
        Assert.IsNotNull(provided!.Keys.Single().RevokedAtUtc);
        Assert.AreEqual(MessageAuditType.IntegratorApiKeyRevoked, api.Audit.Rows[^1].Type);
    }

    [TestMethod]
    public async Task An_active_key_must_be_revoked_before_it_is_removed()
    {
        await using var api = await Api.StartAsync(owner: true);
        var state = await api.GetStateAsync();
        var keyId = await api.IssueAsync(state.Token);

        using var refused = await api.SendAsync(HttpMethod.Delete, $"{Route}/{keyId}", state.Token);
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.AreEqual("StillActive", JsonNode.Parse(await refused.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());

        using var revoked = await api.SendAsync(HttpMethod.Post, $"{Route}/{keyId}/revoke", state.Token);
        using var removed = await api.SendAsync(HttpMethod.Delete, $"{Route}/{keyId}", state.Token);

        Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode);
        Assert.AreEqual(0, JsonNode.Parse(await removed.Content.ReadAsStringAsync())!["keys"]!.AsArray().Count);
        Assert.AreEqual(MessageAuditType.IntegratorApiKeyRemoved, api.Audit.Rows[^1].Type);
    }

    [TestMethod]
    public async Task Without_a_key_store_the_api_answers_not_implemented()
    {
        await using var api = await Api.StartAsync(owner: true, withStore: false);

        using var response = await api.Client.GetAsync(Route);

        Assert.AreEqual(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [TestMethod]
    public void Key_writes_are_always_audited()
    {
        CollectionAssert.IsSubsetOf(
            new[] { MessageAuditType.IntegratorApiKeyCreated, MessageAuditType.IntegratorApiKeyRevoked, MessageAuditType.IntegratorApiKeyRemoved },
            AuditSettingsProvider.AlwaysRecorded.ToArray());
    }

    [TestMethod]
    public async Task An_unknown_endpoint_an_empty_scope_and_an_owner_role_are_refused()
    {
        await using var api = await Api.StartAsync(owner: true);
        var state = await api.GetStateAsync();

        using var unknown = await api.SendAsync(HttpMethod.Post, Route, state.Token, new { name = "Sync", role = "reader", endpoints = new[] { "Billing", "Payroll" } });
        using var empty = await api.SendAsync(HttpMethod.Post, Route, state.Token, new { name = "Sync", role = "reader", endpoints = Array.Empty<string>() });
        using var duplicate = await api.SendAsync(HttpMethod.Post, Route, state.Token, new { name = "Sync", role = "reader", endpoints = new[] { "Billing", "billing" } });
        using var owner = await api.SendAsync(HttpMethod.Post, Route, state.Token, new { name = "Sync", role = "owner" });

        Assert.AreEqual(HttpStatusCode.BadRequest, unknown.StatusCode);
        StringAssert.Contains(await unknown.Content.ReadAsStringAsync(), "Payroll", StringComparison.Ordinal);
        Assert.AreEqual(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, duplicate.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, owner.StatusCode, "Owner is not a role a key can carry.");
        Assert.AreEqual(0, (await api.Store.GetIntegratorApiKeySettings()).Keys.Count);
    }

    [TestMethod]
    public async Task A_key_without_endpoints_is_site_wide()
    {
        await using var api = await Api.StartAsync(owner: true);
        var state = await api.GetStateAsync();

        using var response = await api.SendAsync(HttpMethod.Post, Route, state.Token, new { name = "Reporting", role = "reader" });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var stored = (await api.Store.GetIntegratorApiKeySettings()).Keys.Single();
        Assert.IsNull(stored.Endpoints);
        Assert.AreEqual(NimBus.MessageStore.States.IntegratorApiKeyRole.Reader, stored.Role);
        Assert.IsFalse(stored.CanReadPii);
    }

    private sealed class TwoEndpointPlatform : NimBus.Core.IPlatform
    {
        public System.Collections.Generic.IEnumerable<NimBus.Core.Endpoints.IEndpoint> Endpoints { get; } =
            new[] { "Billing", "Warehouse" }.Select(id => (NimBus.Core.Endpoints.IEndpoint)new NamedEndpoint(id)).ToList();

        public System.Collections.Generic.IEnumerable<NimBus.Core.Events.IEventType> EventTypes => [];

        public System.Collections.Generic.IEnumerable<NimBus.Core.Endpoints.IEndpoint> GetConsumers(NimBus.Core.Events.IEventType eventType) => [];

        public System.Collections.Generic.IEnumerable<NimBus.Core.Endpoints.IEndpoint> GetProducers(NimBus.Core.Events.IEventType eventType) => [];
    }

    private sealed class NamedEndpoint(string id) : NimBus.Core.Endpoints.IEndpoint
    {
        public string Id => id;
        public string Name => id;
        public string Description => string.Empty;
        public string Namespace => string.Empty;
        public string SecurityGroupName => string.Empty;
        public NimBus.Core.Endpoints.ISystem System => null!;
        public System.Collections.Generic.IEnumerable<NimBus.Core.Events.IEventType> EventTypesProduced => [];
        public System.Collections.Generic.IEnumerable<NimBus.Core.Events.IEventType> EventTypesConsumed => [];
        public System.Collections.Generic.IEnumerable<NimBus.Core.Endpoints.IRoleAssignment> RoleAssignments => [];
    }

    private sealed class Api : IAsyncDisposable
    {
        private readonly IHost _host;

        private Api(IHost host, InMemoryMessageStore store, AgentKeysApiTests.RecordingAudit audit)
        {
            _host = host;
            Store = store;
            Audit = audit;
            Client = host.GetTestClient();
        }

        public HttpClient Client { get; }
        public InMemoryMessageStore Store { get; }
        public AgentKeysApiTests.RecordingAudit Audit { get; }
        public IServiceProvider Services => _host.Services;

        public static async Task<Api> StartAsync(bool owner, bool withStore = true)
        {
            var store = new InMemoryMessageStore();
            var audit = new AgentKeysApiTests.RecordingAudit();
            var builder = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
            {
                services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                services.AddRouting();
                services.AddAuthorization();
                services.AddAuthentication(TestScheme.Name).AddScheme<AuthenticationSchemeOptions, TestScheme>(TestScheme.Name, null);
                services.AddAntiforgery(options => options.HeaderName = "X-NimBus-CSRF");
                services.AddHttpContextAccessor();
                if (withStore)
                    services.AddSingleton<IIntegratorApiKeyStore>(store);
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
                services.AddSingleton<IAuditLogService>(audit);
                services.AddSingleton<NimBus.Core.IPlatform>(new TwoEndpointPlatform());
                services.AddSingleton<IEndpointAuthorizationService>(new OwnerAuthorization(owner));
                services.AddSingleton<IIntegratorApiKeyProvider, IntegratorApiKeyProvider>();
                services.AddScoped<IntegratorApiKeyService>();
                services.AddTransient<NimBus.WebApp.ManagementApi.IIntegratorKeysApiController, IntegratorKeysImplementation>();
                services.AddControllersWithViews()
                    .AddApplicationPart(typeof(IntegratorKeysImplementation).Assembly)
                    .AddJsonOptions(opts =>
                    {
                        opts.JsonSerializerOptions.Converters.Add(new EnumMemberJsonConverterFactory());
                        opts.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
                    });
            }).Configure(app =>
            {
                app.Use(async (context, next) =>
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "owner")], "test"));
                    await next(context);
                });
                app.UseRouting();
                app.UseAuthorization();
                app.UseEndpoints(e =>
                {
                    e.MapControllers();
                    e.MapGet(TokenRoute, (HttpContext http, IAntiforgery antiforgery) =>
                        Results.Json(new { csrfToken = antiforgery.GetAndStoreTokens(http).RequestToken }));
                });
            }));
            return new Api(await builder.StartAsync(), store, audit);
        }

        public async Task<(string Token, JsonNode State)> GetStateAsync()
        {
            using var response = await Client.GetAsync(Route);
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            KeepCookies(response);
            var state = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
            return (state["csrfToken"]!.GetValue<string>(), state);
        }

        public async Task<string> TestTokenAsync()
        {
            using var response = await Client.GetAsync(TokenRoute);
            KeepCookies(response);
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["csrfToken"]!.GetValue<string>();
        }

        public async Task<string> IssueAsync(string csrf)
        {
            using var response = await SendAsync(HttpMethod.Post, Route, csrf, new { name = "Billing sync", role = "reader" });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["issued"]!["keyId"]!.GetValue<string>();
        }

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string csrf, object? body = null)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add("X-NimBus-CSRF", csrf);
            if (body is not null)
                request.Content = JsonContent.Create(body);
            return Client.SendAsync(request);
        }

        private void KeepCookies(HttpResponseMessage response)
        {
            if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
                Client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies.Select(c => c.Split(';')[0])));
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _host.StopAsync();
            _host.Dispose();
        }
    }

    // Lets ForbidResult answer 403, as the WebApp's own schemes do.
    private sealed class TestScheme(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Name = "test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    private sealed class OwnerAuthorization(bool owner) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(owner);
        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);
        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess());
        public string? GetCurrentUserName() => "owner@example.com";
    }
}
