using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NimBus.MessageStore;

namespace NimBus.WebApp.Services;

/// <summary>
/// Shared core for settling a pending hand-off, used by both the operator path
/// (<c>EventImplementation.PostHandoffComplete/FailAsync</c>) and the agent path
/// (<c>AgentImplementation.PostAgentSettleAsync</c>). Loading the parked event,
/// the PendingHandoff guard, the settlement publish, and — critically — the
/// management audit-row write all live here so the two callers cannot drift
/// (ADR-002: every settlement leaves a who/why audit trail).
/// </summary>
public interface IHandoffSettlementService
{
    /// <summary>
    /// Loads the parked event on <paramref name="endpointId"/>, verifies it is a
    /// PendingHandoff, invokes <paramref name="settle"/> to publish the settlement
    /// control message, then writes the <paramref name="auditType"/> audit row
    /// attributed to <paramref name="auditorName"/>.
    /// </summary>
    /// <param name="endpointId">Endpoint (zone) the parked event lives on.</param>
    /// <param name="eventId">Id of the parked event to settle.</param>
    /// <param name="messageId">Message id to stamp on the settlement entry.</param>
    /// <param name="auditType">CompleteHandoff or FailHandoff.</param>
    /// <param name="auditComment">Operator/agent note or failure reason recorded on the audit row.</param>
    /// <param name="auditorName">Actor recorded as the auditor (operator principal name, or agent id).</param>
    /// <param name="httpContext">Current request context for the audit log's structured sink; may be null.</param>
    /// <param name="settle">Publishes the settlement; receives the built entry and the auditor name.</param>
    /// <param name="cancellationToken">Observed by the audit write.</param>
    /// <returns>
    /// 200 on success; 404 when the event is genuinely absent (endpoint container
    /// missing or Cosmos reports it not found); 400 when the event exists but is not
    /// a PendingHandoff, or when this instance already sent a settlement for it that the
    /// Resolver has not projected yet (no second control message or audit row). Transient/unexpected store faults are NOT swallowed — they
    /// propagate so callers see a 500 rather than a misleading "handoff gone" 404.
    /// </returns>
    Task<IActionResult> SettleAsync(
        string endpointId,
        string eventId,
        string messageId,
        MessageAuditType auditType,
        string? auditComment,
        string auditorName,
        HttpContext? httpContext,
        Func<MessageEntity, string, Task> settle,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether this instance has sent a settlement for <paramref name="parkedEvent"/> that
    /// the Resolver has not projected yet. The parked row still reads Pending+Handoff in that
    /// window, so receive uses this to avoid handing the same hand-off out again.
    /// </summary>
    /// <param name="endpointId">Endpoint (zone) the parked event lives on.</param>
    /// <param name="parkedEvent">The Pending+Handoff row as read from the store.</param>
    /// <returns><c>true</c> while a settlement of this parked message is in flight.</returns>
    bool IsSettlementInFlight(string endpointId, UnresolvedEvent parkedEvent) => false;
}
