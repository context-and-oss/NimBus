#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Mcp.Access;
using NimBus.WebApp.Mcp.Prompts;
using NimBus.WebApp.Mcp.Tools;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>Spec 040: the operator workflow prompts over the real MCP pipeline.</summary>
[TestClass]
public partial class OperatorPromptsTests
{
    private static readonly string[] AllPrompts = ["triage", "investigate-message", "recover-message", "failure-report", "handle-failures"];
    private static readonly string[] ReadOnlyPrompts = ["triage", "investigate-message", "failure-report"];

    [TestMethod]
    public async Task All_prompts_are_listed_in_local_development()
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync();
        await using var client = await host.CreateClientAsync();

        var prompts = (await client.ListPromptsAsync()).Select(p => p.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        CollectionAssert.AreEqual(AllPrompts.OrderBy(n => n, StringComparer.Ordinal).ToArray(), prompts);
    }

    [TestMethod]
    public async Task Prompts_declare_their_arguments()
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync();
        await using var client = await host.CreateClientAsync();

        var prompts = (await client.ListPromptsAsync()).ToDictionary(p => p.Name, p => p.ProtocolPrompt.Arguments ?? []);

        AssertArguments(prompts["triage"], ("endpointId", false), ("period", false));
        AssertArguments(prompts["investigate-message"], ("endpointId", true), ("eventId", true));
        AssertArguments(prompts["recover-message"], ("endpointId", true), ("eventId", true), ("action", false));
        AssertArguments(prompts["failure-report"], ("period", false));
        AssertArguments(prompts["handle-failures"], ("endpointId", false), ("period", false), ("unattended", false));
    }

    [TestMethod]
    [DataRow("triage")]
    [DataRow("investigate-message")]
    [DataRow("recover-message")]
    [DataRow("failure-report")]
    [DataRow("handle-failures")]
    public async Task Each_prompt_returns_one_user_message_with_the_untrusted_data_rule(string prompt)
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync();
        await using var client = await host.CreateClientAsync();

        var result = await client.GetPromptAsync(prompt, ValidArguments(prompt));

        Assert.AreEqual(1, result.Messages.Count);
        Assert.AreEqual(Role.User, result.Messages[0].Role);
        var text = Text(result);
        StringAssert.Contains(text, "nimbus_get_capabilities", StringComparison.Ordinal);
        StringAssert.Contains(text, "untrusted", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Argument_values_appear_quoted_in_the_inputs_block()
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync();
        await using var client = await host.CreateClientAsync();

        var result = await client.GetPromptAsync("recover-message", new Dictionary<string, object?>
        {
            ["endpointId"] = "CrmEndpoint", ["eventId"] = "evt \"42\"", ["action"] = "skip",
        });

        var inputs = Text(result)[Text(result).IndexOf("Inputs", StringComparison.Ordinal)..];
        StringAssert.Contains(inputs, "endpointId: \"CrmEndpoint\"", StringComparison.Ordinal);
        StringAssert.Contains(inputs, "eventId: \"evt \\\"42\\\"\"", StringComparison.Ordinal);
        StringAssert.Contains(inputs, "action: \"skip\"", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Omitted_optional_arguments_use_their_defaults()
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync();
        await using var client = await host.CreateClientAsync();

        var text = Text(await client.GetPromptAsync("triage"));

        StringAssert.Contains(text, "period: \"1d\"", StringComparison.Ordinal);
        StringAssert.Contains(text, "endpointId: (all readable endpoints)", StringComparison.Ordinal);
    }

    [TestMethod]
    [DataRow("investigate-message", "endpointId", "Crm\nEndpoint")]
    [DataRow("investigate-message", "eventId", "")]
    [DataRow("investigate-message", "eventId", null)]
    [DataRow("triage", "period", "2d")]
    [DataRow("failure-report", "period", "1D ")]
    [DataRow("recover-message", "action", "purge")]
    public async Task Invalid_arguments_are_refused(string prompt, string argument, string? value)
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync();
        await using var client = await host.CreateClientAsync();
        var arguments = ValidArguments(prompt);
        arguments[argument] = value;

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => client.GetPromptAsync(prompt, arguments).AsTask());

        StringAssert.Contains(error.Message, "[InvalidArgument]", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task An_overlong_id_is_refused()
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync();
        await using var client = await host.CreateClientAsync();

        var error = await Assert.ThrowsAsync<McpProtocolException>(() => client.GetPromptAsync("investigate-message", new Dictionary<string, object?>
        {
            ["endpointId"] = "CrmEndpoint", ["eventId"] = new string('e', 201),
        }).AsTask());

        StringAssert.Contains(error.Message, "[InvalidArgument]", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Recover_message_is_hidden_and_refused_when_resubmit_and_skip_are_off()
    {
        await using var host = await StartWithPolicyAsync(p =>
        {
            p.Capabilities.Resubmit = false;
            p.Capabilities.Skip = false;
        });
        await using var client = await host.CreateClientAsync();

        var prompts = (await client.ListPromptsAsync()).Select(p => p.Name).ToList();
        var error = await Assert.ThrowsAsync<McpProtocolException>(() => client.GetPromptAsync("recover-message", ValidArguments("recover-message")).AsTask());

        CollectionAssert.DoesNotContain(prompts, "recover-message");
        CollectionAssert.IsSubsetOf(ReadOnlyPrompts, prompts);
        StringAssert.Contains(error.Message, "[PermissionDenied] recover-message is turned off by an administrator.", StringComparison.Ordinal);
        var refusal = host.Get<McpTestHost.RecordingAuditLog>().Rows.Single(r => r.Type == MessageAuditType.McpAccessRefused && r.Denied);
        StringAssert.Contains(refusal.Data, "\"reason\":\"tool\"", StringComparison.Ordinal);
        StringAssert.Contains(refusal.Data, "recover-message", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Recover_message_stays_while_either_resubmit_or_skip_is_on()
    {
        await using var host = await StartWithPolicyAsync(p => p.Capabilities.Skip = false);
        await using var client = await host.CreateClientAsync();

        var prompts = (await client.ListPromptsAsync()).Select(p => p.Name).ToList();

        CollectionAssert.Contains(prompts, "recover-message");
    }

    [TestMethod]
    public async Task Recover_message_is_hidden_from_a_client_that_may_not_change_messages()
    {
        await using var host = await StartWithPolicyAsync(p =>
        {
            p.Clients.Mode = McpClientMode.Approved;
            p.Clients.Approved = [new McpApprovedClient { ClientId = McpTestHost.AgentClientId, Name = "Triage", MayChange = false }];
        }, entra: true);
        await using var client = await host.CreateClientAsync(McpTestHost.CreateToken(scopes: "nimbus.observe nimbus.resubmit nimbus.skip"));

        var prompts = (await client.ListPromptsAsync()).Select(p => p.Name).ToList();

        CollectionAssert.DoesNotContain(prompts, "recover-message");
        CollectionAssert.Contains(prompts, "triage");
    }

    [TestMethod]
    public async Task Handle_failures_is_hidden_and_refused_when_classify_is_off()
    {
        await using var host = await StartWithPolicyAsync(p => p.Capabilities.Classify = false);
        await using var client = await host.CreateClientAsync();

        var prompts = (await client.ListPromptsAsync()).Select(p => p.Name).ToList();
        var error = await Assert.ThrowsAsync<McpProtocolException>(() => client.GetPromptAsync("handle-failures", ValidArguments("handle-failures")).AsTask());

        CollectionAssert.DoesNotContain(prompts, "handle-failures");
        CollectionAssert.Contains(prompts, "recover-message");
        StringAssert.Contains(error.Message, "[PermissionDenied] handle-failures is turned off by an administrator.", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Handle_failures_asks_before_every_retry_unless_it_runs_unattended()
    {
        var attended = OperatorPrompts.Render("handle-failures", new Dictionary<string, string?>());
        var unattended = OperatorPrompts.Render("handle-failures", new Dictionary<string, string?> { ["unattended"] = "true" });

        StringAssert.Contains(attended, "Only after a clear yes", StringComparison.Ordinal);
        StringAssert.Contains(attended, "unattended: \"false\"", StringComparison.Ordinal);
        Assert.IsFalse(unattended.Contains("Only after a clear yes", StringComparison.Ordinal));
        StringAssert.Contains(unattended, "never ask a question", StringComparison.Ordinal);
        StringAssert.Contains(unattended, "within your budget", StringComparison.Ordinal);
        StringAssert.Contains(unattended, "nimbus_get_operation", StringComparison.Ordinal);
        StringAssert.Contains(unattended, "OutcomeUnknown: hand it to a person", StringComparison.Ordinal);
        StringAssert.Contains(unattended, "Never skip", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Handle_failures_refuses_an_unattended_value_that_is_not_true_or_false()
    {
        var error = Assert.ThrowsExactly<McpProtocolException>(() =>
            OperatorPrompts.Render("handle-failures", new Dictionary<string, string?> { ["unattended"] = "yes please" }));

        StringAssert.Contains(error.Message, "unattended must be true or false", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Handle_failures_maps_guidance_and_origin_to_the_four_outcomes()
    {
        var text = OperatorPrompts.Render("handle-failures", new Dictionary<string, string?>());

        StringAssert.Contains(text, "guidance RetryMayHelp: Retry", StringComparison.Ordinal);
        StringAssert.Contains(text, "origin source_data: Report data quality", StringComparison.Ordinal);
        StringAssert.Contains(text, "origin adapter_code: Register a bug and draft a fix", StringComparison.Ordinal);
        StringAssert.Contains(text, "anything else: Hand to a person", StringComparison.Ordinal);
        StringAssert.Contains(text, "never the NimBus repository", StringComparison.Ordinal);
        StringAssert.Contains(text, "Never skip", StringComparison.Ordinal);
        StringAssert.Contains(text, "nimbus_record_classification", StringComparison.Ordinal);
        StringAssert.Contains(text, "endpointId: (all readable endpoints)", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Triage_starts_from_the_worklist_and_offers_only_reads()
    {
        // Spec 046 §4: one call in place of overview, metrics and per-endpoint searches.
        var text = OperatorPrompts.Render("triage", new Dictionary<string, string?>());

        StringAssert.Contains(text, "2. Call nimbus_get_worklist", StringComparison.Ordinal);
        StringAssert.Contains(text, "olderFailuresLikely", StringComparison.Ordinal);
        StringAssert.Contains(text, "Offer only nextActions of kind read", StringComparison.Ordinal);
        Assert.IsFalse(text.Contains("nimbus_get_metrics", StringComparison.Ordinal));
    }

    [TestMethod]
    public void Every_tool_a_prompt_names_is_registered()
    {
        var registered = RegisteredToolNames();

        foreach (var (prompt, text) in PromptTexts())
        {
            var named = ToolName().Matches(text).Select(m => m.Value).Distinct().ToList();
            Assert.IsNotEmpty(named, $"{prompt} names no tool.");
            foreach (var tool in named)
                Assert.Contains(tool, registered, $"{prompt} names {tool}, which is not a registered tool.");
        }
    }

    [TestMethod]
    public void Read_only_prompts_name_no_tool_that_prepares_or_changes_a_message()
    {
        var changing = McpToolAccess.ChangeTools.Keys.Append(McpToolAccess.PrepareTool).ToHashSet(StringComparer.Ordinal);

        foreach (var (prompt, text) in PromptTexts().Where(p => ReadOnlyPrompts.Contains(p.Prompt)))
        {
            var named = ToolName().Matches(text).Select(m => m.Value).Where(changing.Contains).ToList();
            Assert.IsEmpty(named, $"{prompt} names {string.Join(", ", named)}.");
        }
    }

    private static IEnumerable<(string Prompt, string Text)> PromptTexts()
    {
        foreach (var prompt in AllPrompts)
        {
            var arguments = ValidArguments(prompt);
            yield return (prompt, OperatorPrompts.Render(prompt, arguments.ToDictionary(a => a.Key, a => (string?)a.Value?.ToString())));
        }

        // The variant of recover-message without a requested action.
        yield return ("recover-message", OperatorPrompts.Render("recover-message", new Dictionary<string, string?>
        {
            ["endpointId"] = "CrmEndpoint", ["eventId"] = "evt-1",
        }));
    }

    internal static HashSet<string> RegisteredToolNames()
        => typeof(OperatorMessageTools).Assembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .SelectMany(t => t.GetMethods())
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .OfType<string>()
            .ToHashSet(StringComparer.Ordinal);

    private static Dictionary<string, object?> ValidArguments(string prompt) => prompt switch
    {
        "triage" => new() { ["endpointId"] = "CrmEndpoint", ["period"] = "7d" },
        "investigate-message" => new() { ["endpointId"] = "CrmEndpoint", ["eventId"] = "evt-1" },
        "recover-message" => new() { ["endpointId"] = "CrmEndpoint", ["eventId"] = "evt-1", ["action"] = "resubmit" },
        "failure-report" => new() { ["period"] = "3d" },
        "handle-failures" => new() { ["endpointId"] = "CrmEndpoint", ["period"] = "1d" },
        _ => throw new ArgumentOutOfRangeException(nameof(prompt), prompt, null),
    };

    private static void AssertArguments(IList<PromptArgument> actual, params (string Name, bool Required)[] expected)
    {
        var shape = actual.Select(a => (a.Name, a.Required == true)).OrderBy(a => a.Name, StringComparer.Ordinal).ToArray();
        CollectionAssert.AreEqual(expected.OrderBy(a => a.Name, StringComparer.Ordinal).ToArray(), shape);
    }

    private static string Text(GetPromptResult result)
        => string.Join("\n", result.Messages.Select(m => m.Content).OfType<TextContentBlock>().Select(c => c.Text));

    private static async Task<McpTestHost> StartWithPolicyAsync(Action<McpAccessSettings> policy, bool entra = false)
    {
        var store = new InMemoryMessageStore();
        var settings = new McpAccessSettings { Revision = Guid.NewGuid().ToString() };
        policy(settings);
        Assert.IsTrue(await store.TrySetMcpAccessSettings(settings, null));

        void Configure(IServiceCollection services)
        {
            services.AddSingleton(new McpTestHost.StubAccess { Contributor = true, SiteReader = true });
            services.AddSingleton(store);
            services.AddSingleton<IMessageTrackingStore>(store);
            services.AddSingleton<IEndpointMetadataStore>(store);
        }

        return entra ? await McpTestHost.StartEntraAsync(Configure) : await McpTestHost.StartLocalDevelopmentAsync(Configure);
    }

    [GeneratedRegex(@"\bnimbus_[a-z_]+\b")]
    internal static partial Regex ToolName();
}
