#pragma warning disable CA1707, CA2007
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.MessageStore.HealthChecks;

namespace NimBus.MessageStore.CosmosDb.Tests;

/// <summary>
/// The health check caches the account round-trip (healthy and unhealthy alike)
/// for 30 seconds so frequent /ready probes don't each pay a ReadAccountAsync call.
/// </summary>
[TestClass]
public sealed class CosmosDbHealthCheckTests
{
    [TestMethod]
    public async Task Healthy_result_is_cached_within_the_cache_window()
    {
        var client = new CountingCosmosClient();
        var time = new ManualTimeProvider();
        var check = new CosmosDbHealthCheck(client, time);

        var first = await check.CheckHealthAsync(new HealthCheckContext());
        time.Advance(TimeSpan.FromSeconds(29));
        var second = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.AreEqual(HealthStatus.Healthy, first.Status);
        Assert.AreEqual(HealthStatus.Healthy, second.Status);
        Assert.AreEqual(1, client.ReadAccountCalls,
            "Probes inside the 30s window must reuse the cached result.");
    }

    [TestMethod]
    public async Task Cache_expires_after_the_window_and_refreshes()
    {
        var client = new CountingCosmosClient();
        var time = new ManualTimeProvider();
        var check = new CosmosDbHealthCheck(client, time);

        await check.CheckHealthAsync(new HealthCheckContext());
        time.Advance(TimeSpan.FromSeconds(31));
        await check.CheckHealthAsync(new HealthCheckContext());

        Assert.AreEqual(2, client.ReadAccountCalls,
            "A probe after the 30s window must refresh the account read.");
    }

    [TestMethod]
    public async Task Unhealthy_result_is_cached_too()
    {
        var client = new CountingCosmosClient { Failure = new InvalidOperationException("account unreachable") };
        var time = new ManualTimeProvider();
        var check = new CosmosDbHealthCheck(client, time);

        var first = await check.CheckHealthAsync(new HealthCheckContext());
        var second = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.AreEqual(HealthStatus.Unhealthy, first.Status);
        Assert.AreEqual(HealthStatus.Unhealthy, second.Status);
        Assert.AreEqual(1, client.ReadAccountCalls,
            "Unhealthy results must be cached as well — a flapping account must not be hammered by probes.");
    }

    [TestMethod]
    public async Task Unhealthy_cache_expires_and_recovers()
    {
        var client = new CountingCosmosClient { Failure = new InvalidOperationException("account unreachable") };
        var time = new ManualTimeProvider();
        var check = new CosmosDbHealthCheck(client, time);

        var first = await check.CheckHealthAsync(new HealthCheckContext());
        client.Failure = null!;
        time.Advance(TimeSpan.FromSeconds(31));
        var second = await check.CheckHealthAsync(new HealthCheckContext());

        Assert.AreEqual(HealthStatus.Unhealthy, first.Status);
        Assert.AreEqual(HealthStatus.Healthy, second.Status);
        Assert.AreEqual(2, client.ReadAccountCalls);
    }

    /// <summary>
    /// Uses the SDK's protected mocking constructor; ReadAccountAsync is virtual.
    /// AccountProperties has no public constructor, so it is materialized via
    /// Newtonsoft with non-public constructor handling.
    /// </summary>
    private sealed class CountingCosmosClient : CosmosClient
    {
        private int _readAccountCalls;

        public Exception? Failure { get; set; }

        public int ReadAccountCalls => Volatile.Read(ref _readAccountCalls);

        public override Task<AccountProperties> ReadAccountAsync()
        {
            Interlocked.Increment(ref _readAccountCalls);
            if (Failure is not null)
            {
                throw Failure;
            }

            var account = JsonConvert.DeserializeObject<AccountProperties>(
                "{\"id\":\"test-account\"}",
                new JsonSerializerSettings { ConstructorHandling = ConstructorHandling.AllowNonPublicDefaultConstructor });
            return Task.FromResult(account!);
        }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private long _nowTicks = DateTime.UnixEpoch.Ticks;

        public void Advance(TimeSpan by) => _nowTicks += by.Ticks;

        public override long GetTimestamp() => _nowTicks;

        // Custom TimeProvider default frequency is TicksPerSecond, which matches
        // the tick-based GetTimestamp above; stated explicitly for clarity.
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
    }
}
