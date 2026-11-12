#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>
/// Spec 037: the site Owner's MCP access policy over the real MCP pipeline. The policy is
/// seeded into the store before the first call, which loads it.
/// </summary>
[TestClass]
public class McpAccessPolicyEnforcementTests
{
    private static readonly string[] ChangeTools =
    [
        "nimbus_prepare_action", "nimbus_resubmit_message", "nimbus_skip_message",
        "nimbus_set_message_reported", "nimbus_classify_failure", "nimbus_record_classification",
    ];

    [TestMethod]
    public async Task A_turned_off_endpoint_answers_503_Disabled()
    {
        await using var host = await StartLocalAsync(p => p.Enabled = false);

        using var response = await host.PostToolsListAsync();

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "[Disabled]", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task An_unreadable_policy_answers_503_Unavailable_instead_of_falling_back_to_defaults()
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync(services =>
            services.AddSingleton<IEndpointMetadataStore>(new UnreadablePolicyStore()));

        using var response = await host.PostToolsListAsync();

        Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        StringAssert.Contains(await response.Content.ReadAsStringAsync(), "[Unavailable]", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Switched_off_capabilities_are_hidden_from_the_tool_list()
    {
        await using var host = await StartLocalAsync(p =>
        {
            p.Capabilities.Skip = false;
            p.Capabilities.Classify = false;
        });
        await using var client = await host.CreateClientAsync();

        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();

        CollectionAssert.DoesNotContain(tools, "nimbus_skip_message");
        CollectionAssert.DoesNotContain(tools, "nimbus_classify_failure");
        CollectionAssert.Contains(tools, "nimbus_resubmit_message");
        CollectionAssert.Contains(tools, "nimbus_prepare_action");
        CollectionAssert.Contains(tools, "nimbus_get_message");
    }

    [TestMethod]
    public async Task Prepare_action_is_hidden_when_both_resubmit_and_skip_are_off()
    {
        await using var host = await StartLocalAsync(p =>
        {
            p.Capabilities.Resubmit = false;
            p.Capabilities.Skip = false;
        });
        await using var client = await host.CreateClientAsync();

        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();

        CollectionAssert.DoesNotContain(tools, "nimbus_prepare_action");
        CollectionAssert.Contains(tools, "nimbus_set_message_reported");
    }

    [TestMethod]
    public async Task A_direct_call_to_a_hidden_tool_is_refused_and_audited_once_per_window()
    {
        await using var host = await StartLocalAsync(p => p.Capabilities.Report = false);
        await using var client = await host.CreateClientAsync();

        var first = await client.CallToolAsync("nimbus_set_message_reported", ReportArguments());
        var second = await client.CallToolAsync("nimbus_set_message_reported", ReportArguments());

        AssertError(first, "PermissionDenied", "turned off by an administrator");
        AssertError(second, "PermissionDenied", "turned off by an administrator");
        var refusals = Refusals(host);
        Assert.AreEqual(1, refusals.Count, "A repeated refusal is audited once per window.");
        StringAssert.Contains(refusals[0].Data, "\"reason\":\"tool\"", StringComparison.Ordinal);
        StringAssert.Contains(refusals[0].Data, "nimbus_set_message_reported", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Preparing_a_switched_off_action_is_refused_while_the_other_stays_available()
    {
        await using var host = await StartLocalAsync(p => p.Capabilities.Skip = false);
        await using var client = await host.CreateClientAsync();

        var result = await client.CallToolAsync("nimbus_prepare_action", new Dictionary<string, object?>
        {
            ["action"] = "skip", ["endpointId"] = "CrmEndpoint", ["eventId"] = "evt-1", ["messageVersion"] = "v",
        });

        AssertError(result, "PermissionDenied", "skip is turned off by an administrator");
    }

    [TestMethod]
    public async Task Capabilities_report_only_the_permitted_actions_and_the_lowered_limit()
    {
        await using var host = await StartLocalAsync(p =>
        {
            p.Capabilities.Skip = false;
            p.Limits.RequestsPerWindow = 12;
        });
        await using var client = await host.CreateClientAsync();

        var json = await CallAsync(client, "nimbus_get_capabilities", new());

        var permitted = json.GetProperty("permittedActions").EnumerateArray().Select(e => e.GetString()).ToList();
        CollectionAssert.DoesNotContain(permitted, "skip");
        CollectionAssert.Contains(permitted, "resubmit");
        Assert.AreEqual(12, json.GetProperty("limits").GetProperty("requestsPerWindow").GetInt32());
    }

    [TestMethod]
    public async Task Hidden_endpoints_are_left_out_and_answer_endpoint_not_found()
    {
        await using var host = await StartLocalAsync(p =>
        {
            p.Endpoints.Visibility = McpEndpointVisibility.AllExcept;
            p.Endpoints.Hidden = ["ErpEndpoint"];
        });
        await using var client = await host.CreateClientAsync();

        var listed = await CallAsync(client, "nimbus_list_endpoints", new());
        var ids = listed.GetProperty("endpoints").EnumerateArray().Select(e => e.GetProperty("id").GetString()).ToList();
        CollectionAssert.AreEqual(new[] { "CrmEndpoint" }, ids);

        var hidden = await client.CallToolAsync("nimbus_get_endpoint", new Dictionary<string, object?> { ["endpointId"] = "erpendpoint" });
        AssertError(hidden, "EndpointNotFound", "erpendpoint");
    }

    [TestMethod]
    public async Task Changes_outside_the_listed_endpoints_are_refused()
    {
        await using var host = await StartLocalAsync(p =>
        {
            p.Endpoints.Changes = McpChangeScope.Listed;
            p.Endpoints.ChangeOn = ["ErpEndpoint"];
        });
        await using var client = await host.CreateClientAsync();

        var result = await client.CallToolAsync("nimbus_set_message_reported", ReportArguments());

        AssertError(result, "PermissionDenied", "Changes are not allowed on endpoint 'CrmEndpoint'");
        StringAssert.Contains(Refusals(host).Single().Data, "\"reason\":\"endpoint\"", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Switched_off_payloads_are_refused_with_their_own_reason()
    {
        await using var host = await StartLocalAsync(p => p.Capabilities.Payloads = false, piiReader: true);
        await using var client = await host.CreateClientAsync();

        var result = await client.CallToolAsync("nimbus_get_message", new Dictionary<string, object?>
        {
            ["endpointId"] = "CrmEndpoint", ["eventId"] = "evt-1", ["includePayload"] = true,
        });

        AssertError(result, "PermissionDenied", "Payloads are turned off by an administrator");
    }

    [TestMethod]
    public async Task A_lowered_change_limit_applies_per_caller()
    {
        await using var host = await StartLocalAsync(p => p.Limits.MutationsPerWindow = 1);
        await using var client = await host.CreateClientAsync();

        var first = await client.CallToolAsync("nimbus_set_message_reported", ReportArguments("evt-a"));
        var second = await client.CallToolAsync("nimbus_set_message_reported", ReportArguments("evt-b"));

        Assert.IsFalse(first.IsError == true, ErrorText(first));
        AssertError(second, "RateLimited", "At most 1 changes");
    }

    [TestMethod]
    public async Task Entra_refuses_a_client_that_is_not_approved_and_audits_it()
    {
        await using var host = await StartEntraAsync(p =>
        {
            p.Clients.Mode = McpClientMode.Approved;
            p.Clients.Approved = [new McpApprovedClient { ClientId = "55555555-5555-5555-5555-555555555555", Name = "Claude Code", MayChange = true }];
        });
        var token = McpTestHost.CreateToken();

        using var response = await host.PostToolsListAsync(r => r.Headers.Authorization = new("Bearer", token));

        Assert.AreEqual(HttpStatusCode.Forbidden, response.StatusCode);
        var refusal = Refusals(host).Single();
        StringAssert.Contains(refusal.Data, "\"reason\":\"client\"", StringComparison.Ordinal);
        StringAssert.Contains(refusal.Data, McpTestHost.AgentClientId, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Entra_hides_the_change_tools_from_an_approved_client_that_may_not_change()
    {
        await using var host = await StartEntraAsync(p =>
        {
            p.Clients.Mode = McpClientMode.Approved;
            p.Clients.Approved = [new McpApprovedClient { ClientId = McpTestHost.AgentClientId, Name = "Triage", MayChange = false }];
        });
        await using var client = await host.CreateClientAsync(McpTestHost.CreateToken(scopes: "nimbus.observe nimbus.resubmit nimbus.annotate"));

        var tools = (await client.ListToolsAsync()).Select(t => t.Name).ToList();

        Assert.IsFalse(tools.Intersect(ChangeTools).Any(), string.Join(", ", tools));
        CollectionAssert.Contains(tools, "nimbus_get_message");
    }

    [TestMethod]
    [DataRow("33333333-3333-3333-3333-333333333333", true)]
    [DataRow("66666666-6666-6666-6666-666666666666", false)]
    public async Task Entra_admits_only_listed_people(string listed, bool admitted)
    {
        await using var host = await StartEntraAsync(p =>
        {
            p.People.Mode = McpPeopleMode.Listed;
            p.People.Principals = [new McpPrincipal { Principal = listed }];
        });
        var token = McpTestHost.CreateToken();

        using var response = await host.PostToolsListAsync(r => r.Headers.Authorization = new("Bearer", token));

        Assert.AreEqual(admitted ? HttpStatusCode.OK : HttpStatusCode.Forbidden, response.StatusCode);
        if (!admitted)
            StringAssert.Contains(Refusals(host).Single().Data, "\"reason\":\"person\"", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Entra_refuses_workload_tokens_when_workloads_are_off()
    {
        await using var host = await StartEntraAsync(p => p.AllowWorkloads = false);
        var workload = McpTestHost.CreateToken(scopes: null, roles: "Nimbus.Observe");
        var delegated = McpTestHost.CreateToken();

        using var refused = await host.PostToolsListAsync(r => r.Headers.Authorization = new("Bearer", workload));
        using var admitted = await host.PostToolsListAsync(r => r.Headers.Authorization = new("Bearer", delegated));

        Assert.AreEqual(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.AreEqual(HttpStatusCode.OK, admitted.StatusCode);
    }

    private static Dictionary<string, object?> ReportArguments(string eventId = "evt-1") => new()
    {
        ["endpointId"] = "CrmEndpoint", ["eventId"] = eventId, ["reported"] = true,
        ["reason"] = "Ticket raised", ["idempotencyKey"] = Guid.NewGuid().ToString(), ["ticketId"] = "INC-1",
    };

    private static Task<McpTestHost> StartLocalAsync(Action<McpAccessSettings> policy, bool piiReader = false)
        => StartAsync(policy, entra: false, piiReader);

    private static Task<McpTestHost> StartEntraAsync(Action<McpAccessSettings> policy)
        => StartAsync(policy, entra: true, piiReader: false);

    private static async Task<McpTestHost> StartAsync(Action<McpAccessSettings> policy, bool entra, bool piiReader)
    {
        var store = new InMemoryMessageStore();
        var settings = new McpAccessSettings { Revision = Guid.NewGuid().ToString() };
        policy(settings);
        Assert.IsTrue(await store.TrySetMcpAccessSettings(settings, null));

        void Configure(IServiceCollection services)
        {
            services.AddSingleton(new McpTestHost.StubAccess { Contributor = true, SiteReader = true, PiiReader = piiReader });
            services.AddSingleton(store);
            services.AddSingleton<IMessageTrackingStore>(store);
            services.AddSingleton<IEndpointMetadataStore>(store);
        }

        return entra ? await McpTestHost.StartEntraAsync(Configure) : await McpTestHost.StartLocalDevelopmentAsync(Configure);
    }

    private static List<McpTestHost.AuditRow> Refusals(McpTestHost host)
        => host.Get<McpTestHost.RecordingAuditLog>().Rows.Where(r => r.Type == MessageAuditType.McpAccessRefused && r.Denied).ToList();

    private static async Task<JsonElement> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments);
        Assert.IsFalse(result.IsError == true, ErrorText(result));
        return JsonSerializer.SerializeToElement(result.StructuredContent);
    }

    private static void AssertError(CallToolResult result, string code, string detail)
    {
        Assert.IsTrue(result.IsError == true, "Expected an error result.");
        StringAssert.Contains(ErrorText(result), $"[{code}]", StringComparison.Ordinal);
        StringAssert.Contains(ErrorText(result), detail, StringComparison.Ordinal);
    }

    private static string ErrorText(CallToolResult result)
        => string.Join(" ", result.Content.OfType<TextContentBlock>().Select(c => c.Text));

    private sealed class UnreadablePolicyStore : InMemoryMessageStore
    {
        public override Task<McpAccessSettings> GetMcpAccessSettings()
            => throw new InvalidOperationException("Storage is unreachable.");
    }
}
