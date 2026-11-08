#pragma warning disable CA1707, CA2007
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Outbox;
using NimBus.Outbox.SqlServer;

namespace NimBus.Outbox.SqlServer.Tests;

/// <summary>
/// DB-free assertions on the multi-row INSERT command builder: one command per
/// 100 messages, per-row suffixed parameters (12/row, under the 2,100 limit),
/// null-vs-value parameter mapping identical to the single-insert path, and
/// rows inserted in list order.
/// </summary>
[TestClass]
public sealed class SqlServerOutboxBatchInsertCommandTests
{
    private static SqlServerOutbox CreateOutbox() =>
        new SqlServerOutbox(new SqlServerOutboxOptions
        {
            ConnectionString = "Server=unused;Database=unused",
        });

    private static OutboxMessage NewMessage(int i) => new OutboxMessage
    {
        Id = $"id-{i}",
        MessageId = $"msg-{i}",
        To = i % 2 == 0 ? $"endpoint-{i}" : null,
        EventTypeId = $"type-{i}",
        SessionId = $"session-{i}",
        CorrelationId = null!,
        Payload = $"{{\"n\":{i}}}",
        EnqueueDelayMinutes = 0,
        ScheduledEnqueueTimeUtc = null,
        CreatedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddSeconds(i),
        TraceParent = null!,
        TraceState = null!,
    };

    [TestMethod]
    public void Builds_one_values_row_and_12_parameters_per_message()
    {
        var outbox = CreateOutbox();
        var messages = Enumerable.Range(0, 5).Select(NewMessage).ToList();

        using var connection = new SqlConnection();
        using var command = outbox.CreateBatchInsertCommand(connection, transaction: null!, messages, offset: 0, count: 5);

        Assert.AreEqual(5 * 12, command.Parameters.Count);
        Assert.AreEqual(5, Regex.Count(command.CommandText, Regex.Escape("(@Id")),
            "Expected one VALUES row per message.");
        Assert.AreEqual(1, Regex.Count(command.CommandText, "INSERT INTO"),
            "The whole page must be a single INSERT statement.");
        StringAssert.Contains(command.CommandText, "[nimbus].[OutboxMessages]", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Suffixes_parameters_per_row_and_maps_nulls_to_dbnull()
    {
        var outbox = CreateOutbox();
        var messages = Enumerable.Range(0, 2).Select(NewMessage).ToList();

        using var connection = new SqlConnection();
        using var command = outbox.CreateBatchInsertCommand(connection, transaction: null!, messages, offset: 0, count: 2);

        Assert.AreEqual("id-0", command.Parameters["@Id0"].Value);
        Assert.AreEqual("id-1", command.Parameters["@Id1"].Value);
        Assert.AreEqual("endpoint-0", command.Parameters["@To0"].Value, "Even rows carry a To value.");
        Assert.AreEqual(DBNull.Value, command.Parameters["@To1"].Value, "Odd rows have null To -> DBNull.");
        Assert.AreEqual(DBNull.Value, command.Parameters["@CorrelationId0"].Value);
        Assert.AreEqual(DBNull.Value, command.Parameters["@ScheduledEnqueueTimeUtc1"].Value);
        StringAssert.Contains(command.CommandText, "@TraceState1, 1)", StringComparison.Ordinal);
    }

    [TestMethod]
    public void Inserts_the_page_in_list_order()
    {
        // The rows' SequenceNumber values (the dispatch order) follow the list only because the
        // page is inserted with INSERT ... SELECT ... ORDER BY: SQL Server documents the order of
        // generated values for that form, not for a plain multi-row VALUES insert.
        var outbox = CreateOutbox();
        var messages = Enumerable.Range(0, 3).Select(NewMessage).ToList();

        using var connection = new SqlConnection();
        using var command = outbox.CreateBatchInsertCommand(connection, transaction: null!, messages, offset: 0, count: 3);

        StringAssert.Contains(command.CommandText, "@TraceState0, 0)", StringComparison.Ordinal);
        StringAssert.Contains(command.CommandText, "@TraceState2, 2)", StringComparison.Ordinal);
        StringAssert.Matches(command.CommandText, new Regex(@"ORDER BY \[Ordinal\]\s*$"));
    }

    [TestMethod]
    public void Respects_offset_and_count_for_pages_after_the_first()
    {
        var outbox = CreateOutbox();
        var messages = Enumerable.Range(0, 150).Select(NewMessage).ToList();

        using var connection = new SqlConnection();
        using var command = outbox.CreateBatchInsertCommand(connection, transaction: null!, messages, offset: 100, count: 50);

        Assert.AreEqual(50 * 12, command.Parameters.Count);
        Assert.AreEqual("id-100", command.Parameters["@Id0"].Value,
            "The second page starts at the offset with suffixes restarting at 0.");
        Assert.AreEqual("id-149", command.Parameters["@Id49"].Value);
    }

    [TestMethod]
    public void Full_page_of_100_rows_stays_under_the_parameter_limit()
    {
        var outbox = CreateOutbox();
        var messages = Enumerable.Range(0, SqlServerOutbox.InsertBatchSize).Select(NewMessage).ToList();

        using var connection = new SqlConnection();
        using var command = outbox.CreateBatchInsertCommand(connection, transaction: null!, messages, offset: 0, count: messages.Count);

        Assert.AreEqual(1200, command.Parameters.Count, "12 params x 100 rows.");
        Assert.IsTrue(command.Parameters.Count < 2100, "Must stay under SQL Server's 2,100-parameter limit.");
    }

    [TestMethod]
    public async Task StoreBatchAsync_with_empty_input_returns_without_touching_the_connection()
    {
        // The connection string is unusable — reaching for it would throw.
        var outbox = new SqlServerOutbox(new SqlServerOutboxOptions
        {
            ConnectionString = "Server=definitely-not-a-host;Connect Timeout=1",
        });

        await outbox.StoreBatchAsync(Array.Empty<OutboxMessage>());
    }
}
