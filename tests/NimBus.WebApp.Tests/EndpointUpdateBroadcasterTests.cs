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
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;
using static NimBus.WebApp.Tests.StorageHookWebhookKeyTests;

namespace NimBus.WebApp.Tests;

/// <summary>
/// The storage hook throttles per endpoint: a burst of Resolver writes or Change Feed batches
/// costs one recount and one <c>endpointupdate</c> broadcast at its start and one at the end
/// of each interval, and the trailing broadcast carries the state after the last write.
/// </summary>
[TestClass]
public sealed class EndpointUpdateBroadcasterTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    // Guards awaits on background work so a regression fails instead of hanging; never a sleep.
    private static readonly TimeSpan Guard = TimeSpan.FromSeconds(30);

    private const string Endpoint = "ep-1";

    [TestMethod]
    public async Task A_hook_burst_costs_one_leading_and_one_trailing_recount_and_broadcast()
    {
        var fixture = new Fixture();
        var hook = fixture.Hook();

        Assert.IsInstanceOfType<OkResult>(await hook.StoragehookReceiveAsync(Endpoint));
        Assert.AreEqual(1, fixture.Store.Reads(Endpoint));
        Assert.AreEqual(1, fixture.Proxy.SentMethods.Count, "The first change after a quiet interval is broadcast at once.");

        for (var write = 1; write <= 100; write++)
        {
            fixture.Store.FailedCount = write;
            // A fresh controller per call, as the transient registration gives each request.
            Assert.IsInstanceOfType<OkResult>(await fixture.Hook().StoragehookReceiveAsync(Endpoint));
        }

        Assert.AreEqual(1, fixture.Store.Reads(Endpoint), "Changes inside the interval wait for it to end.");
        Assert.AreEqual(1, fixture.Proxy.SentMethods.Count);

        fixture.Time.Advance(Interval);
        await fixture.Broadcaster.CurrentBroadcast(Endpoint).WaitAsync(Guard);

        Assert.AreEqual(2, fixture.Store.Reads(Endpoint));
        CollectionAssert.AreEqual(
            new[] { NimBus.WebApp.Constants.EventSignalNames.EndpointUpdate, NimBus.WebApp.Constants.EventSignalNames.EndpointUpdate },
            fixture.Proxy.SentMethods);
        Assert.AreEqual(100, LastStatus(fixture.Proxy).FailedCount, "The trailing broadcast reflects the last write.");

        fixture.Time.Advance(Interval * 5);
        await fixture.Broadcaster.CurrentBroadcast(Endpoint).WaitAsync(Guard);
        Assert.AreEqual(2, fixture.Proxy.SentMethods.Count, "Nothing is broadcast once the last state has been sent.");
    }

    [TestMethod]
    public async Task Endpoints_are_throttled_independently()
    {
        var fixture = new Fixture();

        await fixture.Broadcaster.RequestAsync("ep-1");
        await fixture.Broadcaster.RequestAsync("ep-2");
        await fixture.Broadcaster.RequestAsync("ep-1");
        await fixture.Broadcaster.RequestAsync("ep-2");

        Assert.AreEqual(1, fixture.Store.Reads("ep-1"));
        Assert.AreEqual(1, fixture.Store.Reads("ep-2"));

        fixture.Time.Advance(Interval);
        await fixture.Broadcaster.CurrentBroadcast("ep-1").WaitAsync(Guard);
        await fixture.Broadcaster.CurrentBroadcast("ep-2").WaitAsync(Guard);

        Assert.AreEqual(2, fixture.Store.Reads("ep-1"));
        Assert.AreEqual(2, fixture.Store.Reads("ep-2"));
        CollectionAssert.AreEquivalent(
            new[] { "ep-1", "ep-1", "ep-2", "ep-2" },
            fixture.Proxy.SentArgs.Select(args => ((EndpointStatusCount)args[0]!).EndpointId).ToArray());
    }

    [TestMethod]
    public async Task Changes_during_a_running_recount_are_broadcast_when_the_interval_ends()
    {
        var fixture = new Fixture();
        fixture.Store.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var leading = fixture.Broadcaster.RequestAsync(Endpoint);
        await fixture.Store.FirstRead.Task.WaitAsync(Guard);
        fixture.Store.FailedCount = 7;
        await fixture.Broadcaster.RequestAsync(Endpoint).WaitAsync(Guard);
        await fixture.Broadcaster.RequestAsync(Endpoint).WaitAsync(Guard);
        fixture.Store.Gate.SetResult();
        await leading.WaitAsync(Guard);
        Assert.AreEqual(1, fixture.Store.Reads(Endpoint));

        fixture.Time.Advance(Interval);
        await fixture.Broadcaster.CurrentBroadcast(Endpoint).WaitAsync(Guard);

        Assert.AreEqual(2, fixture.Store.Reads(Endpoint));
        Assert.AreEqual(2, fixture.Proxy.SentMethods.Count);
        Assert.AreEqual(7, LastStatus(fixture.Proxy).FailedCount);
    }

    [TestMethod]
    public async Task A_failing_recount_is_logged_and_the_next_interval_still_broadcasts()
    {
        var fixture = new Fixture();
        fixture.Store.Failure = new InvalidOperationException("store down");

        await fixture.Broadcaster.RequestAsync(Endpoint);
        Assert.AreEqual(0, fixture.Proxy.SentMethods.Count);

        fixture.Store.Failure = null;
        await fixture.Broadcaster.RequestAsync(Endpoint);
        fixture.Time.Advance(Interval);
        await fixture.Broadcaster.CurrentBroadcast(Endpoint).WaitAsync(Guard);

        Assert.AreEqual(2, fixture.Store.Reads(Endpoint));
        Assert.AreEqual(1, fixture.Proxy.SentMethods.Count);
    }

    [TestMethod]
    public async Task The_recount_refreshes_the_cached_endpoint_status_count()
    {
        var fixture = new Fixture();
        Assert.AreEqual(0, (await fixture.Cache.GetEndpointStateCountAsync(fixture.Store, Endpoint)).FailedCount);

        fixture.Store.FailedCount = 3;
        await fixture.Broadcaster.RequestAsync(Endpoint);

        // The status API and the Monitor read the same entry, so they see the new count too.
        Assert.AreEqual(3, (await fixture.Cache.GetEndpointStateCountAsync(fixture.Store, Endpoint)).FailedCount);
        Assert.AreEqual(2, fixture.Store.Reads(Endpoint));
    }

    [TestMethod]
    public async Task The_trailing_recount_does_not_reuse_a_read_that_began_before_the_last_write()
    {
        var fixture = new Fixture();
        var cache = new SignallingCache(fixture.Cache);
        using var broadcaster = new EndpointUpdateBroadcaster(
            fixture.Hub, fixture.Store, cache, fixture.Time, NullLogger<EndpointUpdateBroadcaster>.Instance, Interval);
        await broadcaster.RequestAsync(Endpoint);
        Assert.AreEqual(1, fixture.Store.Reads(Endpoint));

        // The status API or the Monitor starts a refresh that reads the count before the last write.
        fixture.Store.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<EndpointStateCount> older;
        try
        {
            older = fixture.Cache.RefreshAsync(
                EndpointStateCountCache.CacheKey(Endpoint),
                EndpointStateCountCache.Ttl,
                () => fixture.Store.DownloadEndpointStateCount(Endpoint));
            Assert.AreEqual(2, fixture.Store.Reads(Endpoint), "The older read has begun.");

            fixture.Store.FailedCount = 9;
            await broadcaster.RequestAsync(Endpoint);
            fixture.Time.Advance(Interval);

            // The trailing recount asks for its refresh while the older read is still open.
            await cache.Refreshed.Task.WaitAsync(Guard);
        }
        finally
        {
            fixture.Store.Gate.TrySetResult();
        }

        Assert.AreEqual(0, (await older.WaitAsync(Guard)).FailedCount);
        await broadcaster.CurrentBroadcast(Endpoint).WaitAsync(Guard);

        Assert.AreEqual(3, fixture.Store.Reads(Endpoint), "The trailing recount reads again after the older read.");
        Assert.AreEqual(9, LastStatus(fixture.Proxy).FailedCount, "The trailing broadcast carries the count after the last write.");
    }

    private static EndpointStatusCount LastStatus(RecordingClientProxy proxy) =>
        (EndpointStatusCount)proxy.SentArgs[^1][0]!;

    private sealed class Fixture
    {
        public Fixture()
        {
            Hub = new FakeHubContext(Proxy);
            Broadcaster = new EndpointUpdateBroadcaster(
                Hub, Store, Cache, Time, NullLogger<EndpointUpdateBroadcaster>.Instance, Interval);
        }

        public FakeTimeProvider Time { get; } = new();
        public RecordingClientProxy Proxy { get; } = new();
        public FakeHubContext Hub { get; }
        public CountingStore Store { get; } = new();
        public StoreResultCache Cache { get; } = new(new MemoryCache(new MemoryCacheOptions()));
        public EndpointUpdateBroadcaster Broadcaster { get; }

        public StorageHookImplementation Hook() => new(
            Hub,
            NullLogger<StorageHookImplementation>.Instance,
            Broadcaster,
            new FakePlatform("ep-1", "ep-2"),
            new HttpContextAccessor { HttpContext = new DefaultHttpContext() },
            new FakeHostEnvironment(Environments.Development),
            new ConfigurationBuilder().Build());
    }

    // Signals when a refresh is requested, so a test knows the trailing recount has asked for it.
    private sealed class SignallingCache(IStoreResultCache inner) : IStoreResultCache
    {
        public TaskCompletionSource Refreshed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<T> GetOrCreateAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory) => inner.GetOrCreateAsync(key, ttl, factory);

        public Task<T> RefreshAsync<T>(string key, TimeSpan ttl, Func<Task<T>> factory)
        {
            var refresh = inner.RefreshAsync(key, ttl, factory);
            Refreshed.TrySetResult();
            return refresh;
        }

        public void Remove(string key) => inner.Remove(key);
    }

    private sealed class CountingStore : InMemoryMessageStore
    {
        private readonly object _gate = new();
        private readonly Dictionary<string, int> _reads = new(StringComparer.Ordinal);

        public int FailedCount { get; set; }
        public Exception? Failure { get; set; }
        public TaskCompletionSource? Gate { get; set; }
        public TaskCompletionSource FirstRead { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Reads(string endpointId)
        {
            lock (_gate) return _reads.TryGetValue(endpointId, out var reads) ? reads : 0;
        }

        public override async Task<EndpointStateCount> DownloadEndpointStateCount(string endpointId)
        {
            lock (_gate) _reads[endpointId] = Reads(endpointId) + 1;
            // Read the state when the recount starts, as a store query would.
            var failed = FailedCount;
            FirstRead.TrySetResult();
            if (Gate is not null)
                await Gate.Task;
            if (Failure is not null)
                throw Failure;
            return new EndpointStateCount { EndpointId = endpointId, FailedCount = failed, EventTime = DateTime.UtcNow };
        }
    }
}
