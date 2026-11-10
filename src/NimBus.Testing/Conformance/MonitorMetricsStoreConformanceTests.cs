#pragma warning disable CA1707, CA2007
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>
/// Provider-agnostic conformance suite for <see cref="IMonitorMetricsStore"/>. Setup goes
/// through the aggregate <see cref="INimBusMessageStore"/>; the queries under test through
/// <see cref="MonitorStoreFor"/>.
/// </summary>
[TestClass]
public abstract class MonitorMetricsStoreConformanceTests
{
    private readonly string _scope = $"ct-{Guid.NewGuid():N}"[..16];

    /// <summary>A freshly initialized store.</summary>
    protected abstract INimBusMessageStore CreateStore();

    /// <summary>The store's <see cref="IMonitorMetricsStore"/>; by default a cast.</summary>
    protected virtual IMonitorMetricsStore MonitorStoreFor(INimBusMessageStore store) => (IMonitorMetricsStore)store;

    private string Id(string value) => $"{_scope}-{value}";

    // The latency series is not scoped by endpoint, so each test gets its own far-future hour:
    // providers without a per-test reset (Cosmos) would otherwise count other runs.
    private static DateTime IsolatedHour()
    {
        var start = DateTime.UtcNow.AddHours(Random.Shared.Next(1_000, 1_000_000));
        return new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Utc);
    }

    [TestMethod]
    public async Task GetLatencyTimeSeriesMetrics_aggregates_each_timing_over_outcome_messages_per_bucket()
    {
        var store = CreateStore();
        var from = IsolatedHour();
        var to = from.AddHours(3);

        await store.StoreMessage(Message(Id("m1"), MessageType.ResolutionResponse, from.AddMinutes(5), queueTimeMs: 10, processingTimeMs: 100));
        await store.StoreMessage(Message(Id("m2"), MessageType.ErrorResponse, from.AddMinutes(10), queueTimeMs: 30, processingTimeMs: 300));
        // Processing only: counts in the processing series, not in the queue series.
        await store.StoreMessage(Message(Id("m3"), MessageType.ResolutionResponse, from.AddMinutes(15), queueTimeMs: null, processingTimeMs: 50));
        // Not an outcome message.
        await store.StoreMessage(Message(Id("m4"), MessageType.EventRequest, from.AddMinutes(20), queueTimeMs: 1000, processingTimeMs: 1000));
        // An outcome without timings.
        await store.StoreMessage(Message(Id("m5"), MessageType.ResolutionResponse, from.AddMinutes(25), queueTimeMs: null, processingTimeMs: null));
        // The second hour stays empty; the third has one queue timing only.
        await store.StoreMessage(Message(Id("m6"), MessageType.SkipResponse, from.AddHours(2).AddMinutes(5), queueTimeMs: 5, processingTimeMs: null));
        // At the exclusive upper bound.
        await store.StoreMessage(Message(Id("m7"), MessageType.ResolutionResponse, to, queueTimeMs: 9999, processingTimeMs: 9999));

        var result = await MonitorStoreFor(store).GetLatencyTimeSeriesMetrics(from, to, substringLength: 13, bucketLabel: "hour");

        Assert.AreEqual("hour", result.BucketSize);
        CollectionAssert.AreEqual(
            new[] { from.ToString("o")[..13], from.AddHours(2).ToString("o")[..13] },
            result.DataPoints.Select(p => p.Timestamp).ToArray(),
            "Buckets are sparse and ascending.");

        var first = result.DataPoints[0];
        Assert.AreEqual(2, first.Queue.Count);
        Assert.AreEqual(20, first.Queue.AvgMs, 0.001);
        Assert.AreEqual(10, first.Queue.MinMs, 0.001);
        Assert.AreEqual(30, first.Queue.MaxMs, 0.001);
        Assert.AreEqual(3, first.Processing.Count);
        Assert.AreEqual(150, first.Processing.AvgMs, 0.001);
        Assert.AreEqual(50, first.Processing.MinMs, 0.001);
        Assert.AreEqual(300, first.Processing.MaxMs, 0.001);

        var third = result.DataPoints[1];
        Assert.AreEqual(1, third.Queue.Count);
        Assert.AreEqual(5, third.Queue.AvgMs, 0.001);
        Assert.AreEqual(0, third.Processing.Count, "A series with no values has a zero count.");
    }

    [TestMethod]
    public async Task GetLatencyTimeSeriesMetrics_minute_keys_match_the_count_series()
    {
        var store = CreateStore();
        var from = IsolatedHour();
        var to = from.AddMinutes(10);
        var at = from.AddMinutes(3).AddSeconds(30);

        await store.StoreMessage(Message(Id("m1"), MessageType.ResolutionResponse, at, queueTimeMs: 10, processingTimeMs: 20));

        var latency = await MonitorStoreFor(store).GetLatencyTimeSeriesMetrics(from, to, substringLength: 16, bucketLabel: "minute");
        var counts = await store.GetTimeSeriesMetrics(from, to, substringLength: 16, bucketLabel: "minute");

        var key = latency.DataPoints.Single().Timestamp;
        Assert.AreEqual(at.ToString("o")[..16], key);
        Assert.AreEqual(1, counts.DataPoints.Single(p => p.Timestamp == key).Handled);
    }

    [TestMethod]
    public async Task GetBlockedSessionCount_counts_distinct_sessions_held_by_a_failure_or_a_handoff()
    {
        var store = CreateStore();
        var endpoint = Id("ep");
        var other = Id("ep-other");

        // s1: two failures on one session count once.
        await store.UploadFailedMessage(Id("e1"), "s1", endpoint, Event(endpoint, Id("e1"), "s1"));
        await store.UploadFailedMessage(Id("e2"), "s1", endpoint, Event(endpoint, Id("e2"), "s1"));
        // s2: a dead-lettered event.
        await store.UploadDeadletteredMessage(Id("e3"), "s2", endpoint, Event(endpoint, Id("e3"), "s2"));
        // s3: a pending hand-off with a deferred sibling.
        var handoff = Event(endpoint, Id("e4"), "s3");
        handoff.PendingSubStatus = "Handoff";
        await store.UploadPendingMessage(Id("e4"), "s3", endpoint, handoff);
        await store.UploadDeferredMessage(Id("e5"), "s3", endpoint, Event(endpoint, Id("e5"), "s3"));

        // Not blocked: parked behind replays only, plain pending, unsupported, completed.
        await store.UploadDeferredMessage(Id("e6"), "s4", endpoint, Event(endpoint, Id("e6"), "s4"));
        await store.UploadPendingMessage(Id("e7"), "s5", endpoint, Event(endpoint, Id("e7"), "s5"));
        await store.UploadUnsupportedMessage(Id("e8"), "s6", endpoint, Event(endpoint, Id("e8"), "s6"));
        await store.UploadCompletedMessage(Id("e9"), "s7", endpoint, Event(endpoint, Id("e9"), "s7"));
        // A deleted failure, and a failure without a session.
        await store.UploadFailedMessage(Id("e10"), "s8", endpoint, Event(endpoint, Id("e10"), "s8"));
        await store.RemoveMessage(Id("e10"), "s8", endpoint);
        await store.UploadFailedMessage(Id("e11"), string.Empty, endpoint, Event(endpoint, Id("e11"), string.Empty));
        // Another endpoint's failure.
        await store.UploadFailedMessage(Id("e12"), "s9", other, Event(other, Id("e12"), "s9"));

        var count = await MonitorStoreFor(store).GetBlockedSessionCount(endpoint);

        Assert.AreEqual(3, count);
    }

    [TestMethod]
    public async Task GetBlockedSessionCount_is_zero_when_nothing_holds_a_session()
    {
        var store = CreateStore();
        var endpoint = Id("ep-clear");
        await store.UploadPendingMessage(Id("e1"), "s1", endpoint, Event(endpoint, Id("e1"), "s1"));

        Assert.AreEqual(0, await MonitorStoreFor(store).GetBlockedSessionCount(endpoint));
    }

    private static MessageEntity Message(string id, MessageType messageType, DateTime enqueuedTimeUtc, long? queueTimeMs, long? processingTimeMs) => new()
    {
        EventId = id,
        MessageId = id,
        EndpointId = "receiver",
        SessionId = "session-1",
        CorrelationId = "corr-1",
        EventTypeId = "OrderPlaced",
        EnqueuedTimeUtc = enqueuedTimeUtc,
        MessageType = messageType,
        EndpointRole = EndpointRole.Subscriber,
        From = "publisher",
        To = "receiver",
        QueueTimeMs = queueTimeMs,
        ProcessingTimeMs = processingTimeMs,
        MessageContent = new MessageContent(),
    };

    private static UnresolvedEvent Event(string endpointId, string eventId, string sessionId) => new()
    {
        EventId = eventId,
        SessionId = sessionId,
        EndpointId = endpointId,
        EnqueuedTimeUtc = DateTime.UtcNow.AddSeconds(-1),
        UpdatedAt = DateTime.UtcNow,
        CorrelationId = "corr-1",
        EndpointRole = EndpointRole.Subscriber,
        MessageType = MessageType.EventRequest,
        EventTypeId = "OrderPlaced",
        To = endpointId,
        From = "publisher",
        LastMessageId = "last-message",
        OriginatingMessageId = "origin-message",
        MessageContent = new MessageContent(),
    };
}
