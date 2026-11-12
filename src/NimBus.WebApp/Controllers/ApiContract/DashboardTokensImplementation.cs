using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using NimBus.MessageStore;
using NimBus.MessageStore.States;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.DashboardTokens;
using Api = NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Controllers.ApiContract;

/// <summary>
/// Settings → Dashboard tokens API. Site Owner only, like every other <c>/api/admin/*</c>
/// operation. Writes are always audited, never with the token itself; their <c>X-NimBus-CSRF</c>
/// antiforgery token is checked by <c>[AutoValidateAntiforgeryToken]</c> on the generated
/// controller before they get here.
/// </summary>
public class DashboardTokensImplementation : Api.IDashboardTokensApiController
{
    private readonly HttpContext _context;
    private readonly IEndpointAuthorizationService _authorization;
    private readonly IAuditLogService _audit;
    private readonly IAntiforgery _antiforgery;
    private readonly DashboardTokenService _tokens;

    /// <summary>Creates the controller implementation.</summary>
    public DashboardTokensImplementation(
        IHttpContextAccessor contextAccessor,
        IEndpointAuthorizationService authorization,
        IAuditLogService audit,
        IAntiforgery antiforgery,
        DashboardTokenService tokens)
    {
        ArgumentNullException.ThrowIfNull(contextAccessor);
        _context = contextAccessor.HttpContext!;
        _authorization = authorization;
        _audit = audit;
        _antiforgery = antiforgery;
        _tokens = tokens;
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.DashboardTokenState>> GetAdminDashboardTokensAsync()
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();
        if (!_tokens.IsAvailable)
            return NotImplemented();

        return new OkObjectResult(await StateAsync());
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.DashboardTokenIssued>> PostAdminDashboardTokensAsync(Api.DashboardTokenCreate body)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _audit.LogAuditAsync(MessageAuditType.DashboardTokenCreated, _context, accessDenied: true,
                data: JsonConvert.SerializeObject(new { name = body?.Name }));
            return new ForbidResult();
        }

        var change = await _tokens.CreateAsync(body?.Name, body?.ExpiresAtUtc, _authorization.GetCurrentUserName());
        if (Refused(change) is { } refused)
            return refused;

        await AuditAsync(MessageAuditType.DashboardTokenCreated, change.Token!);
        return new OkObjectResult(new Api.DashboardTokenIssued
        {
            Token = change.FullToken!,
            Issued = ToApi(change.Token!, lastUsed: null),
            State = await StateAsync(),
        });
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.DashboardTokenState>> PostAdminDashboardTokenRevokeAsync(string tokenId)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _audit.LogAuditAsync(MessageAuditType.DashboardTokenRevoked, _context, accessDenied: true,
                data: JsonConvert.SerializeObject(new { tokenId }));
            return new ForbidResult();
        }

        var change = await _tokens.RevokeAsync(tokenId, _authorization.GetCurrentUserName());
        if (Refused(change) is { } refused)
            return refused;

        await AuditAsync(MessageAuditType.DashboardTokenRevoked, change.Token!);
        return new OkObjectResult(await StateAsync());
    }

    /// <inheritdoc />
    public async Task<ActionResult<Api.DashboardTokenState>> DeleteAdminDashboardTokenAsync(string tokenId)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _audit.LogAuditAsync(MessageAuditType.DashboardTokenRemoved, _context, accessDenied: true,
                data: JsonConvert.SerializeObject(new { tokenId }));
            return new ForbidResult();
        }

        var change = await _tokens.RemoveAsync(tokenId, _authorization.GetCurrentUserName());
        if (Refused(change) is { } refused)
            return refused;

        await AuditAsync(MessageAuditType.DashboardTokenRemoved, change.Token!);
        return new OkObjectResult(await StateAsync());
    }

    private async Task<Api.DashboardTokenState> StateAsync()
    {
        var (settings, lastUsed) = await _tokens.ListAsync();
        return new Api.DashboardTokenState
        {
            Tokens = settings.Tokens
                .OrderByDescending(t => t.CreatedAtUtc)
                .Select(t => ToApi(t, lastUsed.TryGetValue(t.TokenId, out var used) ? used : null))
                .ToList(),
            CsrfToken = _antiforgery.GetAndStoreTokens(_context).RequestToken ?? string.Empty,
        };
    }

    private Api.DashboardTokenInfo ToApi(DashboardToken token, DateTime? lastUsed) => new()
    {
        TokenId = token.TokenId,
        Prefix = DashboardTokenDefaults.Prefix + token.TokenId,
        Name = token.Name,
        Status = _tokens.StatusOf(token) switch
        {
            DashboardTokenStatus.Revoked => Api.DashboardTokenStatus.Revoked,
            DashboardTokenStatus.Expired => Api.DashboardTokenStatus.Expired,
            _ => Api.DashboardTokenStatus.Active,
        },
        CreatedAtUtc = token.CreatedAtUtc,
        CreatedBy = token.CreatedBy,
        ExpiresAtUtc = token.ExpiresAtUtc,
        RevokedAtUtc = token.RevokedAtUtc,
        RevokedBy = token.RevokedBy,
        LastUsedAtUtc = lastUsed,
    };

    private Task AuditAsync(MessageAuditType type, DashboardToken token)
        => _audit.LogAuditAsync(type, _context, data: JsonConvert.SerializeObject(new
        {
            tokenId = token.TokenId,
            name = token.Name,
            expiresAtUtc = token.ExpiresAtUtc,
        }));

    private static ObjectResult? Refused(DashboardTokenChange change) => change.Status switch
    {
        DashboardTokenChangeStatus.Saved => null,
        DashboardTokenChangeStatus.Invalid => Problem(400, "Invalid", change.Errors ?? []),
        DashboardTokenChangeStatus.NotFound => Problem(404, "NotFound", ["No dashboard token has that id."]),
        DashboardTokenChangeStatus.StillActive => Problem(400, "StillActive", ["Revoke the token before removing it."]),
        DashboardTokenChangeStatus.Unavailable => NotImplemented(),
        _ => Problem(409, "RevisionConflict", ["The dashboard tokens kept changing while saving. Try again."]),
    };

    private static ObjectResult Problem(int status, string code, IEnumerable<string> errors)
        => new(new Api.ProblemDetails { Code = code, Errors = errors.ToList() }) { StatusCode = status };

    private static ObjectResult NotImplemented()
        => Problem(StatusCodes.Status501NotImplemented, "Unavailable", ["The storage provider cannot store dashboard tokens."]);

    private Task<bool> IsSiteOwnerAsync() => _authorization.HasRoleAsync(AccessRole.Owner);
}
