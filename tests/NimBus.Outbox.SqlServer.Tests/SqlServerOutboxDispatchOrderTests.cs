#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;
using NimBus.Outbox.SqlServer;

namespace NimBus.Outbox.SqlServer.Tests;

/// <summary>
/// Env-gated tests (set <c>NIMBUS_SQL_TEST_CONNECTION</c> to run; otherwise inconclusive) for
/// the order in which the SQL Server outbox hands rows to the dispatcher. Per-session FIFO
/// downstream depends on it, so it must follow the order rows were stored, not
/// <see cref="OutboxMessage.CreatedAtUtc"/>: rows written together share that value, and it
/// comes from each publisher's own clock. Also covers upgrading a table created before rows
/// stored their order.
/// </summary>
[TestClass]
public sealed class SqlServerOutboxDispatchOrderTests
{
    private const string DispatchOrderIndex = "IX_OutboxMessages_DispatchOrder(SequenceNumber,CreatedAtUtc) WHERE ([DispatchedAtUtc] IS NULL)";
    private const string PendingSessionIndex = "IX_OutboxMessages_PendingSession(SessionId) WHERE ([DispatchedAtUtc] IS NULL)";

    private SqlServerOutboxOptions _options = null!;

    [TestMethod]
    public async Task DispatchPendingAsync_sends_each_sessions_messages_in_publish_order()
    {
        var outbox = await CreateOutboxAsync();
        // CustomerCreated then OrderCreated per customer, published as one batch. OutboxSender
        // stamps every row within microseconds of the others and SqlClient sends the stamp as
        // DATETIME (1/300 s), so the stored CreatedAtUtc values tie and only the store order
        // keeps each customer's pair in sequence.
        var published = Enumerable.Range(0, 10)
            .SelectMany(customer => new[]
            {
                CreateMessage($"customer-{customer}", "CustomerCreated"),
                CreateMessage($"customer-{customer}", "OrderCreated"),
            })
            .ToList();
        await new OutboxSender(outbox).Send(published);

        var sender = new RecordingSender();
        var dispatched = await new OutboxDispatcher(outbox, sender).DispatchPendingAsync(batchSize: 100);

        Assert.AreEqual(published.Count, dispatched);
        CollectionAssert.AreEqual(
            published.Select(message => message.MessageId).ToArray(),
            sender.Sent.Select(message => message.MessageId).ToArray());
    }

    [TestMethod]
    public async Task GetPendingAsync_keeps_store_order_when_a_later_row_has_an_earlier_CreatedAtUtc()
    {
        var outbox = await CreateOutboxAsync();
        // The second row comes from a publisher whose clock runs behind the first one's (or
        // from a host whose clock stepped back). It was still stored second.
        var first = CreateRow("clock-first", new DateTime(2030, 1, 1, 0, 0, 5, DateTimeKind.Utc));
        var second = CreateRow("clock-second", new DateTime(2030, 1, 1, 0, 0, 1, DateTimeKind.Utc));
        await outbox.StoreAsync(first);
        await outbox.StoreAsync(second);

        CollectionAssert.AreEqual(
            new[] { first.Id, second.Id },
            (await outbox.GetPendingAsync(10)).Select(message => message.Id).ToArray());
    }

    [TestMethod]
    public async Task EnsureTableExistsAsync_creates_only_the_dispatch_order_and_pending_session_indexes()
    {
        var outbox = await CreateOutboxAsync();
        await outbox.EnsureTableExistsAsync();

        CollectionAssert.AreEqual(new[] { DispatchOrderIndex, PendingSessionIndex }, await GetNonclusteredIndexesAsync());
    }

    [TestMethod]
    public async Task EnsureTableExistsAsync_upgrades_a_table_created_before_rows_stored_their_order()
    {
        var options = CreateOptions();
        await ExecuteAsync(options, $"CREATE SCHEMA [{options.Schema}];");
        // The table and index exactly as EnsureTableExistsAsync created them before the fix,
        // with two rows still pending from the previous version.
        await ExecuteAsync(options, $@"
            CREATE TABLE {FullTableName(options)} (
                [Id]                  NVARCHAR(128) NOT NULL PRIMARY KEY,
                [MessageId]           NVARCHAR(512) NOT NULL,
                [To]                  NVARCHAR(256) NULL,
                [EventTypeId]         NVARCHAR(256) NULL,
                [SessionId]           NVARCHAR(256) NULL,
                [CorrelationId]       NVARCHAR(256) NULL,
                [Payload]             NVARCHAR(MAX) NOT NULL,
                [EnqueueDelayMinutes] INT NOT NULL DEFAULT 0,
                [ScheduledEnqueueTimeUtc] DATETIME2 NULL,
                [CreatedAtUtc]        DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                [DispatchedAtUtc]     DATETIME2 NULL,
                [TraceParent]         NVARCHAR(55) NULL,
                [TraceState]          NVARCHAR(256) NULL,
                INDEX IX_OutboxMessages_Pending NONCLUSTERED ([DispatchedAtUtc], [CreatedAtUtc]) WHERE [DispatchedAtUtc] IS NULL
            );

            INSERT INTO {FullTableName(options)} ([Id], [MessageId], [SessionId], [Payload], [CreatedAtUtc]) VALUES
                (N'legacy-late', N'legacy-late', N'session-1', N'{{}}', '2030-01-01T00:00:02'),
                (N'legacy-early', N'legacy-early', N'session-1', N'{{}}', '2030-01-01T00:00:01');");
        var outbox = new SqlServerOutbox(options);

        await outbox.EnsureTableExistsAsync();
        await outbox.EnsureTableExistsAsync();

        // Rows stored after the upgrade dispatch after every row that was already pending, in
        // store order, even when a publisher clock stamps them earlier.
        var afterUpgrade = Enumerable.Range(0, 3)
            .Select(position => CreateRow($"upgraded-{3 - position}", new DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc)))
            .ToArray();
        await outbox.StoreBatchAsync(afterUpgrade);

        string[] legacyInCreatedOrder = ["legacy-early", "legacy-late"];
        CollectionAssert.AreEqual(
            legacyInCreatedOrder.Concat(afterUpgrade.Select(message => message.Id)).ToArray(),
            (await outbox.GetPendingAsync(10)).Select(message => message.Id).ToArray());
        Assert.AreEqual(
            new DateTimeOffset(2030, 1, 1, 0, 0, 1, TimeSpan.Zero),
            await outbox.GetOldestPendingEnqueuedAtUtcAsync());
        CollectionAssert.AreEqual(new[] { DispatchOrderIndex, PendingSessionIndex }, await GetNonclusteredIndexesAsync(options));
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("NIMBUS_SQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("NIMBUS_SQL_TEST_CONNECTION not set; skipping SQL Server outbox dispatch-order tests.");
        }

        return connectionString;
    }

    // A fresh schema per test, so each test starts from an empty or hand-built table.
    private static SqlServerOutboxOptions CreateOptions() => new()
    {
        ConnectionString = GetConnectionString(),
        Schema = $"nimbus_obx_do_{Guid.NewGuid():N}"[..24],
    };

    private async Task<SqlServerOutbox> CreateOutboxAsync()
    {
        _options = CreateOptions();
        var outbox = new SqlServerOutbox(_options);
        await outbox.EnsureTableExistsAsync();
        return outbox;
    }

    private static string FullTableName(SqlServerOutboxOptions options) => $"[{options.Schema}].[{options.TableName}]";

    private static async Task ExecuteAsync(SqlServerOutboxOptions options, string sql)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private Task<string[]> GetNonclusteredIndexesAsync() => GetNonclusteredIndexesAsync(_options);

    private static async Task<string[]> GetNonclusteredIndexesAsync(SqlServerOutboxOptions options)
    {
        const string sql = @"
            SELECT i.[name] + N'(' + STRING_AGG(c.[name], N',') WITHIN GROUP (ORDER BY ic.[key_ordinal]) + N')'
                + ISNULL(N' WHERE ' + i.[filter_definition], N'')
            FROM sys.indexes i
            JOIN sys.index_columns ic ON ic.[object_id] = i.[object_id] AND ic.[index_id] = i.[index_id] AND ic.[key_ordinal] > 0
            JOIN sys.columns c ON c.[object_id] = ic.[object_id] AND c.[column_id] = ic.[column_id]
            WHERE i.[object_id] = OBJECT_ID(@Table) AND i.[type] = 2
            GROUP BY i.[name], i.[filter_definition]
            ORDER BY i.[name];";

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Table", FullTableName(options));
        var indexes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            indexes.Add(reader.GetString(0));
        }

        return indexes.ToArray();
    }

    private static OutboxMessage CreateRow(string id, DateTime createdAtUtc) => new()
    {
        Id = id,
        MessageId = id,
        To = "ErpEndpoint",
        EventTypeId = "CustomerUpdated",
        SessionId = "customer-1",
        Payload = "{}",
        CreatedAtUtc = createdAtUtc,
    };

    private static Message CreateMessage(string sessionId, string eventTypeId) => new()
    {
        MessageId = $"{sessionId}:{eventTypeId}",
        EventId = $"{sessionId}:{eventTypeId}",
        EventTypeId = eventTypeId,
        SessionId = sessionId,
        CorrelationId = sessionId,
        To = "ErpEndpoint",
        From = "Erp",
        MessageType = MessageType.EventRequest,
        MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = eventTypeId, EventJson = "{}" },
        },
    };

    private sealed class RecordingSender : ISender
    {
        public List<IMessage> Sent { get; } = new();

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Sent.AddRange(messages);
            return Task.CompletedTask;
        }

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
