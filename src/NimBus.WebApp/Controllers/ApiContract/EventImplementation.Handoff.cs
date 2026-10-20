using NimBus.MessageStore;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;

namespace NimBus.WebApp.Controllers.ApiContract;

public partial class EventImplementation
{
    public Task<IActionResult> PostHandoffCompleteAsync(CompleteHandoffRequest body, string endpointId, string eventId, string messageId)
        => SettlePendingHandoffAsync(
            endpointId, eventId, messageId,
            MessageAuditType.CompleteHandoff,
            body?.Note,
            (pendingEntry, operatorName) =>
            {
                // Carry the operator note (and who completed it) on the completion
                // payload so the resulting Completed audit row records the manual
                // intervention, not just an anonymous external job finishing.
                var detailsJson = string.IsNullOrWhiteSpace(body?.Note)
                    ? null
                    : JsonConvert.SerializeObject(new { note = body.Note, completedBy = operatorName });
                return handoffClients.ForEndpoint(endpointId).CompleteAsync(pendingEntry.ToSettlement(), detailsJson);
            });

    public Task<IActionResult> PostHandoffFailAsync(FailHandoffRequest body, string endpointId, string eventId, string messageId)
    {
        if (string.IsNullOrWhiteSpace(body?.Reason))
            return Task.FromResult<IActionResult>(new BadRequestObjectResult("A failure reason is required."));

        return SettlePendingHandoffAsync(
            endpointId, eventId, messageId,
            MessageAuditType.FailHandoff,
            body.Reason,
            (pendingEntry, _) =>
                handoffClients.ForEndpoint(endpointId).FailAsync(pendingEntry.ToSettlement(), body.Reason, body.ErrorType));
    }

    // Operator entry to the two handoff-settlement actions. Does the operator-only
    // pre-checks (endpoint exists, caller manages it) and then delegates the load →
    // PendingHandoff guard → settle → audit core to the shared IHandoffSettlementService,
    // which the agent settle path also uses so neither can skip the audit row.
    private async Task<IActionResult> SettlePendingHandoffAsync(
        string endpointId,
        string eventId,
        string messageId,
        MessageAuditType auditType,
        string? auditComment,
        Func<MessageEntity, string, Task> settle)
    {
        if (!EndpointVerificationService.EndpointExists(platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        if (!await authorizationService.HasRoleAsync(AccessRole.Contributor, endpointId))
        {
            await auditLogService.LogAuditAsync(auditType, httpContextAccessor.HttpContext!,
                accessDenied: true, eventId: eventId, endpointId: endpointId);
            return new ForbidResult();
        }

        var auditorName = AuditLogService.ResolveAuditorName(httpContextAccessor.HttpContext);
        return await handoffSettlement.SettleAsync(
            endpointId, eventId, messageId, auditType, auditComment, auditorName,
            httpContextAccessor.HttpContext, settle);
    }
}
