using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace NimBus.MessageStore.States;

/// <summary>
/// One journaled operator command (Spec 044 Phase 2, from Spec 035 Phase 2b): who asked for
/// what, how far it got, and what the Resolver's history says happened. The operation id is
/// derived from the tenant, the caller and the caller's idempotency key, so a retry with the
/// same key finds the same operation.
/// </summary>
public sealed class OperatorOperation
{
    /// <summary>The operation id, 32 lowercase hex characters; also the command's Service Bus message id.</summary>
    [JsonProperty("id")]
    public string OperationId { get; set; } = string.Empty;

    /// <summary>The caller's tenant (<c>tid</c>), or empty in local development.</summary>
    public string TenantId { get; set; } = string.Empty;

    /// <summary>The caller (<c>oid</c>, else <c>sub</c>, else the name).</summary>
    public string Caller { get; set; } = string.Empty;

    /// <summary>The client application (<c>azp</c>, else <c>appid</c>), when known.</summary>
    public string? ClientId { get; set; }

    /// <summary>Whether the caller is an app-only (workload) token.</summary>
    public bool IsWorkload { get; set; }

    /// <summary>The caller's display name, as audited.</summary>
    public string? ActorName { get; set; }

    /// <summary>The caller's idempotency key.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    /// <summary>SHA-256 over the action, endpoint, event, message version and reason; a retry must match it.</summary>
    public string ArgumentsHash { get; set; } = string.Empty;

    /// <summary>The command.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public OperatorOperationAction Action { get; set; }

    /// <summary>The endpoint the command acts on.</summary>
    public string EndpointId { get; set; } = string.Empty;

    /// <summary>The event the command acts on.</summary>
    public string EventId { get; set; } = string.Empty;

    /// <summary>The event's session.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>The failed attempt the command resolves (the row's latest message id when it was prepared).</summary>
    public string SourceMessageId { get; set; } = string.Empty;

    /// <summary>The message version the caller previewed.</summary>
    public string MessageVersion { get; set; } = string.Empty;

    /// <summary>The audited reason.</summary>
    public string Reason { get; set; } = string.Empty;

    /// <summary>How far the operation got.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public OperatorOperationStatus Status { get; set; }

    /// <summary>The response type the Resolver recorded for the command, once observed.</summary>
    public string? ObservedStatus { get; set; }

    /// <summary>The message id of that response.</summary>
    public string? EvidenceMessageId { get; set; }

    /// <summary>When the response was observed.</summary>
    public DateTime? ObservedAtUtc { get; set; }

    /// <summary>Why the operation stopped: the refusal or error, when there is one.</summary>
    public string? Detail { get; set; }

    /// <summary>The reconciler instance holding the lease, if any.</summary>
    public string? LeaseOwner { get; set; }

    /// <summary>Until when the lease (or the in-request execution) holds the operation.</summary>
    public DateTime? LeaseUntilUtc { get; set; }

    /// <summary>When the command was published, once known.</summary>
    public DateTime? DispatchedAtUtc { get; set; }

    /// <summary>When the operation was created.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>When the operation last changed.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>When the operation may be purged.</summary>
    public DateTime ExpiresAtUtc { get; set; }

    /// <summary>Item time-to-live in seconds for stores that expire items themselves (Cosmos).</summary>
    [JsonProperty("ttl", NullValueHandling = NullValueHandling.Ignore)]
    public int? TimeToLiveSeconds { get; set; }

    /// <summary>
    /// Opaque concurrency token set by the store on every write. Cosmos fills it from the
    /// document's <c>_etag</c>; it is never written into a document.
    /// </summary>
    [JsonProperty("_etag")]
    public string Version { get; set; } = string.Empty;

    /// <summary>Keeps <see cref="Version"/> out of serialized documents (Newtonsoft conditional serialization).</summary>
    public bool ShouldSerializeVersion() => false;

    /// <summary>Whether the operation has reached a status that no longer changes.</summary>
    [JsonIgnore]
    public bool IsTerminal => Status is OperatorOperationStatus.Succeeded
        or OperatorOperationStatus.Failed
        or OperatorOperationStatus.NotSent
        or OperatorOperationStatus.Refused;

    /// <summary>A copy, so a caller's changes never reach a stored instance by reference.</summary>
    public OperatorOperation Clone() => (OperatorOperation)MemberwiseClone();
}

/// <summary>A journaled operator command.</summary>
public enum OperatorOperationAction
{
    /// <summary>Replay the latest stored payload to the endpoint.</summary>
    Resubmit,

    /// <summary>Skip the message; later messages in its session may be released.</summary>
    Skip,
}

/// <summary>How far a journaled operator command got.</summary>
public enum OperatorOperationStatus
{
    /// <summary>Recorded; nothing changed yet.</summary>
    Accepted,

    /// <summary>The failed row is claimed (archived); the command is not sent yet.</summary>
    Claimed,

    /// <summary>The command is being published; whether it left is not known yet.</summary>
    Publishing,

    /// <summary>The command was published; its outcome is not observed yet.</summary>
    Dispatched,

    /// <summary>The Resolver recorded a successful response to the command.</summary>
    Succeeded,

    /// <summary>The Resolver recorded a failure response to the command.</summary>
    Failed,

    /// <summary>The command was never published; the row was restored.</summary>
    NotSent,

    /// <summary>Publishing may or may not have happened; a person decides. Never republished automatically.</summary>
    OutcomeUnknown,

    /// <summary>The command was refused before anything changed (budget, cap or state).</summary>
    Refused,
}

/// <summary>Result of <c>TryCreateOperatorOperation</c>.</summary>
/// <param name="Created">True when this call stored the operation; false when one with the same id existed.</param>
/// <param name="Operation">The stored operation: the new one, or the existing one.</param>
public sealed record OperatorOperationCreateResult(bool Created, OperatorOperation Operation);

/// <summary>Filter for counting journaled operations. Null members do not filter.</summary>
public sealed class OperatorOperationQuery
{
    /// <summary>Only this command.</summary>
    public OperatorOperationAction? Action { get; init; }

    /// <summary>Only this tenant.</summary>
    public string? TenantId { get; init; }

    /// <summary>Only this client application.</summary>
    public string? ClientId { get; init; }

    /// <summary>Only workload (true) or delegated (false) callers.</summary>
    public bool? IsWorkload { get; init; }

    /// <summary>Only this endpoint.</summary>
    public string? EndpointId { get; init; }

    /// <summary>Only this event.</summary>
    public string? EventId { get; init; }

    /// <summary>Only operations created at or after this time.</summary>
    public DateTime? CreatedFromUtc { get; init; }

    /// <summary>Operations in these statuses are not counted.</summary>
    public IReadOnlyCollection<OperatorOperationStatus> ExcludedStatuses { get; init; } = [];

    /// <summary>This operation is not counted (the one being checked).</summary>
    public string? ExcludedOperationId { get; init; }

    /// <summary>Whether <paramref name="operation"/> matches every member of this query.</summary>
    /// <param name="operation">The operation to test.</param>
    public bool Matches(OperatorOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        return (Action is null || operation.Action == Action)
            && (TenantId is null || string.Equals(operation.TenantId, TenantId, StringComparison.Ordinal))
            && (ClientId is null || string.Equals(operation.ClientId, ClientId, StringComparison.Ordinal))
            && (IsWorkload is null || operation.IsWorkload == IsWorkload)
            && (EndpointId is null || string.Equals(operation.EndpointId, EndpointId, StringComparison.Ordinal))
            && (EventId is null || string.Equals(operation.EventId, EventId, StringComparison.Ordinal))
            && (CreatedFromUtc is null || operation.CreatedAtUtc.Ticks >= CreatedFromUtc.Value.Ticks)
            && !ExcludedStatuses.Contains(operation.Status)
            && (ExcludedOperationId is null || !string.Equals(operation.OperationId, ExcludedOperationId, StringComparison.Ordinal));
    }
}
