#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// The short-TTL store-result cache must collapse repeated hot reads (status
/// counts, metrics aggregates) into one store round-trip per window, share one
/// in-flight factory among concurrent callers, and never cache failures.
/// </summary>
[TestClass]
public sealed class StoreResultCacheTests
{
    [TestMethod]
    public async Task Second_call_within_ttl_reuses_cached_value()
    {
        var cache = NewCache();
        var calls = 0;

        var first = await cache.GetOrCreateAsync("k", TimeSpan.FromMinutes(1), () => Task.FromResult(++calls));
        var second = await cache.GetOrCreateAsync("k", TimeSpan.FromMinutes(1), () => Task.FromResult(++calls));

        Assert.AreEqual(1, first);
        Assert.AreEqual(1, second, "Second call within the TTL must return the cached value.");
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task Concurrent_callers_share_one_inflight_factory()
    {
        var cache = NewCache();
        var calls = 0;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task<int> SlowFactory()
        {
            Interlocked.Increment(ref calls);
            await gate.Task;
            return 42;
        }

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => cache.GetOrCreateAsync("k", TimeSpan.FromMinutes(1), SlowFactory))
            .ToList();
        gate.SetResult();
        var results = await Task.WhenAll(tasks);

        Assert.IsTrue(results.All(r => r == 42));
        Assert.AreEqual(1, calls, "Concurrent callers must share a single in-flight factory invocation.");
    }

    [TestMethod]
    public async Task Faulted_factory_is_not_cached_and_next_call_retries()
    {
        var cache = NewCache();
        var calls = 0;

        Task<int> FailingThenSucceeding()
        {
            calls++;
            return calls == 1
                ? Task.FromException<int>(new InvalidOperationException("store down"))
                : Task.FromResult(7);
        }

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => cache.GetOrCreateAsync("k", TimeSpan.FromMinutes(1), FailingThenSucceeding));
        var second = await cache.GetOrCreateAsync("k", TimeSpan.FromMinutes(1), FailingThenSucceeding);

        Assert.AreEqual(7, second, "A faulted result must be evicted so the next caller retries the store.");
        Assert.AreEqual(2, calls);
    }

    [TestMethod]
    public async Task Different_keys_do_not_share_entries()
    {
        var cache = NewCache();

        var a = await cache.GetOrCreateAsync("a", TimeSpan.FromMinutes(1), () => Task.FromResult(1));
        var b = await cache.GetOrCreateAsync("b", TimeSpan.FromMinutes(1), () => Task.FromResult(2));

        Assert.AreEqual(1, a);
        Assert.AreEqual(2, b);
    }

    [TestMethod]
    public async Task Endpoint_status_counts_hit_the_store_once_per_ttl_window()
    {
        var store = new CountingStore();
        var sut = CreateEndpointSut(store, "ep-1", "ep-2");

        await sut.PostApiEndpointStatusCountAsync(new[] { "ep-1", "ep-2" });
        await sut.PostApiEndpointStatusCountAsync(new[] { "ep-1", "ep-2" });

        Assert.AreEqual(1, store.StateCountCalls("ep-1"),
            "Repeated status polls inside the 5s TTL must hit the store once per endpoint.");
        Assert.AreEqual(1, store.StateCountCalls("ep-2"));
    }

    [TestMethod]
    public async Task Metrics_overview_hits_the_store_once_per_period_per_ttl_window()
    {
        var store = new CountingStore();
        var cache = NewCache();
        var sut = new MetricsImplementation(store, cache, new AllowAllAuthorizationService());

        await sut.GetMetricsOverviewAsync(Period._1d);
        await sut.GetMetricsOverviewAsync(Period._1d);
        await sut.GetMetricsOverviewAsync(Period._7d);

        Assert.AreEqual(2, store.MetricsCalls,
            "Same-period calls inside the TTL share one store query; a different period is a distinct cache key.");
    }

    [TestMethod]
    public async Task Failed_insights_cache_the_aggregate_per_period_across_controllers()
    {
        var store = new FailedInsightsStore();
        var cache = NewCache();
        var first = new MetricsImplementation(store, cache, new AllowAllAuthorizationService());
        var second = new MetricsImplementation(store, cache, new AllowAllAuthorizationService());

        var a = await first.GetMetricsFailedInsightsAsync(Period._1d);
        var b = await second.GetMetricsFailedInsightsAsync(Period._1d);
        await first.GetMetricsFailedInsightsAsync(Period._7d);

        Assert.AreEqual(2, store.Calls);
        Assert.AreSame(a.Value, b.Value, "Cache the aggregate, not just the raw error rows.");
        Assert.AreEqual(2, a.Value!.TotalFailed);
        Assert.AreEqual(2, a.Value.Groups.Single().Count);
        Assert.AreEqual(2, a.Value.Groups.Single().SubGroups.Single().Count);
    }

    [TestMethod]
    public async Task Concurrent_failed_insights_requests_share_one_store_query()
    {
        var release = new TaskCompletionSource<List<FailedMessageInfo>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var store = new FailedInsightsStore { Read = () => release.Task };
        var sut = new MetricsImplementation(store, NewCache(), new AllowAllAuthorizationService());
        var requests = Enumerable.Range(0, 10).Select(_ => sut.GetMetricsFailedInsightsAsync(Period._1d)).ToArray();
        release.SetResult([]);
        await Task.WhenAll(requests);
        Assert.AreEqual(1, store.Calls);
    }

    [TestMethod]
    public async Task Failed_insights_retry_after_store_failure()
    {
        var store = new FailedInsightsStore { Read = () => Task.FromException<List<FailedMessageInfo>>(new InvalidOperationException("store down")) };
        var sut = new MetricsImplementation(store, NewCache(), new AllowAllAuthorizationService());
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => sut.GetMetricsFailedInsightsAsync(Period._1d));
        store.Read = () => Task.FromResult(new List<FailedMessageInfo>());
        var result = await sut.GetMetricsFailedInsightsAsync(Period._1d);
        Assert.AreEqual(0, result.Value!.TotalFailed);
        Assert.AreEqual(2, store.Calls);
    }

    [TestMethod]
    public async Task Failed_insights_check_authorization_even_when_cache_is_warm()
    {
        var store = new FailedInsightsStore();
        var authorization = new AllowAllAuthorizationService();
        var sut = new MetricsImplementation(store, NewCache(), authorization);
        await sut.GetMetricsFailedInsightsAsync(Period._1d);
        authorization.Allowed = false;
        var denied = await sut.GetMetricsFailedInsightsAsync(Period._1d);
        Assert.IsInstanceOfType<ForbidResult>(denied.Result);
        Assert.AreEqual(1, store.Calls);
    }

    private sealed class FailedInsightsStore : InMemoryMessageStore
    {
        public int Calls { get; private set; }
        public Func<Task<List<FailedMessageInfo>>> Read { get; set; } = () => Task.FromResult(new List<FailedMessageInfo>
        {
            new() { EndpointId = "crm", EventTypeId = "Account", ErrorText = "Timeout: connection unavailable", EnqueuedTimeUtc = DateTime.UtcNow },
            new() { EndpointId = "erp", EventTypeId = "Account", ErrorText = "Timeout: connection unavailable", EnqueuedTimeUtc = DateTime.UtcNow },
        });

        public override Task<List<FailedMessageInfo>> GetFailedMessageInsights(DateTime from, DateTime to)
        {
            Calls++;
            return Read();
        }
    }

    private static StoreResultCache NewCache() =>
        new StoreResultCache(new MemoryCache(new MemoryCacheOptions()));

    private static EndpointImplementation CreateEndpointSut(INimBusMessageStore store, params string[] endpointIds)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Environment"] = "dev" })
            .Build();

        return new EndpointImplementation(
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new FakePlatform(endpointIds),
            configuration,
            store,
            store,
            store,
            serviceBusManagement: null!,
            new AllowAllAuthorizationService(),
            NullLogger<EndpointImplementation>.Instance,
            auditLogService: null!,
            NewCache(),
            PayloadRedactionTests.NewRedaction());
    }

    private sealed class CountingStore : InMemoryMessageStore
    {
        private readonly Dictionary<string, int> _stateCountCalls = new(StringComparer.Ordinal);
        private int _metricsCalls;

        public int StateCountCalls(string endpointId)
        {
            lock (_stateCountCalls)
            {
                return _stateCountCalls.GetValueOrDefault(endpointId);
            }
        }

        public int MetricsCalls => Volatile.Read(ref _metricsCalls);

        public override Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId)
        {
            lock (_stateCountCalls)
            {
                _stateCountCalls[endpointId] = _stateCountCalls.GetValueOrDefault(endpointId) + 1;
            }

            return base.DownloadEndpointStateCount(endpointId);
        }

        public override Task<EndpointMetricsResult> GetEndpointMetrics(DateTime from, DateTime to)
        {
            Interlocked.Increment(ref _metricsCalls);
            return base.GetEndpointMetrics(from, to);
        }
    }

    private sealed class FakePlatform : NimBus.Core.IPlatform
    {
        private readonly List<NimBus.Core.Endpoints.IEndpoint> _endpoints;

        public FakePlatform(IEnumerable<string> endpointIds)
        {
            _endpoints = endpointIds.Select(id => (NimBus.Core.Endpoints.IEndpoint)new FakeEndpoint(id)).ToList();
        }

        public IEnumerable<NimBus.Core.Endpoints.IEndpoint> Endpoints => _endpoints;

        public IEnumerable<NimBus.Core.Events.IEventType> EventTypes => Enumerable.Empty<NimBus.Core.Events.IEventType>();

        public IEnumerable<NimBus.Core.Endpoints.IEndpoint> GetConsumers(NimBus.Core.Events.IEventType eventType) =>
            Enumerable.Empty<NimBus.Core.Endpoints.IEndpoint>();

        public IEnumerable<NimBus.Core.Endpoints.IEndpoint> GetProducers(NimBus.Core.Events.IEventType eventType) =>
            Enumerable.Empty<NimBus.Core.Endpoints.IEndpoint>();
    }

    private sealed class FakeEndpoint : NimBus.Core.Endpoints.IEndpoint
    {
        public FakeEndpoint(string id)
        {
            Id = id;
        }

        public string Id { get; }
        public string Name => Id;
        public string Description => string.Empty;
        public string Namespace => string.Empty;
        public string SecurityGroupName => string.Empty;
        public NimBus.Core.Endpoints.ISystem System => null!;
        public IEnumerable<NimBus.Core.Events.IEventType> EventTypesProduced => Enumerable.Empty<NimBus.Core.Events.IEventType>();
        public IEnumerable<NimBus.Core.Events.IEventType> EventTypesConsumed => Enumerable.Empty<NimBus.Core.Events.IEventType>();
        public IEnumerable<NimBus.Core.Endpoints.IRoleAssignment> RoleAssignments => Enumerable.Empty<NimBus.Core.Endpoints.IRoleAssignment>();
    }

    private sealed class AllowAllAuthorizationService : IEndpointAuthorizationService
    {
        public bool Allowed { get; set; } = true;

        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(Allowed);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(true);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess
        {
            SiteRole = AccessRole.Owner,
            IsPiiReader = true,
        });

        public string GetCurrentUserName() => "test-user";
    }
}
