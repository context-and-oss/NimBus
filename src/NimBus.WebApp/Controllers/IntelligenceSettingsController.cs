using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NimBus.MessageStore;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.IntegrationIntelligence;

namespace NimBus.WebApp.Controllers;

/// <summary>
/// Site-owner-only configuration of optional failure intelligence. The provider API key can be
/// saved here; it is sealed before storage and is never returned, audited or logged.
/// </summary>
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
    IIntelligenceSecretProtector protector,
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
        var keyAction = "unchanged";
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
            // An empty or whitespace key means "no change"; a present key must be replaced whole.
            var apiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? null : request.ApiKey.Trim();
            if (apiKey is not null && (request.ClearApiKey || !IntelligenceAdminSettings.IsValidApiKey(apiKey)))
                return BadRequest(new { code = "InvalidApiKey" });

            // The sealed key is carried forward from the current record, so the read is fenced
            // by the same revision the save checks.
            var current = await store.ReadAsync(cancellationToken);
            if ((current?.Revision ?? "none") != request.Revision)
            {
                outcome = "Conflict";
                return Conflict(new { code = "SettingsConflict" });
            }
            keyAction = request.ClearApiKey ? "cleared" : apiKey is not null ? "replaced" : "unchanged";
            var protectedKey = request.ClearApiKey ? null : apiKey is not null ? protector.Protect(apiKey) : current?.ProtectedApiKey;
            var document = new IntelligenceSettingsDocument(request.Settings, Guid.NewGuid().ToString("D"), protectedKey);
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
                    data: "{\"outcome\":\"" + outcome + "\",\"apiKey\":\"" + keyAction + "\"}", cancellationToken: auditTimeout.Token).WaitAsync(auditTimeout.Token);
            }
            catch (Exception) { /* Audit is best-effort; never retry a committed settings write. */ }
        }
    }

    private object State(IntelligenceSettingsDocument? saved, string? csrfToken)
    {
        // "configured" means the record holds a key this instance can unseal; "unreadable" means a
        // key exists but was sealed by a different key ring and must be entered again.
        var savedApiKey = saved?.ProtectedApiKey is null ? "none"
            : protector.TryUnprotect(saved.ProtectedApiKey) is null ? "unreadable" : "configured";
        return new
        {
            active = snapshot.Active, saved = saved?.Settings ?? snapshot.Active,
            revision = saved?.Revision ?? "none", activeRevision = snapshot.Revision,
            restartRequired = snapshot.Revision != (saved?.Revision ?? "none"),
            startupLoadFailed = snapshot.LoadFailed, credentialConfigured = snapshot.CredentialConfigured,
            credentialSource = snapshot.CredentialSource, savedApiKey,
            csrfToken,
        };
    }
}

/// <summary>
/// A complete conditional settings update and explicit payload-sharing consent. <see cref="ApiKey"/>
/// replaces the saved provider key when present; <see cref="ClearApiKey"/> removes it. Neither is echoed.
/// Both, and the sealed key they manage, are deprecated with the built-in provider (Spec 044 §6.10)
/// and are removed in v5.0.0.
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
    public string? ApiKey { get; set; }
    public bool ClearApiKey { get; set; }
}
