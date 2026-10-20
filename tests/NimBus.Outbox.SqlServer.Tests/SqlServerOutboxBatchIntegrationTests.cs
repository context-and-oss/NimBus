#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Newtonsoft.Json;
using NimBus.Core.Events;
using NimBus.Core.Messages;
using NimBus.Core.Outbox;
using NimBus.Outbox.SqlServer;
using NimBus.SDK;
using NimBus.SDK.EventHandlers;

namespace NimBus.Outbox.SqlServer.Tests;

/// <summary>
/// Env-gated integration tests for the batched outbox INSERT — set
/// <c>NIMBUS_SQL_TEST_CONNECTION</c> to run (mirrors the message-store
/// conformance suite gating; otherwise inconclusive). Covers page boundaries
/// (1/100/101/250), atomic rollback on duplicate ids, and the ambient
/// connection/transaction path.
/// </summary>
[TestClass]
public sealed class SqlServerOutboxBatchIntegrationTests
{
    private static string? _schema;
    private static SqlServerOutboxOptions _options = null!;

    private static string GetConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable("NIMBUS_SQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("NIMBUS_SQL_TEST_CONNECTION not set; skipping SQL Server outbox integration tests.");
        }

        return connectionString;
    }

    private static async Task<SqlServerOutbox> CreateOutboxAsync()
    {
        var connectionString = GetConnectionString();
        _schema ??= $"nimbus_obx_{Guid.NewGuid():N}"[..24];
        _options = new SqlServerOutboxOptions
        {
            ConnectionString = connectionString,
            Schema = _schema,
        };
        var outbox = new SqlServerOutbox(_options);
        await outbox.EnsureTableExistsAsync();
        await ResetAsync();
        return outbox;
    }

    private static async Task ResetAsync()
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"DELETE FROM [{_options.Schema}].[{_options.TableName}]", connection);
        await command.ExecuteNonQueryAsync();
    }

    private static OutboxMessage NewMessage(int i, string idPrefix = "id") => new OutboxMessage
    {
        Id = $"{idPrefix}-{i}",
        MessageId = $"msg-{i}",
        To = $"endpoint-{i % 3}",
        EventTypeId = "TestEvent",
        SessionId = $"session-{i % 5}",
        CorrelationId = i % 2 == 0 ? $"corr-{i}" : null,
        Payload = $"{{\"n\":{i}}}",
        EnqueueDelayMinutes = 0,
        ScheduledEnqueueTimeUtc = null,
        CreatedAtUtc = DateTime.UtcNow,
        TraceParent = null!,
        TraceState = null!,
    };

    [TestMethod]
    [DataRow(1)]
    [DataRow(100)]
    [DataRow(101)]
    [DataRow(250)]
    public async Task StoreBatchAsync_persists_every_row_across_page_boundaries(int rows)
    {
        var outbox = await CreateOutboxAsync();
        var messages = Enumerable.Range(0, rows).Select(i => NewMessage(i)).ToList();

        await outbox.StoreBatchAsync(messages);

        var pending = await outbox.GetPendingAsync(rows + 10);
        Assert.AreEqual(rows, pending.Count);
        var stored = pending.Single(m => m.Id == "id-0");
        Assert.AreEqual("msg-0", stored.MessageId);
        Assert.AreEqual("{\"n\":0}", stored.Payload);
        Assert.AreEqual("corr-0", stored.CorrelationId);
    }

    [TestMethod]
    public async Task StoreBatchAsync_rolls_back_the_whole_batch_on_duplicate_id()
    {
        var outbox = await CreateOutboxAsync();
        var messages = Enumerable.Range(0, 150).Select(i => NewMessage(i)).ToList();
        messages[120] = NewMessage(0); // duplicate primary key "id-0"

        await Assert.ThrowsExactlyAsync<SqlException>(() => outbox.StoreBatchAsync(messages));

        var pending = await outbox.GetPendingAsync(500);
        Assert.AreEqual(0, pending.Count,
            "A failing page must roll back every page of the batch — the outbox write is all-or-nothing.");
    }

    [TestMethod]
    public async Task StoreBatchAsync_enlists_in_the_ambient_transaction_commit()
    {
        var outbox = await CreateOutboxAsync();
        var messages = Enumerable.Range(0, 120).Select(i => NewMessage(i)).ToList();

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync();
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            using (SqlServerOutboxAmbientTransaction.Begin(connection, transaction))
            {
                await outbox.StoreBatchAsync(messages);
            }

            await transaction.CommitAsync();
        }

        var pending = await outbox.GetPendingAsync(500);
        Assert.AreEqual(120, pending.Count);
    }

    [TestMethod]
    public async Task StoreBatchAsync_ambient_rollback_discards_the_batch()
    {
        var outbox = await CreateOutboxAsync();
        var messages = Enumerable.Range(0, 120).Select(i => NewMessage(i)).ToList();

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync();
        await using (var transaction = (SqlTransaction)await connection.BeginTransactionAsync())
        {
            using (SqlServerOutboxAmbientTransaction.Begin(connection, transaction))
            {
                await outbox.StoreBatchAsync(messages);
            }

            await transaction.RollbackAsync();
        }

        var pending = await outbox.GetPendingAsync(500);
        Assert.AreEqual(0, pending.Count, "Rolling back the ambient transaction must discard the batch.");
    }

    [TestMethod]
    public async Task PublishFromContext_SqlOutboxRoundTrip_MatchesDirectWorkflowMetadata()
    {
        var context = new EventHandlerContext
        {
            MessageId = "inventory-reserved-1",
            SessionId = "order-42",
            CorrelationId = "conversation-7",
            ParentMessageId = "reserve-inventory-1",
            OriginatingMessageId = "order-placed-1",
        };
        var @event = new WorkflowEvent { OrderId = "order-42" };

        var directSender = new CapturingSender();
        await new PublisherClient(directSender, "OrderOrchestrator").PublishFromContext(
            @event,
            context,
            messageId: "order-42:capture-payment:1");

        var sqlOutbox = await CreateOutboxAsync();
        await new PublisherClient(new OutboxSender(sqlOutbox), "OrderOrchestrator").PublishFromContext(
            @event,
            context,
            messageId: "order-42:capture-payment:1");

        var stored = (await sqlOutbox.GetPendingAsync(10)).Single();
        var roundTripped = JsonConvert.DeserializeObject<Message>(
            stored.Payload,
            Constants.CreateSafeJsonSettings());
        Assert.IsNotNull(roundTripped);
        AssertEquivalentMetadata(directSender.Sent.Single(), roundTripped);
    }

    private static void AssertEquivalentMetadata(IMessage expected, Message actual)
    {
        Assert.AreEqual(expected.MessageId, actual.MessageId);
        Assert.AreEqual(expected.SessionId, actual.SessionId);
        Assert.AreEqual(expected.CorrelationId, actual.CorrelationId);
        Assert.AreEqual(expected.ParentMessageId, actual.ParentMessageId);
        Assert.AreEqual(expected.OriginatingMessageId, actual.OriginatingMessageId);
        Assert.AreEqual(expected.OriginatingFrom, actual.OriginatingFrom);
    }

    private sealed class WorkflowEvent : Event
    {
        public string OrderId { get; set; } = string.Empty;

        public override string GetSessionId() => OrderId;
    }

    private sealed class CapturingSender : ISender
    {
        public List<IMessage> Sent { get; } = new();

        public Task Send(IMessage message, int messageEnqueueDelay = 0, CancellationToken cancellationToken = default)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public Task Send(
            IEnumerable<IMessage> messages,
            int messageEnqueueDelay = 0,
            CancellationToken cancellationToken = default)
        {
            Sent.AddRange(messages);
            return Task.CompletedTask;
        }

        public Task<long> ScheduleMessage(
            IMessage message,
            DateTimeOffset scheduledEnqueueTime,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(0L);

        public Task CancelScheduledMessage(long sequenceNumber, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }
}
