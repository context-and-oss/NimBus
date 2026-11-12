namespace NimBus.WebApp.Services.IntegratorApiKeys;

/// <summary>Names shared by the integrator API key scheme (Spec 045).</summary>
public static class IntegratorApiKeyDefaults
{
    /// <summary>
    /// The authentication scheme. Never a default scheme: <c>UseNimBusIntegratorApiKeys</c> runs it
    /// on <c>/api</c> requests that carry <see cref="HeaderName"/>, so a key authenticates on the
    /// REST API only.
    /// </summary>
    public const string SchemeName = "IntegratorApiKey";

    /// <summary>
    /// The request header carrying the key. Not <c>Authorization: Bearer</c>, which the Entra
    /// policy schemes forward to JwtBearer.
    /// </summary>
    public const string HeaderName = "X-NimBus-Api-Key";

    /// <summary>Claim carrying the key's public id; its presence marks a key caller.</summary>
    public const string KeyIdClaim = "nimbus:integrator_key_id";

    /// <summary>Marks the value as a NimBus integrator key, also for secret scanners.</summary>
    public const string Prefix = "nbik_";

    /// <summary>The principal name (and audit auditor) of a key caller.</summary>
    public static string PrincipalName(string keyId) => "integrator:" + keyId;
}
