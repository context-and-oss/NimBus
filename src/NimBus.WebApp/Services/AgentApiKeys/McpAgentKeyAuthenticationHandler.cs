using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services.AgentApiKeys;

/// <summary>
/// Authenticates an agent API key issued for MCP (Spec 052 §6.4), sent as
/// <c>Authorization: Bearer nbak_…</c> or in <see cref="AgentApiKeyDefaults.HeaderName"/>. Only the
/// MCP policy scheme forwards here, and only when the deployment allows keys. The principal is a
/// workload: it names the agent and the key's grant and carries no <c>scp</c>, <c>roles</c>,
/// <c>oid</c>, <c>tid</c>, client or <c>groups</c> claim.
/// </summary>
public sealed partial class McpAgentKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IAgentApiKeyProvider _keys;
    private readonly IAgentApiKeyUsageRecorder _usage;
    private readonly TimeProvider _time;

    /// <summary>Creates the handler.</summary>
    public McpAgentKeyAuthenticationHandler(
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
        var headers = Request.Headers[AgentApiKeyDefaults.HeaderName];
        var authorization = Request.Headers.Authorization;
        string? presented;
        if (headers.Count > 0)
        {
            // One credential per request (§6.2): never merge a key with a token or a second key.
            if (headers.Count > 1 || authorization.Count > 0)
                return Refuse("more than one credential", keyId: null);
            presented = headers[0];
        }
        else
        {
            if (authorization.Count > 1)
                return Refuse("more than one credential", keyId: null);
            presented = McpAgentKeyDefaults.BearerValue(authorization);
            if (presented is null || !presented.StartsWith(AgentApiKeyFormat.Prefix, StringComparison.Ordinal))
                return AuthenticateResult.NoResult();
        }

        if (!AgentApiKeyFormat.TryParse(presented, out var keyId, out var secret))
            return Refuse("malformed", keyId: null);

        var settings = await _keys.GetAsync().ConfigureAwait(false);
        var now = _time.GetUtcNow();
        if (settings is null)
            return Refuse("key store unavailable", keyId);
        if (_keys.LastReadAtUtc is not { } lastRead || now - lastRead > McpAgentKeyDefaults.StalenessBound)
            return Refuse("key list is stale", keyId);

        // A key issued for the agent API is refused like an unknown key: it opens /api/agent/* only.
        var key = settings.Keys.FirstOrDefault(k => string.Equals(k.KeyId, keyId, StringComparison.Ordinal));
        if (key is null || key.Use != AgentApiKeyUse.Mcp || !AgentApiKeyFormat.SecretMatches(secret, key.SecretHash))
            return Refuse("unknown key", keyId);
        if (key.RevokedAtUtc is not null)
            return Refuse("revoked", keyId);
        if (key.ExpiresAtUtc is not { } expires)
            return Refuse("no expiry", keyId);
        if (now.UtcDateTime >= expires)
            return Refuse("expired", keyId);

        var principalName = "agent:" + key.AgentId;
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, principalName),
            new(ClaimTypes.Name, principalName),
            new(AgentApiKeyDefaults.AgentIdClaim, key.AgentId),
            new(AgentApiKeyDefaults.KeyIdClaim, key.KeyId),
            new(McpAgentKeyDefaults.ExpiresAtClaim, expires.ToString("O", CultureInfo.InvariantCulture)),
            new(McpAgentKeyDefaults.PermissionClaim, McpAgentKeyDefaults.ObservePermission),
        };
        var grant = key.Mcp ?? new AgentApiKeyMcpGrant();
        if (grant.Classify)
            claims.Add(new Claim(McpAgentKeyDefaults.PermissionClaim, McpAgentKeyDefaults.ClassifyPermission));
        if (grant.Annotate)
            claims.Add(new Claim(McpAgentKeyDefaults.PermissionClaim, McpAgentKeyDefaults.AnnotatePermission));
        if (grant.Resubmit)
            claims.Add(new Claim(McpAgentKeyDefaults.PermissionClaim, McpAgentKeyDefaults.ResubmitPermission));
        if (grant.RequestsPerWindow is { } limit && limit >= 1)
            claims.Add(new Claim(McpAgentKeyDefaults.RequestsPerWindowClaim, limit.ToString(CultureInfo.InvariantCulture)));

        var identity = new ClaimsIdentity(claims, McpAgentKeyDefaults.SchemeName);
        await _usage.RecordAsync(key.KeyId).ConfigureAwait(false);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), McpAgentKeyDefaults.SchemeName));
    }

    private AuthenticateResult Refuse(string reason, string? keyId)
    {
        // Never log the presented value: it may be a real key sent to the wrong place.
        LogMcpAgentKeyRefused(Logger, reason, keyId ?? "-", Context.Connection.RemoteIpAddress?.ToString() ?? "-");
        return AuthenticateResult.Fail("MCP agent key refused: " + reason + ".");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "MCP agent key refused ({Reason}); key id {KeyId}, client {ClientIp}")]
    private static partial void LogMcpAgentKeyRefused(ILogger logger, string reason, string keyId, string clientIp);
}
