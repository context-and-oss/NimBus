#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
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
using NimBus.WebApp.Services.AgentApiKeys;

namespace NimBus.WebApp.Tests.AgentApiKeys;

[TestClass]
public class AgentKeysApiTests
{
    private const string TokenRoute = "/test/csrf";
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task A_non_owner_cannot_list_keys()
    {
        await using var api = await Api.StartAsync(owner: false);

        using var response = await api.Client.GetAsync("/api/admin/agent-keys");

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task A_non_owner_cannot_issue_a_key_and_the_attempt_is_audited()
    {
        await using var api = await Api.StartAsync(owner: false);
        var token = await api.TestTokenAsync();

        using var response = await api.SendAsync(HttpMethod.Post, "/api/admin/agent-keys", token, new { agentId = "enrichment-agent" });

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        var row = api.Audit.Rows.Single();
        Assert.AreEqual(MessageAuditType.AgentApiKeyCreated, row.Type);
        Assert.IsTrue(row.Denied);
        Assert.AreEqual(0, (await api.Store.GetAgentApiKeySettings()).Keys.Count);
    }

    [TestMethod]
    public async Task Writes_need_the_antiforgery_token()
    {
        await using var api = await Api.StartAsync(owner: true);

        using var response = await api.Client.PostAsJsonAsync("/api/admin/agent-keys", new { agentId = "enrichment-agent" });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual(0, (await api.Store.GetAgentApiKeySettings()).Keys.Count);
    }

    [TestMethod]
    public async Task Issuing_returns_the_key_once_and_it_authenticates_at_once()
    {
        await using var api = await Api.StartAsync(owner: true);
        var state = await api.GetStateAsync();

        using var response = await api.SendAsync(HttpMethod.Post, "/api/admin/agent-keys", state.Token,
            new { agentId = "enrichment-agent", name = "Production enrichment", expiresAtUtc = Now.AddDays(90) });

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
        var issued = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        var key = issued["key"]!.GetValue<string>();
        Assert.IsTrue(AgentApiKeyFormat.TryParse(key, out var keyId, out _));
        Assert.AreEqual(keyId, issued["issued"]!["keyId"]!.GetValue<string>());
        Assert.AreEqual("nbak_" + keyId, issued["issued"]!["prefix"]!.GetValue<string>());
        Assert.AreEqual("enrichment-agent", issued["issued"]!["agentId"]!.GetValue<string>());
        Assert.AreEqual("Production enrichment", issued["issued"]!["name"]!.GetValue<string>());
        Assert.AreEqual("active", issued["issued"]!["status"]!.GetValue<string>());
        Assert.AreEqual("owner@example.com", issued["issued"]!["createdBy"]!.GetValue<string>());

        // Only the hash is stored, and later reads never return the key.
        var stored = (await api.Store.GetAgentApiKeySettings()).Keys.Single();
        Assert.AreNotEqual(key, stored.SecretHash);
        var listed = await api.Client.GetStringAsync("/api/admin/agent-keys");
        Assert.IsFalse(listed.Contains(key[(5 + 12 + 1)..], StringComparison.Ordinal), "the secret must never be listed");

        // This instance accepts the new key without waiting for its cache.
        var provided = await api.Services.GetRequiredService<IAgentApiKeyProvider>().GetAsync();
        Assert.AreEqual(keyId, provided!.Keys.Single().KeyId);

        var row = api.Audit.Rows.Single();
        Assert.AreEqual(MessageAuditType.AgentApiKeyCreated, row.Type);
        Assert.IsFalse(row.Denied);
        StringAssert.Contains(row.Data, keyId);
        StringAssert.Contains(row.Data, "enrichment-agent");
        Assert.IsFalse(row.Data!.Contains(key, StringComparison.Ordinal), "the key must never be audited");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("-starts-with-a-dash")]
    [DataRow("has space")]
    [DataRow("an-agent-id-that-is-far-too-long-to-be-accepted-by-the-validation-rule-x")]
    public async Task An_invalid_agent_id_is_refused(string agentId)
    {
        await using var api = await Api.StartAsync(owner: true);
        var state = await api.GetStateAsync();

        using var response = await api.SendAsync(HttpMethod.Post, "/api/admin/agent-keys", state.Token, new { agentId });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.AreEqual("Invalid", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());
    }

    [TestMethod]
    public async Task A_past_expiry_and_a_long_name_are_refused()
    {
        await using var api = await Api.StartAsync(owner: true);
        var state = await api.GetStateAsync();

        using var past = await api.SendAsync(HttpMethod.Post, "/api/admin/agent-keys", state.Token,
            new { agentId = "a", expiresAtUtc = Now.AddMinutes(-1) });
        using var longName = await api.SendAsync(HttpMethod.Post, "/api/admin/agent-keys", state.Token,
            new { agentId = "a", name = new string('x', 101) });

        Assert.AreEqual(HttpStatusCode.BadRequest, past.StatusCode);
        Assert.AreEqual(HttpStatusCode.BadRequest, longName.StatusCode);
    }

    [TestMethod]
    public async Task Revoking_stops_the_key_and_is_audited()
    {
        await using var api = await Api.StartAsync(owner: true);
        var state = await api.GetStateAsync();
        var keyId = await api.IssueAsync(state.Token, "enrichment-agent");

        using var response = await api.SendAsync(HttpMethod.Post, $"/api/admin/agent-keys/{keyId}/revoke", state.Token);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        var listed = JsonNode.Parse(await response.Content.ReadAsStringAsync())!["keys"]!.AsArray().Single()!;
        Assert.AreEqual("revoked", listed["status"]!.GetValue<string>());
        Assert.AreEqual("owner@example.com", listed["revokedBy"]!.GetValue<string>());
        var provided = await api.Services.GetRequiredService<IAgentApiKeyProvider>().GetAsync();
        Assert.IsNotNull(provided!.Keys.Single().RevokedAtUtc);
        Assert.AreEqual(MessageAuditType.AgentApiKeyRevoked, api.Audit.Rows.Last().Type);
    }

    [TestMethod]
    public async Task An_unknown_key_is_not_found()
    {
        await using var api = await Api.StartAsync(owner: true);
        var state = await api.GetStateAsync();

        using var revoke = await api.SendAsync(HttpMethod.Post, "/api/admin/agent-keys/abcdefghijkl/revoke", state.Token);
        using var remove = await api.SendAsync(HttpMethod.Delete, "/api/admin/agent-keys/abcdefghijkl", state.Token);

        Assert.AreEqual(HttpStatusCode.NotFound, revoke.StatusCode);
        Assert.AreEqual(HttpStatusCode.NotFound, remove.StatusCode);
    }

    [TestMethod]
    public async Task An_active_key_must_be_revoked_before_it_is_removed()
    {
        await using var api = await Api.StartAsync(owner: true);
        var state = await api.GetStateAsync();
        var keyId = await api.IssueAsync(state.Token, "enrichment-agent");

        using var refused = await api.SendAsync(HttpMethod.Delete, $"/api/admin/agent-keys/{keyId}", state.Token);
        Assert.AreEqual(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.AreEqual("StillActive", JsonNode.Parse(await refused.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());

        using var revoked = await api.SendAsync(HttpMethod.Post, $"/api/admin/agent-keys/{keyId}/revoke", state.Token);
        using var removed = await api.SendAsync(HttpMethod.Delete, $"/api/admin/agent-keys/{keyId}", state.Token);

        Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode);
        Assert.AreEqual(0, JsonNode.Parse(await removed.Content.ReadAsStringAsync())!["keys"]!.AsArray().Count);
        Assert.AreEqual(MessageAuditType.AgentApiKeyRemoved, api.Audit.Rows.Last().Type);
    }

    [TestMethod]
    public async Task An_expired_key_can_be_removed_without_revoking_it()
    {
        await using var api = await Api.StartAsync(owner: true);
        var state = await api.GetStateAsync();
        var keyId = await api.IssueAsync(state.Token, "enrichment-agent", expiresAtUtc: Now.AddMinutes(5));
        api.Time.Advance(TimeSpan.FromMinutes(6));

        var listed = JsonNode.Parse(await api.Client.GetStringAsync("/api/admin/agent-keys"))!["keys"]!.AsArray().Single()!;
        Assert.AreEqual("expired", listed["status"]!.GetValue<string>());

        using var removed = await api.SendAsync(HttpMethod.Delete, $"/api/admin/agent-keys/{keyId}", state.Token);
        Assert.AreEqual(HttpStatusCode.OK, removed.StatusCode);
    }

    [TestMethod]
    public async Task The_list_shows_when_each_key_was_last_used()
    {
        await using var api = await Api.StartAsync(owner: true);
        var state = await api.GetStateAsync();
        var used = await api.IssueAsync(state.Token, "a");
        var unused = await api.IssueAsync(state.Token, "b");
        await api.Store.RecordAgentApiKeyUse(used, Now.UtcDateTime.AddMinutes(-3));

        var keys = JsonNode.Parse(await api.Client.GetStringAsync("/api/admin/agent-keys"))!["keys"]!.AsArray();

        var usedRow = keys.Single(k => k!["keyId"]!.GetValue<string>() == used)!;
        var unusedRow = keys.Single(k => k!["keyId"]!.GetValue<string>() == unused)!;
        Assert.AreEqual(Now.UtcDateTime.AddMinutes(-3), usedRow["lastUsedAtUtc"]!.GetValue<DateTime>().ToUniversalTime());
        Assert.IsNull(unusedRow["lastUsedAtUtc"]);
    }

    [TestMethod]
    public async Task A_save_that_keeps_conflicting_is_reported()
    {
        await using var api = await Api.StartAsync(owner: true, store: new AlwaysConflictingStore());
        var state = await api.GetStateAsync();

        using var response = await api.SendAsync(HttpMethod.Post, "/api/admin/agent-keys", state.Token, new { agentId = "a" });

        Assert.AreEqual(HttpStatusCode.Conflict, response.StatusCode);
        Assert.AreEqual("RevisionConflict", JsonNode.Parse(await response.Content.ReadAsStringAsync())!["code"]!.GetValue<string>());
    }

    private sealed class Api : IAsyncDisposable
    {
        private readonly IHost _host;

        private Api(IHost host, InMemoryMessageStore store, RecordingAudit audit, FakeTimeProvider time)
        {
            _host = host;
            Store = store;
            Audit = audit;
            Time = time;
            Client = host.GetTestClient();
        }

        public HttpClient Client { get; }
        public InMemoryMessageStore Store { get; }
        public RecordingAudit Audit { get; }
        public FakeTimeProvider Time { get; }
        public IServiceProvider Services => _host.Services;

        public static async Task<Api> StartAsync(bool owner, InMemoryMessageStore? store = null)
        {
            store ??= new InMemoryMessageStore();
            var audit = new RecordingAudit();
            var time = new FakeTimeProvider(Now);
            var builder = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
            {
                services.AddLogging(b => b.SetMinimumLevel(LogLevel.Warning));
                services.AddRouting();
                services.AddAuthorization();
                services.AddAuthentication(TestScheme.Name).AddScheme<AuthenticationSchemeOptions, TestScheme>(TestScheme.Name, null);
                services.AddAntiforgery(options => options.HeaderName = "X-NimBus-CSRF");
                services.AddHttpContextAccessor();
                services.AddSingleton<IEndpointMetadataStore>(store);
                services.AddSingleton<TimeProvider>(time);
                services.AddSingleton<IAuditLogService>(audit);
                services.AddSingleton<IEndpointAuthorizationService>(new OwnerAuthorization(owner));
                services.AddSingleton<IAgentApiKeyProvider, AgentApiKeyProvider>();
                services.AddScoped<AgentApiKeyService>();
                services.AddTransient<NimBus.WebApp.ManagementApi.IAgentKeysApiController, AgentKeysImplementation>();
                services.AddControllersWithViews()
                    .AddApplicationPart(typeof(AgentKeysImplementation).Assembly)
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
                    // Test-only: lets a non-Owner obtain a token the Owner-only GET would not issue.
                    e.MapGet(TokenRoute, (HttpContext http, IAntiforgery antiforgery) =>
                        Results.Json(new { csrfToken = antiforgery.GetAndStoreTokens(http).RequestToken }));
                });
            }));
            return new Api(await builder.StartAsync(), store, audit, time);
        }

        public async Task<(string Token, JsonNode State)> GetStateAsync()
        {
            using var response = await Client.GetAsync("/api/admin/agent-keys");
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

        public async Task<string> IssueAsync(string token, string agentId, DateTimeOffset? expiresAtUtc = null)
        {
            using var response = await SendAsync(HttpMethod.Post, "/api/admin/agent-keys", token, new { agentId, expiresAtUtc });
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode, await response.Content.ReadAsStringAsync());
            return JsonNode.Parse(await response.Content.ReadAsStringAsync())!["issued"]!["keyId"]!.GetValue<string>();
        }

        public Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, object? body = null)
        {
            var request = new HttpRequestMessage(method, url);
            request.Headers.Add("X-NimBus-CSRF", token);
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

    internal sealed record Row(MessageAuditType Type, bool Denied, string? Data);

    internal sealed class RecordingAudit : IAuditLogService
    {
        private readonly List<Row> _rows = [];

        public IReadOnlyList<Row> Rows => _rows;

        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null, string? auditorNameOverride = null,
            CancellationToken cancellationToken = default)
        {
            _rows.Add(new Row(type, accessDenied, data));
            return Task.CompletedTask;
        }
    }

    private sealed class AlwaysConflictingStore : InMemoryMessageStore
    {
        public override Task<bool> TrySetAgentApiKeySettings(NimBus.MessageStore.States.AgentApiKeySettings settings, string? expectedRevision)
            => Task.FromResult(false);
    }
}
