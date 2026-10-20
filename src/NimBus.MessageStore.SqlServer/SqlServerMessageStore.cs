using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// SQL Server-backed implementation of every NimBus storage contract. Single class
/// mirrors the surface of CosmosDbClient and the four interfaces it satisfies via
/// <see cref="INimBusMessageStore"/>.
///
/// Schema layout: one table per concern with EndpointId as a discriminator
/// (rather than the per-endpoint container model used by Cosmos). Indexes target
/// the dominant access patterns: status counts per endpoint, recent-events lists,
/// and per-event/per-message lookups.
///
/// Idempotency: status uploads use MERGE on the natural key
/// (EndpointId, EventId, SessionId).
/// Concurrency: ROWVERSION on UnresolvedEvents enables optimistic conflict
/// detection if needed by future writers.
/// </summary>
public sealed class SqlServerMessageStore : INimBusMessageStore, IHeartbeatHistoryStore
{
    private readonly SqlServerMessageStoreOptions _options;
    private readonly string _schema;
    private readonly int _commandTimeout;
    private readonly SqlServerMetricsStore _metrics;
    private readonly SqlServerEventSchemaStore _eventSchemas;
    private readonly SqlServerAccessControlStore _accessControl;
    private readonly SqlServerSubscriptionStore _subscriptions;
    private readonly SqlServerHeartbeatHistoryStore _heartbeatHistory;
    private readonly SqlServerServiceHealthStore _serviceHealth;
    private readonly SqlServerEndpointAcknowledgementStore _acknowledgements;
    private readonly SqlServerEndpointMetadataStore _endpointMetadata;
    private readonly SqlServerMessageTrackingStore _messageTracking;

    public SqlServerMessageStore(IOptions<SqlServerMessageStoreOptions> options)
    {
        _options = options.Value;
        _schema = _options.Schema;
        _commandTimeout = _options.CommandTimeoutSeconds;

        var context = new SqlServerStoreContext
        {
            Open = OpenAsync,
            Table = T,
            CommandTimeout = _commandTimeout,
        };
        _metrics = new SqlServerMetricsStore(context);
        _eventSchemas = new SqlServerEventSchemaStore(context);
        _accessControl = new SqlServerAccessControlStore(context);
        _subscriptions = new SqlServerSubscriptionStore(context);
        _heartbeatHistory = new SqlServerHeartbeatHistoryStore(context);
        _serviceHealth = new SqlServerServiceHealthStore(context);
        _acknowledgements = new SqlServerEndpointAcknowledgementStore(context);
        _endpointMetadata = new SqlServerEndpointMetadataStore(context);
        _messageTracking = new SqlServerMessageTrackingStore(context);
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(_options.ConnectionString);
        try
        {
            await SqlServerExceptionTranslation.TranslateAsync(() => conn.OpenAsync()).ConfigureAwait(false);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    // Defensive bracket-quoting: SqlServerSchemaInitializer is the primary gate
    // for schema-name validation, but escape `]` here too so a misuse outside
    // the hosted service can't break out of the quoted identifier.
    private string T(string table) => $"[{_schema.Replace("]", "]]", StringComparison.Ordinal)}].[{table.Replace("]", "]]", StringComparison.Ordinal)}]";

    // ───────── IMessageTrackingStore — implementation in SqlServerMessageTrackingStore ─────────
    public Task<bool> UploadPendingMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => _messageTracking.UploadPendingMessage(eventId, sessionId, endpointId, content);
    public Task<bool> UploadDeferredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => _messageTracking.UploadDeferredMessage(eventId, sessionId, endpointId, content);
    public Task<bool> UploadFailedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => _messageTracking.UploadFailedMessage(eventId, sessionId, endpointId, content);
    public Task<bool> UploadDeadletteredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => _messageTracking.UploadDeadletteredMessage(eventId, sessionId, endpointId, content);
    public Task<bool> UploadUnsupportedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => _messageTracking.UploadUnsupportedMessage(eventId, sessionId, endpointId, content);
    public Task<bool> UploadSkippedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => _messageTracking.UploadSkippedMessage(eventId, sessionId, endpointId, content);
    public Task<bool> UploadCompletedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => _messageTracking.UploadCompletedMessage(eventId, sessionId, endpointId, content);
    public Task<bool> TryCompletePendingMessage(string eventId, string sessionId, string endpointId, string? expectedLastMessageId, UnresolvedEvent content) => _messageTracking.TryCompletePendingMessage(eventId, sessionId, endpointId, expectedLastMessageId, content);
    public Task<bool> TrySkipDeferredMessage(string eventId, string sessionId, string endpointId, string? expectedLastMessageId, DateTime expectedUpdatedAt) => _messageTracking.TrySkipDeferredMessage(eventId, sessionId, endpointId, expectedLastMessageId, expectedUpdatedAt);
    public Task<bool> TryArchiveUnresolvedEvent(string eventId, string sessionId, string endpointId, ResolutionStatus expectedStatus, string? expectedLastMessageId, DateTime expectedUpdatedAt) => _messageTracking.TryArchiveUnresolvedEvent(eventId, sessionId, endpointId, expectedStatus, expectedLastMessageId, expectedUpdatedAt);
    public Task<bool> TryRestoreArchivedEvent(string eventId, string sessionId, string endpointId, ResolutionStatus expectedStatus, string? expectedLastMessageId, DateTime expectedUpdatedAt) => _messageTracking.TryRestoreArchivedEvent(eventId, sessionId, endpointId, expectedStatus, expectedLastMessageId, expectedUpdatedAt);
    public Task<MessageEntity?> GetMessage(string eventId, string messageId) => _messageTracking.GetMessage(eventId, messageId);
    public Task<IEnumerable<MessageEntity>> GetEventHistory(string eventId) => _messageTracking.GetEventHistory(eventId);
    public Task<MessageEntity?> GetLatestEventRequestMessage(string eventId) => _messageTracking.GetLatestEventRequestMessage(eventId);
    public Task<MessageEntity?> GetFailedMessage(string eventId, string endpointId) => _messageTracking.GetFailedMessage(eventId, endpointId);
    public Task<MessageEntity?> GetDeadletteredMessage(string eventId, string endpointId) => _messageTracking.GetDeadletteredMessage(eventId, endpointId);
    public Task RemoveStoredMessage(string eventId, string messageId) => _messageTracking.RemoveStoredMessage(eventId, messageId);
    public Task<int> PurgeStoredMessages(string endpointId) => _messageTracking.PurgeStoredMessages(endpointId);
    public Task<MessageSearchResult> SearchMessages(MessageFilter filter, string? continuationToken, int maxItemCount) => _messageTracking.SearchMessages(filter, continuationToken, maxItemCount);
    public Task StoreMessage(MessageEntity message) => _messageTracking.StoreMessage(message);
    public Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null) => _messageTracking.StoreMessageAudit(eventId, auditEntity, endpointId, eventTypeId);
    public Task<IEnumerable<MessageAuditEntity>> GetMessageAudits(string eventId) => _messageTracking.GetMessageAudits(eventId);
    public Task<AuditSearchResult> SearchAudits(AuditFilter filter, string? continuationToken, int maxItemCount) => _messageTracking.SearchAudits(filter, continuationToken, maxItemCount);
    public Task<IReadOnlyDictionary<string, int>> GetResubmitCounts(string endpointId, IReadOnlyCollection<string> eventIds) => _messageTracking.GetResubmitCounts(endpointId, eventIds);
    public Task SetEventReport(string endpointId, string eventId, bool isReported, string? reportedBy, string? ticketId) => _messageTracking.SetEventReport(endpointId, eventId, isReported, reportedBy, ticketId);
    public Task<IReadOnlyDictionary<string, EventReport>> GetEventReports(string endpointId, IReadOnlyCollection<string> eventIds) => _messageTracking.GetEventReports(endpointId, eventIds);
    public Task<UnresolvedEvent?> GetPendingEvent(string endpointId, string eventId, string sessionId) => _messageTracking.GetPendingEvent(endpointId, eventId, sessionId);
    public Task<UnresolvedEvent?> GetFailedEvent(string endpointId, string eventId, string sessionId) => _messageTracking.GetFailedEvent(endpointId, eventId, sessionId);
    public Task<UnresolvedEvent?> GetDeferredEvent(string endpointId, string eventId, string sessionId) => _messageTracking.GetDeferredEvent(endpointId, eventId, sessionId);
    public Task<UnresolvedEvent?> GetDeadletteredEvent(string endpointId, string eventId, string sessionId) => _messageTracking.GetDeadletteredEvent(endpointId, eventId, sessionId);
    public Task<UnresolvedEvent?> GetUnsupportedEvent(string endpointId, string eventId, string sessionId) => _messageTracking.GetUnsupportedEvent(endpointId, eventId, sessionId);
    public Task<UnresolvedEvent?> GetEvent(string endpointId, string eventId) => _messageTracking.GetEvent(endpointId, eventId);
    public Task<UnresolvedEvent?> GetEventById(string endpointId, string id) => _messageTracking.GetEventById(endpointId, id);
    public Task<List<UnresolvedEvent>> GetEventsByIds(string endpointId, IEnumerable<string> eventIds) => _messageTracking.GetEventsByIds(endpointId, eventIds);
    public Task<UnresolvedEvent?> GetPendingHandoffByExternalJobId(string endpointId, string externalJobId, CancellationToken cancellationToken = default) => _messageTracking.GetPendingHandoffByExternalJobId(endpointId, externalJobId, cancellationToken);
    public Task<UnresolvedEvent?> GetNextPendingHandoffEvent(string endpointId, IReadOnlyCollection<string>? eventTypeIds) => _messageTracking.GetNextPendingHandoffEvent(endpointId, eventTypeIds);
    public Task<IEnumerable<UnresolvedEvent>> GetCompletedEventsOnEndpoint(string endpointId) => _messageTracking.GetCompletedEventsOnEndpoint(endpointId);
    public Task<SearchResponse> GetEventsByFilter(EventFilter filter, string continuationToken, int maxSearchItemsCount) => _messageTracking.GetEventsByFilter(filter, continuationToken, maxSearchItemsCount);
    public Task<SearchResponse> GetFailedEventsAcrossEndpoints(EventFilter filter, IReadOnlyCollection<string> endpointIds, string? continuationToken, int maxItemCount) => _messageTracking.GetFailedEventsAcrossEndpoints(filter, endpointIds, continuationToken, maxItemCount);
    public Task<FailedEventHistogram> GetFailedEventHistogram(EventFilter filter, IReadOnlyCollection<string> endpointIds, DateTime fromUtc, DateTime toUtc, TimeSpan bucketSize) => _messageTracking.GetFailedEventHistogram(filter, endpointIds, fromUtc, toUtc, bucketSize);
    public Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId) => _messageTracking.DownloadEndpointStateCount(endpointId);
    public Task<SessionStateCount> DownloadEndpointSessionStateCount(string endpointId, string sessionId) => _messageTracking.DownloadEndpointSessionStateCount(endpointId, sessionId);
    public Task<IEnumerable<SessionStateCount>> DownloadEndpointSessionStateCountBatch(string endpointId, IEnumerable<string> sessionIds) => _messageTracking.DownloadEndpointSessionStateCountBatch(endpointId, sessionIds);
    public Task<EndpointState?> DownloadEndpointStatePaging(string endpointId, int pageSize, string continuationToken) => _messageTracking.DownloadEndpointStatePaging(endpointId, pageSize, continuationToken);
    public Task<BlockedMessageEventPage> GetBlockedEventsOnSession(string endpointId, string sessionId, int skip, int take) => _messageTracking.GetBlockedEventsOnSession(endpointId, sessionId, skip, take);
    public Task<IEnumerable<UnresolvedEvent>?> GetPendingEventsOnSession(string endpointId) => _messageTracking.GetPendingEventsOnSession(endpointId);
    public Task<IEnumerable<BlockedMessageEvent>> GetInvalidEventsOnSession(string endpointId) => _messageTracking.GetInvalidEventsOnSession(endpointId);
    public Task<bool> RemoveMessage(string eventId, string sessionId, string endpointId) => _messageTracking.RemoveMessage(eventId, sessionId, endpointId);
    public Task<bool> PurgeMessages(string endpointId, string sessionId) => _messageTracking.PurgeMessages(endpointId, sessionId);
    public Task<bool> PurgeMessages(string endpointId) => _messageTracking.PurgeMessages(endpointId);
    public Task ArchiveFailedEvent(string eventId, string sessionId, string endpointId) => _messageTracking.ArchiveFailedEvent(eventId, sessionId, endpointId);
    public Task<string> GetEndpointErrorList(string endpointId) => _messageTracking.GetEndpointErrorList(endpointId);
    // ───────── Subscription store — implementation in SqlServerSubscriptionStore ─────────
    public Task<EndpointSubscription?> SubscribeToEndpointNotification(string endpointId, string mail, string type, string author, string url, List<string> eventTypes, string payload, int frequency)
        => _subscriptions.SubscribeToEndpointNotification(endpointId, mail, type, author, url, eventTypes, payload, frequency);

    public Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpoint(string endpointId)
        => _subscriptions.GetSubscriptionsOnEndpoint(endpointId);

    public Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpointWithEventtype(string endpoint, string eventtypes, string payload, string errorText)
        => _subscriptions.GetSubscriptionsOnEndpointWithEventtype(endpoint, eventtypes, payload, errorText);

    public Task<bool> UpdateSubscription(EndpointSubscription subscription)
        => _subscriptions.UpdateSubscription(subscription);

    public Task<bool> UnsubscribeById(string endpointId, string id)
        => _subscriptions.UnsubscribeById(endpointId, id);

    public Task<bool> UnsubscribeByMail(string endpointId, string mail)
        => _subscriptions.UnsubscribeByMail(endpointId, mail);

    public Task<bool> DeleteSubscription(string subscriptionId)
        => _subscriptions.DeleteSubscription(subscriptionId);
    // ───────── IEndpointMetadataStore — implementation in SqlServerEndpointMetadataStore ─────────
    public Task<EndpointMetadata?> GetEndpointMetadata(string endpointId) => _endpointMetadata.GetEndpointMetadata(endpointId);
    public Task<List<EndpointMetadata>> GetMetadatas() => _endpointMetadata.GetMetadatas();
    public Task<List<EndpointMetadata>?> GetMetadatas(IEnumerable<string> endpointIds) => _endpointMetadata.GetMetadatas(endpointIds);
    public Task<bool> SetEndpointMetadata(EndpointMetadata endpointMetadata) => _endpointMetadata.SetEndpointMetadata(endpointMetadata);
    public Task<List<EndpointMetadata>> GetMetadatasWithEnabledHeartbeat() => _endpointMetadata.GetMetadatasWithEnabledHeartbeat();
    public Task EnableHeartbeatOnEndpoint(string endpointId, bool enable) => _endpointMetadata.EnableHeartbeatOnEndpoint(endpointId, enable);
    public Task<bool> SetHeartbeat(Heartbeat heartbeat, string endpointId) => _endpointMetadata.SetHeartbeat(heartbeat, endpointId);
    public Task<List<string>> SweepTimedOutHeartbeats(DateTime cutoffUtc) => _endpointMetadata.SweepTimedOutHeartbeats(cutoffUtc);
    public Task<HeartbeatSettings> GetHeartbeatSettings() => _endpointMetadata.GetHeartbeatSettings();
    public Task<bool> SetHeartbeatSettings(HeartbeatSettings settings) => _endpointMetadata.SetHeartbeatSettings(settings);
    public Task<bool> TryClaimHeartbeatSend(DateTime dueBefore) => _endpointMetadata.TryClaimHeartbeatSend(dueBefore);
    public Task<List<HeartbeatOverviewItem>> GetHeartbeatOverview() => _endpointMetadata.GetHeartbeatOverview();
    public Task<AuditSettings> GetAuditSettings() => _endpointMetadata.GetAuditSettings();
    public Task<bool> SetAuditSettings(AuditSettings settings) => _endpointMetadata.SetAuditSettings(settings);
    public Task<McpAccessSettings> GetMcpAccessSettings() => _endpointMetadata.GetMcpAccessSettings();
    public Task<bool> TrySetMcpAccessSettings(McpAccessSettings settings, string? expectedRevision)
        => _endpointMetadata.TrySetMcpAccessSettings(settings, expectedRevision);
    public Task<AgentApiKeySettings> GetAgentApiKeySettings() => _endpointMetadata.GetAgentApiKeySettings();
    public Task<bool> TrySetAgentApiKeySettings(AgentApiKeySettings settings, string? expectedRevision)
        => _endpointMetadata.TrySetAgentApiKeySettings(settings, expectedRevision);
    public Task<List<AgentApiKeyUsage>> GetAgentApiKeyUsage() => _endpointMetadata.GetAgentApiKeyUsage();
    public Task RecordAgentApiKeyUse(string keyId, DateTime usedAtUtc) => _endpointMetadata.RecordAgentApiKeyUse(keyId, usedAtUtc);
    // ───────── Durable endpoint heartbeat history — implementation in SqlServerHeartbeatHistoryStore ─────────
    public Task<List<HeartbeatUptimeDay>> GetHeartbeatUptimeDays(DateTime fromDayUtc)
        => _heartbeatHistory.GetHeartbeatUptimeDays(fromDayUtc);

    public Task<bool> UpsertHeartbeatUptimeDays(IEnumerable<HeartbeatUptimeDay> days)
        => _heartbeatHistory.UpsertHeartbeatUptimeDays(days);

    public Task<List<HeartbeatGap>> GetHeartbeatGaps(DateTime fromUtc)
        => _heartbeatHistory.GetHeartbeatGaps(fromUtc);

    public Task<bool> UpsertHeartbeatGaps(IEnumerable<HeartbeatGap> gaps)
        => _heartbeatHistory.UpsertHeartbeatGaps(gaps);

    public Task<bool> TryClaimHeartbeatHistoryFold(DateTime dueBefore)
        => _heartbeatHistory.TryClaimHeartbeatHistoryFold(dueBefore);

    public Task PruneHeartbeatHistory(DateTime cutoffUtc)
        => _heartbeatHistory.PruneHeartbeatHistory(cutoffUtc);
    // ───────── IServiceHealthStore — implementation in SqlServerServiceHealthStore ─────────
    public Task<List<ServiceHealth>> GetServiceHealth() => _serviceHealth.GetServiceHealth();

    public Task<bool> TryClaimServiceProbe(string serviceId, DateTime dueBefore, string probeMessageId)
        => _serviceHealth.TryClaimServiceProbe(serviceId, dueBefore, probeMessageId);

    public Task<bool> SetServiceHealth(ServiceHealth serviceHealth)
        => _serviceHealth.SetServiceHealth(serviceHealth);

    public Task<List<string>> SweepTimedOutServiceProbes(DateTime cutoffUtc)
        => _serviceHealth.SweepTimedOutServiceProbes(cutoffUtc);

    // ───────── Metrics — implementation in SqlServerMetricsStore ─────────

    [Obsolete("Use GetEndpointMetrics(from, to). This overload will be removed in v5.")]
    public Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from) => _metrics.GetEndpointMetrics(from);

    public Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from, DateTime to) => _metrics.GetEndpointMetrics(from, to);

    [Obsolete("Use GetEndpointLatencyMetrics(from, to). This overload will be removed in v5.")]
    public Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from) => _metrics.GetEndpointLatencyMetrics(from);

    public Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from, DateTime to) => _metrics.GetEndpointLatencyMetrics(from, to);

    [Obsolete("Use GetFailedMessageInsights(from, to). This overload will be removed in v5.")]
    public Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from) => _metrics.GetFailedMessageInsights(from);

    public Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from, DateTime to) => _metrics.GetFailedMessageInsights(from, to);

    [Obsolete("Use GetTimeSeriesMetrics(from, to, substringLength, bucketLabel). This overload will be removed in v5.")]
    public Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel) => _metrics.GetTimeSeriesMetrics(from, substringLength, bucketLabel);

    public Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, DateTime to, int substringLength, string bucketLabel) => _metrics.GetTimeSeriesMetrics(from, to, substringLength, bucketLabel);

    [Obsolete("Use GetEventTypeTimeSeriesMetrics(from, to, substringLength, bucketLabel). This overload will be removed in v5.")]
    public Task<EventTypeTimeSeriesResult> GetEventTypeTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel) => _metrics.GetEventTypeTimeSeriesMetrics(from, substringLength, bucketLabel);

    public Task<EventTypeTimeSeriesResult> GetEventTypeTimeSeriesMetrics(DateTime from, DateTime to, int substringLength, string bucketLabel) => _metrics.GetEventTypeTimeSeriesMetrics(from, to, substringLength, bucketLabel);

    // ───────── Event schema store — implementation in SqlServerEventSchemaStore ─────────

    public Task<EventSchema?> GetSchema(string eventTypeId) => _eventSchemas.GetSchema(eventTypeId);

    public Task<IReadOnlyList<EventSchema>> GetSchemas() => _eventSchemas.GetSchemas();

    public Task<EventSchema> DefineEventType(EventSchema schema) => _eventSchemas.DefineEventType(schema);

    // ───────── Access-control store (spec 026) — implementation in SqlServerAccessControlStore ─────────

    public Task<AccessControlList?> GetSiteAccessControl() => _accessControl.GetSiteAccessControl();

    public Task SetSiteAccessControl(AccessControlList accessControl) => _accessControl.SetSiteAccessControl(accessControl);

    public Task<AccessControlList?> GetEndpointAccessControl(string endpointId) => _accessControl.GetEndpointAccessControl(endpointId);

    public Task<IReadOnlyList<AccessControlList>> GetEndpointAccessControls() => _accessControl.GetEndpointAccessControls();

    public Task SetEndpointAccessControl(string endpointId, AccessControlList accessControl) => _accessControl.SetEndpointAccessControl(endpointId, accessControl);

    // ───────── Monitor acknowledgements — implementation in SqlServerEndpointAcknowledgementStore ─────────

    public Task<IReadOnlyList<EndpointAcknowledgement>> GetEndpointAcknowledgements() => _acknowledgements.GetEndpointAcknowledgements();

    public Task SetEndpointAcknowledgement(EndpointAcknowledgement acknowledgement) => _acknowledgements.SetEndpointAcknowledgement(acknowledgement);

    public Task<bool> RemoveEndpointAcknowledgement(string endpointId, string? expectedAcknowledgementId = null)
        => _acknowledgements.RemoveEndpointAcknowledgement(endpointId, expectedAcknowledgementId);
}
