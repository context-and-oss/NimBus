using Microsoft.Extensions.Logging;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using Microsoft.Azure.Cosmos;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.MessageStore;

public class CosmosDbClient : NimBus.MessageStore.Abstractions.INimBusMessageStore, IHeartbeatHistoryStore
{
    /// <inheritdoc />
    public bool PrunesHeartbeatHistoryAutomatically => _heartbeatHistory.PrunesHeartbeatHistoryAutomatically;

    private readonly ICosmosClientAdapter _cosmosClient;
    private readonly ILogger? _logger;

    // Cosmos container handles are lightweight client-side proxies the SDK
    // recommends caching for the client's lifetime. Every data operation used to
    // call CreateContainerIfNotExistsAsync, which issues a control-plane
    // round-trip on each call even when the container already exists. Cache the
    // resolved handle (running the one-time "ensure exists" once per container)
    // so steady-state reads/writes skip that round-trip. Keyed by container id,
    // which is unique per physical container in the database. Entries are evicted
    // when a container is deleted (PurgeMessages) so the next access recreates it.
    private readonly ConcurrentDictionary<string, Task<ICosmosContainerAdapter>> _containerCache = new();

    private readonly CosmosDbMetricsStore _metrics;
    private readonly CosmosDbAccessControlStore _accessControl;
    private readonly CosmosDbEventSchemaStore _eventSchemas;
    private readonly CosmosDbSubscriptionStore _subscriptions;
    private readonly CosmosDbHeartbeatHistoryStore _heartbeatHistory;
    private readonly CosmosDbServiceHealthStore _serviceHealth;
    private readonly CosmosDbEndpointAcknowledgementStore _acknowledgements;
    private readonly CosmosDbEndpointMetadataStore _endpointMetadata;
    private readonly CosmosDbMessageTrackingStore _messageTracking;

    internal const string DatabaseId = "MessageDatabase";

    // Hot-path writes only ever inspect StatusCode on the response; skipping the
    // response body (which echoes the whole document, EventJson included) saves
    // egress bytes and response-deserialization on every tracked message.

    private const string PendingStatus = "Pending";
    private const string FailedStatus = "Failed";
    private const string DeferredStatus = "Deferred";
    private const string DLQStatus = "DeadLettered";
    private const string UnsupportedStatus = "Unsupported";
    private const string CompletedStatus = "Completed";
    private const string SkippedStatus = "Skipped";

    private const string PublisherRole = "Publisher";
    private const string SubscriptionsContainer = "subscriptions";
    private const string MessagesContainer = "messages";
    private const string AuditsContainer = "audits";
    private const string EventSchemasContainer = "eventschemas";
    private const string EventReportsContainer = "eventreports";
    private const string AccessControlContainer = "accesscontrol";
    private const string MetadataContainer = "Metadata";

    // The heartbeat schedule and the service-health rows must NOT live in Metadata:
    // its "SELECT * FROM c" reads would surface them as phantom endpoints.
    private const string SettingsContainer = "settings";
    private const string ServiceHealthContainer = "servicehealth";
    private const string HeartbeatUptimeDaysContainer = "heartbeatuptimedays";
    private const string HeartbeatGapsContainer = "heartbeatgaps";
    private const string EndpointAcknowledgementsContainer = "endpointacknowledgements";
    private const int HeartbeatHistoryTtlSeconds = 90 * 24 * 60 * 60;

    // Seconds stamped as the document ttl on non-terminal tracking rows. -1 disables
    // expiry, which is the default and the behaviour before the option existed.
    private readonly int _unresolvedTtlSeconds;

    public CosmosDbClient(CosmosClient cosmosClient, ILogger<CosmosDbClient>? logger = null)
        : this(cosmosClient, logger, new CosmosDbMessageStoreOptions())
    {
    }

    /// <summary>
    /// Creates the store with explicit options. The third parameter is non-optional so
    /// existing one- and two-argument calls keep binding the overload above.
    /// </summary>
    public CosmosDbClient(CosmosClient cosmosClient, ILogger<CosmosDbClient>? logger, CosmosDbMessageStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _unresolvedTtlSeconds = options.ResolveUnresolvedTimeToLiveSeconds();
        _logger = logger;
        _cosmosClient = new CosmosClientAdapter(cosmosClient, _logger);
        _metrics = new CosmosDbMetricsStore(GetMessagesContainer);
        _accessControl = new CosmosDbAccessControlStore(GetAccessControlContainer);
        _eventSchemas = new CosmosDbEventSchemaStore(GetEventSchemasContainer);
        _subscriptions = new CosmosDbSubscriptionStore(GetSubscriptionsContainer, GetEndpointErrorList, _logger);
        _heartbeatHistory = new CosmosDbHeartbeatHistoryStore(GetHeartbeatUptimeDaysContainer, GetHeartbeatGapsContainer, GetSettingsContainer);
        _serviceHealth = new CosmosDbServiceHealthStore(GetServiceHealthContainer, _logger);
        _acknowledgements = new CosmosDbEndpointAcknowledgementStore(GetEndpointAcknowledgementsContainer);
        _endpointMetadata = new CosmosDbEndpointMetadataStore(GetMetadataContainer, GetSettingsContainer, _logger);
        _messageTracking = new CosmosDbMessageTrackingStore(GetEndpointContainer, GetMessagesContainer, GetAuditsContainer, GetEventReportsContainer, endpointId => _containerCache.TryRemove(endpointId, out _), _logger, _unresolvedTtlSeconds);
    }

    public CosmosDbClient(ICosmosClientAdapter cosmosClient, ILogger<CosmosDbClient>? logger = null)
        : this(cosmosClient, logger, new CosmosDbMessageStoreOptions())
    {
    }

    /// <summary>
    /// Creates the store with explicit options. The third parameter is non-optional so
    /// existing one- and two-argument calls keep binding the overload above.
    /// </summary>
    public CosmosDbClient(ICosmosClientAdapter cosmosClient, ILogger<CosmosDbClient>? logger, CosmosDbMessageStoreOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        _unresolvedTtlSeconds = options.ResolveUnresolvedTimeToLiveSeconds();
        _logger = logger;
        _cosmosClient = new TransientTranslatingCosmosClientAdapter(cosmosClient, _logger);
        _metrics = new CosmosDbMetricsStore(GetMessagesContainer);
        _accessControl = new CosmosDbAccessControlStore(GetAccessControlContainer);
        _eventSchemas = new CosmosDbEventSchemaStore(GetEventSchemasContainer);
        _subscriptions = new CosmosDbSubscriptionStore(GetSubscriptionsContainer, GetEndpointErrorList, _logger);
        _heartbeatHistory = new CosmosDbHeartbeatHistoryStore(GetHeartbeatUptimeDaysContainer, GetHeartbeatGapsContainer, GetSettingsContainer);
        _serviceHealth = new CosmosDbServiceHealthStore(GetServiceHealthContainer, _logger);
        _acknowledgements = new CosmosDbEndpointAcknowledgementStore(GetEndpointAcknowledgementsContainer);
        _endpointMetadata = new CosmosDbEndpointMetadataStore(GetMetadataContainer, GetSettingsContainer, _logger);
        _messageTracking = new CosmosDbMessageTrackingStore(GetEndpointContainer, GetMessagesContainer, GetAuditsContainer, GetEventReportsContainer, endpointId => _containerCache.TryRemove(endpointId, out _), _logger, _unresolvedTtlSeconds);
    }

    // ───────── Subscription store — implementation in CosmosDbSubscriptionStore ─────────
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
    // Resolves a container handle through _containerCache, running "ensure
    // exists" exactly once per container id and caching the resulting handle.
    // A faulted creation is never left cached: the faulted entry is evicted
    // (key + value match, so a newer good entry is never removed) and the
    // next caller retries.
    // defaultTimeToLive is null for every shared container (TTL stays disabled at container
    // level) and set only for per-endpoint tracking containers. Keying the cache by container
    // id alone stays safe because reserved ids are rejected below, so an id can never be
    // reached with two different TTL modes.
    private async Task<ICosmosContainerAdapter> GetCachedContainerAsync(
        string containerId,
        string partitionKeyPath,
        int? defaultTimeToLive = null)
    {
        var containerTask = _containerCache.GetOrAdd(containerId, id => EnsureContainerExistsAsync(id, partitionKeyPath, defaultTimeToLive));
        try
        {
            return await containerTask;
        }
        catch
        {
            _containerCache.TryRemove(new KeyValuePair<string, Task<ICosmosContainerAdapter>>(containerId, containerTask));
            throw;
        }
    }

    private async Task<ICosmosContainerAdapter> EnsureContainerExistsAsync(
        string containerId,
        string partitionKeyPath,
        int? defaultTimeToLive)
    {
        var db = _cosmosClient.GetDatabase(DatabaseId);

        if (defaultTimeToLive is null)
        {
            // Shared containers keep TTL disabled at container level.
            return await CosmosExceptionTranslation.TranslateTransientAsync(
                () => db.CreateContainerIfNotExistsAsync(containerId, partitionKeyPath),
                _logger);
        }

        var properties = new ContainerProperties(containerId, partitionKeyPath)
        {
            DefaultTimeToLive = defaultTimeToLive,
        };
        return await CosmosExceptionTranslation.TranslateTransientAsync(
            () => db.CreateContainerIfNotExistsAsync(properties),
            _logger);
    }

    private Task<ICosmosContainerAdapter> GetEndpointContainer(string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId))
        {
            throw new ArgumentNullException(nameof(endpointId), "EndpointId cannot be null or empty");
        }

        // An endpoint id equal to one of the store's own container ids resolves to the same
        // physical container and the same _containerCache entry, so call order would decide the
        // container's partition key path and TTL mode. Reject instead.
        CosmosContainerDefaults.EnsureNotReservedEndpointId(endpointId);

        return GetCachedContainerAsync(
            endpointId,
            CosmosContainerDefaults.EndpointPartitionKeyPath,
            CosmosContainerDefaults.EndpointContainerDefaultTimeToLive);
    }

    private Task<ICosmosContainerAdapter> GetSubscriptionsContainer() =>
        GetCachedContainerAsync(SubscriptionsContainer, "/id");

    private Task<ICosmosContainerAdapter> GetMetadataContainer() =>
        GetCachedContainerAsync(MetadataContainer, "/id");

    private Task<ICosmosContainerAdapter> GetMessagesContainer() =>
        GetCachedContainerAsync(MessagesContainer, "/eventId");

    private Task<ICosmosContainerAdapter> GetAuditsContainer() =>
        GetCachedContainerAsync(AuditsContainer, "/eventId");

    private Task<ICosmosContainerAdapter> GetEventSchemasContainer() =>
        GetCachedContainerAsync(EventSchemasContainer, "/id");

    // EventReport serializes with PascalCase names (only "id" is attributed), so
    // the partition path is /EndpointId — all lookups are endpoint-scoped.
    private Task<ICosmosContainerAdapter> GetEventReportsContainer() =>
        GetCachedContainerAsync(EventReportsContainer, "/EndpointId");

    private Task<ICosmosContainerAdapter> GetAccessControlContainer() =>
        GetCachedContainerAsync(AccessControlContainer, "/id");

    private Task<ICosmosContainerAdapter> GetSettingsContainer() =>
        GetCachedContainerAsync(SettingsContainer, "/id");

    private Task<ICosmosContainerAdapter> GetServiceHealthContainer() =>
        GetCachedContainerAsync(ServiceHealthContainer, "/id");

    private Task<ICosmosContainerAdapter> GetEndpointAcknowledgementsContainer() =>
        GetCachedContainerAsync(EndpointAcknowledgementsContainer, "/id");

    private Task<ICosmosContainerAdapter> GetHeartbeatUptimeDaysContainer() =>
        GetCachedContainerAsync(HeartbeatUptimeDaysContainer, "/EndpointId", CosmosContainerDefaults.EndpointContainerDefaultTimeToLive);

    private Task<ICosmosContainerAdapter> GetHeartbeatGapsContainer() =>
        GetCachedContainerAsync(HeartbeatGapsContainer, "/EndpointId", CosmosContainerDefaults.EndpointContainerDefaultTimeToLive);

    // ── IAccessControlStore (spec 026) — implementation in CosmosDbAccessControlStore ──

    public Task<AccessControlList?> GetSiteAccessControl() => _accessControl.GetSiteAccessControl();

    public Task SetSiteAccessControl(AccessControlList accessControl) => _accessControl.SetSiteAccessControl(accessControl);

    public Task<AccessControlList?> GetEndpointAccessControl(string endpointId) => _accessControl.GetEndpointAccessControl(endpointId);

    public Task<IReadOnlyList<AccessControlList>> GetEndpointAccessControls() => _accessControl.GetEndpointAccessControls();

    public Task SetEndpointAccessControl(string endpointId, AccessControlList accessControl) => _accessControl.SetEndpointAccessControl(endpointId, accessControl);

    // ── IEventSchemaStore — implementation in CosmosDbEventSchemaStore ──

    public Task<EventSchema?> GetSchema(string eventTypeId) => _eventSchemas.GetSchema(eventTypeId);

    public Task<IReadOnlyList<EventSchema>> GetSchemas() => _eventSchemas.GetSchemas();

    public Task<EventSchema> DefineEventType(EventSchema schema) => _eventSchemas.DefineEventType(schema);

    // ── IMessageTrackingStore — implementation in CosmosDbMessageTrackingStore ──
    public Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId) => _messageTracking.DownloadEndpointStateCount(endpointId);
    public Task<SessionStateCount> DownloadEndpointSessionStateCount(string endpointId, string sessionId) => _messageTracking.DownloadEndpointSessionStateCount(endpointId, sessionId);
    public Task<IEnumerable<SessionStateCount>> DownloadEndpointSessionStateCountBatch(string endpointId, IEnumerable<string> sessionIds) => _messageTracking.DownloadEndpointSessionStateCountBatch(endpointId, sessionIds);
    public Task<EndpointState?> DownloadEndpointStatePaging(string endpointId, int pageSize, string continuationToken) => _messageTracking.DownloadEndpointStatePaging(endpointId, pageSize, continuationToken);
    public Task<bool> UploadDeferredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => _messageTracking.UploadDeferredMessage(eventId, sessionId, endpointId, content);
    public Task<bool> UploadFailedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => _messageTracking.UploadFailedMessage(eventId, sessionId, endpointId, content);
    public Task<bool> UploadPendingMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => _messageTracking.UploadPendingMessage(eventId, sessionId, endpointId, content);
    public Task<bool> UploadDeadletteredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => _messageTracking.UploadDeadletteredMessage(eventId, sessionId, endpointId, content);
    public Task<bool> UploadUnsupportedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => _messageTracking.UploadUnsupportedMessage(eventId, sessionId, endpointId, content);
    public Task<bool> UploadSkippedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => _messageTracking.UploadSkippedMessage(eventId, sessionId, endpointId, content);
    public Task<bool> UploadCompletedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => _messageTracking.UploadCompletedMessage(eventId, sessionId, endpointId, content);
    public Task<bool> TryCompletePendingMessage(string eventId, string sessionId, string endpointId, string? expectedLastMessageId, UnresolvedEvent content) => _messageTracking.TryCompletePendingMessage(eventId, sessionId, endpointId, expectedLastMessageId, content);
    public Task<bool> TrySkipDeferredMessage(string eventId, string sessionId, string endpointId, string? expectedLastMessageId, DateTime expectedUpdatedAt) => _messageTracking.TrySkipDeferredMessage(eventId, sessionId, endpointId, expectedLastMessageId, expectedUpdatedAt);
    public Task<bool> TryArchiveUnresolvedEvent(string eventId, string sessionId, string endpointId, ResolutionStatus expectedStatus, string? expectedLastMessageId, DateTime expectedUpdatedAt) => _messageTracking.TryArchiveUnresolvedEvent(eventId, sessionId, endpointId, expectedStatus, expectedLastMessageId, expectedUpdatedAt);
    public Task<bool> TryRestoreArchivedEvent(string eventId, string sessionId, string endpointId, ResolutionStatus expectedStatus, string? expectedLastMessageId, DateTime expectedUpdatedAt) => _messageTracking.TryRestoreArchivedEvent(eventId, sessionId, endpointId, expectedStatus, expectedLastMessageId, expectedUpdatedAt);
    public Task<bool> RemoveMessage(string eventId, string sessionId, string endpointId) => _messageTracking.RemoveMessage(eventId, sessionId, endpointId);
    public Task<bool> PurgeMessages(string endpointId, string sessionId) => _messageTracking.PurgeMessages(endpointId, sessionId);
    public Task<bool> PurgeMessages(string endpointId) => _messageTracking.PurgeMessages(endpointId);
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
    public Task<BlockedMessageEventPage> GetBlockedEventsOnSession(string endpointId, string sessionId, int skip, int take) => _messageTracking.GetBlockedEventsOnSession(endpointId, sessionId, skip, take);
    public Task<IEnumerable<UnresolvedEvent>?> GetPendingEventsOnSession(string endpointId) => _messageTracking.GetPendingEventsOnSession(endpointId);
    public Task<IEnumerable<BlockedMessageEvent>> GetInvalidEventsOnSession(string endpointId) => _messageTracking.GetInvalidEventsOnSession(endpointId);
    public Task<MessageSearchResult> SearchMessages(MessageFilter filter, string? continuationToken, int maxItemCount) => _messageTracking.SearchMessages(filter, continuationToken, maxItemCount);
    public Task StoreMessage(MessageEntity message) => _messageTracking.StoreMessage(message);
    public Task RemoveStoredMessage(string eventId, string messageId) => _messageTracking.RemoveStoredMessage(eventId, messageId);
    public Task<int> PurgeStoredMessages(string endpointId) => _messageTracking.PurgeStoredMessages(endpointId);
    public Task<MessageEntity?> GetMessage(string eventId, string messageId) => _messageTracking.GetMessage(eventId, messageId);
    public Task<IEnumerable<MessageEntity>> GetEventHistory(string eventId) => _messageTracking.GetEventHistory(eventId);
    public Task<MessageEntity?> GetLatestEventRequestMessage(string eventId) => _messageTracking.GetLatestEventRequestMessage(eventId);
    public Task<MessageEntity?> GetFailedMessage(string eventId, string endpointId) => _messageTracking.GetFailedMessage(eventId, endpointId);
    public Task<MessageEntity?> GetDeadletteredMessage(string eventId, string endpointId) => _messageTracking.GetDeadletteredMessage(eventId, endpointId);
    public Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null) => _messageTracking.StoreMessageAudit(eventId, auditEntity, endpointId, eventTypeId);
    public Task<IEnumerable<MessageAuditEntity>> GetMessageAudits(string eventId) => _messageTracking.GetMessageAudits(eventId);
    public Task<AuditSearchResult> SearchAudits(AuditFilter filter, string? continuationToken, int maxItemCount) => _messageTracking.SearchAudits(filter, continuationToken, maxItemCount);
    public Task<IReadOnlyDictionary<string, int>> GetResubmitCounts(string endpointId, IReadOnlyCollection<string> eventIds) => _messageTracking.GetResubmitCounts(endpointId, eventIds);
    public Task SetEventReport(string endpointId, string eventId, bool isReported, string? reportedBy, string? ticketId) => _messageTracking.SetEventReport(endpointId, eventId, isReported, reportedBy, ticketId);
    public Task<IReadOnlyDictionary<string, EventReport>> GetEventReports(string endpointId, IReadOnlyCollection<string> eventIds) => _messageTracking.GetEventReports(endpointId, eventIds);
    public Task ArchiveFailedEvent(string eventId, string sessionId, string endpointId) => _messageTracking.ArchiveFailedEvent(eventId, sessionId, endpointId);
    public Task<string> GetEndpointErrorList(string endpointId) => _messageTracking.GetEndpointErrorList(endpointId);

    internal const string MessageSearchProjection = CosmosDbMessageTrackingStore.MessageSearchProjection;
    // ── IEndpointMetadataStore — implementation in CosmosDbEndpointMetadataStore ──
    public Task<EndpointMetadata?> GetEndpointMetadata(string endpointId) => _endpointMetadata.GetEndpointMetadata(endpointId);
    public Task<List<EndpointMetadata>?> GetMetadatas(IEnumerable<string> endpointIds) => _endpointMetadata.GetMetadatas(endpointIds);
    public Task<List<EndpointMetadata>> GetMetadatas() => _endpointMetadata.GetMetadatas();
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
    // ── Durable endpoint heartbeat history — implementation in CosmosDbHeartbeatHistoryStore ──
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
    // ── Service health — implementation in CosmosDbServiceHealthStore ──
    public Task<List<ServiceHealth>> GetServiceHealth() => _serviceHealth.GetServiceHealth();

    public Task<bool> TryClaimServiceProbe(string serviceId, DateTime dueBefore, string probeMessageId)
        => _serviceHealth.TryClaimServiceProbe(serviceId, dueBefore, probeMessageId);

    public Task<bool> SetServiceHealth(ServiceHealth serviceHealth)
        => _serviceHealth.SetServiceHealth(serviceHealth);

    public Task<List<string>> SweepTimedOutServiceProbes(DateTime cutoffUtc)
        => _serviceHealth.SweepTimedOutServiceProbes(cutoffUtc);

    // ── Monitor acknowledgements — implementation in CosmosDbEndpointAcknowledgementStore ──
    public Task<IReadOnlyList<EndpointAcknowledgement>> GetEndpointAcknowledgements() => _acknowledgements.GetEndpointAcknowledgements();

    public Task SetEndpointAcknowledgement(EndpointAcknowledgement acknowledgement) => _acknowledgements.SetEndpointAcknowledgement(acknowledgement);

    public Task<bool> RemoveEndpointAcknowledgement(string endpointId, string? expectedAcknowledgementId = null)
        => _acknowledgements.RemoveEndpointAcknowledgement(endpointId, expectedAcknowledgementId);
    // ── IMetricsStore — implementation in CosmosDbMetricsStore ──

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

}
