namespace NimBus.WebApp.Services.AgentApiKeys;

/// <summary>Names shared by the agent API key scheme (Spec 039).</summary>
public static class AgentApiKeyDefaults
{
    /// <summary>The authentication scheme. Never a default scheme: only the agent API names it.</summary>
    public const string SchemeName = "AgentApiKey";

    /// <summary>
    /// The request header carrying the key. Not <c>Authorization: Bearer</c>, which the Entra
    /// policy schemes forward to JwtBearer.
    /// </summary>
    public const string HeaderName = "X-NimBus-Agent-Key";

    /// <summary>The authorization policy on <c>/api/agent/*</c>: the sign-in scheme plus this one.</summary>
    public const string PolicyName = "AgentApi";

    /// <summary>Claim carrying the agent id the key was issued for.</summary>
    public const string AgentIdClaim = "nimbus:agent_id";

    /// <summary>Claim carrying the key's public id.</summary>
    public const string KeyIdClaim = "nimbus:agent_key_id";
}
