using System.ComponentModel;
using ModelContextProtocol.Server;
using NimBus.MessageStore.Abstractions;
using NimBus.WebApp.Mcp.Operations;
using NimBus.WebApp.Services.Operations;

namespace NimBus.WebApp.Mcp.Tools;

/// <summary>
/// Spec 035 Phase 2a tools that change a message, for interactive (delegated) callers only.
/// Resubmit and skip take two steps: <c>nimbus_prepare_action</c> previews the action and
/// returns a short-lived token bound to the caller and the message's version, and the execute
/// tool runs it through the same guarded path as the Web UI. Every change re-checks the
/// caller's role against fresh access-control lists and is audited before anything is sent.
/// The agent verifies the outcome by reading the message again.
/// </summary>
[McpServerToolType]
public sealed class OperatorActionTools
{
    /// <summary>Longest reason accepted.</summary>
    public const int MaxReasonLength = 1000;

    private readonly OperatorEndpointCatalog _catalog;
    private readonly IOperatorCommands _commands;
    private readonly OperatorActionAccess _access;
    private readonly OperatorActionTokens _tokens;
    private readonly OperatorMutationLimiter _limiter;
    private readonly OperatorQueries _queries;
    private readonly IOperatorClassificationSource _classifications;
    private readonly IMessageTrackingStore _store;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly ILogger<OperatorActionTools> _logger;

    /// <summary>Creates the tools for one request.</summary>
    public OperatorActionTools(
        OperatorEndpointCatalog catalog,
        IOperatorCommands commands,
        OperatorActionAccess access,
        OperatorActionTokens tokens,
        OperatorMutationLimiter limiter,
        OperatorQueries queries,
        IOperatorClassificationSource classifications,
        IMessageTrackingStore store,
        IHttpContextAccessor httpContextAccessor,
        ILogger<OperatorActionTools> logger)
    {
        _catalog = catalog;
        _commands = commands;
        _access = access;
        _tokens = tokens;
        _limiter = limiter;
        _queries = queries;
        _classifications = classifications;
        _store = store;
        _httpContextAccessor = httpContextAccessor;
        _logger = logger;
    }

    /// <summary>Previews a resubmit or skip and returns a token to run it.</summary>
    [McpServerTool(Name = "nimbus_prepare_action", Title = "Prepare a NimBus recovery action", ReadOnly = true, Idempotent = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Checks and previews resubmitting or skipping one failed message without changing anything. Pass the messageVersion from nimbus_get_message. Returns the impact and an actionToken, valid for about two minutes and only for you, to pass to nimbus_resubmit_message or nimbus_skip_message. Show the preview to the user and get their confirmation before running a skip.")]
    public async Task<PrepareActionResult> PrepareActionAsync(
        [Description("resubmit or skip.")] string action,
        [Description("Endpoint id, as returned by nimbus_list_endpoints.")] string endpointId,
        [Description("The message's event id.")] string eventId,
        [Description("messageVersion from nimbus_get_message: the state you decided on.")] string messageVersion)
    {
        var kind = ParseCommand(action);
        await _access.RequireScopeAsync(kind).ConfigureAwait(false);
        var endpoint = await _catalog.RequireReadableAsync(endpointId).ConfigureAwait(false);
        await _access.RequireChangeAllowedOnAsync(kind, endpoint).ConfigureAwait(false);
        var id = RequireEventId(eventId);
        if (!OperatorMessageVersion.TryDecode(messageVersion, out var expected))
            throw OperatorToolErrors.InvalidArgument("messageVersion is not one nimbus_get_message returned.");

        var target = await FindAsync(endpoint, id).ConfigureAwait(false);
        if (target.Version != expected)
            throw OperatorToolErrors.StaleMessage($"Message '{id}' changed since you read it.");
        if (!OperatorCommandCoordinator.IsActionable(target.Row, OperatorChannel.Mcp))
            throw OperatorToolErrors.ActionNotAllowed($"A {target.Row.ResolutionStatus} message cannot be {Past(kind)} through MCP.");
        if (!await _access.MayAsync(kind, endpoint).ConfigureAwait(false))
            throw OperatorToolErrors.PermissionDenied($"{OperatorActionAccess.NameOf(kind)} requires the Contributor role on endpoint '{endpoint}'.");

        var deferred = 0;
        if (!string.IsNullOrEmpty(target.Row.SessionId))
        {
            var session = await _queries.GetSessionAsync(endpoint, target.Row.SessionId).ConfigureAwait(false);
            deferred = session.DeferredEvents?.Count ?? 0;
        }

        var (caller, tenant, client) = OperatorActionTokens.Identify(User());
        var token = _tokens.Issue(new OperatorActionGrant(
            OperatorActionAccess.NameOf(kind), endpoint, id, target.Version.Encode(), caller, tenant, client, _catalog.Environment));

        var impact = kind == OperatorAction.Skip
            ? $"Skips message '{id}' without processing it. Its history is kept. {Waiting(deferred)}"
            : $"Sends message '{id}' to endpoint '{endpoint}' again with its latest stored payload. {Waiting(deferred)}";

        return new PrepareActionResult(
            _catalog.Environment,
            DateTimeOffset.UtcNow,
            OperatorActionAccess.NameOf(kind),
            endpoint,
            id,
            target.Row.SessionId,
            target.Row.ResolutionStatus.ToString(),
            target.Version.Encode(),
            1,
            deferred,
            impact,
            token,
            (int)OperatorActionTokens.Lifetime.TotalSeconds);
    }

    /// <summary>Runs a prepared resubmit.</summary>
    [McpServerTool(Name = "nimbus_resubmit_message", Title = "Resubmit a NimBus message", ReadOnly = false, Idempotent = false, Destructive = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Resubmits the message an actionToken from nimbus_prepare_action describes, replaying its latest stored payload to its endpoint. Runs only while the message is still in the state you previewed. Requires the nimbus.resubmit scope and the Contributor role. The result says the command was sent, not that it succeeded: read the message again to see the outcome.")]
    public Task<OperatorActionResult> ResubmitMessageAsync(
        [Description("actionToken from nimbus_prepare_action with action resubmit.")] string actionToken,
        [Description("A new GUID for this request, recorded in the audit log.")] string idempotencyKey,
        [Description("Why you are resubmitting, 1-1000 characters. Recorded in the audit log.")] string reason)
        => ExecuteAsync(OperatorAction.Resubmit, actionToken, idempotencyKey, reason);

    /// <summary>Runs a prepared skip.</summary>
    [McpServerTool(Name = "nimbus_skip_message", Title = "Skip a NimBus message", ReadOnly = false, Idempotent = false, Destructive = true, OpenWorld = true, UseStructuredContent = true)]
    [Description("Skips the message an actionToken from nimbus_prepare_action describes: it is never processed, and later messages waiting in its session may be released. Runs only while the message is still in the state you previewed. Requires the nimbus.skip scope and the Contributor role. Confirm with the user first. Read the message again to see the outcome.")]
    public Task<OperatorActionResult> SkipMessageAsync(
        [Description("actionToken from nimbus_prepare_action with action skip.")] string actionToken,
        [Description("A new GUID for this request, recorded in the audit log.")] string idempotencyKey,
        [Description("Why you are skipping, 1-1000 characters. Recorded in the audit log.")] string reason)
        => ExecuteAsync(OperatorAction.Skip, actionToken, idempotencyKey, reason);

    /// <summary>Sets or clears the "reported" marker.</summary>
    [McpServerTool(Name = "nimbus_set_message_reported", Title = "Mark a NimBus message reported", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = false, UseStructuredContent = true)]
    [Description("Marks a message as reported (for example, a ticket was raised) or clears the marker. It changes nothing on the bus. Requires the nimbus.annotate scope and the Contributor role.")]
    public async Task<OperatorActionResult> SetMessageReportedAsync(
        [Description("Endpoint id, as returned by nimbus_list_endpoints.")] string endpointId,
        [Description("The message's event id.")] string eventId,
        [Description("true to mark it reported, false to clear the marker.")] bool reported,
        [Description("Why, 1-1000 characters. Recorded in the audit log.")] string reason,
        [Description("A new GUID for this request, recorded in the audit log.")] string idempotencyKey,
        [Description("External ticket id: letters, digits, '.', '_' and '-', at most 64 characters. Ignored when clearing.")] string? ticketId = null)
    {
        await _access.RequireScopeAsync(OperatorAction.Report).ConfigureAwait(false);
        var endpoint = await _catalog.RequireReadableAsync(endpointId).ConfigureAwait(false);
        await _access.RequireChangeAllowedOnAsync(OperatorAction.Report, endpoint).ConfigureAwait(false);
        var id = RequireEventId(eventId);
        var context = Context(reason, idempotencyKey);
        _limiter.Acquire(Http());

        var result = await _commands.SetReportedAsync(endpoint, id, reported, ticketId, context).ConfigureAwait(false);
        ThrowUnlessAccepted(result, OperatorAction.Report, endpoint, id);
        return Accepted(OperatorAction.Report, endpoint, id, commandSent: false);
    }

    /// <summary>Requests an AI classification of a failure.</summary>
    [McpServerTool(Name = "nimbus_classify_failure", Title = "Classify a NimBus failure", ReadOnly = false, Idempotent = true, Destructive = false, OpenWorld = true, UseStructuredContent = true)]
    [Description("Requests an AI classification of a failed message's error, or returns the stored one. It costs a provider call unless a classification already exists, and it never resubmits or skips anything. The result is advisory only. Requires the nimbus.classify scope, the Contributor role and classification enabled in this deployment.")]
    public async Task<ClassificationResult> ClassifyFailureAsync(
        [Description("Endpoint id, as returned by nimbus_list_endpoints.")] string endpointId,
        [Description("The message's event id.")] string eventId,
        [Description("A new GUID for this request. Repeating it returns the same result.")] string idempotencyKey,
        [Description("The failed attempt's message id. Defaults to the latest attempt.")] string? messageId = null,
        [Description("Classify again even when a classification exists. Default false.")] bool force = false,
        CancellationToken cancellationToken = default)
    {
        await _access.RequireScopeAsync(OperatorAction.Classify).ConfigureAwait(false);
        if (!_classifications.IsAvailable)
            throw OperatorToolErrors.FeatureUnavailable("AI failure classification is not enabled in this deployment.");
        if (!_classifications.CanAnalyze)
            throw OperatorToolErrors.FeatureUnavailable("AI failure classification can't start a new analysis in this deployment: no provider is configured. Saved classifications can still be read.");

        var endpoint = await _catalog.RequireReadableAsync(endpointId).ConfigureAwait(false);
        await _access.RequireChangeAllowedOnAsync(OperatorAction.Classify, endpoint).ConfigureAwait(false);
        if (!await _access.MayFreshAsync(OperatorAction.Classify, endpoint).ConfigureAwait(false))
            throw OperatorToolErrors.PermissionDenied($"Classifying a failure requires the Contributor role on endpoint '{endpoint}'.");
        var id = RequireEventId(eventId);
        var key = RequireGuid(idempotencyKey);
        var attempt = string.IsNullOrWhiteSpace(messageId)
            ? (await FindAsync(endpoint, id).ConfigureAwait(false)).Row.LastMessageId
            : messageId.Trim();
        if (string.IsNullOrEmpty(attempt))
            throw OperatorToolErrors.MessageNotFound(endpoint, id);

        var failedMessage = await _store.GetMessage(id, attempt).ConfigureAwait(false);
        if (!string.Equals(failedMessage?.EndpointId, endpoint, StringComparison.OrdinalIgnoreCase))
            throw OperatorToolErrors.MessageNotFound(endpoint, id);

        _limiter.Acquire(Http());
        var (classification, _) = await _classifications.AnalyzeAsync(endpoint, id, attempt, key, force, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(classification.EndpointId, endpoint, StringComparison.OrdinalIgnoreCase))
            throw OperatorToolErrors.MessageNotFound(endpoint, id);

        return new ClassificationResult(_catalog.Environment, DateTimeOffset.UtcNow, endpoint, id, attempt, true, true,
            ClassificationInfo.From(classification));
    }

    private async Task<OperatorActionResult> ExecuteAsync(OperatorAction kind, string actionToken, string idempotencyKey, string reason)
    {
        await _access.RequireScopeAsync(kind).ConfigureAwait(false);
        var context = Context(reason, idempotencyKey);
        if (!_tokens.TryRead(actionToken, out var grant) || grant is null)
            throw OperatorToolErrors.StaleMessage("The actionToken is expired or invalid.");

        var (caller, tenant, client) = OperatorActionTokens.Identify(User());
        if (!string.Equals(grant.Action, OperatorActionAccess.NameOf(kind), StringComparison.Ordinal)
            || !string.Equals(grant.Caller, caller, StringComparison.Ordinal)
            || !string.Equals(grant.TenantId, tenant, StringComparison.Ordinal)
            || !string.Equals(grant.ClientId, client, StringComparison.Ordinal)
            || !string.Equals(grant.Environment, _catalog.Environment, StringComparison.Ordinal)
            || !OperatorMessageVersion.TryDecode(grant.MessageVersion, out var expected))
        {
            throw OperatorToolErrors.StaleMessage($"The actionToken was not issued to you for {OperatorActionAccess.NameOf(kind)}.");
        }

        var endpoint = await _catalog.RequireReadableAsync(grant.EndpointId).ConfigureAwait(false);
        await _access.RequireChangeAllowedOnAsync(kind, endpoint).ConfigureAwait(false);
        _limiter.Acquire(Http());

        var lookup = await _commands.FindCurrentAsync(endpoint, grant.EventId).ConfigureAwait(false);
        if (lookup.Target is not { } target || target.Version != expected)
            throw OperatorToolErrors.StaleMessage($"Message '{grant.EventId}' changed since you prepared the action.");

        OperatorCommandResult result;
        try
        {
            result = kind == OperatorAction.Skip
                ? await _commands.SkipAsync(target, context).ConfigureAwait(false)
                : await _commands.ResubmitAsync(target, context).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not ModelContextProtocol.McpException)
        {
            _logger.LogError(ex, "MCP {Action} of {EndpointId}/{EventId} failed while sending", kind, endpoint, grant.EventId);
            throw OperatorToolErrors.OutcomeUnknown("Sending the command failed and the message was restored, but the send may have reached Service Bus.");
        }

        ThrowUnlessAccepted(result, kind, endpoint, grant.EventId);
        _logger.LogInformation("MCP {Action} of {EndpointId}/{EventId} accepted for {Caller}", kind, endpoint, grant.EventId, caller);
        return Accepted(kind, endpoint, grant.EventId, commandSent: true);
    }

    private async Task<OperatorCommandTarget> FindAsync(string endpoint, string eventId)
    {
        var lookup = await _commands.FindCurrentAsync(endpoint, eventId).ConfigureAwait(false);
        return lookup.Status switch
        {
            OperatorCommandStatus.Accepted => lookup.Target!,
            OperatorCommandStatus.NotAllowed => throw OperatorToolErrors.ActionNotAllowed(
                $"Commands for message '{eventId}' go to another endpoint than '{endpoint}'."),
            _ => throw OperatorToolErrors.MessageNotFound(endpoint, eventId),
        };
    }

    private static void ThrowUnlessAccepted(OperatorCommandResult result, OperatorAction kind, string endpoint, string eventId)
    {
        switch (result.Status)
        {
            case OperatorCommandStatus.Accepted:
                return;
            case OperatorCommandStatus.Stale:
                throw OperatorToolErrors.StaleMessage($"Message '{eventId}' changed since you prepared the action.");
            case OperatorCommandStatus.NotAllowed:
                throw OperatorToolErrors.ActionNotAllowed(result.Detail ?? $"Message '{eventId}' cannot be {Past(kind)} in its current state.");
            case OperatorCommandStatus.Forbidden:
                throw OperatorToolErrors.PermissionDenied($"{OperatorActionAccess.NameOf(kind)} requires the Contributor role on endpoint '{endpoint}'.");
            case OperatorCommandStatus.AuditUnavailable:
                throw OperatorToolErrors.AuditUnavailable();
            case OperatorCommandStatus.Invalid:
                throw OperatorToolErrors.InvalidArgument(result.Detail ?? "The request was rejected.");
            default:
                throw OperatorToolErrors.MessageNotFound(endpoint, eventId);
        }
    }

    private OperatorActionResult Accepted(OperatorAction kind, string endpoint, string eventId, bool commandSent) =>
        new(_catalog.Environment, DateTimeOffset.UtcNow, OperatorActionAccess.NameOf(kind), endpoint, eventId, "Accepted", commandSent,
            commandSent
                ? "The command was audited and sent. Read the message again with nimbus_get_message to see the outcome."
                : "The change was audited and saved.");

    private OperatorCommandContext Context(string reason, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(reason) || reason.Trim().Length > MaxReasonLength)
            throw OperatorToolErrors.InvalidArgument($"reason is required, 1-{MaxReasonLength} characters.");

        var (_, _, client) = OperatorActionTokens.Identify(User());
        return new OperatorCommandContext(OperatorChannel.Mcp, reason.Trim(), RequireGuid(idempotencyKey), client);
    }

    private static OperatorAction ParseCommand(string action) => action?.Trim().ToLowerInvariant() switch
    {
        "resubmit" => OperatorAction.Resubmit,
        "skip" => OperatorAction.Skip,
        _ => throw OperatorToolErrors.InvalidArgument("action must be resubmit or skip."),
    };

    private static string RequireEventId(string eventId) => string.IsNullOrWhiteSpace(eventId)
        ? throw OperatorToolErrors.InvalidArgument("eventId is required.")
        : eventId.Trim();

    private static string RequireGuid(string idempotencyKey) => Guid.TryParse(idempotencyKey, out var key)
        ? key.ToString("D")
        : throw OperatorToolErrors.InvalidArgument("idempotencyKey must be a GUID.");

    private static string Past(OperatorAction kind) => kind == OperatorAction.Skip ? "skipped" : "resubmitted";

    private static string Waiting(int deferred) => deferred switch
    {
        0 => "No later messages are waiting in its session.",
        1 => "1 later message is waiting in its session and may be released.",
        _ => $"{deferred} later messages are waiting in its session and may be released.",
    };

    private HttpContext Http() => _httpContextAccessor.HttpContext
        ?? throw new InvalidOperationException("Operator tools run inside an HTTP request.");

    private System.Security.Claims.ClaimsPrincipal User() => Http().User;
}

/// <summary>Result of <c>nimbus_prepare_action</c>.</summary>
/// <param name="Environment">The configured NimBus environment name.</param>
/// <param name="AsOfUtc">When the result was produced.</param>
/// <param name="Action">resubmit or skip.</param>
/// <param name="EndpointId">The endpoint.</param>
/// <param name="EventId">The message's event id.</param>
/// <param name="SessionId">The message's session.</param>
/// <param name="ExpectedStatus">The status the action runs against.</param>
/// <param name="MessageVersion">The version the action runs against.</param>
/// <param name="AffectedMessages">Messages the command is sent for: always 1.</param>
/// <param name="DeferredInSession">Later messages waiting in the session, which the action may release.</param>
/// <param name="Impact">What running the action does, for the user to confirm.</param>
/// <param name="ActionToken">Pass to nimbus_resubmit_message or nimbus_skip_message. Opaque, single caller.</param>
/// <param name="ExpiresInSeconds">How long the token stays valid.</param>
public sealed record PrepareActionResult(
    string? Environment, DateTimeOffset AsOfUtc, string Action, string EndpointId, string EventId, string? SessionId,
    string ExpectedStatus, string MessageVersion, int AffectedMessages, int DeferredInSession, string Impact,
    string ActionToken, int ExpiresInSeconds);

/// <summary>Result of an operator MCP tool that changes a message.</summary>
/// <param name="Environment">The configured NimBus environment name.</param>
/// <param name="AsOfUtc">When the result was produced.</param>
/// <param name="Action">The action taken.</param>
/// <param name="EndpointId">The endpoint.</param>
/// <param name="EventId">The message's event id.</param>
/// <param name="Status">Always <c>Accepted</c>; refusals are tool errors.</param>
/// <param name="CommandSent">Whether a command was published to Service Bus.</param>
/// <param name="Next">What to do next.</param>
public sealed record OperatorActionResult(
    string? Environment, DateTimeOffset AsOfUtc, string Action, string EndpointId, string EventId, string Status,
    bool CommandSent, string Next);
