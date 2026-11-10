using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NimBus.Core;
using NimBus.MessageStore;
using NimBus.MessageStore.States;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Controllers.ApiContract;

/// <summary>
/// Shared Monitor acknowledgements. Anyone who can read an endpoint sees its
/// acknowledgement; placing or clearing one is an operator action and needs
/// Contributor on the endpoint, the same bar as resubmit and skip.
/// </summary>
public sealed class MonitorImplementation : IMonitorApiController
{
    private readonly IPlatform _platform;
    private readonly IMonitorAcknowledgementService _acknowledgements;
    private readonly IEndpointAuthorizationService _authorizationService;
    private readonly IAuditLogService _auditLogService;
    private readonly HttpContext _context;

    public MonitorImplementation(
        IPlatform platform,
        IMonitorAcknowledgementService acknowledgements,
        IEndpointAuthorizationService authorizationService,
        IAuditLogService auditLogService,
        IHttpContextAccessor contextAccessor)
    {
        _platform = platform;
        _acknowledgements = acknowledgements;
        _authorizationService = authorizationService;
        _auditLogService = auditLogService;
        _context = contextAccessor.HttpContext!;
    }

    public async Task<ActionResult<IEnumerable<MonitorAcknowledgement>>> GetMonitorAcknowledgementsAsync()
    {
        var visible = new List<MonitorAcknowledgement>();
        foreach (var acknowledgement in await _acknowledgements.GetActiveAsync())
        {
            if (FindEndpointId(acknowledgement.EndpointId) is { } endpointId
                && await _authorizationService.HasRoleAsync(AccessRole.Reader, endpointId))
            {
                visible.Add(ToApi(acknowledgement, endpointId));
            }
        }

        return new OkObjectResult(visible);
    }

    public async Task<ActionResult<MonitorAcknowledgement>> PutMonitorAcknowledgementAsync(
        MonitorAcknowledgementRequest body, string endpointId)
    {
        if (FindEndpointId(endpointId) is not { } canonicalId)
            return new NotFoundObjectResult("Endpoint not found");

        if (!await _authorizationService.HasRoleAsync(AccessRole.Contributor, canonicalId))
        {
            await _auditLogService.LogAuditAsync(MessageAuditType.AcknowledgeEndpoint, _context,
                accessDenied: true, endpointId: canonicalId);
            return new ForbidResult();
        }

        var reason = body?.Reason?.Trim() ?? string.Empty;
        if (reason.Length > MonitorAcknowledgementService.MaxReasonLength)
            return new BadRequestObjectResult($"Reason must be at most {MonitorAcknowledgementService.MaxReasonLength} characters.");

        var acknowledgement = await _acknowledgements.AcknowledgeAsync(
            canonicalId, reason, _authorizationService.GetCurrentUserName());
        await _auditLogService.LogAuditAsync(MessageAuditType.AcknowledgeEndpoint, _context,
            data: reason.Length == 0 ? null : reason, endpointId: canonicalId);
        return new OkObjectResult(ToApi(acknowledgement, canonicalId));
    }

    public async Task<IActionResult> DeleteMonitorAcknowledgementAsync(string endpointId)
    {
        if (FindEndpointId(endpointId) is not { } canonicalId)
            return new NotFoundObjectResult("Endpoint not found");

        if (!await _authorizationService.HasRoleAsync(AccessRole.Contributor, canonicalId))
        {
            await _auditLogService.LogAuditAsync(MessageAuditType.ClearEndpointAcknowledgement, _context,
                accessDenied: true, endpointId: canonicalId);
            return new ForbidResult();
        }

        await _acknowledgements.ClearAsync(canonicalId);
        await _auditLogService.LogAuditAsync(MessageAuditType.ClearEndpointAcknowledgement, _context,
            endpointId: canonicalId);
        return new NoContentResult();
    }

    /// <summary>
    /// The platform's spelling of the endpoint id (ids match case-insensitively
    /// everywhere server side), or null when the platform has no such endpoint.
    /// </summary>
    private string? FindEndpointId(string? endpointId)
        => string.IsNullOrEmpty(endpointId)
            ? null
            : _platform.Endpoints?
                .FirstOrDefault(e => string.Equals(e.Id, endpointId, StringComparison.OrdinalIgnoreCase))?.Id;

    /// <summary>The API shape of an acknowledgement, under the platform's spelling of the endpoint id.</summary>
    internal static MonitorAcknowledgement ToApi(EndpointAcknowledgement acknowledgement, string endpointId) => new()
    {
        EndpointId = endpointId,
        AcknowledgementId = acknowledgement.AcknowledgementId,
        Reason = acknowledgement.Reason,
        AcknowledgedBy = acknowledgement.AcknowledgedBy,
        AcknowledgedAt = acknowledgement.AcknowledgedAtUtc,
        ExpiresAt = acknowledgement.ExpiresAtUtc,
        FailedCountAtAcknowledgement = acknowledgement.FailedCountAtAcknowledgement,
    };
}
