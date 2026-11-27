#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Concurrent;
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
/// dispatching one SQL Server outbox from several dispatcher replicas, parking a poison row,
/// upgrading a table created before rows were claimed, and the batched purge.
/// </summary>
[TestClass]
public sealed class SqlServerOutboxClaimingTests
{
    [TestMethod]
    public async Task Two_concurrent_dispatchers_send_each_row_exactly_once_in_session_order()
    {
        var options = CreateOptions();
        var outbox = new SqlServerOutbox(options);
        await outbox.EnsureTableExistsAsync();
        var published = Enumerable.Range(0, 30)
            .SelectMany(position => Enumerable.Range(0, 8).Select(session => CreateMessage($"session-{session}", position)))
            .ToList();
        await new OutboxSender(outbox).Send(published);

        var sent = new ConcurrentQueue<SentEntry>();
        var first = new OutboxDispatcher(outbox, new ConcurrentRecordingSender("replica-1", sent), new OutboxDispatcherOptions { DispatcherId = "replica-1" });
        var second = new OutboxDispatcher(outbox, new ConcurrentRecordingSender("replica-2", sent), new OutboxDispatcherOptions { DispatcherId = "replica-2" });
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));

        await Task.WhenAll(
            DrainAsync(first, outbox, timeout.Token),
            DrainAsync(second, outbox, timeout.Token));

        var sentInOrder = sent.ToArray();
        Assert.AreEqual(published.Count, sentInOrder.Length, "Every row must be sent exactly once.");
        CollectionAssert.AreEquivalent(
            published.Select(message => message.MessageId).ToArray(),
            sentInOrder.Select(entry => entry.MessageId).ToArray());
        foreach (var session in published.GroupBy(message => message.SessionId))
        {
            CollectionAssert.AreEqual(
                session.Select(message => message.MessageId).ToArray(),
                sentInOrder.Where(entry => entry.SessionId == session.Key).Select(entry => entry.MessageId).ToArray(),
                $"Session {session.Key} was sent out of order.");
        }

        Assert.AreEqual(2, sentInOrder.Select(entry => entry.Replica).Distinct().Count(), "Both replicas should have taken part.");
        Assert.AreEqual(0, await outbox.GetPendingCountAsync());
    }

    [TestMethod]
    public async Task A_poison_row_is_parked_and_does_not_starve_other_sessions()
    {
        var options = CreateOptions();
        var outbox = new SqlServerOutbox(options);
        await outbox.EnsureTableExistsAsync();
        var session = Enumerable.Range(0, 10).Select(position => CreateMessage("poisoned", position)).ToList();
        var other = CreateMessage("healthy", 0);
        await new OutboxSender(outbox).Send(session);
        await new OutboxSender(outbox).Send(other);

        var sender = new ConcurrentRecordingSender("replica", failMessageId: session[0].MessageId);
        var retryDelay = TimeSpan.FromSeconds(2);
        var dispatcher = new OutboxDispatcher(outbox, sender, new OutboxDispatcherOptions
        {
            MaxAttempts = 2,
            InitialRetryDelay = retryDelay,
            MaxRetryDelay = retryDelay,
        });

        Assert.AreEqual(0, await dispatcher.DispatchPendingAsync(batchSize: 10));
        Assert.AreEqual(1, await dispatcher.DispatchPendingAsync(batchSize: 10), "The healthy session is dispatched on the second poll.");
        await Task.Delay(retryDelay + TimeSpan.FromMilliseconds(500));
        Assert.AreEqual(0, await dispatcher.DispatchPendingAsync(batchSize: 10));
        await Task.Delay(retryDelay + TimeSpan.FromMilliseconds(500));
        Assert.AreEqual(0, await dispatcher.DispatchPendingAsync(batchSize: 10));

        Assert.AreEqual(2, sender.Attempts(session[0].MessageId), "The poison row is attempted MaxAttempts times.");
        CollectionAssert.AreEqual(new[] { other.MessageId }, sender.Sent.Select(entry => entry.MessageId).ToArray());
        Assert.AreEqual(1L, await outbox.GetParkedCountAsync());
        StringAssert.Contains(await ReadLastErrorAsync(options, session[0].MessageId!), "poison", StringComparison.Ordinal);

        Assert.AreEqual(1, await outbox.RequeueParkedAsync());
        var recovered = new ConcurrentRecordingSender("recovered");
        var dispatched = await new OutboxDispatcher(outbox, recovered).DispatchPendingAsync(batchSize: 10);

        Assert.AreEqual(session.Count, dispatched);
        CollectionAssert.AreEqual(
            session.Select(message => message.MessageId).ToArray(),
            recovered.Sent.Select(entry => entry.MessageId).ToArray());
    }

    [TestMethod]
    public async Task EnsureTableExistsAsync_upgrades_a_table_created_before_rows_were_claimed()
    {
        var options = CreateOptions();
        await ExecuteAsync(options, $"CREATE SCHEMA [{options.Schema}];");
        // The sequence, table and index exactly as EnsureTableExistsAsync created them before
        // claiming, with rows still pending from the previous version.
        await ExecuteAsync(options, $@"
            CREATE SEQUENCE [{options.Schema}].[{options.TableName}_SequenceNumber] AS BIGINT START WITH 1 INCREMENT BY 1;");
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
                [SequenceNumber]      BIGINT NULL CONSTRAINT [DF_{options.TableName}_SequenceNumber] DEFAULT (NEXT VALUE FOR [{options.Schema}].[{options.TableName}_SequenceNumber])
            );

            CREATE NONCLUSTERED INDEX IX_OutboxMessages_DispatchOrder ON {FullTableName(options)} ([SequenceNumber], [CreatedAtUtc]) WHERE [DispatchedAtUtc] IS NULL;

            INSERT INTO {FullTableName(options)} ([Id], [MessageId], [SessionId], [Payload], [CreatedAtUtc]) VALUES
                (N'legacy-1', N'legacy-1', N'session-1', N'{{}}', '2030-01-01T00:00:01');
            INSERT INTO {FullTableName(options)} ([Id], [MessageId], [SessionId], [Payload], [CreatedAtUtc]) VALUES
                (N'legacy-2', N'legacy-2', N'session-1', N'{{}}', '2030-01-01T00:00:02');
            INSERT INTO {FullTableName(options)} ([Id], [MessageId], [SessionId], [Payload], [CreatedAtUtc], [DispatchedAtUtc]) VALUES
                (N'legacy-sent', N'legacy-sent', N'session-1', N'{{}}', '2030-01-01T00:00:00', '2030-01-01T00:00:03');");
        var outbox = new SqlServerOutbox(options);

        await outbox.EnsureTableExistsAsync();
        await outbox.EnsureTableExistsAsync();

        foreach (var column in new[] { "AttemptCount", "NextAttemptAtUtc", "LastError", "ParkedAtUtc", "LeaseUntilUtc", "ClaimedBy" })
        {
            Assert.IsTrue(await ColumnIsNullableAsync(options, column), $"{column} must be added as a nullable column.");
        }

        CollectionAssert.AreEqual(
            new[] { "IX_OutboxMessages_DispatchOrder", "IX_OutboxMessages_PendingSession" },
            await GetNonclusteredIndexNamesAsync(options));

        var claimed = await outbox.ClaimPendingAsync(10, "upgraded", TimeSpan.FromMinutes(1));
        CollectionAssert.AreEqual(new[] { "legacy-1", "legacy-2" }, claimed.Select(message => message.Id).ToArray());
        Assert.IsTrue(claimed.All(message => message.AttemptCount == 0));
    }

    [TestMethod]
    public async Task PurgeDispatchedAsync_deletes_in_batches_and_keeps_pending_and_recent_rows()
    {
        var options = CreateOptions();
        options.PurgeBatchSize = 3;
        var outbox = new SqlServerOutbox(options);
        await outbox.EnsureTableExistsAsync();
        var old = Enumerable.Range(0, 10).Select(position => CreateRow($"old-{position}")).ToList();
        var recent = CreateRow("recent");
        var pending = CreateRow("pending");
        await outbox.StoreBatchAsync(old.Append(recent).Append(pending).ToList());
        await outbox.MarkAsDispatchedAsync(old.Select(row => row.Id).Append(recent.Id));
        await ExecuteAsync(options, $@"
            UPDATE {FullTableName(options)} SET [DispatchedAtUtc] = DATEADD(DAY, -2, SYSUTCDATETIME()) WHERE [Id] LIKE N'old-%';");

        var purged = await outbox.PurgeDispatchedAsync(TimeSpan.FromDays(1));

        Assert.AreEqual(old.Count, purged);
        Assert.AreEqual(2L, await CountRowsAsync(options));
        Assert.AreEqual(1L, await outbox.GetPendingCountAsync());
    }

    private static async Task DrainAsync(OutboxDispatcher dispatcher, SqlServerOutbox outbox, CancellationToken cancellationToken)
    {
        while (await outbox.GetPendingCountAsync(cancellationToken) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (await dispatcher.DispatchPendingAsync(batchSize: 7, cancellationToken) == 0)
                await Task.Delay(10, cancellationToken);
        }
    }

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("NIMBUS_SQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("NIMBUS_SQL_TEST_CONNECTION not set; skipping SQL Server outbox claiming tests.");
        }

        return connectionString;
    }

    // A fresh schema per test, so each test starts from an empty or hand-built table.
    private static SqlServerOutboxOptions CreateOptions() => new()
    {
        ConnectionString = GetConnectionString(),
        Schema = $"nimbus_obx_cm_{Guid.NewGuid():N}"[..24],
    };

    private static string FullTableName(SqlServerOutboxOptions options) => $"[{options.Schema}].[{options.TableName}]";

    private static async Task ExecuteAsync(SqlServerOutboxOptions options, string sql)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> CountRowsAsync(SqlServerOutboxOptions options)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"SELECT COUNT_BIG(*) FROM {FullTableName(options)}", connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string> ReadLastErrorAsync(SqlServerOutboxOptions options, string messageId)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"SELECT [LastError] FROM {FullTableName(options)} WHERE [MessageId] = @MessageId", connection);
        command.Parameters.AddWithValue("@MessageId", messageId);
        return (string)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> ColumnIsNullableAsync(SqlServerOutboxOptions options, string column)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [is_nullable] FROM sys.columns WHERE [object_id] = OBJECT_ID(@Table) AND [name] = @Column",
            connection);
        command.Parameters.AddWithValue("@Table", FullTableName(options));
        command.Parameters.AddWithValue("@Column", column);
        var result = await command.ExecuteScalarAsync();
        return result is bool nullable && nullable;
    }

    private static async Task<string[]> GetNonclusteredIndexNamesAsync(SqlServerOutboxOptions options)
    {
        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand(
            "SELECT [name] FROM sys.indexes WHERE [object_id] = OBJECT_ID(@Table) AND [type] = 2 ORDER BY [name]",
            connection);
        command.Parameters.AddWithValue("@Table", FullTableName(options));
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            names.Add(reader.GetString(0));
        }

        return names.ToArray();
    }

    private static OutboxMessage CreateRow(string id) => new()
    {
        Id = id,
        MessageId = id,
        To = "ErpEndpoint",
        EventTypeId = "CustomerUpdated",
        SessionId = id,
        Payload = "{}",
        CreatedAtUtc = DateTime.UtcNow,
    };

    private static Message CreateMessage(string sessionId, int position) => new()
    {
        MessageId = $"{sessionId}:{position:D3}",
        EventId = $"{sessionId}:{position:D3}",
        EventTypeId = "CustomerUpdated",
        SessionId = sessionId,
        CorrelationId = sessionId,
        To = "ErpEndpoint",
        From = "Erp",
        MessageType = MessageType.EventRequest,
        MessageContent = new MessageContent
        {
            EventContent = new EventContent { EventTypeId = "CustomerUpdated", EventJson = "{}" },
        },
    };

    private sealed record SentEntry(string? MessageId, string? SessionId, string Replica);

    private sealed class ConcurrentRecordingSender(
        string replica,
        ConcurrentQueue<SentEntry>? sink = null,
        string? failMessageId = null) : ISender
    {
        private readonly ConcurrentDictionary<string, int> _attempts = new(StringComparer.Ordinal);

        public ConcurrentQueue<SentEntry> Sent { get; } = sink ?? new ConcurrentQueue<SentEntry>();

        public int Attempts(string? messageId) =>
            messageId is not null && _attempts.TryGetValue(messageId, out var attempts) ? attempts : 0;

        public async Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            _attempts.AddOrUpdate(message.MessageId!, 1, (_, attempts) => attempts + 1);
            // Yield so the two dispatchers' sends interleave.
            await Task.Delay(1, cancellationToken);
            if (string.Equals(message.MessageId, failMessageId, StringComparison.Ordinal))
                throw new InvalidOperationException("poison");

            Sent.Enqueue(new SentEntry(message.MessageId, message.SessionId, replica));
        }

        public Task Send(IEnumerable<IMessage> messages, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<long> ScheduleMessage(IMessage message, DateTimeOffset scheduledEnqueueTime, CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
