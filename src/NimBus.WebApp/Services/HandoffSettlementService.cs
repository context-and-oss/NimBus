using System;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Logging;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;

namespace NimBus.WebApp.Services;

/// <summary>
/// Default <see cref="IHandoffSettlementService"/>. See the interface for the
/// contract; this implementation always writes the audit row after a successful
/// settlement so no settle path can silently skip it.
/// </summary>
public sealed class HandoffSettlementService : IHandoffSettlementService
{
    private const string HandoffSubStatus = "Handoff";

    private readonly IMessageTrackingStore _store;
    private readonly IAuditLogService _audit;
    private readonly HandoffSettlementTracker _inFlight;
    private readonly ILogger<HandoffSettlementService> _logger;

    /// <summary>
    /// Initialises the service with the message store, audit log, the process-wide record of
    /// settlements in flight, and logger.
    /// </summary>
    public HandoffSettlementService(
        IMessageTrackingStore store,
        IAuditLogService audit,
        HandoffSettlementTracker inFlight,
        ILogger<HandoffSettlementService> logger)
    {
        _store = store;
        _audit = audit;
        _inFlight = inFlight;
        _logger = logger;
    }

    /// <summary>Initialises the service with the message store, audit log, and logger.</summary>
    /// <remarks>
    /// The service gets a private <see cref="HandoffSettlementTracker"/>, so settlements in
    /// flight are not shared with other instances or with agent receive.
    /// </remarks>
    [Obsolete("Pass the shared HandoffSettlementTracker singleton. This constructor will be removed in the next major version.")]
    public HandoffSettlementService(
        IMessageTrackingStore store,
        IAuditLogService audit,
        ILogger<HandoffSettlementService> logger)
        : this(store, audit, new HandoffSettlementTracker(TimeProvider.System), logger)
    {
    }

    /// <inheritdoc/>
    public async Task<IActionResult> SettleAsync(
        string endpointId,
        string eventId,
        string messageId,
        MessageAuditType auditType,
        string? auditComment,
        string auditorName,
        HttpContext? httpContext,
        Func<MessageEntity, string, Task> settle,
        CancellationToken cancellationToken = default)
    {
        UnresolvedEvent? pendingEvent;
        try
        {
            pendingEvent = await _store.GetEvent(endpointId, eventId);
        }
        // Only genuinely-not-found signals map to 404: the endpoint container is
        // absent, or the lookup returns null for a missing row. Transient/unexpected store
        // faults (throttling 429, connectivity) must NOT masquerade as "handoff
        // gone" — let them propagate to a 500 so the caller can retry.
        catch (EndpointNotFoundException e)
        {
            _logger.LogWarning("Handoff settle: endpoint/event not found. EndpointId: {EndpointId}, EventId: {EventId}, Ex: {Exception}", endpointId, eventId, e.Message);
            return new NotFoundObjectResult("Event not found");
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            _logger.LogWarning("Handoff settle: event container not found. EndpointId: {EndpointId}, EventId: {EventId}, Ex: {Exception}", endpointId, eventId, e.Message);
            return new NotFoundObjectResult("Event not found");
        }

        if (pendingEvent == null)
            return new NotFoundObjectResult("Event not found");

        if (pendingEvent.ResolutionStatus != ResolutionStatus.Pending
            || !string.Equals(pendingEvent.PendingSubStatus, HandoffSubStatus, StringComparison.Ordinal))
        {
            _logger.LogWarning(
                "Handoff settle rejected: event is not a pending handoff. EndpointId: {EndpointId}, EventId: {EventId}, Status: {Status}, SubStatus: {SubStatus}",
                endpointId, eventId, pendingEvent.ResolutionStatus, pendingEvent.PendingSubStatus ?? "<null>");
            return new BadRequestObjectResult("Event is not a pending handoff.");
        }

        var pendingEntry = new MessageEntity
        {
            EventId = pendingEvent.EventId,
            MessageId = messageId,
            SessionId = pendingEvent.SessionId,
            CorrelationId = pendingEvent.CorrelationId,
            OriginatingMessageId = pendingEvent.OriginatingMessageId,
            EventTypeId = pendingEvent.EventTypeId,
            PendingSubStatus = pendingEvent.PendingSubStatus,
        };

        _logger.LogInformation(
            "Handoff settle ({AuditType}). EndpointId: {EndpointId}, EventId: {EventId}, MessageId: {MessageId}, Auditor: {Auditor}",
            auditType, endpointId, eventId, messageId, auditorName);

        // The row reads Pending+Handoff until the Resolver projects the settlement, so the
        // guard above cannot see one this instance already sent. Claim the parked message so
        // a second settle in that window gets the same 400 it would get once projected.
        if (!_inFlight.TryBegin(endpointId, eventId, pendingEvent.LastMessageId))
        {
            _logger.LogWarning(
                "Handoff settle rejected: a settlement is already in flight. EndpointId: {EndpointId}, EventId: {EventId}, Auditor: {Auditor}",
                ForLog(endpointId), ForLog(eventId), ForLog(auditorName));
            return new BadRequestObjectResult("Event is not a pending handoff: a settlement is already in flight.");
        }

        try
        {
            await settle(pendingEntry, auditorName);
        }
        catch
        {
            // Nothing was sent, so nothing is in flight: release the claim for a retry.
            _inFlight.Abandon(endpointId, eventId, pendingEvent.LastMessageId);
            throw;
        }

        await _audit.LogAuditAsync(auditType, httpContext,
            data: auditComment, eventId: eventId, endpointId: endpointId,
            eventTypeId: pendingEvent.EventTypeId, auditorNameOverride: auditorName,
            cancellationToken: cancellationToken);

        return new OkResult();
    }

    /// <inheritdoc/>
    public bool IsSettlementInFlight(string endpointId, UnresolvedEvent parkedEvent)
    {
        ArgumentNullException.ThrowIfNull(parkedEvent);
        return _inFlight.IsInFlight(endpointId, parkedEvent.EventId, parkedEvent.LastMessageId);
    }

    // The event id and the auditor (an agent's X-Agent-Id header) come from the caller;
    // strip line breaks so they cannot forge log entries.
    private static string ForLog(string? value)
        => (value ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ');
}
