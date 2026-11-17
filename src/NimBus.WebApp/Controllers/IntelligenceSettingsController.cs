using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NimBus.MessageStore;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.IntegrationIntelligence;

namespace NimBus.WebApp.Controllers;

/// <summary>Site-owner-only configuration of optional failure intelligence.</summary>
[ApiController]
[Authorize]
[ApiExplorerSettings(IgnoreApi = true)]
[Route("api/admin/failure-intelligence")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class IntelligenceSettingsController(
    IEndpointAuthorizationService authorization,
    IAuditLogService audit,
    IIntelligenceSettingsStore store,
    IntelligenceSettingsSnapshot snapshot,
    IAntiforgery antiforgery) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        if (!await authorization.HasRoleAsync(AccessRole.Owner)) return StatusCode(403);
        try
        {
            var saved = await store.ReadAsync(cancellationToken);
            return Ok(State(saved, antiforgery.GetAndStoreTokens(HttpContext).RequestToken));
        }
        catch (Exception) { return StatusCode(503, new { code = "SettingsUnavailable" }); }
    }

    [HttpPut]
    [RequestSizeLimit(32768)]
    public async Task<IActionResult> Put([FromBody] SaveIntelligenceSettings request, CancellationToken cancellationToken)
    {
        var outcome = "Rejected";
        var denied = false;
        try
        {
            if (!await authorization.HasRoleAsync(AccessRole.Owner))
            {
                denied = true;
                return StatusCode(403);
            }
            try { await antiforgery.ValidateRequestAsync(HttpContext); }
            catch (AntiforgeryValidationException) { return BadRequest(new { code = "InvalidAntiforgeryToken" }); }
            if (request.Settings is null || (request.Revision != "none" && !Guid.TryParse(request.Revision, out _)))
                return BadRequest(new { code = "InvalidSettings" });
            var errors = request.Settings.Validate();
            if (errors.Count != 0) return BadRequest(new { code = "InvalidSettings", errors });
            if (request.Settings.IncludeEventPayload && !request.PayloadSharingAcknowledged)
                return BadRequest(new { code = "PayloadConsentRequired" });

            var document = new IntelligenceSettingsDocument(request.Settings, Guid.NewGuid().ToString("D"));
            if (!await store.TrySaveAsync(document, request.Revision, cancellationToken))
            {
                outcome = "Conflict";
                return Conflict(new { code = "SettingsConflict" });
            }
            outcome = "Saved";
            return Ok(State(document, antiforgery.GetAndStoreTokens(HttpContext).RequestToken));
        }
        catch (Exception)
        {
            outcome = "Unavailable";
            return StatusCode(503, new { code = "SettingsUnavailable" });
        }
        finally
        {
            using var auditTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try
            {
                await audit.LogAuditAsync(MessageAuditType.UpdateIntelligenceSettings, HttpContext, denied,
                    data: "{\"outcome\":\"" + outcome + "\"}", cancellationToken: auditTimeout.Token).WaitAsync(auditTimeout.Token);
            }
            catch (Exception) { /* Audit is best-effort; never retry a committed settings write. */ }
        }
    }

    private object State(IntelligenceSettingsDocument? saved, string? csrfToken) => new
    {
        active = snapshot.Active, saved = saved?.Settings ?? snapshot.Active,
        revision = saved?.Revision ?? "none", activeRevision = snapshot.Revision,
        restartRequired = snapshot.Revision != (saved?.Revision ?? "none"),
        startupLoadFailed = snapshot.LoadFailed,
        csrfToken,
    };
}

/// <summary>
/// A complete conditional settings update and explicit payload-sharing consent. Unknown members,
/// such as the <c>apiKey</c> of the provider removed in v5.0.0, are rejected.
/// </summary>
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed class SaveIntelligenceSettings
{
    [System.Text.Json.Serialization.JsonRequired]
    public IntelligenceAdminSettings? Settings { get; set; }
    [System.Text.Json.Serialization.JsonRequired]
    public string Revision { get; set; } = string.Empty;
    [System.Text.Json.Serialization.JsonRequired]
    public bool PayloadSharingAcknowledged { get; set; }
}
