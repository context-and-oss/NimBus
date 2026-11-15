using Microsoft.AspNetCore.Mvc;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Controllers.ApiContract;

public partial class EventImplementation
{
    /// <summary>Returns read-only broker observations and endpoint-scoped processing evidence.</summary>
    public async Task<ActionResult<DeferredInspection>> GetDeferredInspectionAsync(string endpointId, string eventId)
    {
        if (!EndpointVerificationService.EndpointExists(platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found.");
        endpointId = CanonicalEndpointId(endpointId);
        if (!await authorizationService.HasRoleAsync(AccessRole.Reader, endpointId)) return new ForbidResult();
        var row = await GetDeferredRecoveryRowAsync(endpointId, eventId);
        if (row is null) return new NotFoundObjectResult("Event not found.");
        var result = await InspectDeferredRowAsync(row);
        if (!await authorizationService.HasRoleAsync(AccessRole.Contributor, endpointId))
        {
            result.CanSkip = false;
            result.SkipDetail = "Contributor permission on this endpoint is required to skip tracking records.";
        }
        return result;
    }

    /// <summary>Conditionally skips only the inspected stale tracking row; preserves broker state and history.</summary>
    public async Task<ActionResult<DeferredSkipResult>> PostSkipDeferredTrackingAsync(string endpointId, string eventId, DeferredSkipRequest body)
    {
        if (!EndpointVerificationService.EndpointExists(platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found.");
        endpointId = CanonicalEndpointId(endpointId);
        if (!await authorizationService.HasRoleAsync(AccessRole.Contributor, endpointId))
        {
            await auditLogService.LogAuditAsync(MessageAuditType.Skip, httpContextAccessor.HttpContext!,
                accessDenied: true, eventId: eventId, endpointId: endpointId,
                data: "{\"action\":\"skip-deferred-tracking\"}");
            return new ForbidResult();
        }
        if (string.IsNullOrWhiteSpace(body?.Reason) || body.Reason.Length > 1000 || string.IsNullOrEmpty(body.RowVersion))
            return new BadRequestObjectResult("An inspected row version and a reason of 1–1000 characters are required.");

        // The version check, the repeated inspection, the conditional write and the audit row
        // live in the shared service, so the operator MCP tool applies exactly the same rules.
        var outcome = await DeferredRecovery().SkipAsync(
            endpointId, eventId, body.RowVersion, body.Reason,
            authorizationService.GetCurrentUserName() ?? AuditLogService.ResolveAuditorName(httpContextAccessor.HttpContext),
            origin: null,
            httpContextAccessor.HttpContext?.RequestAborted ?? default);
        return outcome.Status switch
        {
            DeferredTrackingSkipStatus.Skipped => new DeferredSkipResult { AuditRecorded = outcome.AuditRecorded },
            DeferredTrackingSkipStatus.NotFound => new NotFoundObjectResult("Event not found."),
            DeferredTrackingSkipStatus.NotSupported =>
                new ObjectResult("This storage provider does not support conditional deferred recovery.") { StatusCode = 501 },
            _ => new ConflictObjectResult(outcome.Detail),
        };
    }

    private Task<UnresolvedEvent?> GetDeferredRecoveryRowAsync(string endpointId, string eventId)
        => DeferredRecovery().FindRowAsync(endpointId, eventId);

    private Task<DeferredInspection> InspectDeferredRowAsync(UnresolvedEvent row)
        => DeferredRecovery().InspectAsync(row, httpContextAccessor.HttpContext?.RequestAborted ?? default);

    private DeferredTrackingRecovery DeferredRecovery()
        => new(messageStore, serviceBusClient, serviceBusAdministrationClient, logger);
}
