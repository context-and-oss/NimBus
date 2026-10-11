using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Storage operations for endpoint metadata, ownership, heartbeat state and the
/// platform-wide audit settings.
/// Implemented per storage provider.
/// </summary>
public interface IEndpointMetadataStore
{
    /// <summary>
    /// Returns the endpoint's metadata, or null when none has been stored. Optional string
    /// fields round-trip exactly (null stays null), like every single-row store lookup.
    /// </summary>
    Task<EndpointMetadata?> GetEndpointMetadata(string endpointId);
    Task<List<EndpointMetadata>> GetMetadatas();
    Task<List<EndpointMetadata>?> GetMetadatas(IEnumerable<string> endpointIds);
    Task<bool> SetEndpointMetadata(EndpointMetadata endpointMetadata);

    /// <summary>
    /// Endpoints that explicitly opted IN to heartbeat probing
    /// (<see cref="EndpointMetadata.IsHeartbeatEnabled"/> == true).
    /// </summary>
    /// <remarks>
    /// This is NOT the fan-out enumeration. The sender walks the platform catalog and
    /// excludes only explicit opt-outs (<c>IsHeartbeatEnabled == false</c>), so an
    /// endpoint with missing or null metadata is probed. Wiring the fan-out to this
    /// method would silently skip every endpoint an operator never touched.
    /// </remarks>
    Task<List<EndpointMetadata>> GetMetadatasWithEnabledHeartbeat();

    /// <summary>
    /// Sets the heartbeat opt-in flag for one endpoint, creating the metadata record
    /// when it does not exist yet.
    /// </summary>
    /// <param name="endpointId">The endpoint to configure.</param>
    /// <param name="enable">True to probe the endpoint, false to leave it out of the fan-out.</param>
    Task EnableHeartbeatOnEndpoint(string endpointId, bool enable);

    /// <summary>
    /// Writes one heartbeat row for an endpoint, keyed by
    /// <see cref="Heartbeat.MessageId"/> so the Pending probe and the answer that
    /// settles it share a row, prunes the endpoint's history, and refreshes the
    /// rollup status on the metadata record.
    /// </summary>
    /// <param name="heartbeat">The probe state to store.</param>
    /// <param name="endpointId">The endpoint the probe belongs to.</param>
    /// <returns>True when the row was written.</returns>
    Task<bool> SetHeartbeat(Heartbeat heartbeat, string endpointId);

    /// <summary>
    /// Settles every Pending heartbeat started at or before <paramref name="cutoffUtc"/>
    /// to Off and refreshes the per-endpoint rollup status. Returns the affected endpoint ids.
    /// </summary>
    /// <param name="cutoffUtc">Probes started at or before this instant have timed out.</param>
    /// <returns>The endpoint ids that were swept.</returns>
    Task<List<string>> SweepTimedOutHeartbeats(DateTime cutoffUtc);

    /// <summary>The platform-wide heartbeat schedule; defaults when nothing has been written.</summary>
    Task<HeartbeatSettings> GetHeartbeatSettings();

    /// <summary>
    /// Upserts the heartbeat schedule. A null <see cref="HeartbeatSettings.LastSentAtUtc"/>
    /// leaves the stored value untouched — that field is owned by
    /// <see cref="TryClaimHeartbeatSend"/>, so an operator edit must not reset the schedule.
    /// </summary>
    /// <param name="settings">The schedule to store.</param>
    /// <returns>True when the record was written.</returns>
    Task<bool> SetHeartbeatSettings(HeartbeatSettings settings);

    /// <summary>
    /// Atomically claims the next scheduled fan-out. Returns false when heartbeats are
    /// disabled, or when the last send happened after <paramref name="dueBefore"/> —
    /// so exactly one scaled-out instance sends per interval.
    /// </summary>
    /// <param name="dueBefore">A fan-out is due only when the previous one ran at or before this instant.</param>
    /// <returns>True when this caller owns the send.</returns>
    Task<bool> TryClaimHeartbeatSend(DateTime dueBefore);

    /// <summary>One row per known endpoint: current status, round-trip and reported SDK version.</summary>
    Task<List<HeartbeatOverviewItem>> GetHeartbeatOverview();

    /// <summary>
    /// The platform-wide audit-type selection; defaults (every type recorded) when nothing
    /// has been written.
    /// </summary>
    Task<AuditSettings> GetAuditSettings();

    /// <summary>Upserts the platform-wide audit-type selection, replacing the stored list.</summary>
    /// <param name="settings">The selection to store.</param>
    /// <returns>True when the record was written.</returns>
    Task<bool> SetAuditSettings(AuditSettings settings);

    /// <summary>
    /// The site Owner's MCP access policy (Spec 037), or the defaults with a null
    /// <see cref="McpAccessSettings.Revision"/> when none was saved.
    /// </summary>
    Task<McpAccessSettings> GetMcpAccessSettings();

    /// <summary>
    /// Saves the MCP access policy when the stored revision equals
    /// <paramref name="expectedRevision"/>; null means only when nothing is stored yet.
    /// <paramref name="settings"/> must already carry the new revision.
    /// </summary>
    /// <param name="settings">The policy to store, with its new revision.</param>
    /// <param name="expectedRevision">The revision the caller read, or null to create.</param>
    /// <returns>False on a revision conflict; the stored record is unchanged.</returns>
    Task<bool> TrySetMcpAccessSettings(McpAccessSettings settings, string? expectedRevision);

    /// <summary>
    /// The issued agent API keys (Spec 039), or an empty list with a null
    /// <see cref="AgentApiKeySettings.Revision"/> when none was saved.
    /// </summary>
    Task<AgentApiKeySettings> GetAgentApiKeySettings();

    /// <summary>
    /// Saves the agent API keys when the stored revision equals
    /// <paramref name="expectedRevision"/>; null means only when nothing is stored yet.
    /// <paramref name="settings"/> must already carry the new revision.
    /// </summary>
    /// <param name="settings">The keys to store, with their new revision.</param>
    /// <param name="expectedRevision">The revision the caller read, or null to create.</param>
    /// <returns>False on a revision conflict; the stored record is unchanged.</returns>
    Task<bool> TrySetAgentApiKeySettings(AgentApiKeySettings settings, string? expectedRevision);

    /// <summary>The last recorded use of every agent API key that has been used.</summary>
    Task<List<AgentApiKeyUsage>> GetAgentApiKeyUsage();

    /// <summary>Records that the key authenticated a request; the last write wins.</summary>
    /// <param name="keyId">The key's <see cref="AgentApiKey.KeyId"/>.</param>
    /// <param name="usedAtUtc">When the key was used.</param>
    Task RecordAgentApiKeyUse(string keyId, DateTime usedAtUtc);
}
