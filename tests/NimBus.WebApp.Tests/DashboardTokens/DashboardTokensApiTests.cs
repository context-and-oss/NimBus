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
using NimBus.WebApp.Services.DashboardTokens;
using NimBus.WebApp.Tests.AgentApiKeys;

namespace NimBus.WebApp.Tests.DashboardTokens;

[TestClass]
public class DashboardTokensApiTests
{
    private const string TokenRoute = "/test/csrf";
    private const string Route = "/api/admin/dashboard-tokens";
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task A_non_owner_cannot_list_tokens()
    {
        await using var api = await Api.StartAsync(owner: false);

        using var response = await api.Client.GetAsync(Route);

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task A_non_owner_cannot_issue_a_token_and_the_attempt_is_audited()
    {
        await using var api = await Api.StartAsync(owner: false);
        var token = await api.TestTokenAsync();

        using var response = await api.SendAsync(HttpMethod.Post, Route, token, new { name = "Wall" });

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        var row = api.Audit.Rows.Single();
        Assert.AreEqual(MessageAuditType.DashboardTokenCreated, row.Type);
        Assert.IsTrue(row.Denied);
        Assert.AreEqual(0, (await api.Store.GetDashboardTokenSettings()).Tokens.Count);
    }

    [TestMethod]
    public async Task Writes_need_the_antiforgery_token()
    {
        await using var api = await Api.StartAsync(owner: true);

        using var response = await api.Client.PostAsJsonAsync(Route, new { name = "Wall" });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(0, (await api.Store.GetDashboardTokenSettings()).Tokens.Count);
    }

    [TestMethod]
    public async Task Issuing_returns_the_token_once_and_it_authenticates_at_once()
    {
        await using var api = await Api.StartAsync(owner: true);
        var state = await api.GetStateAsync();

        using var response = await api.SendAsync(HttpMethod.Post, Route, state.Token,
            new { name = "Operations wall", expiresAtUtc = Now.AddDays(365) });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var issued = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var token = issued["token"]!.GetValue<string>();
        Assert.IsTrue(SecretTokenFormat.TryParse(DashboardTokenDefaults.Prefix, token, out var tokenId, out _));
        Assert.AreEqual(tokenId, issued["issued"]!["tokenId"]!.GetValue<string>());
        Assert.AreEqual("nbdt_" + tokenId, issued["issued"]!["prefix"]!.GetValue<string>());
        Assert.AreEqual("Operations wall", issued["issued"]!["name"]!.GetValue<string>());
        Assert.AreEqual("active", issued["issued"]!["status"]!.GetValue<string>());

        var stored = (await api.Store.GetDashboardTokenSettings()).Tokens.Single();
        Assert.AreNotEqual(token, stored.SecretHash);
        var listed = await api.Client.GetStringAsync(Route);
        Assert.IsFalse(listed.Contains(token[(5 + 12 + 1)..], StringComparison.Ordinal), "the secret must never be listed");

        var provided = await api.Services.GetRequiredService<IDashboardTokenProvider>().GetAsync();
        Assert.AreEqual(tokenId, provided!.Tokens.Single().TokenId);

        var row = api.Audit.Rows.Single();
        Assert.AreEqual(MessageAuditType.DashboardTokenCreated, row.Type);
        Assert.IsFalse(row.Denied);
        StringAssert.Contains(row.Data, tokenId, StringComparison.Ordinal);
        Assert.IsFalse(row.Data!.Contains(token, StringComparison.Ordinal), "the token must never be audited");
    }

    [TestMethod]
    public async Task A_missing_name_a_long_name_and_a_past_expiry_are_refused()
    {
        await using var api = await Api.StartAsync(owner: true);
        var state = await api.GetStateAsync();

        using var missing = await api.SendAsync(HttpMethod.Post, Route, state.Token, new { name = " " });
        using var longName = await api.SendAsync(HttpMethod.Post, Route, state.Token, new { name = new string('x', 101) });
        using var past = await api.SendAsync(HttpMethod.Post, Route, state.Token, new { name = "Wall", expiresAtUtc = Now.AddMinutes(-1) });

        Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, longName.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, past.StatusCode);
    }

    [TestMethod]
    public async Task Revoking_stops_the_token_and_is_audited()
    {
        await using var api = await Api.StartAsync(owner: true);
        var state = await api.GetStateAsync();
        var tokenId = await api.IssueAsync(state.Token);

        using var response = await api.SendAsync(HttpMethod.Post, $"{Route}/{tokenId}/revoke", state.Token);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var listed = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["tokens"]!.AsArray().Single()!;
        Assert.AreEqual("revoked", listed["status"]!.GetValue<string>());
        var provided = await api.Services.GetRequiredService<IDashboardTokenProvider>().GetAsync();
        Assert.IsNotNull(provided!.Tokens.Single().RevokedAtUtc);
        Assert.AreEqual(MessageAuditType.DashboardTokenRevoked, api.Audit.Rows[^1].Type);
    }

    [TestMethod]
    public async Task An_active_token_must_be_revoked_before_it_is_removed()
    {
        await using var api = await Api.StartAsync(owner: true);
        var state = await api.GetStateAsync();
        var tokenId = await api.IssueAsync(state.Token);

        using var refused = await api.SendAsync(HttpMethod.Delete, $"{Route}/{tokenId}", state.Token);
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.AreEqual("StillActive", JsonNode.Parse(await refused.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());

        using var revoked = await api.SendAsync(HttpMethod.Post, $"{Route}/{tokenId}/revoke", state.Token);
        using var removed = await api.SendAsync(HttpMethod.Delete, $"{Route}/{tokenId}", state.Token);

        Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode);
        Assert.AreEqual(0, JsonNode.Parse(await removed.Content.ReadAsStringAsync())!["tokens"]!.AsArray().Count);
        Assert.AreEqual(MessageAuditType.DashboardTokenRemoved, api.Audit.Rows[^1].Type);
    }

    [TestMethod]
    public async Task Without_a_token_store_the_api_answers_not_implemented()
    {
        await using var api = await Api.StartAsync(owner: true, withStore: false);

        using var response = await api.Client.GetAsync(Route);

        Assert.AreEqual(HttpStatusCode.NotImplemented, response.StatusCode);
    }

    [TestMethod]
    public void Token_writes_are_always_audited()
    {
        CollectionAssert.IsSubsetOf(
            new[] { MessageAuditType.DashboardTokenCreated, MessageAuditType.DashboardTokenRevoked, MessageAuditType.DashboardTokenRemoved },
            AuditSettingsProvider.AlwaysRecorded.ToArray());
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
                    services.AddSingleton<IDashboardTokenStore>(store);
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(Now));
                services.AddSingleton<IAuditLogService>(audit);
                services.AddSingleton<IEndpointAuthorizationService>(new OwnerAuthorization(owner));
                services.AddSingleton<IDashboardTokenProvider, DashboardTokenProvider>();
                services.AddScoped<DashboardTokenService>();
                services.AddTransient<NimBus.WebApp.ManagementApi.IDashboardTokensApiController, DashboardTokensImplementation>();
                services.AddControllersWithViews()
                    .AddApplicationPart(typeof(DashboardTokensImplementation).Assembly)
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
            using var response = await SendAsync(HttpMethod.Post, Route, csrf, new { name = "Wall" });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["issued"]!["tokenId"]!.GetValue<string>();
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
