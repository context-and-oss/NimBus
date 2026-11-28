#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.Metrics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;

namespace NimBus.Core.Tests;

/// <summary>
/// <see cref="OutboxDispatcher"/> against an outbox that implements <see cref="IOutboxClaiming"/>:
/// retry backoff, parking, session ordering and multi-replica claiming.
/// </summary>
[TestClass]
public sealed class OutboxDispatcherClaimingTests
{
    [TestMethod]
    public async Task A_poison_session_filling_the_batch_does_not_starve_other_sessions()
    {
        var outbox = new ConformanceInMemoryOutbox();
        var sessionA = Enumerable.Range(0, 10).Select(position => CreateRow("A", position)).ToList();
        var sessionB = CreateRow("B", 0);
        await outbox.StoreBatchAsync(sessionA.Append(sessionB).ToList());
        var sender = new ScriptedSender(failing: sessionA[0].MessageId);
        var dispatcher = new OutboxDispatcher(outbox, sender);

        var polls = 0;
        while (!sender.Sent.Contains(sessionB.MessageId) && polls < 2)
        {
            await dispatcher.DispatchPendingAsync(batchSize: 10);
            polls++;
        }

        CollectionAssert.AreEqual(new[] { sessionB.MessageId }, sender.Sent.ToArray(), "Only session B may be sent; A waits behind its failed row.");
        Assert.AreEqual(1, sender.AttemptsOf(sessionA[0].MessageId));
        Assert.AreEqual(1, outbox.GetAttemptCount(sessionA[0].Id));
        Assert.IsTrue(sessionA.Skip(1).All(row => !outbox.IsLeased(row.Id)), "Rows queued behind the failed row are released.");
    }

    [TestMethod]
    public async Task A_poison_row_is_parked_after_MaxAttempts_and_keeps_its_session_behind_it()
    {
        var outbox = new ConformanceInMemoryOutbox();
        var a1 = CreateRow("A", 0);
        var a2 = CreateRow("A", 1);
        var b1 = CreateRow("B", 0);
        await outbox.StoreBatchAsync([a1, a2, b1]);
        var sender = new ScriptedSender(failing: a1.MessageId);
        var options = new OutboxDispatcherOptions { MaxAttempts = 3 };
        var dispatcher = new OutboxDispatcher(outbox, sender, options, logger: null);
        using var parked = new ParkedCounter();

        for (var poll = 0; poll < 8; poll++)
        {
            await dispatcher.DispatchPendingAsync(batchSize: 10);
            outbox.AdvanceClock(options.MaxRetryDelay);
        }

        Assert.AreEqual(3, sender.AttemptsOf(a1.MessageId), "The row stops being attempted once parked.");
        Assert.IsTrue(outbox.IsParked(a1.Id));
        Assert.AreEqual(3, outbox.GetAttemptCount(a1.Id));
        StringAssert.Contains(outbox.GetLastError(a1.Id), "poison", StringComparison.Ordinal);
        CollectionAssert.AreEqual(new[] { b1.MessageId }, sender.Sent.ToArray(), "A2 is never sent past its parked predecessor.");
        Assert.AreEqual(1L, parked.Total);
    }

    [TestMethod]
    public async Task A_transient_failure_keeps_session_order_and_sends_each_row_once()
    {
        var outbox = new ConformanceInMemoryOutbox();
        var session = Enumerable.Range(0, 5).Select(position => CreateRow("A", position)).ToList();
        await outbox.StoreBatchAsync(session);
        var sender = new ScriptedSender(failing: session[0].MessageId, failures: 2);
        var options = new OutboxDispatcherOptions();
        var dispatcher = new OutboxDispatcher(outbox, sender, options, logger: null);

        for (var poll = 0; poll < 4; poll++)
        {
            await dispatcher.DispatchPendingAsync(batchSize: 10);
            outbox.AdvanceClock(options.MaxRetryDelay);
        }

        CollectionAssert.AreEqual(session.Select(row => row.MessageId).ToArray(), sender.Sent.ToArray());
        Assert.AreEqual(3, sender.AttemptsOf(session[0].MessageId));
        Assert.AreEqual(0, (await outbox.GetPendingAsync(10)).Count);
    }

    [TestMethod]
    public async Task Failed_attempts_back_off_exponentially_up_to_MaxRetryDelay()
    {
        var outbox = new ConformanceInMemoryOutbox();
        var row = CreateRow("A", 0);
        await outbox.StoreAsync(row);
        var sender = new ScriptedSender(failing: row.MessageId, failures: int.MaxValue);
        var options = new OutboxDispatcherOptions
        {
            InitialRetryDelay = TimeSpan.FromSeconds(1),
            MaxRetryDelay = TimeSpan.FromSeconds(5),
        };
        var dispatcher = new OutboxDispatcher(outbox, sender, options, logger: null);

        var delays = new List<TimeSpan?>();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            await dispatcher.DispatchPendingAsync(batchSize: 10);
            delays.Add(outbox.GetLastRetryDelay(row.Id));

            // Not due yet: the row is not attempted again before its retry time.
            await dispatcher.DispatchPendingAsync(batchSize: 10);
            Assert.AreEqual(attempt + 1, sender.AttemptsOf(row.MessageId));
            outbox.AdvanceClock(options.MaxRetryDelay);
        }

        CollectionAssert.AreEqual(
            new TimeSpan?[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(5) },
            delays);
    }

    [TestMethod]
    public async Task An_unreadable_payload_is_parked_on_its_first_attempt()
    {
        var outbox = new ConformanceInMemoryOutbox();
        var unreadable = CreateRow("A", 0);
        unreadable.Payload = "{invalid json";
        var next = CreateRow("A", 1);
        await outbox.StoreBatchAsync([unreadable, next]);
        var sender = new ScriptedSender();

        await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10);

        Assert.IsTrue(outbox.IsParked(unreadable.Id));
        Assert.AreEqual(0, sender.Sent.Count);
    }

    [TestMethod]
    public async Task Rows_without_a_session_are_not_held_back_by_a_failed_row()
    {
        var outbox = new ConformanceInMemoryOutbox();
        var failing = CreateRow(null, 0);
        var healthy = CreateRow(null, 1);
        await outbox.StoreBatchAsync([failing, healthy]);
        var sender = new ScriptedSender(failing: failing.MessageId);

        var dispatched = await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10);

        Assert.AreEqual(1, dispatched);
        CollectionAssert.AreEqual(new[] { healthy.MessageId }, sender.Sent.ToArray());
    }

    [TestMethod]
    public async Task Concurrent_dispatchers_send_each_row_exactly_once_in_session_order()
    {
        var outbox = new ConformanceInMemoryOutbox();
        var rows = Enumerable.Range(0, 25)
            .SelectMany(position => Enumerable.Range(0, 6).Select(session => CreateRow($"session-{session}", position)))
            .ToList();
        await outbox.StoreBatchAsync(rows);
        var sent = new ConcurrentQueue<string?>();
        var dispatchers = Enumerable.Range(0, 3)
            .Select(replica => new OutboxDispatcher(outbox, new ScriptedSender(sink: sent, yieldOnSend: true), new OutboxDispatcherOptions { DispatcherId = $"replica-{replica}" }, logger: null))
            .ToList();

        await Task.WhenAll(dispatchers.Select(dispatcher => Task.Run(async () =>
        {
            for (var poll = 0; poll < 500 && (await outbox.GetPendingAsync(1)).Count > 0; poll++)
            {
                if (await dispatcher.DispatchPendingAsync(batchSize: 4) == 0)
                    await Task.Yield();
            }
        })));

        var sentInOrder = sent.ToArray();
        Assert.AreEqual(rows.Count, sentInOrder.Length, "Each row must be sent exactly once.");
        CollectionAssert.AreEquivalent(rows.Select(row => row.MessageId).ToArray(), sentInOrder);
        foreach (var session in rows.GroupBy(row => row.SessionId))
        {
            var expected = session.Select(row => row.MessageId).ToArray();
            CollectionAssert.AreEqual(expected, sentInOrder.Where(expected.Contains).ToArray(), $"Session {session.Key} was sent out of order.");
        }
    }

    [TestMethod]
    public async Task Rows_left_when_half_the_lease_is_used_are_released_unsent()
    {
        var outbox = new ConformanceInMemoryOutbox();
        var first = CreateRow("A", 0);
        var second = CreateRow("B", 0);
        await outbox.StoreBatchAsync([first, second]);
        var options = new OutboxDispatcherOptions { LeaseDuration = TimeSpan.FromSeconds(1) };
        var sender = new ScriptedSender(sendDelay: TimeSpan.FromMilliseconds(600));

        var dispatched = await new OutboxDispatcher(outbox, sender, options, logger: null).DispatchPendingAsync(batchSize: 10);

        Assert.AreEqual(1, dispatched);
        CollectionAssert.AreEqual(new[] { first.MessageId }, sender.Sent.ToArray());
        Assert.IsFalse(outbox.IsLeased(second.Id));
        Assert.AreEqual(0, outbox.GetAttemptCount(second.Id));
    }

    [TestMethod]
    public async Task Backoff_reaches_MaxRetryDelay_with_a_tiny_initial_delay_and_many_attempts()
    {
        // With a one-tick initial delay the cap of five minutes is reached at attempt 33
        // (2^32 ticks is about 430 s); a clamped exponent would stop growing at about 107 s.
        var options = new OutboxDispatcherOptions
        {
            InitialRetryDelay = TimeSpan.FromTicks(1),
            MaxRetryDelay = TimeSpan.FromMinutes(5),
            MaxAttempts = 40,
        };
        Assert.AreEqual(TimeSpan.FromTicks(1), options.GetRetryDelay(1));
        Assert.AreEqual(TimeSpan.FromTicks(1L << 31), options.GetRetryDelay(32));
        Assert.AreEqual(options.MaxRetryDelay, options.GetRetryDelay(33));
        Assert.AreEqual(options.MaxRetryDelay, options.GetRetryDelay(39));
        Assert.AreEqual(options.MaxRetryDelay, options.GetRetryDelay(int.MaxValue));

        var outbox = new ConformanceInMemoryOutbox();
        var row = CreateRow("A", 0);
        await outbox.StoreAsync(row);
        var sender = new ScriptedSender(failing: row.MessageId);
        var dispatcher = new OutboxDispatcher(outbox, sender, options, logger: null);
        var delays = new List<TimeSpan?>();
        for (var attempt = 1; attempt < options.MaxAttempts; attempt++)
        {
            await dispatcher.DispatchPendingAsync(batchSize: 10);
            delays.Add(outbox.GetLastRetryDelay(row.Id));
            outbox.AdvanceClock(options.MaxRetryDelay);
        }

        Assert.AreEqual(options.MaxRetryDelay, delays[^1], "The delay before the last retry is the cap.");
        await dispatcher.DispatchPendingAsync(batchSize: 10);
        Assert.IsTrue(outbox.IsParked(row.Id), "The 40th failure parks the row.");
        Assert.AreEqual(options.MaxAttempts, sender.AttemptsOf(row.MessageId));
    }

    [TestMethod]
    public async Task A_dispatcher_that_lost_its_claim_mid_send_stops_and_leaves_the_rows_to_the_new_owner()
    {
        var outbox = new ConformanceInMemoryOutbox();
        var a1 = CreateRow("A", 0);
        var a2 = CreateRow("A", 1);
        var b1 = CreateRow("B", 0);
        await outbox.StoreBatchAsync([a1, a2, b1]);
        var options = new OutboxDispatcherOptions { DispatcherId = "replica-a" };
        IReadOnlyList<OutboxMessage>? claimedByB = null;
        // While A1 is being sent, the store's clock passes A's lease (a paused process, a slow
        // broker) and replica B claims every row.
        var sender = new ScriptedSender(onSend: async message =>
        {
            if (message.MessageId != a1.MessageId)
                return;
            outbox.AdvanceClock(options.LeaseDuration + TimeSpan.FromSeconds(1));
            claimedByB = await outbox.ClaimPendingAsync(10, "replica-b", TimeSpan.FromMinutes(5));
        });
        var logger = new ListLogger();

        var dispatched = await new OutboxDispatcher(outbox, sender, options, logger).DispatchPendingAsync(batchSize: 10);

        Assert.AreEqual(0, dispatched, "A's checkpoint of A1 is fenced out by B's claim.");
        CollectionAssert.AreEqual(new[] { a1.MessageId }, sender.Sent.ToArray(), "A must not send A2 or B1 once it lost its claim.");
        Assert.IsNotNull(claimedByB);
        CollectionAssert.AreEqual(new[] { a1.Id, a2.Id, b1.Id }, claimedByB.Select(row => row.Id).ToArray());
        Assert.IsNull(outbox.GetDispatchedAtUtc(a1.Id), "A's stale checkpoint must not mark B's row dispatched.");
        Assert.IsTrue(outbox.IsLeased(a2.Id) && outbox.IsLeased(b1.Id), "A's release must not free B's claims.");
        Assert.IsTrue(
            logger.Entries.Any(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("lost its claim", StringComparison.Ordinal)),
            "Losing the claim is logged as a warning.");

        // B, the new owner, sends the session in order and checkpoints it.
        CollectionAssert.AreEquivalent(
            new[] { a1.Id, a2.Id, b1.Id },
            (await outbox.MarkClaimedAsDispatchedAsync("replica-b", [a1.Id, a2.Id, b1.Id])).ToArray());
    }

    [TestMethod]
    public async Task A_send_still_running_near_the_end_of_the_lease_is_cancelled_and_counted_as_a_failed_attempt()
    {
        var outbox = new ConformanceInMemoryOutbox();
        var a1 = CreateRow("A", 0);
        var a2 = CreateRow("A", 1);
        await outbox.StoreBatchAsync([a1, a2]);
        var options = new OutboxDispatcherOptions { LeaseDuration = TimeSpan.FromSeconds(2) };
        // Honours cancellation, but would otherwise run far past the lease.
        var sender = new ScriptedSender(sendDelay: TimeSpan.FromMinutes(5));
        var logger = new ListLogger();
        var started = System.Diagnostics.Stopwatch.StartNew();

        var dispatched = await new OutboxDispatcher(outbox, sender, options, logger).DispatchPendingAsync(batchSize: 10);

        Assert.IsTrue(started.Elapsed < options.LeaseDuration, $"The send must be cut off before the lease expires; took {started.Elapsed}.");
        Assert.AreEqual(0, dispatched);
        Assert.AreEqual(0, sender.Sent.Count);
        Assert.AreEqual(1, outbox.GetAttemptCount(a1.Id), "The cut-off send is a failed attempt, backed off like any other.");
        StringAssert.Contains(outbox.GetLastError(a1.Id), "lease", StringComparison.Ordinal);
        Assert.AreEqual(0, sender.AttemptsOf(a2.MessageId), "A2 stays behind its failed predecessor.");
        Assert.AreEqual(0, outbox.GetAttemptCount(a2.Id));
        Assert.IsFalse(outbox.IsLeased(a2.Id));
        Assert.IsTrue(logger.Entries.Any(entry => entry.Level == LogLevel.Warning && entry.Message.Contains("lease was about to expire", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task An_outbox_without_claiming_logs_the_single_dispatcher_requirement_once_as_a_warning()
    {
        var outbox = new PlainOutbox();
        var logger = new ListLogger();
        var dispatcher = new OutboxDispatcher(outbox, new ScriptedSender(), logger);

        await dispatcher.DispatchPendingAsync();
        await dispatcher.DispatchPendingAsync();

        var diagnostics = logger.Entries.Where(entry => entry.Message.Contains("only one dispatcher instance", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(1, diagnostics.Count);
        Assert.AreEqual(LogLevel.Warning, diagnostics[0].Level);
    }

    [TestMethod]
    public async Task Existing_constructor_calls_with_null_still_bind_to_the_logger_constructors()
    {
        // Compile-level guard: these calls compiled before OutboxDispatcherOptions existed and
        // must keep compiling, unambiguously, with the logger constructors.
        var outbox = new ConformanceInMemoryOutbox();
        var row = CreateRow("A", 0);
        await outbox.StoreAsync(row);
        var sender = new ScriptedSender();

        var withNullLogger = new OutboxDispatcher(outbox, sender, null);
        var withTimeoutAndNullLogger = new OutboxDispatcher(outbox, sender, TimeSpan.FromSeconds(1), null);

        Assert.AreEqual(1, await withNullLogger.DispatchPendingAsync(batchSize: 10));
        Assert.AreEqual(0, await withTimeoutAndNullLogger.DispatchPendingAsync(batchSize: 10));
    }

    [TestMethod]
    public void Options_validation_rejects_out_of_range_settings()
    {
        var outbox = new ConformanceInMemoryOutbox();
        var sender = new ScriptedSender();

        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OutboxDispatcher(outbox, sender, new OutboxDispatcherOptions { MaxAttempts = 0 }, logger: null));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OutboxDispatcher(outbox, sender, new OutboxDispatcherOptions { InitialRetryDelay = TimeSpan.Zero }, logger: null));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OutboxDispatcher(outbox, sender, new OutboxDispatcherOptions { MaxRetryDelay = TimeSpan.FromMilliseconds(1) }, logger: null));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new OutboxDispatcher(outbox, sender, new OutboxDispatcherOptions { LeaseDuration = TimeSpan.FromMilliseconds(10) }, logger: null));
        Assert.ThrowsExactly<ArgumentNullException>(() => new OutboxDispatcher(outbox, sender, (OutboxDispatcherOptions)null!, logger: null));
    }

    [TestMethod]
    public void DispatcherId_defaults_to_a_unique_value_and_honours_the_option()
    {
        var outbox = new ConformanceInMemoryOutbox();
        var sender = new ScriptedSender();

        var first = new OutboxDispatcher(outbox, sender);
        var second = new OutboxDispatcher(outbox, sender);
        var named = new OutboxDispatcher(outbox, sender, new OutboxDispatcherOptions { DispatcherId = "replica-1" }, logger: null);

        Assert.AreNotEqual(first.DispatcherId, second.DispatcherId);
        Assert.AreEqual("replica-1", named.DispatcherId);
    }

    private static OutboxMessage CreateRow(string? sessionId, int position)
    {
        var messageId = $"{sessionId ?? "none"}:{position:D3}";
        return new OutboxMessage
        {
            Id = $"outbox-{messageId}",
            MessageId = messageId,
            To = "Test",
            SessionId = sessionId,
            Payload = JsonConvert.SerializeObject(new Message
            {
                MessageId = messageId,
                To = "Test",
                SessionId = sessionId!,
                MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "OrderPlaced" } },
            }),
            CreatedAtUtc = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        };
    }

    private sealed class ScriptedSender(
        string? failing = null,
        int failures = int.MaxValue,
        ConcurrentQueue<string?>? sink = null,
        bool yieldOnSend = false,
        TimeSpan sendDelay = default,
        Func<IMessage, Task>? onSend = null) : ISender
    {
        private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

        public ConcurrentQueue<string?> Sent { get; } = sink ?? new ConcurrentQueue<string?>();

        public int AttemptsOf(string? messageId) =>
            messageId is not null && _attempts.TryGetValue(messageId, out var attempts) ? attempts : 0;

        public async Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            var attempt = _attempts.AddOrUpdate(message.MessageId!, 1, (_, attempts) => attempts + 1);
            if (sendDelay > TimeSpan.Zero)
                await Task.Delay(sendDelay, cancellationToken);
            else if (yieldOnSend)
                await Task.Yield();

            if (onSend is not null)
                await onSend(message);

            if (string.Equals(message.MessageId, failing, StringComparison.Ordinal) && attempt <= failures)
                throw new InvalidOperationException("poison");

            Sent.Enqueue(message.MessageId);
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed record LogEntry(LogLevel Level, string Message);

    private sealed class ListLogger : ILogger<OutboxDispatcher>
    {
        private readonly ConcurrentQueue<LogEntry> _entries = new();

        public IReadOnlyList<LogEntry> Entries => _entries.ToArray();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _entries.Enqueue(new LogEntry(logLevel, formatter(state, exception)));
    }

    /// <summary>An outbox that implements only <see cref="IOutbox"/> and is always empty.</summary>
    private sealed class PlainOutbox : IOutbox
    {
        public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OutboxMessage>>([]);

        public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class ParkedCounter : IDisposable
    {
        private readonly MeterListener _listener = new();
        private long _total;

        public ParkedCounter()
        {
            _listener.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name == "nimbus.outbox.parked")
                    listener.EnableMeasurementEvents(instrument);
            };
            _listener.SetMeasurementEventCallback<long>((_, value, _, _) => Interlocked.Add(ref _total, value));
            _listener.Start();
        }

        public long Total => Interlocked.Read(ref _total);

        public void Dispose() => _listener.Dispose();
    }

    // ── Batched dispatch (a sender that implements IBatchingSender) ─────────────────────

    [TestMethod]
    public async Task Batching_interleaved_sessions_go_out_as_one_batch_per_session()
    {
        var log = new List<string>();
        var (outbox, rows) = await StoreAsync(log, ("A", 0), ("B", 0), ("A", 1), ("B", 1));
        var sender = new BatchingSender(log);

        var dispatched = await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10);

        Assert.AreEqual(4, dispatched);
        AssertBatches(sender, ["A:000", "A:001"], ["B:000", "B:001"]);
        Assert.AreEqual(0, sender.SingleAttempts.Count, "No row is sent through ISender.Send.");
        Assert.AreEqual(sender.Created, sender.Disposed, "Every batch is disposed.");
    }

    [TestMethod]
    public async Task Batching_checkpoints_each_batch_with_one_fenced_call()
    {
        var log = new List<string>();
        var (outbox, rows) = await StoreAsync(log, ("A", 0), ("B", 0), ("A", 1), ("B", 1));
        var sender = new BatchingSender(log);

        var dispatched = await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10);

        Assert.AreEqual(4, dispatched);
        Assert.AreEqual(2, outbox.Checkpoints.Count);
        CollectionAssert.AreEqual(new[] { rows[0].Id, rows[2].Id }, outbox.Checkpoints[0]);
        CollectionAssert.AreEqual(new[] { rows[1].Id, rows[3].Id }, outbox.Checkpoints[1]);
        Assert.IsTrue(rows.All(row => outbox.Inner.GetDispatchedAtUtc(row.Id) is not null));
    }

    [TestMethod]
    public async Task Batching_a_destination_change_in_a_session_starts_a_new_batch()
    {
        var log = new List<string>();
        var (outbox, _) = await StoreAsync(log, ("A", 0, "X"), ("A", 1, "X"), ("A", 2, "Y"), ("A", 3, "Y"));
        var sender = new BatchingSender(log);

        Assert.AreEqual(4, await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10));

        AssertBatches(sender, ["A:000", "A:001"], ["A:002", "A:003"]);
    }

    [TestMethod]
    public async Task Batching_a_scheduled_row_is_sent_alone_between_its_sessions_batches()
    {
        var log = new List<string>();
        var a0 = CreateRow("A", 0);
        var a1 = CreateRow("A", 1);
        a1.ScheduledEnqueueTimeUtc = new DateTime(2031, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var a2 = CreateRow("A", 2);
        var a3 = CreateRow("A", 3);
        var outbox = new RecordingOutbox(log);
        await outbox.StoreBatchAsync([a0, a1, a2, a3]);
        var sender = new BatchingSender(log);

        Assert.AreEqual(4, await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10));

        CollectionAssert.AreEqual(
            new[] { "send [A:000]", "schedule A:001", "send [A:002, A:003]" },
            log.Where(entry => entry.StartsWith("send", StringComparison.Ordinal) || entry.StartsWith("schedule", StringComparison.Ordinal) || entry.StartsWith("single", StringComparison.Ordinal)).ToArray());
    }

    [TestMethod]
    public async Task Batching_a_full_batch_is_sent_and_the_overflowing_row_starts_the_next()
    {
        var log = new List<string>();
        var (outbox, _) = await StoreAsync(log, ("A", 0), ("A", 1), ("A", 2), ("A", 3), ("A", 4));
        var sender = new BatchingSender(log) { Capacity = 2 };

        Assert.AreEqual(5, await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10));

        AssertBatches(sender, ["A:000", "A:001"], ["A:002", "A:003"], ["A:004"]);
        Assert.AreEqual(3, outbox.Checkpoints.Count);
    }

    [TestMethod]
    public async Task Batching_a_row_too_large_for_an_empty_batch_is_sent_alone_and_its_failure_blocks_the_session()
    {
        var log = new List<string>();
        var (outbox, rows) = await StoreAsync(log, ("A", 0), ("A", 1), ("A", 2), ("B", 0));
        var sender = new BatchingSender(log);
        sender.TooLarge.Add("A:001");
        sender.FailSingle.Add("A:001");

        var dispatched = await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10);

        Assert.AreEqual(2, dispatched);
        AssertBatches(sender, ["A:000"], ["B:000"]);
        CollectionAssert.AreEqual(new[] { "A:001" }, sender.SingleAttempts.ToArray());
        CollectionAssert.AreEqual(new[] { rows[1].Id }, outbox.Failures.Select(failure => failure.Id).ToArray());
        Assert.IsFalse(outbox.Failures[0].Park);
        Assert.AreEqual(0, outbox.Inner.GetAttemptCount(rows[2].Id));
        Assert.IsFalse(outbox.Inner.IsLeased(rows[2].Id), "A2 waits behind A1 and is released.");
        Assert.AreEqual(sender.Created, sender.Disposed);
    }

    [TestMethod]
    public async Task Batching_never_mixes_routing_keys_or_destinations_and_keeps_each_session_in_claim_order()
    {
        var log = new List<string>();
        var keys = new[] { "S0", "S1", "S2", null };
        var specs = Enumerable.Range(0, 4)
            .SelectMany(position => keys.Select(key => (key, position, position < 2 ? "X" : "Y")))
            .ToArray();
        var (outbox, rows) = await StoreAsync(log, specs);
        var sender = new BatchingSender(log);

        Assert.AreEqual(rows.Count, await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 100));

        Assert.IsTrue(sender.SentBatches.Count >= 8);
        foreach (var batch in sender.SentBatches)
        {
            Assert.AreEqual(1, batch.Select(message => string.IsNullOrEmpty(message.SessionId) ? "" : message.SessionId).Distinct().Count(), "One routing key per batch.");
            Assert.AreEqual(1, batch.Select(message => message.To).Distinct().Count(), "One destination per batch.");
        }

        var sent = sender.SentBatches.SelectMany(batch => batch).Select(message => message.MessageId).ToArray();
        CollectionAssert.AreEquivalent(rows.Select(row => row.MessageId).ToArray(), sent);
        foreach (var session in rows.GroupBy(row => row.SessionId))
        {
            var expected = session.Select(row => row.MessageId).ToArray();
            CollectionAssert.AreEqual(expected, sent.Where(expected.Contains).ToArray(), $"Session {session.Key} was sent out of order.");
        }
    }

    [TestMethod]
    public async Task Batching_destinations_X_Y_X_in_one_session_go_out_as_three_sends_in_order()
    {
        var log = new List<string>();
        var (outbox, _) = await StoreAsync(log, ("A", 0, "X"), ("A", 1, "Y"), ("A", 2, "X"));
        var sender = new BatchingSender(log);

        Assert.AreEqual(3, await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10));

        AssertBatches(sender, ["A:000"], ["A:001"], ["A:002"]);
    }

    [TestMethod]
    public async Task Batching_a_sessions_next_segment_starts_only_after_the_previous_one_is_sent_and_checkpointed()
    {
        var log = new List<string>();
        var (outbox, rows) = await StoreAsync(log, ("S", 0, "X"), ("B", 0, "X"), ("S", 1, "Y"), ("B", 1, "X"), ("S", 2, "X"));
        var sender = new BatchingSender(log);

        Assert.AreEqual(5, await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10));

        CollectionAssert.AreEqual(
            new[]
            {
                "send [S:000]", $"checkpoint [{rows[0].Id}]",
                "send [B:000, B:001]", $"checkpoint [{rows[1].Id}, {rows[3].Id}]",
                "send [S:001]", $"checkpoint [{rows[2].Id}]",
                "send [S:002]", $"checkpoint [{rows[4].Id}]",
            },
            log.Where(entry => entry.StartsWith("send", StringComparison.Ordinal) || entry.StartsWith("checkpoint", StringComparison.Ordinal)).ToArray());
    }

    [TestMethod]
    public async Task Batching_rows_with_different_enqueue_delays_share_a_batch_with_their_own_delays()
    {
        var log = new List<string>();
        var rows = new[] { CreateRow("A", 0), CreateRow("A", 1), CreateRow("A", 2) };
        rows[1].EnqueueDelayMinutes = 5;
        rows[2].EnqueueDelayMinutes = 10;
        var outbox = new RecordingOutbox(log);
        await outbox.StoreBatchAsync(rows);
        var sender = new BatchingSender(log);

        Assert.AreEqual(3, await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10));

        AssertBatches(sender, ["A:000", "A:001", "A:002"]);
        CollectionAssert.AreEqual(new[] { 0, 5, 10 }, sender.Adds.Select(add => add.Delay).ToArray());
    }

    [TestMethod]
    public async Task Batching_a_poison_row_is_never_batched_and_parks_while_other_sessions_are_batched()
    {
        var log = new List<string>();
        var a0 = CreateRow("A", 0);
        var a1 = CreateRow("A", 1);
        a1.Payload = "{invalid json";
        var a2 = CreateRow("A", 2);
        var b0 = CreateRow("B", 0);
        var b1 = CreateRow("B", 1);
        var outbox = new RecordingOutbox(log);
        await outbox.StoreBatchAsync([a0, a1, a2, b0, b1]);
        var sender = new BatchingSender(log);

        var dispatched = await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10);

        Assert.AreEqual(3, dispatched);
        Assert.IsFalse(sender.Adds.Any(add => add.MessageId == a1.MessageId), "The poison row is never added to a batch.");
        AssertBatches(sender, ["A:000"], ["B:000", "B:001"]);
        Assert.AreEqual(1, outbox.Failures.Count);
        Assert.AreEqual(a1.Id, outbox.Failures[0].Id);
        Assert.IsTrue(outbox.Failures[0].Park);
        Assert.IsFalse(outbox.Inner.IsLeased(a2.Id));
        Assert.AreEqual(0, outbox.Inner.GetAttemptCount(a2.Id));
        Assert.IsNotNull(outbox.Inner.GetDispatchedAtUtc(b1.Id));
    }

    [TestMethod]
    public async Task Batching_a_failed_batch_send_falls_back_to_per_row_sends_that_isolate_the_bad_row()
    {
        var log = new List<string>();
        var (outbox, rows) = await StoreAsync(log, ("A", 0), ("A", 1), ("A", 2), ("B", 0));
        var sender = new BatchingSender(log)
        {
            FailBatchSend = ids => ids.Contains("A:000") ? new InvalidOperationException("batch rejected") : null,
        };
        sender.FailSingle.Add("A:001");

        var dispatched = await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10);

        Assert.AreEqual(2, dispatched);
        CollectionAssert.AreEqual(new[] { "A:000", "A:001" }, sender.SingleAttempts.ToArray(), "The fallback stops at the failed row.");
        AssertBatches(sender, ["B:000"]);
        Assert.IsFalse(outbox.Checkpoints.Any(ids => ids.Contains(rows[1].Id) || ids.Contains(rows[2].Id)), "Nothing of the failed batch is checkpointed by the batch path.");
        CollectionAssert.AreEqual(new[] { rows[0].Id }, outbox.Checkpoints[0]);
        CollectionAssert.AreEqual(new[] { rows[1].Id }, outbox.Failures.Select(failure => failure.Id).ToArray(), "Only the row that really failed gets an attempt.");
        Assert.AreEqual(1, outbox.Inner.GetAttemptCount(rows[1].Id));
        Assert.AreEqual(0, outbox.Inner.GetAttemptCount(rows[2].Id));
        Assert.IsFalse(outbox.Inner.IsLeased(rows[2].Id));
        Assert.IsNull(outbox.Inner.GetDispatchedAtUtc(rows[2].Id));
    }

    [TestMethod]
    public async Task Batching_a_failed_batch_creation_falls_back_per_row_and_the_next_segment_creates_a_batch_again()
    {
        var log = new List<string>();
        var (outbox, rows) = await StoreAsync(log, ("A", 0), ("A", 1), ("B", 0), ("B", 1));
        var sender = new BatchingSender(log) { FailCreates = 1 };
        sender.FailSingle.Add("A:000");

        var dispatched = await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10);

        Assert.AreEqual(2, dispatched);
        CollectionAssert.AreEqual(new[] { "A:000" }, sender.SingleAttempts.ToArray());
        AssertBatches(sender, ["B:000", "B:001"]);
        CollectionAssert.AreEqual(new[] { rows[0].Id }, outbox.Failures.Select(failure => failure.Id).ToArray(), "Nothing is recorded for the failed creation itself.");
        Assert.IsFalse(outbox.Inner.IsLeased(rows[1].Id));
        Assert.AreEqual(0, outbox.Inner.GetAttemptCount(rows[1].Id));
    }

    [TestMethod]
    public async Task Batching_an_add_failure_sends_the_open_batch_and_a_successful_single_send_lets_the_session_continue()
    {
        var log = new List<string>();
        var (outbox, rows) = await StoreAsync(log, ("A", 0), ("A", 1), ("A", 2), ("B", 0), ("B", 1));
        var sender = new BatchingSender(log);
        sender.AddThrows["A:001"] = new InvalidOperationException("cannot map");

        var dispatched = await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10);

        Assert.AreEqual(5, dispatched);
        AssertBatches(sender, ["A:000"], ["A:002"], ["B:000", "B:001"]);
        CollectionAssert.AreEqual(new[] { "A:001" }, sender.SingleAttempts.ToArray());
        Assert.AreEqual(0, outbox.Failures.Count);
        CollectionAssert.AreEqual(new[] { rows[0].Id }, outbox.Checkpoints[0]);
        CollectionAssert.AreEqual(new[] { rows[1].Id }, outbox.Checkpoints[1]);
        Assert.AreEqual(sender.Created, sender.Disposed, "The batch whose add failed is disposed.");
    }

    [TestMethod]
    public async Task Batching_an_add_failure_followed_by_a_failed_single_send_blocks_the_session()
    {
        var log = new List<string>();
        var (outbox, rows) = await StoreAsync(log, ("A", 0), ("A", 1), ("A", 2), ("A", 3), ("B", 0), ("B", 1));
        var sender = new BatchingSender(log);
        sender.AddThrows["A:001"] = new InvalidOperationException("cannot map");
        sender.FailSingle.Add("A:001");

        var dispatched = await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10);

        Assert.AreEqual(3, dispatched);
        AssertBatches(sender, ["A:000"], ["B:000", "B:001"]);
        CollectionAssert.AreEqual(new[] { rows[1].Id }, outbox.Failures.Select(failure => failure.Id).ToArray());
        Assert.AreEqual(1, outbox.Inner.GetAttemptCount(rows[1].Id));
        Assert.IsTrue(new[] { rows[2], rows[3] }.All(row => !outbox.Inner.IsLeased(row.Id) && outbox.Inner.GetAttemptCount(row.Id) == 0));
        Assert.AreEqual(sender.Created, sender.Disposed);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Batching_an_add_failure_of_a_row_without_a_session_lets_the_rest_of_its_segment_batch(bool singleSendFails)
    {
        var log = new List<string>();
        var (outbox, rows) = await StoreAsync(log, (null, 0), (null, 1), (null, 2), (null, 3));
        var sender = new BatchingSender(log);
        sender.AddThrows["none:001"] = new InvalidOperationException("cannot map");
        if (singleSendFails)
            sender.FailSingle.Add("none:001");

        var dispatched = await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10);

        Assert.AreEqual(singleSendFails ? 3 : 4, dispatched);
        AssertBatches(sender, ["none:000"], ["none:002", "none:003"]);
        Assert.AreEqual(singleSendFails ? 1 : 0, outbox.Failures.Count);
    }

    [TestMethod]
    public async Task Batching_a_partial_batch_checkpoint_means_the_claim_was_lost_and_stops_the_dispatcher()
    {
        var log = new List<string>();
        var (outbox, rows) = await StoreAsync(log, ("A", 0), ("A", 1), ("B", 0), ("B", 1));
        outbox.CheckpointFilter = (ids, result) => outbox.Checkpoints.Count == 1 ? result.Take(1).ToList() : result;
        var sender = new BatchingSender(log);
        var logger = new ListLogger();

        var dispatched = await new OutboxDispatcher(outbox, sender, logger).DispatchPendingAsync(batchSize: 10);

        Assert.AreEqual(1, dispatched);
        AssertBatches(sender, ["A:000", "A:001"]);
        Assert.AreEqual(1, outbox.Checkpoints.Count);
        CollectionAssert.AreEquivalent(new[] { rows[2].Id, rows[3].Id }, outbox.Releases.SelectMany(ids => ids).ToArray());
        var lost = logger.Entries.Where(entry => entry.Message.Contains("lost its claim", StringComparison.Ordinal)).ToList();
        Assert.AreEqual(1, lost.Count);
        StringAssert.Contains(lost[0].Message, rows[1].Id, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task Batching_no_batch_is_created_once_half_the_lease_is_used()
    {
        var log = new List<string>();
        var (outbox, rows) = await StoreAsync(log, ("A", 0), ("B", 0), ("B", 1));
        var options = new OutboxDispatcherOptions { LeaseDuration = TimeSpan.FromSeconds(1) };
        var sender = new BatchingSender(log) { BatchSendDelay = TimeSpan.FromMilliseconds(600) };
        var logger = new ListLogger();

        var dispatched = await new OutboxDispatcher(outbox, sender, options, logger).DispatchPendingAsync(batchSize: 10);

        Assert.AreEqual(1, dispatched);
        AssertBatches(sender, ["A:000"]);
        Assert.AreEqual(1, sender.Created, "No batch is created for B once the budget is used.");
        Assert.IsTrue(new[] { rows[1], rows[2] }.All(row => !outbox.Inner.IsLeased(row.Id) && outbox.Inner.GetAttemptCount(row.Id) == 0));
        Assert.IsTrue(logger.Entries.Any(entry => entry.Message.Contains("released 2 unsent", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task Batching_a_batch_is_not_sent_once_half_the_lease_is_used()
    {
        var log = new List<string>();
        var (outbox, rows) = await StoreAsync(log, ("A", 0), ("A", 1), ("B", 0));
        var options = new OutboxDispatcherOptions { LeaseDuration = TimeSpan.FromSeconds(1) };
        // Creation finishes inside the lease window but past the half-lease send budget.
        var sender = new BatchingSender(log) { CreateDelay = TimeSpan.FromMilliseconds(600) };
        var logger = new ListLogger();

        var dispatched = await new OutboxDispatcher(outbox, sender, options, logger).DispatchPendingAsync(batchSize: 10);

        Assert.AreEqual(0, dispatched);
        Assert.AreEqual(0, sender.SentBatches.Count);
        Assert.AreEqual(0, outbox.Checkpoints.Count);
        Assert.AreEqual(0, outbox.Failures.Count);
        Assert.IsTrue(rows.All(row => !outbox.Inner.IsLeased(row.Id)));
        Assert.IsTrue(logger.Entries.Any(entry => entry.Message.Contains("released 3 unsent", StringComparison.Ordinal)));
        Assert.AreEqual(sender.Created, sender.Disposed);
    }

    [TestMethod]
    [DataRow("send")]
    [DataRow("create")]
    public async Task Batching_a_batch_create_or_send_running_past_the_lease_window_is_cancelled_and_blames_the_first_row(string stage)
    {
        var log = new List<string>();
        var (outbox, rows) = await StoreAsync(log, ("A", 0), ("A", 1), ("A", 2));
        var options = new OutboxDispatcherOptions { LeaseDuration = TimeSpan.FromSeconds(2) };
        var sender = new BatchingSender(log);
        if (stage == "send")
            sender.BatchSendDelay = TimeSpan.FromMinutes(5);
        else
            sender.CreateDelay = TimeSpan.FromMinutes(5);
        var started = System.Diagnostics.Stopwatch.StartNew();

        var dispatched = await new OutboxDispatcher(outbox, sender, options, logger: null).DispatchPendingAsync(batchSize: 10);

        Assert.IsTrue(started.Elapsed < options.LeaseDuration, $"The {stage} must be cut off before the lease expires; took {started.Elapsed}.");
        AssertTimedOutBatch(outbox, sender, rows, dispatched);
    }

    [TestMethod]
    public async Task Batching_an_add_that_returns_after_the_lease_window_stops_the_batch_from_being_sent()
    {
        var log = new List<string>();
        var (outbox, rows) = await StoreAsync(log, ("A", 0), ("A", 1), ("A", 2));
        var options = new OutboxDispatcherOptions { LeaseDuration = TimeSpan.FromSeconds(1) };
        var sender = new BatchingSender(log);
        // The add is synchronous: it is not interrupted, but runs past the 900 ms window.
        sender.AddBlock["A:001"] = TimeSpan.FromMilliseconds(1000);

        var dispatched = await new OutboxDispatcher(outbox, sender, options, logger: null).DispatchPendingAsync(batchSize: 10);

        CollectionAssert.AreEqual(new[] { "A:000", "A:001" }, sender.Adds.Select(add => add.MessageId).ToArray(), "No row is added after the overrun.");
        AssertTimedOutBatch(outbox, sender, rows, dispatched);
    }

    [TestMethod]
    public async Task Batching_cancellation_after_the_batch_send_checkpoints_the_whole_batch_and_releases_the_rest()
    {
        var log = new List<string>();
        var (outbox, rows) = await StoreAsync(log, ("A", 0), ("A", 1), ("B", 0));
        using var cts = new CancellationTokenSource();
        outbox.BeforeCheckpoint = (ids, token) =>
        {
            if (outbox.Checkpoints.Count > 1)
                return;
            cts.Cancel();
            token.ThrowIfCancellationRequested();
        };
        var sender = new BatchingSender(log);

        await Assert.ThrowsAsync<OperationCanceledException>(() => new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10, cts.Token));

        Assert.AreEqual(2, outbox.Checkpoints.Count, "The interrupted checkpoint is followed by a compensating one.");
        CollectionAssert.AreEquivalent(new[] { rows[0].Id, rows[1].Id }, outbox.Checkpoints[1]);
        Assert.IsNotNull(outbox.Inner.GetDispatchedAtUtc(rows[1].Id));
        CollectionAssert.AreEquivalent(new[] { rows[2].Id }, outbox.Releases.SelectMany(ids => ids).ToArray());
        Assert.AreEqual(0, outbox.Failures.Count);
    }

    [TestMethod]
    [DataRow("send")]
    [DataRow("create")]
    public async Task Batching_cancellation_during_batch_create_or_send_releases_the_open_batch(string stage)
    {
        var log = new List<string>();
        var (outbox, rows) = await StoreAsync(log, ("A", 0), ("A", 1), ("B", 0));
        using var cts = new CancellationTokenSource();
        var sender = new BatchingSender(log);
        if (stage == "send")
            sender.BatchSendDelay = TimeSpan.FromMinutes(5);
        else
            sender.CreateDelay = TimeSpan.FromMinutes(5);
        cts.CancelAfter(TimeSpan.FromMilliseconds(200));

        await Assert.ThrowsAsync<OperationCanceledException>(() => new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10, cts.Token));

        AssertCancelledBeforeSend(outbox, sender, rows);
    }

    [TestMethod]
    public async Task Batching_cancellation_signalled_during_a_synchronous_add_is_detected_after_it_returns()
    {
        var log = new List<string>();
        var (outbox, rows) = await StoreAsync(log, ("A", 0), ("A", 1), ("A", 2));
        using var cts = new CancellationTokenSource();
        var sender = new BatchingSender(log) { OnAdd = message => { if (message.MessageId == "A:001") cts.Cancel(); } };

        await Assert.ThrowsAsync<OperationCanceledException>(() => new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10, cts.Token));

        CollectionAssert.AreEqual(new[] { "A:000", "A:001" }, sender.Adds.Select(add => add.MessageId).ToArray());
        AssertCancelledBeforeSend(outbox, sender, rows);
    }

    [TestMethod]
    public async Task A_sender_without_batching_sends_and_checkpoints_one_row_at_a_time_in_claim_order()
    {
        var log = new List<string>();
        var (outbox, rows) = await StoreAsync(log, ("A", 0), ("B", 0), ("A", 1));
        var sender = new ScriptedSender();

        Assert.AreEqual(3, await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10));

        CollectionAssert.AreEqual(rows.Select(row => row.MessageId).ToArray(), sender.Sent.ToArray());
        Assert.AreEqual(3, outbox.Checkpoints.Count);
        for (var index = 0; index < rows.Count; index++)
            CollectionAssert.AreEqual(new[] { rows[index].Id }, outbox.Checkpoints[index]);
    }

    [TestMethod]
    public async Task An_outbox_without_claiming_dispatches_per_row_even_with_a_batching_sender()
    {
        var log = new List<string>();
        var rows = new List<OutboxMessage> { CreateRow("A", 0), CreateRow("A", 1), CreateRow("B", 0) };
        var outbox = new ListOutbox(rows);
        var sender = new BatchingSender(log);

        Assert.AreEqual(3, await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10));

        Assert.AreEqual(0, sender.Created);
        CollectionAssert.AreEqual(rows.Select(row => row.MessageId).ToArray(), sender.SingleAttempts.ToArray());
        Assert.AreEqual(1, outbox.MarkCalls.Count);
        CollectionAssert.AreEqual(rows.Select(row => row.Id).ToArray(), outbox.MarkCalls[0]);
    }

    [TestMethod]
    public async Task Batching_keeps_metrics_and_trace_links_per_row()
    {
        var endpoint = $"metrics-{Guid.NewGuid():N}";
        var log = new List<string>();
        var rows = Enumerable.Range(0, 4).Select(position => CreateRow(position % 2 == 0 ? "A" : "B", position, endpoint)).ToList();
        var traceIds = new List<string>();
        for (var index = 0; index < 3; index++)
        {
            var traceId = System.Diagnostics.ActivityTraceId.CreateRandom().ToHexString();
            traceIds.Add(traceId);
            rows[index].TraceParent = $"00-{traceId}-{System.Diagnostics.ActivitySpanId.CreateRandom().ToHexString()}-01";
        }

        var outbox = new RecordingOutbox(log);
        await outbox.StoreBatchAsync(rows);
        using var observer = new DispatchObserver(endpoint);

        Assert.AreEqual(4, await new OutboxDispatcher(outbox, new BatchingSender(log)).DispatchPendingAsync(batchSize: 10));

        Assert.AreEqual(4L, observer.Count("dispatched"));
        Assert.AreEqual(0L, observer.Count("failed"));
        Assert.AreEqual(4, observer.Durations);
        CollectionAssert.AreEquivalent(traceIds, observer.LinkedTraceIds.ToArray());
        Assert.AreEqual(4, observer.ProducerSpans);
        Assert.AreEqual(1, observer.OrphanSpans);
    }

    [TestMethod]
    public async Task Batching_counts_a_row_that_falls_back_once_for_its_final_outcome()
    {
        var endpoint = $"metrics-{Guid.NewGuid():N}";
        var log = new List<string>();
        var rows = Enumerable.Range(0, 3).Select(position => CreateRow("A", position, endpoint)).ToList();
        var outbox = new RecordingOutbox(log);
        await outbox.StoreBatchAsync(rows);
        var sender = new BatchingSender(log) { FailBatchSend = _ => new InvalidOperationException("batch rejected") };
        sender.FailSingle.Add("A:001");
        using var observer = new DispatchObserver(endpoint);

        Assert.AreEqual(1, await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 10));

        Assert.AreEqual(1L, observer.Count("dispatched"));
        Assert.AreEqual(1L, observer.Count("failed"));
        Assert.AreEqual(2, observer.Durations, "A3 is released unsent and not counted.");
    }

    private static void AssertTimedOutBatch(RecordingOutbox outbox, BatchingSender sender, IReadOnlyList<OutboxMessage> rows, int dispatched)
    {
        Assert.AreEqual(0, dispatched);
        Assert.AreEqual(0, sender.SentBatches.Count);
        Assert.AreEqual(0, outbox.Checkpoints.Count, "Nothing from the timed-out batch is checkpointed.");
        CollectionAssert.AreEqual(new[] { rows[0].Id }, outbox.Failures.Select(failure => failure.Id).ToArray(), "Only the batch's first row is blamed.");
        StringAssert.Contains(outbox.Failures[0].Error, typeof(TimeoutException).FullName!, StringComparison.Ordinal);
        Assert.AreEqual(1, outbox.Inner.GetAttemptCount(rows[0].Id));
        Assert.IsTrue(rows.Skip(1).All(row => outbox.Inner.GetAttemptCount(row.Id) == 0 && !outbox.Inner.IsLeased(row.Id)));
        Assert.AreEqual(sender.Created, sender.Disposed);
    }

    private static void AssertCancelledBeforeSend(RecordingOutbox outbox, BatchingSender sender, IReadOnlyList<OutboxMessage> rows)
    {
        Assert.AreEqual(0, sender.SentBatches.Count);
        Assert.AreEqual(0, outbox.Checkpoints.Count, "No row of the open batch is checkpointed.");
        Assert.AreEqual(0, outbox.Failures.Count, "A cancelled poll records no attempt.");
        CollectionAssert.AreEquivalent(rows.Select(row => row.Id).ToArray(), outbox.Releases.SelectMany(ids => ids).ToArray());
        Assert.AreEqual(sender.Created, sender.Disposed);
    }

    private static void AssertBatches(BatchingSender sender, params string[][] expected)
    {
        var actual = sender.SentBatches.Select(batch => string.Join(", ", batch.Select(message => message.MessageId))).ToArray();
        CollectionAssert.AreEqual(expected.Select(batch => string.Join(", ", batch)).ToArray(), actual, $"Batches sent: {string.Join(" | ", actual)}");
    }

    private static Task<(RecordingOutbox Outbox, List<OutboxMessage> Rows)> StoreAsync(List<string> log, params (string? Session, int Position)[] specs) =>
        StoreAsync(log, specs.Select(spec => (spec.Session, spec.Position, "Test")).ToArray());

    private static async Task<(RecordingOutbox Outbox, List<OutboxMessage> Rows)> StoreAsync(List<string> log, params (string? Session, int Position, string To)[] specs)
    {
        var rows = specs.Select(spec => CreateRow(spec.Session, spec.Position, spec.To)).ToList();
        var outbox = new RecordingOutbox(log);
        await outbox.StoreBatchAsync(rows);
        return (outbox, rows);
    }

    private static OutboxMessage CreateRow(string? sessionId, int position, string to)
    {
        var row = CreateRow(sessionId, position);
        row.To = to;
        var message = JsonConvert.DeserializeObject<Message>(row.Payload)!;
        message.To = to;
        row.Payload = JsonConvert.SerializeObject(message);
        return row;
    }

    /// <summary>
    /// A sender that implements <see cref="IBatchingSender"/> and records every call in a log
    /// shared with <see cref="RecordingOutbox"/>.
    /// </summary>
    private sealed class BatchingSender(List<string> log) : ISender, IBatchingSender
    {
        public List<string> Log { get; } = log;
        public int Capacity { get; init; } = int.MaxValue;
        public HashSet<string> TooLarge { get; } = new(StringComparer.Ordinal);
        public HashSet<string> FailSingle { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, Exception> AddThrows { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, TimeSpan> AddBlock { get; } = new(StringComparer.Ordinal);
        public Func<string[], Exception?>? FailBatchSend { get; init; }
        public int FailCreates { get; set; }
        public TimeSpan CreateDelay { get; set; }
        public TimeSpan BatchSendDelay { get; set; }
        public Action<IMessage>? OnAdd { get; init; }

        public List<List<IMessage>> SentBatches { get; } = new();
        public List<(string? MessageId, int Delay)> Adds { get; } = new();
        public List<string?> SingleAttempts { get; } = new();
        public int Created { get; private set; }
        public int Disposed { get; private set; }

        public async Task<ISenderBatch> CreateBatchAsync(CancellationToken cancellationToken = default)
        {
            Log.Add("create");
            if (CreateDelay > TimeSpan.Zero)
                await Task.Delay(CreateDelay, cancellationToken);
            if (FailCreates > 0)
            {
                FailCreates--;
                throw new InvalidOperationException("connection failed");
            }

            Created++;
            return new Batch(this);
        }

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Log.Add($"single {message.MessageId}");
            SingleAttempts.Add(message.MessageId);
            if (FailSingle.Contains(message.MessageId!))
                throw new InvalidOperationException("poison");
            return Task.CompletedTask;
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default)
        {
            Log.Add($"schedule {message.MessageId}");
            return Task.FromResult(1L);
        }

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) => Task.CompletedTask;

        private sealed class Batch(BatchingSender owner) : ISenderBatch
        {
            private readonly List<IMessage> _messages = new();

            public int Count => _messages.Count;

            public bool TryAdd(IMessage message, int messageEnqueueDelay)
            {
                owner.Adds.Add((message.MessageId, messageEnqueueDelay));
                owner.OnAdd?.Invoke(message);
                if (owner.AddBlock.TryGetValue(message.MessageId!, out var block))
                    Thread.Sleep(block);
                if (owner.AddThrows.TryGetValue(message.MessageId!, out var error))
                    throw error;
                if (owner.TooLarge.Contains(message.MessageId!) || _messages.Count >= owner.Capacity)
                    return false;
                _messages.Add(message);
                return true;
            }

            public async Task SendAsync(CancellationToken cancellationToken = default)
            {
                var ids = _messages.Select(message => message.MessageId!).ToArray();
                if (owner.BatchSendDelay > TimeSpan.Zero)
                    await Task.Delay(owner.BatchSendDelay, cancellationToken);
                var failure = owner.FailBatchSend?.Invoke(ids);
                if (failure is not null)
                    throw failure;
                owner.Log.Add($"send [{string.Join(", ", ids)}]");
                owner.SentBatches.Add(_messages.ToList());
            }

            public void Dispose() => owner.Disposed++;
        }
    }

    /// <summary>
    /// Wraps <see cref="ConformanceInMemoryOutbox"/> and records the claiming calls the
    /// dispatcher makes.
    /// </summary>
    private sealed class RecordingOutbox(List<string> log) : IOutbox, IOutboxClaiming
    {
        public ConformanceInMemoryOutbox Inner { get; } = new();
        public List<string[]> Checkpoints { get; } = new();
        public List<string[]> Releases { get; } = new();
        public List<(string Id, string Error, bool Park)> Failures { get; } = new();
        public Func<string[], IReadOnlyCollection<string>, IReadOnlyCollection<string>>? CheckpointFilter { get; set; }
        public Action<string[], CancellationToken>? BeforeCheckpoint { get; set; }

        public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default) => Inner.StoreAsync(message, cancellationToken);

        public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default) => Inner.StoreBatchAsync(messages, cancellationToken);

        public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) => Inner.GetPendingAsync(batchSize, cancellationToken);

        public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default) => Inner.MarkAsDispatchedAsync(id, cancellationToken);

        public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default) => Inner.MarkAsDispatchedAsync(ids, cancellationToken);

        public Task<IReadOnlyList<OutboxMessage>> ClaimPendingAsync(int batchSize, string claimantId, TimeSpan leaseDuration, CancellationToken cancellationToken = default) =>
            Inner.ClaimPendingAsync(batchSize, claimantId, leaseDuration, cancellationToken);

        public async Task<IReadOnlyCollection<string>> MarkClaimedAsDispatchedAsync(string claimantId, IEnumerable<string> ids, CancellationToken cancellationToken = default)
        {
            var list = ids.ToArray();
            Checkpoints.Add(list);
            log.Add($"checkpoint [{string.Join(", ", list)}]");
            BeforeCheckpoint?.Invoke(list, cancellationToken);
            var result = await Inner.MarkClaimedAsDispatchedAsync(claimantId, list, cancellationToken);
            return CheckpointFilter is null ? result : CheckpointFilter(list, result);
        }

        public Task RecordDispatchFailureAsync(string id, string claimantId, string error, TimeSpan retryDelay, bool park, CancellationToken cancellationToken = default)
        {
            Failures.Add((id, error, park));
            return Inner.RecordDispatchFailureAsync(id, claimantId, error, retryDelay, park, cancellationToken);
        }

        public Task ReleaseClaimsAsync(string claimantId, IEnumerable<string> ids, CancellationToken cancellationToken = default)
        {
            var list = ids.ToArray();
            Releases.Add(list);
            return Inner.ReleaseClaimsAsync(claimantId, list, cancellationToken);
        }
    }

    /// <summary>An outbox that implements only <see cref="IOutbox"/> and serves a fixed set of rows.</summary>
    private sealed class ListOutbox(List<OutboxMessage> rows) : IOutbox
    {
        public List<string[]> MarkCalls { get; } = new();

        public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default) => Task.CompletedTask;

        public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<OutboxMessage>>(rows.Take(batchSize).ToList());

        public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default) => MarkAsDispatchedAsync([id], cancellationToken);

        public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
        {
            MarkCalls.Add(ids.ToArray());
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Observes the outbox dispatch metrics and producer spans of one endpoint.
    /// </summary>
    private sealed class DispatchObserver : IDisposable
    {
        private readonly string _endpoint;
        private readonly MeterListener _meters = new();
        private readonly System.Diagnostics.ActivityListener _activities;
        private readonly ConcurrentDictionary<string, long> _counts = new(StringComparer.Ordinal);
        private readonly ConcurrentQueue<string> _links = new();
        private int _durations;
        private int _producerSpans;
        private int _orphanSpans;

        public DispatchObserver(string endpoint)
        {
            _endpoint = endpoint;
            _meters.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Name is "nimbus.outbox.dispatched" or "nimbus.outbox.dispatch.duration")
                    listener.EnableMeasurementEvents(instrument);
            };
            _meters.SetMeasurementEventCallback<long>((instrument, value, tags, _) =>
            {
                if (HasEndpoint(tags))
                    _counts.AddOrUpdate(Outcome(tags), value, (_, total) => total + value);
            });
            _meters.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            {
                if (HasEndpoint(tags))
                    Interlocked.Increment(ref _durations);
            });
            _meters.Start();

            _activities = new System.Diagnostics.ActivityListener
            {
                ShouldListenTo = source => source.Name == "NimBus.Outbox",
                Sample = (ref System.Diagnostics.ActivityCreationOptions<System.Diagnostics.ActivityContext> _) => System.Diagnostics.ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity =>
                {
                    if (activity.Kind != System.Diagnostics.ActivityKind.Producer || !Equals(activity.GetTagItem("nimbus.endpoint"), _endpoint))
                        return;
                    Interlocked.Increment(ref _producerSpans);
                    if (activity.Events.Any(e => e.Name == "nimbus.outbox.orphan_row"))
                        Interlocked.Increment(ref _orphanSpans);
                    foreach (var link in activity.Links)
                        _links.Enqueue(link.Context.TraceId.ToHexString());
                },
            };
            System.Diagnostics.ActivitySource.AddActivityListener(_activities);
        }

        public int Durations => Volatile.Read(ref _durations);
        public int ProducerSpans => Volatile.Read(ref _producerSpans);
        public int OrphanSpans => Volatile.Read(ref _orphanSpans);
        public IReadOnlyCollection<string> LinkedTraceIds => _links.ToArray();

        public long Count(string outcome) => _counts.TryGetValue(outcome, out var total) ? total : 0;

        public void Dispose()
        {
            _meters.Dispose();
            _activities.Dispose();
        }

        private bool HasEndpoint(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "nimbus.endpoint" && Equals(tag.Value, _endpoint))
                    return true;
            }

            return false;
        }

        private static string Outcome(ReadOnlySpan<KeyValuePair<string, object?>> tags)
        {
            foreach (var tag in tags)
            {
                if (tag.Key == "nimbus.outcome")
                    return tag.Value as string ?? "";
            }

            return "";
        }
    }
}
