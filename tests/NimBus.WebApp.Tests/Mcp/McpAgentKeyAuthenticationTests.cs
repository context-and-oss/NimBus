#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Mcp;
using NimBus.WebApp.Services.AgentApiKeys;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>
/// Spec 052 §6.2, §6.4 and §6.10: how <c>/mcp</c> authenticates an agent API key issued for MCP.
/// The handler is exercised directly for its checks, and through the real MCP pipeline for the
/// routing between keys and Entra tokens.
/// </summary>
[TestClass]
public sealed class McpAgentKeyAuthenticationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 12, 0, 0, TimeSpan.Zero);

    // ---- The handler -------------------------------------------------------------------------

    [TestMethod]
    public async Task A_bearer_mcp_key_authenticates_as_its_agent_with_its_grant()
    {
        await using var host = await HandlerHost.CreateAsync();
        var key = await host.IssueAsync("sre-agent", grant: new AgentApiKeyMcpGrant { Classify = true, Resubmit = true, RequestsPerWindow = 5 });

        var result = await host.AuthenticateAsync(bearer: key.Key);

        Assert.IsTrue(result.Succeeded, result.Failure?.Message);
        var principal = result.Principal!;
        Assert.AreEqual(McpAgentKeyDefaults.SchemeName, principal.Identity!.AuthenticationType);
        Assert.AreEqual("agent:sre-agent", principal.FindFirstValue(ClaimTypes.NameIdentifier));
        Assert.AreEqual("agent:sre-agent", principal.Identity.Name);
        Assert.AreEqual("sre-agent", principal.FindFirstValue(AgentApiKeyDefaults.AgentIdClaim));
        Assert.AreEqual(key.KeyId, principal.FindFirstValue(AgentApiKeyDefaults.KeyIdClaim));
        CollectionAssert.AreEquivalent(
            new[] { "observe", "classify", "resubmit" },
            principal.FindAll(McpAgentKeyDefaults.PermissionClaim).Select(c => c.Value).ToArray());
        Assert.AreEqual("5", principal.FindFirstValue(McpAgentKeyDefaults.RequestsPerWindowClaim));
        // A key is a workload with no person, tenant, client or Entra role.
        foreach (var type in new[] { "scp", "roles", "oid", "tid", "azp", "appid", "groups", "email", "sub" })
            Assert.IsFalse(principal.HasClaim(c => c.Type == type), type);

        var caller = McpAgentKeyDefaults.Read(principal);
        Assert.IsNotNull(caller);
        Assert.AreEqual(Now.UtcDateTime.AddDays(30), caller.ExpiresAtUtc);
    }

    [TestMethod]
    public async Task The_custom_header_authenticates_too()
    {
        await using var host = await HandlerHost.CreateAsync();
        var key = await host.IssueAsync("sre-agent");

        var result = await host.AuthenticateAsync(header: key.Key);

        Assert.IsTrue(result.Succeeded, result.Failure?.Message);
    }

    [TestMethod]
    public async Task Two_credentials_on_one_request_fail()
    {
        await using var host = await HandlerHost.CreateAsync();
        var key = await host.IssueAsync("sre-agent");
        var other = await host.IssueAsync("sre-agent");

        Assert.IsNotNull((await host.AuthenticateAsync(header: key.Key, bearer: other.Key)).Failure, "header and bearer key");
        Assert.IsNotNull((await host.AuthenticateAsync(header: key.Key, authorization: "Bearer eyJhbGciOi.x.y")).Failure, "header and a token");
        Assert.IsNotNull((await host.AuthenticateAsync(header: key.Key, secondHeader: other.Key)).Failure, "two headers");
    }

    [TestMethod]
    public async Task An_agent_api_key_fails_on_mcp()
    {
        await using var host = await HandlerHost.CreateAsync();
        var key = await host.IssueAsync("enrichment-agent", customize: k => { k.Use = AgentApiKeyUse.AgentApi; k.Mcp = null; });

        Assert.IsNotNull((await host.AuthenticateAsync(bearer: key.Key)).Failure);
    }

    [TestMethod]
    [DataRow("malformed")]
    [DataRow("unknown")]
    [DataRow("revoked")]
    [DataRow("expired")]
    [DataRow("no expiry")]
    public async Task A_key_that_is_not_currently_valid_fails(string state)
    {
        await using var host = await HandlerHost.CreateAsync();
        var key = await host.IssueAsync("sre-agent", customize: k =>
        {
            if (state == "revoked") k.RevokedAtUtc = Now.UtcDateTime.AddMinutes(-1);
            if (state == "expired") k.ExpiresAtUtc = Now.UtcDateTime.AddSeconds(-1);
            if (state == "no expiry") k.ExpiresAtUtc = null;
        });
        var presented = state switch
        {
            "malformed" => "nbak_short",
            "unknown" => AgentApiKeyFormat.Generate().Key,
            _ => key.Key,
        };

        var result = await host.AuthenticateAsync(bearer: presented);

        Assert.IsNotNull(result.Failure, state);
    }

    [TestMethod]
    public async Task A_refusal_is_logged_with_the_key_id_and_never_the_secret()
    {
        await using var host = await HandlerHost.CreateAsync();
        var key = await host.IssueAsync("sre-agent", customize: k => k.RevokedAtUtc = Now.UtcDateTime.AddMinutes(-1));

        await host.AuthenticateAsync(bearer: key.Key);

        var lines = host.Logs.Where(l => l.Contains("refused", StringComparison.Ordinal)).ToList();
        Assert.IsTrue(lines.Any(l => l.Contains("key id " + key.KeyId, StringComparison.Ordinal)), string.Join(" | ", lines));
        // Neither the handler's line nor ASP.NET Core's own failure line carries the secret.
        Assert.IsFalse(host.Logs.Any(l => l.Contains(key.Key, StringComparison.Ordinal)), "the presented key must never be logged");
    }

    [TestMethod]
    public async Task A_key_list_older_than_five_minutes_refuses_every_key()
    {
        // §6.10: an instance that cannot read the key list keeps the last one, but only so long.
        var store = new SwitchableKeyStore();
        await using var host = await HandlerHost.CreateAsync(store);
        var key = await host.IssueAsync("sre-agent");
        Assert.IsTrue((await host.AuthenticateAsync(bearer: key.Key)).Succeeded, "fresh");

        store.Down = true;
        host.Time.Advance(TimeSpan.FromMinutes(4));
        Assert.IsTrue((await host.AuthenticateAsync(bearer: key.Key)).Succeeded, "stale but within the bound");

        host.Time.Advance(TimeSpan.FromMinutes(2));
        Assert.IsNotNull((await host.AuthenticateAsync(bearer: key.Key)).Failure, "past the five-minute bound");

        store.Down = false;
        host.Time.Advance(TimeSpan.FromSeconds(10));
        Assert.IsTrue((await host.AuthenticateAsync(bearer: key.Key)).Succeeded, "fresh again after a successful read");
    }

    [TestMethod]
    public async Task A_successful_authentication_records_the_key_use()
    {
        await using var host = await HandlerHost.CreateAsync();
        var key = await host.IssueAsync("sre-agent");

        Assert.IsTrue((await host.AuthenticateAsync(bearer: key.Key)).Succeeded);

        var usage = await host.Store.GetAgentApiKeyUsage();
        Assert.IsTrue(usage.Any(u => u.KeyId == key.KeyId));
    }

    [TestMethod]
    public void An_entra_token_identity_is_never_read_as_a_key()
    {
        // §6.4: key claims are read only from an identity of the key scheme.
        var forged = new ClaimsPrincipal(new ClaimsIdentity(
            new[]
            {
                new Claim(AgentApiKeyDefaults.AgentIdClaim, "sre-agent"),
                new Claim(AgentApiKeyDefaults.KeyIdClaim, "abcdefghijkl"),
                new Claim(McpAgentKeyDefaults.PermissionClaim, "observe"),
                new Claim("name", "agent:sre-agent"),
            },
            "AuthenticationTypes.Federation"));

        Assert.IsNull(McpAgentKeyDefaults.Read(forged));
        Assert.IsFalse(McpOperatorPermissions.CanObserve(forged));
    }

    // ---- Routing on /mcp ---------------------------------------------------------------------

    [TestMethod]
    public async Task A_bearer_key_and_a_header_key_both_reach_the_tools()
    {
        await using var host = await StartKeysAsync();
        var key = await IssueAsync(host, "sre-agent");

        using var bearer = await host.PostToolsListAsync(r => r.Headers.Authorization = new("Bearer", key));
        using var header = await host.PostToolsListAsync(r => r.Headers.Add(AgentApiKeyDefaults.HeaderName, key));

        Assert.AreEqual(HttpStatusCode.OK, bearer.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, header.StatusCode);
    }

    [TestMethod]
    public async Task Entra_tokens_keep_working_next_to_keys()
    {
        await using var host = await StartKeysAsync();

        using var response = await host.PostToolsListAsync(r => r.Headers.Authorization = new("Bearer", McpTestHost.CreateToken()));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public async Task A_refused_key_gets_the_mcp_challenge()
    {
        await using var host = await StartKeysAsync();

        using var response = await host.PostToolsListAsync(r => r.Headers.Authorization = new("Bearer", AgentApiKeyFormat.Generate().Key));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        StringAssert.Contains(string.Join(" ", response.Headers.WwwAuthenticate), "resource_metadata=", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task With_the_deployment_ceiling_off_a_key_gets_401_and_tokens_are_unchanged()
    {
        await using var host = await McpTestHost.StartEntraAsync();
        await AllowKeysAsync(host);
        var key = await IssueAsync(host, "sre-agent");

        using var bearer = await host.PostToolsListAsync(r => r.Headers.Authorization = new("Bearer", key));
        using var header = await host.PostToolsListAsync(r => r.Headers.Add(AgentApiKeyDefaults.HeaderName, key));
        using var token = await host.PostToolsListAsync(r => r.Headers.Authorization = new("Bearer", McpTestHost.CreateToken()));

        Assert.AreEqual(HttpStatusCode.Unauthorized, bearer.StatusCode);
        Assert.AreEqual(HttpStatusCode.Unauthorized, header.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, token.StatusCode);
    }

    // ---- Startup wiring ----------------------------------------------------------------------

    [TestMethod]
    public async Task The_key_scheme_exists_only_in_entra_mode_with_the_ceiling_on()
    {
        await using (var on = await StartKeysAsync())
            Assert.IsNotNull(await on.Get<IAuthenticationSchemeProvider>().GetSchemeAsync(McpAgentKeyDefaults.SchemeName));

        await using (var off = await McpTestHost.StartEntraAsync())
            Assert.IsNull(await off.Get<IAuthenticationSchemeProvider>().GetSchemeAsync(McpAgentKeyDefaults.SchemeName));

        await using (var local = await McpTestHost.StartLocalDevelopmentAsync())
            Assert.IsNull(await local.Get<IAuthenticationSchemeProvider>().GetSchemeAsync(McpAgentKeyDefaults.SchemeName));
    }

    [TestMethod]
    public async Task Only_the_mcp_policy_names_the_mcp_authentication_scheme()
    {
        await using var host = await StartKeysAsync();

        var policy = await host.Get<IAuthorizationPolicyProvider>().GetPolicyAsync(McpOperatorPolicies.Observe);

        CollectionAssert.AreEqual(new[] { McpAuthenticationSchemes.Auth }, policy!.AuthenticationSchemes.ToArray());
    }

    // ---- Helpers -----------------------------------------------------------------------------

    internal static async Task<McpTestHost> StartKeysAsync(Action<IServiceCollection>? configureServices = null)
    {
        var host = await McpTestHost.StartEntraAsync(new Dictionary<string, string?> { ["NimBus:Mcp:AgentKeys:Enabled"] = "true" }, configureServices);
        await AllowKeysAsync(host);
        return host;
    }

    /// <summary>Saves a policy with agent keys allowed, before the first request loads it.</summary>
    internal static async Task AllowKeysAsync(McpTestHost host, Action<McpAccessSettings>? customize = null)
    {
        var store = host.Get<IEndpointMetadataStore>();
        var current = await store.GetMcpAccessSettings();
        var next = current.Clone();
        next.AllowAgentKeys = true;
        customize?.Invoke(next);
        next.Revision = Guid.NewGuid().ToString();
        Assert.IsTrue(await store.TrySetMcpAccessSettings(next, current.Revision));
    }

    /// <summary>Issues an MCP key straight into the store and returns its full value.</summary>
    internal static async Task<string> IssueAsync(McpTestHost host, string agentId, AgentApiKeyMcpGrant? grant = null, Action<AgentApiKey>? customize = null)
    {
        var generated = AgentApiKeyFormat.Generate();
        var record = new AgentApiKey
        {
            KeyId = generated.KeyId,
            AgentId = agentId,
            SecretHash = generated.SecretHash,
            CreatedAtUtc = DateTime.UtcNow.AddDays(-1),
            ExpiresAtUtc = DateTime.UtcNow.AddDays(30),
            Use = AgentApiKeyUse.Mcp,
            Mcp = grant ?? new AgentApiKeyMcpGrant(),
        };
        customize?.Invoke(record);

        var store = host.Get<IEndpointMetadataStore>();
        var current = await store.GetAgentApiKeySettings();
        var next = current.Clone();
        next.Keys.Add(record);
        next.Revision = Guid.NewGuid().ToString();
        Assert.IsTrue(await store.TrySetAgentApiKeySettings(next, current.Revision));
        host.Find<IAgentApiKeyProvider>()?.Accept(next);
        return generated.Key;
    }

    private sealed class HandlerHost : IAsyncDisposable
    {
        private readonly ServiceProvider _services;

        private HandlerHost(ServiceProvider services, IEndpointMetadataStore store, FakeTimeProvider time, ListLogger logs)
        {
            _services = services;
            Store = store;
            Time = time;
            Logs = logs.Lines;
        }

        public IEndpointMetadataStore Store { get; }

        public FakeTimeProvider Time { get; }

        public ConcurrentQueue<string> Logs { get; }

        public static Task<HandlerHost> CreateAsync(IEndpointMetadataStore? store = null)
        {
            store ??= new InMemoryMessageStore();
            var time = new FakeTimeProvider(Now);
            var logs = new ListLogger();
            var services = new ServiceCollection();
            services.AddLogging(b => b.AddProvider(logs));
            services.AddSingleton<TimeProvider>(time);
            services.AddSingleton(store);
            services.AddSingleton<IAgentApiKeyProvider, AgentApiKeyProvider>();
            services.AddSingleton<IAgentApiKeyUsageRecorder, AgentApiKeyUsageRecorder>();
            services.AddAuthentication()
                .AddScheme<AuthenticationSchemeOptions, McpAgentKeyAuthenticationHandler>(McpAgentKeyDefaults.SchemeName, null);
            return Task.FromResult(new HandlerHost(services.BuildServiceProvider(), store, time, logs));
        }

        public async Task<GeneratedAgentApiKey> IssueAsync(string agentId, AgentApiKeyMcpGrant? grant = null, Action<AgentApiKey>? customize = null)
        {
            var generated = AgentApiKeyFormat.Generate();
            var record = new AgentApiKey
            {
                KeyId = generated.KeyId,
                AgentId = agentId,
                SecretHash = generated.SecretHash,
                CreatedAtUtc = Now.UtcDateTime.AddDays(-1),
                ExpiresAtUtc = Now.UtcDateTime.AddDays(30),
                Use = AgentApiKeyUse.Mcp,
                Mcp = grant ?? new AgentApiKeyMcpGrant(),
            };
            customize?.Invoke(record);

            var current = await Store.GetAgentApiKeySettings();
            var next = current.Clone();
            next.Keys.Add(record);
            next.Revision = Guid.NewGuid().ToString();
            Assert.IsTrue(await Store.TrySetAgentApiKeySettings(next, current.Revision));
            _services.GetRequiredService<IAgentApiKeyProvider>().Accept(next);
            return generated;
        }

        public async Task<AuthenticateResult> AuthenticateAsync(string? bearer = null, string? header = null, string? authorization = null, string? secondHeader = null)
        {
            await using var scope = _services.CreateAsyncScope();
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            if (header is not null)
                context.Request.Headers[AgentApiKeyDefaults.HeaderName] = secondHeader is null ? header : new Microsoft.Extensions.Primitives.StringValues(new[] { header, secondHeader });
            if (bearer is not null)
                context.Request.Headers.Authorization = "Bearer " + bearer;
            if (authorization is not null)
                context.Request.Headers.Authorization = authorization;
            return await context.AuthenticateAsync(McpAgentKeyDefaults.SchemeName);
        }

        public ValueTask DisposeAsync() => _services.DisposeAsync();
    }

    private sealed class SwitchableKeyStore : InMemoryMessageStore
    {
        public bool Down { get; set; }

        public override Task<AgentApiKeySettings> GetAgentApiKeySettings()
            => Down ? throw new InvalidOperationException("store down") : base.GetAgentApiKeySettings();
    }

    private sealed class ListLogger : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Lines.Enqueue(formatter(state, exception));

        public void Dispose()
        {
        }
    }
}
