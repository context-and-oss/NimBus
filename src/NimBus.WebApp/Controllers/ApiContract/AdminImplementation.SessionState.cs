using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Newtonsoft.Json;
using NimBus.MessageStore;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Controllers.ApiContract;

// Operations → Session state and Clean up session state (plan 2026-11-03). Site Owner only, like
// every /api/admin/* action. Reads aren't audited; every write is, and a write whose audit row
// can't be recorded doesn't happen.
public partial class AdminImplementation
{
    /// <summary>Service Bus caps a session id at 128 characters.</summary>
    private const int MaxSessionIdLength = 128;

    /// <summary>Most sessions one cleanup run may reset.</summary>
    private const int MaxCleanupSessions = 1000;

    /// <inheritdoc />
    public async Task<ActionResult<SessionStateView>> GetAdminSessionStateAsync(string endpointId, string sessionId)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();
        if (SessionRequestError(endpointId, sessionId) is { } error)
            return error;

        return new OkObjectResult(await SessionStates.GetAsync(endpointId, sessionId, _context.RequestAborted));
    }

    /// <inheritdoc />
    public async Task<ActionResult<SessionStateView>> PostAdminSessionStateAsync(SessionStateChangeRequest body, string endpointId, string sessionId)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _auditLogService.LogAuditAsync(MessageAuditType.ChangeSessionState, _context,
                accessDenied: true, endpointId: endpointId,
                data: JsonConvert.SerializeObject(new { sessionId, action = body?.Action.ToString() }));
            return new ForbidResult();
        }
        if (SessionRequestError(endpointId, sessionId) is { } error)
            return error;
        if (body is null || string.IsNullOrWhiteSpace(body.ExpectedVersion))
            return new BadRequestObjectResult("expectedVersion is required: look up the session state first.");

        SessionStateChangeOutcome outcome;
        try
        {
            outcome = await SessionStates.ChangeAsync(endpointId, sessionId, body,
                write => _auditLogService.LogRequiredAuditAsync(MessageAuditType.ChangeSessionState, _context,
                    endpointId: endpointId,
                    data: JsonConvert.SerializeObject(new
                    {
                        action = write.Action,
                        sessionId = write.SessionId,
                        before = write.Before,
                        after = write.After,
                    })),
                _context.RequestAborted);
        }
        catch (AuditUnavailableException ex)
        {
            return AuditUnavailable(ex);
        }

        return outcome.Status switch
        {
            SessionStateChangeStatus.Changed => new OkObjectResult(outcome.View),
            SessionStateChangeStatus.Conflict or SessionStateChangeStatus.Locked => new ConflictObjectResult(outcome.View),
            _ => new BadRequestObjectResult(string.Join(" ", outcome.Errors)),
        };
    }

    /// <inheritdoc />
    public async Task<ActionResult<SessionCleanupPreview>> PostAdminSessionCleanupPreviewAsync(SessionCleanupPreviewRequest body, string endpointId)
    {
        if (!await IsSiteOwnerAsync())
            return new ForbidResult();
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");
        if (body?.MaxSessions is < 1 or > SessionStateService.MaxSessionsLimit)
            return new BadRequestObjectResult($"maxSessions must be between 1 and {SessionStateService.MaxSessionsLimit}.");

        return new OkObjectResult(await SessionStates.PreviewCleanupAsync(
            endpointId, body ?? new SessionCleanupPreviewRequest(), _context.RequestAborted));
    }

    /// <inheritdoc />
    public async Task<ActionResult<SessionCleanupResult>> PostAdminSessionCleanupAsync(SessionCleanupRequest body, string endpointId)
    {
        if (!await IsSiteOwnerAsync())
        {
            await _auditLogService.LogAuditAsync(MessageAuditType.ChangeSessionState, _context,
                accessDenied: true, endpointId: endpointId,
                data: JsonConvert.SerializeObject(new { action = "session-cleanup", sessionCount = body?.Sessions?.Count ?? 0 }));
            return new ForbidResult();
        }
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");

        var sessions = body?.Sessions;
        if (sessions is null || sessions.Count == 0)
            return new BadRequestObjectResult("Select at least one session to reset.");
        if (sessions.Count > MaxCleanupSessions)
            return new BadRequestObjectResult($"At most {MaxCleanupSessions} sessions can be reset in one run.");
        if (sessions.Any(s => string.IsNullOrEmpty(s?.SessionId) || s.SessionId.Length > MaxSessionIdLength || string.IsNullOrEmpty(s.StateVersion)))
            return new BadRequestObjectResult("Every session needs its id and the stateVersion from the preview.");

        // Recorded before any state is reset, so a run that can't be audited doesn't happen.
        try
        {
            await _auditLogService.LogRequiredAuditAsync(MessageAuditType.ChangeSessionState, _context,
                endpointId: endpointId,
                data: JsonConvert.SerializeObject(new
                {
                    action = "session-cleanup",
                    includeStaleBlocks = body!.IncludeStaleBlocks,
                    sessionCount = sessions.Count,
                    sessionIds = sessions.Select(s => s.SessionId),
                }));
        }
        catch (AuditUnavailableException ex)
        {
            return AuditUnavailable(ex);
        }

        return new OkObjectResult(await SessionStates.CleanupAsync(endpointId, body, _context.RequestAborted));
    }

    private ISessionStateService SessionStates =>
        _sessionStates ?? throw new InvalidOperationException("ISessionStateService is not registered.");

    private ActionResult? SessionRequestError(string endpointId, string sessionId)
    {
        if (!EndpointVerificationService.EndpointExists(_platform, endpointId))
            return new NotFoundObjectResult("Endpoint not found");
        if (string.IsNullOrWhiteSpace(sessionId) || sessionId.Length > MaxSessionIdLength)
            return new BadRequestObjectResult($"The session id must be 1 to {MaxSessionIdLength} characters.");
        return null;
    }

    private static ObjectResult AuditUnavailable(AuditUnavailableException ex) =>
        new(ex.Message) { StatusCode = StatusCodes.Status503ServiceUnavailable };
}
