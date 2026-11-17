#pragma warning disable CA1707, CA2007
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore;
using NimBus.WebApp.Controllers;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.IntegrationIntelligence;

namespace NimBus.WebApp.Tests;

[TestClass]
public sealed class IntelligenceSettingsApiTests
{
    [TestMethod]
    public async Task Api_Requires_SiteOwner_Csrf_Consent_And_Current_Revision()
    {
        var store = new FakeStore();
        var audit = new FakeAudit();
        using var host = await CreateHost(store, audit);
        using var client = host.GetTestClient();
        Assert.AreEqual(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/admin/failure-intelligence")).StatusCode);
        client.DefaultRequestHeaders.Add("TestRole", "Reader");
        Assert.AreEqual(HttpStatusCode.Forbidden, (await client.GetAsync("/api/admin/failure-intelligence")).StatusCode);
        Assert.AreEqual(0, store.Reads);
        client.DefaultRequestHeaders.Remove("TestRole");
        client.DefaultRequestHeaders.Add("TestRole", "Owner");
        var response = await client.GetAsync("/api/admin/failure-intelligence");
        var text = await response.Content.ReadAsStringAsync();
        Assert.IsFalse(text.Contains("deployment-secret", StringComparison.Ordinal));
        var state = JsonNode.Parse(text)!;
        var update = new SaveIntelligenceSettings { Revision = "none", Settings = new() { Enabled = true, IncludeEventPayload = true } };
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/admin/failure-intelligence", update)).StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
        client.DefaultRequestHeaders.Add("X-NimBus-CSRF", state["csrfToken"]!.GetValue<string>());
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/admin/failure-intelligence", update)).StatusCode);
        update.PayloadSharingAcknowledged = true;
        var saved = await client.PutAsJsonAsync("/api/admin/failure-intelligence", update);
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
        var savedState = JsonNode.Parse(await saved.Content.ReadAsStringAsync())!;
        Assert.IsTrue(savedState["restartRequired"]!.GetValue<bool>());
        Assert.IsFalse(savedState["active"]!["includeEventPayload"]!.GetValue<bool>());
        Assert.IsTrue(savedState["saved"]!["includeEventPayload"]!.GetValue<bool>());
        Assert.AreEqual(HttpStatusCode.Conflict, (await client.PutAsJsonAsync("/api/admin/failure-intelligence", update)).StatusCode);
        Assert.AreEqual(1, store.Writes);
        Assert.AreEqual(4, audit.Writes);
        var configuration = host.Services.GetRequiredService<IConfiguration>();
        var restarted = await IntelligenceSettingsBootstrap.LoadAsync(configuration, store, CancellationToken.None);
        Assert.IsTrue(IntelligenceAdminSettings.FromConfiguration(restarted).IncludeEventPayload);
        Assert.AreEqual(store.Document!.Revision, restarted[IntelligenceSettingsBootstrap.RevisionKey]);
    }

    [TestMethod]
    public async Task Storage_Failure_Is_Sanitized_And_Unknown_Fields_Are_Rejected()
    {
        var store = new FakeStore();
        using var host = await CreateHost(store, new FakeAudit());
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("TestRole", "Owner");
        var response = await client.GetAsync("/api/admin/failure-intelligence");
        var state = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
        client.DefaultRequestHeaders.Add("X-NimBus-CSRF", state["csrfToken"]!.GetValue<string>());
        var body = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new SaveIntelligenceSettings { Settings = new(), Revision = "none" }))!;
        body["BaseUrl"] = "https://must-not-be-stored.example";
        Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/admin/failure-intelligence", body)).StatusCode);
        Assert.AreEqual(0, store.Writes);
        store.Throw = true;
        response = await client.GetAsync("/api/admin/failure-intelligence");
        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.IsFalse((await response.Content.ReadAsStringAsync()).Contains("secret", StringComparison.Ordinal));
        var failed = IntelligenceSettingsBootstrap.FailClosed(host.Services.GetRequiredService<IConfiguration>());
        Assert.IsFalse(failed.GetValue<bool>("NimBus:IntegrationIntelligence:Enabled"));
    }

    [TestMethod]
    public async Task Api_Rejects_A_Provider_Key_And_Reports_No_Credential_State()
    {
        // v5.0.0 removed the built-in provider, its key storage and the key fields of this API.
        const string url = "/api/admin/failure-intelligence";
        var store = new FakeStore();
        var audit = new FakeAudit();
        using var host = await CreateHost(store, audit);
        using var client = host.GetTestClient();
        client.DefaultRequestHeaders.Add("TestRole", "Owner");
        var response = await client.GetAsync(url);
        var state = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        foreach (var removed in new[] { "credentialConfigured", "credentialSource", "savedApiKey" })
            Assert.IsNull(state[removed], removed);
        Assert.IsNull(state["saved"]!["model"]);
        Assert.IsNull(state["saved"]!["timeoutSeconds"]);
        client.DefaultRequestHeaders.Add("Cookie", response.Headers.GetValues("Set-Cookie").First().Split(';')[0]);
        client.DefaultRequestHeaders.Add("X-NimBus-CSRF", state["csrfToken"]!.GetValue<string>());

        foreach (var field in new[] { "ApiKey", "ClearApiKey" })
        {
            var body = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(new SaveIntelligenceSettings { Settings = new(), Revision = "none" }))!;
            body[field] = field == "ApiKey" ? "ts-live-key-1234567890" : true;
            Assert.AreEqual(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync(url, body)).StatusCode, field);
        }
        Assert.AreEqual(0, store.Writes);

        var saved = await client.PutAsJsonAsync(url, new SaveIntelligenceSettings { Revision = "none", Settings = new() { Enabled = true } });
        Assert.AreEqual(HttpStatusCode.OK, saved.StatusCode, await saved.Content.ReadAsStringAsync());
        Assert.AreEqual("{\"outcome\":\"Saved\"}", audit.Data.Last());
    }

    private static Task<IHost> CreateHost(FakeStore store, FakeAudit audit) => new HostBuilder()
        .ConfigureAppConfiguration(builder => builder.AddInMemoryCollection(new Dictionary<string, string?>
        { ["NimBus:IntegrationIntelligence:FailureClassification:TypeSafe:ApiKey"] = "deployment-secret" }))
        .ConfigureWebHost(web => web.UseTestServer().ConfigureServices(services =>
        {
            services.AddLogging(); services.AddRouting(); services.AddAuthorization();
            services.AddAntiforgery(options => options.HeaderName = "X-NimBus-CSRF");
            services.AddSingleton<IIntelligenceSettingsStore>(store); services.AddSingleton<IAuditLogService>(audit);
            services.AddSingleton(sp => IntelligenceSettingsSnapshot.Create(sp.GetRequiredService<IConfiguration>()));
            services.AddHttpContextAccessor(); services.AddScoped<IEndpointAuthorizationService, FakeAuthorization>();
            services.AddControllers().AddApplicationPart(typeof(IntelligenceSettingsController).Assembly);
        }).Configure(app =>
        {
            app.Use(async (context, next) =>
            {
                var role = context.Request.Headers["TestRole"].ToString();
                if (role.Length == 0) { context.Response.StatusCode = 401; return; }
                context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, "test"), new Claim(ClaimTypes.Role, role)], "test"));
                await next(context);
            });
            app.UseRouting(); app.UseAuthorization(); app.UseEndpoints(e => e.MapControllers());
        })).StartAsync();

    private sealed class FakeAuthorization(IHttpContextAccessor accessor) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(accessor.HttpContext!.User.IsInRole("Owner"));
        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);
        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => throw new NotSupportedException();
        public string? GetCurrentUserName() => "test";
    }
    private sealed class FakeAudit : IAuditLogService
    {
        public int Writes { get; private set; }
        public List<string?> Data { get; } = [];
        public Task LogAuditAsync(MessageAuditType type, HttpContext context, bool accessDenied = false, string? data = null,
            string? eventId = null, string? endpointId = null, string? eventTypeId = null, string? auditorNameOverride = null,
            CancellationToken cancellationToken = default) { Writes++; Data.Add(data); return Task.CompletedTask; }
    }
    private sealed class FakeStore : IIntelligenceSettingsStore
    {
        public IntelligenceSettingsDocument? Document { get; private set; }
        public void Seed(IntelligenceSettingsDocument document) => Document = document;
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public bool Throw { get; set; }
        public Task<IntelligenceSettingsDocument?> ReadAsync(CancellationToken cancellationToken)
        { Reads++; if (Throw) throw new InvalidOperationException("secret connection details"); return Task.FromResult(Document); }
        public Task<bool> TrySaveAsync(IntelligenceSettingsDocument document, string expectedRevision, CancellationToken cancellationToken)
        {
            if ((Document?.Revision ?? "none") != expectedRevision) return Task.FromResult(false);
            Document = document; Writes++; return Task.FromResult(true);
        }
    }
}
