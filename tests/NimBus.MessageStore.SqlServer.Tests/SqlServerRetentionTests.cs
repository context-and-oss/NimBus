#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.SqlServer;

namespace NimBus.MessageStore.SqlServer.Tests;

/// <summary>
/// Live retention rules (GH#291) against <c>NIMBUS_SQL_TEST_CONNECTION</c>, in an isolated
/// schema. Rows are seeded by raw SQL so their ages can be set directly.
/// </summary>
[TestClass]
public sealed class SqlServerRetentionTests
{
    private static readonly string[] AllStatuses = { "Pending", "Failed", "DeadLettered", "Completed", "Skipped" };

    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerRetentionTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerRetentionTests));

    [TestMethod]
    public async Task Messages_older_than_90_days_are_deleted_and_newer_kept()
    {
        await SeedMessage("old", ageDays: 91);
        await SeedMessage("young", ageDays: 89);

        var result = await RunOnce();

        CollectionAssert.AreEquivalent(new[] { "young" }, await Ids("Messages", "EventId"));
        Assert.AreEqual(1, result.MessagesDeleted);
    }

    [TestMethod]
    public async Task Audits_older_than_365_days_are_deleted_and_newer_kept()
    {
        await SeedAudit("old", ageDays: 366);
        await SeedAudit("young", ageDays: 364);

        var result = await RunOnce();

        CollectionAssert.AreEquivalent(new[] { "young" }, await Ids("MessageAudits", "EventId"));
        Assert.AreEqual(1, result.AuditsDeleted);
    }

    [TestMethod]
    public async Task Archived_tracking_rows_older_than_30_days_are_deleted_and_newer_kept()
    {
        await SeedTracking("old", "Failed", deleted: true, ageDays: 31);
        await SeedTracking("young", "Failed", deleted: true, ageDays: 29);

        var result = await RunOnce();

        CollectionAssert.AreEquivalent(new[] { "young" }, await Ids("UnresolvedEvents", "EventId"));
        Assert.AreEqual(1, result.ArchivedTrackingDeleted);
    }

    [TestMethod]
    public async Task Non_archived_tracking_rows_of_any_status_survive_by_default()
    {
        foreach (var status in AllStatuses)
            await SeedTracking($"old-{status}", status, deleted: false, ageDays: 400);

        var result = await RunOnce();

        Assert.AreEqual(AllStatuses.Length, (await Ids("UnresolvedEvents", "EventId")).Count);
        Assert.AreEqual(0, result.UnresolvedTrackingDeleted);
    }

    [TestMethod]
    public async Task Non_archived_tracking_rows_older_than_configured_days_are_deleted()
    {
        await SeedTracking("old-failed", "Failed", deleted: false, ageDays: 31);
        await SeedTracking("old-completed", "Completed", deleted: false, ageDays: 31);
        await SeedTracking("young-failed", "Failed", deleted: false, ageDays: 29);
        await SeedTracking("young-completed", "Completed", deleted: false, ageDays: 29);

        var result = await RunOnce(o => o.UnresolvedRetentionDays = 30);

        CollectionAssert.AreEquivalent(
            new[] { "young-failed", "young-completed" },
            await Ids("UnresolvedEvents", "EventId"));
        Assert.AreEqual(2, result.UnresolvedTrackingDeleted);
    }

    [TestMethod]
    public async Task Disabled_retention_deletes_nothing()
    {
        await SeedMessage("m", ageDays: 400);
        await SeedAudit("a", ageDays: 400);
        await SeedTracking("archived", "Failed", deleted: true, ageDays: 400);
        await SeedTracking("live", "Failed", deleted: false, ageDays: 400);

        var logger = new CapturingLogger<SqlServerRetentionService>();
        var service = CreateService(logger, o =>
        {
            o.RetentionEnabled = false;
            o.UnresolvedRetentionDays = 1;
            o.RetentionInterval = TimeSpan.FromMilliseconds(50);
        });

        await service.StartAsync(CancellationToken.None);
        await Task.Delay(500);
        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, (await Ids("Messages", "EventId")).Count);
        Assert.AreEqual(1, (await Ids("MessageAudits", "EventId")).Count);
        Assert.AreEqual(2, (await Ids("UnresolvedEvents", "EventId")).Count);
        Assert.IsFalse(logger.Entries.Any(e => e.Message.Contains("retention run completed", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task More_than_one_batch_of_matching_rows_is_deleted_in_full()
    {
        await using (var conn = await Open())
        {
            await using var cmd = new SqlCommand($@"
INSERT INTO {T("Messages")} (EventId, MessageId, EndpointId, EnqueuedTimeUtc, MessageContentJson, CreatedAtUtc)
SELECT TOP (5001) CONCAT('bulk-', ROW_NUMBER() OVER (ORDER BY (SELECT NULL))), 'm', 'ep',
       DATEADD(day, -100, SYSUTCDATETIME()), '{{}}', DATEADD(day, -100, SYSUTCDATETIME())
FROM sys.all_objects a CROSS JOIN sys.all_objects b;", conn);
            Assert.AreEqual(5001, await cmd.ExecuteNonQueryAsync());
        }

        var result = await RunOnce();

        Assert.AreEqual(0, (await Ids("Messages", "EventId")).Count);
        Assert.AreEqual(5001, result.MessagesDeleted);
    }

    [TestMethod]
    public async Task Completed_run_logs_counts_per_rule_at_information()
    {
        await SeedMessage("m", ageDays: 91);
        await SeedAudit("a1", ageDays: 366);
        await SeedAudit("a2", ageDays: 366);
        await SeedTracking("archived", "Failed", deleted: true, ageDays: 31);
        await SeedTracking("live1", "Failed", deleted: false, ageDays: 31);
        await SeedTracking("live2", "Completed", deleted: false, ageDays: 31);
        await SeedTracking("live3", "Skipped", deleted: false, ageDays: 31);

        var logger = new CapturingLogger<SqlServerRetentionService>();
        var service = CreateService(logger, o => o.UnresolvedRetentionDays = 30);

        await service.StartAsync(CancellationToken.None);
        var entry = await logger.WaitFor(e => e.Level == LogLevel.Information && e.State.ContainsKey("MessagesDeleted"));
        await service.StopAsync(CancellationToken.None);

        Assert.AreEqual(1, entry.State["MessagesDeleted"]);
        Assert.AreEqual(2, entry.State["AuditsDeleted"]);
        Assert.AreEqual(1, entry.State["ArchivedTrackingDeleted"]);
        Assert.AreEqual(3, entry.State["UnresolvedTrackingDeleted"]);
        Assert.IsTrue(entry.State.ContainsKey("DurationMs"));
    }

    [TestMethod]
    public async Task Failing_run_is_logged_at_error_and_the_next_run_still_executes()
    {
        var logger = new CapturingLogger<SqlServerRetentionService>();
        var options = new SqlServerMessageStoreOptions
        {
            ConnectionString = SqlServerStoreTestHarness.GetConnectionString(),
            Schema = "nimbus_missing_" + Guid.NewGuid().ToString("N")[..8],
            RetentionInterval = TimeSpan.FromMilliseconds(50),
        };
        var service = new SqlServerRetentionService(Options.Create(options), ReadySignal(), logger);

        await service.StartAsync(CancellationToken.None);
        await logger.WaitFor(_ => logger.Entries.Count(e => e.Level == LogLevel.Error) >= 2);
        await service.StopAsync(CancellationToken.None);

        Assert.IsTrue(logger.Entries.Count(e => e.Level == LogLevel.Error) >= 2);
    }

    [TestMethod]
    public async Task Other_tables_are_untouched()
    {
        await using (var conn = await Open())
        {
            await using var cmd = new SqlCommand($@"
INSERT INTO {T("EndpointMetadata")} (EndpointId, UpdatedAtUtc) VALUES ('ep-old', DATEADD(day, -400, SYSUTCDATETIME()));", conn);
            await cmd.ExecuteNonQueryAsync();
        }

        await RunOnce(o => o.UnresolvedRetentionDays = 1);

        Assert.AreEqual(1, (await Ids("EndpointMetadata", "EndpointId")).Count);
    }

    private static Task<SqlServerRetentionResult> RunOnce(Action<SqlServerMessageStoreOptions>? configure = null)
        => new SqlServerRetentionRunner(BuildOptions(configure)).RunOnceAsync(CancellationToken.None);

    private static SqlServerRetentionService CreateService(
        ILogger<SqlServerRetentionService> logger, Action<SqlServerMessageStoreOptions> configure)
        => new(Options.Create(BuildOptions(configure)), ReadySignal(), logger);

    private static SqlServerSchemaReadiness ReadySignal()
    {
        var readiness = new SqlServerSchemaReadiness();
        readiness.MarkReady();
        return readiness;
    }

    private static SqlServerMessageStoreOptions BuildOptions(Action<SqlServerMessageStoreOptions>? configure)
    {
        var options = new SqlServerMessageStoreOptions
        {
            ConnectionString = SqlServerStoreTestHarness.GetConnectionString(),
            Schema = Schema,
        };
        configure?.Invoke(options);
        return options;
    }

    private static string Schema => SqlServerStoreTestHarness.GetSchema(typeof(SqlServerRetentionTests));

    private static string T(string table) => $"[{Schema}].[{table}]";

    private static async Task<SqlConnection> Open()
    {
        var conn = new SqlConnection(SqlServerStoreTestHarness.GetConnectionString());
        await conn.OpenAsync();
        return conn;
    }

    private static async Task SeedMessage(string eventId, int ageDays)
    {
        await using var conn = await Open();
        await using var cmd = new SqlCommand($@"
INSERT INTO {T("Messages")} (EventId, MessageId, EndpointId, EnqueuedTimeUtc, MessageContentJson, CreatedAtUtc)
VALUES (@EventId, 'm', 'ep', DATEADD(day, -@Age, SYSUTCDATETIME()), '{{}}', DATEADD(day, -@Age, SYSUTCDATETIME()));", conn);
        cmd.Parameters.AddWithValue("@EventId", eventId);
        cmd.Parameters.AddWithValue("@Age", ageDays);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedAudit(string eventId, int ageDays)
    {
        await using var conn = await Open();
        await using var cmd = new SqlCommand($@"
INSERT INTO {T("MessageAudits")} (EventId, AuditTimestamp, AuditType, CreatedAtUtc)
VALUES (@EventId, DATEADD(day, -@Age, SYSUTCDATETIME()), 'Resubmit', DATEADD(day, -@Age, SYSUTCDATETIME()));", conn);
        cmd.Parameters.AddWithValue("@EventId", eventId);
        cmd.Parameters.AddWithValue("@Age", ageDays);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task SeedTracking(string eventId, string status, bool deleted, int ageDays)
    {
        await using var conn = await Open();
        await using var cmd = new SqlCommand($@"
INSERT INTO {T("UnresolvedEvents")} (EventId, SessionId, EndpointId, Status, UpdatedAtUtc, EnqueuedTimeUtc, Deleted)
VALUES (@EventId, 's', 'ep', @Status, DATEADD(day, -@Age, SYSUTCDATETIME()), DATEADD(day, -@Age, SYSUTCDATETIME()), @Deleted);", conn);
        cmd.Parameters.AddWithValue("@EventId", eventId);
        cmd.Parameters.AddWithValue("@Status", status);
        cmd.Parameters.AddWithValue("@Deleted", deleted);
        cmd.Parameters.AddWithValue("@Age", ageDays);
        await cmd.ExecuteNonQueryAsync();
    }

    private static async Task<List<string>> Ids(string table, string column)
    {
        await using var conn = await Open();
        await using var cmd = new SqlCommand($"SELECT [{column}] FROM {T(table)}", conn);
        await using var reader = await cmd.ExecuteReaderAsync();
        var ids = new List<string>();
        while (await reader.ReadAsync())
            ids.Add(reader.GetString(0));
        return ids;
    }

    internal sealed record LogEntry(LogLevel Level, string Message, IReadOnlyDictionary<string, object?> State);

    internal sealed class CapturingLogger<TCategory> : ILogger<TCategory>
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? pairs.ToDictionary(p => p.Key, p => p.Value)
                : new Dictionary<string, object?>();
            Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), values));
        }

        public async Task<LogEntry> WaitFor(Func<LogEntry, bool> predicate)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (DateTime.UtcNow < deadline)
            {
                var match = Entries.FirstOrDefault(predicate);
                if (match is not null)
                    return match;
                await Task.Delay(25);
            }

            Assert.Fail("Expected log entry was not written within 30 seconds.");
            return null!;
        }
    }
}
