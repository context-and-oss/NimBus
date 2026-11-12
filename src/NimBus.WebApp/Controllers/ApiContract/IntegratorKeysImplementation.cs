using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using NimBus.Core;
using NimBus.MessageStore;
using NimBus.MessageStore.States;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.IntegratorApiKeys;
using Api = NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Controllers.ApiContract;

/// <summary>
/// Settings → Integrator keys API (Spec 045). Site Owner only, like every other
/// <c>/api/v1/admin/*</c> operation, so an integrator key can never manage keys. Writes are always
/// audited, never with the key itself; their <c>X-NimBus-CSRF</c> antiforgery token is checked by
/// <c>[AutoValidateAntiforgeryToken]</c> on the generated controller before they get here.
/// </summary>
public class IntegratorKeysImplementation : Api.IIntegratorKeysApiController
{
    private readonly HttpContext _context;
    private readonly IEndpointAuthorizationService _authorization;
    private readonly IAuditLogService _audit;
    private readonly IAntiforgery _antiforgery;
    private readonly IntegratorApiKeyService _keys;
    private readonly IPlatform _platform;

    /// <summary>Creates the controller implementation.</summary>
    public IntegratorKeysImplementation(
        IHttpContextAccessor contextAccessor,
        IEndpointAuthorizationService authorization,
        IAuditLogService audit,
        IAntiforgery antiforgery,
        IntegratorApiKeyService keys,
        IPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(contextAccessor);
        _context = contextAccessor.HttpContext!;
        _authorization = authorization;
        _audit = audit;
        _antiforgery = antiforgery;
        _keys = keys;
        _platform = platform;
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.IntegratorKeyState>> GetAdminIntegratorKeysAsync()
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();
        if (!_keys.IsAvailable)
            return NotImplemented();

        return new OkObjectResult(await StateAsync());
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.IntegratorKeyIssued>> PostAdminIntegratorKeysAsync(Api.IntegratorKeyCreate body)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _audit.LogAuditAsync(MessageAuditType.IntegratorApiKeyCreated, _context, accessDenied: true,
                data: JsonConvert.SerializeObject(new { name = body?.Name }));
            return new ForbidResult();
        }

        var request = new IntegratorApiKeyRequest(
            body?.Name,
            body?.Role == Api.IntegratorKeyRole.Contributor ? IntegratorApiKeyRole.Contributor : IntegratorApiKeyRole.Reader,
            body?.Endpoints,
            body?.CanReadPii ?? false,
            body?.ExpiresAtUtc);
        var change = await _keys.CreateAsync(request, KnownEndpoints(), _authorization.GetCurrentUserName());
        if (Refused(change) is { } refused)
            return refused;

        await AuditAsync(MessageAuditType.IntegratorApiKeyCreated, change.Key!);
        return new OkObjectResult(new Api.IntegratorKeyIssued
        {
            Key = change.FullKey!,
            Issued = ToApi(change.Key!, lastUsed: null),
            State = await StateAsync(),
        });
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.IntegratorKeyState>> PostAdminIntegratorKeyRevokeAsync(string keyId)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _audit.LogAuditAsync(MessageAuditType.IntegratorApiKeyRevoked, _context, accessDenied: true,
                data: JsonConvert.SerializeObject(new { keyId }));
            return new ForbidResult();
        }

        var change = await _keys.RevokeAsync(keyId, _authorization.GetCurrentUserName());
        if (Refused(change) is { } refused)
            return refused;

        await AuditAsync(MessageAuditType.IntegratorApiKeyRevoked, change.Key!);
        return new OkObjectResult(await StateAsync());
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.IntegratorKeyState>> DeleteAdminIntegratorKeyAsync(string keyId)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _audit.LogAuditAsync(MessageAuditType.IntegratorApiKeyRemoved, _context, accessDenied: true,
                data: JsonConvert.SerializeObject(new { keyId }));
            return new ForbidResult();
        }

        var change = await _keys.RemoveAsync(keyId, _authorization.GetCurrentUserName());
        if (Refused(change) is { } refused)
            return refused;

        await AuditAsync(MessageAuditType.IntegratorApiKeyRemoved, change.Key!);
        return new OkObjectResult(await StateAsync());
    }

    private List<string> KnownEndpoints()
        => _platform.Endpoints.Select(e => e.Id).Order(StringComparer.OrdinalIgnoreCase).ToList();

    private async Task<Api.IntegratorKeyState> StateAsync()
    {
        var (settings, lastUsed) = await _keys.ListAsync();
        return new Api.IntegratorKeyState
        {
            Keys = settings.Keys
                .OrderByDescending(k => k.CreatedAtUtc)
                .Select(k => ToApi(k, lastUsed.TryGetValue(k.KeyId, out var used) ? used : null))
                .ToList(),
            Endpoints = KnownEndpoints(),
            CsrfToken = _antiforgery.GetAndStoreTokens(_context).RequestToken ?? string.Empty,
        };
    }

    private Api.IntegratorKeyInfo ToApi(IntegratorApiKey key, DateTime? lastUsed) => new()
    {
        KeyId = key.KeyId,
        Prefix = IntegratorApiKeyDefaults.Prefix + key.KeyId,
        Name = key.Name,
        Role = key.Role == IntegratorApiKeyRole.Contributor ? Api.IntegratorKeyRole.Contributor : Api.IntegratorKeyRole.Reader,
        Endpoints = key.Endpoints?.ToList(),
        CanReadPii = key.CanReadPii,
        Status = _keys.StatusOf(key) switch
        {
            IntegratorApiKeyStatus.Revoked => Api.IntegratorKeyStatus.Revoked,
            IntegratorApiKeyStatus.Expired => Api.IntegratorKeyStatus.Expired,
            _ => Api.IntegratorKeyStatus.Active,
        },
        CreatedAtUtc = key.CreatedAtUtc,
        CreatedBy = key.CreatedBy,
        ExpiresAtUtc = key.ExpiresAtUtc,
        RevokedAtUtc = key.RevokedAtUtc,
        RevokedBy = key.RevokedBy,
        LastUsedAtUtc = lastUsed,
    };

    private Task AuditAsync(MessageAuditType type, IntegratorApiKey key)
        => _audit.LogAuditAsync(type, _context, data: JsonConvert.SerializeObject(new
        {
            keyId = key.KeyId,
            name = key.Name,
            role = key.Role.ToString(),
            endpoints = key.Endpoints,
            canReadPii = key.CanReadPii,
            expiresAtUtc = key.ExpiresAtUtc,
        }));

    private static ObjectResult? Refused(IntegratorApiKeyChange change) => change.Status switch
    {
        IntegratorApiKeyChangeStatus.Saved => null,
        IntegratorApiKeyChangeStatus.Invalid => Problem(400, "Invalid", change.Errors ?? []),
        IntegratorApiKeyChangeStatus.NotFound => Problem(404, "NotFound", ["No integrator API key has that id."]),
        IntegratorApiKeyChangeStatus.StillActive => Problem(400, "StillActive", ["Revoke the key before removing it."]),
        IntegratorApiKeyChangeStatus.Unavailable => NotImplemented(),
        _ => Problem(409, "RevisionConflict", ["The integrator API keys kept changing while saving. Try again."]),
    };

    private static ObjectResult Problem(int status, string code, IEnumerable<string> errors)
        => new(new Api.ProblemDetails { Code = code, Errors = errors.ToList() }) { StatusCode = status };

    private static ObjectResult NotImplemented()
        => Problem(StatusCodes.Status501NotImplemented, "Unavailable", ["The storage provider cannot store integrator API keys."]);

    private Task<bool> IsSiteOwnerAsync() => _authorization.HasRoleAsync(AccessRole.Owner);
}
