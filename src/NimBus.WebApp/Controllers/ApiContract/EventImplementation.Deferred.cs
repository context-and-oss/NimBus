using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
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
        var row = await GetDeferredRecoveryRowAsync(endpointId, eventId);
        if (row is null) return new NotFoundObjectResult("Event not found.");
        if (!string.Equals(body.RowVersion, DeferredMessageInspector.RowVersion(row), StringComparison.Ordinal))
            return new ConflictObjectResult("The tracking record changed. Check it again.");

        var expectedMessageId = row.LastMessageId;
        var expectedUpdatedAt = row.UpdatedAt;
        var inspection = await InspectDeferredRowAsync(row);
        if (!inspection.CanSkip) return new ConflictObjectResult(inspection.SkipDetail);
        httpContextAccessor.HttpContext?.RequestAborted.ThrowIfCancellationRequested();
        try
        {
            if (!await messageStore.TrySkipDeferredMessage(eventId, row.SessionId!, endpointId, expectedMessageId, expectedUpdatedAt))
                return new ConflictObjectResult("The tracking record changed. Check it again.");
        }
        catch (NotSupportedException)
        {
            return new ObjectResult("This storage provider does not support conditional deferred recovery.") { StatusCode = 501 };
        }

        // Report audit persistence separately: the state transition already succeeded and
        // must not be presented as a failed skip (or silently retried) if the audit sink fails.
        var auditRecorded = false;
        try
        {
            await messageStore.StoreMessageAudit(eventId, new MessageAuditEntity
            {
                AuditType = MessageAuditType.Skip, AuditTimestamp = DateTime.UtcNow,
                AuditorName = authorizationService.GetCurrentUserName() ?? AuditLogService.ResolveAuditorName(httpContextAccessor.HttpContext),
                EventId = eventId, EndpointId = endpointId, Comment = body.Reason.Trim(),
                Data = JsonConvert.SerializeObject(new
                {
                    action = "skip-deferred-tracking", previousStatus = "Deferred", newStatus = "Skipped",
                    inspectedVersion = body.RowVersion, inspection.HistoryOutcome, inspection.TerminalMessageId,
                    inspection.TerminalTime, inspection.HasLaterAttempt,
                    brokerChecks = inspection.BrokerChecks.Select(c => new { c.Location, status = c.Status.ToString(), c.Scanned }),
                }),
            }, endpointId, row.EventTypeId);
            auditRecorded = true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Deferred tracking was skipped but audit failed for {EndpointId}/{EventId}", endpointId, eventId);
        }
        logger.LogInformation("Deferred tracking skipped for {EndpointId}/{EventId}; audit recorded: {AuditRecorded}", endpointId, eventId, auditRecorded);
        return new DeferredSkipResult { AuditRecorded = auditRecorded };
    }

    private async Task<UnresolvedEvent?> GetDeferredRecoveryRowAsync(string endpointId, string eventId)
    {
        try { return await messageStore.GetEvent(endpointId, eventId); }
        catch (EndpointNotFoundException) { return null; }
    }

    private async Task<DeferredInspection> InspectDeferredRowAsync(UnresolvedEvent row) =>
        await new DeferredMessageInspector(serviceBusClient, serviceBusAdministrationClient).InspectAsync(row,
            await messageStore.GetEventHistory(row.EventId), httpContextAccessor.HttpContext?.RequestAborted ?? default);
}
