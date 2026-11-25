using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;

namespace NimBus.Testing.Conformance;

// Erasure of one event copy (Spec 049) and age-based deletion (Spec 050) for the in-memory store.
public partial class InMemoryMessageStore
{
    // The event type and write time of each audit entry, which MessageAuditEntity doesn't carry.
    // Keyed by reference: the store keeps the caller's instances.
    private readonly ConcurrentDictionary<MessageAuditEntity, AuditMetadata> _auditMetadata = new(ReferenceEqualityComparer.Instance);

    // When each history entry was first stored, for the message-history period.
    private readonly ConcurrentDictionary<(string EventId, string MessageId), DateTime> _messageCreatedAt = new();

    // Erasures and purges read, then write, across several dictionaries.
    private readonly object _erasureLock = new();

    private sealed record AuditMetadata(string? EventTypeId, DateTime CreatedAtUtc);

    /// <summary>
    /// Called before each write an erasure makes, with the place it writes. Test doubles override it
    /// and throw to simulate a failed write; the default does nothing.
    /// </summary>
    protected virtual void OnErasureWrite(ErasurePlace place)
    {
    }

    /// <inheritdoc />
    public Task<EventErasureResult> EraseEventCopyAsync(EventErasureRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return EventErasureRunner.RunAsync(request, new ErasureSteps(this, request.EndpointId, request.EventId), cancellationToken);
    }

    /// <inheritdoc />
    public Task<RetentionPurgeResult> PurgeExpiredData(RetentionPeriods periods, DateTime nowUtc, int batchSize, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(periods);
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_erasureLock)
        {
            var open = 0;
            if (Cutoff(periods.OpenEventsDays, nowUtc) is { } openCutoff)
            {
                open = RemoveOldest(_events, row => IsOpen(row.ResolutionStatus) && row.UpdatedAt < openCutoff, batchSize);
            }

            var resolved = 0;
            if (Cutoff(periods.ResolvedEventsDays, nowUtc) is { } resolvedCutoff)
            {
                resolved = RemoveOldest(_archived, row => row.UpdatedAt < resolvedCutoff, batchSize);
                resolved += RemoveOldest(
                    _events,
                    row => row.ResolutionStatus is ResolutionStatus.Completed or ResolutionStatus.Skipped && row.UpdatedAt < resolvedCutoff,
                    batchSize - resolved);
            }

            var history = 0;
            if (Cutoff(periods.MessageHistoryDays, nowUtc) is { } historyCutoff)
            {
                foreach (var key in _messageCreatedAt.Where(kv => kv.Value < historyCutoff).OrderBy(kv => kv.Value).Select(kv => kv.Key).Take(batchSize).ToList())
                {
                    _messageCreatedAt.TryRemove(key, out _);
                    if (_messages.TryRemove(key, out _))
                        history++;
                }
            }

            var audits = 0;
            if (Cutoff(periods.AuditTrailDays, nowUtc) is { } auditCutoff)
            {
                foreach (var (eventId, entries) in _audits)
                {
                    lock (entries)
                    {
                        var expired = entries.Where(a => AuditCreatedAt(a) < auditCutoff).Take(batchSize - audits).ToList();
                        foreach (var audit in expired)
                        {
                            entries.Remove(audit);
                            _auditMetadata.TryRemove(audit, out _);
                        }

                        audits += expired.Count;
                    }

                    if (audits >= batchSize)
                        break;
                }
            }

            return Task.FromResult(new RetentionPurgeResult(open, resolved, history, audits));
        }
    }

    private static DateTime? Cutoff(int days, DateTime nowUtc) =>
        days == RetentionPeriods.Unlimited ? null : nowUtc.AddDays(-days);

    private static bool IsOpen(ResolutionStatus status) =>
        status is ResolutionStatus.Pending or ResolutionStatus.Failed or ResolutionStatus.Deferred
            or ResolutionStatus.DeadLettered or ResolutionStatus.Unsupported;

    private static int RemoveOldest(
        ConcurrentDictionary<(string Endpoint, string EventId, string Session), UnresolvedEvent> rows,
        Func<UnresolvedEvent, bool> expired,
        int limit)
    {
        if (limit <= 0)
            return 0;

        var removed = 0;
        foreach (var key in rows.Where(kv => expired(kv.Value)).OrderBy(kv => kv.Value.UpdatedAt).Select(kv => kv.Key).Take(limit).ToList())
        {
            if (rows.TryRemove(key, out _))
                removed++;
        }

        return removed;
    }

    private DateTime AuditCreatedAt(MessageAuditEntity audit) =>
        _auditMetadata.TryGetValue(audit, out var meta) ? meta.CreatedAtUtc : audit.AuditTimestamp;

    private sealed class ErasureSteps : IEventErasureSteps
    {
        private readonly InMemoryMessageStore _store;
        private readonly string _endpointId;
        private readonly string _eventId;

        public ErasureSteps(InMemoryMessageStore store, string endpointId, string eventId)
        {
            _store = store;
            _endpointId = endpointId;
            _eventId = eventId;
        }

        private bool IsPair((string Endpoint, string EventId, string Session) key) =>
            string.Equals(key.Endpoint, _endpointId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(key.EventId, _eventId, StringComparison.Ordinal);

        private bool IsHistory(MessageEntity message) =>
            string.Equals(message.EventId, _eventId, StringComparison.Ordinal)
            && string.Equals(message.EndpointId, _endpointId, StringComparison.OrdinalIgnoreCase);

        public Task<ErasureSources> ReadSourcesAsync(CancellationToken cancellationToken)
        {
            lock (_store._erasureLock)
            {
                var rows = _store._events.Where(kv => IsPair(kv.Key)).Select(kv => new ErasureTrackingRow(kv.Value, Hidden: false))
                    .Concat(_store._archived.Where(kv => IsPair(kv.Key)).Select(kv => new ErasureTrackingRow(kv.Value, Hidden: true)))
                    .ToList();
                var history = _store._messages.Values.Where(IsHistory).ToList();
                var payloads = rows.Select(r => r.Event.MessageContent?.EventContent?.EventJson)
                    .Concat(history.Select(m => m.MessageContent?.EventContent?.EventJson))
                    .OfType<string>()
                    .ToList();
                return Task.FromResult(new ErasureSources(rows, history.Count, payloads));
            }
        }

        public Task<int> RedactEventAuditsAsync(string marker, CancellationToken cancellationToken)
        {
            lock (_store._erasureLock)
            {
                if (!_store._audits.TryGetValue(_eventId, out var entries))
                    return Task.FromResult(0);

                lock (entries)
                {
                    var targets = entries
                        .Where(a => string.IsNullOrEmpty(a.EndpointId) || string.Equals(a.EndpointId, _endpointId, StringComparison.OrdinalIgnoreCase))
                        .Where(a => ErasureRules.AuditNeedsRedaction(a.AuditType, a.Data, a.Comment))
                        .ToList();
                    if (targets.Count == 0)
                        return Task.FromResult(0);

                    _store.OnErasureWrite(ErasurePlace.EventAudits);
                    foreach (var audit in targets)
                    {
                        audit.Data = ErasureRules.RedactedAuditData(audit.AuditType, audit.Data, marker);
                        audit.Comment = null;
                    }

                    return Task.FromResult(targets.Count);
                }
            }
        }

        public Task<int> RedactComposeAuditsAsync(
            string producerEndpointId,
            string eventTypeId,
            Func<string?, string?, bool> matches,
            string marker,
            CancellationToken cancellationToken)
        {
            lock (_store._erasureLock)
            {
                if (!_store._audits.TryGetValue(string.Empty, out var entries))
                    return Task.FromResult(0);

                lock (entries)
                {
                    var targets = entries
                        .Where(a => a.AuditType == MessageAuditType.Compose
                            && string.Equals(a.EndpointId, producerEndpointId, StringComparison.OrdinalIgnoreCase))
                        .Select(a => (Audit: a, EventTypeId: _store._auditMetadata.TryGetValue(a, out var meta) ? meta.EventTypeId : null))
                        .Where(a => string.Equals(a.EventTypeId, eventTypeId, StringComparison.OrdinalIgnoreCase))
                        .Where(a => matches(a.Audit.Data, a.EventTypeId))
                        .Select(a => a.Audit)
                        .ToList();
                    if (targets.Count == 0)
                        return Task.FromResult(0);

                    _store.OnErasureWrite(ErasurePlace.ComposeAudits);
                    foreach (var audit in targets)
                    {
                        audit.Data = marker;
                        audit.Comment = null;
                    }

                    return Task.FromResult(targets.Count);
                }
            }
        }

        public Task<int> DeleteHistoryAsync(CancellationToken cancellationToken)
        {
            lock (_store._erasureLock)
            {
                var keys = _store._messages.Where(kv => IsHistory(kv.Value)).Select(kv => kv.Key).ToList();
                if (keys.Count == 0)
                    return Task.FromResult(0);

                _store.OnErasureWrite(ErasurePlace.MessageHistory);
                var removed = 0;
                foreach (var key in keys)
                {
                    _store._messageCreatedAt.TryRemove(key, out _);
                    if (_store._messages.TryRemove(key, out _))
                        removed++;
                }

                return Task.FromResult(removed);
            }
        }

        public Task<int> DeleteEventReportAsync(CancellationToken cancellationToken)
        {
            lock (_store._erasureLock)
            {
                var keys = _store._eventReports.Keys
                    .Where(k => string.Equals(k.EndpointId, _endpointId, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(k.EventId, _eventId, StringComparison.Ordinal))
                    .ToList();
                if (keys.Count == 0)
                    return Task.FromResult(0);

                _store.OnErasureWrite(ErasurePlace.EventReport);
                return Task.FromResult(keys.Count(k => _store._eventReports.TryRemove(k, out _)));
            }
        }

        public Task<int> DeleteTrackingRowsAsync(CancellationToken cancellationToken)
        {
            lock (_store._erasureLock)
            {
                var live = _store._events.Keys.Where(IsPair).ToList();
                var hidden = _store._archived.Keys.Where(IsPair).ToList();
                if (live.Count + hidden.Count == 0)
                    return Task.FromResult(0);

                _store.OnErasureWrite(ErasurePlace.TrackingRow);
                return Task.FromResult(
                    live.Count(k => _store._events.TryRemove(k, out _))
                    + hidden.Count(k => _store._archived.TryRemove(k, out _)));
            }
        }
    }
}
