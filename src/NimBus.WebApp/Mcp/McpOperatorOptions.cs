namespace NimBus.WebApp.Mcp;

/// <summary>
/// Operator MCP endpoint settings, bound from the <c>NimBus:Mcp</c> configuration section
/// (Spec 035). The endpoint is off unless <see cref="Enabled"/> is set.
/// </summary>
public sealed class McpOperatorOptions
{
    /// <summary>The configuration section these options bind from.</summary>
    public const string SectionName = "NimBus:Mcp";

    /// <summary>The route the MCP endpoint is served on.</summary>
    public const string Path = "/mcp";

    /// <summary>Maps the MCP endpoint. When false nothing is registered and <c>/mcp</c> is not routed.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Maps the MCP endpoint only when the local-dev bypass is active (Development and
    /// <c>EnableLocalDevAuthentication=true</c>); otherwise leaves it off without failing
    /// startup. The Aspire AppHost sets this, since only the WebApp knows whether the bypass is on.
    /// </summary>
    public bool EnableForLocalDevelopment { get; set; }

    /// <summary>
    /// Browser origins allowed to call the endpoint. A request carrying any other
    /// <c>Origin</c> header is refused; requests without one (non-browser clients) are unaffected.
    /// In local development, localhost origins are also allowed.
    /// </summary>
    public string[] AllowedOrigins { get; set; } = [];

    /// <summary>The MCP resource's own Entra app registration.</summary>
    public McpEntraOptions Entra { get; set; } = new();

    /// <summary>Agent API keys on <c>/mcp</c> (Spec 052).</summary>
    public McpAgentKeyOptions AgentKeys { get; set; } = new();

    /// <summary>
    /// The most deferred messages one <c>nimbus_reprocess_deferred</c> may ask an endpoint to
    /// process again (Spec 035 Phase 3). A session with more is refused, not partly reprocessed:
    /// a person reprocesses it from the Web UI. Default 200.
    /// </summary>
    public int MaxDeferredReprocess { get; set; } = 200;
}

/// <summary>
/// The deployment's ceiling for agent API keys on <c>/mcp</c> (Spec 052 §6.5), bound from
/// <c>NimBus:Mcp:AgentKeys</c>. A key is a credential NimBus mints itself, so accepting one on the
/// operator tools is a deployment decision; a site Owner then turns keys on in Settings → MCP access.
/// </summary>
public sealed class McpAgentKeyOptions
{
    /// <summary>
    /// Accept MCP agent keys on <c>/mcp</c>, in Entra mode only. Default false: a key on <c>/mcp</c>
    /// then gets 401 whatever the Owner's policy says.
    /// </summary>
    public bool Enabled { get; set; }
}

/// <summary>
/// The Entra app registration that represents the MCP resource. It is separate from the
/// WebApp's own registration so MCP tokens carry their own audience and <c>nimbus.*</c> scopes.
/// </summary>
public sealed class McpEntraOptions
{
    /// <summary>The login host. Default <c>https://login.microsoftonline.com/</c>.</summary>
    public string Instance { get; set; } = "https://login.microsoftonline.com/";

    /// <summary>The tenant that issues MCP tokens.</summary>
    public string? TenantId { get; set; }

    /// <summary>The MCP resource's application (client) id.</summary>
    public string? ClientId { get; set; }

    /// <summary>The App ID URI. Default <c>api://{ClientId}</c>.</summary>
    public string? ApplicationIdUri { get; set; }

    /// <summary>
    /// The delegated scopes the app registration defines, by short name (<c>nimbus.observe</c>,
    /// <c>nimbus.resubmit</c>, ...). NimBus advertises them in the protected-resource metadata and
    /// names them in the 401 challenge, and MCP clients ask Entra for exactly these, so every one
    /// must exist on the registration or sign-in fails. Empty (the default): every scope NimBus
    /// understands. List fewer when the registration defines fewer, for example before adding the
    /// scopes a new release introduces, or only <c>nimbus.observe</c> for read-only access.
    /// </summary>
    public string[] Scopes { get; set; } = [];

    /// <summary>
    /// <see cref="Scopes"/>, or every scope when it is empty, in <see cref="McpOperatorPermissions.AllScopes"/> order.
    /// </summary>
    /// <exception cref="InvalidOperationException">An entry is blank or unknown, or <c>nimbus.observe</c> is missing.</exception>
    public IReadOnlyList<string> ResolveScopes()
    {
        if (Scopes.Length == 0)
            return McpOperatorPermissions.AllScopes;

        var unknown = Scopes.Where(scope => string.IsNullOrWhiteSpace(scope)
            || !McpOperatorPermissions.AllScopes.Contains(scope.Trim(), StringComparer.Ordinal)).ToList();
        if (unknown.Count > 0)
        {
            throw new InvalidOperationException(
                $"NimBus:Mcp:Entra:Scopes has entries that are blank or not NimBus scopes: '{string.Join("', '", unknown)}'. " +
                $"Known scopes: {string.Join(", ", McpOperatorPermissions.AllScopes)}.");
        }

        var configured = Scopes.Select(scope => scope.Trim()).ToHashSet(StringComparer.Ordinal);
        if (!configured.Contains(McpOperatorPermissions.ObserveScope))
        {
            throw new InvalidOperationException(
                $"NimBus:Mcp:Entra:Scopes must include {McpOperatorPermissions.ObserveScope}: every MCP tool needs it.");
        }

        return McpOperatorPermissions.AllScopes.Where(configured.Contains).ToList();
    }

    /// <summary>True when both <see cref="TenantId"/> and <see cref="ClientId"/> are set.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId);

    /// <summary>The v2.0 token issuer and OpenID Connect authority for <see cref="TenantId"/>.</summary>
    public string Authority => $"{Instance.TrimEnd('/')}/{TenantId}/v2.0";

    /// <summary>The App ID URI, falling back to <c>api://{ClientId}</c>.</summary>
    public string ResolvedApplicationIdUri => string.IsNullOrWhiteSpace(ApplicationIdUri) ? $"api://{ClientId}" : ApplicationIdUri;
}
