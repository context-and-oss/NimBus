using System.Security.Claims;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.AgentApiKeys;

namespace NimBus.WebApp.Mcp.Access;

/// <summary>
/// Who is calling the operator MCP endpoint, as the MCP access policy (Spec 037) sees it.
/// MCP tokens keep raw claim names (<c>MapInboundClaims = false</c>).
/// </summary>
/// <param name="LocalDevelopment">The local-dev bypass authenticated the call; people, client and workload rules do not apply.</param>
/// <param name="TenantId">The <c>tid</c> claim.</param>
/// <param name="CallerId">The <c>oid</c> or <c>sub</c> claim; <c>agent:&lt;agentId&gt;</c> for an MCP agent key.</param>
/// <param name="ClientId">The <c>azp</c> claim, else <c>appid</c>; <c>agent:&lt;agentId&gt;</c> for an MCP agent key.</param>
/// <param name="IsWorkload">An app-only token or an MCP agent key: no delegated <c>scp</c> claim.</param>
/// <param name="Identifiers">Emails, object id and group ids, lowercase where they are emails; <c>agent:&lt;agentId&gt;</c> for a key.</param>
public sealed record McpCaller(
    bool LocalDevelopment,
    string? TenantId,
    string? CallerId,
    string? ClientId,
    bool IsWorkload,
    IReadOnlyCollection<string> Identifiers)
{
    /// <summary>The agent of an MCP agent key caller (Spec 052); null for every other caller.</summary>
    public string? AgentId { get; init; }

    /// <summary>The key id of an MCP agent key caller, recorded in audit rows; null for every other caller.</summary>
    public string? AgentKeyId { get; init; }

    /// <summary>Whether the caller authenticated with an MCP agent key.</summary>
    public bool IsAgentKey => AgentKeyId is not null;

    /// <summary>Reads the caller from <paramref name="user"/>.</summary>
    public static McpCaller From(ClaimsPrincipal? user, McpAuthenticationMode mode)
    {
        if (mode == McpAuthenticationMode.LocalDevelopment || user is null)
            return new McpCaller(mode == McpAuthenticationMode.LocalDevelopment, null, null, null, false, []);

        // A key is a workload named after its agent; it has no tenant, person or Entra client.
        if (McpAgentKeyDefaults.Read(user) is { } key)
        {
            return new McpCaller(false, null, key.Principal, key.Principal, true, [key.Principal])
            {
                AgentId = key.AgentId,
                AgentKeyId = key.KeyId,
            };
        }

        var identifiers = EndpointAuthorizationService.ResolveIdentifiers(user)
            .Concat(user.FindAll("groups").Select(claim => claim.Value.Trim()))
            .Where(value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new McpCaller(
            false,
            user.FindFirstValue("tid"),
            user.FindFirstValue("oid") ?? user.FindFirstValue("sub"),
            user.FindFirstValue("azp") ?? user.FindFirstValue("appid"),
            !user.HasClaim(claim => claim.Type == "scp"),
            identifiers);
    }
}
