#pragma warning disable CA1707, CA2007
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Protocol;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>
/// The operator MCP endpoint in each authentication mode: disabled, local Aspire development
/// (the local-dev bypass, no sign-in) and Entra (bearer tokens for the MCP resource).
/// </summary>
[TestClass]
public class McpOperatorEndpointTests
{
    private static readonly string[] ReadTools =
    [
        "nimbus_get_capabilities", "nimbus_list_endpoints", "nimbus_get_overview", "nimbus_get_endpoint",
        "nimbus_find_messages", "nimbus_get_message", "nimbus_get_message_history", "nimbus_get_session",
        "nimbus_search_messages", "nimbus_get_metrics", "nimbus_get_classification", "nimbus_prepare_action",
        "nimbus_get_failure_evidence",
    ];

    // Spec 035 Phase 2a and Spec 044. Listed for every caller; each checks its own scope and role.
    private static readonly string[] WriteTools =
    [
        "nimbus_resubmit_message", "nimbus_skip_message", "nimbus_set_message_reported", "nimbus_classify_failure",
        "nimbus_record_classification",
    ];

    private static readonly string[] OperatorTools = [.. ReadTools, .. WriteTools];

    [TestMethod]
    public async Task Disabled_does_not_map_the_endpoint()
    {
        await using var host = await McpTestHost.StartDisabledAsync();

        using var response = await host.PostToolsListAsync();

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Aspire_setting_serves_the_endpoint_when_the_local_dev_bypass_is_on()
    {
        await using var host = await McpTestHost.StartAspireAsync(localDevBypass: true);
        await using var client = await host.CreateClientAsync();

        var tools = await client.ListToolsAsync();

        CollectionAssert.AreEquivalent(OperatorTools, tools.Select(t => t.Name).ToArray());
    }

    [TestMethod]
    public async Task Aspire_setting_leaves_the_endpoint_unmapped_without_the_local_dev_bypass()
    {
        await using var host = await McpTestHost.StartAspireAsync(localDevBypass: false);

        using var response = await host.PostToolsListAsync();

        Assert.AreEqual(HttpStatusCode.NotFound, response.StatusCode);
    }

    [TestMethod]
    public async Task Local_development_lists_exactly_the_operator_tools_without_sign_in()
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync();
        await using var client = await host.CreateClientAsync();

        var tools = await client.ListToolsAsync();

        CollectionAssert.AreEquivalent(OperatorTools, tools.Select(t => t.Name).ToArray());
    }

    [TestMethod]
    public async Task Read_tools_are_read_only_and_write_tools_say_what_they_change()
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync();
        await using var client = await host.CreateClientAsync();

        var tools = (await client.ListToolsAsync()).ToDictionary(t => t.Name, t => t.ProtocolTool.Annotations);

        Assert.IsTrue(ReadTools.All(name => tools[name]?.ReadOnlyHint == true));
        Assert.IsTrue(WriteTools.All(name => tools[name]?.ReadOnlyHint == false));
        Assert.IsTrue(tools["nimbus_skip_message"]?.DestructiveHint == true, "Skip can release later messages and is never undone.");
        Assert.IsTrue(tools["nimbus_resubmit_message"]?.DestructiveHint == false);
    }

    [TestMethod]
    public async Task List_endpoints_returns_only_endpoints_the_caller_can_read()
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync();
        await using var client = await host.CreateClientAsync();

        var result = await client.CallToolAsync("nimbus_list_endpoints");

        Assert.IsFalse(result.IsError == true);
        var json = StructuredContent(result);
        var endpoints = json.GetProperty("endpoints").EnumerateArray().ToList();
        CollectionAssert.AreEquivalent(
            McpTestHost.ReadableEndpoints,
            endpoints.Select(e => e.GetProperty("id").GetString()).ToArray());

        var crm = endpoints.Single(e => e.GetProperty("id").GetString() == "CrmEndpoint");
        CollectionAssert.AreEqual(
            new[] { "CustomerChanged" },
            crm.GetProperty("eventTypesProduced").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.AreEqual("test", json.GetProperty("environment").GetString());
    }

    [TestMethod]
    public async Task Capabilities_report_the_environment_mode_and_readable_endpoints()
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync();
        await using var client = await host.CreateClientAsync();

        var result = await client.CallToolAsync("nimbus_get_capabilities");

        var json = StructuredContent(result);
        Assert.AreEqual("test", json.GetProperty("environment").GetString());
        Assert.AreEqual("LocalDevelopment", json.GetProperty("authenticationMode").GetString());
        Assert.AreEqual("Local Developer", json.GetProperty("caller").GetProperty("name").GetString());
        CollectionAssert.AreEquivalent(
            McpTestHost.ReadableEndpoints,
            json.GetProperty("readableEndpoints").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.AreEqual(0, json.GetProperty("permittedActions").GetArrayLength());
    }

    [TestMethod]
    public async Task Local_development_accepts_a_localhost_origin()
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync();
        await using var client = await host.CreateClientAsync(origin: "http://localhost:6274");

        var tools = await client.ListToolsAsync();

        Assert.AreEqual(OperatorTools.Length, tools.Count);
    }

    [TestMethod]
    public async Task Local_development_rejects_a_foreign_origin()
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync();

        using var response = await host.PostToolsListAsync(r => r.Headers.Add("Origin", "https://attacker.example"));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task Local_development_rejects_a_non_loopback_caller()
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync();

        using var response = await host.PostToolsListAsync(r => r.Headers.Add(McpTestHost.RemoteAddressHeader, "10.1.2.3"));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    public async Task Entra_without_a_token_is_challenged_with_resource_metadata()
    {
        await using var host = await McpTestHost.StartEntraAsync();

        using var response = await host.PostToolsListAsync();

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        var challenge = string.Join(" ", response.Headers.WwwAuthenticate.Select(h => h.ToString()));
        StringAssert.Contains(challenge, "resource_metadata=", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Entra_serves_the_protected_resource_metadata()
    {
        await using var host = await McpTestHost.StartEntraAsync();

        using var response = await host.Server.CreateClient().GetAsync("/.well-known/oauth-protected-resource/mcp");

        Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var servers = document.RootElement.GetProperty("authorization_servers").EnumerateArray().Select(e => e.GetString()).ToArray();
        CollectionAssert.Contains(servers, McpTestHost.Issuer);
        var scopes = document.RootElement.GetProperty("scopes_supported").EnumerateArray().Select(e => e.GetString()).ToArray();
        foreach (var scope in new[] { "nimbus.observe", "nimbus.payload.read", "nimbus.resubmit", "nimbus.skip", "nimbus.annotate", "nimbus.classify" })
            CollectionAssert.Contains(scopes, $"api://{McpTestHost.ClientId}/{scope}");
    }

    [TestMethod]
    public async Task Entra_rejects_a_token_for_another_audience()
    {
        await using var host = await McpTestHost.StartEntraAsync();
        var token = McpTestHost.CreateToken(audience: "https://graph.microsoft.com");

        using var response = await host.PostToolsListAsync(r => r.Headers.Authorization = new("Bearer", token));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Entra_rejects_a_token_from_another_issuer()
    {
        await using var host = await McpTestHost.StartEntraAsync();
        var token = McpTestHost.CreateToken(issuer: "https://login.microsoftonline.com/99999999-9999-9999-9999-999999999999/v2.0");

        using var response = await host.PostToolsListAsync(r => r.Headers.Authorization = new("Bearer", token));

        Assert.AreEqual(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [TestMethod]
    public async Task Entra_forbids_a_token_without_the_observe_permission()
    {
        await using var host = await McpTestHost.StartEntraAsync();
        var token = McpTestHost.CreateToken(scopes: "access_as_user");

        using var response = await host.PostToolsListAsync(r => r.Headers.Authorization = new("Bearer", token));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [TestMethod]
    [DataRow("nimbus.observe", null)]
    [DataRow(null, "Nimbus.Observe")]
    public async Task Entra_lists_tools_for_a_delegated_scope_or_workload_role(string? scopes, string? roles)
    {
        await using var host = await McpTestHost.StartEntraAsync();
        var token = McpTestHost.CreateToken(audience: $"api://{McpTestHost.ClientId}", scopes: scopes, roles: roles);
        await using var client = await host.CreateClientAsync(token);

        var tools = await client.ListToolsAsync();

        CollectionAssert.AreEquivalent(OperatorTools, tools.Select(t => t.Name).ToArray());
    }

    [TestMethod]
    public async Task Entra_accepts_a_configured_origin_and_rejects_others()
    {
        await using var host = await McpTestHost.StartEntraAsync();
        var token = McpTestHost.CreateToken();

        using var allowed = await host.PostToolsListAsync(r =>
        {
            r.Headers.Authorization = new("Bearer", token);
            r.Headers.Add("Origin", "https://agents.example");
        });
        using var localhost = await host.PostToolsListAsync(r =>
        {
            r.Headers.Authorization = new("Bearer", token);
            r.Headers.Add("Origin", "http://localhost:6274");
        });

        Assert.AreEqual(HttpStatusCode.OK, allowed.StatusCode);
        Assert.AreEqual(HttpStatusCode.Forbidden, localhost.StatusCode);
    }

    private static JsonElement StructuredContent(CallToolResult result)
    {
        Assert.IsNotNull(result.StructuredContent, "The tool must return structured content.");
        return JsonSerializer.SerializeToElement(result.StructuredContent);
    }
}
