using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Options;
using NimBus.Core.Diagnostics;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.OpenTelemetry.Instrumentation;

/// <summary>
/// Pass-through decorator that times every <see cref="IMessageTrackingStore"/>
/// method, records <c>nimbus.store.operation.duration</c> with
/// <c>nimbus.store.operation</c> + <c>nimbus.store.provider</c> tags, and on
/// exception increments <c>nimbus.store.operation.failed</c> tagged additionally
/// with <c>error.type</c>. Per-operation spans are emitted only when
/// <see cref="NimBusOpenTelemetryOptions.Verbose"/> is <c>true</c> (per FR-010 /
/// FR-055): non-verbose installs cover the store via metrics alone.
/// </summary>
internal sealed class InstrumentingMessageTrackingStoreDecorator : IMessageTrackingStore
{
    private readonly IMessageTrackingStore _inner;
    private readonly string _provider;
    private readonly IOptionsMonitor<NimBusOpenTelemetryOptions>? _options;

    public InstrumentingMessageTrackingStoreDecorator(
        IMessageTrackingStore inner,
        string provider,
        IOptionsMonitor<NimBusOpenTelemetryOptions>? options = null)
    {
        _inner = inner ?? throw new System.ArgumentNullException(nameof(inner));
        _provider = provider ?? throw new System.ArgumentNullException(nameof(provider));
        _options = options;
    }

    private bool VerboseEnabled => _options?.CurrentValue.Verbose ?? false;

    private async Task<T> InstrumentAsync<T>(string operation, System.Func<Task<T>> inner)
    {
        Activity? activity = null;
        if (VerboseEnabled)
        {
            activity = NimBusActivitySources.Store.StartActivity(
                $"NimBus.Store.{operation}", ActivityKind.Internal);
            if (activity is not null)
            {
                activity.SetTag(MessagingAttributes.NimBusStoreOperation, operation);
                activity.SetTag(MessagingAttributes.NimBusStoreProvider, _provider);
            }
        }

        var startTimestamp = Stopwatch.GetTimestamp();
        string? errorType = null;
        try
        {
            var result = await inner().ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return result;
        }
        catch (System.Exception ex)
        {
            errorType = ex.GetType().FullName;
            if (activity is not null)
            {
                activity.SetStatus(ActivityStatusCode.Error, ex.Message);
                activity.SetTag(MessagingAttributes.ErrorType, errorType);
            }
            NimBusMeters.StoreOperationFailed.Add(1, BuildTags(operation, errorType));
            throw;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;
            NimBusMeters.StoreOperationDuration.Record(elapsed, BuildTags(operation, errorType));
            activity?.Dispose();
        }
    }

    private async Task InstrumentAsync(string operation, System.Func<Task> inner)
    {
        await InstrumentAsync<object?>(operation, async () =>
        {
            await inner().ConfigureAwait(false);
            return null;
        }).ConfigureAwait(false);
    }

    private KeyValuePair<string, object?>[] BuildTags(string operation, string? errorType)
    {
        if (errorType is null)
            return new[]
            {
                new KeyValuePair<string, object?>(MessagingAttributes.NimBusStoreOperation, operation),
                new KeyValuePair<string, object?>(MessagingAttributes.NimBusStoreProvider, _provider),
            };
        return new[]
        {
            new KeyValuePair<string, object?>(MessagingAttributes.NimBusStoreOperation, operation),
            new KeyValuePair<string, object?>(MessagingAttributes.NimBusStoreProvider, _provider),
            new KeyValuePair<string, object?>(MessagingAttributes.ErrorType, errorType),
        };
    }

    // ── Status transition uploads ───────────────────────────────────────

    public Task<bool> UploadPendingMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) =>
        InstrumentAsync(nameof(UploadPendingMessage), () => _inner.UploadPendingMessage(eventId, sessionId, endpointId, content));

    public Task<bool> UploadDeferredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) =>
        InstrumentAsync(nameof(UploadDeferredMessage), () => _inner.UploadDeferredMessage(eventId, sessionId, endpointId, content));

    public Task<bool> UploadFailedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) =>
        InstrumentAsync(nameof(UploadFailedMessage), () => _inner.UploadFailedMessage(eventId, sessionId, endpointId, content));

    public Task<bool> UploadDeadletteredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) =>
        InstrumentAsync(nameof(UploadDeadletteredMessage), () => _inner.UploadDeadletteredMessage(eventId, sessionId, endpointId, content));

    public Task<bool> UploadUnsupportedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) =>
        InstrumentAsync(nameof(UploadUnsupportedMessage), () => _inner.UploadUnsupportedMessage(eventId, sessionId, endpointId, content));

    public Task<bool> UploadSkippedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) =>
        InstrumentAsync(nameof(UploadSkippedMessage), () => _inner.UploadSkippedMessage(eventId, sessionId, endpointId, content));

    public Task<bool> UploadCompletedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) =>
        InstrumentAsync(nameof(UploadCompletedMessage), () => _inner.UploadCompletedMessage(eventId, sessionId, endpointId, content));

    public Task<bool> TryCompletePendingMessage(string eventId, string sessionId, string endpointId, string? expectedLastMessageId, UnresolvedEvent content) =>
        InstrumentAsync(nameof(TryCompletePendingMessage), () => _inner.TryCompletePendingMessage(eventId, sessionId, endpointId, expectedLastMessageId, content));

    public Task<bool> TrySkipDeferredMessage(string eventId, string sessionId, string endpointId, string? expectedLastMessageId, DateTime expectedUpdatedAt) =>
        InstrumentAsync(nameof(TrySkipDeferredMessage), () => _inner.TrySkipDeferredMessage(eventId, sessionId, endpointId, expectedLastMessageId, expectedUpdatedAt));

    public Task<bool> TryArchiveUnresolvedEvent(string eventId, string sessionId, string endpointId, ResolutionStatus expectedStatus, string? expectedLastMessageId, DateTime expectedUpdatedAt) =>
        InstrumentAsync(nameof(TryArchiveUnresolvedEvent), () => _inner.TryArchiveUnresolvedEvent(eventId, sessionId, endpointId, expectedStatus, expectedLastMessageId, expectedUpdatedAt));

    public Task<bool> TryRestoreArchivedEvent(string eventId, string sessionId, string endpointId, ResolutionStatus expectedStatus, string? expectedLastMessageId, DateTime expectedUpdatedAt) =>
        InstrumentAsync(nameof(TryRestoreArchivedEvent), () => _inner.TryRestoreArchivedEvent(eventId, sessionId, endpointId, expectedStatus, expectedLastMessageId, expectedUpdatedAt));

    // ── Single-event lookups ────────────────────────────────────────────

    public Task<UnresolvedEvent?> GetPendingEvent(string endpointId, string eventId, string sessionId) =>
        InstrumentAsync(nameof(GetPendingEvent), () => _inner.GetPendingEvent(endpointId, eventId, sessionId));

    public Task<UnresolvedEvent?> GetPendingHandoffByExternalJobId(string endpointId, string externalJobId, CancellationToken cancellationToken = default) =>
        InstrumentAsync(nameof(GetPendingHandoffByExternalJobId), () => _inner.GetPendingHandoffByExternalJobId(endpointId, externalJobId, cancellationToken));

    public Task<UnresolvedEvent?> GetNextPendingHandoffEvent(string endpointId, IReadOnlyCollection<string>? eventTypeIds) =>
        InstrumentAsync(nameof(GetNextPendingHandoffEvent), () => _inner.GetNextPendingHandoffEvent(endpointId, eventTypeIds));

    public Task<UnresolvedEvent?> GetFailedEvent(string endpointId, string eventId, string sessionId) =>
        InstrumentAsync(nameof(GetFailedEvent), () => _inner.GetFailedEvent(endpointId, eventId, sessionId));

    public Task<UnresolvedEvent?> GetDeferredEvent(string endpointId, string eventId, string sessionId) =>
        InstrumentAsync(nameof(GetDeferredEvent), () => _inner.GetDeferredEvent(endpointId, eventId, sessionId));

    public Task<UnresolvedEvent?> GetDeadletteredEvent(string endpointId, string eventId, string sessionId) =>
        InstrumentAsync(nameof(GetDeadletteredEvent), () => _inner.GetDeadletteredEvent(endpointId, eventId, sessionId));

    public Task<UnresolvedEvent?> GetUnsupportedEvent(string endpointId, string eventId, string sessionId) =>
        InstrumentAsync(nameof(GetUnsupportedEvent), () => _inner.GetUnsupportedEvent(endpointId, eventId, sessionId));

    public Task<UnresolvedEvent?> GetEvent(string endpointId, string eventId) =>
        InstrumentAsync(nameof(GetEvent), () => _inner.GetEvent(endpointId, eventId));

    public Task<UnresolvedEvent?> GetEventById(string endpointId, string id) =>
        InstrumentAsync(nameof(GetEventById), () => _inner.GetEventById(endpointId, id));

    public Task<List<UnresolvedEvent>> GetEventsByIds(string endpointId, IEnumerable<string> eventIds) =>
        InstrumentAsync(nameof(GetEventsByIds), () => _inner.GetEventsByIds(endpointId, eventIds));

    public Task<IEnumerable<UnresolvedEvent>> GetCompletedEventsOnEndpoint(string endpointId) =>
        InstrumentAsync(nameof(GetCompletedEventsOnEndpoint), () => _inner.GetCompletedEventsOnEndpoint(endpointId));

    // ── Filtered queries ────────────────────────────────────────────────

    public Task<SearchResponse> GetEventsByFilter(EventFilter filter, string continuationToken, int maxSearchItemsCount) =>
        InstrumentAsync(nameof(GetEventsByFilter), () => _inner.GetEventsByFilter(filter, continuationToken, maxSearchItemsCount));

    public Task<SearchResponse> GetFailedEventsAcrossEndpoints(EventFilter filter, IReadOnlyCollection<string> endpointIds, string? continuationToken, int maxItemCount) =>
        InstrumentAsync(nameof(GetFailedEventsAcrossEndpoints), () => _inner.GetFailedEventsAcrossEndpoints(filter, endpointIds, continuationToken, maxItemCount));

    public Task<FailedEventHistogram> GetFailedEventHistogram(EventFilter filter, IReadOnlyCollection<string> endpointIds, DateTime fromUtc, DateTime toUtc, TimeSpan bucketSize) =>
        InstrumentAsync(nameof(GetFailedEventHistogram), () => _inner.GetFailedEventHistogram(filter, endpointIds, fromUtc, toUtc, bucketSize));

    // ── State counts ────────────────────────────────────────────────────

    public Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId) =>
        InstrumentAsync(nameof(DownloadEndpointStateCount), () => _inner.DownloadEndpointStateCount(endpointId));

    public Task<SessionStateCount> DownloadEndpointSessionStateCount(string endpointId, string sessionId) =>
        InstrumentAsync(nameof(DownloadEndpointSessionStateCount), () => _inner.DownloadEndpointSessionStateCount(endpointId, sessionId));

    public Task<IEnumerable<SessionStateCount>> DownloadEndpointSessionStateCountBatch(string endpointId, IEnumerable<string> sessionIds) =>
        InstrumentAsync(nameof(DownloadEndpointSessionStateCountBatch), () => _inner.DownloadEndpointSessionStateCountBatch(endpointId, sessionIds));

    public Task<EndpointState?> DownloadEndpointStatePaging(string endpointId, int pageSize, string continuationToken) =>
        InstrumentAsync(nameof(DownloadEndpointStatePaging), () => _inner.DownloadEndpointStatePaging(endpointId, pageSize, continuationToken));

    // ── Session views ───────────────────────────────────────────────────

    public Task<BlockedMessageEventPage> GetBlockedEventsOnSession(string endpointId, string sessionId, int skip, int take) =>
        InstrumentAsync(nameof(GetBlockedEventsOnSession), () => _inner.GetBlockedEventsOnSession(endpointId, sessionId, skip, take));

    public Task<IEnumerable<UnresolvedEvent>?> GetPendingEventsOnSession(string endpointId) =>
        InstrumentAsync(nameof(GetPendingEventsOnSession), () => _inner.GetPendingEventsOnSession(endpointId));

    public Task<IEnumerable<BlockedMessageEvent>> GetInvalidEventsOnSession(string endpointId) =>
        InstrumentAsync(nameof(GetInvalidEventsOnSession), () => _inner.GetInvalidEventsOnSession(endpointId));

    // ── Lifecycle / cleanup ─────────────────────────────────────────────

    public Task<bool> RemoveMessage(string eventId, string sessionId, string endpointId) =>
        InstrumentAsync(nameof(RemoveMessage), () => _inner.RemoveMessage(eventId, sessionId, endpointId));

    public Task<bool> PurgeMessages(string endpointId, string sessionId) =>
        InstrumentAsync(nameof(PurgeMessages), () => _inner.PurgeMessages(endpointId, sessionId));

    public Task<bool> PurgeMessages(string endpointId) =>
        InstrumentAsync(nameof(PurgeMessages), () => _inner.PurgeMessages(endpointId));

    public Task ArchiveFailedEvent(string eventId, string sessionId, string endpointId) =>
        InstrumentAsync(nameof(ArchiveFailedEvent), () => _inner.ArchiveFailedEvent(eventId, sessionId, endpointId));

    // ── Per-message history records ─────────────────────────────────────

    public Task StoreMessage(MessageEntity message) =>
        InstrumentAsync(nameof(StoreMessage), () => _inner.StoreMessage(message));

    public Task<MessageEntity?> GetMessage(string eventId, string messageId) =>
        InstrumentAsync(nameof(GetMessage), () => _inner.GetMessage(eventId, messageId));

    public Task<IEnumerable<MessageEntity>> GetEventHistory(string eventId) =>
        InstrumentAsync(nameof(GetEventHistory), () => _inner.GetEventHistory(eventId));

    public Task<MessageEntity?> GetLatestEventRequestMessage(string eventId) =>
        InstrumentAsync(nameof(GetLatestEventRequestMessage), () => _inner.GetLatestEventRequestMessage(eventId));

    public Task<MessageEntity?> GetFailedMessage(string eventId, string endpointId) =>
        InstrumentAsync(nameof(GetFailedMessage), () => _inner.GetFailedMessage(eventId, endpointId));

    public Task<MessageEntity?> GetDeadletteredMessage(string eventId, string endpointId) =>
        InstrumentAsync(nameof(GetDeadletteredMessage), () => _inner.GetDeadletteredMessage(eventId, endpointId));

    public Task RemoveStoredMessage(string eventId, string messageId) =>
        InstrumentAsync(nameof(RemoveStoredMessage), () => _inner.RemoveStoredMessage(eventId, messageId));

    public Task<int> PurgeStoredMessages(string endpointId) =>
        InstrumentAsync(nameof(PurgeStoredMessages), () => _inner.PurgeStoredMessages(endpointId));

    public Task<MessageSearchResult> SearchMessages(MessageFilter filter, string? continuationToken, int maxItemCount) =>
        InstrumentAsync(nameof(SearchMessages), () => _inner.SearchMessages(filter, continuationToken, maxItemCount));

    // ── Audit trail ─────────────────────────────────────────────────────

    public Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null) =>
        InstrumentAsync(nameof(StoreMessageAudit), () => _inner.StoreMessageAudit(eventId, auditEntity, endpointId, eventTypeId));

    public Task<IEnumerable<MessageAuditEntity>> GetMessageAudits(string eventId) =>
        InstrumentAsync(nameof(GetMessageAudits), () => _inner.GetMessageAudits(eventId));

    public Task<AuditSearchResult> SearchAudits(AuditFilter filter, string? continuationToken, int maxItemCount) =>
        InstrumentAsync(nameof(SearchAudits), () => _inner.SearchAudits(filter, continuationToken, maxItemCount));

    public Task<IReadOnlyDictionary<string, int>> GetResubmitCounts(string endpointId, IReadOnlyCollection<string> eventIds) =>
        InstrumentAsync(nameof(GetResubmitCounts), () => _inner.GetResubmitCounts(endpointId, eventIds));

    public Task SetEventReport(string endpointId, string eventId, bool isReported, string? reportedBy, string? ticketId) =>
        InstrumentAsync(nameof(SetEventReport), () => _inner.SetEventReport(endpointId, eventId, isReported, reportedBy, ticketId));

    public Task<IReadOnlyDictionary<string, EventReport>> GetEventReports(string endpointId, IReadOnlyCollection<string> eventIds) =>
        InstrumentAsync(nameof(GetEventReports), () => _inner.GetEventReports(endpointId, eventIds));

    // ── Endpoint diagnostics ────────────────────────────────────────────

    public Task<string> GetEndpointErrorList(string endpointId) =>
        InstrumentAsync(nameof(GetEndpointErrorList), () => _inner.GetEndpointErrorList(endpointId));
}
