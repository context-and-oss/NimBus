using Microsoft.Data.SqlClient;
using NimBus.Core.Outbox;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Outbox.SqlServer;

/// <summary>
/// SQL Server implementation of the transactional outbox.
/// </summary>
/// <remarks>
/// Pending rows are dispatched in the order they were stored. SQL Server stamps each row with
/// a <c>SequenceNumber</c> from a sequence object as it is inserted; <see cref="OutboxMessage.CreatedAtUtc"/>
/// cannot order rows because it comes from each publisher's clock and rows written together share it.
/// </remarks>
public class SqlServerOutbox : IOutbox, IOutboxCleanup, IOutboxMetricsQuery
{
    private const string InsertColumns = "[Id], [MessageId], [To], [EventTypeId], [SessionId], [CorrelationId], [Payload], [EnqueueDelayMinutes], [ScheduledEnqueueTimeUtc], [CreatedAtUtc], [TraceParent], [TraceState]";

    private readonly SqlServerOutboxOptions _options;

    public SqlServerOutbox(SqlServerOutboxOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateSqlIdentifier(_options.Schema, nameof(_options.Schema));
        ValidateSqlIdentifier(_options.TableName, nameof(_options.TableName));
    }

    private string SequenceName => $"[{_options.Schema}].[{_options.TableName}_SequenceNumber]";

    /// <summary>
    /// Ensures the outbox table exists. Call on startup if AutoCreateTable is enabled.
    /// Also runs idempotent migrations so deployments that pre-date the W3C trace-context
    /// columns or the <c>SequenceNumber</c> dispatch order gain them on next startup.
    /// </summary>
    public async Task EnsureTableExistsAsync(CancellationToken cancellationToken = default)
    {
        // SequenceNumber is nullable so adding it to an existing table is a metadata-only change
        // (IDENTITY would rewrite every row, and the table keeps dispatched rows until purged).
        // Rows stored before the upgrade keep NULL, which sorts first, so they drain before any
        // later row. The dispatch-order index is created before the old pending index is dropped,
        // so the pending query always has an index.
        var sql = $@"
                IF NOT EXISTS (SELECT * FROM sys.schemas WHERE name = @Schema)
                    EXEC('CREATE SCHEMA [{_options.Schema}]');

                IF OBJECT_ID('{SequenceName}', 'SO') IS NULL
                    CREATE SEQUENCE {SequenceName} AS BIGINT START WITH 1 INCREMENT BY 1;

                IF NOT EXISTS (SELECT * FROM sys.tables t JOIN sys.schemas s ON t.schema_id = s.schema_id WHERE s.name = @Schema AND t.name = @TableName)
                CREATE TABLE {_options.FullTableName} (
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
                    [SequenceNumber]      BIGINT NULL CONSTRAINT [DF_{_options.TableName}_SequenceNumber] DEFAULT (NEXT VALUE FOR {SequenceName})
                );

                IF COL_LENGTH('{_options.FullTableName}', 'TraceParent') IS NULL
                    ALTER TABLE {_options.FullTableName} ADD [TraceParent] NVARCHAR(55) NULL;

                IF COL_LENGTH('{_options.FullTableName}', 'TraceState') IS NULL
                    ALTER TABLE {_options.FullTableName} ADD [TraceState] NVARCHAR(256) NULL;

                IF COL_LENGTH('{_options.FullTableName}', 'To') IS NULL
                    ALTER TABLE {_options.FullTableName} ADD [To] NVARCHAR(256) NULL;

                IF COL_LENGTH('{_options.FullTableName}', 'SequenceNumber') IS NULL
                    ALTER TABLE {_options.FullTableName} ADD [SequenceNumber] BIGINT NULL
                        CONSTRAINT [DF_{_options.TableName}_SequenceNumber] DEFAULT (NEXT VALUE FOR {SequenceName});

                IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('{_options.FullTableName}') AND name = 'IX_OutboxMessages_DispatchOrder')
                    CREATE NONCLUSTERED INDEX IX_OutboxMessages_DispatchOrder ON {_options.FullTableName} ([SequenceNumber], [CreatedAtUtc]) WHERE [DispatchedAtUtc] IS NULL;

                IF EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('{_options.FullTableName}') AND name = 'IX_OutboxMessages_Pending')
                    DROP INDEX IX_OutboxMessages_Pending ON {_options.FullTableName};";

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Schema", _options.Schema);
        command.Parameters.AddWithValue("@TableName", _options.TableName);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task StoreAsync(OutboxMessage message, CancellationToken cancellationToken = default)
    {
        var sql = $@"
                INSERT INTO {_options.FullTableName}
                    ({InsertColumns})
                VALUES
                    (@Id, @MessageId, @To, @EventTypeId, @SessionId, @CorrelationId, @Payload, @EnqueueDelayMinutes, @ScheduledEnqueueTimeUtc, @CreatedAtUtc, @TraceParent, @TraceState)";

        var ambient = SqlServerOutboxAmbientTransaction.Current;
        if (ambient.HasValue)
        {
            await using var ambientCommand = new SqlCommand(sql, ambient.Value.Connection, ambient.Value.Transaction);
            AddOutboxMessageParameters(ambientCommand, message);
            await ambientCommand.ExecuteNonQueryAsync(cancellationToken);
            return;
        }

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        AddOutboxMessageParameters(command, message);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // 12 parameters per row; 100 rows = 1,200 parameters, comfortably under
    // SQL Server's 2,100-per-command limit. Matches the batch size used by
    // MarkAsDispatchedAsync.
    internal const int InsertBatchSize = 100;

    public async Task StoreBatchAsync(IEnumerable<OutboxMessage> messages, CancellationToken cancellationToken = default)
    {
        var list = messages as IReadOnlyList<OutboxMessage> ?? new List<OutboxMessage>(messages);
        if (list.Count == 0)
        {
            return;
        }

        var ambient = SqlServerOutboxAmbientTransaction.Current;
        if (ambient.HasValue)
        {
            for (var offset = 0; offset < list.Count; offset += InsertBatchSize)
            {
                var count = Math.Min(InsertBatchSize, list.Count - offset);
                await using var ambientCommand = CreateBatchInsertCommand(ambient.Value.Connection, ambient.Value.Transaction, list, offset, count);
                await ambientCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            return;
        }

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync(cancellationToken);

        try
        {
            for (var offset = 0; offset < list.Count; offset += InsertBatchSize)
            {
                var count = Math.Min(InsertBatchSize, list.Count - offset);
                await using var command = CreateBatchInsertCommand(connection, transaction, list, offset, count);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        catch
        {
            await transaction.RollbackAsync(cancellationToken);
            throw;
        }
    }

    /// <summary>
    /// Builds one multi-row INSERT for <paramref name="count"/> messages
    /// starting at <paramref name="offset"/>, with per-row suffixed parameter
    /// names. One round-trip per <see cref="InsertBatchSize"/> rows instead of
    /// one per message.
    /// </summary>
    internal SqlCommand CreateBatchInsertCommand(SqlConnection connection, SqlTransaction transaction, IReadOnlyList<OutboxMessage> messages, int offset, int count)
    {
        var command = new SqlCommand
        {
            Connection = connection,
            Transaction = transaction,
        };

        var rows = new string[count];
        for (var i = 0; i < count; i++)
        {
            var suffix = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            rows[i] = $"(@Id{suffix}, @MessageId{suffix}, @To{suffix}, @EventTypeId{suffix}, @SessionId{suffix}, @CorrelationId{suffix}, @Payload{suffix}, @EnqueueDelayMinutes{suffix}, @ScheduledEnqueueTimeUtc{suffix}, @CreatedAtUtc{suffix}, @TraceParent{suffix}, @TraceState{suffix}, {suffix})";
            AddOutboxMessageParameters(command, messages[offset + i], suffix);
        }

        // Each row carries its list position. SQL Server generates the SequenceNumber default in
        // ORDER BY order for INSERT ... SELECT ... ORDER BY; a plain multi-row VALUES insert has
        // no documented order, and the rows' CreatedAtUtc values usually tie.
        command.CommandText = $@"
                INSERT INTO {_options.FullTableName}
                    ({InsertColumns})
                SELECT {InsertColumns}
                FROM (VALUES
                    {string.Join(",\n                    ", rows)}
                ) AS [Rows] ({InsertColumns}, [Ordinal])
                ORDER BY [Ordinal]";

        return command;
    }

    public async Task<IReadOnlyList<OutboxMessage>> GetPendingAsync(int batchSize, CancellationToken cancellationToken = default)
    {
        // Store order: SequenceNumber is assigned as rows are inserted. Rows stored before the
        // column existed have NULL, sort first, and keep their previous CreatedAtUtc order.
        var sql = $@"
                SELECT TOP (@BatchSize) [Id], [MessageId], [To], [EventTypeId], [SessionId], [CorrelationId], [Payload], [EnqueueDelayMinutes], [CreatedAtUtc], [ScheduledEnqueueTimeUtc], [TraceParent], [TraceState]
                FROM {_options.FullTableName} WITH (UPDLOCK, READPAST)
                WHERE [DispatchedAtUtc] IS NULL
                ORDER BY [SequenceNumber] ASC, [CreatedAtUtc] ASC";

        var result = new List<OutboxMessage>();

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@BatchSize", batchSize);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new OutboxMessage
            {
                Id = reader.GetString(0),
                MessageId = reader.GetString(1),
                To = reader.IsDBNull(2) ? null : reader.GetString(2),
                EventTypeId = reader.IsDBNull(3) ? null : reader.GetString(3),
                SessionId = reader.IsDBNull(4) ? null : reader.GetString(4),
                CorrelationId = reader.IsDBNull(5) ? null : reader.GetString(5),
                Payload = reader.GetString(6),
                EnqueueDelayMinutes = reader.GetInt32(7),
                CreatedAtUtc = reader.GetDateTime(8),
                ScheduledEnqueueTimeUtc = reader.IsDBNull(9) ? null : reader.GetDateTime(9),
                DispatchedAtUtc = null,
                TraceParent = reader.IsDBNull(10) ? null : reader.GetString(10),
                TraceState = reader.IsDBNull(11) ? null : reader.GetString(11)
            });
        }

        return result;
    }

    public async Task MarkAsDispatchedAsync(string id, CancellationToken cancellationToken = default)
    {
        var sql = $@"UPDATE {_options.FullTableName} SET [DispatchedAtUtc] = SYSUTCDATETIME() WHERE [Id] = @Id AND [DispatchedAtUtc] IS NULL";

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@Id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkAsDispatchedAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);

        // Use a batched approach with parameterized IN clause
        var idList = new List<string>(ids);
        for (int i = 0; i < idList.Count; i += 100)
        {
            var batch = idList.GetRange(i, Math.Min(100, idList.Count - i));
            var paramNames = new string[batch.Count];
            var sql = $"UPDATE {_options.FullTableName} SET [DispatchedAtUtc] = SYSUTCDATETIME() WHERE [DispatchedAtUtc] IS NULL AND [Id] IN (";

            await using var command = new SqlCommand();
            command.Connection = connection;

            for (int j = 0; j < batch.Count; j++)
            {
                paramNames[j] = $"@Id{j}";
                command.Parameters.AddWithValue(paramNames[j], batch[j]);
            }

            command.CommandText = sql + string.Join(", ", paramNames) + ")";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task<int> PurgeDispatchedAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        var sql = $@"DELETE FROM {_options.FullTableName} WHERE [DispatchedAtUtc] IS NOT NULL AND [DispatchedAtUtc] < @CutoffTime";

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.AddWithValue("@CutoffTime", DateTime.UtcNow.Subtract(olderThan));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<long> GetPendingCountAsync(CancellationToken cancellationToken = default)
    {
        var sql = $@"SELECT COUNT_BIG(*) FROM {_options.FullTableName} WHERE [DispatchedAtUtc] IS NULL";

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long count ? count : 0;
    }

    public async Task<DateTimeOffset?> GetOldestPendingEnqueuedAtUtcAsync(CancellationToken cancellationToken = default)
    {
        // The next row to dispatch, in the same order as GetPendingAsync.
        var sql = $@"SELECT TOP 1 [CreatedAtUtc] FROM {_options.FullTableName} WHERE [DispatchedAtUtc] IS NULL ORDER BY [SequenceNumber] ASC, [CreatedAtUtc] ASC";

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        if (result is null || result is DBNull)
            return null;
        return new DateTimeOffset((DateTime)result, TimeSpan.Zero);
    }

    private static void ValidateSqlIdentifier(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException($"SQL identifier '{parameterName}' cannot be null or empty.", parameterName);
        // Allow characters valid in bracket-quoted SQL Server identifiers, but reject
        // characters that could escape the brackets or enable injection.
        if (value.Contains(']') || value.Contains('\'') || value.Contains(';') || value.Contains("--", StringComparison.Ordinal))
            throw new ArgumentException($"SQL identifier '{parameterName}' contains characters that are not allowed (], ', ;, or --).", parameterName);
    }

    private static void AddOutboxMessageParameters(SqlCommand command, OutboxMessage message, string suffix = "")
    {
        command.Parameters.AddWithValue($"@Id{suffix}", message.Id);
        command.Parameters.AddWithValue($"@MessageId{suffix}", message.MessageId);
        command.Parameters.AddWithValue($"@To{suffix}", (object?)message.To ?? DBNull.Value);
        command.Parameters.AddWithValue($"@EventTypeId{suffix}", (object?)message.EventTypeId ?? DBNull.Value);
        command.Parameters.AddWithValue($"@SessionId{suffix}", (object?)message.SessionId ?? DBNull.Value);
        command.Parameters.AddWithValue($"@CorrelationId{suffix}", (object?)message.CorrelationId ?? DBNull.Value);
        command.Parameters.AddWithValue($"@Payload{suffix}", message.Payload);
        command.Parameters.AddWithValue($"@EnqueueDelayMinutes{suffix}", message.EnqueueDelayMinutes);
        command.Parameters.AddWithValue($"@ScheduledEnqueueTimeUtc{suffix}", (object?)message.ScheduledEnqueueTimeUtc ?? DBNull.Value);
        command.Parameters.AddWithValue($"@CreatedAtUtc{suffix}", message.CreatedAtUtc);
        command.Parameters.AddWithValue($"@TraceParent{suffix}", (object?)message.TraceParent ?? DBNull.Value);
        command.Parameters.AddWithValue($"@TraceState{suffix}", (object?)message.TraceState ?? DBNull.Value);
    }
}
