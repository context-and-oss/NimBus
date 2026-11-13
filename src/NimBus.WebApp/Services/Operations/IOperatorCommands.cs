using System;
using System.Threading.Tasks;
using NimBus.MessageStore;

namespace NimBus.WebApp.Services.Operations;

/// <summary>
/// The one path for operator commands that change a tracked message, shared by the Web UI's
/// REST API and the operator MCP server (Spec 035 Phase 2a). Every command re-checks the
/// caller's role against fresh access-control lists, claims the row atomically at the version
/// it was decided against, records an audit row, and only then sends anything. Two callers
/// acting on the same version can therefore never both send a command.
/// </summary>
public interface IOperatorCommands
{
    /// <summary>
    /// Finds the row a REST caller means by the message id it loaded, which is the row's latest
    /// message. A message that is no longer the row's latest attempt, or whose row is gone, is
    /// <see cref="OperatorCommandStatus.Stale"/>.
    /// </summary>
    Task<OperatorTargetLookup> FindByMessageAsync(string eventId, string messageId);

    /// <summary>
    /// Finds the visible row of <paramref name="eventId"/> on <paramref name="endpointId"/> and its
    /// latest message, at the row's current version. Used by callers that address a message by
    /// endpoint and event id and carry the version they decided on separately (the MCP tools).
    /// </summary>
    Task<OperatorTargetLookup> FindCurrentAsync(string endpointId, string eventId);

    /// <summary>
    /// Resolves the event type of <paramref name="target"/> from stored messages only, never from
    /// a request body.
    /// </summary>
    Task<string> ResolveEventTypeIdAsync(OperatorCommandTarget target);

    /// <summary>Replays the target's latest stored payload to its endpoint.</summary>
    Task<OperatorCommandResult> ResubmitAsync(OperatorCommandTarget target, OperatorCommandContext context);

    /// <summary>
    /// Replays an operator-edited payload. The caller has already validated the payload;
    /// <paramref name="auditData"/> is recorded as the audit row's data.
    /// </summary>
    Task<OperatorCommandResult> ResubmitWithChangesAsync(OperatorCommandTarget target, string eventTypeId,
        string eventJson, string auditData, OperatorCommandContext context);

    /// <summary>Skips the target, which can release later messages in its session.</summary>
    Task<OperatorCommandResult> SkipAsync(OperatorCommandTarget target, OperatorCommandContext context);

    /// <summary>
    /// Sets or clears the durable "reported" marker of an event. A marker is an annotation, not
    /// a state change, so it has no version guard: the last writer wins.
    /// </summary>
    Task<OperatorCommandResult> SetReportedAsync(string endpointId, string eventId, bool reported,
        string? ticketId, OperatorCommandContext context);
}

/// <summary>Where an operator command came from.</summary>
public enum OperatorChannel
{
    /// <summary>The management Web UI's REST API.</summary>
    WebApp,

    /// <summary>The operator MCP server.</summary>
    Mcp,
}

/// <summary>Outcome of an operator command or target lookup.</summary>
public enum OperatorCommandStatus
{
    /// <summary>The command was audited and sent, or the target was found.</summary>
    Accepted,

    /// <summary>No such message or endpoint.</summary>
    NotFound,

    /// <summary>The message changed since the caller looked at it; nothing was sent.</summary>
    Stale,

    /// <summary>The message's current state does not allow this command.</summary>
    NotAllowed,

    /// <summary>The caller lacks the role; the denial was audited.</summary>
    Forbidden,

    /// <summary>An argument was rejected; see the detail.</summary>
    Invalid,

    /// <summary>The audit row could not be written, so nothing was sent.</summary>
    AuditUnavailable,
}

/// <summary>Who issued a command, and why.</summary>
/// <param name="Channel">The API the command arrived through.</param>
/// <param name="Reason">The operator's reason, when the channel asks for one.</param>
/// <param name="IdempotencyKey">The caller's idempotency key, recorded for correlation.</param>
/// <param name="ClientId">The OAuth client application, when known.</param>
/// <param name="Journal">
/// The operation journal entry the command reports its progress to (Spec 044 Phase 2), or null
/// for an unjournaled command (the Web UI, or a deployment without a journal store).
/// </param>
public sealed record OperatorCommandContext(
    OperatorChannel Channel,
    string? Reason = null,
    string? IdempotencyKey = null,
    string? ClientId = null,
    IOperatorCommandJournal? Journal = null);

/// <summary>
/// Progress reports from <see cref="IOperatorCommands"/> to a journaled operation. The
/// coordinator calls them in order; a journal that cannot record a step throws, and the
/// command stops before the step it guards.
/// </summary>
public interface IOperatorCommandJournal
{
    /// <summary>The Service Bus message id the command is published under: the operation id.</summary>
    string CommandMessageId { get; }

    /// <summary>The operation id, for the audit row.</summary>
    string OperationId { get; }

    /// <summary>The row was claimed; nothing is published yet.</summary>
    Task ClaimedAsync();

    /// <summary>Publishing starts now. Called before the send, so a crash after it is never mistaken for "not sent".</summary>
    Task PublishingAsync();

    /// <summary>The broker accepted the command.</summary>
    Task DispatchedAsync();

    /// <summary>The command stopped before anything was published; the claim, if any, was released.</summary>
    /// <param name="status">Why.</param>
    /// <param name="detail">A description.</param>
    Task NotRunAsync(OperatorCommandStatus status, string? detail);

    /// <summary>The send threw: the command may or may not have reached Service Bus.</summary>
    /// <param name="exception">The send's exception.</param>
    Task PublishFailedAsync(Exception exception);
}

/// <summary>A message an operator command acts on, at the version it was found.</summary>
/// <param name="EndpointId">The endpoint the row belongs to and commands are sent to.</param>
/// <param name="Row">The tracked row as it was read.</param>
/// <param name="Message">The row's latest message, which commands are parented to.</param>
/// <param name="Version">The version the command must still match.</param>
public sealed record OperatorCommandTarget(
    string EndpointId,
    UnresolvedEvent Row,
    MessageEntity Message,
    OperatorMessageVersion Version)
{
    /// <summary>The event id.</summary>
    public string EventId => Row.EventId;
}

/// <summary>The result of finding a target.</summary>
/// <param name="Status"><see cref="OperatorCommandStatus.Accepted"/> when found.</param>
/// <param name="Target">The target, when found.</param>
public sealed record OperatorTargetLookup(OperatorCommandStatus Status, OperatorCommandTarget? Target = null);

/// <summary>The result of an operator command.</summary>
/// <param name="Status">What happened.</param>
/// <param name="Detail">A message for the caller, when the status needs one.</param>
public sealed record OperatorCommandResult(OperatorCommandStatus Status, string? Detail = null)
{
    /// <summary>The command was audited and sent.</summary>
    public static readonly OperatorCommandResult Accepted = new(OperatorCommandStatus.Accepted);
}
