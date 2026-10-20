using System;
using System.Collections.Generic;
using System.Text;

namespace NimBus.MessageStore;

public class MessageAuditEntity
{
    public string AuditorName { get; set; } = null!;
    public DateTime AuditTimestamp { get; set; }
    public MessageAuditType AuditType { get; set; }
    public string? Comment { get; set; }

    /// <summary>
    /// True when the audited action was rejected by the authorization layer
    /// (the user attempted the action but did not have the required permission).
    /// Defaults to <c>false</c> so legacy rows project unchanged.
    /// </summary>
    public bool AccessDenied { get; set; }

    /// <summary>
    /// Optional structured context for the action: search filter JSON,
    /// ResubmitWithChanges body, or any other payload the caller wants
    /// preserved alongside the audit row. Truncated to ~4 KB by the
    /// <see cref="NimBus.WebApp.Services"/> audit writer to stay within
    /// every provider's column / document size budget.
    /// </summary>
    public string? Data { get; set; }

    /// <summary>
    /// Event id the audit row is associated with. Mirrors the <c>eventId</c>
    /// argument supplied to <see cref="Abstractions.IMessageTrackingStore.StoreMessageAudit"/>
    /// so downstream readers do not have to join on a side channel.
    /// </summary>
    public string? EventId { get; set; }

    /// <summary>
    /// Endpoint id the audit row is associated with. Mirrors the <c>endpointId</c>
    /// argument supplied to <see cref="Abstractions.IMessageTrackingStore.StoreMessageAudit"/>.
    /// </summary>
    public string? EndpointId { get; set; }

    /// <summary>
    /// CloudEvents <c>id</c> attribute when the tracked message was received or
    /// published as a CloudEvent, otherwise <c>null</c>. Preserved so CloudEvents
    /// identity surfaces in the message store / management UI. Defaults to
    /// <c>null</c> so legacy rows and native messages project unchanged.
    /// </summary>
    public string? CloudEventId { get; set; }

    /// <summary>CloudEvents <c>source</c> attribute, or <c>null</c> for a native message.</summary>
    public string? CloudEventSource { get; set; }

    /// <summary>CloudEvents <c>type</c> attribute, or <c>null</c> for a native message.</summary>
    public string? CloudEventType { get; set; }

    /// <summary>CloudEvents <c>subject</c> attribute, or <c>null</c> for a native message.</summary>
    public string? CloudEventSubject { get; set; }
}

public enum MessageAuditType
{
    Resubmit,
    ResubmitWithChanges,
    Skip,
    Retry,
    Comment,
    CompleteHandoff,
    FailHandoff,

    /// <summary>Operator searched the event store with a filter (records the filter as Data).</summary>
    SearchEvents,

    /// <summary>Operator opened the event-details page for a specific event.</summary>
    GetEventDetails,

    /// <summary>Operator opened the endpoint-details page for a specific endpoint.</summary>
    GetEndpointDetails,

    /// <summary>Operator enabled an endpoint subscription.</summary>
    EnableEndpoint,

    /// <summary>Operator disabled an endpoint subscription.</summary>
    DisableEndpoint,

    /// <summary>Operator enabled sending on an endpoint (topic status Active).</summary>
    EnableEndpointSend,

    /// <summary>Operator disabled sending on an endpoint (topic status SendDisabled).</summary>
    DisableEndpointSend,

    /// <summary>Operator purged messages from an endpoint / subscription / session.</summary>
    PurgeMessages,

    /// <summary>Operator composed and published a new event from the WebApp.</summary>
    Compose,

    /// <summary>
    /// Operator toggled the per-event "reported" marker (records the flag and
    /// ticket id as Data). Appended last: SQL persists enum names but Cosmos
    /// persists numeric values, so existing members must keep their positions.
    /// </summary>
    ReportEvent,

    /// <summary>Operator granted an access-control role (spec 026; scope/role/entry recorded as Data).</summary>
    GrantRole,

    /// <summary>Operator revoked an access-control role (spec 026; scope/role/entry recorded as Data).</summary>
    RevokeRole,

    /// <summary>
    /// Operator changed a Service Bus subscription from Topology → Subscriptions
    /// (pause, resume, purge, delete, recreate, rule detach/restore). The topic,
    /// subscription and action are recorded as Data.
    /// </summary>
    ManageSubscription,

    /// <summary>Operator changed the platform heartbeat schedule (the new settings are recorded as Data).</summary>
    UpdateHeartbeatSettings,

    /// <summary>Operator triggered an immediate heartbeat fan-out from Settings → Heartbeat probing.</summary>
    SendHeartbeatNow,

    /// <summary>Operator opted one endpoint back into the heartbeat fan-out.</summary>
    EnableEndpointHeartbeat,

    /// <summary>Operator excluded one endpoint from the heartbeat fan-out.</summary>
    DisableEndpointHeartbeat,

    /// <summary>Operator permanently deleted an orphaned Cosmos DB container.</summary>
    DeleteStorageContainer,

    /// <summary>Operator reconciled stale Pending rows from stored Resolver responses.</summary>
    ReconcileStalePending,

    /// <summary>An advisory failure classification was requested for an event.</summary>
    FailureClassified,

    /// <summary>A site Owner changed shared failure-intelligence configuration.</summary>
    UpdateIntelligenceSettings,

    /// <summary>A site Owner changed the traffic simulator's settings, including endpoint ownership (recorded as Data).</summary>
    UpdateSimulationSettings,

    /// <summary>A site Owner started, paused or stopped the traffic simulator (the action is recorded as Data).</summary>
    ControlSimulation,

    /// <summary>A site Owner replaced the traffic simulator's publisher and failure-mode config (recorded as Data).</summary>
    UpdateSimulationConfig,

    /// <summary>Operator acknowledged an endpoint's failures on the Monitor (the reason is recorded as Data).</summary>
    AcknowledgeEndpoint,

    /// <summary>Operator cleared an endpoint's Monitor acknowledgement.</summary>
    ClearEndpointAcknowledgement,

    /// <summary>
    /// A site Owner changed which audit types are recorded (the disabled types are
    /// recorded as Data). Always recorded, whatever the selection.
    /// </summary>
    UpdateAuditSettings,

    /// <summary>
    /// An operator command (resubmit, skip) was accepted and audited, but publishing it
    /// failed, so the message was restored and nothing was sent. The cause is recorded as Data.
    /// Always recorded, whatever the selection.
    /// </summary>
    CommandNotSent,

    /// <summary>
    /// A site Owner saved the MCP access policy or turned MCP access off (Spec 037). The
    /// revisions and the list of changes are recorded as Data. Always recorded.
    /// </summary>
    UpdateMcpSettings,

    /// <summary>
    /// An MCP call was refused by the MCP access policy or for a missing scope (Spec 037),
    /// recorded with <see cref="MessageAuditEntity.AccessDenied"/> set. The reason, tool and
    /// client are recorded as Data. Always recorded, deduplicated per caller and reason.
    /// </summary>
    McpAccessRefused,

    /// <summary>
    /// A site Owner issued an agent API key (Spec 039). The key id, agent id and name are
    /// recorded as Data, never the key. Always recorded.
    /// </summary>
    AgentApiKeyCreated,

    /// <summary>A site Owner revoked an agent API key (Spec 039). Always recorded.</summary>
    AgentApiKeyRevoked,

    /// <summary>A site Owner removed a revoked or expired agent API key (Spec 039). Always recorded.</summary>
    AgentApiKeyRemoved,
}
