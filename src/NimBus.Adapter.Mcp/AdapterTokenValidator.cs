using System.Security.Claims;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace NimBus.Adapter.Mcp;

/// <summary>The verdict on a caller's bearer token.</summary>
public enum AdapterTokenStatus
{
    /// <summary>A token for the adapter resource from the tenant, carrying the read role.</summary>
    Valid = 0,

    /// <summary>No bearer token: answered with 401.</summary>
    Missing = 1,

    /// <summary>A token that fails validation, such as one for NimBus or another tenant: answered with 401.</summary>
    Invalid = 2,

    /// <summary>A valid token without the read role: answered with 403.</summary>
    Forbidden = 3,
}

/// <summary>The result of <see cref="AdapterTokenValidator.ValidateAsync"/>.</summary>
public sealed class AdapterTokenValidationResult
{
    internal AdapterTokenValidationResult(AdapterTokenStatus status, ClaimsPrincipal? principal = null, string? objectId = null, string? applicationId = null)
    {
        Status = status;
        Principal = principal;
        ObjectId = objectId;
        ApplicationId = applicationId;
    }

    /// <summary>The verdict.</summary>
    public AdapterTokenStatus Status { get; }

    /// <summary>The caller, when the token is <see cref="AdapterTokenStatus.Valid"/>.</summary>
    public ClaimsPrincipal? Principal { get; }

    /// <summary>The caller's object id (<c>oid</c>), when the token validated.</summary>
    public string? ObjectId { get; }

    /// <summary>The calling client's application id: <c>azp</c> in a v2 token, <c>appid</c> in a v1 token.</summary>
    public string? ApplicationId { get; }
}

/// <summary>
/// Accepts only Entra access tokens issued for the adapter MCP resource by the configured tenant
/// and carrying the read role (Spec 051 §7). The audience is the resource's client id or App ID
/// URI, so a token for NimBus, or for any other resource, is refused; the issuer is the tenant's
/// v1 or v2 issuer; the role is read from <c>roles</c>. Signing keys come from the tenant's OIDC
/// metadata and are refreshed once when a token names a key the cached metadata lacks.
/// </summary>
public sealed class AdapterTokenValidator
{
    private const string RoleClaim = "roles";

    private static readonly JsonWebTokenHandler Handler = new() { MapInboundClaims = false };

    private readonly IConfigurationManager<OpenIdConnectConfiguration> _metadata;
    private readonly string _readRole;
    private readonly string[] _audiences;
    private readonly string[] _issuers;

    /// <summary>A validator reading the tenant's OIDC metadata from <see cref="AdapterMcpOptions.Instance"/>.</summary>
    /// <param name="options">The adapter MCP options.</param>
    /// <exception cref="InvalidOperationException"><see cref="AdapterMcpOptions.TenantId"/> or <see cref="AdapterMcpOptions.Audience"/> is not set.</exception>
    public AdapterTokenValidator(AdapterMcpOptions options)
        : this(options, Metadata(options))
    {
    }

    /// <summary>A validator reading signing keys from <paramref name="metadata"/>.</summary>
    /// <param name="options">The adapter MCP options.</param>
    /// <param name="metadata">The tenant's OIDC metadata.</param>
    /// <exception cref="InvalidOperationException"><see cref="AdapterMcpOptions.TenantId"/> or <see cref="AdapterMcpOptions.Audience"/> is not set.</exception>
    public AdapterTokenValidator(AdapterMcpOptions options, IConfigurationManager<OpenIdConnectConfiguration> metadata)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(metadata);
        Require(options);

        _metadata = metadata;
        _readRole = string.IsNullOrWhiteSpace(options.ReadRole) ? new AdapterMcpOptions().ReadRole : options.ReadRole;
        _audiences = [options.Audience!, options.ResolvedApplicationIdUri];
        _issuers = [options.Authority, $"https://sts.windows.net/{options.TenantId}/"];
    }

    /// <summary>Validates the value of an <c>Authorization</c> header.</summary>
    /// <param name="authorization">The header's value, or null when absent.</param>
    /// <param name="cancellationToken">Cancels a metadata download.</param>
    public async Task<AdapterTokenValidationResult> ValidateAsync(string? authorization, CancellationToken cancellationToken)
    {
        const string scheme = "Bearer ";
        if (authorization is null
            || !authorization.StartsWith(scheme, StringComparison.OrdinalIgnoreCase)
            || string.IsNullOrWhiteSpace(authorization[scheme.Length..]))
        {
            return new AdapterTokenValidationResult(AdapterTokenStatus.Missing);
        }

        var token = authorization[scheme.Length..].Trim();
        var result = await ValidateTokenAsync(token, refresh: false, cancellationToken).ConfigureAwait(false);
        if (result.Exception is SecurityTokenSignatureKeyNotFoundException)
        {
            // A key rollover: the token names a key published after the metadata was cached.
            _metadata.RequestRefresh();
            result = await ValidateTokenAsync(token, refresh: true, cancellationToken).ConfigureAwait(false);
        }

        if (!result.IsValid)
            return new AdapterTokenValidationResult(AdapterTokenStatus.Invalid);

        var identity = result.ClaimsIdentity;
        var objectId = identity.FindFirst("oid")?.Value;
        var applicationId = identity.FindFirst("azp")?.Value ?? identity.FindFirst("appid")?.Value;
        if (!identity.HasClaim(RoleClaim, _readRole))
            return new AdapterTokenValidationResult(AdapterTokenStatus.Forbidden, objectId: objectId, applicationId: applicationId);

        return new AdapterTokenValidationResult(AdapterTokenStatus.Valid, new ClaimsPrincipal(identity), objectId, applicationId);
    }

    private async Task<TokenValidationResult> ValidateTokenAsync(string token, bool refresh, CancellationToken cancellationToken)
    {
        OpenIdConnectConfiguration configuration;
        try
        {
            configuration = await _metadata.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (refresh && exception is not OperationCanceledException)
        {
            // The refresh failed: answer with the first attempt's verdict rather than a 500.
            return new TokenValidationResult { IsValid = false, Exception = exception };
        }

        return await Handler.ValidateTokenAsync(token, new TokenValidationParameters
        {
            ValidAudiences = _audiences,
            ValidIssuers = _issuers,
            IssuerSigningKeys = configuration.SigningKeys,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ValidateLifetime = true,
            NameClaimType = "name",
            RoleClaimType = RoleClaim,
        }).ConfigureAwait(false);
    }

    private static void Require(AdapterMcpOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.TenantId))
            throw new InvalidOperationException($"{AdapterMcpOptions.SectionName}:{nameof(AdapterMcpOptions.TenantId)} is required: the Entra tenant whose tokens the adapter accepts.");
        if (string.IsNullOrWhiteSpace(options.Audience))
            throw new InvalidOperationException($"{AdapterMcpOptions.SectionName}:{nameof(AdapterMcpOptions.Audience)} is required: the client id of the adapter MCP resource registration.");
    }

    private static ConfigurationManager<OpenIdConnectConfiguration> Metadata(AdapterMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Require(options);
        return new ConfigurationManager<OpenIdConnectConfiguration>(
            $"{options.Authority}/.well-known/openid-configuration",
            new OpenIdConnectConfigurationRetriever(),
            new HttpDocumentRetriever { RequireHttps = true });
    }
}
