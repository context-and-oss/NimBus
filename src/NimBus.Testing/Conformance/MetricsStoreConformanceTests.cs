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
/// Provider-agnostic conformance suite for <see cref="IMetricsStore"/>.
/// Metrics are derived from stored messages and failed resolver state, so this
/// suite uses the aggregate <see cref="INimBusMessageStore"/> for setup.
/// </summary>
[TestClass]
public abstract class MetricsStoreConformanceTests
{
    private readonly string _scope = $"ct-{Guid.NewGuid():N}"[..16];

    protected abstract INimBusMessageStore CreateStore();

    private string Id(string value) => $"{_scope}-{value}";

    [TestMethod]
    public async Task GetEndpointMetrics_counts_message_types_since_from()
    {
        var store = CreateStore();
        var from = DateTime.UtcNow.AddHours(-1);
        var receiver = Id("receiver");
        var publisher = Id("publisher");

        await store.StoreMessage(SampleMessage(Id("evt-published"), Id("msg-published"), MessageType.EventRequest, from.AddMinutes(1), endpointId: receiver, fromAddress: publisher));
        await store.StoreMessage(SampleMessage(Id("evt-handled"), Id("msg-handled"), MessageType.ResolutionResponse, from.AddMinutes(2), endpointId: receiver, fromAddress: publisher));
        await store.StoreMessage(SampleMessage(Id("evt-failed"), Id("msg-failed"), MessageType.ErrorResponse, from.AddMinutes(3), endpointId: receiver, fromAddress: publisher));
        await store.StoreMessage(SampleMessage(Id("evt-old"), Id("msg-old"), MessageType.EventRequest, from.AddMinutes(-30), endpointId: receiver, fromAddress: publisher));

        var metrics = await store.GetEndpointMetrics(from, from.AddHours(2));

        Assert.AreEqual(1, metrics.Published.Single(m => m.EndpointId == publisher && m.EventTypeId == "OrderPlaced").Count);
        Assert.AreEqual(1, metrics.Handled.Single(m => m.EndpointId == receiver && m.EventTypeId == "OrderPlaced").Count);
        Assert.AreEqual(1, metrics.Failed.Single(m => m.EndpointId == receiver && m.EventTypeId == "OrderPlaced").Count);
    }

    [TestMethod]
    public async Task GetEndpointLatencyMetrics_aggregates_outcome_timings_since_from()
    {
        var store = CreateStore();
        var from = DateTime.UtcNow.AddHours(-1);
        var receiver = Id("receiver");

        await store.StoreMessage(SampleMessage(Id("evt-lat-1"), Id("msg-lat-1"), MessageType.ResolutionResponse, from.AddMinutes(1), endpointId: receiver, queueTimeMs: 10, processingTimeMs: 100));
        await store.StoreMessage(SampleMessage(Id("evt-lat-2"), Id("msg-lat-2"), MessageType.ErrorResponse, from.AddMinutes(2), endpointId: receiver, queueTimeMs: 30, processingTimeMs: 300));
        await store.StoreMessage(SampleMessage(Id("evt-lat-old"), Id("msg-lat-old"), MessageType.ResolutionResponse, from.AddMinutes(-10), endpointId: receiver, queueTimeMs: 1000, processingTimeMs: 1000));

        var metrics = await store.GetEndpointLatencyMetrics(from, from.AddHours(2));
        var row = metrics.Latencies.Single(m => m.EndpointId == receiver && m.EventTypeId == "OrderPlaced");

        Assert.AreEqual(2, row.Queue.Count);
        Assert.AreEqual(20, row.Queue.AvgMs);
        Assert.AreEqual(10, row.Queue.MinMs);
        Assert.AreEqual(30, row.Queue.MaxMs);
        Assert.AreEqual(2, row.Processing.Count);
        Assert.AreEqual(200, row.Processing.AvgMs);
    }

    [TestMethod]
    public async Task GetFailedMessageInsights_returns_recent_failed_error_details()
    {
        var store = CreateStore();
        var from = DateTime.UtcNow.AddHours(-1);
        var eventId = Id("evt-insight");
        var receiver = Id("receiver");
        var failure = SampleMessage(
            eventId,
            Id("msg-insight"),
            MessageType.ErrorResponse,
            from.AddMinutes(1),
            endpointId: receiver,
            errorText: "Downstream timeout");
        failure.OriginatingFrom = Id("publisher");
        await store.StoreMessage(failure);
        await store.UploadFailedMessage(eventId, "session-1", receiver, SampleFailedEvent(eventId, from.AddMinutes(1), "Downstream timeout", receiver));

        var insights = await store.GetFailedMessageInsights(from, from.AddHours(2));
        var row = insights.Single(i => i.EventId == eventId);

        Assert.AreEqual(receiver, row.EndpointId);
        Assert.AreEqual("OrderPlaced", row.EventTypeId);
        Assert.AreEqual("Downstream timeout", row.ErrorText);
        Assert.AreEqual(Id("msg-insight"), row.MessageId);
        // With EventId it forms the stored event id ({EventId}_{SessionId}) that GetEventsByIds needs.
        Assert.AreEqual("session-1", row.SessionId);
        // Spec 044 Phase 3: the publisher, for grouping source-data failures by source.
        Assert.AreEqual(Id("publisher"), row.OriginatingFrom);
    }

    [TestMethod]
    public async Task GetFailedMessageInsights_rows_resolve_their_current_event_through_the_stored_id()
    {
        // Spec 043 §4.3: the drill-down reads current status with GetEventsByIds, which is only
        // guaranteed for stored ids ({EventId}_{SessionId}); a bare EventId finds nothing on Cosmos.
        var store = CreateStore();
        var from = DateTime.UtcNow.AddHours(-1);
        var eventId = Id("evt-status");
        var receiver = Id("receiver");
        await store.StoreMessage(SampleMessage(eventId, Id("msg-status"), MessageType.ErrorResponse, from.AddMinutes(1),
            endpointId: receiver, errorText: "Downstream timeout"));
        await store.UploadFailedMessage(eventId, "session-1", receiver, SampleFailedEvent(eventId, from.AddMinutes(1), "Downstream timeout", receiver));

        var row = (await store.GetFailedMessageInsights(from, from.AddHours(2))).Single(i => i.EventId == eventId);
        var events = await store.GetEventsByIds(row.EndpointId, [$"{row.EventId}_{row.SessionId}"]);

        Assert.AreEqual(eventId, events.Single().EventId);
        Assert.AreEqual(ResolutionStatus.Failed, events.Single().ResolutionStatus);
    }

    [TestMethod]
    public async Task GetFailedMessageInsights_returns_one_row_per_failed_attempt_with_its_message_id()
    {
        // Spec 043 §4.1: the counting unit is a failed attempt, identified by its exact MessageId,
        // so an event that failed twice yields two rows that classifications can join to.
        var store = CreateStore();
        var from = DateTime.UtcNow.AddHours(-1);
        var eventId = Id("evt-attempts");
        var receiver = Id("receiver");
        var firstAttempt = Id("msg-attempt-1");
        var secondAttempt = Id("msg-attempt-2");
        await store.StoreMessage(SampleMessage(eventId, firstAttempt, MessageType.ErrorResponse, from.AddMinutes(1),
            endpointId: receiver, errorText: "First failure"));
        await store.StoreMessage(SampleMessage(eventId, secondAttempt, MessageType.ErrorResponse, from.AddMinutes(2),
            endpointId: receiver, errorText: "Second failure"));

        var rows = (await store.GetFailedMessageInsights(from, from.AddHours(2)))
            .Where(i => i.EventId == eventId)
            .OrderBy(i => i.EnqueuedTimeUtc)
            .ToList();

        Assert.HasCount(2, rows);
        Assert.AreEqual(firstAttempt, rows[0].MessageId);
        Assert.AreEqual("First failure", rows[0].ErrorText);
        Assert.AreEqual(secondAttempt, rows[1].MessageId);
        Assert.AreEqual("Second failure", rows[1].ErrorText);
        Assert.IsTrue(rows.All(r => r.SessionId == "session-1"));
    }

    [TestMethod]
    public async Task GetTimeSeriesMetrics_buckets_message_type_counts()
    {
        var store = CreateStore();
        // The query is not scoped by endpoint, so each run gets its own far-future hour:
        // providers without a per-test reset (Cosmos) would otherwise count earlier runs.
        var from = DateTime.UtcNow.AddHours(Random.Shared.Next(1_000, 1_000_000));
        var bucketTime = from.AddMinutes(1);
        var bucketKey = bucketTime.ToString("o")[..13];
        var receiver = Id("receiver");
        var publisher = Id("publisher");

        await store.StoreMessage(SampleMessage(Id("evt-ts-published"), Id("msg-ts-published"), MessageType.EventRequest, bucketTime, endpointId: receiver, fromAddress: publisher));
        await store.StoreMessage(SampleMessage(Id("evt-ts-handled"), Id("msg-ts-handled"), MessageType.ResolutionResponse, bucketTime, endpointId: receiver, fromAddress: publisher));
        await store.StoreMessage(SampleMessage(Id("evt-ts-failed"), Id("msg-ts-failed"), MessageType.ErrorResponse, bucketTime, endpointId: receiver, fromAddress: publisher));

        var timeSeries = await store.GetTimeSeriesMetrics(from, from.AddHours(2), substringLength: 13, bucketLabel: "hour");
        var bucket = timeSeries.DataPoints.Single(dp => dp.Timestamp == bucketKey);

        Assert.AreEqual("hour", timeSeries.BucketSize);
        Assert.AreEqual(1, bucket.Published);
        Assert.AreEqual(1, bucket.Handled);
        Assert.AreEqual(1, bucket.Failed);
    }

    [TestMethod]
    public async Task GetEventTypeTimeSeriesMetrics_buckets_published_counts_by_event_type()
    {
        var store = CreateStore();
        // +65min keeps this test's messages a full hour bucket away from
        // GetTimeSeriesMetrics' window: providers without per-test reset
        // (Cosmos) share one database, and that test counts by MessageType
        // across all event types.
        var from = DateTime.UtcNow.AddMinutes(65);
        var bucketTime = from.AddMinutes(1);
        var bucketKey = bucketTime.ToString("o")[..13];
        var receiver = Id("receiver");
        var publisher = Id("publisher");
        // Scoped event type: the series query has no endpoint filter, so a
        // shared-database provider would otherwise fold other tests'
        // "OrderPlaced" rows into this entry.
        var eventTypeId = Id("OrderPlaced");

        await store.StoreMessage(SampleMessage(Id("evt-et-1"), Id("msg-et-1"), MessageType.EventRequest, bucketTime, endpointId: receiver, fromAddress: publisher, eventTypeId: eventTypeId));
        await store.StoreMessage(SampleMessage(Id("evt-et-2"), Id("msg-et-2"), MessageType.EventRequest, bucketTime, endpointId: receiver, fromAddress: publisher, eventTypeId: eventTypeId));
        // Non-published outcome in the same window must not count.
        await store.StoreMessage(SampleMessage(Id("evt-et-3"), Id("msg-et-3"), MessageType.ResolutionResponse, bucketTime, endpointId: receiver, fromAddress: publisher, eventTypeId: eventTypeId));

        var result = await store.GetEventTypeTimeSeriesMetrics(from, from.AddHours(2), substringLength: 13, bucketLabel: "hour");
        var entry = result.Series.Single(s => s.EventTypeId == eventTypeId);

        Assert.AreEqual("hour", result.BucketSize);
        Assert.AreEqual(2, entry.Total);
        var bucket = entry.DataPoints.Single(dp => dp.Timestamp == bucketKey);
        Assert.AreEqual(2, bucket.Published);
    }

    [TestMethod]
    public async Task GetEndpointMetrics_excludes_messages_enqueued_at_or_after_to()
    {
        var store = CreateStore();
        var to = UpperBound();
        var from = to.AddHours(-1);
        var receiver = Id("receiver");
        var publisher = Id("publisher");

        await store.StoreMessage(SampleMessage(Id("evt-in"), Id("msg-in"), MessageType.EventRequest, to.AddMinutes(-1), endpointId: receiver, fromAddress: publisher));
        await store.StoreMessage(SampleMessage(Id("evt-at-to"), Id("msg-at-to"), MessageType.EventRequest, to, endpointId: receiver, fromAddress: publisher));
        await store.StoreMessage(SampleMessage(Id("evt-late"), Id("msg-late"), MessageType.EventRequest, to.AddMinutes(5), endpointId: receiver, fromAddress: publisher));

        var metrics = await store.GetEndpointMetrics(from, to);

        Assert.AreEqual(1, metrics.Published.Single(m => m.EndpointId == publisher && m.EventTypeId == "OrderPlaced").Count);
    }

    [TestMethod]
    public async Task GetEndpointLatencyMetrics_excludes_messages_enqueued_at_or_after_to()
    {
        var store = CreateStore();
        var to = UpperBound();
        var from = to.AddHours(-1);
        var receiver = Id("receiver");

        await store.StoreMessage(SampleMessage(Id("evt-lat-in"), Id("msg-lat-in"), MessageType.ResolutionResponse, to.AddMinutes(-1), endpointId: receiver, queueTimeMs: 10, processingTimeMs: 100));
        await store.StoreMessage(SampleMessage(Id("evt-lat-at-to"), Id("msg-lat-at-to"), MessageType.ResolutionResponse, to, endpointId: receiver, queueTimeMs: 1000, processingTimeMs: 1000));
        await store.StoreMessage(SampleMessage(Id("evt-lat-late"), Id("msg-lat-late"), MessageType.ResolutionResponse, to.AddMinutes(5), endpointId: receiver, queueTimeMs: 1000, processingTimeMs: 1000));

        var metrics = await store.GetEndpointLatencyMetrics(from, to);
        var row = metrics.Latencies.Single(m => m.EndpointId == receiver && m.EventTypeId == "OrderPlaced");

        Assert.AreEqual(1, row.Processing.Count);
        Assert.AreEqual(100, row.Processing.MaxMs);
    }

    [TestMethod]
    public async Task GetFailedMessageInsights_excludes_messages_enqueued_at_or_after_to()
    {
        var store = CreateStore();
        var to = UpperBound();
        var from = to.AddHours(-1);
        var receiver = Id("receiver");

        await store.StoreMessage(SampleMessage(Id("evt-fi-in"), Id("msg-fi-in"), MessageType.ErrorResponse, to.AddMinutes(-1), endpointId: receiver, errorText: "Inside"));
        await store.StoreMessage(SampleMessage(Id("evt-fi-at-to"), Id("msg-fi-at-to"), MessageType.ErrorResponse, to, endpointId: receiver, errorText: "At the bound"));
        await store.StoreMessage(SampleMessage(Id("evt-fi-late"), Id("msg-fi-late"), MessageType.ErrorResponse, to.AddMinutes(5), endpointId: receiver, errorText: "Too late"));

        var insights = await store.GetFailedMessageInsights(from, to);
        var ours = insights.Where(i => i.EventId?.StartsWith(_scope, StringComparison.Ordinal) == true).ToList();

        Assert.AreEqual(1, ours.Count);
        Assert.AreEqual(Id("evt-fi-in"), ours[0].EventId);
    }

    [TestMethod]
    public async Task GetTimeSeriesMetrics_zero_fills_only_up_to_to()
    {
        var store = CreateStore();
        // Hour-aligned far-future window of its own, as in GetTimeSeriesMetrics_buckets_message_type_counts.
        var start = DateTime.UtcNow.AddHours(Random.Shared.Next(1_000, 1_000_000));
        var from = new DateTime(start.Year, start.Month, start.Day, start.Hour, 0, 0, DateTimeKind.Utc);
        var to = from.AddHours(2);
        var receiver = Id("receiver");
        var publisher = Id("publisher");

        await store.StoreMessage(SampleMessage(Id("evt-tsb-in"), Id("msg-tsb-in"), MessageType.EventRequest, from.AddMinutes(1), endpointId: receiver, fromAddress: publisher));
        await store.StoreMessage(SampleMessage(Id("evt-tsb-at-to"), Id("msg-tsb-at-to"), MessageType.EventRequest, to, endpointId: receiver, fromAddress: publisher));

        var timeSeries = await store.GetTimeSeriesMetrics(from, to, substringLength: 13, bucketLabel: "hour");

        CollectionAssert.AreEqual(
            new[] { from.ToString("o")[..13], from.AddHours(1).ToString("o")[..13] },
            timeSeries.DataPoints.Select(dp => dp.Timestamp).ToArray());
        Assert.AreEqual(1, timeSeries.DataPoints[0].Published);
        Assert.AreEqual(0, timeSeries.DataPoints[1].Published);
    }

    [TestMethod]
    public async Task GetEventTypeTimeSeriesMetrics_excludes_messages_enqueued_at_or_after_to()
    {
        var store = CreateStore();
        var to = UpperBound();
        var from = to.AddHours(-1);
        var receiver = Id("receiver");
        var publisher = Id("publisher");
        var eventTypeId = Id("OrderPlaced");

        await store.StoreMessage(SampleMessage(Id("evt-etb-in"), Id("msg-etb-in"), MessageType.EventRequest, to.AddMinutes(-1), endpointId: receiver, fromAddress: publisher, eventTypeId: eventTypeId));
        await store.StoreMessage(SampleMessage(Id("evt-etb-at-to"), Id("msg-etb-at-to"), MessageType.EventRequest, to, endpointId: receiver, fromAddress: publisher, eventTypeId: eventTypeId));
        await store.StoreMessage(SampleMessage(Id("evt-etb-late"), Id("msg-etb-late"), MessageType.EventRequest, to.AddMinutes(5), endpointId: receiver, fromAddress: publisher, eventTypeId: eventTypeId));

        var result = await store.GetEventTypeTimeSeriesMetrics(from, to, substringLength: 13, bucketLabel: "hour");

        Assert.AreEqual(1, result.Series.Single(s => s.EventTypeId == eventTypeId).Total);
    }

    // An upper bound 2 ms past a whole second. A store that sends it as SQL Server's legacy
    // DATETIME rounds it up to +3 ms, which would count a message enqueued exactly at the bound.
    private static DateTime UpperBound()
    {
        var past = DateTime.UtcNow.AddHours(-2);
        return new DateTime(past.Year, past.Month, past.Day, past.Hour, past.Minute, past.Second, DateTimeKind.Utc)
            .AddMilliseconds(2);
    }

    private static MessageEntity SampleMessage(
        string eventId,
        string messageId,
        MessageType messageType,
        DateTime enqueuedTimeUtc,
        string endpointId = "receiver",
        string fromAddress = "publisher",
        long? queueTimeMs = null,
        long? processingTimeMs = null,
        string? errorText = null,
        string eventTypeId = "OrderPlaced") => new()
    {
        EventId = eventId,
        MessageId = messageId,
        EndpointId = endpointId,
        SessionId = "session-1",
        CorrelationId = "corr-1",
        EventTypeId = eventTypeId,
        EnqueuedTimeUtc = enqueuedTimeUtc,
        MessageType = messageType,
        EndpointRole = EndpointRole.Subscriber,
        From = fromAddress,
        To = endpointId,
        QueueTimeMs = queueTimeMs,
        ProcessingTimeMs = processingTimeMs,
        MessageContent = new MessageContent
        {
            ErrorContent = errorText == null ? null : new ErrorContent { ErrorText = errorText },
        },
        DeadLetterErrorDescription = errorText,
    };

    private static UnresolvedEvent SampleFailedEvent(string eventId, DateTime enqueuedTimeUtc, string errorText, string endpointId) => new()
    {
        EventId = eventId,
        SessionId = "session-1",
        EndpointId = endpointId,
        EnqueuedTimeUtc = enqueuedTimeUtc,
        UpdatedAt = enqueuedTimeUtc,
        CorrelationId = "corr-1",
        EndpointRole = EndpointRole.Subscriber,
        MessageType = MessageType.ErrorResponse,
        EventTypeId = "OrderPlaced",
        To = endpointId,
        From = "publisher",
        ResolutionStatus = ResolutionStatus.Failed,
        DeadLetterErrorDescription = errorText,
        MessageContent = new MessageContent
        {
            ErrorContent = new ErrorContent { ErrorText = errorText },
        },
    };
}
