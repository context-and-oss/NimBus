#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Diagnostics;
using NimBus.Core.Inbox;
using NimBus.SDK.Hosting;

namespace NimBus.SDK.Tests;

[TestClass]
public sealed class InboxPurgeHostedServiceTests
{
    [TestMethod]
    public async Task Purge_failure_is_retried_on_the_next_tick()
    {
        var store = new ControllableInboxStore { FailFirstPurge = true };
        var now = new DateTimeOffset(2026, 7, 19, 12, 0, 0, TimeSpan.Zero);
        var timeProvider = new FixedUtcTimeProvider(now);
        var options = new InboxOptions
        {
            RetentionPeriod = TimeSpan.FromDays(7),
            CleanupInterval = TimeSpan.FromMilliseconds(5),
        };
        var sut = new InboxPurgeHostedService(
            store,
            "Billing",
            options,
            timeProvider,
            NullLogger<InboxPurgeHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await store.SecondPurge.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None);

        Assert.IsGreaterThanOrEqualTo(2, store.PurgeCalls);
        Assert.AreEqual(now - options.RetentionPeriod, store.LastCutoff);
        Assert.AreEqual("Billing", store.LastEndpointId, "Cleanup must stay scoped to this subscriber's endpoint");
    }

    [TestMethod]
    public async Task Stop_cancels_an_in_progress_purge()
    {
        var store = new ControllableInboxStore { BlockUntilCancelled = true };
        var options = new InboxOptions
        {
            CleanupInterval = TimeSpan.FromMilliseconds(5),
        };
        var sut = new InboxPurgeHostedService(
            store,
            "Billing",
            options,
            TimeProvider.System,
            NullLogger<InboxPurgeHostedService>.Instance);

        await sut.StartAsync(CancellationToken.None);
        await store.PurgeStarted.WaitAsync(TimeSpan.FromSeconds(5));
        await sut.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.IsTrue(store.ObservedCancellation);
    }

    private static readonly DateTimeOffset PassStart = new(2026, 11, 27, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task One_tick_drains_a_backlog_larger_than_one_batch()
    {
        var clock = new ManualTimeProvider(PassStart);
        var store = new BatchedInboxStore(remaining: 2_500, batchSize: 1_000);
        var logger = new CapturingLogger();
        var options = new InboxOptions { RetentionPeriod = TimeSpan.FromDays(7) };
        var sut = new InboxPurgeHostedService(store, "Billing", options, clock, logger);

        await sut.StartAsync(CancellationToken.None);
        await clock.FireTimerAsync();
        await logger.WaitForAsync(e => e.Level == LogLevel.Information, 1);
        await sut.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, clock.FireCount, "The backlog must drain without a second timer tick");
        CollectionAssert.AreEqual(new[] { 1_000, 1_000, 500, 0 }, store.Results.ToArray());
        Assert.AreEqual(0, store.Remaining);
        Assert.IsTrue(store.Calls.All(c => c.EndpointId == "Billing"), "Every call must stay scoped to the endpoint");
        Assert.IsTrue(
            store.Calls.All(c => c.OlderThan == PassStart - options.RetentionPeriod),
            "Every call in one pass must use the cutoff computed at the start of the pass");

        var info = logger.Entries.Where(e => e.Level == LogLevel.Information).ToList();
        Assert.HasCount(1, info, "One Information entry per pass, not one per batch");
        Assert.AreEqual(2_500L, Convert.ToInt64(info[0].Value("PurgedRecordCount")));
        Assert.AreEqual("Billing", info[0].Value("EndpointId"));
        Assert.IsFalse(logger.Entries.Any(e => e.Level == LogLevel.Warning), "A drained pass must not warn");
    }

    [TestMethod]
    public async Task Cutoff_stays_fixed_while_the_clock_moves_during_a_pass()
    {
        var clock = new ManualTimeProvider(PassStart);
        var store = new BatchedInboxStore(remaining: 3, batchSize: 1)
        {
            OnCall = (_, _) => clock.Advance(TimeSpan.FromSeconds(10)),
        };
        var logger = new CapturingLogger();
        var options = new InboxOptions { RetentionPeriod = TimeSpan.FromDays(1) };
        var sut = new InboxPurgeHostedService(store, "Billing", options, clock, logger);

        await sut.StartAsync(CancellationToken.None);
        await clock.FireTimerAsync();
        await logger.WaitForAsync(e => e.Level == LogLevel.Information, 1);
        await sut.StopAsync(CancellationToken.None);

        Assert.HasCount(4, store.Calls);
        Assert.IsTrue(store.Calls.All(c => c.OlderThan == PassStart - options.RetentionPeriod));
    }

    [TestMethod]
    public async Task An_empty_store_gets_exactly_one_call_per_tick_and_still_logs()
    {
        var clock = new ManualTimeProvider(PassStart);
        var store = new BatchedInboxStore(remaining: 0, batchSize: 1_000);
        var logger = new CapturingLogger();
        var sut = new InboxPurgeHostedService(store, "Billing", new InboxOptions(), clock, logger);

        await sut.StartAsync(CancellationToken.None);
        await clock.FireTimerAsync();
        await logger.WaitForAsync(e => e.Level == LogLevel.Information, 1);
        Assert.HasCount(1, store.Calls);

        await clock.FireTimerAsync();
        await logger.WaitForAsync(e => e.Level == LogLevel.Information, 2);
        await sut.StopAsync(CancellationToken.None);

        Assert.HasCount(2, store.Calls);
        var info = logger.Entries.Where(e => e.Level == LogLevel.Information).ToList();
        Assert.HasCount(2, info);
        Assert.IsTrue(info.All(e => Convert.ToInt64(e.Value("PurgedRecordCount")) == 0));
    }

    [TestMethod]
    public async Task The_time_budget_ends_a_pass_and_the_next_tick_starts_a_new_one()
    {
        var clock = new ManualTimeProvider(PassStart);
        var store = new BatchedInboxStore(remaining: int.MaxValue, batchSize: 10)
        {
            OnCall = (_, _) => clock.Advance(TimeSpan.FromMinutes(1)),
        };
        var logger = new CapturingLogger();
        var options = new InboxOptions { CleanupTimeBudget = TimeSpan.FromMinutes(5) };
        var sut = new InboxPurgeHostedService(store, "Billing", options, clock, logger);

        await sut.StartAsync(CancellationToken.None);
        await clock.FireTimerAsync();
        await logger.WaitForAsync(e => e.Level == LogLevel.Warning, 1);
        await Task.Delay(50);
        Assert.HasCount(5, store.Calls, "The budget, not the backlog, bounds the number of calls");

        await clock.FireTimerAsync();
        await logger.WaitForAsync(e => e.Level == LogLevel.Warning, 2);
        await sut.StopAsync(CancellationToken.None);

        Assert.HasCount(10, store.Calls, "A later tick starts a fresh pass with a fresh budget");
        var warning = logger.Entries.First(e => e.Level == LogLevel.Warning);
        Assert.AreEqual("Billing", warning.Value("EndpointId"));
        Assert.AreEqual(50L, Convert.ToInt64(warning.Value("PurgedRecordCount")));
        Assert.AreEqual(TimeSpan.FromMinutes(5), warning.Value("CleanupTimeBudget"));
        var info = logger.Entries.Where(e => e.Level == LogLevel.Information).ToList();
        Assert.HasCount(2, info, "A budget-exhausted pass still writes its one Information total");
        Assert.AreEqual(50L, Convert.ToInt64(info[0].Value("PurgedRecordCount")));
    }

    [TestMethod]
    public async Task The_budget_does_not_abort_a_call_that_is_already_running()
    {
        var clock = new ManualTimeProvider(PassStart);
        var store = new BatchedInboxStore(remaining: int.MaxValue, batchSize: 10)
        {
            OnCall = (_, _) => clock.Advance(TimeSpan.FromMinutes(10)),
        };
        var logger = new CapturingLogger();
        var options = new InboxOptions { CleanupTimeBudget = TimeSpan.FromMinutes(5) };
        var sut = new InboxPurgeHostedService(store, "Billing", options, clock, logger);

        await sut.StartAsync(CancellationToken.None);
        await clock.FireTimerAsync();
        await logger.WaitForAsync(e => e.Level == LogLevel.Warning, 1);
        await sut.StopAsync(CancellationToken.None);

        Assert.HasCount(1, store.Calls);
        Assert.IsFalse(store.Calls[0].ObservedCancellation, "The running call must complete, not be cancelled");
        var info = logger.Entries.Single(e => e.Level == LogLevel.Information);
        Assert.AreEqual(10L, Convert.ToInt64(info.Value("PurgedRecordCount")), "The overrunning call's count must be included");
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task Stopping_between_batches_makes_no_further_call_and_is_not_a_failure()
    {
        using var failures = new FailureCounter();
        var clock = new ManualTimeProvider(PassStart);
        var logger = new CapturingLogger();
        InboxPurgeHostedService? sut = null;
        Task? stopTask = null;
        var store = new BatchedInboxStore(remaining: int.MaxValue, batchSize: 1_000);
        store.OnCall = (call, _) =>
        {
            // Cancel the stopping token while the second batch is running; the batch ignores
            // the token and completes, so cancellation is first observed between batches.
            if (call == 2)
                stopTask = sut!.StopAsync(CancellationToken.None);
        };
        sut = new InboxPurgeHostedService(store, "Billing", new InboxOptions(), clock, logger);

        await sut.StartAsync(CancellationToken.None);
        await clock.FireTimerAsync();
        await store.WaitForCallsAsync(2);
        await WaitUntilAsync(() => stopTask is not null);
        await stopTask!.WaitAsync(WaitTimeout);

        Assert.HasCount(2, store.Calls);
        Assert.AreEqual(0L, failures.Total);
        Assert.IsFalse(logger.Entries.Any(e => e.Level == LogLevel.Warning));
    }

    [TestMethod]
    [DoNotParallelize]
    public async Task A_failure_mid_pass_ends_the_pass_counts_once_and_the_next_tick_retries()
    {
        using var failures = new FailureCounter();
        var clock = new ManualTimeProvider(PassStart);
        var logger = new CapturingLogger();
        var store = new BatchedInboxStore(remaining: 5_000, batchSize: 1_000)
        {
            OnCall = (call, _) =>
            {
                if (call == 2)
                    throw new InvalidOperationException("provider details");
            },
        };
        var sut = new InboxPurgeHostedService(store, "Billing", new InboxOptions(), clock, logger);

        await sut.StartAsync(CancellationToken.None);
        await clock.FireTimerAsync();
        await logger.WaitForAsync(e => e.Level == LogLevel.Warning, 1);
        await Task.Delay(50);
        Assert.HasCount(2, store.Calls, "The pass must end at the failing call");
        Assert.AreEqual(1L, failures.Total);
        Assert.AreEqual("purge", failures.LastOperation);

        await clock.FireTimerAsync();
        await logger.WaitForAsync(e => e.Level == LogLevel.Information, 1);
        await sut.StopAsync(CancellationToken.None);

        Assert.IsGreaterThan(2, store.Calls.Count, "The next tick must start a new pass");
        Assert.AreEqual(1L, failures.Total);
    }

    private sealed class ControllableInboxStore : IInboxStore
    {
        private readonly TaskCompletionSource _purgeStarted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _secondPurge =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool FailFirstPurge { get; set; }
        public bool BlockUntilCancelled { get; set; }
        public int PurgeCalls { get; private set; }
        public string? LastEndpointId { get; private set; }
        public DateTimeOffset LastCutoff { get; private set; }
        public bool ObservedCancellation { get; private set; }
        public Task PurgeStarted => _purgeStarted.Task;
        public Task SecondPurge => _secondPurge.Task;

        public Task<bool> HasProcessedAsync(
            string endpointId,
            string messageId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task RecordProcessedAsync(
            string endpointId,
            string messageId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public async Task<int> PurgeExpiredAsync(
            string endpointId,
            DateTimeOffset olderThan,
            CancellationToken cancellationToken = default)
        {
            PurgeCalls++;
            LastEndpointId = endpointId;
            LastCutoff = olderThan;
            _purgeStarted.TrySetResult();

            if (FailFirstPurge && PurgeCalls == 1)
                throw new InvalidOperationException("provider details");

            if (PurgeCalls >= 2)
                _secondPurge.TrySetResult();

            if (BlockUntilCancelled)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    ObservedCancellation = true;
                    throw;
                }
            }

            return 0;
        }
    }

    private sealed class FixedUtcTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _utcNow;

        public FixedUtcTimeProvider(DateTimeOffset utcNow)
        {
            _utcNow = utcNow;
        }

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) =>
            TimeProvider.System.CreateTimer(callback, state, dueTime, period);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
                Assert.Fail("Timed out waiting for the condition.");
            await Task.Delay(5);
        }
    }

    private sealed record PurgeCall(string EndpointId, DateTimeOffset OlderThan)
    {
        public bool ObservedCancellation { get; set; }
    }

    /// <summary>Holds a backlog and removes at most <c>batchSize</c> rows per call.</summary>
    private sealed class BatchedInboxStore : IInboxStore
    {
        private readonly int _batchSize;
        private readonly object _gate = new();
        private readonly List<PurgeCall> _calls = [];
        private readonly List<int> _results = [];

        public BatchedInboxStore(int remaining, int batchSize)
        {
            Remaining = remaining;
            _batchSize = batchSize;
        }

        public int Remaining { get; private set; }
        public Action<int, CancellationToken>? OnCall { get; set; }

        public IReadOnlyList<PurgeCall> Calls
        {
            get { lock (_gate) return _calls.ToList(); }
        }

        public IReadOnlyList<int> Results
        {
            get { lock (_gate) return _results.ToList(); }
        }

        public Task WaitForCallsAsync(int count) => WaitUntilAsync(() => Calls.Count >= count);

        public Task<bool> HasProcessedAsync(
            string endpointId,
            string messageId,
            CancellationToken cancellationToken = default) => Task.FromResult(false);

        public Task RecordProcessedAsync(
            string endpointId,
            string messageId,
            CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<int> PurgeExpiredAsync(
            string endpointId,
            DateTimeOffset olderThan,
            CancellationToken cancellationToken = default)
        {
            var call = new PurgeCall(endpointId, olderThan);
            int callNumber;
            lock (_gate)
            {
                _calls.Add(call);
                callNumber = _calls.Count;
            }

            OnCall?.Invoke(callNumber, cancellationToken);
            call.ObservedCancellation = cancellationToken.IsCancellationRequested;

            var removed = Math.Min(_batchSize, Remaining);
            if (Remaining != int.MaxValue)
                Remaining -= removed;
            lock (_gate)
                _results.Add(removed);
            return Task.FromResult(removed);
        }
    }

    /// <summary>A clock whose time and timestamps move only when told, and whose timers fire on demand.</summary>
    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly DateTimeOffset _start;
        private readonly ConcurrentQueue<ManualTimer> _timers = new();
        private long _elapsedTicks;
        private int _fireCount;

        public ManualTimeProvider(DateTimeOffset start)
        {
            _start = start;
        }

        public int FireCount => Volatile.Read(ref _fireCount);

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _elapsedTicks);

        public override DateTimeOffset GetUtcNow() => _start + TimeSpan.FromTicks(Interlocked.Read(ref _elapsedTicks));

        public void Advance(TimeSpan by) => Interlocked.Add(ref _elapsedTicks, by.Ticks);

        public async Task FireTimerAsync()
        {
            await WaitUntilAsync(() => !_timers.IsEmpty);
            Interlocked.Increment(ref _fireCount);
            foreach (var timer in _timers)
                timer.Fire();
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(callback, state);
            _timers.Enqueue(timer);
            return timer;
        }

        private sealed class ManualTimer(TimerCallback callback, object? state) : ITimer
        {
            private volatile bool _disposed;

            public void Fire()
            {
                if (!_disposed)
                    callback(state);
            }

            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                _disposed = true;
                return ValueTask.CompletedTask;
            }
        }
    }

    private sealed record LogEntry(LogLevel Level, string Message, IReadOnlyList<KeyValuePair<string, object?>> State)
    {
        public object? Value(string key) => State.FirstOrDefault(kv => kv.Key == key).Value;
    }

    private sealed class CapturingLogger : ILogger<InboxPurgeHostedService>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => _entries.ToList();

        public Task WaitForAsync(Func<LogEntry, bool> predicate, int count) =>
            WaitUntilAsync(() => _entries.Count(predicate) >= count);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var values = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
            _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), values.ToList()));
        }
    }

    /// <summary>Counts <see cref="NimBusMeters.InboxOperationFailed"/> increments while alive.</summary>
    private sealed class FailureCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;

        public FailureCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (ReferenceEquals(instrument, NimBusMeters.InboxOperationFailed))
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, tags, _) =>
            {
                foreach (var tag in tags)
                {
                    if (tag.Key == MessagingAttributes.NimBusStoreOperation)
                        LastOperation = tag.Value as string;
                }

                Interlocked.Add(ref _total, value);
            });
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);
        public string? LastOperation { get; private set; }

        public void Dispose() => _listener.Dispose();
    }
}
