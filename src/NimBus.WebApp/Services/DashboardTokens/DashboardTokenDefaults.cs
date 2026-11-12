namespace NimBus.WebApp.Services.DashboardTokens;

/// <summary>Names shared by the Monitor dashboard token scheme.</summary>
public static class DashboardTokenDefaults
{
    /// <summary>The authentication scheme. Never a default scheme: only the Monitor feed names it.</summary>
    public const string SchemeName = "DashboardToken";

    /// <summary>
    /// The request header carrying the token. Not <c>Authorization: Bearer</c>, which the Entra
    /// policy schemes forward to JwtBearer.
    /// </summary>
    public const string HeaderName = "X-NimBus-Dashboard-Token";

    /// <summary>The authorization policy on <c>/api/monitor/feed/*</c>: the sign-in scheme plus this one.</summary>
    public const string PolicyName = "MonitorFeed";

    /// <summary>Claim carrying the token's public id; its presence marks a token caller.</summary>
    public const string TokenIdClaim = "nimbus:dashboard_token_id";

    /// <summary>Marks the value as a NimBus dashboard token, also for secret scanners.</summary>
    public const string Prefix = "nbdt_";
}
