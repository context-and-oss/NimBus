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
}
