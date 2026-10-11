using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace NimBus.WebApp.Services.AgentApiKeys;

/// <summary>
/// Authenticates an agent by the key in <see cref="AgentApiKeyDefaults.HeaderName"/> (Spec 039).
/// The principal names the agent the key was issued for and carries no <c>groups</c> or
/// <c>oid</c> claim, so it can never match the platform-admin marker or an ACL entry. Only
/// the agent API's authorization policy names this scheme.
/// </summary>
public sealed class AgentApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IAgentApiKeyProvider _keys;
    private readonly IAgentApiKeyUsageRecorder _usage;
    private readonly TimeProvider _time;

    /// <summary>Creates the handler.</summary>
    public AgentApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IAgentApiKeyProvider keys,
        IAgentApiKeyUsageRecorder usage,
        TimeProvider time)
        : base(options, logger, encoder)
    {
        _keys = keys;
        _usage = usage;
        _time = time;
    }

    /// <inheritdoc/>
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var presented = Request.Headers[AgentApiKeyDefaults.HeaderName].ToString();
        if (string.IsNullOrEmpty(presented))
            return AuthenticateResult.NoResult();

        if (!AgentApiKeyFormat.TryParse(presented, out var keyId, out var secret))
            return Refuse("malformed", keyId: null);

        var settings = await _keys.GetAsync().ConfigureAwait(false);
        if (settings is null)
            return Refuse("key store unavailable", keyId);

        var key = settings.Keys.FirstOrDefault(k => string.Equals(k.KeyId, keyId, StringComparison.Ordinal));
        if (key is null || !AgentApiKeyFormat.SecretMatches(secret, key.SecretHash))
            return Refuse("unknown key", keyId);
        if (key.RevokedAtUtc is not null)
            return Refuse("revoked", keyId);
        if (key.ExpiresAtUtc is { } expires && _time.GetUtcNow().UtcDateTime >= expires)
            return Refuse("expired", keyId);

        var principalName = "agent:" + key.AgentId;
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, principalName),
                new Claim(ClaimTypes.Name, principalName),
                new Claim(AgentApiKeyDefaults.AgentIdClaim, key.AgentId),
                new Claim(AgentApiKeyDefaults.KeyIdClaim, key.KeyId),
            },
            AgentApiKeyDefaults.SchemeName);

        await _usage.RecordAsync(key.KeyId).ConfigureAwait(false);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), AgentApiKeyDefaults.SchemeName));
    }

    private AuthenticateResult Refuse(string reason, string? keyId)
    {
        // Never log the presented value: it may be a real key sent to the wrong place.
        Logger.LogWarning(
            "Agent API key refused ({Reason}); key id {KeyId}, client {ClientIp}",
            reason,
            keyId ?? "-",
            Context.Connection.RemoteIpAddress?.ToString() ?? "-");
        // The failure message stays server-side; the client only sees 401.
        return AuthenticateResult.Fail("Agent API key refused: " + reason + ".");
    }
}
