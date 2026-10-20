using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NimBus.Core;
using NimBus.Core.Messages;
using NimBus.Manager;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;

namespace NimBus.WebApp.Services.Operations;

/// <summary>
/// Default <see cref="IOperatorCommands"/>. A command runs in this order:
/// <list type="number">
/// <item>the row's current state must allow it;</item>
/// <item>the caller must hold Contributor on the endpoint, checked against fresh access-control
/// lists (a denial is audited);</item>
/// <item>the row is claimed with the store's guarded archive at the version the caller saw, so a
/// concurrent command on the same version loses and sends nothing;</item>
/// <item>the audit row is written, and a failure releases the claim and refuses the command;</item>
/// <item>the command is published, and a failure releases the claim, is audited as
/// <see cref="MessageAuditType.CommandNotSent"/>, and is rethrown.</item>
/// </list>
/// A crash between the claim and the publish leaves the row archived with no command sent; the
/// audit row written before the publish is the trace. Spec 035 Phase 2b's operation journal
/// closes that gap.
/// </summary>
public sealed partial class OperatorCommandCoordinator : IOperatorCommands
{
    // Payload-carrying request message types: the ones that carry the original event JSON to be
    // re-delivered on resubmit. Mirrored by the frontend's PAYLOAD_REQUEST_TYPES.
    private static readonly MessageType[] PayloadCarryingRequestTypes =
    {
        MessageType.EventRequest,
        MessageType.ResubmissionRequest,
        MessageType.RetryRequest,
        MessageType.ContinuationRequest,
        MessageType.ProcessDeferredRequest,
    };

    // What the Web UI offers resubmit and skip on (events-panel ACTIONABLE_STATUSES, plus the
    // Pending+Handoff manual override). The MCP server is narrower: handoff and deferred
    // recovery are Spec 035 Phase 3.
    private static readonly IReadOnlySet<ResolutionStatus> WebAppActionable = new HashSet<ResolutionStatus>
    {
        ResolutionStatus.Failed, ResolutionStatus.DeadLettered, ResolutionStatus.Unsupported, ResolutionStatus.Deferred,
    };

    private static readonly IReadOnlySet<ResolutionStatus> McpActionable = new HashSet<ResolutionStatus>
    {
        ResolutionStatus.Failed, ResolutionStatus.DeadLettered, ResolutionStatus.Unsupported,
    };

    private const string HandoffSubStatus = "Handoff";

    private readonly IMessageTrackingStore _store;
    private readonly IManagerClient _managerClient;
    private readonly IEndpointAuthorizationService _authorization;
    private readonly IAuditLogService _audit;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IPlatform _platform;
    private readonly ILogger<OperatorCommandCoordinator> _logger;

    /// <summary>Creates the coordinator.</summary>
    public OperatorCommandCoordinator(
        IMessageTrackingStore store,
        IManagerClient managerClient,
        IEndpointAuthorizationService authorization,
        IAuditLogService audit,
        IHttpContextAccessor httpContextAccessor,
        IPlatform platform,
        ILogger<OperatorCommandCoordinator> logger)
    {
        _store = store;
        _managerClient = managerClient;
        _authorization = authorization;
        _audit = audit;
        _httpContextAccessor = httpContextAccessor;
        _platform = platform;
        _logger = logger;
    }

    /// <summary>
    /// Whether <paramref name="row"/>'s current state allows resubmit or skip from
    /// <paramref name="channel"/>.
    /// </summary>
    public static bool IsActionable(UnresolvedEvent row, OperatorChannel channel)
    {
        ArgumentNullException.ThrowIfNull(row);
        return channel == OperatorChannel.Mcp
            ? McpActionable.Contains(row.ResolutionStatus)
            : WebAppActionable.Contains(row.ResolutionStatus)
              || (row.ResolutionStatus == ResolutionStatus.Pending
                  && string.Equals(row.PendingSubStatus, HandoffSubStatus, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The endpoint a command for <paramref name="message"/> goes to: the receiving endpoint of a
    /// self-originating (dead-letter forwarded) message, otherwise the endpoint that failed.
    /// </summary>
    public static string? TargetEndpoint(MessageEntity message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return BlockedEventRules.IsSelfOriginating(message.OriginatingMessageId) ? message.To : message.From;
    }

    /// <summary>
    /// The latest request message that still carries the event payload: the original
    /// EventRequest, or a later Resubmission/Retry/Continuation/ProcessDeferredRequest.
    /// </summary>
    internal static MessageEntity? LatestRequestMessageWithPayload(IEnumerable<MessageEntity> history) =>
        history
            .Where(m => PayloadCarryingRequestTypes.Contains(m.MessageType)
                     && !string.IsNullOrEmpty(m.MessageContent?.EventContent?.EventJson))
            .OrderByDescending(m => m.EnqueuedTimeUtc)
            .FirstOrDefault();

    /// <inheritdoc/>
    public async Task<OperatorTargetLookup> FindByMessageAsync(string eventId, string messageId)
    {
        var message = await GetMessageWithFallback(eventId, messageId);
        if (message == null)
            return new(OperatorCommandStatus.NotFound);

        var endpointId = TargetEndpoint(message);
        if (string.IsNullOrEmpty(endpointId))
            return new(OperatorCommandStatus.NotFound);

        UnresolvedEvent? row;
        try
        {
            row = await _store.GetEvent(endpointId, eventId);
        }
        catch (EndpointNotFoundException)
        {
            return new(OperatorCommandStatus.NotFound);
        }

        // The UI acts on the row's latest message. A row that is gone (already resubmitted,
        // skipped or purged) or has moved on to a newer attempt is no longer what the
        // operator looked at.
        if (row == null
            || !string.Equals(row.SessionId, message.SessionId, StringComparison.Ordinal)
            || (row.LastMessageId != null && !string.Equals(row.LastMessageId, messageId, StringComparison.Ordinal)))
        {
            return new(OperatorCommandStatus.Stale);
        }

        return new(OperatorCommandStatus.Accepted,
            new OperatorCommandTarget(endpointId, row, message, OperatorMessageVersion.From(row)));
    }

    /// <inheritdoc/>
    public async Task<OperatorTargetLookup> FindCurrentAsync(string endpointId, string eventId)
    {
        if (string.IsNullOrEmpty(endpointId) || string.IsNullOrEmpty(eventId))
            return new(OperatorCommandStatus.NotFound);

        UnresolvedEvent? row;
        try
        {
            row = await _store.GetEvent(endpointId, eventId);
        }
        catch (EndpointNotFoundException)
        {
            return new(OperatorCommandStatus.NotFound);
        }

        if (row == null)
            return new(OperatorCommandStatus.NotFound);

        var message = (string.IsNullOrEmpty(row.LastMessageId) ? null : await _store.GetMessage(eventId, row.LastMessageId))
            ?? MessageEntityFromUnresolvedEvent(row);

        // Commands go to the endpoint the latest message names. A row whose message points
        // elsewhere is not one this endpoint's caller may act on.
        if (!string.Equals(TargetEndpoint(message), endpointId, StringComparison.OrdinalIgnoreCase))
            return new(OperatorCommandStatus.NotAllowed);

        return new(OperatorCommandStatus.Accepted,
            new OperatorCommandTarget(endpointId, row, message, OperatorMessageVersion.From(row)));
    }

    /// <inheritdoc/>
    public async Task<string> ResolveEventTypeIdAsync(OperatorCommandTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);
        var message = target.Message;
        if (!string.IsNullOrEmpty(message.EventTypeId))
            return message.EventTypeId;

        // For a failed hand-off the terminal ErrorResponse carries no event type, so resolve it
        // from the request history rather than the originating message. Falls back to the
        // originating-message lookup, and finally to the terminal message itself.
        var history = await _store.GetEventHistory(target.EventId);
        var requestMessage = LatestRequestMessageWithPayload(history)
            ?? await GetMessageWithFallback(target.EventId, message.OriginatingMessageId)
            ?? message;
        return !string.IsNullOrWhiteSpace(requestMessage.EventTypeId)
            ? requestMessage.EventTypeId
            : requestMessage.MessageContent?.EventContent?.EventTypeId!;
    }

    /// <inheritdoc/>
    public async Task<OperatorCommandResult> ResubmitAsync(OperatorCommandTarget target, OperatorCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);
        var message = target.Message;

        // Resubmit must replay the original event payload. For a failed hand-off the latest
        // message is the terminal ErrorResponse, whose content carries no usable event JSON, so
        // source the payload (and, when missing, the event type) from the latest REQUEST message
        // that carries it. A CloudEvent may be stored without native EventContent; the
        // subscriber's PendingHandoffResponse preserves the validated event JSON it parked.
        var history = (await _store.GetEventHistory(target.EventId)).ToList();
        var latestRequest = LatestRequestMessageWithPayload(history);
        var parkedPayload = history
            .Where(m => m.MessageType == MessageType.PendingHandoffResponse
                && !string.IsNullOrEmpty(m.MessageContent?.EventContent?.EventJson))
            .OrderByDescending(m => m.EnqueuedTimeUtc)
            .FirstOrDefault();
        var requestMessage = latestRequest ?? parkedPayload ?? message;

        var eventTypeId = message.EventTypeId;
        if (string.IsNullOrEmpty(eventTypeId))
        {
            var typeSource = latestRequest ?? parkedPayload
                ?? await GetMessageWithFallback(target.EventId, message.OriginatingMessageId)
                ?? message;
            eventTypeId = !string.IsNullOrWhiteSpace(typeSource.EventTypeId)
                ? typeSource.EventTypeId
                : typeSource.MessageContent?.EventContent?.EventTypeId!;
        }

        var eventJson = requestMessage.MessageContent?.EventContent?.EventJson!;
        return await RunAsync(target, context, MessageAuditType.Resubmit, eventTypeId, Describe(target, context),
            () => _managerClient.Resubmit(message, target.EndpointId, eventTypeId, eventJson));
    }

    /// <inheritdoc/>
    public Task<OperatorCommandResult> ResubmitWithChangesAsync(OperatorCommandTarget target, string eventTypeId,
        string eventJson, string auditData, OperatorCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);
        return RunAsync(target, context, MessageAuditType.ResubmitWithChanges, eventTypeId, auditData,
            () => _managerClient.Resubmit(target.Message, target.EndpointId, eventTypeId, eventJson));
    }

    /// <inheritdoc/>
    public async Task<OperatorCommandResult> SkipAsync(OperatorCommandTarget target, OperatorCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);
        var message = target.Message;

        // The originating request can be gone; skip routes on the endpoint and does not need the
        // event type, so proceed without it rather than failing.
        var eventTypeId = message.EventTypeId;
        if (string.IsNullOrEmpty(eventTypeId))
            eventTypeId = (await GetMessageWithFallback(target.EventId, message.OriginatingMessageId))?.EventTypeId!;

        return await RunAsync(target, context, MessageAuditType.Skip, eventTypeId, Describe(target, context),
            () => _managerClient.Skip(message, target.EndpointId, eventTypeId));
    }

    /// <inheritdoc/>
    public async Task<OperatorCommandResult> SetReportedAsync(string endpointId, string eventId, bool reported,
        string? ticketId, OperatorCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (string.IsNullOrEmpty(endpointId) || string.IsNullOrEmpty(eventId))
            return new(OperatorCommandStatus.Invalid, "endpointId and eventId are required.");

        var canonical = _platform.Endpoints.FirstOrDefault(e => e.Id.Equals(endpointId, StringComparison.OrdinalIgnoreCase))?.Id;

        if (!await _authorization.HasRoleFreshAsync(AccessRole.Contributor, canonical ?? endpointId))
        {
            await _audit.LogAuditAsync(MessageAuditType.ReportEvent, _httpContextAccessor.HttpContext!,
                accessDenied: true, eventId: eventId, endpointId: endpointId);
            return new(OperatorCommandStatus.Forbidden);
        }

        // Store under the platform's canonical endpoint casing: authorization and existence
        // checks are case-insensitive, but Cosmos partitions (and the enrichment lookups) match
        // the endpoint id exactly, so a lowercase request must not create a marker searches
        // never find.
        if (canonical == null)
            return new(OperatorCommandStatus.NotFound, "Endpoint not found");

        string? normalizedTicket = null;
        if (reported && !string.IsNullOrWhiteSpace(ticketId))
        {
            normalizedTicket = ticketId.Trim();
            if (!TicketIdPattern().IsMatch(normalizedTicket))
                return new(OperatorCommandStatus.Invalid, "Ticket id may use letters, digits, '.', '_' and '-' (max 64 chars).");
        }

        var data = JsonConvert.SerializeObject(new
        {
            reported,
            ticketId = normalizedTicket,
            channel = context.Channel.ToString(),
            reason = context.Reason,
            idempotencyKey = context.IdempotencyKey,
            clientId = context.ClientId,
        });
        try
        {
            await _audit.LogRequiredAuditAsync(MessageAuditType.ReportEvent, _httpContextAccessor.HttpContext!,
                data: data, eventId: eventId, endpointId: canonical);
        }
        catch (AuditUnavailableException)
        {
            return new(OperatorCommandStatus.AuditUnavailable, AuditUnavailableDetail);
        }

        var reportedBy = _authorization.GetCurrentUserName() ?? "anonymous";
        await _store.SetEventReport(canonical, eventId, reported, reportedBy, normalizedTicket);
        return OperatorCommandResult.Accepted;
    }

    private const string AuditUnavailableDetail = "The audit log is unavailable, so the command was not run. Try again later.";

    private async Task<OperatorCommandResult> RunAsync(
        OperatorCommandTarget target,
        OperatorCommandContext context,
        MessageAuditType auditType,
        string? eventTypeId,
        string auditData,
        Func<Task> publish)
    {
        var httpContext = _httpContextAccessor.HttpContext!;
        if (!IsActionable(target.Row, context.Channel))
            return new(OperatorCommandStatus.NotAllowed, $"A {target.Row.ResolutionStatus} message cannot be {Verb(auditType)}.");

        if (!await _authorization.HasRoleFreshAsync(AccessRole.Contributor, target.EndpointId))
        {
            await _audit.LogAuditAsync(auditType, httpContext, accessDenied: true, data: auditData,
                eventId: target.EventId, endpointId: target.EndpointId, eventTypeId: eventTypeId);
            return new(OperatorCommandStatus.Forbidden);
        }

        var version = target.Version;
        if (!await _store.TryArchiveUnresolvedEvent(target.EventId, version.SessionId!, target.EndpointId,
                version.Status, version.LastMessageId, version.UpdatedAt))
        {
            return new(OperatorCommandStatus.Stale);
        }

        try
        {
            await _audit.LogRequiredAuditAsync(auditType, httpContext, data: auditData,
                eventId: target.EventId, endpointId: target.EndpointId, eventTypeId: eventTypeId);
        }
        catch (AuditUnavailableException)
        {
            await ReleaseClaimAsync(target);
            return new(OperatorCommandStatus.AuditUnavailable, AuditUnavailableDetail);
        }

        try
        {
            await publish();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Publishing {AuditType} for event {EventId} on {EndpointId} failed; releasing the claim",
                auditType, target.EventId, target.EndpointId);
            await ReleaseClaimAsync(target);
            await _audit.LogAuditAsync(MessageAuditType.CommandNotSent, httpContext,
                data: JsonConvert.SerializeObject(new { command = auditType.ToString(), error = ex.GetType().Name }),
                eventId: target.EventId, endpointId: target.EndpointId, eventTypeId: eventTypeId);
            throw;
        }

        return OperatorCommandResult.Accepted;
    }

    private async Task ReleaseClaimAsync(OperatorCommandTarget target)
    {
        var version = target.Version;
        try
        {
            if (!await _store.TryRestoreArchivedEvent(target.EventId, version.SessionId!, target.EndpointId,
                    version.Status, version.LastMessageId, version.UpdatedAt))
            {
                _logger.LogWarning("Event {EventId} on {EndpointId} could not be restored after an unsent command; a newer write already replaced it",
                    target.EventId, target.EndpointId);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Restoring event {EventId} on {EndpointId} after an unsent command failed; it stays archived",
                target.EventId, target.EndpointId);
        }
    }

    private static string Describe(OperatorCommandTarget target, OperatorCommandContext context) =>
        JsonConvert.SerializeObject(new
        {
            channel = context.Channel.ToString(),
            reason = context.Reason,
            idempotencyKey = context.IdempotencyKey,
            clientId = context.ClientId,
            priorStatus = target.Row.ResolutionStatus.ToString(),
            messageId = target.Version.LastMessageId,
            messageVersion = target.Version.Encode(),
        });

    private static string Verb(MessageAuditType type) => type == MessageAuditType.Skip ? "skipped" : "resubmitted";

    private async Task<MessageEntity?> GetMessageWithFallback(string eventId, string? messageId)
    {
        if (!string.IsNullOrEmpty(messageId))
        {
            var message = await _store.GetMessage(eventId, messageId);
            if (message != null)
                return message;
        }

        // Fallback: the message wasn't in the shared messages container, so probe the
        // per-endpoint containers. An event lives in exactly one of them, so probe concurrently.
        var probes = _platform.Endpoints.Select(async ep =>
        {
            try
            {
                return (ep.Id, Event: await _store.GetEvent(ep.Id, eventId));
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Fallback lookup failed for endpoint {EndpointId}", ep.Id);
                return (ep.Id, Event: (UnresolvedEvent?)null);
            }
        });

        var hit = Array.Find(await Task.WhenAll(probes), r => r.Event != null);
        if (hit.Event == null)
            return null;

        _logger.LogInformation("Message {MessageId} not found in messages container, using fallback from endpoint {EndpointId}", messageId, hit.Id);
        return MessageEntityFromUnresolvedEvent(hit.Event);
    }

    private static MessageEntity MessageEntityFromUnresolvedEvent(UnresolvedEvent e) => new()
    {
        EventId = e.EventId,
        MessageId = e.LastMessageId!,
        EventTypeId = e.EventTypeId!,
        OriginatingMessageId = e.OriginatingMessageId!,
        ParentMessageId = e.ParentMessageId!,
        From = e.From!,
        To = e.To!,
        OriginatingFrom = e.OriginatingFrom!,
        SessionId = e.SessionId!,
        CorrelationId = e.CorrelationId!,
        EnqueuedTimeUtc = e.EnqueuedTimeUtc,
        MessageContent = e.MessageContent,
        MessageType = e.MessageType,
        EndpointRole = e.EndpointRole,
        EndpointId = e.EndpointId,
        RetryCount = e.RetryCount,
        RetryLimit = e.RetryLimit,
        DeadLetterReason = e.DeadLetterReason!,
        DeadLetterErrorDescription = e.DeadLetterErrorDescription!,
        QueueTimeMs = e.QueueTimeMs,
        ProcessingTimeMs = e.ProcessingTimeMs,
    };

    // Generic external-ticket reference: a sane cross-tool subset (Jira keys, ServiceNow INC
    // numbers, plain ids). Mirrored by the frontend's normalizeTicketId and the EventReports
    // TicketId column width (64).
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$")]
    private static partial Regex TicketIdPattern();
}
