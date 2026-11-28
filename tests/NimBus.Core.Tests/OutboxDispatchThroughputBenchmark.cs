#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;

namespace NimBus.Core.Tests;

/// <summary>
/// Rerunnable throughput harness for <see cref="OutboxDispatcher"/>: dispatches the same rows
/// once with a sender that sends one message per call (per-row) and once with an
/// <see cref="IBatchingSender"/> (batched), against a claiming outbox, with simulated latency.
/// It reports messages per second, store round trips per message and broker calls per message.
/// </summary>
/// <remarks>
/// <para>Run it with:</para>
/// <code>
/// dotnet test tests/NimBus.Core.Tests -c Release --filter "FullyQualifiedName~OutboxDispatchThroughputBenchmark" --logger "console;verbosity=detailed"
/// </code>
/// <para>
/// Override the defaults with environment variables: <c>NIMBUS_OUTBOX_BENCH_ROWS</c> (500),
/// <c>NIMBUS_OUTBOX_BENCH_SESSIONS</c> (10, rows interleaved over them in claim order),
/// <c>NIMBUS_OUTBOX_BENCH_BATCH</c> (100, the dispatcher's claim size),
/// <c>NIMBUS_OUTBOX_BENCH_SENDER_MS</c> (2, latency of one broker call) and
/// <c>NIMBUS_OUTBOX_BENCH_STORE_MS</c> (1, latency of one store round trip). Latency is
/// simulated with <see cref="Task.Delay(TimeSpan)"/>; no Service Bus namespace or emulator is used.
/// </para>
/// </remarks>
[TestClass]
public sealed class OutboxDispatchThroughputBenchmark
{
    /// <summary>MSTest context, used to write the results.</summary>
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task Batched_dispatch_needs_fewer_store_round_trips_per_message_than_per_row_dispatch()
    {
        var setup = new BenchmarkSetup(
            Rows: ReadSetting("NIMBUS_OUTBOX_BENCH_ROWS", 500),
            Sessions: ReadSetting("NIMBUS_OUTBOX_BENCH_SESSIONS", 10),
            BatchSize: ReadSetting("NIMBUS_OUTBOX_BENCH_BATCH", 100),
            SenderLatency: TimeSpan.FromMilliseconds(ReadSetting("NIMBUS_OUTBOX_BENCH_SENDER_MS", 2)),
            StoreLatency: TimeSpan.FromMilliseconds(ReadSetting("NIMBUS_OUTBOX_BENCH_STORE_MS", 1)));

        var perRow = await RunAsync(setup, batching: false);
        var batched = await RunAsync(setup, batching: true);

        TestContext.WriteLine(
            $"Outbox dispatch throughput: {setup.Rows} rows interleaved over {setup.Sessions} session(s), claim batch size {setup.BatchSize}, " +
            $"simulated sender latency {setup.SenderLatency.TotalMilliseconds} ms per broker call, store latency {setup.StoreLatency.TotalMilliseconds} ms per round trip.");
        TestContext.WriteLine(perRow.Describe("per-row"));
        TestContext.WriteLine(batched.Describe("batched"));

        Assert.AreEqual(setup.Rows, perRow.Dispatched);
        Assert.AreEqual(setup.Rows, batched.Dispatched);
        if (setup.Rows > setup.Sessions)
        {
            Assert.IsTrue(
                batched.StoreRoundTripsPerMessage < perRow.StoreRoundTripsPerMessage,
                $"Batched dispatch needs {batched.StoreRoundTripsPerMessage:F3} store round trips per message, per-row {perRow.StoreRoundTripsPerMessage:F3}.");
        }
    }

    private static async Task<BenchmarkResult> RunAsync(BenchmarkSetup setup, bool batching)
    {
        var outbox = new LatencyOutbox(setup.StoreLatency);
        await outbox.StoreBatchAsync(Enumerable.Range(0, setup.Rows).Select(index => CreateRow(index, setup.Sessions)).ToList());
        ISender sender = batching ? new LatencyBatchingSender(setup.SenderLatency) : new LatencySender(setup.SenderLatency);
        var dispatcher = new OutboxDispatcher(outbox, sender);

        var stopwatch = Stopwatch.StartNew();
        var dispatched = 0;
        for (var poll = 0; poll < setup.Rows * 2 && dispatched < setup.Rows; poll++)
            dispatched += await dispatcher.DispatchPendingAsync(setup.BatchSize);
        stopwatch.Stop();

        return new BenchmarkResult(dispatched, stopwatch.Elapsed, outbox.RoundTrips, ((LatencySender)sender).BrokerCalls);
    }

    private static OutboxMessage CreateRow(int index, int sessions)
    {
        var sessionId = $"session-{index % sessions}";
        var messageId = $"bench-{index:D6}";
        return new OutboxMessage
        {
            Id = $"outbox-{messageId}",
            MessageId = messageId,
            To = "Benchmark",
            SessionId = sessionId,
            Payload = JsonConvert.SerializeObject(new Message
            {
                MessageId = messageId,
                To = "Benchmark",
                SessionId = sessionId,
                MessageContent = new MessageContent { EventContent = new EventContent { EventTypeId = "OrderPlaced", EventJson = "{}" } },
            }),
            CreatedAtUtc = new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(index),
        };
    }

    private static int ReadSetting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value > 0 ? value : fallback;

    private sealed record BenchmarkSetup(int Rows, int Sessions, int BatchSize, TimeSpan SenderLatency, TimeSpan StoreLatency);

    private sealed record BenchmarkResult(int Dispatched, TimeSpan Elapsed, int StoreRoundTrips, int BrokerCalls)
    {
        public double MessagesPerSecond => Dispatched / Elapsed.TotalSeconds;
        public double StoreRoundTripsPerMessage => (double)StoreRoundTrips / Dispatched;
        public double BrokerCallsPerMessage => (double)BrokerCalls / Dispatched;

        public string Describe(string mode) => string.Create(
            CultureInfo.InvariantCulture,
            $"{mode,-8}: {Dispatched} messages in {Elapsed.TotalMilliseconds:F0} ms = {MessagesPerSecond:F0} msg/s; " +
            $"{StoreRoundTrips} store round trips ({StoreRoundTripsPerMessage:F3} per message); {BrokerCalls} broker calls ({BrokerCallsPerMessage:F3} per message)");
    }

    /// <summary>A claiming outbox that adds a fixed latency to, and counts, every store round trip.</summary>
    private sealed class LatencyOutbox(TimeSpan latency) : IOutbox, IOutboxClaiming
    {
        private readonly ConformanceInMemoryOutbox _inner = new();
        private int _roundTrips;

        public int RoundTrips => Volatile.Read(ref _roundTrips);

        public Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default) => _inner.StoreAsync(message, cancellationToken);

        public Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default) => _inner.StoreBatchAsync(messages, cancellationToken);

        public Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default) => _inner.GetPendingAsync(batchSize, cancellationToken);

        public Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default) => _inner.MarkAsDispatchedAsync(id, cancellationToken);

        public Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default) => _inner.MarkAsDispatchedAsync(ids, cancellationToken);

        public async Task<IReadOnlyList<OutboxMessage>> ClaimPendingAsync(int batchSize, string claimantId, TimeSpan leaseDuration, CancellationToken cancellationToken = default)
        {
            await RoundTripAsync(cancellationToken);
            return await _inner.ClaimPendingAsync(batchSize, claimantId, leaseDuration, cancellationToken);
        }

        public async Task<IReadOnlyCollection<string>> MarkClaimedAsDispatchedAsync(string claimantId, IEnumerable<string> ids, CancellationToken cancellationToken = default)
        {
            await RoundTripAsync(cancellationToken);
            return await _inner.MarkClaimedAsDispatchedAsync(claimantId, ids, cancellationToken);
        }

        public async Task RecordDispatchFailureAsync(string id, string claimantId, string error, TimeSpan retryDelay, bool park, CancellationToken cancellationToken = default)
        {
            await RoundTripAsync(cancellationToken);
            await _inner.RecordDispatchFailureAsync(id, claimantId, error, retryDelay, park, cancellationToken);
        }

        public async Task ReleaseClaimsAsync(string claimantId, IEnumerable<string> ids, CancellationToken cancellationToken = default)
        {
            await RoundTripAsync(cancellationToken);
            await _inner.ReleaseClaimsAsync(claimantId, ids, cancellationToken);
        }

        private Task RoundTripAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _roundTrips);
            return latency > TimeSpan.Zero ? Task.Delay(latency, cancellationToken) : Task.CompletedTask;
        }
    }

    /// <summary>A sender that sends one message per broker call, each with a fixed latency.</summary>
    private class LatencySender(TimeSpan latency) : ISender
    {
        private int _brokerCalls;

        public int BrokerCalls => Volatile.Read(ref _brokerCalls);

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default) => BrokerCallAsync(cancellationToken);

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default) => BrokerCallAsync(cancellationToken);

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default) =>
            BrokerCallAsync(cancellationToken).ContinueWith(_ => 0L, cancellationToken, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) => BrokerCallAsync(cancellationToken);

        protected Task BrokerCallAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _brokerCalls);
            return latency > TimeSpan.Zero ? Task.Delay(latency, cancellationToken) : Task.CompletedTask;
        }
    }

    /// <summary>
    /// A <see cref="LatencySender"/> that also sends batches: creating a batch is local, and
    /// sending it is one broker call with the same latency as a single send.
    /// </summary>
    private sealed class LatencyBatchingSender(TimeSpan latency) : LatencySender(latency), IBatchingSender
    {
        public Task<ISenderBatch> CreateBatchAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<ISenderBatch>(new Batch(this));

        private sealed class Batch(LatencyBatchingSender owner) : ISenderBatch
        {
            private int _count;

            public int Count => _count;

            public bool TryAdd(IMessage message, int messageEnqueueDelay)
            {
                _count++;
                return true;
            }

            public Task SendAsync(CancellationToken cancellationToken = default) => owner.BrokerCallAsync(cancellationToken);

            public void Dispose()
            {
            }
        }
    }
}
