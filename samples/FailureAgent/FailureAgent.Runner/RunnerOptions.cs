namespace FailureAgent.Runner;

/// <summary>How the runner signs in to the NimBus operator MCP endpoint.</summary>
public enum RunnerAuthentication
{
    /// <summary>An app-only Entra token for the MCP resource (workload identity, managed identity or a client secret).</summary>
    Entra,

    /// <summary>No token: the WebApp's local-development bypass under Aspire (loopback only).</summary>
    LocalDevelopment,
}

/// <summary>The runner's settings, bound from the <c>FailureAgent</c> configuration section.</summary>
public sealed class RunnerOptions
{
    /// <summary>The configuration section.</summary>
    public const string Section = "FailureAgent";

    /// <summary>The operator MCP endpoint, for example <c>https://nimbus.example.com/mcp</c>.</summary>
    public string McpUrl { get; set; } = string.Empty;

    /// <summary>How to sign in.</summary>
    public RunnerAuthentication Authentication { get; set; } = RunnerAuthentication.Entra;

    /// <summary>The MCP resource scope for an app-only token, for example <c>api://&lt;mcp-app-id&gt;/.default</c>.</summary>
    public string? Scope { get; set; }

    /// <summary>Only this endpoint; null for every endpoint the workload can read.</summary>
    public string? EndpointId { get; set; }

    /// <summary>How far back to look: 1h, 12h, 1d, 3d, 7d or 30d.</summary>
    public string Period { get; set; } = "1d";

    /// <summary>Where the coding agent works: an empty directory it may clone adapter repositories into.</summary>
    public string WorkingDirectory { get; set; } = Path.Combine(Path.GetTempPath(), "nimbus-failure-agent");

    /// <summary>The coding agent's executable. The reference is Claude Code (<c>claude</c>).</summary>
    public string AgentCommand { get; set; } = "claude";

    /// <summary>The model, when the agent should not use its default.</summary>
    public string? Model { get; set; }

    /// <summary>The most the agent may spend on model calls in one run, in US dollars.</summary>
    public decimal MaxBudgetUsd { get; set; } = 5m;

    /// <summary>The longest a run may take; it must fit in the token's remaining lifetime.</summary>
    public int MaxRunMinutes { get; set; } = 40;

    /// <summary>Extra instructions appended to the agent's system prompt, for example where to file reports.</summary>
    public string? AppendSystemPrompt { get; set; }

    /// <summary>The problems with these settings; empty when they are usable.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!Uri.TryCreate(McpUrl, UriKind.Absolute, out var url) || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp))
            errors.Add("FailureAgent:McpUrl must be the absolute address of the NimBus /mcp endpoint.");
        else if (Authentication == RunnerAuthentication.Entra && url.Scheme != Uri.UriSchemeHttps)
            errors.Add("FailureAgent:McpUrl must use https with Entra authentication.");
        if (Authentication == RunnerAuthentication.Entra && string.IsNullOrWhiteSpace(Scope))
            errors.Add("FailureAgent:Scope is required with Entra authentication, for example api://<mcp-app-id>/.default.");
        if (Period is not ("1h" or "12h" or "1d" or "3d" or "7d" or "30d"))
            errors.Add("FailureAgent:Period must be 1h, 12h, 1d, 3d, 7d or 30d.");
        if (string.IsNullOrWhiteSpace(AgentCommand))
            errors.Add("FailureAgent:AgentCommand is required.");
        if (MaxBudgetUsd <= 0)
            errors.Add("FailureAgent:MaxBudgetUsd must be positive.");
        if (MaxRunMinutes is < 1 or > 120)
            errors.Add("FailureAgent:MaxRunMinutes must be between 1 and 120.");
        return errors;
    }
}
