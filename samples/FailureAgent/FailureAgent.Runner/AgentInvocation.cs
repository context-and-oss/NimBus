using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FailureAgent.Runner;

/// <summary>
/// Builds the headless coding-agent invocation. The reference is Claude Code in print mode
/// (<c>claude -p</c>); another agent CLI can be used by changing <see cref="RunnerOptions.AgentCommand"/>
/// and these arguments.
/// </summary>
public static class AgentInvocation
{
    /// <summary>The MCP server name in the agent's configuration; tool names are <c>mcp__nimbus__*</c>.</summary>
    public const string ServerName = "nimbus";

    /// <summary>
    /// Tools the agent may use without asking: the NimBus server, reading and editing files in its
    /// working directory, and git and gh for the adapter's repository.
    /// </summary>
    public static readonly IReadOnlyList<string> AllowedTools =
        [$"mcp__{ServerName}", "Read", "Edit", "Write", "Glob", "Grep", "Bash(git *)", "Bash(gh *)", "Bash(dotnet *)"];

    /// <summary>
    /// Tools the agent may never use. NimBus refuses skip for workloads anyway; this is defence in
    /// depth, and keeps the agent off the web.
    /// </summary>
    public static readonly IReadOnlyList<string> DisallowedTools =
        [$"mcp__{ServerName}__nimbus_skip_message", "WebFetch", "WebSearch"];

    /// <summary>The agent's arguments, with the prompt sent on standard input.</summary>
    /// <param name="options">The runner's settings.</param>
    /// <param name="mcpConfigPath">The temporary MCP configuration file.</param>
    public static IReadOnlyList<string> Arguments(RunnerOptions options, string mcpConfigPath)
    {
        ArgumentNullException.ThrowIfNull(options);
        var arguments = new List<string>
        {
            "-p",
            "--strict-mcp-config",
            "--mcp-config", mcpConfigPath,
            "--permission-mode", "dontAsk",
            "--allowedTools", string.Join(",", AllowedTools),
            "--disallowedTools", string.Join(",", DisallowedTools),
            "--output-format", "json",
            "--max-budget-usd", options.MaxBudgetUsd.ToString(CultureInfo.InvariantCulture),
            "--no-session-persistence",
        };
        if (!string.IsNullOrWhiteSpace(options.Model))
            arguments.AddRange(["--model", options.Model]);
        if (!string.IsNullOrWhiteSpace(options.AppendSystemPrompt))
            arguments.AddRange(["--append-system-prompt", options.AppendSystemPrompt]);
        return arguments;
    }

    /// <summary>The agent's MCP configuration: the NimBus server over HTTP, with the bearer token when there is one.</summary>
    /// <param name="mcpUrl">The operator MCP endpoint.</param>
    /// <param name="accessToken">The app-only token, or null in local development.</param>
    public static string McpConfig(string mcpUrl, string? accessToken)
    {
        var server = new JsonObject { ["type"] = "http", ["url"] = mcpUrl };
        if (!string.IsNullOrEmpty(accessToken))
            server["headers"] = new JsonObject { ["Authorization"] = "Bearer " + accessToken };
        return new JsonObject { ["mcpServers"] = new JsonObject { [ServerName] = server } }.ToJsonString();
    }

    /// <summary>
    /// Writes <paramref name="content"/> to a new file only the current user can read, and returns
    /// its path. The file holds a bearer token: delete it when the run ends.
    /// </summary>
    /// <param name="directory">Where to write it.</param>
    /// <param name="content">The configuration.</param>
    public static string WritePrivateFile(string directory, string content)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"nimbus-mcp-{Guid.NewGuid():N}.json");
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream))
            writer.Write(content);
        return path;
    }

    /// <summary>The run's summary from the agent's JSON output.</summary>
    /// <param name="output">The agent's standard output.</param>
    public static RunSummary Summarize(string output)
    {
        try
        {
            using var document = JsonDocument.Parse(output);
            var root = document.RootElement;
            var isError = root.TryGetProperty("is_error", out var error) && error.ValueKind == JsonValueKind.True;
            var result = root.TryGetProperty("result", out var text) ? text.GetString() : null;
            decimal? cost = root.TryGetProperty("total_cost_usd", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetDecimal() : null;
            return new RunSummary(!isError, result, cost);
        }
        catch (JsonException)
        {
            return new RunSummary(false, output.Length > 4000 ? output[..4000] : output, null);
        }
    }
}

/// <summary>What a run produced.</summary>
/// <param name="Succeeded">Whether the agent finished without an error.</param>
/// <param name="Result">The agent's final message: its summary table.</param>
/// <param name="CostUsd">What the run cost, when the agent reports it.</param>
public sealed record RunSummary(bool Succeeded, string? Result, decimal? CostUsd);
