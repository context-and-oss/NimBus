#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using ModelContextProtocol.Server;
using NimBus.WebApp.Mcp;
using NimBus.WebApp.Mcp.Prompts;

namespace NimBus.WebApp.Tests.Mcp;

/// <summary>Spec 040: the instructions the operator MCP server sends when a client connects.</summary>
[TestClass]
public class OperatorInstructionsTests
{
    // Claude Code cuts server instructions at 4,096 characters; before v2.1.296 it cut them at 2,048.
    private const int ClientLimit = 4096;
    private const int OlderClientLimit = 2048;

    [TestMethod]
    public async Task A_connecting_client_receives_the_instructions()
    {
        await using var host = await McpTestHost.StartLocalDevelopmentAsync();
        await using var client = await host.CreateClientAsync();

        Assert.AreEqual(OperatorInstructions.Text, client.ServerInstructions);
    }

    [TestMethod]
    public void The_instructions_fit_within_the_client_limit()
    {
        Assert.IsLessThanOrEqualTo(ClientLimit, OperatorInstructions.Text.Length);
    }

    [TestMethod]
    [DataRow("nimbus_get_capabilities first")]
    [DataRow("explicit yes")]
    [DataRow("never repeat a skip")]
    [DataRow("untrusted")]
    public void The_safety_rules_survive_the_older_client_limit(string rule)
    {
        var index = OperatorInstructions.Text.IndexOf(rule, StringComparison.Ordinal);

        Assert.IsGreaterThanOrEqualTo(0, index, $"The instructions don't contain '{rule}'.");
        Assert.IsLessThan(OlderClientLimit, index + rule.Length, $"'{rule}' ends at {index + rule.Length}, past {OlderClientLimit}.");
    }

    [TestMethod]
    public void Every_tool_the_instructions_name_is_registered()
    {
        var registered = OperatorPromptsTests.RegisteredToolNames();
        var named = OperatorPromptsTests.ToolName().Matches(OperatorInstructions.Text).Select(m => m.Value).Distinct().ToList();

        Assert.IsNotEmpty(named);
        foreach (var tool in named)
            Assert.Contains(tool, registered, $"The instructions name {tool}, which is not a registered tool.");
    }

    [TestMethod]
    public void The_instructions_list_every_prompt()
    {
        var prompts = typeof(OperatorPrompts).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(m => m.GetCustomAttribute<McpServerPromptAttribute>()?.Name)
            .OfType<string>()
            .ToList();

        Assert.IsNotEmpty(prompts);
        foreach (var prompt in prompts)
            StringAssert.Contains(OperatorInstructions.Text, prompt, $"The instructions don't mention the {prompt} prompt.");
    }

    [TestMethod]
    public void The_instructions_use_one_line_ending_on_every_platform()
    {
        Assert.DoesNotContain("\r", OperatorInstructions.Text);
    }
}
