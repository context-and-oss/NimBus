using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Storage operations covering the lifecycle of a tracked message: status transitions,
/// per-message records, audit trail, search, and bulk operations. Implemented by each
/// storage provider (Cosmos DB, SQL Server, in-memory test fake, etc.).
/// </summary>
public interface IMessageTrackingStore
{
    // Status transition uploads.
    //
    // Every one of them returns true when the audit row was created or replaced, and false when
    // StaleWriteGuard refused the write because the row already holds a later outcome — a copy
    // delayed by auto-forward lag, by ScheduleRedelivery, or replayed from the dead-letter queue
    // (Spec 030). Nothing is written on a refusal, and the caller must not retry: the row is
    // already correct. Provider failures, including a lost compare-and-swap, throw rather than
    // returning false, so an unavailable store is never mistaken for a refusal.
    //
    // Terminal writes (Failed, DeadLettered, Unsupported, Skipped, Completed) and content whose
    // MessageType the guard does not classify are unguarded and always return true. The
    // conformance suite pins the rule identically for every provider.

    /// <inheritdoc cref="IMessageTrackingStore"/>
    /// <returns>True when the row was created or replaced; false when the write was refused as stale.</returns>
    Task<bool> UploadPendingMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content);

    /// <inheritdoc cref="UploadPendingMessage"/>
    Task<bool> UploadDeferredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content);

    /// <inheritdoc cref="UploadPendingMessage"/>
    Task<bool> UploadFailedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content);

    /// <inheritdoc cref="UploadPendingMessage"/>
    Task<bool> UploadDeadletteredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content);

    /// <inheritdoc cref="UploadPendingMessage"/>
    Task<bool> UploadUnsupportedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content);

    /// <inheritdoc cref="UploadPendingMessage"/>
    Task<bool> UploadSkippedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content);

    /// <inheritdoc cref="UploadPendingMessage"/>
    Task<bool> UploadCompletedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content);

    /// <summary>
    /// Replaces a Pending row with a Completed projection only when the row still has the
    /// expected last message id. Returns <see langword="false"/> when the row is missing,
    /// no longer Pending, has a different last message id, or another writer won the race.
    /// Provider failures throw. The default implementation is a compatibility fallback for
    /// external providers and is not atomic; built-in providers override it with an atomic
    /// conditional write.
    /// </summary>
    Task<bool> TryCompletePendingMessage(
        string eventId,
        string sessionId,
        string endpointId,
        string? expectedLastMessageId,
        UnresolvedEvent content)
    {
        return TryCompletePendingMessageFallbackAsync(
            eventId, sessionId, endpointId, expectedLastMessageId, content);
    }

    private async Task<bool> TryCompletePendingMessageFallbackAsync(
        string eventId,
        string sessionId,
        string endpointId,
        string? expectedLastMessageId,
        UnresolvedEvent content)
    {
        var current = await GetPendingEvent(endpointId, eventId, sessionId).ConfigureAwait(false);
        if (current is null
            || !string.Equals(current.LastMessageId, expectedLastMessageId, StringComparison.Ordinal))
        {
            return false;
        }

        return await UploadCompletedMessage(eventId, sessionId, endpointId, content).ConfigureAwait(false);
    }

    /// <summary>
    /// Atomically marks an existing Deferred row Skipped only if its last message ID and
    /// update timestamp still match the inspected version. Preserves message content and
    /// identity. Returns false on a missing, changed, deleted, or non-Deferred row.
    /// External providers must implement an atomic operation before supporting recovery.
    /// </summary>
    Task<bool> TrySkipDeferredMessage(string eventId, string sessionId, string endpointId,
        string? expectedLastMessageId, DateTime expectedUpdatedAt) =>
        throw new NotSupportedException("This provider does not support conditional deferred recovery.");

    /// <summary>
    /// Atomically archives an unresolved row, like <see cref="ArchiveFailedEvent"/>, only if it is
    /// visible, has <paramref name="expectedStatus"/>, and its last message ID and update
    /// timestamp still match the inspected version. Operator commands claim a row this way before
    /// they publish, so two callers acting on the same version cannot both send a command.
    /// Returns false on a missing, changed or already archived row.
    /// External providers must implement an atomic operation before supporting guarded commands.
    /// </summary>
    Task<bool> TryArchiveUnresolvedEvent(string eventId, string sessionId, string endpointId,
        ResolutionStatus expectedStatus, string? expectedLastMessageId, DateTime expectedUpdatedAt) =>
        throw new NotSupportedException("This provider does not support guarded operator commands.");

    /// <summary>
    /// Atomically reverses <see cref="TryArchiveUnresolvedEvent"/> when the command it guarded was
    /// not sent: makes the row visible again only if it is still archived with the same status,
    /// last message ID and update timestamp, and no newer write has revived it. Returns false
    /// otherwise.
    /// </summary>
    Task<bool> TryRestoreArchivedEvent(string eventId, string sessionId, string endpointId,
        ResolutionStatus expectedStatus, string? expectedLastMessageId, DateTime expectedUpdatedAt) =>
        throw new NotSupportedException("This provider does not support guarded operator commands.");

    // Single-event lookups.
    //
    // Every single-row lookup (events and messages) returns null when the row does not exist,
    // has another status or session, or was removed or archived. None of them throws for a
    // missing row; EndpointNotFoundException is reserved for an unknown endpoint. Optional
    // string fields round-trip exactly: null reads back as null and "" as "". The conformance
    // suite pins this for every provider.

    /// <summary>Returns the Pending row for the event on the session, or null.</summary>
    Task<UnresolvedEvent?> GetPendingEvent(string endpointId, string eventId, string sessionId);
    /// <summary>Returns the Failed row for the event on the session, or null.</summary>
    Task<UnresolvedEvent?> GetFailedEvent(string endpointId, string eventId, string sessionId);
    /// <summary>Returns the Deferred row for the event on the session, or null.</summary>
    Task<UnresolvedEvent?> GetDeferredEvent(string endpointId, string eventId, string sessionId);
    /// <summary>Returns the DeadLettered row for the event on the session, or null.</summary>
    Task<UnresolvedEvent?> GetDeadletteredEvent(string endpointId, string eventId, string sessionId);
    /// <summary>Returns the Unsupported row for the event on the session, or null.</summary>
    Task<UnresolvedEvent?> GetUnsupportedEvent(string endpointId, string eventId, string sessionId);
    /// <summary>
    /// Returns the most recently updated row for the event on the endpoint, whatever its
    /// status or session, or null.
    /// </summary>
    Task<UnresolvedEvent?> GetEvent(string endpointId, string eventId);
    /// <summary>
    /// Returns the row whose stored id (<c>{EventId}_{SessionId}</c>) matches
    /// <paramref name="id"/>, or null.
    /// </summary>
    Task<UnresolvedEvent?> GetEventById(string endpointId, string id);
    Task<List<UnresolvedEvent>> GetEventsByIds(string endpointId, IEnumerable<string> eventIds);
    Task<IEnumerable<UnresolvedEvent>> GetCompletedEventsOnEndpoint(string endpointId);

    /// <summary>
    /// Locate the single pending-handoff row whose ExternalJobId matches, scoped
    /// to the endpoint that owns the handoff. Used by the operator-side tooling
    /// — the WebApp's "settle this stuck handoff" action and CLI diagnostics —
    /// that needs to resolve audit-row coordinates from the external correlation
    /// token an adapter persisted.
    ///
    /// <para>Not used by <c>IHandoffClient</c>: adapters carry the audit-row
    /// coordinates themselves (via <c>HandoffSettlement</c>) so the settlement
    /// process needs no audit-DB access. See ADR-012 for the rationale.</para>
    ///
    /// <para>Returns null when no matching pending-handoff row exists (already
    /// settled, never registered, or wrong job id). The endpoint scope keeps
    /// Cosmos partitioning correct and lets SQL Server hit a filtered index
    /// (see 0011_HandoffLookup.sql).</para>
    /// </summary>
    Task<UnresolvedEvent?> GetPendingHandoffByExternalJobId(string endpointId, string externalJobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the single oldest (or, for providers that cannot cheaply order, any) pending
    /// event on <paramref name="endpointId"/> whose <c>PendingSubStatus</c> is <c>"Handoff"</c>,
    /// optionally restricted to the given <paramref name="eventTypeIds"/>. Returns null when none.
    ///
    /// <para>Backs the agent receive long-poll, which previously materialised every pending event
    /// (full <c>EventJson</c>) and filtered client-side. All filters (status, sub-status, event
    /// type) are applied server-side and the query is bounded to a single result so the poll stays
    /// cheap on Cosmos.</para>
    /// </summary>
    /// <param name="endpointId">The endpoint (partition) that owns the handoff.</param>
    /// <param name="eventTypeIds">
    /// Restrict to these event types; null or empty matches any event type.
    /// </param>
    Task<UnresolvedEvent?> GetNextPendingHandoffEvent(string endpointId, IReadOnlyCollection<string>? eventTypeIds);

    // Filtered queries

    /// <summary>
    /// Filtered event search. The endpoint id matches EXACTLY, never by prefix:
    /// callers scope authorization and bulk writes by it, so "Billing" must not
    /// return "BillingV2" rows. Its case sensitivity is provider-specific (SQL
    /// Server follows the column collation, Cosmos DB resolves the endpoint's
    /// container, the in-memory store ignores case). The other ID-like string
    /// filters (event id, session id, last message id) match by case-insensitive
    /// PREFIX on every provider; free-text filters (To/From/Payload) keep
    /// provider-specific substring semantics.
    /// Results omit <c>MessageContent.EventContent.EventJson</c> — detail views
    /// fetch the payload on demand via <see cref="GetLatestEventRequestMessage"/>;
    /// <c>ErrorContent</c> is kept (the error-grouped view reads it).
    /// </summary>
    Task<SearchResponse> GetEventsByFilter(EventFilter filter, string continuationToken, int maxSearchItemsCount);

    /// <summary>
    /// Searches the unresolved failures (<c>Failed</c>, <c>DeadLettered</c>, <c>Unsupported</c>)
    /// of every endpoint in <paramref name="endpointIds"/> (exact ids, already authorized by the
    /// caller). <paramref name="filter"/> narrows the result with the same semantics as
    /// <see cref="GetEventsByFilter"/>; its <c>ResolutionStatus</c> is intersected with the three
    /// failure statuses (empty means all three). Results are ordered by <c>UpdatedAt</c>
    /// descending, omit <c>EventJson</c> and keep <c>ErrorContent</c>. The continuation token is
    /// opaque and provider-specific; null or empty when there are no more rows. An empty
    /// <paramref name="endpointIds"/> returns no rows.
    /// </summary>
    Task<SearchResponse> GetFailedEventsAcrossEndpoints(
        EventFilter filter,
        IReadOnlyCollection<string> endpointIds,
        string? continuationToken,
        int maxItemCount);

    /// <summary>
    /// Counts the unresolved failures matched by
    /// <see cref="GetFailedEventsAcrossEndpoints"/> whose <c>UpdatedAt</c> falls in
    /// [<paramref name="fromUtc"/>, <paramref name="toUtc"/>), grouped into buckets of
    /// <paramref name="bucketSize"/> aligned to <paramref name="fromUtc"/>, per endpoint and
    /// status. Rows are sparse (empty buckets are absent).
    /// </summary>
    Task<FailedEventHistogram> GetFailedEventHistogram(
        EventFilter filter,
        IReadOnlyCollection<string> endpointIds,
        DateTime fromUtc,
        DateTime toUtc,
        TimeSpan bucketSize);

    // State counts
    Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId);
    Task<SessionStateCount> DownloadEndpointSessionStateCount(string endpointId, string sessionId);
    Task<IEnumerable<SessionStateCount>> DownloadEndpointSessionStateCountBatch(string endpointId, IEnumerable<string> sessionIds);
    Task<EndpointState?> DownloadEndpointStatePaging(string endpointId, int pageSize, string continuationToken);

    // Session views
    Task<BlockedMessageEventPage> GetBlockedEventsOnSession(string endpointId, string sessionId, int skip, int take);
    Task<IEnumerable<UnresolvedEvent>?> GetPendingEventsOnSession(string endpointId);
    Task<IEnumerable<BlockedMessageEvent>> GetInvalidEventsOnSession(string endpointId);

    // Lifecycle / cleanup
    Task<bool> RemoveMessage(string eventId, string sessionId, string endpointId);
    Task<bool> PurgeMessages(string endpointId, string sessionId);
    Task<bool> PurgeMessages(string endpointId);
    Task ArchiveFailedEvent(string eventId, string sessionId, string endpointId);

    // Per-message history records
    Task StoreMessage(MessageEntity message);
    /// <summary>Returns the stored message, or null.</summary>
    Task<MessageEntity?> GetMessage(string eventId, string messageId);
    Task<IEnumerable<MessageEntity>> GetEventHistory(string eventId);
    /// <summary>
    /// Returns the most recent message that carries event content (an
    /// <c>EventRequest</c> or <c>ResubmissionRequest</c> with a non-empty
    /// <c>EventJson</c>), or <c>null</c> when none exists. Lets callers obtain the
    /// "current request payload" without materialising the whole event history.
    /// </summary>
    Task<MessageEntity?> GetLatestEventRequestMessage(string eventId);
    /// <summary>
    /// Returns the newest message for the event on the endpoint that carries
    /// <c>ErrorContent</c>, or null.
    /// </summary>
    Task<MessageEntity?> GetFailedMessage(string eventId, string endpointId);
    /// <summary>Returns the newest message for the event on the endpoint, or null.</summary>
    Task<MessageEntity?> GetDeadletteredMessage(string eventId, string endpointId);
    Task RemoveStoredMessage(string eventId, string messageId);
    /// <summary>
    /// Hard-deletes every per-message history record stored for the endpoint
    /// (matched on <see cref="MessageEntity.EndpointId"/>) and returns how many were
    /// removed. Audit records are kept. Used by the WebApp's "Delete all events".
    /// </summary>
    Task<int> PurgeStoredMessages(string endpointId);

    /// <summary>
    /// Filtered message search. ID-like string filters (endpoint id, event id,
    /// message id, session id) match by case-insensitive PREFIX on every
    /// provider. Results omit <c>MessageContent.EventContent.EventJson</c> —
    /// detail views fetch the payload via <see cref="GetMessage"/>;
    /// <c>ErrorContent</c> is kept.
    /// </summary>
    Task<MessageSearchResult> SearchMessages(MessageFilter filter, string? continuationToken, int maxItemCount);

    // Audit trail
    Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null);
    Task<IEnumerable<MessageAuditEntity>> GetMessageAudits(string eventId);

    /// <summary>
    /// Filtered audit search. ID-like string filters (event id, endpoint id,
    /// auditor name, event type id) match by case-insensitive PREFIX on every
    /// provider — except that <see cref="AuditFilter.EndpointIdExact"/> switches
    /// the endpoint id to case-insensitive EQUALITY (used by
    /// authorization-scoped callers). Providers unaware of the flag fall back to
    /// prefix semantics; security-sensitive callers must therefore keep a final
    /// exact check on the results.
    /// </summary>
    Task<AuditSearchResult> SearchAudits(AuditFilter filter, string? continuationToken, int maxItemCount);

    /// <summary>
    /// Number of resubmissions per event, counted from the audit trail
    /// (<see cref="MessageAuditType.Resubmit"/> + <see cref="MessageAuditType.ResubmitWithChanges"/>),
    /// for the given events on <paramref name="endpointId"/>. Denied attempts
    /// (<see cref="MessageAuditEntity.AccessDenied"/>) are excluded — they never
    /// resubmitted. Events with no resubmit audits are absent from the result
    /// (treat missing as 0). Implementations answer with a single grouped query
    /// so callers can enrich a whole search page in one round trip.
    /// <para>Default implementation returns an empty map so pre-existing
    /// external providers keep compiling; the UI then simply shows 0
    /// resubmissions.</para>
    /// </summary>
    Task<IReadOnlyDictionary<string, int>> GetResubmitCounts(string endpointId, IReadOnlyCollection<string> eventIds)
        => Task.FromResult<IReadOnlyDictionary<string, int>>(new Dictionary<string, int>());

    // Event reports ("reported" markers)

    /// <summary>
    /// Upserts the per-event "reported" marker keyed on
    /// (<paramref name="endpointId"/>, <paramref name="eventId"/>).
    /// <see cref="EventReport.ReportedAtUtc"/> is stamped with the current UTC
    /// time on every toggle; clearing the marker (<paramref name="isReported"/>
    /// false) also drops the ticket reference.
    /// <para>Default implementation throws <see cref="NotSupportedException"/>
    /// so pre-existing external providers keep compiling while a write cannot
    /// silently succeed without being persisted.</para>
    /// </summary>
    Task SetEventReport(string endpointId, string eventId, bool isReported, string? reportedBy, string? ticketId)
        => throw new NotSupportedException($"This {nameof(IMessageTrackingStore)} implementation does not support event reports.");

    /// <summary>
    /// Batched lookup of "reported" markers for the given events on
    /// <paramref name="endpointId"/>. Events that were never reported are absent
    /// from the result. Implementations answer with a single query so callers
    /// can enrich a whole search page in one round trip.
    /// <para>Default implementation returns an empty map so pre-existing
    /// external providers keep compiling; events then render as unreported.</para>
    /// </summary>
    Task<IReadOnlyDictionary<string, EventReport>> GetEventReports(string endpointId, IReadOnlyCollection<string> eventIds)
        => Task.FromResult<IReadOnlyDictionary<string, EventReport>>(new Dictionary<string, EventReport>());

    // Endpoint diagnostics
    Task<string> GetEndpointErrorList(string endpointId);
}
