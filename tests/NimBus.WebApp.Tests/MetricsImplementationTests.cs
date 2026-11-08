#pragma warning disable CA1707, CA2007

using System;
using System.Net;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.States;
using NimBus.Testing.Conformance;
using NimBus.WebApp.Controllers.ApiContract;
using NimBus.WebApp.ManagementApi;
using NimBus.WebApp.Services;

namespace NimBus.WebApp.Tests;

/// <summary>
/// The metrics window: a period preset ending now, or a custom from/to with a bucket size that
/// fits its span, passed to the store as exact UTC bounds.
/// </summary>
[TestClass]
public sealed class MetricsImplementationTests
{
    private static readonly DateTime Now = new(2026, 9, 25, 10, 17, 30, DateTimeKind.Utc);

    [TestMethod]
    [DataRow(Period._1h, 1, 16, "minute")]
    [DataRow(Period._1d, 24, 13, "hour")]
    [DataRow(Period._7d, 168, 13, "hour")]
    [DataRow(Period._30d, 720, 10, "day")]
    public void ResolveWindow_presets_end_now(Period period, int hours, int substringLength, string bucketLabel)
    {
        var window = MetricsImplementation.ResolveWindow(period, null, null, Now)!.Value;

        Assert.AreEqual(Now, window.To);
        Assert.AreEqual(Now.AddHours(-hours), window.From);
        Assert.AreEqual(substringLength, window.SubstringLength);
        Assert.AreEqual(bucketLabel, window.BucketLabel);
        Assert.AreEqual(period.ToString(), window.CacheKey, "Preset cache keys stay as they were.");
    }

    [TestMethod]
    [DataRow(1, 16, "minute")]
    [DataRow(2, 16, "minute")]
    [DataRow(3, 13, "hour")]
    [DataRow(14 * 24, 13, "hour")]
    [DataRow(15 * 24, 10, "day")]
    [DataRow(90 * 24, 10, "day")]
    public void ResolveWindow_custom_ranges_pick_a_bucket_for_their_span(int hours, int substringLength, string bucketLabel)
    {
        var from = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

        var window = MetricsImplementation.ResolveWindow(Period._1d, from, from.AddHours(hours), Now)!.Value;

        Assert.AreEqual(from, window.From);
        Assert.AreEqual(from.AddHours(hours), window.To);
        Assert.AreEqual(substringLength, window.SubstringLength);
        Assert.AreEqual(bucketLabel, window.BucketLabel);
    }

    [TestMethod]
    public void ResolveWindow_rejects_a_lone_inverted_or_oversized_custom_range()
    {
        var from = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);

        Assert.IsNull(MetricsImplementation.ResolveWindow(Period._1d, from, null, Now));
        Assert.IsNull(MetricsImplementation.ResolveWindow(Period._1d, null, from, Now));
        Assert.IsNull(MetricsImplementation.ResolveWindow(Period._1d, from, from, Now));
        Assert.IsNull(MetricsImplementation.ResolveWindow(Period._1d, from, from.AddDays(-1), Now));
        Assert.IsNull(MetricsImplementation.ResolveWindow(Period._1d, from, from.AddDays(90).AddMinutes(1), Now));
    }

    [TestMethod]
    public void ResolveWindow_reads_custom_bounds_as_utc_and_keys_the_cache_by_them()
    {
        var local = new DateTime(2026, 9, 1, 10, 0, 0, DateTimeKind.Local);
        var unspecified = new DateTime(2026, 9, 2, 10, 0, 0, DateTimeKind.Unspecified);

        var window = MetricsImplementation.ResolveWindow(Period._1d, local, unspecified, Now)!.Value;
        var other = MetricsImplementation.ResolveWindow(Period._1d, local, unspecified.AddHours(1), Now)!.Value;

        Assert.AreEqual(DateTimeKind.Utc, window.From.Kind);
        Assert.AreEqual(local.ToUniversalTime().Ticks, window.From.Ticks);
        Assert.AreEqual(DateTimeKind.Utc, window.To.Kind);
        Assert.AreEqual(unspecified.Ticks, window.To.Ticks, "An unspecified kind is taken as UTC.");
        Assert.AreNotEqual(window.CacheKey, other.CacheKey);
    }

    [TestMethod]
    public async Task A_custom_range_reaches_the_store_as_its_exact_bounds()
    {
        var store = new RecordingStore();
        var sut = FailedInsightsServiceTests.Metrics(store, NewCache(), new AllowAllAuthorizationService());
        var from = new DateTime(2026, 9, 20, 6, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 9, 21, 15, 30, 0, DateTimeKind.Utc);

        var result = await sut.GetMetricsTimeseriesAsync(Period._1d, from, to);

        Assert.AreEqual("hour", result.Value!.BucketSize);
        Assert.AreEqual((from, to, 13), store.TimeSeriesCall);
    }

    [TestMethod]
    public async Task An_invalid_custom_range_is_a_bad_request_without_a_store_query()
    {
        var store = new RecordingStore();
        var sut = FailedInsightsServiceTests.Metrics(store, NewCache(), new AllowAllAuthorizationService());
        var from = new DateTime(2026, 9, 20, 6, 0, 0, DateTimeKind.Utc);

        Assert.IsInstanceOfType<BadRequestObjectResult>((await sut.GetMetricsOverviewAsync(Period._1d, from, null)).Result);
        Assert.IsInstanceOfType<BadRequestObjectResult>((await sut.GetMetricsLatencyAsync(Period._1d, from, from)).Result);
        Assert.IsInstanceOfType<BadRequestObjectResult>((await sut.GetMetricsFailedInsightsAsync(Period._1d, null, from)).Result);
        Assert.IsInstanceOfType<BadRequestObjectResult>((await sut.GetMetricsTimeseriesAsync(Period._1d, from, from.AddDays(91))).Result);
        Assert.IsInstanceOfType<BadRequestObjectResult>((await sut.GetMetricsTimeseriesByEventtypeAsync(Period._1d, from, null)).Result);
        Assert.IsNull(store.TimeSeriesCall);
    }

    [TestMethod]
    public async Task The_query_string_binds_iso_utc_bounds_to_the_same_instants()
    {
        var store = new RecordingStore();
        using var host = await CreateHost(FailedInsightsServiceTests.Metrics(store, NewCache(), new AllowAllAuthorizationService()));
        using var client = host.GetTestServer().CreateClient();

        var ok = await client.GetAsync("/api/metrics/timeseries?period=1d&from=2026-09-20T06:00:00.000Z&to=2026-09-21T15:30:00.000Z");
        var lone = await client.GetAsync("/api/metrics/timeseries?period=1d&from=2026-09-20T06:00:00.000Z");

        Assert.AreEqual(HttpStatusCode.OK, ok.StatusCode);
        Assert.AreEqual(new DateTime(2026, 9, 20, 6, 0, 0, DateTimeKind.Utc), store.TimeSeriesCall!.Value.From);
        Assert.AreEqual(new DateTime(2026, 9, 21, 15, 30, 0, DateTimeKind.Utc), store.TimeSeriesCall!.Value.To);
        Assert.AreEqual(DateTimeKind.Utc, store.TimeSeriesCall!.Value.From.Kind);
        Assert.AreEqual(HttpStatusCode.BadRequest, lone.StatusCode);
    }

    private static async Task<IHost> CreateHost(IMetricsApiController implementation)
    {
        var builder = new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddSingleton(implementation);
                    services.AddControllers()
                        .AddApplicationPart(typeof(MetricsApiController).Assembly)
                        .AddJsonOptions(options =>
                            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints => endpoints.MapControllers());
                });
            });

        return await builder.StartAsync();
    }

    private static StoreResultCache NewCache() =>
        new StoreResultCache(new MemoryCache(new MemoryCacheOptions()));

    private sealed class RecordingStore : InMemoryMessageStore
    {
        public (DateTime From, DateTime To, int SubstringLength)? TimeSeriesCall { get; private set; }

        public override Task<TimeSeriesResult> GetTimeSeriesMetrics(DateTime from, DateTime to, int substringLength, string bucketLabel)
        {
            TimeSeriesCall = (from, to, substringLength);
            return base.GetTimeSeriesMetrics(from, to, substringLength, bucketLabel);
        }
    }

    private sealed class AllowAllAuthorizationService : IEndpointAuthorizationService
    {
        public Task<bool> HasRoleAsync(AccessRole required, string? endpointId = null) => Task.FromResult(true);

        public Task<bool> CanReadPiiAsync() => Task.FromResult(true);

        public Task<CurrentUserAccess> GetCurrentUserAccessAsync() => Task.FromResult(new CurrentUserAccess
        {
            SiteRole = AccessRole.Owner,
            IsPiiReader = true,
        });

        public string GetCurrentUserName() => "test-user";
    }
}
