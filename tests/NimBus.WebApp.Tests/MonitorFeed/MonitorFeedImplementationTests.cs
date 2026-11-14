#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core;
using NimBus.Core.Endpoints;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using NimBus.WebApp.Services.DashboardTokens;
using NimBus.WebApp.Services.Heartbeat;
using NimBus.WebApp.Services.MonitorFeed;
using HeartbeatSettings = NimBus.MessageStore.States.HeartbeatSettings;
using MessageContent = NimBus.Core.Messages.MessageContent;
using MessageType = NimBus.Core.Messages.MessageType;

namespace NimBus.WebApp.Tests.MonitorFeed;

[TestClass]
public class MonitorFeedImplementationTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 10, 12, 34, 56, TimeSpan.Zero);

    [TestMethod]
    public async Task A_signed_in_caller_without_the_site_reader_role_is_forbidden()
    {
        var feed = new Harness().Feed(reader: false);

        Assert.IsInstanceOfType<ForbidResult>((await feed.GetMonitorFeedStatusAsync()).Result);
        Assert.IsInstanceOfType<ForbidResult>((await feed.GetMonitorFeedMetricsAsync()).Result);
        Assert.IsInstanceOfType<ForbidResult>((await feed.GetMonitorFeedHistoryAsync()).Result);
    }

    [TestMethod]
    public async Task A_token_caller_is_admitted_without_a_role_and_flagged()
    {
        var harness = new Harness();

        var status = (await harness.Feed(reader: false, token: true).GetMonitorFeedStatusAsync()).Value!;

        Assert.IsTrue(status.TokenAccess);
        Assert.AreEqual(2, status.Endpoints.Count);
    }

    [TestMethod]
    public async Task A_claim_named_like_the_token_claim_from_another_identity_is_not_a_token()
    {
        var harness = new Harness();
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(DashboardTokenDefaults.TokenIdClaim, "x")], "Cookies")),
        };

        var result = await harness.Feed(reader: false, context: context).GetMonitorFeedStatusAsync();

        Assert.IsInstanceOfType<ForbidResult>(result.Result);
    }

    [TestMethod]
    public async Task The_status_carries_counts_blocked_sessions_heartbeat_and_acknowledgement()
    {
        var harness = new Harness();
        await harness.Store.UploadFailedMessage("e1", "s1", "Billing", Event("Billing", "e1", "s1"));
        await harness.Store.UploadFailedMessage("e2", "s2", "Billing", Event("Billing", "e2", "s2"));
        await harness.Store.UploadDeferredMessage("e3", "s2", "Billing", Event("Billing", "e3", "s2"));
        harness.Heartbeats.Rows.Add(new HeartbeatOverviewItem
        {
            EndpointId = "Billing",
            Status = HeartbeatStatus.On,
            LastStartTime = Now.UtcDateTime.AddSeconds(-5),
            LastEndTime = Now.UtcDateTime.AddSeconds(-30),
        });
        await harness.Acknowledgements.AcknowledgeAsync("Billing", "known outage", "mia");

        var status = (await harness.Feed().GetMonitorFeedStatusAsync()).Value!;
        var billing = status.Endpoints.Single(e => e.Status.EndpointId == "Billing");
        var crm = status.Endpoints.Single(e => e.Status.EndpointId == "Crm");

        Assert.IsFalse(status.TokenAccess);
        Assert.IsFalse(status.Stale);
        Assert.AreEqual(2d, billing.Status.FailedCount);
        Assert.AreEqual(1d, billing.Status.DeferredCount);
        Assert.AreEqual(2, billing.BlockedSessionCount);
        Assert.AreEqual("alive", billing.Heartbeat!.Liveness);
        Assert.AreEqual(Now.UtcDateTime.AddSeconds(-30), billing.Heartbeat.LastAnsweredUtc, "the last answered probe, not the last sent");
        Assert.AreEqual("known outage", billing.Acknowledgement!.Reason);
        Assert.AreEqual(0, crm.BlockedSessionCount);
        Assert.IsNull(crm.Heartbeat);
        Assert.IsNull(crm.Acknowledgement);
        Assert.AreEqual("dev", status.Environment);
    }

    [TestMethod]
    public async Task Every_response_forbids_caching()
    {
        var harness = new Harness();
        var context = new DefaultHttpContext();
        var feed = harness.Feed(context: context);

        await feed.GetMonitorFeedStatusAsync();

        Assert.AreEqual("no-store", context.Response.Headers.CacheControl.ToString());
    }

    [TestMethod]
    public async Task A_missing_endpoint_container_gives_the_stub_and_no_blocked_count()
    {
        var harness = new Harness(new UnreadableEndpointStore("Crm"));

        var status = (await harness.Feed().GetMonitorFeedStatusAsync()).Value!;
        var crm = status.Endpoints.Single(e => e.Status.EndpointId == "Crm");

        Assert.AreEqual("unavailable", crm.Status.StorageStatus);
        Assert.IsNull(crm.BlockedSessionCount);
    }

    [TestMethod]
    public async Task A_failing_blocked_session_query_leaves_the_count_out_but_keeps_the_status()
    {
        var store = new CountingStore { FailBlockedSessions = true };
        var harness = new Harness(store);

        var status = (await harness.Feed().GetMonitorFeedStatusAsync()).Value!;

        Assert.IsTrue(status.Endpoints.All(e => e.BlockedSessionCount is null));
        Assert.IsFalse(status.Stale);
    }

    [TestMethod]
    public async Task Two_callers_inside_the_ttl_cause_one_store_read()
    {
        var store = new CountingStore();
        var harness = new Harness(store);

        var asReader = (await harness.Feed().GetMonitorFeedMetricsAsync()).Value!;
        var asToken = (await harness.Feed(reader: false, token: true).GetMonitorFeedMetricsAsync()).Value!;

        Assert.AreEqual(1, store.TimeSeriesReads);
        Assert.AreEqual(asReader.GeneratedAtUtc, asToken.GeneratedAtUtc);
    }

    [TestMethod]
    public async Task A_store_failure_after_a_good_read_serves_the_old_result_as_stale_without_retrying_inside_the_ttl()
    {
        var store = new CountingStore();
        var harness = new Harness(store);
        var first = (await harness.Feed().GetMonitorFeedMetricsAsync()).Value!;

        store.FailTimeSeries = true;
        harness.Clock.Advance(TimeSpan.FromSeconds(31));
        var stale = (await harness.Feed().GetMonitorFeedMetricsAsync()).Value!;
        var again = (await harness.Feed().GetMonitorFeedMetricsAsync()).Value!;

        Assert.IsTrue(stale.Stale);
        Assert.AreEqual(first.GeneratedAtUtc, stale.GeneratedAtUtc);
        Assert.IsTrue(again.Stale);
        Assert.AreEqual(2, store.TimeSeriesReads, "one failed attempt, then the stale result is reused for the TTL");
        Assert.IsFalse(first.Stale, "the earlier response is not changed");
    }

    [TestMethod]
    public async Task A_store_failure_with_nothing_cached_answers_503()
    {
        var harness = new Harness(new CountingStore { FailTimeSeries = true });

        var result = await harness.Feed().GetMonitorFeedMetricsAsync();

        Assert.AreEqual(StatusCodes.Status503ServiceUnavailable, ((ObjectResult)result.Result!).StatusCode);
    }

    [TestMethod]
    public async Task The_metrics_have_sixty_minute_buckets_and_per_endpoint_counts_and_weighted_timings()
    {
        var harness = new Harness();
        var at = Now.UtcDateTime.AddMinutes(-10);
        await harness.Store.StoreMessage(Message("p1", MessageType.EventRequest, at, endpointId: "Billing", from: "Crm"));
        await harness.Store.StoreMessage(Message("h1", MessageType.ResolutionResponse, at, endpointId: "Billing", queue: 100, processing: 10));
        await harness.Store.StoreMessage(Message("h2", MessageType.ResolutionResponse, at, endpointId: "Billing", eventType: "Other", queue: 400, processing: null));
        await harness.Store.StoreMessage(Message("f1", MessageType.ErrorResponse, at, endpointId: "Billing", queue: 100, processing: 40));

        var metrics = (await harness.Feed().GetMonitorFeedMetricsAsync()).Value!;

        Assert.AreEqual(60, metrics.LastHour.Count);
        Assert.AreEqual(Now.UtcDateTime.ToString("o")[..16], metrics.LastHour.Last().Timestamp, "the last bucket is the current minute");
        var billing = metrics.Endpoints.Single(e => e.EndpointId == "Billing").Counts;
        var crm = metrics.Endpoints.Single(e => e.EndpointId == "Crm").Counts;
        Assert.AreEqual(2, billing.Handled);
        Assert.AreEqual(1, billing.Failed);
        Assert.AreEqual(1, crm.Published);
        Assert.AreEqual(3, billing.Queue.Count);
        Assert.AreEqual(200, billing.Queue.AvgMs, 0.01, "weighted over both event types");
        Assert.AreEqual(2, billing.Processing.Count, "the message without a processing time is not counted");
        Assert.AreEqual(25, billing.Processing.AvgMs, 0.01);
        Assert.AreEqual(1, metrics.Fleet.Published);
        Assert.AreEqual(3, metrics.Fleet.Queue.Count);
    }

    [TestMethod]
    public async Task The_history_has_seventy_two_hour_buckets_and_the_latency_series()
    {
        var harness = new Harness();
        await harness.Store.StoreMessage(Message("h1", MessageType.ResolutionResponse, Now.UtcDateTime.AddHours(-2), endpointId: "Billing", queue: 50, processing: 5));

        var history = (await harness.Feed().GetMonitorFeedHistoryAsync()).Value!;

        Assert.AreEqual(72, history.LastThreeDays.Count);
        Assert.AreEqual(Now.UtcDateTime.ToString("o")[..13], history.LastThreeDays.Last().Timestamp);
        var point = history.LatencyLastDay!.Single();
        Assert.AreEqual(Now.UtcDateTime.AddHours(-2).ToString("o")[..13], point.Timestamp);
        Assert.AreEqual(50, point.Queue.AvgMs, 0.01);
        Assert.AreEqual(5, history.Endpoints.Single(e => e.EndpointId == "Billing").Processing.AvgMs, 0.01);
    }

    [TestMethod]
    public async Task Without_the_monitor_store_the_latency_series_and_blocked_counts_are_unavailable()
    {
        var harness = new Harness(withMonitorStore: false);

        var history = (await harness.Feed().GetMonitorFeedHistoryAsync()).Value!;
        var status = (await harness.Feed().GetMonitorFeedStatusAsync()).Value!;

        Assert.IsNull(history.LatencyLastDay);
        Assert.IsTrue(status.Endpoints.All(e => e.BlockedSessionCount is null));
    }

    [TestMethod]
    public void The_windows_start_on_a_bucket_boundary_and_hold_a_fixed_number_of_buckets()
    {
        var at = new DateTime(2026, 11, 10, 12, 0, 0, DateTimeKind.Utc);

        Assert.AreEqual(new FeedWindow(at.AddMinutes(-59), at.AddMinutes(1)), MonitorFeedWindows.LastHourByMinute(at));
        Assert.AreEqual(new FeedWindow(at.AddMinutes(-59), at.AddMinutes(1)), MonitorFeedWindows.LastHourByMinute(at.AddSeconds(59.9)));
        Assert.AreEqual(new FeedWindow(at.AddHours(-71), at.AddHours(1)), MonitorFeedWindows.LastThreeDaysByHour(at.AddMinutes(59)));
        Assert.AreEqual(new FeedWindow(at.AddHours(-23), at.AddHours(1)), MonitorFeedWindows.LastDayByHour(at));
    }

    [TestMethod]
    public void Combining_weights_by_count_and_ignores_empty_parts()
    {
        var combined = MonitorFeedWindows.Combine(
        [
            new LatencyAggregate { Count = 1, AvgMs = 100, MinMs = 100, MaxMs = 100 },
            new LatencyAggregate(),
            new LatencyAggregate { Count = 3, AvgMs = 20, MinMs = 10, MaxMs = 30 },
        ]);

        Assert.AreEqual(4, combined.Count);
        Assert.AreEqual(40, combined.AvgMs, 0.001);
        Assert.AreEqual(10, combined.MinMs);
        Assert.AreEqual(100, combined.MaxMs);
        Assert.AreEqual(0, MonitorFeedWindows.Combine([new LatencyAggregate()]).Count);
    }

    // ───────── Test doubles ─────────

    private static UnresolvedEvent Event(string endpointId, string eventId, string sessionId) => new()
    {
        EventId = eventId,
        SessionId = sessionId,
        EndpointId = endpointId,
        EventTypeId = "OrderPlaced",
        MessageContent = new MessageContent(),
    };

    private static MessageEntity Message(string id, MessageType type, DateTime at, string endpointId, string from = "publisher",
        string eventType = "OrderPlaced", long? queue = null, long? processing = null) => new()
    {
        EventId = id,
        MessageId = id,
        EndpointId = endpointId,
        SessionId = "s",
        EventTypeId = eventType,
        EnqueuedTimeUtc = at,
        MessageType = type,
        From = from,
        To = endpointId,
        QueueTimeMs = queue,
        ProcessingTimeMs = processing,
        MessageContent = new MessageContent(),
    };

    private sealed class Harness
    {
        private readonly bool _withMonitorStore;
        private readonly StoreResultCache _cache = new(new MemoryCache(new MemoryCacheOptions()));
        private readonly MonitorFeedCache _feedCache;

        public Harness(InMemoryMessageStore? store = null, bool withMonitorStore = true)
        {
            Store = store ?? new InMemoryMessageStore();
            _withMonitorStore = withMonitorStore;
            Clock = new FakeTimeProvider(Now);
            _feedCache = new MonitorFeedCache(_cache, Clock, NullLogger<MonitorFeedCache>.Instance);
            Acknowledgements = new MonitorAcknowledgementService(Store, Store, _cache, NullLogger<MonitorAcknowledgementService>.Instance, Clock);
        }

        public InMemoryMessageStore Store { get; }
        public FakeTimeProvider Clock { get; }
        public FakeHeartbeats Heartbeats { get; } = new();
        public MonitorAcknowledgementService Acknowledgements { get; }

        public MonitorFeedImplementation Feed(bool reader = true, bool token = false, DefaultHttpContext? context = null)
        {
            context ??= new DefaultHttpContext();
            if (token)
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(DashboardTokenDefaults.TokenIdClaim, "abcdefghijkl")], DashboardTokenDefaults.SchemeName));
            }

            return new MonitorFeedImplementation(
                new HttpContextAccessor { HttpContext = context },
                new FakePlatform("Billing", "Crm"),
                new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Environment"] = "dev" }).Build(),
                new ReaderStub(reader),
                Store,
                Store,
                Store,
                _cache,
                _feedCache,
                Acknowledgements,
                Heartbeats,
                Clock,
                NullLogger<MonitorFeedImplementation>.Instance,
                _withMonitorStore ? Store : null);
        }
    }

    private sealed class CountingStore : InMemoryMessageStore
    {
        public int TimeSeriesReads;
        public bool FailTimeSeries { get; set; }
        public bool FailBlockedSessions { get; set; }

        public override Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, DateTime to, int substringLength, string bucketLabel)
        {
            Interlocked.Increment(ref TimeSeriesReads);
            return FailTimeSeries
                ? throw new InvalidOperationException("store down")
                : base.GetTimeSeriesMetrics(from, to, substringLength, bucketLabel);
        }

        public override Task<int> GetBlockedSessionCount(string endpointId)
            => FailBlockedSessions ? throw new InvalidOperationException("store down") : base.GetBlockedSessionCount(endpointId);
    }

    private sealed class UnreadableEndpointStore(string endpointId) : InMemoryMessageStore
    {
        public override Task<EndpointStateCount> DownloadEndpointStateCount(string id)
            => id == endpointId ? throw new EndpointNotFoundException(id) : base.DownloadEndpointStateCount(id);
    }

    private sealed class ReaderStub(bool reader) : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null)
            => Task.FromResult(reader && required == AccessRole.Reader && endpointId is null);
        public Task<bool> CanReadPiiAsync() => Task.FromResult(false);
        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => throw new NotSupportedException();
        public string? GetCurrentUserName() => null;
    }

    private sealed class FakeHeartbeats : IHeartbeatService
    {
        public List<HeartbeatOverviewItem> Rows { get; } = [];

        public Task<IReadOnlyList<HeartbeatOverviewItem>> GetOverviewAsync() => Task.FromResult<IReadOnlyList<HeartbeatOverviewItem>>(Rows);
        public Task<HeartbeatSettings> GetSettingsAsync() => throw new NotSupportedException();
        public Task<HeartbeatSettings> SetSettingsAsync(HeartbeatSettings settings) => throw new NotSupportedException();
        public Task<int> SweepTimeoutsAsync() => throw new NotSupportedException();
        public Task<int> SendHeartbeatsAsync(bool force = false) => throw new NotSupportedException();
        public Task SetEndpointEnabledAsync(string endpointId, bool enabled) => throw new NotSupportedException();
        public Task<IReadOnlyList<ServiceHealth>> GetServiceHealthAsync() => throw new NotSupportedException();
        public Task<bool> ProbeResolverAsync() => throw new NotSupportedException();
        public Task<bool> RunScheduledTickAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FakePlatform(params string[] endpointIds) : IPlatform
    {
        public IEnumerable<IEndpoint> Endpoints { get; } = endpointIds.Select(id => (IEndpoint)new FakeEndpoint(id)).ToList();
        public IEnumerable<IEventType> EventTypes => [];
        public IEnumerable<IEndpoint> GetConsumers(IEventType eventType) => [];
        public IEnumerable<IEndpoint> GetProducers(IEventType eventType) => [];
    }

    private sealed class FakeEndpoint(string id) : IEndpoint
    {
        public string Id { get; } = id;
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => string.Empty;
        public string SecurityGroupName => string.Empty;
        public ISystem System => null!;
        public IEnumerable<IEventType> EventTypesProduced => [];
        public IEnumerable<IEventType> EventTypesConsumed => [];
        public IEnumerable<IRoleAssignment> RoleAssignments => [];
    }
}
