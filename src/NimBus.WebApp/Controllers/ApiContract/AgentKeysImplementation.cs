using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using NimBus.MessageStore;
using NimBus.MessageStore.States;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.AgentApiKeys;
using Api = NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Controllers.ApiContract;

/// <summary>
/// Settings → Agent keys API (Spec 039). Site Owner only, like every other <c>/api/admin/*</c>
/// operation. Writes are always audited, never with the key itself; their <c>X-NimBus-CSRF</c>
/// antiforgery token is checked by <c>[AutoValidateAntiforgeryToken]</c> on the generated
/// controller before they get here.
/// </summary>
public class AgentKeysImplementation : Api.IAgentKeysApiController
{
    private readonly HttpContext _context;
    private readonly IEndpointAuthorizationService _authorization;
    private readonly IAuditLogService _audit;
    private readonly IAntiforgery _antiforgery;
    private readonly AgentApiKeyService _keys;

    /// <summary>Creates the controller implementation.</summary>
    public AgentKeysImplementation(
        IHttpContextAccessor contextAccessor,
        IEndpointAuthorizationService authorization,
        IAuditLogService audit,
        IAntiforgery antiforgery,
        AgentApiKeyService keys)
    {
        ArgumentNullException.ThrowIfNull(contextAccessor);
        _context = contextAccessor.HttpContext!;
        _authorization = authorization;
        _audit = audit;
        _antiforgery = antiforgery;
        _keys = keys;
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.AgentApiKeyState>> GetAdminAgentKeysAsync()
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();

        return new OkObjectResult(await StateAsync());
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.AgentApiKeyIssued>> PostAdminAgentKeysAsync(Api.AgentApiKeyCreate body)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _audit.LogAuditAsync(MessageAuditType.AgentApiKeyCreated, _context, accessDenied: true,
                data: JsonConvert.SerializeObject(new { agentId = body?.AgentId }));
            return new ForbidResult();
        }

        var change = await _keys.CreateAsync(body?.AgentId, body?.Name, body?.ExpiresAtUtc, _authorization.GetCurrentUserName());
        if (Refused(change) is { } refused)
            return refused;

        await AuditAsync(MessageAuditType.AgentApiKeyCreated, change.Key!);
        return new OkObjectResult(new Api.AgentApiKeyIssued
        {
            Key = change.FullKey!,
            Issued = ToApi(change.Key!, lastUsed: null),
            State = await StateAsync(),
        });
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.AgentApiKeyState>> PostAdminAgentKeyRevokeAsync(string keyId)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _audit.LogAuditAsync(MessageAuditType.AgentApiKeyRevoked, _context, accessDenied: true,
                data: JsonConvert.SerializeObject(new { keyId }));
            return new ForbidResult();
        }

        var change = await _keys.RevokeAsync(keyId, _authorization.GetCurrentUserName());
        if (Refused(change) is { } refused)
            return refused;

        await AuditAsync(MessageAuditType.AgentApiKeyRevoked, change.Key!);
        return new OkObjectResult(await StateAsync());
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.AgentApiKeyState>> DeleteAdminAgentKeyAsync(string keyId)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _audit.LogAuditAsync(MessageAuditType.AgentApiKeyRemoved, _context, accessDenied: true,
                data: JsonConvert.SerializeObject(new { keyId }));
            return new ForbidResult();
        }

        var change = await _keys.RemoveAsync(keyId, _authorization.GetCurrentUserName());
        if (Refused(change) is { } refused)
            return refused;

        await AuditAsync(MessageAuditType.AgentApiKeyRemoved, change.Key!);
        return new OkObjectResult(await StateAsync());
    }

    private async Task<Api.AgentApiKeyState> StateAsync()
    {
        var (settings, lastUsed) = await _keys.ListAsync();
        return new Api.AgentApiKeyState
        {
            Keys = settings.Keys
                .OrderByDescending(k => k.CreatedAtUtc)
                .Select(k => ToApi(k, lastUsed.TryGetValue(k.KeyId, out var used) ? used : null))
                .ToList(),
            CsrfToken = _antiforgery.GetAndStoreTokens(_context).RequestToken ?? string.Empty,
        };
    }

    private Api.AgentApiKeyInfo ToApi(AgentApiKey key, DateTime? lastUsed) => new()
    {
        KeyId = key.KeyId,
        Prefix = AgentApiKeyFormat.Prefix + key.KeyId,
        AgentId = key.AgentId,
        Name = key.Name,
        Status = _keys.StatusOf(key) switch
        {
            AgentApiKeyStatus.Revoked => Api.AgentApiKeyStatus.Revoked,
            AgentApiKeyStatus.Expired => Api.AgentApiKeyStatus.Expired,
            _ => Api.AgentApiKeyStatus.Active,
        },
        CreatedAtUtc = key.CreatedAtUtc,
        CreatedBy = key.CreatedBy,
        ExpiresAtUtc = key.ExpiresAtUtc,
        RevokedAtUtc = key.RevokedAtUtc,
        RevokedBy = key.RevokedBy,
        LastUsedAtUtc = lastUsed,
    };

    private Task AuditAsync(MessageAuditType type, AgentApiKey key)
        => _audit.LogAuditAsync(type, _context, data: JsonConvert.SerializeObject(new
        {
            keyId = key.KeyId,
            agentId = key.AgentId,
            name = key.Name,
            expiresAtUtc = key.ExpiresAtUtc,
        }));

    private static ObjectResult? Refused(AgentApiKeyChange change) => change.Status switch
    {
        AgentApiKeyChangeStatus.Saved => null,
        AgentApiKeyChangeStatus.Invalid => Problem(400, "Invalid", change.Errors ?? []),
        AgentApiKeyChangeStatus.NotFound => Problem(404, "NotFound", ["No agent API key has that id."]),
        AgentApiKeyChangeStatus.StillActive => Problem(400, "StillActive", ["Revoke the key before removing it."]),
        _ => Problem(409, "RevisionConflict", ["The agent keys kept changing while saving. Try again."]),
    };

    private static ObjectResult Problem(int status, string code, IEnumerable<string> errors)
        => new(new Api.AgentApiKeyProblem { Code = code, Errors = errors.ToList() }) { StatusCode = status };

    private Task<bool> IsSiteOwnerAsync() => _authorization.HasRoleAsync(AccessRole.Owner);
}
