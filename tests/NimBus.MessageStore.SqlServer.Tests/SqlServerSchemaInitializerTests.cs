#pragma warning disable CA1707, CA2007
using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NimBus.MessageStore.SqlServer;

namespace NimBus.MessageStore.SqlServer.Tests;

[TestClass]
public sealed class SqlServerSchemaInitializerTests
{
    [TestMethod]
    public async Task VerifyOnly_on_empty_database_fails_fast_with_missing_artifacts()
    {
        var schema = NewSchemaName();
        var initializer = CreateInitializer(schema, SchemaProvisioningMode.VerifyOnly);

        var ex = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => initializer.StartAsync(CancellationToken.None));

        StringAssert.Contains(ex.Message, "Missing artifacts", StringComparison.Ordinal);
        StringAssert.Contains(ex.Message, $"schema '{schema}'", StringComparison.Ordinal);
        StringAssert.Contains(ex.Message, $"[{schema}].[DbUpJournal]", StringComparison.Ordinal);
        StringAssert.Contains(ex.Message, $"[{schema}].[Messages]", StringComparison.Ordinal);
        StringAssert.Contains(ex.Message, $"[{schema}].[UnresolvedEvents]", StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task AutoApply_can_run_twice_on_same_connection_as_no_op()
    {
        var schema = NewSchemaName();
        var first = CreateInitializer(schema, SchemaProvisioningMode.AutoApply);
        var second = CreateInitializer(schema, SchemaProvisioningMode.AutoApply);
        var verify = CreateInitializer(schema, SchemaProvisioningMode.VerifyOnly);

        await first.StartAsync(CancellationToken.None);
        await second.StartAsync(CancellationToken.None);
        await verify.StartAsync(CancellationToken.None);
    }

    [TestMethod]
    public async Task AutoApply_creates_date_leading_search_sort_indexes()
    {
        var schema = NewSchemaName();
        var initializer = CreateInitializer(schema, SchemaProvisioningMode.AutoApply);

        await initializer.StartAsync(CancellationToken.None);

        Assert.IsTrue(
            await IndexExists(schema, "Messages", "IX_Messages_EnqueuedTimeUtc"),
            "IX_Messages_EnqueuedTimeUtc should exist after provisioning.");
        Assert.IsTrue(
            await IndexExists(schema, "UnresolvedEvents", "IX_UnresolvedEvents_UpdatedAtUtc"),
            "IX_UnresolvedEvents_UpdatedAtUtc should exist after provisioning.");
        Assert.IsTrue(
            await IndexExists(schema, "MessageAudits", "IX_MessageAudits_CreatedAtUtc"),
            "IX_MessageAudits_CreatedAtUtc should exist after provisioning.");
    }

    [TestMethod]
    public async Task AutoApply_adds_nullable_CloudEvent_columns_to_tracking_and_audit_tables()
    {
        var schema = NewSchemaName();
        var initializer = CreateInitializer(schema, SchemaProvisioningMode.AutoApply);

        await initializer.StartAsync(CancellationToken.None);

        foreach (var table in new[] { "Messages", "UnresolvedEvents", "MessageAudits" })
        {
            foreach (var column in new[] { "CloudEventId", "CloudEventSource", "CloudEventType", "CloudEventSubject" })
            {
                Assert.IsTrue(await NullableColumnExists(schema, table, column),
                    $"[{schema}].[{table}].[{column}] should exist and remain nullable for legacy native-message rows.");
            }
        }
    }

    [TestMethod]
    public async Task AutoApply_adds_nullable_handoff_columns_to_messages_and_unresolved_events()
    {
        var schema = NewSchemaName();
        var initializer = CreateInitializer(schema, SchemaProvisioningMode.AutoApply);

        await initializer.StartAsync(CancellationToken.None);

        foreach (var table in new[] { "Messages", "UnresolvedEvents" })
        {
            foreach (var column in new[] { "PendingSubStatus", "HandoffReason", "ExternalJobId", "ExpectedBy" })
            {
                Assert.IsTrue(await NullableColumnExists(schema, table, column),
                    $"[{schema}].[{table}].[{column}] should exist and remain nullable for non-handoff rows.");
            }
        }
    }

    [TestMethod]
    public async Task AutoApply_recreates_the_platform_heartbeat_objects_dropped_by_0010()
    {
        var schema = NewSchemaName();
        var initializer = CreateInitializer(schema, SchemaProvisioningMode.AutoApply);

        await initializer.StartAsync(CancellationToken.None);

        foreach (var column in new[] { "IsHeartbeatEnabled", "EndpointHeartbeatStatus" })
        {
            Assert.IsTrue(await NullableColumnExists(schema, "EndpointMetadata", column),
                $"[{schema}].[EndpointMetadata].[{column}] should be re-added by 0018 after 0010 dropped it.");
        }

        Assert.IsTrue(await NullableColumnExists(schema, "Heartbeats", "SdkVersion"),
            "Heartbeats.SdkVersion should exist and stay nullable for pre-heartbeat SDKs.");
        Assert.IsTrue(
            await IndexExists(schema, "Heartbeats", "IX_Heartbeats_EndpointId_ReceivedTimeUtc"),
            "IX_Heartbeats_EndpointId_ReceivedTimeUtc should exist after provisioning.");
        Assert.AreEqual(1, await RowCount(schema, "HeartbeatSettings"),
            "0018 seeds exactly one HeartbeatSettings row.");
        Assert.AreEqual(1, await RowCount(schema, "ServiceHealth"),
            "0018 seeds the Resolver service-health row.");
    }

    [TestMethod]
    public async Task AutoApply_creates_heartbeat_history_objects()
    {
        var schema = NewSchemaName();
        var initializer = CreateInitializer(schema, SchemaProvisioningMode.AutoApply);

        await initializer.StartAsync(CancellationToken.None);

        Assert.IsTrue(await ColumnExists(schema, "Heartbeats", "IntervalSeconds", nullable: false));
        Assert.IsTrue(await ColumnExists(schema, "HeartbeatSettings", "LastHeartbeatFoldAtUtc", nullable: true));
        Assert.IsTrue(await TableExists(schema, "HeartbeatUptimeDays"));
        Assert.IsTrue(await TableExists(schema, "HeartbeatGaps"));
        Assert.IsTrue(await IndexExists(schema, "HeartbeatUptimeDays", "IX_HeartbeatUptimeDays_DayUtc"));
        Assert.IsTrue(await IndexExists(schema, "HeartbeatGaps", "IX_HeartbeatGaps_ToUtc"));
    }

    private static async Task<int> RowCount(string schema, string table)
    {
        await using var conn = new SqlConnection(SqlServerStoreTestHarness.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        // Schema and table names are test-owned constants, never user input.
        cmd.CommandText = $"SELECT COUNT(1) FROM [{schema}].[{table}]";
        var result = await cmd.ExecuteScalarAsync();
        return result is int count ? count : 0;
    }

    private static async Task<bool> IndexExists(string schema, string table, string indexName)
    {
        await using var conn = new SqlConnection(SqlServerStoreTestHarness.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT COUNT(1)
FROM sys.indexes i
INNER JOIN sys.tables t ON i.object_id = t.object_id
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE s.name = @Schema AND t.name = @Table AND i.name = @Index";
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@Table", table);
        cmd.Parameters.AddWithValue("@Index", indexName);
        var result = await cmd.ExecuteScalarAsync();
        return result is int count && count > 0;
    }

    private static async Task<bool> NullableColumnExists(string schema, string table, string column)
        => await ColumnExists(schema, table, column, nullable: true);

    private static async Task<bool> ColumnExists(string schema, string table, string column, bool nullable)
    {
        await using var conn = new SqlConnection(SqlServerStoreTestHarness.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT COUNT(1)
FROM sys.columns c
INNER JOIN sys.tables t ON c.object_id = t.object_id
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE s.name = @Schema AND t.name = @Table AND c.name = @Column AND c.is_nullable = @Nullable";
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@Table", table);
        cmd.Parameters.AddWithValue("@Column", column);
        cmd.Parameters.AddWithValue("@Nullable", nullable);
        var result = await cmd.ExecuteScalarAsync();
        return result is int count && count > 0;
    }

    private static async Task<bool> TableExists(string schema, string table)
    {
        await using var conn = new SqlConnection(SqlServerStoreTestHarness.GetConnectionString());
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT COUNT(1)
FROM sys.tables t
INNER JOIN sys.schemas s ON t.schema_id = s.schema_id
WHERE s.name = @Schema AND t.name = @Table";
        cmd.Parameters.AddWithValue("@Schema", schema);
        cmd.Parameters.AddWithValue("@Table", table);
        var result = await cmd.ExecuteScalarAsync();
        return result is int count && count > 0;
    }

    private static SqlServerSchemaInitializer CreateInitializer(string schema, SchemaProvisioningMode mode)
        => new(
            Options.Create(new SqlServerMessageStoreOptions
            {
                ConnectionString = SqlServerStoreTestHarness.GetConnectionString(),
                Schema = schema,
                ProvisioningMode = mode,
            }),
            NullLogger<SqlServerSchemaInitializer>.Instance);

    private static string NewSchemaName()
        => $"nimbus_test_{Guid.NewGuid():N}"[..24];
}
