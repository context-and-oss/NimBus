#pragma warning disable CA1707, CA2007
using System.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Diagnostics;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace NimBus.OpenTelemetry.Tests;

[TestClass]
public sealed class StoreDecoratorTests
{
    [TestMethod]
    public async Task Decorator_records_duration_per_operation()
    {
        var metrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;

        var inner = new InMemoryMessageStore();
        var sut = NimBusOpenTelemetryDecorators.InstrumentMessageTrackingStore(inner, "test");

        var ev = new UnresolvedEvent { EventId = "event-1", SessionId = "session-1", EndpointId = "ep-1" };
        await sut.UploadCompletedMessage("event-1", "session-1", "ep-1", ev);
        meterProvider.ForceFlush();

        var duration = metrics.Single(m => m.Name == "nimbus.store.operation.duration");
        long observations = 0;
        foreach (ref readonly var point in duration.GetMetricPoints())
            observations += point.GetHistogramCount();
        Assert.AreEqual(1, observations);

        var firstPoint = First(duration);
        var tags = TagsOf(firstPoint);
        Assert.AreEqual("UploadCompletedMessage", tags[MessagingAttributes.NimBusStoreOperation]);
        Assert.AreEqual("test", tags[MessagingAttributes.NimBusStoreProvider]);
    }

    [TestMethod]
    public async Task Decorator_forwards_endpoint_state_count_with_oldest_failure()
    {
        var inner = new InMemoryMessageStore();
        var sut = NimBusOpenTelemetryDecorators.InstrumentMessageTrackingStore(inner, "test");
        var ev = new UnresolvedEvent { EventId = "event-1", SessionId = "session-1", EndpointId = "ep-1" };
        await inner.UploadFailedMessage("event-1", "session-1", "ep-1", ev);
        var expected = (await inner.DownloadEndpointStateCount("ep-1")).OldestFailureAt;

        var counts = await sut.DownloadEndpointStateCount("ep-1");

        Assert.AreEqual(1, counts.FailedCount);
        Assert.IsNotNull(counts.OldestFailureAt);
        Assert.AreEqual(expected, counts.OldestFailureAt);
    }

    [TestMethod]
    public async Task Decorator_records_failure_with_error_type()
    {
        var metrics = new List<Metric>();
        using var meterProvider = Sdk.CreateMeterProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(metrics)
            .Build()!;

        var sut = NimBusOpenTelemetryDecorators.InstrumentMessageTrackingStore(
            new ThrowingStore(new InvalidOperationException("store down")),
            "test");

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => sut.UploadFailedMessage("event-1", "session-1", "ep-1", new UnresolvedEvent()));
        meterProvider.ForceFlush();

        var failed = metrics.Single(m => m.Name == "nimbus.store.operation.failed");
        var point = First(failed);
        var tags = TagsOf(point);
        Assert.AreEqual("UploadFailedMessage", tags[MessagingAttributes.NimBusStoreOperation]);
        Assert.AreEqual("test", tags[MessagingAttributes.NimBusStoreProvider]);
        Assert.AreEqual(typeof(InvalidOperationException).FullName, tags[MessagingAttributes.ErrorType]);
        Assert.AreEqual(1, point.GetSumLong());
    }

    [TestMethod]
    public async Task Decorator_does_not_open_span_when_Verbose_false()
    {
        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(activities)
            .Build()!;

        var options = new TestOptionsMonitor(new NimBusOpenTelemetryOptions { Verbose = false });
        var sut = NimBusOpenTelemetryDecorators.InstrumentMessageTrackingStore(
            new InMemoryMessageStore(), "test", options);

        await sut.UploadCompletedMessage("e", "s", "ep", new UnresolvedEvent());
        tracer.ForceFlush();

        Assert.AreEqual(0, activities.Count(a => a.Source.Name == NimBusInstrumentation.StoreActivitySourceName),
            "No NimBus.Store.* spans should be emitted unless Verbose is true");
    }

    [TestMethod]
    public async Task Decorator_opens_span_per_operation_when_Verbose_true()
    {
        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(activities)
            .Build()!;

        var options = new TestOptionsMonitor(new NimBusOpenTelemetryOptions { Verbose = true });
        var sut = NimBusOpenTelemetryDecorators.InstrumentMessageTrackingStore(
            new InMemoryMessageStore(), "test", options);

        await sut.UploadCompletedMessage("e", "s", "ep", new UnresolvedEvent());
        tracer.ForceFlush();

        var span = activities.Single(a => a.Source.Name == NimBusInstrumentation.StoreActivitySourceName);
        Assert.AreEqual("NimBus.Store.UploadCompletedMessage", span.OperationName);
        Assert.AreEqual(ActivityKind.Internal, span.Kind);
        Assert.AreEqual("UploadCompletedMessage", span.GetTagItem(MessagingAttributes.NimBusStoreOperation));
        Assert.AreEqual("test", span.GetTagItem(MessagingAttributes.NimBusStoreProvider));
        Assert.AreEqual(ActivityStatusCode.Ok, span.Status);
    }

    [TestMethod]
    public async Task Decorator_marks_verbose_span_error_on_failure()
    {
        var activities = new List<Activity>();
        using var tracer = Sdk.CreateTracerProviderBuilder()
            .AddNimBusInstrumentation()
            .AddInMemoryExporter(activities)
            .Build()!;

        var options = new TestOptionsMonitor(new NimBusOpenTelemetryOptions { Verbose = true });
        var sut = NimBusOpenTelemetryDecorators.InstrumentMessageTrackingStore(
            new ThrowingStore(new InvalidOperationException("nope")), "test", options);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => sut.UploadCompletedMessage("e", "s", "ep", new UnresolvedEvent()));
        tracer.ForceFlush();

        var span = activities.Single(a => a.Source.Name == NimBusInstrumentation.StoreActivitySourceName);
        Assert.AreEqual(ActivityStatusCode.Error, span.Status);
        Assert.AreEqual(typeof(InvalidOperationException).FullName, span.GetTagItem(MessagingAttributes.ErrorType));
    }

    [TestMethod]
    public void Decorator_forwards_every_default_interface_method()
    {
        // A default interface method the decorator does not override silently
        // bypasses the inner store (e.g. a provider-supported operation throws
        // the interface's NotSupportedException fallback instead).
        var decoratorType = NimBusOpenTelemetryDecorators
            .InstrumentMessageTrackingStore(new InMemoryMessageStore(), "test").GetType();
        var map = decoratorType.GetInterfaceMap(typeof(IMessageTrackingStore));

        var notForwarded = map.TargetMethods
            .Where(m => m.DeclaringType != decoratorType)
            .Select(m => m.Name)
            .ToList();

        Assert.AreEqual(0, notForwarded.Count, "Not forwarded: " + string.Join(", ", notForwarded));
    }

    [TestMethod]
    public async Task Decorator_forwards_TrySkipDeferredMessage_to_inner_store()
    {
        var inner = new InMemoryMessageStore();
        var sut = NimBusOpenTelemetryDecorators.InstrumentMessageTrackingStore(inner, "test");

        // No such row: the inner store answers false rather than the interface's
        // NotSupportedException fallback.
        Assert.IsFalse(await sut.TrySkipDeferredMessage("missing", "s1", "ep-1", null, DateTime.UtcNow));
    }

    private static MetricPoint First(Metric metric)
    {
        foreach (ref readonly var point in metric.GetMetricPoints())
            return point;
        throw new InvalidOperationException("Metric has no points");
    }

    private static IReadOnlyDictionary<string, object?> TagsOf(MetricPoint point)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var t in point.Tags) dict[t.Key] = t.Value;
        return dict;
    }
}

internal sealed class TestOptionsMonitor : IOptionsMonitor<NimBusOpenTelemetryOptions>
{
    public TestOptionsMonitor(NimBusOpenTelemetryOptions value) => CurrentValue = value;
    public NimBusOpenTelemetryOptions CurrentValue { get; }
    public NimBusOpenTelemetryOptions Get(string? name) => CurrentValue;
    public IDisposable? OnChange(Action<NimBusOpenTelemetryOptions, string?> listener) => null;
}

/// <summary>
/// Minimal IMessageTrackingStore that throws the configured exception from every
/// method. Wraps an InMemoryMessageStore for the methods we never call so we don't
/// hand-stub 30+ signatures — we only need the throwing behaviour on the methods
/// the tests actually exercise.
/// </summary>
internal sealed class ThrowingStore : IMessageTrackingStore
{
    private readonly Exception _exception;
    private readonly IMessageTrackingStore _passthrough = new InMemoryMessageStore();

    public ThrowingStore(Exception exception) => _exception = exception;

    private Task<T> Throw<T>() => Task.FromException<T>(_exception);
    private Task Throw() => Task.FromException(_exception);

    public Task<bool> UploadPendingMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Throw<bool>();
    public Task<bool> UploadDeferredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Throw<bool>();
    public Task<bool> UploadFailedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Throw<bool>();
    public Task<bool> UploadDeadletteredMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Throw<bool>();
    public Task<bool> UploadUnsupportedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Throw<bool>();
    public Task<bool> UploadSkippedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Throw<bool>();
    public Task<bool> UploadCompletedMessage(string eventId, string sessionId, string endpointId, UnresolvedEvent content) => Throw<bool>();

    // Methods the tests don't exercise — delegate to passthrough so we don't have
    // to stub them (return values aren't asserted).
    public Task<UnresolvedEvent?> GetPendingEvent(string endpointId, string eventId, string sessionId) => _passthrough.GetPendingEvent(endpointId, eventId, sessionId);
    public Task<UnresolvedEvent?> GetPendingHandoffByExternalJobId(string endpointId, string externalJobId, System.Threading.CancellationToken cancellationToken = default) => _passthrough.GetPendingHandoffByExternalJobId(endpointId, externalJobId, cancellationToken);
    public Task<UnresolvedEvent?> GetNextPendingHandoffEvent(string endpointId, IReadOnlyCollection<string>? eventTypeIds) => _passthrough.GetNextPendingHandoffEvent(endpointId, eventTypeIds);
    public Task<UnresolvedEvent?> GetFailedEvent(string endpointId, string eventId, string sessionId) => _passthrough.GetFailedEvent(endpointId, eventId, sessionId);
    public Task<UnresolvedEvent?> GetDeferredEvent(string endpointId, string eventId, string sessionId) => _passthrough.GetDeferredEvent(endpointId, eventId, sessionId);
    public Task<UnresolvedEvent?> GetDeadletteredEvent(string endpointId, string eventId, string sessionId) => _passthrough.GetDeadletteredEvent(endpointId, eventId, sessionId);
    public Task<UnresolvedEvent?> GetUnsupportedEvent(string endpointId, string eventId, string sessionId) => _passthrough.GetUnsupportedEvent(endpointId, eventId, sessionId);
    public Task<UnresolvedEvent?> GetEvent(string endpointId, string eventId) => _passthrough.GetEvent(endpointId, eventId);
    public Task<UnresolvedEvent?> GetEventById(string endpointId, string id) => _passthrough.GetEventById(endpointId, id);
    public Task<List<UnresolvedEvent>> GetEventsByIds(string endpointId, IEnumerable<string> eventIds) => _passthrough.GetEventsByIds(endpointId, eventIds);
    public Task<IEnumerable<UnresolvedEvent>> GetCompletedEventsOnEndpoint(string endpointId) => _passthrough.GetCompletedEventsOnEndpoint(endpointId);
    public Task<SearchResponse> GetEventsByFilter(NimBus.MessageStore.EventFilter filter, string continuationToken, int maxSearchItemsCount) => _passthrough.GetEventsByFilter(filter, continuationToken, maxSearchItemsCount);
    public Task<SearchResponse> GetFailedEventsAcrossEndpoints(NimBus.MessageStore.EventFilter filter, IReadOnlyCollection<string> endpointIds, string? continuationToken, int maxItemCount) => _passthrough.GetFailedEventsAcrossEndpoints(filter, endpointIds, continuationToken, maxItemCount);
    public Task<FailedEventHistogram> GetFailedEventHistogram(NimBus.MessageStore.EventFilter filter, IReadOnlyCollection<string> endpointIds, DateTime fromUtc, DateTime toUtc, TimeSpan bucketSize) => _passthrough.GetFailedEventHistogram(filter, endpointIds, fromUtc, toUtc, bucketSize);
    public Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId) => _passthrough.DownloadEndpointStateCount(endpointId);
    public Task<SessionStateCount> DownloadEndpointSessionStateCount(string endpointId, string sessionId) => _passthrough.DownloadEndpointSessionStateCount(endpointId, sessionId);
    public Task<IEnumerable<SessionStateCount>> DownloadEndpointSessionStateCountBatch(string endpointId, IEnumerable<string> sessionIds) => _passthrough.DownloadEndpointSessionStateCountBatch(endpointId, sessionIds);
    public Task<EndpointState?> DownloadEndpointStatePaging(string endpointId, int pageSize, string continuationToken) => _passthrough.DownloadEndpointStatePaging(endpointId, pageSize, continuationToken);
    public Task<BlockedMessageEventPage> GetBlockedEventsOnSession(string endpointId, string sessionId, int skip, int take) => _passthrough.GetBlockedEventsOnSession(endpointId, sessionId, skip, take);
    public Task<IEnumerable<UnresolvedEvent>?> GetPendingEventsOnSession(string endpointId) => _passthrough.GetPendingEventsOnSession(endpointId);
    public Task<IEnumerable<BlockedMessageEvent>> GetInvalidEventsOnSession(string endpointId) => _passthrough.GetInvalidEventsOnSession(endpointId);
    public Task<bool> RemoveMessage(string eventId, string sessionId, string endpointId) => _passthrough.RemoveMessage(eventId, sessionId, endpointId);
    public Task<bool> PurgeMessages(string endpointId, string sessionId) => _passthrough.PurgeMessages(endpointId, sessionId);
    public Task<bool> PurgeMessages(string endpointId) => _passthrough.PurgeMessages(endpointId);
    public Task ArchiveFailedEvent(string eventId, string sessionId, string endpointId) => _passthrough.ArchiveFailedEvent(eventId, sessionId, endpointId);
    public Task StoreMessage(NimBus.MessageStore.MessageEntity message) => _passthrough.StoreMessage(message);
    public Task<NimBus.MessageStore.MessageEntity> GetMessage(string eventId, string messageId) => _passthrough.GetMessage(eventId, messageId);
    public Task<IEnumerable<NimBus.MessageStore.MessageEntity>> GetEventHistory(string eventId) => _passthrough.GetEventHistory(eventId);
    public Task<NimBus.MessageStore.MessageEntity> GetLatestEventRequestMessage(string eventId) => _passthrough.GetLatestEventRequestMessage(eventId);
    public Task<NimBus.MessageStore.MessageEntity> GetFailedMessage(string eventId, string endpointId) => _passthrough.GetFailedMessage(eventId, endpointId);
    public Task<NimBus.MessageStore.MessageEntity> GetDeadletteredMessage(string eventId, string endpointId) => _passthrough.GetDeadletteredMessage(eventId, endpointId);
    public Task RemoveStoredMessage(string eventId, string messageId) => _passthrough.RemoveStoredMessage(eventId, messageId);
    public Task<int> PurgeStoredMessages(string endpointId) => _passthrough.PurgeStoredMessages(endpointId);
    public Task<NimBus.MessageStore.MessageSearchResult> SearchMessages(NimBus.MessageStore.MessageFilter filter, string? continuationToken, int maxItemCount) => _passthrough.SearchMessages(filter, continuationToken, maxItemCount);
    public Task StoreMessageAudit(string eventId, NimBus.MessageStore.MessageAuditEntity auditEntity, string? endpointId = null, string? eventTypeId = null) => _passthrough.StoreMessageAudit(eventId, auditEntity, endpointId, eventTypeId);
    public Task<IEnumerable<NimBus.MessageStore.MessageAuditEntity>> GetMessageAudits(string eventId) => _passthrough.GetMessageAudits(eventId);
    public Task<NimBus.MessageStore.AuditSearchResult> SearchAudits(NimBus.MessageStore.AuditFilter filter, string? continuationToken, int maxItemCount) => _passthrough.SearchAudits(filter, continuationToken, maxItemCount);
    public Task<IReadOnlyDictionary<string, int>> GetResubmitCounts(string endpointId, IReadOnlyCollection<string> eventIds) => _passthrough.GetResubmitCounts(endpointId, eventIds);
    public Task SetEventReport(string endpointId, string eventId, bool isReported, string? reportedBy, string? ticketId) => _passthrough.SetEventReport(endpointId, eventId, isReported, reportedBy, ticketId);
    public Task<IReadOnlyDictionary<string, NimBus.MessageStore.States.EventReport>> GetEventReports(string endpointId, IReadOnlyCollection<string> eventIds) => _passthrough.GetEventReports(endpointId, eventIds);
    public Task<string> GetEndpointErrorList(string endpointId) => _passthrough.GetEndpointErrorList(endpointId);
}
