#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;
using NimBus.MessageStore;
using NimBus.MessageStore.States;
using NimBus.WebApp.Mcp;
using NimBus.WebApp.Mcp.Access;
using NimBus.WebApp.Mcp.Operations;
using NimBus.WebApp.RateLimiting;
using NimBus.WebApp.Services.AgentApiKeys;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>
/// Spec 052 §6.1 and §6.5 to §6.9: an MCP agent key is governed exactly like an app-only token,
/// behind its own Owner switch, and never skips, reads payloads or uses the delegated-only tools.
/// </summary>
[TestClass]
public sealed class McpAgentKeyGovernanceTests
{
    // ---- Admission ---------------------------------------------------------------------------

    [TestMethod]
    [DataRow(false, true, DisplayName = "Agent API keys switch off")]
    [DataRow(true, false, DisplayName = "unattended workloads off")]
    public async Task A_key_is_refused_with_reason_key_until_the_owner_allows_it(bool allowKeys, bool allowWorkloads)
    {
        await using var host = await McpTestHost.StartEntraAsync(new Dictionary<string, string?> { ["NimBus:Mcp:AgentKeys:Enabled"] = "true" });
        await McpAgentKeyAuthenticationTests.AllowKeysAsync(host, p => { p.AllowAgentKeys = allowKeys; p.AllowWorkloads = allowWorkloads; });
        var key = await McpAgentKeyAuthenticationTests.IssueAsync(host, "sre-agent");

        using var response = await host.PostToolsListAsync(r => r.Headers.Authorization = new("Bearer", key));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        var refusal = Refusals(host).Single();
        StringAssert.Contains(refusal.Data, "\"reason\":\"key\"", StringComparison.Ordinal);
        StringAssert.Contains(refusal.Data, $"\"agentKeyId\":\"{KeyIdOf(key)}\"", StringComparison.Ordinal);
        StringAssert.Contains(refusal.Data, "\"clientId\":\"agent:sre-agent\"", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Refusals_are_deduplicated_per_key_not_across_keys()
    {
        await using var host = await McpTestHost.StartEntraAsync(new Dictionary<string, string?> { ["NimBus:Mcp:AgentKeys:Enabled"] = "true" });
        var first = await McpAgentKeyAuthenticationTests.IssueAsync(host, "sre-agent");
        var second = await McpAgentKeyAuthenticationTests.IssueAsync(host, "sre-agent");

        foreach (var key in new[] { first, first, second })
            (await host.PostToolsListAsync(r => r.Headers.Authorization = new("Bearer", key))).Dispose();

        Assert.AreEqual(2, Refusals(host).Count);
    }

    [TestMethod]
    public async Task The_approved_client_list_does_not_block_a_key()
    {
        await using var host = await McpAgentKeyAuthenticationTests.StartKeysAsync();
        await McpAgentKeyAuthenticationTests.AllowKeysAsync(host, p => p.Clients.Mode = McpClientMode.Approved);
        var key = await McpAgentKeyAuthenticationTests.IssueAsync(host, "sre-agent");

        using var response = await host.PostToolsListAsync(r => r.Headers.Authorization = new("Bearer", key));

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [TestMethod]
    public void The_policy_admits_a_key_only_with_both_switches_and_ignores_the_client_and_people_lists()
    {
        var caller = McpCaller.From(KeyPrincipal("sre-agent", "abcdefghijkl"), McpAuthenticationMode.Entra);
        Assert.IsTrue(caller.IsWorkload);
        Assert.AreEqual("sre-agent", caller.AgentId);
        Assert.AreEqual("abcdefghijkl", caller.AgentKeyId);
        Assert.AreEqual("agent:sre-agent", caller.ClientId);

        var on = Policy(p =>
        {
            p.AllowAgentKeys = true;
            p.Clients.Mode = McpClientMode.Approved;
            p.People.Mode = McpPeopleMode.Listed;
        });
        Assert.IsNull(on.CheckCaller(caller));
        Assert.IsTrue(on.ClientMayChange(caller));

        Assert.AreEqual(McpRefusalReason.Key, Policy(_ => { }).CheckCaller(caller));
        Assert.AreEqual(McpRefusalReason.Key, Policy(p => { p.AllowAgentKeys = true; p.AllowWorkloads = false; }).CheckCaller(caller));
    }

    // ---- What a key may do -------------------------------------------------------------------

    [TestMethod]
    public async Task An_observe_only_key_reads_but_cannot_record_a_classification()
    {
        await using var host = await McpAgentKeyAuthenticationTests.StartKeysAsync(Contributor);
        await McpAgentKeyAuthenticationTests.AllowKeysAsync(host, p => p.AllowWorkloadClassification = true);
        var key = await McpAgentKeyAuthenticationTests.IssueAsync(host, "sre-agent");
        await using var client = await host.CreateClientAsync(key);

        var capabilities = await client.CallToolAsync("nimbus_get_capabilities", new Dictionary<string, object?>());
        var recorded = await client.CallToolAsync("nimbus_record_classification", RecordArguments());

        Assert.IsFalse(capabilities.IsError == true, ErrorText(capabilities));
        AssertError(recorded, "PermissionDenied");
    }

    [TestMethod]
    [DataRow(true, false, DisplayName = "grant without the Owner switch")]
    [DataRow(false, true, DisplayName = "Owner switch without the grant")]
    public async Task Recording_needs_both_the_grant_and_the_owner_switch(bool grant, bool ownerSwitch)
    {
        await using var host = await McpAgentKeyAuthenticationTests.StartKeysAsync(Contributor);
        await McpAgentKeyAuthenticationTests.AllowKeysAsync(host, p => p.AllowWorkloadClassification = ownerSwitch);
        var key = await McpAgentKeyAuthenticationTests.IssueAsync(host, "sre-agent", new AgentApiKeyMcpGrant { Classify = grant });
        await using var client = await host.CreateClientAsync(key);

        AssertError(await client.CallToolAsync("nimbus_record_classification", RecordArguments()), "PermissionDenied");
    }

    [TestMethod]
    [DataRow("nimbus_skip_message")]
    [DataRow("nimbus_settle_handoff")]
    [DataRow("nimbus_reprocess_deferred")]
    [DataRow("nimbus_skip_deferred_tracking")]
    [DataRow("nimbus_resubmit_with_changes")]
    public async Task A_key_with_every_permission_and_switch_still_cannot_use_the_delegated_only_tools(string tool)
    {
        await using var host = await McpAgentKeyAuthenticationTests.StartKeysAsync(services =>
            services.AddSingleton(new McpTestHost.StubAccess { Contributor = true, SiteReader = true, PiiReader = true }));
        await McpAgentKeyAuthenticationTests.AllowKeysAsync(host, p =>
        {
            p.AllowWorkloadClassification = p.AllowWorkloadAnnotation = p.AllowWorkloadResubmit = true;
            p.Capabilities.SettleHandoffs = p.Capabilities.RecoverDeferred = p.Capabilities.EditPayloads = true;
        });
        var key = await McpAgentKeyAuthenticationTests.IssueAsync(host, "sre-agent",
            new AgentApiKeyMcpGrant { Classify = true, Annotate = true, Resubmit = true });
        await using var client = await host.CreateClientAsync(key);

        var result = await client.CallToolAsync(tool, new Dictionary<string, object?>
        {
            ["endpointId"] = "CrmEndpoint", ["eventId"] = "evt-1", ["actionToken"] = "x", ["idempotencyKey"] = Guid.NewGuid().ToString(),
            ["reason"] = "test", ["sessionId"] = "s-1", ["outcome"] = "complete", ["eventContent"] = "{}",
        });

        AssertError(result, "PermissionDenied");
    }

    [TestMethod]
    public void A_key_never_holds_the_payload_scope_or_a_delegated_scope()
    {
        var key = KeyPrincipal("sre-agent", "abcdefghijkl", "observe", "classify", "annotate", "resubmit");

        Assert.IsFalse(McpOperatorPermissions.HasPayloadScope(key));
        foreach (var scope in McpOperatorPermissions.AllScopes)
            Assert.IsFalse(McpOperatorPermissions.HasDelegatedScope(key, scope), scope);
    }

    [TestMethod]
    public void A_key_holds_a_workload_role_only_through_its_grant()
    {
        var full = KeyPrincipal("sre-agent", "abcdefghijkl", "observe", "classify", "annotate", "resubmit");
        var observe = KeyPrincipal("sre-agent", "abcdefghijkl", "observe");

        foreach (var role in McpOperatorPermissions.AllRoles)
        {
            Assert.IsTrue(McpOperatorPermissions.IsWorkloadWithRole(full, role), role);
            Assert.AreEqual(role == McpOperatorPermissions.ObserveRole, McpOperatorPermissions.IsWorkloadWithRole(observe, role), role);
        }

        // The same claims on an identity of another scheme grant nothing.
        var forged = new ClaimsPrincipal(new ClaimsIdentity(full.Claims, "AuthenticationTypes.Federation"));
        foreach (var role in McpOperatorPermissions.AllRoles)
            Assert.IsFalse(McpOperatorPermissions.IsWorkloadWithRole(forged, role), role);
    }

    // ---- Identity, budget and rate partitions ------------------------------------------------

    [TestMethod]
    public void Action_tokens_and_the_budget_name_the_agent_so_sibling_keys_share_them()
    {
        var first = OperatorActionTokens.Identify(KeyPrincipal("sre-agent", "abcdefghijkl"));
        var sibling = OperatorActionTokens.Identify(KeyPrincipal("sre-agent", "mnopqrstuvwx"));
        var other = OperatorActionTokens.Identify(KeyPrincipal("triage-agent", "yzabcdefghij"));

        Assert.AreEqual(("agent:sre-agent", (string?)null, (string?)"agent:sre-agent"), first);
        Assert.AreEqual(first, sibling);
        Assert.AreNotEqual(first, other);
    }

    [TestMethod]
    public void Each_key_has_its_own_rate_partition_and_its_own_lower_limit()
    {
        var options = new RateLimitOptions();
        var first = Context(KeyPrincipal("sre-agent", "abcdefghijkl"));
        var sibling = Context(KeyPrincipal("sre-agent", "mnopqrstuvwx", limit: 5, "observe"));

        Assert.AreEqual("mcp:agent-key:abcdefghijkl", RateLimitingServiceCollectionExtensions.McpPartitionKey(first, options));
        Assert.AreEqual("mcp:agent-key:mnopqrstuvwx", RateLimitingServiceCollectionExtensions.McpPartitionKey(sibling, options));
        Assert.AreEqual(options.Mcp.PermitLimit, RateLimitingServiceCollectionExtensions.EffectiveMcpLimit(first, options));
        Assert.AreEqual(5, RateLimitingServiceCollectionExtensions.EffectiveMcpLimit(sibling, options));
    }

    // ---- Helpers -----------------------------------------------------------------------------

    private static void Contributor(IServiceCollection services)
        => services.AddSingleton(new McpTestHost.StubAccess { Contributor = true, SiteReader = true });

    internal static ClaimsPrincipal KeyPrincipal(string agentId, string keyId, params string[] permissions)
        => KeyPrincipal(agentId, keyId, limit: null, permissions);

    internal static ClaimsPrincipal KeyPrincipal(string agentId, string keyId, int? limit, params string[] permissions)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, "agent:" + agentId),
            new(ClaimTypes.Name, "agent:" + agentId),
            new(AgentApiKeyDefaults.AgentIdClaim, agentId),
            new(AgentApiKeyDefaults.KeyIdClaim, keyId),
        };
        foreach (var permission in permissions.Length == 0 ? new[] { "observe" } : permissions)
            claims.Add(new Claim(McpAgentKeyDefaults.PermissionClaim, permission));
        if (limit is { } value)
            claims.Add(new Claim(McpAgentKeyDefaults.RequestsPerWindowClaim, value.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, McpAgentKeyDefaults.SchemeName));
    }

    private static DefaultHttpContext Context(ClaimsPrincipal user)
        => new() { User = user, RequestServices = new ServiceCollection().BuildServiceProvider() };

    private static McpAccessPolicy Policy(Action<McpAccessSettings> configure)
    {
        var settings = new McpAccessSettings();
        configure(settings);
        return McpAccessPolicy.From(settings, new RateLimitOptions());
    }

    /// <summary>The key id is the twelve characters after <c>nbak_</c>.</summary>
    internal static string KeyIdOf(string key) => key.Substring(AgentApiKeyFormat.Prefix.Length, 12);

    private static List<McpTestHost.AuditRow> Refusals(McpTestHost host)
        => host.Get<McpTestHost.RecordingAuditLog>().Rows.Where(r => r.Type == MessageAuditType.McpAccessRefused && r.Denied).ToList();

    private static Dictionary<string, object?> RecordArguments() => new()
    {
        ["endpointId"] = "CrmEndpoint", ["eventId"] = "evt-1", ["messageId"] = "msg-1", ["idempotencyKey"] = Guid.NewGuid().ToString(),
        ["questionSetVersion"] = 1, ["category"] = "transient_dependency", ["categoryConfidence"] = 0.9,
        ["retryLikelihood"] = 0.9, ["changeRequiredLikelihood"] = 0.1, ["externalDependencyLikelihood"] = 0.9,
        ["faultOrigin"] = "external_system", ["faultOriginConfidence"] = 0.8, ["rationale"] = "timeout",
    };

    private static void AssertError(CallToolResult result, string code)
    {
        Assert.IsTrue(result.IsError == true, "Expected an error result: " + ErrorText(result));
        StringAssert.Contains(ErrorText(result), $"[{code}]", StringComparison.Ordinal);
    }

    private static string ErrorText(CallToolResult result)
        => string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));
}
