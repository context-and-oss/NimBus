#pragma warning disable CA1707, CA2007
using System.Security.Cryptography;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace NimBus.Adapter.Mcp.Tests.Infrastructure;

/// <summary>
/// Locally signed Entra-shaped access tokens and the offline OIDC configuration that trusts them.
/// Entra signs every tenant's tokens with the same keys, so a token from another tenant is signed
/// with the trusted key here too: only its issuer can tell it apart.
/// </summary>
internal static class TestTokens
{
    public const string TenantId = "aaaaaaaa-0000-0000-0000-000000000001";
    public const string OtherTenantId = "aaaaaaaa-0000-0000-0000-000000000002";

    /// <summary>The adapter MCP resource registration (Spec 051 §7).</summary>
    public const string AdapterClientId = "bbbbbbbb-0000-0000-0000-000000000001";
    public const string AdapterAppIdUri = "api://nimbus-adapters";

    /// <summary>The NimBus operator MCP resource: a separate registration.</summary>
    public const string NimBusClientId = "cccccccc-0000-0000-0000-000000000001";

    public const string CallerObjectId = "dddddddd-0000-0000-0000-000000000001";
    public const string CallerAppId = "eeeeeeee-0000-0000-0000-000000000001";
    public const string ReadRole = "Adapter.Read";

    private static readonly RsaSecurityKey TrustedKey = new(RSA.Create(2048)) { KeyId = "trusted" };
    private static readonly RsaSecurityKey UntrustedKey = new(RSA.Create(2048)) { KeyId = "untrusted" };

    public static string V2Issuer(string tenantId = TenantId) => $"https://login.microsoftonline.com/{tenantId}/v2.0";

    public static string V1Issuer(string tenantId = TenantId) => $"https://sts.windows.net/{tenantId}/";

    public static AdapterMcpOptions Options() => new()
    {
        TenantId = TenantId,
        Audience = AdapterClientId,
        ApplicationIdUri = AdapterAppIdUri,
    };

    public static Dictionary<string, string?> Settings() => new()
    {
        ["AdapterMcp:TenantId"] = TenantId,
        ["AdapterMcp:Audience"] = AdapterClientId,
        ["AdapterMcp:ApplicationIdUri"] = AdapterAppIdUri,
        ["AdapterMcp:PiiHashSalt"] = "test-salt",
    };

    /// <summary>The tenant's OIDC metadata, served without a network call.</summary>
    public static IConfigurationManager<OpenIdConnectConfiguration> Metadata()
    {
        var configuration = new OpenIdConnectConfiguration { Issuer = V2Issuer() };
        configuration.SigningKeys.Add(TrustedKey);
        return new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
    }

    public static AdapterTokenValidator Validator() => new(Options(), Metadata());

    /// <summary>
    /// A token for the adapter resource carrying the read role, unless told otherwise. A v1 token
    /// names its client in <c>appid</c>, a v2 token in <c>azp</c>.
    /// </summary>
    public static string Create(
        string audience = AdapterClientId,
        string? issuer = null,
        string[]? roles = null,
        bool v1 = false,
        bool untrustedKey = false,
        DateTime? expires = null)
    {
        var claims = new Dictionary<string, object>
        {
            ["oid"] = CallerObjectId,
            ["tid"] = TenantId,
            [v1 ? "appid" : "azp"] = CallerAppId,
            ["roles"] = roles ?? [ReadRole],
        };

        var notBefore = (expires ?? DateTime.UtcNow.AddMinutes(10)).AddMinutes(-30);
        return new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
        {
            Issuer = issuer ?? (v1 ? V1Issuer() : V2Issuer()),
            Audience = audience,
            Claims = claims,
            NotBefore = notBefore,
            IssuedAt = notBefore,
            Expires = expires ?? DateTime.UtcNow.AddMinutes(10),
            SigningCredentials = new SigningCredentials(untrustedKey ? UntrustedKey : TrustedKey, SecurityAlgorithms.RsaSha256),
        });
    }
}
