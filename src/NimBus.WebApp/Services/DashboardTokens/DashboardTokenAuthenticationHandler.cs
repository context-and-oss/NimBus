using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace NimBus.WebApp.Services.DashboardTokens;

/// <summary>
/// Authenticates a wall display by the token in <see cref="DashboardTokenDefaults.HeaderName"/>.
/// The principal carries only the token id: no email, <c>oid</c> or <c>groups</c> claim, so it can
/// never match the platform-admin marker or an ACL entry. Only the Monitor feed's authorization
/// policy names this scheme.
/// </summary>
public sealed partial class DashboardTokenAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IDashboardTokenProvider _tokens;
    private readonly IDashboardTokenUsageRecorder _usage;
    private readonly TimeProvider _time;

    /// <summary>Creates the handler.</summary>
    public DashboardTokenAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IDashboardTokenProvider tokens,
        IDashboardTokenUsageRecorder usage,
        TimeProvider time)
        : base(options, logger, encoder)
    {
        _tokens = tokens;
        _usage = usage;
        _time = time;
    }

    /// <inheritdoc/>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Request.Headers[DashboardTokenDefaults.HeaderName].ToString();
        if (string.IsNullOrEmpty(presented))
            return AuthenticateResult.NoResult();

        if (!SecretTokenFormat.TryParse(DashboardTokenDefaults.Prefix, presented, out var tokenId, out var secret))
            return Refuse("malformed", tokenId: null);

        var settings = await _tokens.GetAsync().ConfigureAwait(false);
        if (settings is null)
            return Refuse("token store unavailable", tokenId);

        var token = settings.Tokens.FirstOrDefault(t => string.Equals(t.TokenId, tokenId, StringComparison.Ordinal));
        if (token is null || !SecretTokenFormat.SecretMatches(secret, token.SecretHash))
            return Refuse("unknown token", tokenId);
        if (token.RevokedAtUtc is not null)
            return Refuse("revoked", tokenId);
        if (token.ExpiresAtUtc is { } expires && _time.GetUtcNow().UtcDateTime >= expires)
            return Refuse("expired", tokenId);

        var principalName = "dashboard:" + token.TokenId;
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, principalName),
                new Claim(ClaimTypes.Name, principalName),
                new Claim(DashboardTokenDefaults.TokenIdClaim, token.TokenId),
            },
            DashboardTokenDefaults.SchemeName);

        await _usage.RecordAsync(token.TokenId).ConfigureAwait(false);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), DashboardTokenDefaults.SchemeName));
    }

    private AuthenticateResult Refuse(string reason, string? tokenId)
    {
        // Never log the presented value: it is a credential.
        LogRefused(Logger, reason, tokenId ?? "-", Context.Connection.RemoteIpAddress?.ToString() ?? "-");
        // The failure message stays server-side; the client only sees 401.
        return AuthenticateResult.Fail("Dashboard token refused: " + reason + ".");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Dashboard token refused ({Reason}); token id {TokenId}, client {ClientIp}")]
    private static partial void LogRefused(ILogger logger, string reason, string tokenId, string clientIp);
}
