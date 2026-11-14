#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>Spec 046 §5: completions for prompt arguments.</summary>
[TestClass]
public class PromptCompletionsTests
{
    [TestMethod]
    public async Task The_server_advertises_completions()
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync();
        await using var client = await host.CreateClientAsync();

        Assert.IsNotNull(client.ServerCapabilities.Completions);
    }

    [TestMethod]
    [DataRow("triage", "period", "", new[] { "1h", "12h", "1d", "3d", "7d", "30d" })]
    [DataRow("failure-report", "period", "1", new[] { "1h", "12h", "1d" })]
    [DataRow("recover-message", "action", "", new[] { "resubmit", "skip" })]
    [DataRow("recover-message", "action", "s", new[] { "skip" })]
    [DataRow("handle-failures", "unattended", "", new[] { "true", "false" })]
    public async Task Fixed_arguments_complete_from_their_allowed_values(string prompt, string argument, string typed, string[] expected)
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync();
        await using var client = await host.CreateClientAsync();

        var result = await client.CompleteAsync(new PromptReference { Name = prompt }, argument, typed);

        CollectionAssert.AreEqual(expected, result.Completion.Values.ToArray());
    }

    [TestMethod]
    public async Task Endpoint_ids_complete_from_the_readable_endpoints_by_prefix()
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync();
        await using var client = await host.CreateClientAsync();

        var all = await client.CompleteAsync(new PromptReference { Name = "triage" }, "endpointId", "");
        var crm = await client.CompleteAsync(new PromptReference { Name = "investigate-message" }, "endpointId", "crm");

        // BillingEndpoint is in the catalog, but the caller can't read it.
        CollectionAssert.AreEqual(McpTestHost.ReadableEndpoints, all.Completion.Values.ToArray());
        CollectionAssert.AreEqual(new[] { "CrmEndpoint" }, crm.Completion.Values.ToArray());
        Assert.AreEqual(1, crm.Completion.Total);
        Assert.IsFalse(crm.Completion.HasMore == true);
    }

    [TestMethod]
    public async Task A_hidden_endpoint_is_not_completed()
    {
        await using var host = await StartWithPolicyAsync(p =>
        {
            p.Endpoints.Visibility = McpEndpointVisibility.AllExcept;
            p.Endpoints.Hidden = ["ErpEndpoint"];
        });
        await using var client = await host.CreateClientAsync();

        var result = await client.CompleteAsync(new PromptReference { Name = "triage" }, "endpointId", "");

        CollectionAssert.AreEqual(new[] { "CrmEndpoint" }, result.Completion.Values.ToArray());
    }

    [TestMethod]
    public async Task A_prompt_the_caller_cannot_list_completes_nothing()
    {
        await using var host = await StartWithPolicyAsync(p =>
        {
            p.Capabilities.Resubmit = false;
            p.Capabilities.Skip = false;
        });
        await using var client = await host.CreateClientAsync();

        var hidden = await client.CompleteAsync(new PromptReference { Name = "recover-message" }, "endpointId", "");
        var listed = await client.CompleteAsync(new PromptReference { Name = "triage" }, "endpointId", "");

        Assert.IsEmpty(hidden.Completion.Values);
        Assert.IsNotEmpty(listed.Completion.Values);
    }

    [TestMethod]
    [DataRow("triage", "eventId")]
    [DataRow("investigate-message", "eventId")]
    [DataRow("no-such-prompt", "period")]
    [DataRow("triage", "unknown")]
    public async Task Event_ids_and_unknown_arguments_complete_nothing(string prompt, string argument)
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync();
        await using var client = await host.CreateClientAsync();

        var result = await client.CompleteAsync(new PromptReference { Name = prompt }, argument, "");

        Assert.IsEmpty(result.Completion.Values);
    }

    private static async Task<McpTestHost> StartWithPolicyAsync(Action<McpAccessSettings> policy)
    {
        var store = new InMemoryMessageStore();
        var settings = new McpAccessSettings { Revision = Guid.NewGuid().ToString() };
        policy(settings);
        Assert.IsTrue(await store.TrySetMcpAccessSettings(settings, null));

        return await McpTestHost.StartLocalDevelopmentAsync(services =>
        {
            services.AddSingleton(store);
            services.AddSingleton<IMessageTrackingStore>(store);
            services.AddSingleton<IEndpointMetadataStore>(store);
        });
    }
}
