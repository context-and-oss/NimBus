#pragma warning disable CA1707, CA2007
using System;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.Core.Outbox;
using NimBus.Outbox.SqlServer;
using NimBus.Testing.Conformance;

namespace NimBus.Outbox.SqlServer.Tests;

[TestClass]
[DoNotParallelize]
public sealed class SqlServerOutboxClaimingConformanceTests : OutboxClaimingConformanceTests
{
    private static readonly string Schema = $"nimbus_obx_cl_{Guid.NewGuid():N}"[..24];

    protected override async Task<IOutbox> CreateOutboxAsync()
    {
        var connectionString = Environment.GetEnvironmentVariable("NIMBUS_SQL_TEST_CONNECTION");
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            Assert.Inconclusive("NIMBUS_SQL_TEST_CONNECTION not set; skipping SQL Server outbox claiming conformance tests.");
        }

        var options = new SqlServerOutboxOptions
        {
            ConnectionString = connectionString,
            Schema = Schema,
        };

        var outbox = new SqlServerOutbox(options);
        await outbox.EnsureTableExistsAsync();

        await using var connection = new SqlConnection(options.ConnectionString);
        await connection.OpenAsync();
        await using var command = new SqlCommand($"DELETE FROM {options.FullTableName}", connection);
        await command.ExecuteNonQueryAsync();
        return outbox;
    }
}
