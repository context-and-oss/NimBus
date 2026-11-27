#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Outbox;
using NimBus.Testing.Conformance;

namespace NimBus.Core.Tests;

[TestClass]
public sealed class InMemoryOutboxConformanceTests : OutboxConformanceTests
{
    private readonly ConformanceInMemoryOutbox _outbox = new();

    protected override Task<IOutbox> CreateOutboxAsync() => Task.FromResult<IOutbox>(_outbox);

    protected override Task<DateTime?> GetDispatchedAtUtcAsync(string id) =>
        Task.FromResult(_outbox.GetDispatchedAtUtc(id));

    protected override Task AdvanceDispatchClockAsync()
    {
        _outbox.AdvanceClock();
        return Task.CompletedTask;
    }
}

[TestClass]
public sealed class InMemoryOutboxClaimingConformanceTests : OutboxClaimingConformanceTests
{
    private readonly ConformanceInMemoryOutbox _outbox = new();

    protected override Task<IOutbox> CreateOutboxAsync() => Task.FromResult<IOutbox>(_outbox);

    protected override Task AdvanceClockAsync(TimeSpan duration)
    {
        _outbox.AdvanceClock(duration);
        return Task.CompletedTask;
    }
}

/// <summary>
/// In-memory outbox with the full <see cref="IOutboxClaiming"/> contract on a manual clock.
/// Thread-safe, so several dispatchers can share it.
/// </summary>
internal sealed class ConformanceInMemoryOutbox : IOutbox, IOutboxClaiming
{
    private readonly object _gate = new();
    private readonly Dictionary<string, Row> _rows = new(StringComparer.Ordinal);
    private readonly List<Row> _storeOrder = new();
    private DateTime _utcNow = new(2030, 1, 2, 0, 0, 0, DateTimeKind.Utc);

    public DateTime UtcNow
    {
        get
        {
            lock (_gate)
            {
                return _utcNow;
            }
        }
    }

    public DateTime? GetDispatchedAtUtc(string id)
    {
        lock (_gate)
        {
            return _rows[id].Message.DispatchedAtUtc;
        }
    }

    public void AdvanceClock() => AdvanceClock(TimeSpan.FromMinutes(1));

    public void AdvanceClock(TimeSpan duration)
    {
        lock (_gate)
        {
            _utcNow = _utcNow.Add(duration);
        }
    }

    public int GetAttemptCount(string id)
    {
        lock (_gate)
        {
            return _rows[id].AttemptCount;
        }
    }

    public bool IsParked(string id)
    {
        lock (_gate)
        {
            return _rows[id].ParkedAtUtc.HasValue;
        }
    }

    public bool IsLeased(string id)
    {
        lock (_gate)
        {
            var row = _rows[id];
            return row.LeaseUntilUtc.HasValue && row.LeaseUntilUtc > _utcNow;
        }
    }

    public string? GetLastError(string id)
    {
        lock (_gate)
        {
            return _rows[id].LastError;
        }
    }

    public TimeSpan? GetLastRetryDelay(string id)
    {
        lock (_gate)
        {
            return _rows[id].LastRetryDelay;
        }
    }

    public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        Store(message);
        return Task.CompletedTask;
    }

    public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default)
    {
        foreach (var message in messages)
        {
            Store(message);
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            IReadOnlyList<OutboxMessage> pending = Pending()
                .Take(batchSize)
                .Select(row => Snapshot(row))
                .ToList();
            return Task.FromResult(pending);
        }
    }

    public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            _rows[id].Message.DispatchedAtUtc ??= _utcNow;
        }

        return Task.CompletedTask;
    }

    public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var id in ids)
            {
                _rows[id].Message.DispatchedAtUtc ??= _utcNow;
            }
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<OutboxMessage>> ClaimPendingAsync(
        int batchSize,
        string claimantId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var pending = Pending().ToList();
            // Evaluate against the state before this claim, like the SQL statement does.
            var blocked = pending.Where(IsBlocking).ToList();
            var claimable = pending
                .Where(row => IsReady(row) && !blocked.Any(other =>
                    !ReferenceEquals(other, row)
                    && !string.IsNullOrEmpty(row.Message.SessionId)
                    && string.Equals(other.Message.SessionId, row.Message.SessionId, StringComparison.Ordinal)))
                .Take(batchSize)
                .ToList();

            foreach (var row in claimable)
            {
                row.LeaseUntilUtc = _utcNow.Add(leaseDuration);
                row.ClaimedBy = claimantId;
            }

            IReadOnlyList<OutboxMessage> result = claimable.Select(Snapshot).ToList();
            return Task.FromResult(result);
        }
    }

    public Task RecordDispatchFailureAsync(
        string id,
        string claimantId,
        string error,
        TimeSpan retryDelay,
        bool park,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var row = _rows[id];
            if (row.Message.DispatchedAtUtc is not null
                || row.LeaseUntilUtc is null
                || !string.Equals(row.ClaimedBy, claimantId, StringComparison.Ordinal))
            {
                return Task.CompletedTask;
            }

            row.AttemptCount++;
            row.LastError = error;
            row.LastRetryDelay = park ? null : retryDelay;
            row.NextAttemptAtUtc = park ? null : _utcNow.Add(retryDelay);
            row.ParkedAtUtc = park ? _utcNow : null;
            row.LeaseUntilUtc = null;
        }

        return Task.CompletedTask;
    }

    public Task ReleaseClaimsAsync(
        string claimantId,
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            foreach (var id in ids)
            {
                var row = _rows[id];
                if (row.Message.DispatchedAtUtc is null
                    && string.Equals(row.ClaimedBy, claimantId, StringComparison.Ordinal))
                {
                    row.LeaseUntilUtc = null;
                }
            }
        }

        return Task.CompletedTask;
    }

    private void Store(OutboxMessage message)
    {
        lock (_gate)
        {
            var row = new Row(message);
            _rows.Add(message.Id, row);
            _storeOrder.Add(row);
        }
    }

    // OrderBy is stable, so rows sharing a CreatedAtUtc keep their store order.
    private IEnumerable<Row> Pending() => _storeOrder
        .Where(row => row.Message.DispatchedAtUtc is null)
        .OrderBy(row => row.Message.CreatedAtUtc);

    private bool IsReady(Row row) =>
        row.ParkedAtUtc is null
        && (row.NextAttemptAtUtc is null || row.NextAttemptAtUtc <= _utcNow)
        && (row.LeaseUntilUtc is null || row.LeaseUntilUtc <= _utcNow);

    private bool IsBlocking(Row row) =>
        row.ParkedAtUtc is not null
        || row.NextAttemptAtUtc > _utcNow
        || row.LeaseUntilUtc > _utcNow;

    private static OutboxMessage Snapshot(Row row) => new()
    {
        Id = row.Message.Id,
        MessageId = row.Message.MessageId,
        To = row.Message.To,
        EventTypeId = row.Message.EventTypeId,
        SessionId = row.Message.SessionId,
        CorrelationId = row.Message.CorrelationId,
        Payload = row.Message.Payload,
        EnqueueDelayMinutes = row.Message.EnqueueDelayMinutes,
        CreatedAtUtc = row.Message.CreatedAtUtc,
        ScheduledEnqueueTimeUtc = row.Message.ScheduledEnqueueTimeUtc,
        DispatchedAtUtc = row.Message.DispatchedAtUtc,
        TraceParent = row.Message.TraceParent,
        TraceState = row.Message.TraceState,
        AttemptCount = row.AttemptCount,
    };

    private sealed class Row(OutboxMessage message)
    {
        public OutboxMessage Message { get; } = message;
        public int AttemptCount { get; set; }
        public DateTime? NextAttemptAtUtc { get; set; }
        public DateTime? ParkedAtUtc { get; set; }
        public DateTime? LeaseUntilUtc { get; set; }
        public string? ClaimedBy { get; set; }
        public string? LastError { get; set; }
        public TimeSpan? LastRetryDelay { get; set; }
    }
}
