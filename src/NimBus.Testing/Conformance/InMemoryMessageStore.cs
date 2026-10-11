using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Pure in-memory implementation of <see cref="INimBusMessageStore"/>. Intended for
/// the conformance suite (where it serves as the always-green reference) and for
/// unit tests that need a working store without provisioning Cosmos or SQL Server.
/// Thread-safe via <see cref="ConcurrentDictionary{TKey, TValue}"/>.
///
/// Most methods are minimum-viable: enough surface to exercise the resolver state
/// transitions, message persistence, and basic queries the conformance suite cares
/// about. Search/pagination methods return empty results.
/// </summary>
public class InMemoryMessageStore : INimBusMessageStore, IHeartbeatHistoryStore
{
    private readonly ConcurrentDictionary<(string Endpoint, string EventId, string Session), UnresolvedEvent> _events = new();
    // Rows archived by TryArchiveUnresolvedEvent, kept so TryRestoreArchivedEvent can put them back.
    private readonly ConcurrentDictionary<(string Endpoint, string EventId, string Session), UnresolvedEvent> _archived = new();
    private readonly ConcurrentDictionary<(string EventId, string MessageId), MessageEntity> _messages = new();
    private readonly ConcurrentDictionary<string, List<MessageAuditEntity>> _audits = new();
    private readonly ConcurrentDictionary<string, EndpointSubscription> _subscriptions = new();
    // Tuple key, not string concatenation — ("a_b","c") and ("a","b_c") must
    // not collide.
    private readonly ConcurrentDictionary<(string EndpointId, string EventId), EventReport> _eventReports = new();
    private readonly ConcurrentDictionary<string, EndpointMetadata> _metadata = new();
    private readonly ConcurrentDictionary<string, EventSchema> _schemas = new();
    private readonly ConcurrentDictionary<string, AccessControlList> _accessControls = new();
    private readonly ConcurrentDictionary<string, EndpointAcknowledgement> _acknowledgements = new(StringComparer.Ordinal);
    // Heartbeat state is read-modify-write across several records (rows, rollup,
    // claims), so it is guarded by one lock rather than by concurrent collections:
    // the claim methods are only atomic if the read and the write are one step.
    private readonly Dictionary<string, ServiceHealth> _serviceHealth = new(StringComparer.Ordinal);
    private readonly Dictionary<(string EndpointId, DateTime DayUtc), HeartbeatUptimeDay> _heartbeatUptimeDays = new();
    private readonly Dictionary<(string EndpointId, DateTime FromUtc), HeartbeatGap> _heartbeatGaps = new();
    private readonly object _heartbeatLock = new();
    private HeartbeatSettings? _heartbeatSettings;
    // Replaced whole on every write, so a volatile reference swap is enough.
    private volatile AuditSettings? _auditSettings;
    // Compare-and-set on the revision, so the read and the write share one lock.
    private readonly object _mcpAccessLock = new();
    private McpAccessSettings? _mcpAccessSettings;
    private readonly object _agentApiKeysLock = new();
    private AgentApiKeySettings? _agentApiKeySettings;
    private readonly ConcurrentDictionary<string, DateTime> _agentApiKeyUsage = new(StringComparer.Ordinal);

    private (string, string, string) Key(string endpoint, string eventId, string session) => (endpoint, eventId, session ?? string.Empty);

    private Task<bool> Upsert(string eventId, string sessionId, string endpointId, ResolutionStatus status, UnresolvedEvent content)
    {
        // Decide against the stored row BEFORE stamping the caller's object, and report the
        // factory's own decision rather than comparing references: passing the same instance
        // twice must not be able to bypass StaleWriteGuard. Parity with Cosmos and SQL, which
        // evaluate the same rule atomically against the row they are about to replace.
        //
        // AddOrUpdate may invoke a factory more than once under contention, and only the last
        // invocation's value is committed — so both branches assign the flag unconditionally and
        // the winning call is the one that decides. Setting it only on the applied branch would
        // report a refusal as applied after a lost CAS.
        var applied = false;

        UnresolvedEvent Stamp()
        {
            content.ResolutionStatus = status;
            content.UpdatedAt = DateTime.UtcNow;
            content.EndpointId = endpointId;
            content.EventId = eventId;
            content.SessionId = sessionId;
            applied = true;
            return content;
        }

        _events.AddOrUpdate(
            Key(endpointId, eventId, sessionId),
            _ => Stamp(),
            (_, existing) =>
            {
                if (StaleWriteGuard.Allows(status, content, existing))
                {
                    return Stamp();
                }

                applied = false;
                return existing;
            });

        return Task.FromResult(applied);
    }

    public Task<bool> UploadPendingMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Upsert(eventId, sessionId, endpointId, ResolutionStatus.Pending, content);
    public Task<bool> UploadDeferredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Upsert(eventId, sessionId, endpointId, ResolutionStatus.Deferred, content);
    public Task<bool> UploadFailedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Upsert(eventId, sessionId, endpointId, ResolutionStatus.Failed, content);
    public Task<bool> UploadDeadletteredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Upsert(eventId, sessionId, endpointId, ResolutionStatus.DeadLettered, content);
    public Task<bool> UploadUnsupportedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Upsert(eventId, sessionId, endpointId, ResolutionStatus.Unsupported, content);
    public Task<bool> UploadSkippedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Upsert(eventId, sessionId, endpointId, ResolutionStatus.Skipped, content);
    public Task<bool> UploadCompletedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Upsert(eventId, sessionId, endpointId, ResolutionStatus.Completed, content);

    public Task<bool> TrySkipDeferredMessage(string eventId, string sessionId, string endpointId,
        string? expectedLastMessageId, DateTime expectedUpdatedAt)
    {
        var key = Key(endpointId, eventId, sessionId);
        if (!_events.TryGetValue(key, out var current)
            || current.ResolutionStatus != ResolutionStatus.Deferred
            || current.UpdatedAt != expectedUpdatedAt
            || !string.Equals(current.LastMessageId, expectedLastMessageId, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        var replacement = Clone(current);
        replacement.ResolutionStatus = ResolutionStatus.Skipped;
        replacement.UpdatedAt = DateTime.UtcNow;
        return Task.FromResult(_events.TryUpdate(key, replacement, current));
    }

    public Task<bool> TryArchiveUnresolvedEvent(string eventId, string sessionId, string endpointId,
        ResolutionStatus expectedStatus, string? expectedLastMessageId, DateTime expectedUpdatedAt)
    {
        var key = Key(endpointId, eventId, sessionId);
        if (!_events.TryGetValue(key, out var current)
            || !MatchesVersion(current, expectedStatus, expectedLastMessageId, expectedUpdatedAt)
            || !_events.TryRemove(KeyValuePair.Create(key, current)))
        {
            return Task.FromResult(false);
        }

        _archived[key] = current;
        return Task.FromResult(true);
    }

    public Task<bool> TryRestoreArchivedEvent(string eventId, string sessionId, string endpointId,
        ResolutionStatus expectedStatus, string? expectedLastMessageId, DateTime expectedUpdatedAt)
    {
        var key = Key(endpointId, eventId, sessionId);
        // A live row means a newer write revived it; restoring the archived copy would clobber it.
        if (!_archived.TryGetValue(key, out var archived)
            || !MatchesVersion(archived, expectedStatus, expectedLastMessageId, expectedUpdatedAt)
            || !_events.TryAdd(key, archived))
        {
            return Task.FromResult(false);
        }

        _archived.TryRemove(KeyValuePair.Create(key, archived));
        return Task.FromResult(true);
    }

    private static bool MatchesVersion(UnresolvedEvent row, ResolutionStatus expectedStatus,
        string? expectedLastMessageId, DateTime expectedUpdatedAt) =>
        row.ResolutionStatus == expectedStatus
        && row.UpdatedAt == expectedUpdatedAt
        && string.Equals(row.LastMessageId, expectedLastMessageId, StringComparison.Ordinal);

    public Task<bool> TryCompletePendingMessage(
        string eventId,
        string sessionId,
        string endpointId,
        string? expectedLastMessageId,
        UnresolvedEvent content)
    {
        var key = Key(endpointId, eventId, sessionId);
        if (!_events.TryGetValue(key, out var existing)
            || existing.ResolutionStatus != ResolutionStatus.Pending
            || !string.Equals(existing.LastMessageId, expectedLastMessageId, StringComparison.Ordinal))
        {
            return Task.FromResult(false);
        }

        // Unlike the Upsert path, this one stamps a COPY: Cosmos and SQL Server leave the caller's
        // content untouched when they refuse, and a caller must not be able to tell the providers
        // apart by inspecting its own projection after a lost compare-and-swap.
        var replacement = Clone(content);
        replacement.ResolutionStatus = ResolutionStatus.Completed;
        replacement.UpdatedAt = DateTime.UtcNow;
        replacement.EndpointId = endpointId;
        replacement.EventId = eventId;
        replacement.SessionId = sessionId;
        return Task.FromResult(_events.TryUpdate(key, replacement, existing));
    }

    public Task<UnresolvedEvent?> GetPendingEvent(string endpointId, string eventId, string sessionId) => GetByStatus(endpointId, eventId, sessionId, ResolutionStatus.Pending);

    public Task<UnresolvedEvent?> GetPendingHandoffByExternalJobId(string endpointId, string externalJobId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(externalJobId)) return Task.FromResult<UnresolvedEvent?>(null);
        var match = _events.Values.FirstOrDefault(e =>
            e.EndpointId == endpointId
            && e.ResolutionStatus == ResolutionStatus.Pending
            && e.PendingSubStatus == "Handoff"
            && e.ExternalJobId == externalJobId);
        return Task.FromResult(match);
    }

    public virtual Task<UnresolvedEvent?> GetNextPendingHandoffEvent(string endpointId, IReadOnlyCollection<string>? eventTypeIds)
    {
        var hasFilter = eventTypeIds is { Count: > 0 };
        var match = _events.Values
            .Where(e => e.EndpointId == endpointId
                && e.ResolutionStatus == ResolutionStatus.Pending
                && e.PendingSubStatus == "Handoff"
                && (!hasFilter || eventTypeIds!.Contains(e.EventTypeId)))
            .OrderBy(e => e.EnqueuedTimeUtc)
            .FirstOrDefault();
        return Task.FromResult<UnresolvedEvent?>(match);
    }
    public Task<UnresolvedEvent?> GetFailedEvent(string endpointId, string eventId, string sessionId) => GetByStatus(endpointId, eventId, sessionId, ResolutionStatus.Failed);
    public Task<UnresolvedEvent?> GetDeferredEvent(string endpointId, string eventId, string sessionId) => GetByStatus(endpointId, eventId, sessionId, ResolutionStatus.Deferred);
    public Task<UnresolvedEvent?> GetDeadletteredEvent(string endpointId, string eventId, string sessionId) => GetByStatus(endpointId, eventId, sessionId, ResolutionStatus.DeadLettered);
    public Task<UnresolvedEvent?> GetUnsupportedEvent(string endpointId, string eventId, string sessionId) => GetByStatus(endpointId, eventId, sessionId, ResolutionStatus.Unsupported);

    private Task<UnresolvedEvent?> GetByStatus(string endpointId, string eventId, string sessionId, ResolutionStatus status)
        => Task.FromResult(_events.Values.FirstOrDefault(e => e.EndpointId == endpointId && e.EventId == eventId && (e.SessionId ?? string.Empty) == (sessionId ?? string.Empty) && e.ResolutionStatus == status));

    public virtual Task<UnresolvedEvent?> GetEvent(string endpointId, string eventId)
        => Task.FromResult(_events.Values.Where(e => e.EndpointId == endpointId && e.EventId == eventId).OrderByDescending(e => e.UpdatedAt).FirstOrDefault());

    public Task<UnresolvedEvent?> GetEventById(string endpointId, string id)
        => Task.FromResult(_events.Values.FirstOrDefault(e => e.EndpointId == endpointId && CompositeEventId(e) == id));

    public Task<List<UnresolvedEvent>> GetEventsByIds(string endpointId, IEnumerable<string> eventIds)
    {
        var set = new HashSet<string>(eventIds);
        return Task.FromResult(_events.Values.Where(e => e.EndpointId == endpointId && (set.Contains(e.EventId) || set.Contains(CompositeEventId(e)))).ToList());
    }

    public Task<IEnumerable<UnresolvedEvent>> GetCompletedEventsOnEndpoint(string endpointId)
        => Task.FromResult<IEnumerable<UnresolvedEvent>>(_events.Values.Where(e => e.EndpointId == endpointId && e.ResolutionStatus == ResolutionStatus.Completed).ToList());

    public virtual Task<SearchResponse> GetEventsByFilter(EventFilter filter, string continuationToken, int maxSearchItemsCount)
    {
        var q = ApplyEventFilter(_events.Values, filter);
        if (filter.ResolutionStatus is { Count: > 0 })
        {
            var statuses = new HashSet<string>(filter.ResolutionStatus);
            q = q.Where(e => statuses.Contains(e.ResolutionStatus.ToString()));
        }
        // Search results never surface the full request payload (cross-provider
        // contract). This store hands out its STORED instances elsewhere, so the
        // results must be clones — nulling EventJson in place would corrupt the
        // stored event.
        var results = q.OrderByDescending(e => e.UpdatedAt)
            .Take(PaginationLimits.Resolve(maxSearchItemsCount))
            .Select(CloneWithoutEventJson)
            .ToList();
        return Task.FromResult(new SearchResponse { Events = results });
    }

    public virtual Task<SearchResponse> GetFailedEventsAcrossEndpoints(
        EventFilter filter,
        IReadOnlyCollection<string> endpointIds,
        string? continuationToken,
        int maxItemCount)
    {
        var pageSize = PaginationLimits.Resolve(maxItemCount);
        var offset = int.TryParse(continuationToken, out var parsed) && parsed > 0 ? parsed : 0;
        var page = FailedEventsQuery(filter, endpointIds)
            .OrderByDescending(e => e.UpdatedAt)
            .ThenByDescending(e => e.EndpointId, StringComparer.Ordinal)
            .ThenByDescending(CompositeEventId, StringComparer.Ordinal)
            .Skip(offset)
            .Take(pageSize + 1)
            .ToList();
        var hasMore = page.Count > pageSize;
        return Task.FromResult(new SearchResponse
        {
            Events = page.Take(pageSize).Select(CloneWithoutEventJson).ToList(),
            ContinuationToken = hasMore ? (offset + pageSize).ToString(System.Globalization.CultureInfo.InvariantCulture) : null!,
        });
    }

    public virtual Task<FailedEventHistogram> GetFailedEventHistogram(
        EventFilter filter,
        IReadOnlyCollection<string> endpointIds,
        DateTime fromUtc,
        DateTime toUtc,
        TimeSpan bucketSize)
    {
        var observations = FailedEventsQuery(filter, endpointIds)
            .Where(e => e.UpdatedAt >= fromUtc && e.UpdatedAt < toUtc)
            .Select(e => (e.UpdatedAt, e.EndpointId, e.ResolutionStatus.ToString()))
            .ToList();
        return Task.FromResult(new FailedEventHistogram
        {
            Rows = FailedEventQuery.Bucket(observations, fromUtc, bucketSize),
        });
    }

    private IEnumerable<UnresolvedEvent> FailedEventsQuery(EventFilter filter, IReadOnlyCollection<string> endpointIds)
    {
        var endpoints = new HashSet<string>(endpointIds, StringComparer.Ordinal);
        var statuses = new HashSet<string>(FailedEventQuery.ResolveStatuses(filter.ResolutionStatus));
        return ApplyEventFilter(_events.Values, filter)
            .Where(e => endpoints.Contains(e.EndpointId) && statuses.Contains(e.ResolutionStatus.ToString()));
    }

    // Shared by every event search so the filters cannot drift apart. ID-like fields use
    // case-insensitive PREFIX matching, converging with the Cosmos (STARTSWITH) and SQL Server
    // (LIKE 'value%') providers; free-text fields use case-insensitive substring matching.
    // The endpoint id is the exception: an exact match, so "Billing" never returns "BillingV2".
    private static IEnumerable<UnresolvedEvent> ApplyEventFilter(IEnumerable<UnresolvedEvent> q, EventFilter filter)
    {
        if (!string.IsNullOrEmpty(filter.EndPointId)) q = q.Where(e => string.Equals(e.EndpointId, filter.EndPointId, StringComparison.OrdinalIgnoreCase));
        if (!string.IsNullOrEmpty(filter.EventId)) q = q.Where(e => HasPrefix(e.EventId, filter.EventId));
        if (!string.IsNullOrEmpty(filter.SessionId)) q = q.Where(e => HasPrefix(e.SessionId, filter.SessionId));
        if (!string.IsNullOrEmpty(filter.LastMessageId)) q = q.Where(e => HasPrefix(e.LastMessageId, filter.LastMessageId));
        if (!string.IsNullOrEmpty(filter.ErrorText))
            q = q.Where(e => e.MessageContent?.ErrorContent?.ErrorText?.Contains(filter.ErrorText, StringComparison.OrdinalIgnoreCase) == true);
        if (filter.EventTypeId is { Count: > 0 })
        {
            var types = new HashSet<string>(filter.EventTypeId);
            q = q.Where(e => e.EventTypeId != null && types.Contains(e.EventTypeId));
        }
        if (filter.UpdatedAtFrom != null) q = q.Where(e => e.UpdatedAt >= filter.UpdatedAtFrom);
        if (filter.UpdatedAtTo != null) q = q.Where(e => e.UpdatedAt <= filter.UpdatedAtTo);
        if (!string.IsNullOrEmpty(filter.To)) q = q.Where(e => e.To?.Contains(filter.To, StringComparison.OrdinalIgnoreCase) == true);
        if (!string.IsNullOrEmpty(filter.From)) q = q.Where(e => e.From?.Contains(filter.From, StringComparison.OrdinalIgnoreCase) == true);
        return q;
    }

    /// <summary>Deep copy, so a stamped replacement cannot alias the caller's instance.</summary>
    private static UnresolvedEvent Clone(UnresolvedEvent source) =>
        JsonConvert.DeserializeObject<UnresolvedEvent>(JsonConvert.SerializeObject(source))!;

    private static UnresolvedEvent CloneWithoutEventJson(UnresolvedEvent source)
    {
        var clone = Newtonsoft.Json.JsonConvert.DeserializeObject<UnresolvedEvent>(
            Newtonsoft.Json.JsonConvert.SerializeObject(source));
        if (clone?.MessageContent?.EventContent != null)
        {
            clone.MessageContent.EventContent.EventJson = null;
        }

        return clone!;
    }

    private static MessageEntity CloneWithoutEventJson(MessageEntity source)
    {
        var clone = Newtonsoft.Json.JsonConvert.DeserializeObject<MessageEntity>(
            Newtonsoft.Json.JsonConvert.SerializeObject(source));
        if (clone?.MessageContent?.EventContent != null)
        {
            clone.MessageContent.EventContent.EventJson = null;
        }

        return clone!;
    }

    public virtual Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId)
    {
        var events = _events.Values.Where(e => e.EndpointId == endpointId).ToList();
        var grouped = events.GroupBy(e => e.ResolutionStatus).ToDictionary(g => g.Key, g => g.Count());
        var failures = events
            .Where(e => e.ResolutionStatus is ResolutionStatus.Failed or ResolutionStatus.DeadLettered)
            .Select(e => (DateTime?)DateTime.SpecifyKind(e.UpdatedAt, DateTimeKind.Utc));
        return Task.FromResult(new EndpointStateCount
        {
            EndpointId = endpointId,
            EventTime = DateTime.UtcNow,
            OldestFailureAt = failures.Min(),
            PendingCount = grouped.GetValueOrDefault(ResolutionStatus.Pending),
            DeferredCount = grouped.GetValueOrDefault(ResolutionStatus.Deferred),
            FailedCount = grouped.GetValueOrDefault(ResolutionStatus.Failed),
            DeadletterCount = grouped.GetValueOrDefault(ResolutionStatus.DeadLettered),
            UnsupportedCount = grouped.GetValueOrDefault(ResolutionStatus.Unsupported),
        });
    }

    public Task<SessionStateCount> DownloadEndpointSessionStateCount(string endpointId, string sessionId)
    {
        var events = _events.Values
            .Where(e => e.EndpointId == endpointId && (e.SessionId ?? string.Empty) == (sessionId ?? string.Empty))
            .ToList();

        return Task.FromResult(new SessionStateCount
        {
            SessionId = sessionId,
            PendingEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Pending).Select(CompositeEventId).ToList(),
            DeferredEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Deferred).Select(CompositeEventId).ToList(),
        });
    }

    public Task<IEnumerable<SessionStateCount>> DownloadEndpointSessionStateCountBatch(string endpointId, IEnumerable<string> sessionIds)
        => Task.FromResult<IEnumerable<SessionStateCount>>(sessionIds.Select(s => DownloadEndpointSessionStateCount(endpointId, s).GetAwaiter().GetResult()).ToList());

    public Task<EndpointState> DownloadEndpointStatePaging(string endpointId, int pageSize, string continuationToken)
    {
        var take = PaginationLimits.Resolve(pageSize);
        var events = _events.Values
            .Where(e => e.EndpointId == endpointId)
            .Where(e => e.ResolutionStatus is ResolutionStatus.Pending or ResolutionStatus.Deferred or ResolutionStatus.Failed or ResolutionStatus.DeadLettered or ResolutionStatus.Unsupported)
            .OrderByDescending(e => e.UpdatedAt)
            .Take(take)
            .ToList();

        return Task.FromResult(new EndpointState
        {
            EndpointId = endpointId,
            EventTime = DateTime.UtcNow,
            EnrichedUnresolvedEvents = events,
            PendingEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Pending).Select(CompositeEventId).ToList(),
            DeferredEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Deferred).Select(CompositeEventId).ToList(),
            FailedEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Failed).Select(CompositeEventId).ToList(),
            DeadletteredEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.DeadLettered).Select(CompositeEventId).ToList(),
            UnsupportedEvents = events.Where(e => e.ResolutionStatus == ResolutionStatus.Unsupported).Select(CompositeEventId).ToList(),
            ContinuationToken = events.Count == take ? "more" : string.Empty,
        });
    }

    public Task<BlockedMessageEventPage> GetBlockedEventsOnSession(string endpointId, string sessionId, int skip, int take)
    {
        var safeSkip = skip < 0 ? 0 : skip;
        var safeTake = PaginationLimits.Resolve(take);

        var matches = _events.Values
            .Where(e => e.EndpointId == endpointId && (e.SessionId ?? string.Empty) == (sessionId ?? string.Empty))
            .Where(e => e.ResolutionStatus is ResolutionStatus.Pending or ResolutionStatus.Deferred)
            .ToList();

        var page = matches.Skip(safeSkip).Take(safeTake).Select(ToBlockedMessageEvent).ToList();

        return Task.FromResult(new BlockedMessageEventPage
        {
            Items = page,
            Total = matches.Count,
        });
    }

    public virtual Task<IEnumerable<UnresolvedEvent>> GetPendingEventsOnSession(string endpointId)
        => Task.FromResult<IEnumerable<UnresolvedEvent>>(_events.Values.Where(e => e.EndpointId == endpointId && e.ResolutionStatus == ResolutionStatus.Pending).ToList());

    public Task<IEnumerable<BlockedMessageEvent>> GetInvalidEventsOnSession(string endpointId)
        => Task.FromResult<IEnumerable<BlockedMessageEvent>>(_events.Values
            .Where(e => e.EndpointId == endpointId && e.EndpointRole == EndpointRole.Publisher)
            .Select(ToBlockedMessageEvent)
            .ToList());

    public Task<bool> RemoveMessage(string eventId, string sessionId, string endpointId)
        => Task.FromResult(_events.TryRemove(Key(endpointId, eventId, sessionId), out _));

    public Task<bool> PurgeMessages(string endpointId, string sessionId)
    {
        var keys = _events.Keys.Where(k => k.Item1 == endpointId && k.Item3 == (sessionId ?? string.Empty)).ToList();
        foreach (var k in keys) _events.TryRemove(k, out _);
        return Task.FromResult(keys.Count > 0);
    }

    public Task<bool> PurgeMessages(string endpointId)
    {
        var keys = _events.Keys.Where(k => k.Item1 == endpointId).ToList();
        foreach (var k in keys) _events.TryRemove(k, out _);
        return Task.FromResult(keys.Count > 0);
    }

    public Task ArchiveFailedEvent(string eventId, string sessionId, string endpointId)
    {
        _events.TryRemove(Key(endpointId, eventId, sessionId), out _);
        return Task.CompletedTask;
    }

    public Task StoreMessage(MessageEntity message)
    {
        _messages[(message.EventId, message.MessageId)] = message;
        return Task.CompletedTask;
    }

    public Task<MessageEntity?> GetMessage(string eventId, string messageId)
        => Task.FromResult(_messages.TryGetValue((eventId, messageId), out var m) ? m : null);

    public Task<IEnumerable<MessageEntity>> GetEventHistory(string eventId)
        => Task.FromResult<IEnumerable<MessageEntity>>(_messages.Values.Where(m => m.EventId == eventId).ToList());

    public Task<MessageEntity?> GetLatestEventRequestMessage(string eventId)
        => Task.FromResult(
            _messages.Values
                .Where(m => m.EventId == eventId
                         && (m.MessageType == MessageType.EventRequest || m.MessageType == MessageType.ResubmissionRequest)
                         && !string.IsNullOrEmpty(m.MessageContent?.EventContent?.EventJson))
                .OrderByDescending(m => m.EnqueuedTimeUtc)
                .FirstOrDefault());

    public Task<MessageEntity?> GetFailedMessage(string eventId, string endpointId)
        => Task.FromResult(MessagesOnEndpointNewestFirst(eventId, endpointId).FirstOrDefault(m => m.MessageContent?.ErrorContent != null));

    public Task<MessageEntity?> GetDeadletteredMessage(string eventId, string endpointId)
        => Task.FromResult(MessagesOnEndpointNewestFirst(eventId, endpointId).FirstOrDefault());

    // Endpoint ids compare case-insensitively, like the Cosmos LOWER() query and SQL Server's
    // default collation.
    private IEnumerable<MessageEntity> MessagesOnEndpointNewestFirst(string eventId, string endpointId)
        => _messages.Values
            .Where(m => m.EventId == eventId && string.Equals(m.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.EnqueuedTimeUtc);

    public Task RemoveStoredMessage(string eventId, string messageId)
    {
        _messages.TryRemove((eventId, messageId), out _);
        return Task.CompletedTask;
    }

    public Task<int> PurgeStoredMessages(string endpointId)
    {
        var keys = _messages
            .Where(kv => string.Equals(kv.Value.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase))
            .Select(kv => kv.Key)
            .ToList();
        var removed = keys.Count(key => _messages.TryRemove(key, out _));
        return Task.FromResult(removed);
    }

    public Task<MessageSearchResult> SearchMessages(MessageFilter filter, string? continuationToken, int maxItemCount)
    {
        IEnumerable<MessageEntity> q = _messages.Values;
        // ID-like fields use case-insensitive PREFIX matching — see GetEventsByFilter.
        if (!string.IsNullOrEmpty(filter.EndpointId)) q = q.Where(m => HasPrefix(m.EndpointId, filter.EndpointId));
        if (!string.IsNullOrEmpty(filter.EventId)) q = q.Where(m => HasPrefix(m.EventId, filter.EventId));
        if (!string.IsNullOrEmpty(filter.MessageId)) q = q.Where(m => HasPrefix(m.MessageId, filter.MessageId));
        if (!string.IsNullOrEmpty(filter.SessionId)) q = q.Where(m => HasPrefix(m.SessionId, filter.SessionId));
        if (!string.IsNullOrEmpty(filter.From)) q = q.Where(m => m.From == filter.From);
        if (!string.IsNullOrEmpty(filter.To)) q = q.Where(m => m.To == filter.To);
        // Clone + strip EventJson — see GetEventsByFilter.
        var results = q.OrderByDescending(m => m.EnqueuedTimeUtc)
            .Take(PaginationLimits.Resolve(maxItemCount))
            .Select(CloneWithoutEventJson)
            .ToList();
        return Task.FromResult(new MessageSearchResult { Messages = results });
    }

    public virtual Task StoreMessageAudit(string eventId, MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null)
    {
        // Mirror the call arguments onto the entity (like SQL columns / Cosmos
        // document fields) so scoped queries such as GetResubmitCounts can
        // filter on them.
        auditEntity.EventId ??= eventId;
        auditEntity.EndpointId ??= endpointId;
        _audits.GetOrAdd(eventId, _ => new List<MessageAuditEntity>()).Add(auditEntity);
        return Task.CompletedTask;
    }

    public Task<IEnumerable<MessageAuditEntity>> GetMessageAudits(string eventId)
        => Task.FromResult<IEnumerable<MessageAuditEntity>>(_audits.GetValueOrDefault(eventId) ?? new List<MessageAuditEntity>());

    public Task<AuditSearchResult> SearchAudits(AuditFilter filter, string? continuationToken, int maxItemCount)
    {
        var allAudits = _audits.SelectMany(kvp => kvp.Value.Select(a => new AuditSearchItem
        {
            EventId = kvp.Key,
            EndpointId = a.EndpointId,
            Audit = a,
            CreatedAt = a.AuditTimestamp,
        }));
        // ID-like fields use case-insensitive PREFIX matching — see GetEventsByFilter.
        if (!string.IsNullOrEmpty(filter.EventId)) allAudits = allAudits.Where(a => HasPrefix(a.EventId, filter.EventId));
        if (!string.IsNullOrEmpty(filter.EndpointId))
        {
            // Exact scope vs. historical prefix — see AuditFilter.EndpointIdExact.
            allAudits = filter.EndpointIdExact
                ? allAudits.Where(a => string.Equals(a.EndpointId, filter.EndpointId, StringComparison.OrdinalIgnoreCase))
                : allAudits.Where(a => HasPrefix(a.EndpointId, filter.EndpointId));
        }
        if (!string.IsNullOrEmpty(filter.AuditorName)) allAudits = allAudits.Where(a => HasPrefix(a.Audit.AuditorName, filter.AuditorName));
        if (filter.AuditType.HasValue) allAudits = allAudits.Where(a => a.Audit.AuditType == filter.AuditType.Value);
        var results = allAudits.OrderByDescending(a => a.CreatedAt).Take(PaginationLimits.Resolve(maxItemCount)).ToList();
        return Task.FromResult(new AuditSearchResult { Audits = results });
    }

    public Task<IReadOnlyDictionary<string, int>> GetResubmitCounts(string endpointId, IReadOnlyCollection<string> eventIds)
    {
        var ids = new HashSet<string>((eventIds ?? Array.Empty<string>()).Where(e => !string.IsNullOrEmpty(e)));
        var counts = new Dictionary<string, int>();
        if (string.IsNullOrEmpty(endpointId) || ids.Count == 0)
            return Task.FromResult<IReadOnlyDictionary<string, int>>(counts);

        foreach (var (eventId, audits) in _audits)
        {
            if (!ids.Contains(eventId)) continue;
            var count = audits.Count(a =>
                a.EndpointId == endpointId &&
                !a.AccessDenied &&
                a.AuditType is MessageAuditType.Resubmit or MessageAuditType.ResubmitWithChanges);
            if (count > 0) counts[eventId] = count;
        }

        return Task.FromResult<IReadOnlyDictionary<string, int>>(counts);
    }

    public Task SetEventReport(string endpointId, string eventId, bool isReported, string? reportedBy, string? ticketId)
    {
        if (string.IsNullOrEmpty(endpointId)) throw new ArgumentNullException(nameof(endpointId));
        if (string.IsNullOrEmpty(eventId)) throw new ArgumentNullException(nameof(eventId));

        _eventReports[(endpointId, eventId)] = new EventReport
        {
            Id = $"{endpointId}_{eventId}",
            EndpointId = endpointId,
            EventId = eventId,
            IsReported = isReported,
            ReportedBy = reportedBy,
            ReportedAtUtc = DateTime.UtcNow,
            // Clearing the marker drops the ticket reference too.
            TicketId = isReported ? ticketId : null,
        };
        return Task.CompletedTask;
    }

    public Task<IReadOnlyDictionary<string, EventReport>> GetEventReports(string endpointId, IReadOnlyCollection<string> eventIds)
    {
        var result = new Dictionary<string, EventReport>();
        if (string.IsNullOrEmpty(endpointId) || eventIds is not { Count: > 0 })
            return Task.FromResult<IReadOnlyDictionary<string, EventReport>>(result);

        foreach (var eventId in eventIds.Where(e => !string.IsNullOrEmpty(e)).Distinct())
        {
            if (_eventReports.TryGetValue((endpointId, eventId), out var report))
                result[eventId] = report;
        }

        return Task.FromResult<IReadOnlyDictionary<string, EventReport>>(result);
    }

    private static bool HasPrefix(string? value, string prefix)
        => value != null && value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    public Task<string> GetEndpointErrorList(string endpointId)
    {
        var ids = _events.Values
            .Where(e => e.EndpointId == endpointId)
            .Where(e => e.ResolutionStatus is ResolutionStatus.Failed or ResolutionStatus.Deferred)
            .Select(CompositeEventId)
            .ToList();

        return Task.FromResult(EndpointErrorListFormat.Format(ids));
    }

    public Task<EndpointSubscription> SubscribeToEndpointNotification(string endpointId, string mail, string type, string author, string url, List<string> eventTypes, string payload, int frequency)
    {
        var sub = new EndpointSubscription
        {
            Id = Guid.NewGuid().ToString(),
            EndpointId = endpointId, Mail = mail, Type = type, AuthorId = author, Url = url,
            EventTypes = eventTypes, Payload = payload, Frequency = frequency,
        };
        _subscriptions[sub.Id] = sub;
        return Task.FromResult(sub);
    }

    public Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpoint(string endpointId)
        => Task.FromResult<IEnumerable<EndpointSubscription>>(_subscriptions.Values.Where(s => s.EndpointId == endpointId).ToList());

    public Task<IEnumerable<EndpointSubscription>> GetSubscriptionsOnEndpointWithEventtype(string endpoint, string eventtypes, string payload, string errorText)
        => GetSubscriptionsOnEndpoint(endpoint);

    public Task<bool> UpdateSubscription(EndpointSubscription subscription)
    {
        if (!_subscriptions.ContainsKey(subscription.Id)) return Task.FromResult(false);
        _subscriptions[subscription.Id] = subscription;
        return Task.FromResult(true);
    }

    public Task<bool> UnsubscribeById(string endpointId, string id)
        => Task.FromResult(_subscriptions.TryRemove(id, out _));

    public Task<bool> UnsubscribeByMail(string endpointId, string mail)
    {
        var match = _subscriptions.Values.FirstOrDefault(s => s.EndpointId == endpointId && s.Mail == mail);
        return match == null ? Task.FromResult(false) : Task.FromResult(_subscriptions.TryRemove(match.Id, out _));
    }

    public Task<bool> DeleteSubscription(string subscriptionId)
        => Task.FromResult(_subscriptions.TryRemove(subscriptionId, out _));

    public Task<EventSchema?> GetSchema(string eventTypeId)
        => Task.FromResult(_schemas.TryGetValue(eventTypeId, out var s) ? s : (EventSchema?)null);

    public Task<IReadOnlyList<EventSchema>> GetSchemas()
        => Task.FromResult<IReadOnlyList<EventSchema>>(_schemas.Values.ToList());

    public Task<EventSchema> DefineEventType(EventSchema schema)
    {
        if (string.IsNullOrWhiteSpace(schema?.EventTypeId)) throw new ArgumentException("schema.EventTypeId is required.", nameof(schema));
        if (string.IsNullOrWhiteSpace(schema?.JsonSchema)) throw new ArgumentException("schema.JsonSchema is required.", nameof(schema));
        var existing = _schemas.GetOrAdd(schema.EventTypeId, schema);
        if (!ReferenceEquals(existing, schema) && !SchemaJson.Equal(existing.JsonSchema, schema.JsonSchema))
            throw new SchemaConflictException(schema.EventTypeId);
        return Task.FromResult(existing);
    }

    public Task<AccessControlList?> GetSiteAccessControl()
        => Task.FromResult(_accessControls.TryGetValue(AccessControlList.SiteId, out var acl) ? Clone(acl) : null);

    public Task SetSiteAccessControl(AccessControlList accessControl)
    {
        var copy = Clone(accessControl)!;
        copy.Id = AccessControlList.SiteId;
        copy.EndpointId = null;
        _accessControls[copy.Id] = copy;
        return Task.CompletedTask;
    }

    public Task<AccessControlList?> GetEndpointAccessControl(string endpointId)
        => Task.FromResult(_accessControls.TryGetValue(AccessControlList.IdForEndpoint(endpointId), out var acl) ? Clone(acl) : null);

    public Task<IReadOnlyList<AccessControlList>> GetEndpointAccessControls()
        => Task.FromResult<IReadOnlyList<AccessControlList>>(
            _accessControls.Values
                .Where(a => a.Id.StartsWith(AccessControlList.EndpointIdPrefix, StringComparison.Ordinal))
                .Select(a => Clone(a)!)
                .ToList());

    public Task SetEndpointAccessControl(string endpointId, AccessControlList accessControl)
    {
        var copy = Clone(accessControl)!;
        copy.Id = AccessControlList.IdForEndpoint(endpointId);
        copy.EndpointId = endpointId;
        _accessControls[copy.Id] = copy;
        return Task.CompletedTask;
    }

    // Copy on both write and read so callers can never mutate stored state in place.
    private static AccessControlList? Clone(AccessControlList? acl)
        => acl == null ? null : JsonConvert.DeserializeObject<AccessControlList>(JsonConvert.SerializeObject(acl));

    public Task<IReadOnlyList<EndpointAcknowledgement>> GetEndpointAcknowledgements()
        => Task.FromResult<IReadOnlyList<EndpointAcknowledgement>>(_acknowledgements.Values.Select(Clone).ToList());

    public Task SetEndpointAcknowledgement(EndpointAcknowledgement acknowledgement)
    {
        var copy = Clone(acknowledgement);
        _acknowledgements[copy.EndpointId] = copy;
        return Task.CompletedTask;
    }

    public Task<bool> RemoveEndpointAcknowledgement(string endpointId, string? expectedAcknowledgementId = null)
    {
        if (!_acknowledgements.TryGetValue(endpointId, out var stored)
            || (expectedAcknowledgementId != null && stored.AcknowledgementId != expectedAcknowledgementId))
        {
            return Task.FromResult(false);
        }

        // Removes only the instance just checked, so a concurrent replace survives.
        return Task.FromResult(_acknowledgements.TryRemove(new KeyValuePair<string, EndpointAcknowledgement>(endpointId, stored)));
    }

    private static EndpointAcknowledgement Clone(EndpointAcknowledgement source) => new()
    {
        EndpointId = source.EndpointId,
        AcknowledgementId = source.AcknowledgementId,
        Reason = source.Reason,
        AcknowledgedBy = source.AcknowledgedBy,
        AcknowledgedAtUtc = source.AcknowledgedAtUtc,
        ExpiresAtUtc = source.ExpiresAtUtc,
        FailedCountAtAcknowledgement = source.FailedCountAtAcknowledgement,
    };

    public Task<EndpointMetadata?> GetEndpointMetadata(string endpointId)
        => Task.FromResult(_metadata.TryGetValue(endpointId, out var m) ? m : null);

    public Task<List<EndpointMetadata>> GetMetadatas() => Task.FromResult(_metadata.Values.ToList());

    public Task<List<EndpointMetadata>?> GetMetadatas(IEnumerable<string> endpointIds)
    {
        var set = new HashSet<string>(endpointIds);
        return Task.FromResult<List<EndpointMetadata>?>(_metadata.Values.Where(m => set.Contains(m.EndpointId)).ToList());
    }

    public Task<bool> SetEndpointMetadata(EndpointMetadata endpointMetadata)
    {
        endpointMetadata.TechnicalContacts ??= new List<TechnicalContact>();
        _metadata[endpointMetadata.EndpointId] = endpointMetadata;
        return Task.FromResult(true);
    }

    // ───────── Endpoint heartbeat ─────────

    public Task<List<EndpointMetadata>> GetMetadatasWithEnabledHeartbeat()
        => Task.FromResult(_metadata.Values.Where(m => m.IsHeartbeatEnabled == true).ToList());

    public Task EnableHeartbeatOnEndpoint(string endpointId, bool enable)
    {
        lock (_heartbeatLock)
        {
            MetadataFor(endpointId).IsHeartbeatEnabled = enable;
        }

        return Task.CompletedTask;
    }

    public Task<bool> SetHeartbeat(Heartbeat heartbeat, string endpointId)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);

        lock (_heartbeatLock)
        {
            var metadata = MetadataFor(endpointId);
            metadata.Heartbeats ??= new List<Heartbeat>();

            // Keyed by MessageId so the Pending probe and the answer that settles it
            // share one row instead of accumulating a duplicate.
            var existing = metadata.Heartbeats.FirstOrDefault(h => h.MessageId == heartbeat.MessageId);
            if (existing != null)
            {
                existing.StartTime = heartbeat.StartTime;
                existing.ReceivedTime = heartbeat.ReceivedTime;
                existing.EndTime = heartbeat.EndTime;
                existing.SdkVersion = heartbeat.SdkVersion;
                existing.EndpointHeartbeatStatus = heartbeat.EndpointHeartbeatStatus;
                if (heartbeat.IntervalSeconds > 0)
                {
                    existing.IntervalSeconds = heartbeat.IntervalSeconds;
                }
            }
            else
            {
                metadata.Heartbeats.Add(CloneHeartbeat(heartbeat));
            }

            metadata.Heartbeats = HeartbeatRollup.Prune(metadata.Heartbeats);
            HeartbeatRollup.Apply(metadata);
        }

        return Task.FromResult(true);
    }

    public Task<List<string>> SweepTimedOutHeartbeats(DateTime cutoffUtc)
    {
        var swept = new List<string>();
        lock (_heartbeatLock)
        {
            foreach (var metadata in _metadata.Values)
            {
                var timedOut = metadata.Heartbeats?
                    .Where(h => h.EndpointHeartbeatStatus == HeartbeatStatus.Pending && h.StartTime <= cutoffUtc)
                    .ToList();
                if (timedOut is not { Count: > 0 }) continue;

                foreach (var heartbeat in timedOut)
                {
                    heartbeat.EndpointHeartbeatStatus = HeartbeatStatus.Off;
                }

                HeartbeatRollup.Apply(metadata);
                swept.Add(metadata.EndpointId);
            }
        }

        return Task.FromResult(swept);
    }

    public Task<HeartbeatSettings> GetHeartbeatSettings()
    {
        lock (_heartbeatLock)
        {
            return Task.FromResult(CloneSettings(_heartbeatSettings) ?? new HeartbeatSettings());
        }
    }

    public Task<bool> SetHeartbeatSettings(HeartbeatSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        lock (_heartbeatLock)
        {
            var stored = CloneSettings(settings)!;
            if (string.IsNullOrWhiteSpace(stored.Id)) stored.Id = HeartbeatSettings.SingletonId;
            // The claim owns LastSentAtUtc: an operator edit that carries no value
            // must not reset the send schedule.
            stored.LastSentAtUtc ??= _heartbeatSettings?.LastSentAtUtc;
            stored.LastHeartbeatFoldAtUtc ??= _heartbeatSettings?.LastHeartbeatFoldAtUtc;
            _heartbeatSettings = stored;
        }

        return Task.FromResult(true);
    }

    public virtual Task<AuditSettings> GetAuditSettings()
        => Task.FromResult(CloneAuditSettings(_auditSettings) ?? new AuditSettings());

    public virtual Task<bool> SetAuditSettings(AuditSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var stored = CloneAuditSettings(settings)!;
        if (string.IsNullOrWhiteSpace(stored.Id)) stored.Id = AuditSettings.SingletonId;
        _auditSettings = stored;
        return Task.FromResult(true);
    }

    public virtual Task<McpAccessSettings> GetMcpAccessSettings()
    {
        lock (_mcpAccessLock)
        {
            return Task.FromResult(_mcpAccessSettings?.Clone() ?? new McpAccessSettings());
        }
    }

    public virtual Task<bool> TrySetMcpAccessSettings(McpAccessSettings settings, string? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_mcpAccessLock)
        {
            if (!string.Equals(_mcpAccessSettings?.Revision, expectedRevision, StringComparison.Ordinal))
                return Task.FromResult(false);

            var stored = settings.Clone();
            stored.Id = McpAccessSettings.SingletonId;
            _mcpAccessSettings = stored;
            return Task.FromResult(true);
        }
    }

    public virtual Task<AgentApiKeySettings> GetAgentApiKeySettings()
    {
        lock (_agentApiKeysLock)
        {
            return Task.FromResult(_agentApiKeySettings?.Clone() ?? new AgentApiKeySettings());
        }
    }

    public virtual Task<bool> TrySetAgentApiKeySettings(AgentApiKeySettings settings, string? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_agentApiKeysLock)
        {
            if (!string.Equals(_agentApiKeySettings?.Revision, expectedRevision, StringComparison.Ordinal))
                return Task.FromResult(false);

            var stored = settings.Clone();
            stored.Id = AgentApiKeySettings.SingletonId;
            _agentApiKeySettings = stored;
            return Task.FromResult(true);
        }
    }

    public virtual Task<List<AgentApiKeyUsage>> GetAgentApiKeyUsage()
        => Task.FromResult(_agentApiKeyUsage
            .Select(u => new AgentApiKeyUsage { KeyId = u.Key, LastUsedAtUtc = u.Value })
            .ToList());

    public virtual Task RecordAgentApiKeyUse(string keyId, DateTime usedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        _agentApiKeyUsage[keyId] = usedAtUtc;
        return Task.CompletedTask;
    }

    public Task<bool> TryClaimHeartbeatSend(DateTime dueBefore)
    {
        lock (_heartbeatLock)
        {
            _heartbeatSettings ??= new HeartbeatSettings();
            if (!_heartbeatSettings.Enabled ||
                (_heartbeatSettings.LastSentAtUtc.HasValue && _heartbeatSettings.LastSentAtUtc.Value > dueBefore))
            {
                return Task.FromResult(false);
            }

            _heartbeatSettings.LastSentAtUtc = DateTime.UtcNow;
            return Task.FromResult(true);
        }
    }

    public Task<List<HeartbeatOverviewItem>> GetHeartbeatOverview()
    {
        lock (_heartbeatLock)
        {
            return Task.FromResult(_metadata.Values
                .OrderBy(m => m.EndpointId, StringComparer.Ordinal)
                .Select(HeartbeatRollup.BuildOverviewItem)
                .ToList());
        }
    }

    // ───────── Durable endpoint heartbeat history ─────────

    public Task<List<HeartbeatUptimeDay>> GetHeartbeatUptimeDays(DateTime fromDayUtc)
    {
        lock (_heartbeatLock)
        {
            return Task.FromResult(_heartbeatUptimeDays.Values
                .Where(day => day.DayUtc >= fromDayUtc)
                .OrderBy(day => day.EndpointId, StringComparer.Ordinal)
                .ThenBy(day => day.DayUtc)
                .Select(CloneUptimeDay)
                .ToList());
        }
    }

    public Task<bool> UpsertHeartbeatUptimeDays(IEnumerable<HeartbeatUptimeDay> days)
    {
        ArgumentNullException.ThrowIfNull(days);
        lock (_heartbeatLock)
        {
            foreach (var day in days)
            {
                _heartbeatUptimeDays[(day.EndpointId, day.DayUtc)] = CloneUptimeDay(day);
            }
        }

        return Task.FromResult(true);
    }

    public Task<List<HeartbeatGap>> GetHeartbeatGaps(DateTime fromUtc)
    {
        lock (_heartbeatLock)
        {
            return Task.FromResult(_heartbeatGaps.Values
                .Where(gap => gap.ToUtc is null || gap.ToUtc >= fromUtc)
                .OrderByDescending(gap => gap.FromUtc)
                .Select(CloneGap)
                .ToList());
        }
    }

    public Task<bool> UpsertHeartbeatGaps(IEnumerable<HeartbeatGap> gaps)
    {
        ArgumentNullException.ThrowIfNull(gaps);
        lock (_heartbeatLock)
        {
            foreach (var gap in gaps)
            {
                _heartbeatGaps[(gap.EndpointId, gap.FromUtc)] = CloneGap(gap);
            }
        }

        return Task.FromResult(true);
    }

    public Task<bool> TryClaimHeartbeatHistoryFold(DateTime dueBefore)
    {
        lock (_heartbeatLock)
        {
            _heartbeatSettings ??= new HeartbeatSettings();
            if (_heartbeatSettings.LastHeartbeatFoldAtUtc.HasValue &&
                _heartbeatSettings.LastHeartbeatFoldAtUtc.Value > dueBefore)
            {
                return Task.FromResult(false);
            }

            _heartbeatSettings.LastHeartbeatFoldAtUtc = DateTime.UtcNow;
            return Task.FromResult(true);
        }
    }

    public Task PruneHeartbeatHistory(DateTime cutoffUtc)
    {
        lock (_heartbeatLock)
        {
            foreach (var key in _heartbeatUptimeDays
                .Where(pair => pair.Value.DayUtc < cutoffUtc.Date)
                .Select(pair => pair.Key)
                .ToList())
            {
                _heartbeatUptimeDays.Remove(key);
            }

            foreach (var key in _heartbeatGaps
                .Where(pair => pair.Value.ToUtc.HasValue && pair.Value.ToUtc.Value < cutoffUtc)
                .Select(pair => pair.Key)
                .ToList())
            {
                _heartbeatGaps.Remove(key);
            }
        }

        return Task.CompletedTask;
    }

    // ───────── Service health (platform services, not endpoints) ─────────

    public Task<List<ServiceHealth>> GetServiceHealth()
    {
        lock (_heartbeatLock)
        {
            return Task.FromResult(_serviceHealth.Values
                .OrderBy(s => s.ServiceId, StringComparer.Ordinal)
                .Select(s => CloneServiceHealth(s)!)
                .ToList());
        }
    }

    public Task<bool> TryClaimServiceProbe(string serviceId, DateTime dueBefore, string probeMessageId)
    {
        if (string.IsNullOrWhiteSpace(serviceId)) throw new ArgumentNullException(nameof(serviceId));

        lock (_heartbeatLock)
        {
            if (!_serviceHealth.TryGetValue(serviceId, out var health))
            {
                _serviceHealth[serviceId] = new ServiceHealth
                {
                    ServiceId = serviceId,
                    LastProbeSentUtc = DateTime.UtcNow,
                    LastProbeMessageId = probeMessageId,
                };
                return Task.FromResult(true);
            }

            if (health.LastProbeSentUtc.HasValue && health.LastProbeSentUtc.Value > dueBefore)
            {
                return Task.FromResult(false);
            }

            health.LastProbeSentUtc = DateTime.UtcNow;
            health.LastProbeMessageId = probeMessageId;
            return Task.FromResult(true);
        }
    }

    public Task<bool> SetServiceHealth(ServiceHealth serviceHealth)
    {
        ArgumentNullException.ThrowIfNull(serviceHealth);
        if (string.IsNullOrWhiteSpace(serviceHealth.ServiceId)) throw new ArgumentNullException(nameof(serviceHealth));

        lock (_heartbeatLock)
        {
            var stored = CloneServiceHealth(serviceHealth)!;
            // The claim owns LastProbeSentUtc; preserve it so an answer never resets
            // the send schedule, and clear the in-flight marker.
            stored.LastProbeSentUtc = _serviceHealth.TryGetValue(stored.ServiceId, out var existing)
                ? existing.LastProbeSentUtc
                : null;
            stored.LastProbeMessageId = null;
            _serviceHealth[stored.ServiceId] = stored;
        }

        return Task.FromResult(true);
    }

    public Task<List<string>> SweepTimedOutServiceProbes(DateTime cutoffUtc)
    {
        var swept = new List<string>();
        lock (_heartbeatLock)
        {
            foreach (var health in _serviceHealth.Values)
            {
                if (health.LastProbeMessageId == null ||
                    !health.LastProbeSentUtc.HasValue ||
                    health.LastProbeSentUtc.Value > cutoffUtc)
                {
                    continue;
                }

                health.Status = HeartbeatStatus.Off;
                health.LastProbeMessageId = null;
                swept.Add(health.ServiceId);
            }
        }

        return Task.FromResult(swept);
    }

    // Callers must hold _heartbeatLock.
    private EndpointMetadata MetadataFor(string endpointId)
        => _metadata.GetOrAdd(endpointId, id => new EndpointMetadata
        {
            EndpointId = id,
            TechnicalContacts = new List<TechnicalContact>(),
        });

    // Copy on write and on read so callers can never mutate stored state in place.
    private static Heartbeat CloneHeartbeat(Heartbeat source) => new()
    {
        MessageId = source.MessageId,
        StartTime = source.StartTime,
        ReceivedTime = source.ReceivedTime,
        EndTime = source.EndTime,
        IntervalSeconds = source.IntervalSeconds,
        SdkVersion = source.SdkVersion,
        EndpointHeartbeatStatus = source.EndpointHeartbeatStatus,
    };

    private static AuditSettings? CloneAuditSettings(AuditSettings? source) => source == null ? null : new AuditSettings
    {
        Id = source.Id,
        DisabledAuditTypes = new List<string>(source.DisabledAuditTypes ?? new List<string>()),
    };

    private static HeartbeatSettings? CloneSettings(HeartbeatSettings? source) => source == null ? null : new HeartbeatSettings
    {
        Id = source.Id,
        Enabled = source.Enabled,
        IntervalSeconds = source.IntervalSeconds,
        TimeoutSeconds = source.TimeoutSeconds,
        LastSentAtUtc = source.LastSentAtUtc,
        LastHeartbeatFoldAtUtc = source.LastHeartbeatFoldAtUtc,
    };

    private static HeartbeatUptimeDay CloneUptimeDay(HeartbeatUptimeDay source) => new()
    {
        Id = source.Id,
        EndpointId = source.EndpointId,
        DayUtc = source.DayUtc,
        Expected = source.Expected,
        Received = source.Received,
        Missed = source.Missed,
        ObservedSeconds = source.ObservedSeconds,
        LongestGapSeconds = source.LongestGapSeconds,
        LastBeatUtc = source.LastBeatUtc,
        TimeToLiveSeconds = source.TimeToLiveSeconds,
    };

    private static HeartbeatGap CloneGap(HeartbeatGap source) => new()
    {
        Id = source.Id,
        EndpointId = source.EndpointId,
        FromUtc = source.FromUtc,
        ToUtc = source.ToUtc,
        SdkVersionBefore = source.SdkVersionBefore,
        SdkVersionAfter = source.SdkVersionAfter,
        TimeToLiveSeconds = source.TimeToLiveSeconds,
    };

    private static ServiceHealth? CloneServiceHealth(ServiceHealth? source) => source == null ? null : new ServiceHealth
    {
        ServiceId = source.ServiceId,
        Status = source.Status,
        Version = source.Version,
        LastProbeMessageId = source.LastProbeMessageId,
        LastProbeSentUtc = source.LastProbeSentUtc,
        LastSeenUtc = source.LastSeenUtc,
        RoundTripMs = source.RoundTripMs,
    };

    [Obsolete("Use GetEndpointMetrics(from, to). This overload will be removed in v5.")]
    public virtual Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from)
        => GetEndpointMetrics(from, DateTime.UtcNow);

    public virtual Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from, DateTime to)
    {
        var messages = _messages.Values.Where(m => m.EnqueuedTimeUtc >= from && m.EnqueuedTimeUtc < to).ToList();
        return Task.FromResult(new EndpointMetricsResult
        {
            Published = CountByEndpointAndEventType(messages.Where(m => m.MessageType == MessageType.EventRequest), published: true),
            Handled = CountByEndpointAndEventType(messages.Where(m => m.MessageType == MessageType.ResolutionResponse), published: false),
            Failed = CountByEndpointAndEventType(messages.Where(m => m.MessageType == MessageType.ErrorResponse), published: false),
        });
    }

    [Obsolete("Use GetEndpointLatencyMetrics(from, to). This overload will be removed in v5.")]
    public virtual Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from)
        => GetEndpointLatencyMetrics(from, DateTime.UtcNow);

    public virtual Task<EndpointLatencyMetricsResult> GetEndpointLatencyMetrics(DateTime from, DateTime to)
    {
        var outcomeTypes = new HashSet<MessageType>
        {
            MessageType.ResolutionResponse,
            MessageType.ErrorResponse,
            MessageType.SkipResponse,
            MessageType.DeferralResponse,
            MessageType.UnsupportedResponse,
        };

        var latencies = _messages.Values
            .Where(m => m.EnqueuedTimeUtc >= from && m.EnqueuedTimeUtc < to && outcomeTypes.Contains(m.MessageType))
            .GroupBy(m => (EndpointId: m.EndpointId ?? string.Empty, EventTypeId: m.EventTypeId ?? string.Empty))
            .Select(g => new EndpointLatencyAggregate
            {
                EndpointId = g.Key.EndpointId,
                EventTypeId = g.Key.EventTypeId,
                Queue = Aggregate(g.Select(m => m.QueueTimeMs)),
                Processing = Aggregate(g.Select(m => m.ProcessingTimeMs)),
            })
            .ToList();

        return Task.FromResult(new EndpointLatencyMetricsResult { Latencies = latencies });
    }

    [Obsolete("Use GetFailedMessageInsights(from, to). This overload will be removed in v5.")]
    public Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from)
        => GetFailedMessageInsights(from, DateTime.UtcNow);

    public virtual Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from, DateTime to)
    {
        var results = _messages.Values
            .Where(m => m.EnqueuedTimeUtc >= from && m.EnqueuedTimeUtc < to && m.MessageType == MessageType.ErrorResponse)
            .Select(m => new FailedMessageInfo
            {
                EndpointId = m.EndpointId,
                EventTypeId = m.EventTypeId,
                ErrorText = m.MessageContent?.ErrorContent?.ErrorText ?? m.DeadLetterErrorDescription ?? string.Empty,
                EnqueuedTimeUtc = m.EnqueuedTimeUtc,
                EventId = m.EventId,
            })
            .ToList();

        return Task.FromResult(results);
    }

    [Obsolete("Use GetTimeSeriesMetrics(from, to, substringLength, bucketLabel). This overload will be removed in v5.")]
    public virtual Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel)
        => GetTimeSeriesMetrics(from, DateTime.UtcNow, substringLength, bucketLabel);

    public virtual Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, DateTime to, int substringLength, string bucketLabel)
    {
        var buckets = GenerateBucketKeys(from, to, substringLength)
            .ToDictionary(k => k, k => new TimeSeriesBucket { Timestamp = k });

        foreach (var message in _messages.Values.Where(m => m.EnqueuedTimeUtc >= from && m.EnqueuedTimeUtc < to))
        {
            var key = message.EnqueuedTimeUtc.ToUniversalTime().ToString("o")[..substringLength];
            if (!buckets.TryGetValue(key, out var bucket))
            {
                bucket = new TimeSeriesBucket { Timestamp = key };
                buckets[key] = bucket;
            }

            switch (message.MessageType)
            {
                case MessageType.EventRequest:
                    bucket.Published++;
                    break;
                case MessageType.ResolutionResponse:
                    bucket.Handled++;
                    break;
                case MessageType.ErrorResponse:
                    bucket.Failed++;
                    break;
            }
        }

        return Task.FromResult(new TimeSeriesResult
        {
            BucketSize = bucketLabel,
            DataPoints = buckets.Values.OrderBy(b => b.Timestamp).ToList(),
        });
    }

    [Obsolete("Use GetEventTypeTimeSeriesMetrics(from, to, substringLength, bucketLabel). This overload will be removed in v5.")]
    public virtual Task<EventTypeTimeSeriesResult> GetEventTypeTimeSeriesMetrics(DateTime from, int substringLength, string bucketLabel)
        => GetEventTypeTimeSeriesMetrics(from, DateTime.UtcNow, substringLength, bucketLabel);

    public virtual Task<EventTypeTimeSeriesResult> GetEventTypeTimeSeriesMetrics(DateTime from, DateTime to, int substringLength, string bucketLabel)
    {
        // Published (EventRequest) counts only; buckets stay sparse (no zero-fill)
        // and sorted ascending — the contract shared with the SQL/Cosmos backends.
        var series = _messages.Values
            .Where(m => m.EnqueuedTimeUtc >= from
                && m.EnqueuedTimeUtc < to
                && m.MessageType == MessageType.EventRequest
                && !string.IsNullOrEmpty(m.EventTypeId))
            .GroupBy(m => m.EventTypeId!)
            .Select(g => new EventTypeSeriesEntry
            {
                EventTypeId = g.Key,
                Total = g.Count(),
                DataPoints = g
                    .GroupBy(m => m.EnqueuedTimeUtc.ToUniversalTime().ToString("o")[..substringLength])
                    .OrderBy(b => b.Key)
                    .Select(b => new EventTypeSeriesBucket { Timestamp = b.Key, Published = b.Count() })
                    .ToList(),
            })
            .OrderByDescending(s => s.Total)
            .ToList();

        return Task.FromResult(new EventTypeTimeSeriesResult { BucketSize = bucketLabel, Series = series });
    }

    private static List<EndpointEventTypeCount> CountByEndpointAndEventType(IEnumerable<MessageEntity> messages, bool published)
        => messages
            .GroupBy(m => (
                EndpointId: published ? (m.From ?? string.Empty) : (m.EndpointId ?? string.Empty),
                EventTypeId: m.EventTypeId ?? string.Empty))
            .Select(g => new EndpointEventTypeCount
            {
                EndpointId = g.Key.EndpointId,
                EventTypeId = g.Key.EventTypeId,
                Count = g.Count(),
            })
            .ToList();

    private static string CompositeEventId(UnresolvedEvent @event)
        => $"{@event.EventId}_{@event.SessionId ?? string.Empty}";

    private static BlockedMessageEvent ToBlockedMessageEvent(UnresolvedEvent @event)
    {
        return new BlockedMessageEvent
        {
            EventId = @event.EventId,
            OriginatingId = BlockedEventRules.ResolveOriginatingId(@event.OriginatingMessageId, @event.LastMessageId),
            Status = @event.ResolutionStatus.ToString(),
        };
    }

    private static LatencyAggregate Aggregate(IEnumerable<long?> values)
    {
        var captured = values.Where(v => v.HasValue).Select(v => (double)v!.Value).ToList();
        return captured.Count == 0
            ? new LatencyAggregate()
            : new LatencyAggregate
            {
                Count = captured.Count,
                AvgMs = captured.Average(),
                MinMs = captured.Min(),
                MaxMs = captured.Max(),
            };
    }

    private static List<string> GenerateBucketKeys(DateTime from, DateTime to, int substringLength)
    {
        var current = substringLength switch
        {
            16 => new DateTime(from.Year, from.Month, from.Day, from.Hour, from.Minute, 0, DateTimeKind.Utc),
            13 => new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0, DateTimeKind.Utc),
            10 => new DateTime(from.Year, from.Month, from.Day, 0, 0, 0, DateTimeKind.Utc),
            _ => new DateTime(from.Year, from.Month, from.Day, from.Hour, 0, 0, DateTimeKind.Utc)
        };

        var step = substringLength switch
        {
            16 => TimeSpan.FromMinutes(1),
            13 => TimeSpan.FromHours(1),
            10 => TimeSpan.FromDays(1),
            _ => TimeSpan.FromHours(1)
        };

        var keys = new List<string>();
        while (current < to)
        {
            keys.Add(current.ToString("o")[..substringLength]);
            current += step;
        }

        return keys;
    }
}
