#pragma warning disable CA1707, CA2007
using System.Threading.Tasks;
using System;
using System.Linq;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.MessageStore.Abstractions;
using NimBus.Testing.Conformance;

namespace NimBus.MessageStore.SqlServer.Tests;

/// <summary>
/// SQL Server conformance run. Skipped automatically when no
/// <c>NIMBUS_SQL_TEST_CONNECTION</c> env var is set, so contributors without a
/// running SQL Server can still run the rest of the suite. CI sets the env var
/// to point at the Linux SQL Server service container.
/// </summary>
[TestClass]
public sealed class SqlServerMessageTrackingStoreTests : MessageTrackingStoreConformanceTests
{
    [ClassInitialize]
    public static Task ClassInit(TestContext context)
        => SqlServerStoreTestHarness.InitializeAsync(typeof(SqlServerMessageTrackingStoreTests));

    [TestInitialize]
    public Task ResetSchema()
        => SqlServerStoreTestHarness.ResetAsync(typeof(SqlServerMessageTrackingStoreTests));

    protected override IMessageTrackingStore CreateStore()
        => SqlServerStoreTestHarness.CreateStore(typeof(SqlServerMessageTrackingStoreTests));

    [TestMethod]
    public async Task TrySkipDeferredMessage_preserves_datetime2_version_precision()
    {
        var store = CreateStore();
        await store.UploadDeferredMessage("precision-event", "session", "endpoint", new UnresolvedEvent
        {
            EventId = "precision-event", SessionId = "session", EndpointId = "endpoint",
            LastMessageId = "deferral", EnqueuedTimeUtc = DateTime.UtcNow,
        });
        await using var connection = new SqlConnection(SqlServerStoreTestHarness.GetConnectionString());
        await connection.OpenAsync();
        var schema = SqlServerStoreTestHarness.GetSchema(typeof(SqlServerMessageTrackingStoreTests));
        await using var command = new SqlCommand($"UPDATE [{schema}].[UnresolvedEvents] SET UpdatedAtUtc = CAST('2026-09-23T00:00:00.1234567' AS datetime2) WHERE EventId = @EventId", connection);
        command.Parameters.AddWithValue("@EventId", "precision-event");
        await command.ExecuteNonQueryAsync();
        var inspected = await store.GetEvent("endpoint", "precision-event");
        Assert.IsTrue(await store.TrySkipDeferredMessage("precision-event", "session", "endpoint", "deferral", inspected!.UpdatedAt));
    }

    [TestMethod]
    public async Task Status_writes_stamp_UpdatedAt_with_datetime2_precision()
    {
        // A DateTime parameter without DbType.DateTime2 is sent as legacy datetime, which
        // rounds to 1/300 s, so every stored stamp lands on that grid (sub-millisecond ticks of
        // 0, 3333 or 6667). Writes less than ~3 ms apart then tie on UpdatedAtUtc and "latest
        // row" queries such as GetEvent pick arbitrarily. A datetime2 stamp lands on the grid
        // with ~5e-4 probability per row, so across several rows at least one must be off it.
        var store = CreateStore();
        var offGrid = 0;
        for (var i = 0; i < 5; i++)
        {
            var eventId = $"stamp-precision-{i}";
            await store.UploadPendingMessage(eventId, "session", "endpoint", new UnresolvedEvent
            {
                EventId = eventId, SessionId = "session", EndpointId = "endpoint", EnqueuedTimeUtc = DateTime.UtcNow,
            });
            var stored = await store.GetEvent("endpoint", eventId);
            var subMillisecond = stored!.UpdatedAt.Ticks % TimeSpan.TicksPerMillisecond;
            if (subMillisecond is not (0 or 3333 or 3334 or 6666 or 6667)) offGrid++;
        }

        Assert.AreNotEqual(0, offGrid, "UpdatedAtUtc was rounded to SQL datetime precision.");
    }

    [TestMethod]
    public async Task Whole_endpoint_purges_work_through_more_than_one_batch()
    {
        // Delete all events purges in batches so it finishes on a small database; an endpoint
        // with more rows than one batch must still be purged completely, and only that endpoint.
        var store = CreateStore();
        var rowCount = (SqlServerMessageTrackingStore.PurgeBatchSize * 2) + 1;
        var t = DateTime.UtcNow;
        for (var start = 0; start < rowCount; start += 50)
        {
            await Task.WhenAll(Enumerable.Range(start, Math.Min(50, rowCount - start)).Select(async i =>
            {
                var eventId = $"batch-event-{i}";
                await store.UploadFailedMessage(eventId, $"session-{i}", "ep-batch", new UnresolvedEvent
                {
                    EventId = eventId, SessionId = $"session-{i}", EndpointId = "ep-batch", EnqueuedTimeUtc = t,
                });
                await store.StoreMessage(new MessageEntity
                {
                    EventId = eventId, MessageId = $"batch-message-{i}", EndpointId = "ep-batch",
                    EnqueuedTimeUtc = t, MessageContent = new MessageContent(),
                });
            }));
        }

        await store.UploadFailedMessage("kept-event", "kept-session", "ep-kept", new UnresolvedEvent
        {
            EventId = "kept-event", SessionId = "kept-session", EndpointId = "ep-kept", EnqueuedTimeUtc = t,
        });
        await store.StoreMessage(new MessageEntity
        {
            EventId = "kept-event", MessageId = "kept-message", EndpointId = "ep-kept",
            EnqueuedTimeUtc = t, MessageContent = new MessageContent(),
        });

        Assert.IsTrue(await store.PurgeMessages("ep-batch"));
        Assert.AreEqual(rowCount, await store.PurgeStoredMessages("ep-batch"));

        Assert.AreEqual(0, await CountAsync("UnresolvedEvents", "ep-batch", "AND Deleted = 0"));
        Assert.AreEqual(0, await CountAsync("Messages", "ep-batch"));
        Assert.AreEqual(1, await CountAsync("UnresolvedEvents", "ep-kept", "AND Deleted = 0"));
        Assert.AreEqual(1, await CountAsync("Messages", "ep-kept"));
        Assert.IsFalse(await store.PurgeMessages("ep-batch"), "A second purge has nothing left to mark deleted.");
        Assert.AreEqual(0, await store.PurgeStoredMessages("ep-batch"));
    }

    private static async Task<int> CountAsync(string table, string endpointId, string extraFilter = "")
    {
        await using var connection = new SqlConnection(SqlServerStoreTestHarness.GetConnectionString());
        await connection.OpenAsync();
        var schema = SqlServerStoreTestHarness.GetSchema(typeof(SqlServerMessageTrackingStoreTests));
        await using var command = new SqlCommand(
            $"SELECT COUNT(*) FROM [{schema}].[{table}] WHERE EndpointId = @EndpointId {extraFilter}", connection);
        command.Parameters.AddWithValue("@EndpointId", endpointId);
        return (int)(await command.ExecuteScalarAsync())!;
    }
}
