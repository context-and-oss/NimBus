#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Results = Microsoft.AspNetCore.Http.Results;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.Mcp;
using NimBus.WebApp.Mcp.Access;
using NimBus.WebApp.RateLimiting;
using NimBus.WebApp.Services;
using CoreEndpoint = NimBus.Core.Endpoints.Endpoint;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>Spec 037: policy rules, the settings service, the cached provider and the Admin API.</summary>
[TestClass]
public class McpAccessSettingsTests
{
    private static readonly string[] Catalog = ["CrmEndpoint", "ErpEndpoint", "PayrollEndpoint"];

    // ── Rules ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void Normalize_accepts_the_defaults()
        => Assert.AreEqual(0, McpAccessRules.Normalize(new McpAccessSettings(), Catalog, new RateLimitOptions()).Count);

    [TestMethod]
    public void Normalize_puts_endpoints_in_catalog_spelling_and_trims_entries()
    {
        var settings = new McpAccessSettings
        {
            People = new() { Mode = McpPeopleMode.Listed, Principals = [new() { Principal = "  Pilot@Example.com " }, new() { Principal = "pilot@example.com" }] },
            Endpoints = new() { Visibility = McpEndpointVisibility.AllExcept, Hidden = [" payrollendpoint", "PAYROLLENDPOINT"] },
        };

        var errors = McpAccessRules.Normalize(settings, Catalog, new RateLimitOptions());

        Assert.AreEqual(0, errors.Count, string.Join("; ", errors));
        Assert.AreEqual("Pilot@Example.com", settings.People.Principals.Single().Principal);
        CollectionAssert.AreEqual(new[] { "PayrollEndpoint" }, settings.Endpoints.Hidden);
    }

    [TestMethod]
    [DataRow("people", "Add at least one user or group")]
    [DataRow("clients", "Approve at least one client")]
    [DataRow("hidden", "Pick at least one endpoint to hide")]
    [DataRow("changeOn", "Pick at least one endpoint where agents may change")]
    [DataRow("unknownEndpoint", "is not an endpoint in the catalog")]
    [DataRow("badPrincipal", "is not an email address or an object id")]
    [DataRow("badClient", "is not an application (client) id")]
    [DataRow("hiddenChange", "is hidden, so agents cannot change")]
    [DataRow("limitAboveDeployment", "between 1 and 60")]
    public void Normalize_rejects_invalid_policies(string variant, string expected)
    {
        var settings = new McpAccessSettings();
        switch (variant)
        {
            case "people": settings.People.Mode = McpPeopleMode.Listed; break;
            case "clients": settings.Clients.Mode = McpClientMode.Approved; break;
            case "hidden": settings.Endpoints.Visibility = McpEndpointVisibility.AllExcept; break;
            case "changeOn": settings.Endpoints.Changes = McpChangeScope.Listed; break;
            case "unknownEndpoint": settings.Endpoints.Visibility = McpEndpointVisibility.AllExcept; settings.Endpoints.Hidden = ["Nope"]; break;
            case "badPrincipal": settings.People.Principals = [new() { Principal = "not a person" }]; break;
            case "badClient": settings.Clients.Approved = [new() { ClientId = "claude", Name = "Claude" }]; break;
            case "hiddenChange":
                settings.Endpoints.Visibility = McpEndpointVisibility.AllExcept;
                settings.Endpoints.Hidden = ["ErpEndpoint"];
                settings.Endpoints.Changes = McpChangeScope.Listed;
                settings.Endpoints.ChangeOn = ["ErpEndpoint"];
                break;
            case "limitAboveDeployment": settings.Limits.RequestsPerWindow = 61; break;
        }

        var errors = McpAccessRules.Normalize(settings, Catalog, new RateLimitOptions());

        Assert.IsTrue(errors.Any(e => e.Contains(expected, StringComparison.Ordinal)), string.Join("; ", errors));
    }

    [TestMethod]
    public void Normalize_refuses_limits_when_rate_limiting_is_off()
    {
        var settings = new McpAccessSettings { Limits = new() { MutationsPerWindow = 2 } };

        var errors = McpAccessRules.Normalize(settings, Catalog, new RateLimitOptions { Enabled = false });

        StringAssert.Contains(errors.Single(), "Rate limiting is off", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Changes_mark_what_widens_and_what_narrows()
    {
        var before = new McpAccessSettings();
        before.Capabilities.Skip = false;
        before.Limits.MutationsPerWindow = 2;
        before.Clients = new() { Mode = McpClientMode.Approved, Approved = [new() { ClientId = "a", Name = "Old", MayChange = false }] };

        var after = before.Clone();
        after.Capabilities.Skip = true;            // widens
        after.Capabilities.Payloads = false;       // narrows
        after.Limits.MutationsPerWindow = null;    // widens: back to the deployment value
        after.Clients.Approved[0].MayChange = true; // widens
        after.Enabled = false;                     // narrows

        var changes = McpAccessRules.Changes(before, after);

        CollectionAssert.AreEquivalent(
            new[]
            {
                ("Turn the MCP endpoint off", false),
                ("Allow skipping messages", true),
                ("Stop raw payloads", false),
                ("Let client Old change messages", true),
                ("Message changes per window: 2 → deployment value", true),
            },
            changes.Select(c => (c.Text, c.Widens)).ToArray());
    }

    // ── Settings service ───────────────────────────────────────────────────

    [TestMethod]
    public async Task Save_requires_confirmation_for_a_widening_change_and_stores_nothing_without_it()
    {
        var (service, store, _) = CreateService(seed: s => s.Capabilities.Skip = false);
        var current = await store.GetMcpAccessSettings();
        var next = current.Clone();
        next.Capabilities.Skip = true;

        var refused = await service.SaveAsync(next, current.Revision, confirmWidening: false, "owner");
        Assert.AreEqual(McpAccessSaveStatus.ConfirmationRequired, refused.Status);
        Assert.IsFalse((await store.GetMcpAccessSettings()).Capabilities.Skip);

        var saved = await service.SaveAsync(next, current.Revision, confirmWidening: true, "owner");
        Assert.AreEqual(McpAccessSaveStatus.Saved, saved.Status);
        Assert.IsTrue((await store.GetMcpAccessSettings()).Capabilities.Skip);
        Assert.AreEqual("owner", saved.Saved.UpdatedBy);
        Assert.AreNotEqual(current.Revision, saved.Saved.Revision);
    }

    [TestMethod]
    public async Task Save_needs_no_confirmation_to_narrow_and_refreshes_this_instance_at_once()
    {
        var (service, store, provider) = CreateService();
        Assert.IsTrue((await provider.GetAsync())!.AllowsAction(NimBus.WebApp.Mcp.Operations.OperatorAction.Skip));

        var next = new McpAccessSettings();
        next.Capabilities.Skip = false;
        var result = await service.SaveAsync(next, (await store.GetMcpAccessSettings()).Revision, confirmWidening: false, "owner");

        Assert.AreEqual(McpAccessSaveStatus.Saved, result.Status);
        Assert.IsFalse(provider.Current!.AllowsAction(NimBus.WebApp.Mcp.Operations.OperatorAction.Skip), "No TTL wait on the saving instance.");
    }

    [TestMethod]
    public async Task Save_with_a_stale_revision_is_a_conflict()
    {
        var (service, _, _) = CreateService(seed: _ => { });

        var result = await service.SaveAsync(new McpAccessSettings(), Guid.NewGuid().ToString(), confirmWidening: true, "owner");

        Assert.AreEqual(McpAccessSaveStatus.Conflict, result.Status);
    }

    [TestMethod]
    public async Task Turn_off_ignores_the_editors_revision_keeps_the_rest_and_works_with_nothing_saved()
    {
        var (service, store, _) = CreateService();

        var first = await service.TurnOffAsync("owner");
        Assert.AreEqual(McpAccessSaveStatus.Saved, first.Status);
        Assert.IsFalse((await store.GetMcpAccessSettings()).Enabled);

        var seeded = (await store.GetMcpAccessSettings()).Clone();
        seeded.Enabled = true;
        seeded.Capabilities.Skip = false;
        seeded.Revision = Guid.NewGuid().ToString();
        Assert.IsTrue(await store.TrySetMcpAccessSettings(seeded, first.Saved.Revision));

        var second = await service.TurnOffAsync("owner");
        var stored = await store.GetMcpAccessSettings();
        Assert.AreEqual(McpAccessSaveStatus.Saved, second.Status);
        Assert.IsFalse(stored.Enabled);
        Assert.IsFalse(stored.Capabilities.Skip, "Turning off keeps every other setting.");
    }

    // ── Provider ───────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Provider_caches_for_the_ttl_and_keeps_the_last_policy_when_a_read_fails()
    {
        var store = new FlakyStore();
        var time = new MutableTime();
        var provider = new McpAccessPolicyProvider(store, Options.Create(new RateLimitOptions()), NullLogger<McpAccessPolicyProvider>.Instance, time);

        Assert.IsTrue((await provider.GetAsync())!.Enabled);
        var off = new McpAccessSettings { Enabled = false, Revision = Guid.NewGuid().ToString() };
        Assert.IsTrue(await store.TrySetMcpAccessSettings(off, null));

        Assert.IsTrue((await provider.GetAsync())!.Enabled, "Within the TTL the cached policy is used.");
        time.Advance(McpAccessPolicyProvider.SuccessTtl);
        Assert.IsFalse((await provider.GetAsync())!.Enabled, "After the TTL the new policy applies.");

        store.Fail = true;
        time.Advance(McpAccessPolicyProvider.SuccessTtl);
        Assert.IsFalse((await provider.GetAsync())!.Enabled, "A failed read keeps the last policy.");
    }

    [TestMethod]
    public async Task Provider_returns_no_policy_until_a_read_succeeds()
    {
        var store = new FlakyStore { Fail = true };
        var time = new MutableTime();
        var provider = new McpAccessPolicyProvider(store, Options.Create(new RateLimitOptions()), NullLogger<McpAccessPolicyProvider>.Instance, time);

        Assert.IsNull(await provider.GetAsync());
        store.Fail = false;
        Assert.IsNull(await provider.GetAsync(), "The failure is cached briefly.");
        time.Advance(McpAccessPolicyProvider.FailureTtl);
        Assert.IsNotNull(await provider.GetAsync());
    }

    [TestMethod]
    public void Policy_lowers_limits_to_the_deployment_and_ignores_them_when_rate_limiting_is_off()
    {
        var settings = new McpAccessSettings { Limits = new() { RequestsPerWindow = 500, MutationsPerWindow = 2 } };

        var limited = McpAccessPolicy.From(settings, new RateLimitOptions());
        var unlimited = McpAccessPolicy.From(settings, new RateLimitOptions { Enabled = false });

        Assert.AreEqual(60, limited.RequestLimit);
        Assert.AreEqual(2, limited.MutationLimit);
        Assert.IsNull(unlimited.RequestLimit);
        Assert.IsNull(unlimited.MutationLimit);
    }

    // ── Admin API ──────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Api_is_for_site_owners_only_and_audits_a_denied_save()
    {
        var audit = new RecordingAudit();
        using var host = await CreateApiHostAsync(new InMemoryMessageStore(), audit, owner: false);
        var client = host.GetTestClient();

        using var get = await client.GetAsync("/api/admin/mcp/settings");
        Assert.AreEqual(HttpStatusCode.Forbidden, get.StatusCode);

        // Without a token the antiforgery filter refuses before the Owner check: nothing to audit.
        using (var unsigned = await client.PutAsJsonAsync("/api/admin/mcp/settings", new { settings = new { enabled = false } }))
            Assert.AreEqual(HttpStatusCode.BadRequest, unsigned.StatusCode);
        Assert.AreEqual(0, audit.Rows.Count);

        // A non-Owner with a valid token reaches the Owner check, which refuses and audits.
        var token = await GetStateAsync(client, TokenRoute);
        using var put = await SendAsync(client, HttpMethod.Put, "/api/admin/mcp/settings", token,
            new JsonObject { ["settings"] = new JsonObject { ["enabled"] = false } });
        Assert.AreEqual(HttpStatusCode.Forbidden, put.StatusCode);
        Assert.IsTrue(audit.Rows.Single() is { Type: MessageAuditType.UpdateMcpSettings, Denied: true });
    }

    [TestMethod]
    public async Task Api_saves_with_antiforgery_and_revision_and_audits_the_changes()
    {
        var store = new InMemoryMessageStore();
        var audit = new RecordingAudit();
        using var host = await CreateApiHostAsync(store, audit, owner: true);
        var client = host.GetTestClient();

        var state = await GetStateAsync(client);
        Assert.IsNull(state["saved"]!["revision"]?.GetValue<string>());
        Assert.AreEqual("disabled", state["deployment"]!["mode"]!.GetValue<string>());
        CollectionAssert.AreEqual(Catalog, state["deployment"]!["endpoints"]!.AsArray().Select(e => e!.GetValue<string>()).ToArray());

        var settings = state["saved"]!.DeepClone();
        settings["capabilities"]!["skip"] = false;
        settings["endpoints"]!["visibility"] = "allExcept";
        settings["endpoints"]!["hidden"] = new JsonArray("payrollendpoint");

        // No antiforgery header: refused by [AutoValidateAntiforgeryToken], nothing saved.
        using (var missing = await client.PutAsJsonAsync("/api/admin/mcp/settings", new { revision = (string?)null, settings, confirmWidening = false }))
            Assert.AreEqual(HttpStatusCode.BadRequest, missing.StatusCode);
        Assert.IsNull((await store.GetMcpAccessSettings()).Revision);

        using var saved = await SendAsync(client, HttpMethod.Put, "/api/admin/mcp/settings", state,
            new JsonObject { ["revision"] = null, ["settings"] = settings, ["confirmWidening"] = false });
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
        var stored = await store.GetMcpAccessSettings();
        Assert.IsFalse(stored.Capabilities.Skip);
        CollectionAssert.AreEqual(new[] { "PayrollEndpoint" }, stored.Endpoints.Hidden);

        var row = audit.Rows.Single();
        Assert.AreEqual(MessageAuditType.UpdateMcpSettings, row.Type);
        StringAssert.Contains(row.Data, "Stop skipping messages", StringComparison.Ordinal);
        StringAssert.Contains(row.Data, "Hide PayrollEndpoint from agents", StringComparison.Ordinal);

        // The same body again carries a revision that is now stale.
        using var stale = await SendAsync(client, HttpMethod.Put, "/api/admin/mcp/settings", state,
            new JsonObject { ["revision"] = null, ["settings"] = settings.DeepClone(), ["confirmWidening"] = false });
        Assert.AreEqual(HttpStatusCode.Conflict, stale.StatusCode);
    }

    [TestMethod]
    public async Task Api_answers_confirmation_required_with_the_widening_changes()
    {
        var store = new InMemoryMessageStore();
        var seeded = new McpAccessSettings { Revision = Guid.NewGuid().ToString() };
        seeded.Capabilities.Skip = false;
        Assert.IsTrue(await store.TrySetMcpAccessSettings(seeded, null));
        using var host = await CreateApiHostAsync(store, new RecordingAudit(), owner: true);
        var client = host.GetTestClient();
        var state = await GetStateAsync(client);
        var settings = state["saved"]!.DeepClone();
        settings["capabilities"]!["skip"] = true;

        using var response = await SendAsync(client, HttpMethod.Put, "/api/admin/mcp/settings", state,
            new JsonObject { ["revision"] = seeded.Revision, ["settings"] = settings, ["confirmWidening"] = false });

        Assert.AreEqual(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        Assert.AreEqual("ConfirmationRequired", problem["code"]!.GetValue<string>());
        Assert.IsTrue(problem["changes"]!.AsArray().Any(c => c!["widens"]!.GetValue<bool>()));
    }

    [TestMethod]
    public async Task Api_turn_off_saves_and_audits()
    {
        var store = new InMemoryMessageStore();
        var audit = new RecordingAudit();
        using var host = await CreateApiHostAsync(store, audit, owner: true);
        var client = host.GetTestClient();
        var state = await GetStateAsync(client);

        using var response = await SendAsync(client, HttpMethod.Post, "/api/admin/mcp/turn-off", state, null);

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        Assert.IsFalse((await store.GetMcpAccessSettings()).Enabled);
        StringAssert.Contains(audit.Rows.Single().Data, "\"turnOff\":true", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Activity_keeps_mcp_rows_and_groups_refused_clients()
    {
        var store = new InMemoryMessageStore();
        await store.StoreMessageAudit("evt-1", Audit(MessageAuditType.Resubmit, """{"channel":"Mcp","reason":"ERP back","clientId":"c1"}"""), "CrmEndpoint");
        await store.StoreMessageAudit("evt-2", Audit(MessageAuditType.Resubmit, """{"channel":"WebApp","reason":"UI"}"""), "CrmEndpoint");
        await store.StoreMessageAudit("", Audit(MessageAuditType.McpAccessRefused, """{"channel":"Mcp","reason":"client","clientId":"c9"}""", denied: true));
        await store.StoreMessageAudit("", Audit(MessageAuditType.McpAccessRefused, """{"channel":"Mcp","reason":"client","clientId":"c9"}""", denied: true));
        await store.StoreMessageAudit("", Audit(MessageAuditType.UpdateMcpSettings, """{"revision":"12345678-aaaa","turnOff":true,"changes":[]}"""));

        var activity = await new McpActivityService(store, TimeProvider.System).GetAsync(24);

        Assert.AreEqual(1, activity.Actions.Count());
        Assert.AreEqual("ERP back", activity.Actions.Single().Reason);
        Assert.AreEqual(2, activity.Refusals.Count());
        Assert.AreEqual(("c9", 2), (activity.RefusedClients.Single().ClientId, activity.RefusedClients.Single().Calls));
        Assert.AreEqual("Turned MCP access off", activity.Items.Single(i => i.Kind == "settings").Detail);
    }

    private static MessageAuditEntity Audit(MessageAuditType type, string data, bool denied = false) => new()
    {
        AuditorName = "Agent Operator",
        AuditTimestamp = DateTime.UtcNow,
        AuditType = type,
        AccessDenied = denied,
        Data = data,
    };

    private const string TokenRoute = "/test/csrf-token";

    private static (McpAccessSettingsService Service, InMemoryMessageStore Store, McpAccessPolicyProvider Provider) CreateService(Action<McpAccessSettings>? seed = null)
    {
        var store = new InMemoryMessageStore();
        if (seed is not null)
        {
            var settings = new McpAccessSettings { Revision = Guid.NewGuid().ToString() };
            seed(settings);
            Assert.IsTrue(store.TrySetMcpAccessSettings(settings, null).GetAwaiter().GetResult());
        }

        var options = Options.Create(new RateLimitOptions());
        var provider = new McpAccessPolicyProvider(store, options, NullLogger<McpAccessPolicyProvider>.Instance, TimeProvider.System);
        return (new McpAccessSettingsService(store, new TestCatalog(), options, TimeProvider.System, [provider]), store, provider);
    }

    private static async Task<JsonNode> GetStateAsync(HttpClient client, string url = "/api/admin/mcp/settings")
    {
        using var response = await client.GetAsync(url);
        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);

        // The test client keeps no cookies; the antiforgery token is bound to its cookie.
        if (response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", string.Join("; ", cookies.Select(c => c.Split(';')[0])));
        }

        return JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string url, JsonNode state, JsonNode? body)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Add("X-NimBus-CSRF", state["csrfToken"]!.GetValue<string>());
        if (body is not null)
            request.Content = JsonContent.Create(body);
        return client.SendAsync(request);
    }

    private static async Task<IHost> CreateApiHostAsync(InMemoryMessageStore store, RecordingAudit audit, bool owner)
    {
        var builder = new HostBuilder().ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddLogging();
            services.AddRouting();
            services.AddAuthorization();
            services.AddAuthentication(TestScheme.Name).AddScheme<AuthenticationSchemeOptions, TestScheme>(TestScheme.Name, null);
            services.AddAntiforgery(options => options.HeaderName = "X-NimBus-CSRF");
            services.AddHttpContextAccessor();
            services.AddSingleton<IEndpointMetadataStore>(store);
            services.AddSingleton<IMessageTrackingStore>(store);
            services.AddSingleton<IPlatform>(new TestCatalog());
            services.AddSingleton(TimeProvider.System);
            services.AddSingleton<IAuditLogService>(audit);
            services.AddSingleton<IEndpointAuthorizationService>(new OwnerAuthorization(owner));
            services.AddSingleton(new McpOperatorRuntime(McpAuthenticationMode.Disabled, new McpOperatorOptions()));
            services.AddScoped<McpAccessSettingsService>();
            services.AddScoped<McpActivityService>();
            services.AddTransient<NimBus.WebApp.ManagementApi.IMcpAccessApiController, McpAccessImplementation>();
            // As in production (Startup.Security): view features provide the antiforgery filters.
            services.AddControllersWithViews()
                .AddApplicationPart(typeof(McpAccessImplementation).Assembly)
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
        return await builder.StartAsync();
    }

    // Lets ForbidResult answer 403, as the WebApp's own schemes do.
    private sealed class TestScheme(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        public const string Name = "test";

        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.NoResult());
    }

    private sealed class TestCatalog : Platform
    {
        public TestCatalog()
        {
            AddEndpoint(new CrmEndpoint());
            AddEndpoint(new ErpEndpoint());
            AddEndpoint(new PayrollEndpoint());
        }
    }

    private sealed class CrmEndpoint : CoreEndpoint { }

    private sealed class ErpEndpoint : CoreEndpoint { }

    private sealed class PayrollEndpoint : CoreEndpoint { }

    private sealed class OwnerAuthorization(bool owner) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(owner);
        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);
        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess());
        public string? GetCurrentUserName() => "owner@example.com";
    }

    private sealed record Row(MessageAuditType Type, bool Denied, string? Data);

    private sealed class RecordingAudit : IAuditLogService
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

    private sealed class FlakyStore : InMemoryMessageStore
    {
        public bool Fail { get; set; }

        public override Task<McpAccessSettings> GetMcpAccessSettings()
            => Fail ? throw new InvalidOperationException("Storage is unreachable.") : base.GetMcpAccessSettings();
    }

    private sealed class MutableTime : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
