using System.ComponentModel;
using ModelContextProtocol.Server;
using Newtonsoft.Json;
using NimBus.MessageStore;
using NimBus.WebApp.Mcp.Operations;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.Operations;

namespace NimBus.WebApp.Mcp.Tools;

/// <summary>
/// Spec 035 Phase 3: the recovery actions the Web UI offers beyond resubmit and skip. Settling a
/// pending handoff, reprocessing a session's deferred messages, skipping a stale Deferred
/// tracking record and resubmitting with an edited payload each take the same two steps as
/// resubmit and skip, need their own delegated scope and Owner switch, and never run for an
/// unattended workload.
/// </summary>
public sealed partial class OperatorActionTools
{
    /// <summary>Longest error type accepted for a failed handoff.</summary>
    public const int MaxErrorTypeLength = 200;

    /// <summary>Runs a prepared handoff settlement.</summary>
    [McpServerTool(Name = "nimbus_settle_handoff", Title = "Settle a NimBus pending handoff", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Completes or fails the pending handoff an actionToken from nimbus_prepare_action (action settle_handoff) describes: the endpoint records the outcome of the external work without running its handler again. Use it only when the user knows that outcome. Runs only while the message is still the pending handoff you previewed, and one settlement at a time. Requires the delegated nimbus.settle scope and the Contributor role; workloads can never settle. Confirm with the user first. The result says the settlement was sent: read the message again after about 10 seconds to see it.")]
    public async Task<OperatorActionResult> SettleHandoffAsync(
        [Description("actionToken from nimbus_prepare_action with action settle_handoff.")] string actionToken,
        [Description("A new GUID for this request, recorded in the audit log.")] string idempotencyKey,
        [Description("complete (the external work succeeded) or fail (it did not).")] string outcome,
        [Description("For complete: a note on how you know the work finished. For fail: why it failed. 1-1000 characters. Recorded in the audit log and sent to the endpoint.")] string reason,
        [Description("For fail only: an optional error type, at most 200 characters.")] string? errorType = null)
    {
        var complete = outcome?.Trim().ToLowerInvariant() switch
        {
            "complete" => true,
            "fail" => false,
            _ => throw OperatorToolErrors.InvalidArgument("outcome must be complete or fail."),
        };
        errorType = string.IsNullOrWhiteSpace(errorType) ? null : errorType.Trim();
        if (complete && errorType is not null)
            throw OperatorToolErrors.InvalidArgument("errorType is only for outcome fail.");
        if (errorType is { Length: > MaxErrorTypeLength })
            throw OperatorToolErrors.InvalidArgument($"errorType is at most {MaxErrorTypeLength} characters.");

        var (grant, endpoint, target, context) = await OpenRecoveryAsync(OperatorAction.SettleHandoff, actionToken, idempotencyKey, reason).ConfigureAwait(false);
        RequireRecoverableState(OperatorAction.SettleHandoff, target.Row);

        OperatorCommandResult result;
        try
        {
            result = await _recovery.SettleHandoffAsync(target, complete, errorType, context).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ModelContextProtocol.McpException)
        {
            LogMcpOfFailedWhileSending(_logger, ex, OperatorAction.SettleHandoff, endpoint, grant.EventId);
            throw OperatorToolErrors.OutcomeUnknown(
                "Sending the settlement failed and its claim was released, but the send may have reached Service Bus. Read the message again before trying again.");
        }

        ThrowUnlessAccepted(result, OperatorAction.SettleHandoff, endpoint, grant.EventId);
        LogMcpOfAccepted(_logger, OperatorAction.SettleHandoff, endpoint, grant.EventId, OperatorActionTokens.Identify(User()).Caller);
        return new OperatorActionResult(_catalog.Environment, DateTimeOffset.UtcNow, OperatorActionAccess.NameOf(OperatorAction.SettleHandoff),
            endpoint, grant.EventId, "Accepted", CommandSent: true,
            complete
                ? "The completion was audited and sent. Read the message again with nimbus_get_message in about 10 seconds: it should be Completed."
                : "The failure was audited and sent. Read the message again with nimbus_get_message in about 10 seconds: it should be Failed, and it then blocks its session until it is resubmitted or skipped.");
    }

    /// <summary>Runs a prepared reprocess of a session's deferred messages.</summary>
    [McpServerTool(Name = "nimbus_reprocess_deferred", Title = "Reprocess a NimBus session's deferred messages", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Resets the blocked state of the session an actionToken from nimbus_prepare_action (action reprocess_deferred) describes and asks its endpoint to process the session's deferred messages again, in order. Nothing is skipped or deleted. Refused while a failed message still blocks the session (resubmit or skip that message instead) and for a session with more deferred messages than the server allows. Requires the delegated nimbus.recover scope and the Contributor role; workloads can never run it. Confirm with the user first. Read the session again with nimbus_get_session after about 15 seconds to see what is still deferred.")]
    public async Task<OperatorActionResult> ReprocessDeferredAsync(
        [Description("actionToken from nimbus_prepare_action with action reprocess_deferred.")] string actionToken,
        [Description("A new GUID for this request, recorded in the audit log.")] string idempotencyKey,
        [Description("Why you are reprocessing, 1-1000 characters. Recorded in the audit log.")] string reason)
    {
        var (grant, endpoint, target, context) = await OpenRecoveryAsync(OperatorAction.ReprocessDeferred, actionToken, idempotencyKey, reason).ConfigureAwait(false);
        RequireRecoverableState(OperatorAction.ReprocessDeferred, target.Row);
        var sessionId = target.Row.SessionId!;
        var deferred = await RequireReprocessableSessionAsync(endpoint, sessionId).ConfigureAwait(false);

        var outcome = await _recovery.ReprocessDeferredAsync(endpoint, sessionId, grant.EventId, deferred, context).ConfigureAwait(false);
        ThrowUnlessAccepted(outcome.Result, OperatorAction.ReprocessDeferred, endpoint, grant.EventId);
        LogMcpOfAccepted(_logger, OperatorAction.ReprocessDeferred, endpoint, grant.EventId, OperatorActionTokens.Identify(User()).Caller);

        var errors = (outcome.Errors ?? []).Select(error => OperatorProjection.Truncate(error).Text ?? string.Empty).ToList();
        return new OperatorActionResult(_catalog.Environment, DateTimeOffset.UtcNow, OperatorActionAccess.NameOf(OperatorAction.ReprocessDeferred),
            endpoint, grant.EventId, "Accepted", outcome.ProcessRequestSent,
            outcome.ProcessRequestSent
                ? "The reprocess was audited and requested. Read the session again with nimbus_get_session in about 15 seconds to see what is still deferred."
                : "The reprocess was audited, but the request to process the deferred messages was not sent; see errors. Nothing was skipped or deleted. Try again later or hand it to a person.")
        {
            SessionStateCleared = outcome.SessionStateCleared,
            Errors = errors.Count == 0 ? null : errors,
        };
    }

    /// <summary>Runs a prepared skip of a stale Deferred tracking record.</summary>
    [McpServerTool(Name = "nimbus_skip_deferred_tracking", Title = "Skip a stale NimBus deferred tracking record", ReadOnly = false, Idempotent = true, Destructive = true, OpenWorld = false, UseStructuredContent = true)]
    [Description("Marks the Deferred tracking record an actionToken from nimbus_prepare_action (action skip_deferred_tracking) describes as Skipped. This changes the record in NimBus only: no message is removed from Service Bus, no session state changes, its history is kept, and work that is already running is not cancelled. The broker is inspected again first, and the skip is refused unless the record has been unchanged for 15 minutes and no matching message is found anywhere. Requires the delegated nimbus.recover scope and the Contributor role; workloads can never run it. Confirm with the user first. auditRecorded says whether the audit row was written; the record is Skipped either way.")]
    public async Task<OperatorActionResult> SkipDeferredTrackingAsync(
        [Description("actionToken from nimbus_prepare_action with action skip_deferred_tracking.")] string actionToken,
        [Description("A new GUID for this request, recorded in the audit log.")] string idempotencyKey,
        [Description("Why the record is stale and how its outcome was verified, 1-1000 characters. Recorded in the audit log.")] string reason,
        CancellationToken cancellationToken = default)
    {
        var (grant, endpoint, target, context) = await OpenRecoveryAsync(OperatorAction.SkipDeferredTracking, actionToken, idempotencyKey, reason).ConfigureAwait(false);
        RequireRecoverableState(OperatorAction.SkipDeferredTracking, target.Row);

        var (result, auditRecorded) = await _recovery.SkipDeferredTrackingAsync(
            endpoint, grant.EventId, DeferredMessageInspector.RowVersion(target.Row), context, cancellationToken).ConfigureAwait(false);
        ThrowUnlessAccepted(result, OperatorAction.SkipDeferredTracking, endpoint, grant.EventId);
        LogMcpOfAccepted(_logger, OperatorAction.SkipDeferredTracking, endpoint, grant.EventId, OperatorActionTokens.Identify(User()).Caller);

        return new OperatorActionResult(_catalog.Environment, DateTimeOffset.UtcNow, OperatorActionAccess.NameOf(OperatorAction.SkipDeferredTracking),
            endpoint, grant.EventId, "Accepted", CommandSent: false,
            auditRecorded
                ? "The tracking record is Skipped and the audit row was written. Nothing was removed from Service Bus."
                : "The tracking record is Skipped, but the audit row could not be written: tell the user, so the change can be recorded by hand. Nothing was removed from Service Bus.")
        {
            AuditRecorded = auditRecorded,
        };
    }

    /// <summary>Runs a prepared resubmit with an edited payload.</summary>
    [McpServerTool(Name = "nimbus_resubmit_with_changes", Title = "Resubmit a NimBus message with an edited payload", ReadOnly = false, Idempotent = true, Destructive = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Resubmits the message an actionToken from nimbus_prepare_action (action resubmit_with_changes) describes with the edited payload you previewed there, in place of its stored payload. Pass exactly the eventContent you prepared. The event type cannot be changed, and a payload that still holds masked placeholders is refused: every value must be real. Runs only while the message is still in the state you previewed. Requires the delegated nimbus.edit scope, the Contributor role and the PiiReader role; workloads can never run it. Confirm the changed fields with the user first. Returns an operationId: poll nimbus_get_operation to see the outcome.")]
    public Task<OperatorActionResult> ResubmitWithChangesAsync(
        [Description("actionToken from nimbus_prepare_action with action resubmit_with_changes.")] string actionToken,
        [Description("A new GUID for this request, recorded in the audit log.")] string idempotencyKey,
        [Description("Why the payload is edited, 1-1000 characters. Recorded in the audit log.")] string reason,
        [Description("The complete edited payload, exactly as passed to nimbus_prepare_action.")] string eventContent)
    {
        return ExecuteAsync(OperatorAction.ResubmitWithChanges, actionToken, idempotencyKey, reason, async (grant, target) =>
        {
            OperatorPayloadEdit.Parse(eventContent);
            if (!string.Equals(grant.ContentHash, OperatorPayloadEdit.Hash(eventContent), StringComparison.Ordinal))
                throw OperatorToolErrors.StaleMessage("eventContent is not the payload you previewed.");

            RequireRecoverableState(OperatorAction.ResubmitWithChanges, target.Row);
            var edit = await CheckPayloadEditAsync(target, eventContent).ConfigureAwait(false);
            return context => _commands.ResubmitWithChangesAsync(target, edit.EventTypeId, edit.Forwarded,
                DescribeEdit(target, context, edit, grant.ContentHash!), context);
        });
    }

    private async Task<PrepareActionResult> PrepareRecoveryAsync(OperatorAction kind, string endpoint, string id, OperatorCommandTarget target,
        string? eventContent, CancellationToken cancellationToken)
    {
        var name = OperatorActionAccess.NameOf(kind);
        RequireRecoverableState(kind, target.Row);
        if (!await _access.MayAsync(kind, endpoint).ConfigureAwait(false))
            throw OperatorToolErrors.PermissionDenied($"{name} requires the Contributor role on endpoint '{endpoint}'.");

        var affected = 1;
        var deferred = 0;
        string impact;
        string? contentHash = null;
        DeferredTrackingCheck? inspection = null;
        IReadOnlyList<string>? changedPaths = null;

        switch (kind)
        {
            case OperatorAction.SettleHandoff:
                impact = $"Completes or fails the pending handoff of message '{id}' on endpoint '{endpoint}' with the outcome you pass. The endpoint records it without running its handler again: completed, the message is done and later messages in its session continue; failed, it becomes Failed and blocks its session until it is resubmitted or skipped.";
                break;

            case OperatorAction.ReprocessDeferred:
                deferred = await RequireReprocessableSessionAsync(endpoint, target.Row.SessionId!).ConfigureAwait(false);
                affected = deferred;
                impact = $"Resets the blocked state of session '{target.Row.SessionId}' on endpoint '{endpoint}' and asks the endpoint to process its {deferred} deferred message(s) again, in order. Nothing is skipped or deleted.";
                break;

            case OperatorAction.SkipDeferredTracking:
                var inspected = await _recovery.InspectDeferredAsync(endpoint, id, cancellationToken).ConfigureAwait(false)
                    ?? throw OperatorToolErrors.MessageNotFound(endpoint, id);
                var check = inspected.Inspection;
                var locations = (check.BrokerChecks ?? [])
                    .Select(c => new DeferredBrokerLocation(c.Location, c.Status.ToString(), c.Scanned)).ToList();
                if (!check.CanSkip)
                {
                    throw OperatorToolErrors.ActionNotAllowed(
                        $"{check.SkipDetail} Broker checks: {string.Join("; ", locations.Select(l => $"{l.Location} {l.Status}"))}.");
                }

                inspection = new DeferredTrackingCheck(check.HistoryOutcome, check.HasLaterAttempt, check.SkipDetail, locations);
                impact = $"Marks only the tracking record of message '{id}' on endpoint '{endpoint}' as Skipped. No matching message was found in Service Bus; nothing is removed there, no session state changes, its history is kept, and work that is already running is not cancelled. The stored history's latest outcome for it is {check.HistoryOutcome}.";
                break;

            default:
                var parsed = OperatorPayloadEdit.Parse(eventContent);
                var edit = await CheckPayloadEditAsync(target, eventContent!).ConfigureAwait(false);
                changedPaths = OperatorPayloadEdit.ChangedPaths(edit.OriginalJson, parsed);
                if (changedPaths.Count == 0)
                    throw OperatorToolErrors.InvalidArgument("eventContent does not differ from the stored payload. Use resubmit to replay it unchanged.");
                contentHash = OperatorPayloadEdit.Hash(eventContent!);
                impact = $"Sends message '{id}' to endpoint '{endpoint}' again with your edited payload in place of the stored one ({changedPaths.Count} path(s) differ; see changedPaths). The event type stays '{edit.EventTypeId}'. The stored payload is not kept as the latest: the endpoint processes what you send.";
                break;
        }

        var (caller, tenant, client) = OperatorActionTokens.Identify(User());
        var token = _tokens.Issue(new OperatorActionGrant(
            name, endpoint, id, target.Version.Encode(), caller, tenant, client, _catalog.Environment, ContentHash: contentHash));

        return new PrepareActionResult(
            _catalog.Environment,
            DateTimeOffset.UtcNow,
            name,
            endpoint,
            id,
            target.Row.SessionId,
            target.Row.ResolutionStatus.ToString(),
            target.Version.Encode(),
            affected,
            deferred,
            impact,
            token,
            (int)OperatorActionTokens.Lifetime.TotalSeconds)
        {
            // These never run unattended: a person is present and says yes.
            NextActions = OperatorNextActions.ForPrepared(name, token, isWorkload: false),
            Inspection = inspection,
            ChangedPaths = changedPaths,
        };
    }

    /// <summary>
    /// The shared first half of a recovery tool that is not journaled: the scope, the reason and
    /// key, the token and who it was issued to, and the message still at the previewed version.
    /// </summary>
    private async Task<(OperatorActionGrant Grant, string Endpoint, OperatorCommandTarget Target, OperatorCommandContext Context)> OpenRecoveryAsync(
        OperatorAction kind, string actionToken, string idempotencyKey, string reason)
    {
        await _access.RequireScopeAsync(kind).ConfigureAwait(false);
        var context = Context(reason, idempotencyKey);
        var (caller, tenant, client) = OperatorActionTokens.Identify(User());
        var grant = ReadGrant(kind, actionToken, caller, tenant, client);
        var (endpoint, target) = await ResolveGrantTargetAsync(kind, grant).ConfigureAwait(false);
        return (grant, endpoint, target, context);
    }

    /// <summary>Throws <c>[ActionNotAllowed]</c> unless <paramref name="row"/> is in the state <paramref name="kind"/> acts on.</summary>
    private static void RequireRecoverableState(OperatorAction kind, UnresolvedEvent row)
    {
        var allowed = kind switch
        {
            OperatorAction.SettleHandoff => OperatorRecoveryCommands.IsPendingHandoff(row),
            OperatorAction.ReprocessDeferred or OperatorAction.SkipDeferredTracking => row.ResolutionStatus == ResolutionStatus.Deferred,
            _ => OperatorCommandCoordinator.IsActionable(row, OperatorChannel.Mcp),
        };
        if (allowed)
            return;

        throw OperatorToolErrors.ActionNotAllowed(kind switch
        {
            OperatorAction.SettleHandoff => $"A {row.ResolutionStatus} message is not a pending handoff, so there is nothing to settle.",
            OperatorAction.ReprocessDeferred or OperatorAction.SkipDeferredTracking =>
                $"{OperatorActionAccess.NameOf(kind)} acts on a Deferred message; this one is {row.ResolutionStatus}.",
            _ => $"A {row.ResolutionStatus} message cannot be resubmitted with changes through MCP.",
        });
    }

    /// <summary>
    /// The session's deferred count, when it has deferred messages, no failed message still
    /// blocks it and the count is within the server's bound. Reprocess resets the block, so
    /// running it past a failure would let later events overtake the failed one.
    /// </summary>
    private async Task<int> RequireReprocessableSessionAsync(string endpoint, string sessionId)
    {
        var state = await _store.DownloadEndpointSessionStateCount(endpoint, sessionId).ConfigureAwait(false);
        var deferred = state?.DeferredEvents?.Count() ?? 0;
        if (deferred == 0)
            throw OperatorToolErrors.ActionNotAllowed($"Session '{sessionId}' has no deferred messages to reprocess.");

        var max = Math.Max(1, _runtime.Options.MaxDeferredReprocess);
        if (deferred > max)
        {
            throw OperatorToolErrors.ActionNotAllowed(
                $"Session '{sessionId}' has {deferred} deferred messages, more than the {max} one reprocess may request over MCP. Reprocess it from the Web UI.");
        }

        var blockers = await _store.GetEventsByFilter(
            new EventFilter { EndPointId = endpoint, SessionId = sessionId, ResolutionStatus = [ResolutionStatus.Failed.ToString()] },
            null!, 1).ConfigureAwait(false);
        if (blockers?.Events?.FirstOrDefault() is { } blocker)
        {
            throw OperatorToolErrors.ActionNotAllowed(
                $"Session '{sessionId}' is still blocked by failed message '{blocker.EventId}'. Resubmit or skip that message: its session's deferred messages are released when it resolves.");
        }

        return deferred;
    }

    /// <summary>
    /// What an edited payload must pass before it is previewed and again before it is sent:
    /// PiiReader, an event type the server can resolve, and no masked placeholder left in it. The
    /// placeholder check runs for every caller: a PiiReader without the payload scope reads masked
    /// values over MCP and must not write them back as data.
    /// </summary>
    private async Task<PayloadEdit> CheckPayloadEditAsync(OperatorCommandTarget target, string eventContent)
    {
        if (!await _authorization.CanReadPiiAsync().ConfigureAwait(false))
        {
            throw OperatorToolErrors.PermissionDenied(
                "resubmit_with_changes requires the PiiReader role: an edited payload replaces every value, including the sensitive ones.");
        }

        var eventTypeId = await _commands.ResolveEventTypeIdAsync(target).ConfigureAwait(false);
        if (string.IsNullOrEmpty(eventTypeId))
        {
            throw OperatorToolErrors.ActionNotAllowed(
                $"The event type of message '{target.EventId}' could not be resolved, so the edited payload cannot be checked for masked values. Edit it in the Web UI.");
        }

        if (_masker.ContainsRedactPlaceholder(eventTypeId, eventContent))
        {
            throw OperatorToolErrors.InvalidArgument(
                "eventContent still holds masked placeholders. Read the payload with nimbus_get_message includePayload (it needs the nimbus.payload.read scope) and pass real values for every field.");
        }

        var original = await OperatorCommandCoordinator.StoredPayloadAsync(_store, target).ConfigureAwait(false);
        return new PayloadEdit(eventTypeId, _masker.StripMaskedMarker(eventContent), original);
    }

    // The audit row names what changed and binds the payload by hash; it never stores the
    // edited values, which may be personal data.
    private static string DescribeEdit(OperatorCommandTarget target, OperatorCommandContext context, PayloadEdit edit, string contentHash) =>
        JsonConvert.SerializeObject(new
        {
            channel = context.Channel.ToString(),
            reason = context.Reason,
            idempotencyKey = context.IdempotencyKey,
            clientId = context.ClientId,
            operationId = context.Journal?.OperationId,
            priorStatus = target.Row.ResolutionStatus.ToString(),
            messageId = target.Version.LastMessageId,
            messageVersion = target.Version.Encode(),
            eventTypeId = edit.EventTypeId,
            contentHash,
            changedPaths = OperatorPayloadEdit.ChangedPaths(edit.OriginalJson, OperatorPayloadEdit.Parse(edit.Forwarded)),
        });

    private sealed record PayloadEdit(string EventTypeId, string Forwarded, string? OriginalJson);
}
