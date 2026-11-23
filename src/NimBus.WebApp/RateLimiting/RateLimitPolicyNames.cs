namespace NimBus.WebApp.RateLimiting;

/// <summary>
/// Names of the four rate-limiting policies the WebApp registers. Shared by the
/// registration (<see cref="RateLimitingServiceCollectionExtensions"/>), the
/// application-model convention that attaches them, and the tests — a single
/// spelling so a rename cannot silently unbind a policy from its endpoints.
/// </summary>
public static class RateLimitPolicyNames
{
    /// <summary>Concurrency limiter guarding the <c>GET /api/agent/receive</c> long poll.</summary>
    public const string AgentReceive = "nimbus-agent-receive";

    /// <summary>Fixed-window limiter guarding the <c>/api/admin/*</c> bulk operations.</summary>
    public const string Admin = "nimbus-admin";

    /// <summary>Fixed-window limiter guarding the message and audit search endpoints.</summary>
    public const string Search = "nimbus-search";

    /// <summary>
    /// Fixed-window limiter guarding the alert rule preview (Spec 053 Phase 3), which reads a day of
    /// an endpoint's message history: about 170–310 RU on Cosmos DB per preview.
    /// </summary>
    public const string AlertRulePreview = "nimbus-alert-rule-preview";

    /// <summary>Per-client-IP fixed-window limiter guarding <c>POST /account/login</c>.</summary>
    public const string Login = "nimbus-login";

    /// <summary>
    /// Per-caller fixed window over every <c>/api</c> request, ahead of the named policies. Not
    /// attachable to an endpoint; it names the limit in a rejection's problem and log line.
    /// </summary>
    public const string Global = "nimbus-global";

    /// <summary>Fixed-window limiter guarding the operator MCP endpoint, per tenant, client and user.</summary>
    public const string Mcp = "nimbus-mcp";
}
