#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Extensions;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.SqlServer.Tests;

/// <summary>Spec 050 §5.4: the SQL Server retention cleanup and the periods it applies.</summary>
[TestClass]
public sealed class SqlServerRetentionTests
{
    private const string ConnectionString = "Server=localhost;Database=NimBus;User Id=sa;Password=not-a-real-password;TrustServerCertificate=True";

    /// <summary>Records every purge call and deletes a scripted number of rows per period.</summary>
    private sealed class ScriptedStore : InMemoryMessageStore, IMessageTrackingStore
    {
        public Queue<RetentionPurgeResult> Batches { get; } = new();
        public List<RetentionPeriods> Calls { get; } = new();
        public Exception? Failure { get; set; }

        Task<RetentionPurgeResult> IMessageTrackingStore.PurgeExpiredData(RetentionPeriods periods, DateTime nowUtc, int batchSize, CancellationToken cancellationToken)
        {
            Calls.Add(periods);
            if (Failure is not null)
                return Task.FromException<RetentionPurgeResult>(Failure);
            return Task.FromResult(Batches.Count > 0 ? Batches.Dequeue() : RetentionPurgeResult.None);
        }
    }

    private static SqlServerRetentionService Service(IMessageTrackingStore store, RetentionPolicy policy, SqlServerSchemaReadiness? readiness = null) =>
        new(Options.Create(new SqlServerMessageStoreOptions { ConnectionString = ConnectionString }),
            readiness ?? new SqlServerSchemaReadiness(), store, policy, NullLogger<SqlServerRetentionService>.Instance);

    private static RetentionPolicy Policy(params (string Key, string Value)[] pins) =>
        new(RetentionPeriods.AllUnlimited, key => pins.FirstOrDefault(p => p.Key == key).Value);

    [TestMethod]
    public async Task Run_with_every_period_unlimited_deletes_nothing()
    {
        var store = new ScriptedStore();

        var result = await Service(store, Policy()).RunOnceAsync(CancellationToken.None);

        Assert.AreEqual(RetentionPurgeResult.None, result);
        Assert.IsEmpty(store.Calls);
    }

    [TestMethod]
    public async Task Run_repeats_each_period_until_its_batch_comes_back_short()
    {
        var store = new ScriptedStore();
        const int full = SqlServerRetentionService.BatchSize;
        store.Batches.Enqueue(new RetentionPurgeResult(full, 3, full, 0));
        store.Batches.Enqueue(new RetentionPurgeResult(2, 0, full, 0));
        store.Batches.Enqueue(new RetentionPurgeResult(0, 0, 1, 0));

        var result = await Service(store, Policy(("OpenEventsDays", "30"), ("ResolvedEventsDays", "30"), ("MessageHistoryDays", "90"), ("AuditTrailDays", "365")))
            .RunOnceAsync(CancellationToken.None);

        Assert.AreEqual(new RetentionPurgeResult(full + 2, 3, 2 * full + 1, 0), result);
        Assert.HasCount(3, store.Calls);
        // A period that came back short is not asked again.
        Assert.AreEqual(new RetentionPeriods(30, 30, 90, 365), store.Calls[0]);
        Assert.AreEqual(new RetentionPeriods(30, RetentionPeriods.Unlimited, 90, RetentionPeriods.Unlimited), store.Calls[1]);
        Assert.AreEqual(new RetentionPeriods(RetentionPeriods.Unlimited, RetentionPeriods.Unlimited, 90, RetentionPeriods.Unlimited), store.Calls[2]);
    }

    [TestMethod]
    public async Task Completed_and_skipped_rows_fall_under_the_resolved_period()
    {
        var store = new InMemoryMessageStore();
        var row = new UnresolvedEvent { EventId = "e1", SessionId = "s1", EndpointId = "ep", EnqueuedTimeUtc = DateTime.UtcNow };
        await store.UploadCompletedMessage("e1", "s1", "ep", row);
        await store.UploadSkippedMessage("e2", "s1", "ep", new UnresolvedEvent { EventId = "e2", SessionId = "s1", EndpointId = "ep", EnqueuedTimeUtc = DateTime.UtcNow });
        var openOnly = new RetentionPeriods(1, RetentionPeriods.Unlimited, RetentionPeriods.Unlimited, RetentionPeriods.Unlimited);
        var resolvedOnly = new RetentionPeriods(RetentionPeriods.Unlimited, 1, RetentionPeriods.Unlimited, RetentionPeriods.Unlimited);
        var later = DateTime.UtcNow.AddDays(2);

        Assert.AreEqual(RetentionPurgeResult.None, await SqlServerRetentionService.PurgeAsync(store, openOnly, later, 100, CancellationToken.None));
        Assert.AreEqual(2, (await SqlServerRetentionService.PurgeAsync(store, resolvedOnly, later, 100, CancellationToken.None)).ResolvedEvents);
    }

    [TestMethod]
    public async Task A_failed_run_is_logged_and_the_next_run_still_happens()
    {
        var store = new ScriptedStore { Failure = new InvalidOperationException("database down") };
        var readiness = new SqlServerSchemaReadiness();
        readiness.MarkReady();
        var options = Options.Create(new SqlServerMessageStoreOptions { ConnectionString = ConnectionString, RetentionInterval = TimeSpan.FromMilliseconds(50) });
        using var service = new SqlServerRetentionService(options, readiness, store, Policy(("OpenEventsDays", "30")), NullLogger<SqlServerRetentionService>.Instance);

        await service.StartAsync(CancellationToken.None);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (store.Calls.Count < 2 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await service.StopAsync(CancellationToken.None);

        Assert.IsGreaterThanOrEqualTo(2, store.Calls.Count, "The cleanup stopped after a failed run.");
    }

    [TestMethod]
    public async Task The_cleanup_waits_for_the_schema()
    {
        var store = new ScriptedStore();
        var readiness = new SqlServerSchemaReadiness();
        var options = Options.Create(new SqlServerMessageStoreOptions { ConnectionString = ConnectionString, RetentionInterval = TimeSpan.FromMilliseconds(20) });
        using var service = new SqlServerRetentionService(options, readiness, store, Policy(("OpenEventsDays", "30")), NullLogger<SqlServerRetentionService>.Instance);

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(200);
        Assert.IsEmpty(store.Calls);

        readiness.MarkReady();
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (store.Calls.Count == 0 && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        await service.StopAsync(CancellationToken.None);

        Assert.IsNotEmpty(store.Calls);
    }

    [TestMethod]
    [DataRow("OpenEventsDays", "abc")]
    [DataRow("ResolvedEventsDays", "0")]
    [DataRow("AuditTrailDays", "10")]
    [DataRow("MessageHistoryDays", "4000")]
    public void An_invalid_pin_fails_with_the_key(string key, string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() => Policy((key, value)));

        StringAssert.Contains(error.Message, key);
    }

    [TestMethod]
    public void A_bounded_audit_trail_must_outlive_the_other_periods()
    {
        Assert.Throws<InvalidOperationException>(() => Policy(("MessageHistoryDays", "90"), ("AuditTrailDays", "30")));

        // Unlimited counts only against bounded values.
        var policy = Policy(("AuditTrailDays", "365"), ("OpenEventsDays", "unlimited"), ("ResolvedEventsDays", "-1"));
        Assert.AreEqual(new RetentionPeriods(RetentionPeriods.Unlimited, RetentionPeriods.Unlimited, RetentionPeriods.Unlimited, 365), policy.Current);
    }

    [TestMethod]
    public void Sql_registration_reads_pins_and_keeps_everything_by_default()
    {
        Assert.IsTrue(Resolve(new Dictionary<string, string?>()).GetRequiredService<RetentionPolicy>().Current.IsAllUnlimited);

        var pinned = Resolve(new Dictionary<string, string?> { ["NimBus:Retention:ResolvedEventsDays"] = "30" })
            .GetRequiredService<RetentionPolicy>().Current;
        Assert.AreEqual(30, pinned.ResolvedEventsDays);
        Assert.AreEqual(RetentionPeriods.Unlimited, pinned.OpenEventsDays);
    }

    [TestMethod]
    public void Sql_registration_binds_the_retention_interval()
    {
        var options = Resolve(new Dictionary<string, string?> { ["NimBus:SqlServer:RetentionInterval"] = "00:15:00" })
            .GetRequiredService<IOptions<SqlServerMessageStoreOptions>>().Value;

        Assert.AreEqual(TimeSpan.FromMinutes(15), options.RetentionInterval);
    }

    [TestMethod]
    public void Registering_the_cleanup_twice_gives_one_job_and_none_without_it()
    {
        static int Jobs(IServiceProvider provider) =>
            provider.GetServices<IHostedService>().OfType<SqlServerRetentionService>().Count();

        Assert.AreEqual(0, Jobs(Resolve(new Dictionary<string, string?>())));
        Assert.AreEqual(1, Jobs(Resolve(new Dictionary<string, string?>(), retentionCalls: 2)));
    }

    private static ServiceProvider Resolve(Dictionary<string, string?> settings, int retentionCalls = 0)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(settings).Build());
        var builder = new NimBusBuilder(services);
        builder.AddSqlServerMessageStore(options => options.ConnectionString = ConnectionString);
        for (var i = 0; i < retentionCalls; i++)
            builder.AddSqlServerMessageStoreRetention();
        return services.BuildServiceProvider();
    }
}
