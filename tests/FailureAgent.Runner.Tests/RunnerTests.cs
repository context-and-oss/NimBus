#pragma warning disable CA1707, CA2007
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace FailureAgent.Runner.Tests;

[TestClass]
public sealed class RunnerTests
{
    private static RunnerOptions Valid() => new()
    {
        McpUrl = "https://nimbus.example.com/mcp",
        Scope = "api://11111111-1111-1111-1111-111111111111/.default",
    };

    [TestMethod]
    public void Valid_options_have_no_problems()
        => Assert.AreEqual(0, Valid().Validate().Count);

    [TestMethod]
    public void Entra_needs_https_and_a_scope()
    {
        var options = Valid();
        options.McpUrl = "http://nimbus.example.com/mcp";
        options.Scope = null;

        var problems = options.Validate();

        Assert.IsTrue(problems.Any(p => p.Contains("https", StringComparison.Ordinal)));
        Assert.IsTrue(problems.Any(p => p.Contains("Scope", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Local_development_needs_no_scope_and_allows_http()
    {
        var options = new RunnerOptions { McpUrl = "http://localhost:18080/mcp", Authentication = RunnerAuthentication.LocalDevelopment };

        Assert.AreEqual(0, options.Validate().Count);
    }

    [TestMethod]
    [DataRow("2d")]
    [DataRow("")]
    public void An_unknown_period_is_refused(string period)
    {
        var options = Valid();
        options.Period = period;

        Assert.IsTrue(options.Validate().Any(p => p.Contains("Period", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void The_agent_runs_headless_with_only_the_nimbus_server_and_never_skips()
    {
        var options = Valid();
        options.Model = "sonnet";
        options.MaxBudgetUsd = 2.5m;

        var arguments = AgentInvocation.Arguments(options, "config.json");

        CollectionAssert.Contains(arguments.ToList(), "-p");
        CollectionAssert.Contains(arguments.ToList(), "--strict-mcp-config");
        Assert.AreEqual("config.json", After(arguments, "--mcp-config"));
        Assert.AreEqual("dontAsk", After(arguments, "--permission-mode"));
        Assert.AreEqual("json", After(arguments, "--output-format"));
        Assert.AreEqual("2.5", After(arguments, "--max-budget-usd"));
        Assert.AreEqual("sonnet", After(arguments, "--model"));
        StringAssert.Contains(After(arguments, "--disallowedTools"), "mcp__nimbus__nimbus_skip_message", StringComparison.Ordinal);
        StringAssert.Contains(After(arguments, "--allowedTools"), "mcp__nimbus", StringComparison.Ordinal);
    }

    [TestMethod]
    public void The_mcp_config_carries_the_bearer_token_only_when_there_is_one()
    {
        var withToken = JsonDocument.Parse(AgentInvocation.McpConfig("https://n.example/mcp", "abc")).RootElement.GetProperty("mcpServers").GetProperty("nimbus");
        var local = JsonDocument.Parse(AgentInvocation.McpConfig("http://localhost/mcp", null)).RootElement.GetProperty("mcpServers").GetProperty("nimbus");

        Assert.AreEqual("http", withToken.GetProperty("type").GetString());
        Assert.AreEqual("Bearer abc", withToken.GetProperty("headers").GetProperty("Authorization").GetString());
        Assert.IsFalse(local.TryGetProperty("headers", out _));
    }

    [TestMethod]
    public void The_config_file_is_new_and_private()
    {
        var directory = Path.Combine(Path.GetTempPath(), "runner-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = AgentInvocation.WritePrivateFile(directory, "{}");

            Assert.AreEqual("{}", File.ReadAllText(path));
            if (!OperatingSystem.IsWindows())
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void A_workload_that_may_record_and_resubmit_passes_with_its_budget_noted()
    {
        var result = CapabilityCheck.Evaluate(Capabilities(isWorkload: true, actions: ["classify", "resubmit", "report"]), RunnerAuthentication.Entra);

        Assert.IsTrue(result.Ok);
        Assert.IsTrue(result.CanRetry);
        Assert.IsTrue(result.Warnings.Any(w => w.Contains("18 of 20", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void A_person_token_is_refused_for_an_unattended_run()
    {
        var result = CapabilityCheck.Evaluate(Capabilities(isWorkload: false, actions: ["classify"]), RunnerAuthentication.Entra);

        Assert.IsFalse(result.Ok);
        Assert.IsTrue(result.Errors.Any(e => e.Contains("workload", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void Without_classify_the_run_stops_and_without_resubmit_retries_go_to_a_person()
    {
        var noClassify = CapabilityCheck.Evaluate(Capabilities(isWorkload: true, actions: ["resubmit"]), RunnerAuthentication.Entra);
        var noResubmit = CapabilityCheck.Evaluate(Capabilities(isWorkload: true, actions: ["classify"]), RunnerAuthentication.Entra);

        Assert.IsFalse(noClassify.Ok);
        Assert.IsTrue(noResubmit.Ok);
        Assert.IsFalse(noResubmit.CanRetry);
        Assert.IsTrue(noResubmit.Warnings.Any(w => w.Contains("handed to a person", StringComparison.Ordinal)));
    }

    [TestMethod]
    public void The_run_summary_reads_the_agents_json_result()
    {
        var ok = AgentInvocation.Summarize("""{"type":"result","is_error":false,"result":"| endpoint | outcome |","total_cost_usd":0.42}""");
        var failed = AgentInvocation.Summarize("""{"type":"result","is_error":true,"result":"budget exceeded"}""");
        var garbage = AgentInvocation.Summarize("not json");

        Assert.IsTrue(ok.Succeeded);
        Assert.AreEqual(0.42m, ok.CostUsd);
        Assert.IsFalse(failed.Succeeded);
        Assert.IsFalse(garbage.Succeeded);
        Assert.AreEqual("not json", garbage.Result);
    }

    private static string After(IReadOnlyList<string> arguments, string flag) => arguments[arguments.ToList().IndexOf(flag) + 1];

    private static JsonElement Capabilities(bool isWorkload, string[] actions) => JsonSerializer.SerializeToElement(new
    {
        caller = new { name = "app:runner", isWorkload },
        readableEndpoints = new[] { "ErpEndpoint" },
        permittedActions = actions,
        features = new { canRecordClassifications = true, operationJournal = true },
        unattended = isWorkload && actions.Contains("resubmit")
            ? new { resubmitsPerWindow = 20, resubmitsUsed = 2, windowHours = 24, unsuccessfulResubmitsPerMessage = 2 }
            : null,
    });
}
