using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace NimBus.WebApp.Services.IntegratorApiKeys;

/// <summary>
/// Authenticates a REST API caller by the key in <see cref="IntegratorApiKeyDefaults.HeaderName"/>.
/// The principal carries only the key id: no email, <c>oid</c> or <c>groups</c> claim, so it can
/// never match the platform-admin marker or an ACL entry (Spec 045). The global authorization
/// policy names this scheme; <see cref="EndpointAuthorizationService"/> grants the key's role from
/// its record, never from claims.
/// </summary>
public sealed partial class IntegratorApiKeyAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    private readonly IIntegratorApiKeyProvider _keys;
    private readonly IIntegratorApiKeyUsageRecorder _usage;
    private readonly TimeProvider _time;

    /// <summary>Creates the handler.</summary>
    public IntegratorApiKeyAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        IIntegratorApiKeyProvider keys,
        IIntegratorApiKeyUsageRecorder usage,
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
        var presented = Request.Headers[IntegratorApiKeyDefaults.HeaderName].ToString();
        if (string.IsNullOrEmpty(presented))
            return AuthenticateResult.NoResult();

        // MVC unions the global policy's schemes with a controller's own policy, so the agent API
        // and the Monitor feed would otherwise accept a key. They have their own credentials and
        // the agent API checks no role, so a key must not count there.
        if (IsExcludedSurface(Context))
            return AuthenticateResult.NoResult();

        if (!SecretTokenFormat.TryParse(IntegratorApiKeyDefaults.Prefix, presented, out var keyId, out var secret))
            return Refuse("malformed", keyId: null);

        var settings = await _keys.GetAsync().ConfigureAwait(false);
        if (settings is null)
            return Refuse("key store unavailable", keyId);

        var key = settings.Keys.FirstOrDefault(t => string.Equals(t.KeyId, keyId, StringComparison.Ordinal));
        if (key is null || !SecretTokenFormat.SecretMatches(secret, key.SecretHash))
            return Refuse("unknown key", keyId);
        if (key.RevokedAtUtc is not null)
            return Refuse("revoked", keyId);
        if (key.ExpiresAtUtc is { } expires && _time.GetUtcNow().UtcDateTime >= expires)
            return Refuse("expired", keyId);

        var principalName = IntegratorApiKeyDefaults.PrincipalName(key.KeyId);
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, principalName),
                new Claim(ClaimTypes.Name, principalName),
                new Claim(IntegratorApiKeyDefaults.KeyIdClaim, key.KeyId),
            },
            IntegratorApiKeyDefaults.SchemeName);

        await _usage.RecordAsync(key.KeyId).ConfigureAwait(false);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), IntegratorApiKeyDefaults.SchemeName));
    }

    /// <summary>True for the controllers that never accept an integrator API key.</summary>
    internal static bool IsExcludedSurface(HttpContext context)
    {
        var controller = context.GetEndpoint()?.Metadata
            .GetMetadata<Microsoft.AspNetCore.Mvc.Controllers.ControllerActionDescriptor>()?.ControllerTypeInfo.AsType();
        return controller == typeof(NimBus.WebApp.ManagementApi.AgentApiController)
            || controller == typeof(NimBus.WebApp.ManagementApi.MonitorFeedApiController);
    }

    private AuthenticateResult Refuse(string reason, string? keyId)
    {
        // Never log the presented value: it is a credential.
        LogRefused(Logger, reason, keyId ?? "-", Context.Connection.RemoteIpAddress?.ToString() ?? "-");
        // The failure message stays server-side; the client only sees 401.
        return AuthenticateResult.Fail("Integrator API key refused: " + reason + ".");
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Integrator API key refused ({Reason}); key id {KeyId}, client {ClientIp}")]
    private static partial void LogRefused(ILogger logger, string reason, string keyId, string clientIp);
}
