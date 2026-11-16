using System.Globalization;
using System.Security.Claims;
using Microsoft.Extensions.Primitives;

namespace NimBus.WebApp.Services.AgentApiKeys;

/// <summary>
/// Names and helpers for agent API keys issued for the operator MCP endpoint (Spec 052). An MCP
/// key acts as an unattended workload; its identity is read only through <see cref="Read"/>.
/// </summary>
public static class McpAgentKeyDefaults
{
    /// <summary>The authentication scheme for MCP keys. Only the MCP policy scheme forwards to it.</summary>
    public const string SchemeName = "NimBusMcpAgentKey";

    /// <summary>One claim per permission the key's grant holds: <c>observe</c>, <c>classify</c>, <c>annotate</c>, <c>resubmit</c>.</summary>
    public const string PermissionClaim = "nimbus:mcp_permission";

    /// <summary>The key's own MCP request limit per window, when its grant sets one.</summary>
    public const string RequestsPerWindowClaim = "nimbus:mcp_requests_per_window";

    /// <summary>When the key expires, round-trip format.</summary>
    public const string ExpiresAtClaim = "nimbus:agent_key_expires_at";

    /// <summary>Permission every MCP key holds: the read tools.</summary>
    public const string ObservePermission = "observe";

    /// <summary>Permission to record failure classifications.</summary>
    public const string ClassifyPermission = "classify";

    /// <summary>Permission to mark messages reported.</summary>
    public const string AnnotatePermission = "annotate";

    /// <summary>Permission to resubmit failed messages.</summary>
    public const string ResubmitPermission = "resubmit";

    /// <summary>
    /// How old the last successful read of the key list may be before every MCP key is refused
    /// (Spec 052 §6.10). A revocation saved on one instance then reaches every instance that can
    /// still authenticate a key.
    /// </summary>
    public static readonly TimeSpan StalenessBound = TimeSpan.FromMinutes(5);

    private const string BearerPrefix = "Bearer ";

    /// <summary>
    /// The MCP key caller behind <paramref name="user"/>, or null unless one of its identities was
    /// authenticated by <see cref="SchemeName"/>. Claims with the same names on any other identity,
    /// an Entra token's for example, are never read.
    /// </summary>
    public static McpAgentKeyCaller? Read(ClaimsPrincipal? user)
    {
        var identity = user?.Identities.FirstOrDefault(i =>
            i.IsAuthenticated && string.Equals(i.AuthenticationType, SchemeName, StringComparison.Ordinal));
        if (identity is null)
            return null;

        var agentId = identity.FindFirst(AgentApiKeyDefaults.AgentIdClaim)?.Value;
        var keyId = identity.FindFirst(AgentApiKeyDefaults.KeyIdClaim)?.Value;
        if (string.IsNullOrEmpty(agentId) || string.IsNullOrEmpty(keyId))
            return null;

        var permissions = identity.FindAll(PermissionClaim).Select(c => c.Value).ToHashSet(StringComparer.Ordinal);
        int? limit = int.TryParse(identity.FindFirst(RequestsPerWindowClaim)?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
        DateTime? expires = DateTime.TryParse(identity.FindFirst(ExpiresAtClaim)?.Value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at)
            ? at
            : null;
        return new McpAgentKeyCaller(agentId, keyId, permissions, limit, expires);
    }

    /// <summary>
    /// Whether the request carries an agent key: the <see cref="AgentApiKeyDefaults.HeaderName"/>
    /// header, or an <c>Authorization: Bearer</c> value that starts with <c>nbak_</c>. No JWT starts
    /// with that prefix: a JWS or JWE starts with <c>eyJ</c>.
    /// </summary>
    public static bool CarriesKey(HttpRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Headers.ContainsKey(AgentApiKeyDefaults.HeaderName))
            return true;

        return BearerValue(request.Headers.Authorization) is { } value
               && value.StartsWith(AgentApiKeyFormat.Prefix, StringComparison.Ordinal);
    }

    /// <summary>The value of a single <c>Authorization: Bearer</c> header, or null.</summary>
    internal static string? BearerValue(StringValues authorization)
    {
        if (authorization.Count != 1)
            return null;

        var header = authorization[0];
        return header is not null && header.StartsWith(BearerPrefix, StringComparison.Ordinal)
            ? header[BearerPrefix.Length..].Trim()
            : null;
    }
}

/// <summary>An authenticated MCP agent key caller.</summary>
/// <param name="AgentId">The agent the key was issued for; Access Control grants name <c>agent:&lt;AgentId&gt;</c>.</param>
/// <param name="KeyId">The key's public id, recorded in audit rows and the rate-limit partition.</param>
/// <param name="Permissions">The key's grant, <c>observe</c> always included.</param>
/// <param name="RequestsPerWindow">The key's own request limit, when it has one.</param>
/// <param name="ExpiresAtUtc">When the key expires.</param>
public sealed record McpAgentKeyCaller(string AgentId, string KeyId, IReadOnlySet<string> Permissions, int? RequestsPerWindow, DateTime? ExpiresAtUtc)
{
    /// <summary>The identifier Access Control entries, the journal and the audit log name the agent by.</summary>
    public string Principal => "agent:" + AgentId;

    /// <summary>Whether the key's grant holds <paramref name="permission"/>.</summary>
    public bool Holds(string permission) => Permissions.Contains(permission);
}
