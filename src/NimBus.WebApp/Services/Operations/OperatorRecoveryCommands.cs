using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.SDK;
using DeferredInspection = NimBus.WebApp.ManagementApi.DeferredInspection;

namespace NimBus.WebApp.Services.Operations;

/// <summary>What a deferred reprocess did.</summary>
/// <param name="Result">Whether the command ran.</param>
/// <param name="SessionStateCleared">The session's block and drain request were reset.</param>
/// <param name="ProcessRequestSent">The drain of the session's deferred messages was requested.</param>
/// <param name="Errors">What failed, when a step did.</param>
public sealed record DeferredReprocessOutcome(
    OperatorCommandResult Result,
    bool SessionStateCleared = false,
    bool ProcessRequestSent = false,
    IReadOnlyList<string>? Errors = null);

/// <summary>
/// The recovery commands the operator MCP server adds in Spec 035 Phase 3: settling a pending
/// handoff, reprocessing a session's deferred messages and skipping a stale Deferred tracking
/// row. Each checks Contributor against fresh access-control lists and audits a denial. The
/// caller has already validated the action token, the message version and the state.
/// </summary>
public interface IOperatorRecoveryCommands
{
    /// <summary>
    /// Completes or fails the pending handoff <paramref name="target"/> names. The settlement is
    /// claimed, audited and only then sent; an unrecordable audit sends nothing.
    /// </summary>
    /// <param name="target">The parked event, at the version the caller previewed.</param>
    /// <param name="complete">True to complete the handoff, false to fail it.</param>
    /// <param name="errorType">An optional error type for a failed handoff.</param>
    /// <param name="context">Who settles it; <see cref="OperatorCommandContext.Reason"/> is the note or the failure reason.</param>
    Task<OperatorCommandResult> SettleHandoffAsync(OperatorCommandTarget target, bool complete, string? errorType, OperatorCommandContext context);

    /// <summary>
    /// Resets the session's deferred state and requests a drain of its deferred messages, after
    /// the audit row is written.
    /// </summary>
    /// <param name="endpointId">The endpoint, in the platform's casing.</param>
    /// <param name="sessionId">The session.</param>
    /// <param name="deferredCount">How many deferred messages the caller previewed, for the audit row.</param>
    /// <param name="context">Who asks, and why.</param>
    Task<DeferredReprocessOutcome> ReprocessDeferredAsync(string endpointId, string sessionId, int deferredCount, OperatorCommandContext context);

    /// <summary>
    /// The broker and history inspection of a Deferred tracking row, or null when there is no
    /// such row. Reads only.
    /// </summary>
    Task<(UnresolvedEvent Row, DeferredInspection Inspection)?> InspectDeferredAsync(string endpointId, string eventId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Marks the stale Deferred tracking row Skipped when it still is the inspected
    /// <paramref name="rowVersion"/> and a repeated inspection allows it. Nothing is removed
    /// from the broker.
    /// </summary>
    Task<(OperatorCommandResult Result, bool AuditRecorded)> SkipDeferredTrackingAsync(
        string endpointId, string eventId, string rowVersion, OperatorCommandContext context, CancellationToken cancellationToken = default);
}

/// <summary>Default <see cref="IOperatorRecoveryCommands"/>, over the services the Web UI's recovery actions use.</summary>
public sealed partial class OperatorRecoveryCommands : IOperatorRecoveryCommands
{
    private const string HandoffSubStatus = "Handoff";
    private const string AuditUnavailableDetail = "The audit log is unavailable, so the command was not run. Try again later.";

    private readonly IEndpointAuthorizationService _authorization;
    private readonly IAuditLogService _audit;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IHandoffClientFactory _handoffClients;
    private readonly HandoffSettlementTracker _inFlight;
    private readonly IAdminService _admin;
    private readonly DeferredTrackingRecovery _deferred;
    private readonly ILogger<OperatorRecoveryCommands> _logger;

    /// <summary>Creates the commands.</summary>
    public OperatorRecoveryCommands(
        IEndpointAuthorizationService authorization,
        IAuditLogService audit,
        IHttpContextAccessor httpContextAccessor,
        IHandoffClientFactory handoffClients,
        HandoffSettlementTracker inFlight,
        IAdminService admin,
        DeferredTrackingRecovery deferred,
        ILogger<OperatorRecoveryCommands> logger)
    {
        _authorization = authorization;
        _audit = audit;
        _httpContextAccessor = httpContextAccessor;
        _handoffClients = handoffClients;
        _inFlight = inFlight;
        _admin = admin;
        _deferred = deferred;
        _logger = logger;
    }

    /// <summary>Whether <paramref name="row"/> is a handoff waiting for its external work.</summary>
    public static bool IsPendingHandoff(UnresolvedEvent row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.ResolutionStatus == ResolutionStatus.Pending
            && string.Equals(row.PendingSubStatus, HandoffSubStatus, StringComparison.Ordinal);
    }

    /// <inheritdoc/>
    public async Task<OperatorCommandResult> SettleHandoffAsync(OperatorCommandTarget target, bool complete, string? errorType, OperatorCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(context);
        var httpContext = _httpContextAccessor.HttpContext!;
        var row = target.Row;
        var auditType = complete ? MessageAuditType.CompleteHandoff : MessageAuditType.FailHandoff;

        if (!IsPendingHandoff(row))
            return new(OperatorCommandStatus.NotAllowed, $"A {row.ResolutionStatus} message is not a pending handoff.");

        if (!await _authorization.HasRoleFreshAsync(AccessRole.Contributor, target.EndpointId))
        {
            await _audit.LogAuditAsync(auditType, httpContext, accessDenied: true, data: Describe(target, context, complete, errorType),
                eventId: target.EventId, endpointId: target.EndpointId, eventTypeId: row.EventTypeId);
            return new(OperatorCommandStatus.Forbidden);
        }

        // The row reads Pending+Handoff until the Resolver projects the settlement, so claim the
        // parked message: a second settle in that window is refused, as in the Web UI.
        if (!_inFlight.TryBegin(target.EndpointId, target.EventId, row.LastMessageId))
            return new(OperatorCommandStatus.NotAllowed, "A settlement of this handoff is already in flight.");

        try
        {
            await _audit.LogRequiredAuditAsync(auditType, httpContext, data: Describe(target, context, complete, errorType),
                eventId: target.EventId, endpointId: target.EndpointId, eventTypeId: row.EventTypeId);
        }
        catch (AuditUnavailableException)
        {
            _inFlight.Abandon(target.EndpointId, target.EventId, row.LastMessageId);
            return new(OperatorCommandStatus.AuditUnavailable, AuditUnavailableDetail);
        }

        var settlement = new HandoffSettlement(
            EventId: row.EventId,
            SessionId: row.SessionId!,
            // The control message is parented to the parked attempt, as the Web UI sends it.
            MessageId: row.LastMessageId!,
            EventTypeId: row.EventTypeId!,
            CorrelationId: row.CorrelationId,
            OriginatingMessageId: row.OriginatingMessageId);
        try
        {
            var client = _handoffClients.ForEndpoint(target.EndpointId);
            if (complete)
            {
                // Carry the note and who completed it on the completion payload, so the
                // Completed row records the manual intervention.
                var details = JsonConvert.SerializeObject(new { note = context.Reason, completedBy = _authorization.GetCurrentUserName() });
                await client.CompleteAsync(settlement, details);
            }
            else
            {
                await client.FailAsync(settlement, context.Reason!, errorType);
            }
        }
        catch (Exception ex)
        {
            // Nothing was sent, so nothing is in flight: release the claim for a retry.
            _inFlight.Abandon(target.EndpointId, target.EventId, row.LastMessageId);
            LogSettlementNotSent(_logger, ex, auditType, target.EventId, target.EndpointId);
            await _audit.LogAuditAsync(MessageAuditType.CommandNotSent, httpContext,
                data: JsonConvert.SerializeObject(new { command = auditType.ToString(), error = ex.GetType().Name }),
                eventId: target.EventId, endpointId: target.EndpointId, eventTypeId: row.EventTypeId);
            throw;
        }

        return OperatorCommandResult.Accepted;
    }

    /// <inheritdoc/>
    public async Task<DeferredReprocessOutcome> ReprocessDeferredAsync(string endpointId, string sessionId, int deferredCount, OperatorCommandContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var httpContext = _httpContextAccessor.HttpContext!;
        var data = JsonConvert.SerializeObject(new
        {
            channel = context.Channel.ToString(),
            reason = context.Reason,
            idempotencyKey = context.IdempotencyKey,
            clientId = context.ClientId,
            sessionId,
            deferredCount,
        });

        if (!await _authorization.HasRoleFreshAsync(AccessRole.Contributor, endpointId))
        {
            await _audit.LogAuditAsync(MessageAuditType.ReprocessDeferred, httpContext, accessDenied: true, data: data, endpointId: endpointId);
            return new(new(OperatorCommandStatus.Forbidden));
        }

        try
        {
            await _audit.LogRequiredAuditAsync(MessageAuditType.ReprocessDeferred, httpContext, data: data, endpointId: endpointId);
        }
        catch (AuditUnavailableException)
        {
            return new(new(OperatorCommandStatus.AuditUnavailable, AuditUnavailableDetail));
        }

        var result = await _admin.ReprocessDeferredAsync(endpointId, sessionId);
        return new(OperatorCommandResult.Accepted, result.SessionStateCleared, result.ProcessRequestSent, result.Errors?.ToList() ?? []);
    }

    /// <inheritdoc/>
    public async Task<(UnresolvedEvent Row, DeferredInspection Inspection)?> InspectDeferredAsync(string endpointId, string eventId, CancellationToken cancellationToken = default)
    {
        var row = await _deferred.FindRowAsync(endpointId, eventId);
        if (row is null)
            return null;
        return (row, await _deferred.InspectAsync(row, cancellationToken));
    }

    /// <inheritdoc/>
    public async Task<(OperatorCommandResult Result, bool AuditRecorded)> SkipDeferredTrackingAsync(
        string endpointId, string eventId, string rowVersion, OperatorCommandContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        var httpContext = _httpContextAccessor.HttpContext!;
        var origin = new Dictionary<string, object?>
        {
            ["channel"] = context.Channel.ToString(),
            ["reason"] = context.Reason,
            ["idempotencyKey"] = context.IdempotencyKey,
            ["clientId"] = context.ClientId,
        };

        if (!await _authorization.HasRoleFreshAsync(AccessRole.Contributor, endpointId))
        {
            await _audit.LogAuditAsync(MessageAuditType.Skip, httpContext, accessDenied: true, eventId: eventId, endpointId: endpointId,
                data: JsonConvert.SerializeObject(new Dictionary<string, object?>(origin) { ["action"] = "skip-deferred-tracking" }));
            return (new(OperatorCommandStatus.Forbidden), false);
        }

        var outcome = await _deferred.SkipAsync(
            endpointId, eventId, rowVersion, context.Reason!,
            _authorization.GetCurrentUserName() ?? AuditLogService.ResolveAuditorName(httpContext),
            origin, cancellationToken);
        return outcome.Status switch
        {
            DeferredTrackingSkipStatus.Skipped => (OperatorCommandResult.Accepted, outcome.AuditRecorded),
            DeferredTrackingSkipStatus.NotFound => (new(OperatorCommandStatus.NotFound), false),
            DeferredTrackingSkipStatus.Changed => (new(OperatorCommandStatus.Stale, outcome.Detail), false),
            DeferredTrackingSkipStatus.NotSupported =>
                (new(OperatorCommandStatus.NotAllowed, "This storage provider does not support conditional deferred recovery."), false),
            _ => (new(OperatorCommandStatus.NotAllowed, outcome.Detail), false),
        };
    }

    private static string Describe(OperatorCommandTarget target, OperatorCommandContext context, bool complete, string? errorType) =>
        JsonConvert.SerializeObject(new
        {
            channel = context.Channel.ToString(),
            reason = context.Reason,
            idempotencyKey = context.IdempotencyKey,
            clientId = context.ClientId,
            outcome = complete ? "complete" : "fail",
            errorType,
            priorStatus = target.Row.ResolutionStatus.ToString(),
            messageId = target.Version.LastMessageId,
            messageVersion = target.Version.Encode(),
        });

    [LoggerMessage(Level = LogLevel.Error, Message = "Sending {AuditType} for event {EventId} on {EndpointId} failed; the settlement claim was released")]
    private static partial void LogSettlementNotSent(ILogger logger, Exception exception, MessageAuditType auditType, string eventId, string endpointId);
}
