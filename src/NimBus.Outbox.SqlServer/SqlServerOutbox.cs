using Microsoft.Data.SqlClient;
using NimBus.Core.Outbox;
using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Outbox.SqlServer;

/// <summary>
/// SQL Server implementation of the transactional outbox.
/// </summary>
/// <remarks>
/// <para>
/// Pending rows are dispatched in the order they were stored. SQL Server stamps each row with
/// a <c>SequenceNumber</c> from a sequence object as it is inserted; <see cref="OutboxMessage.CreatedAtUtc"/>
/// cannot order rows because it comes from each publisher's clock and rows written together share it.
/// </para>
/// <para>
/// The outbox implements <see cref="IOutboxClaiming"/>: dispatchers lease rows with
/// <c>LeaseUntilUtc</c>/<c>ClaimedBy</c>, so several dispatcher replicas can share one table, and
/// failed rows carry <c>AttemptCount</c>, <c>NextAttemptAtUtc</c>, <c>LastError</c> and, once
/// parked, <c>ParkedAtUtc</c>. Use <see cref="RequeueParkedAsync"/> to retry parked rows.
/// </para>
/// </remarks>
public class SqlServerOutbox : IOutbox, IOutboxCleanup, IOutboxMetricsQuery, IOutboxClaiming
{
    /// <summary>
    /// Maximum stored length of <c>LastError</c>; longer errors are truncated.
    /// </summary>
    public const int MaxLastErrorLength = 2000;

    private const string InsertColumns = "[Id], [MessageId], [To], [EventTypeId], [SessionId], [CorrelationId], [Payload], [EnqueueDelayMinutes], [ScheduledEnqueueTimeUtc], [CreatedAtUtc], [TraceParent], [TraceState]";

    private readonly SqlServerOutboxOptions _options;

    public SqlServerOutbox(SqlServerOutboxOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        ValidateSqlIdentifier(_options.Schema, nameof(_options.Schema));
        ValidateSqlIdentifier(_options.TableName, nameof(_options.TableName));
    }

    private string SequenceName => $"[{_options.Schema}].[{_options.TableName}_SequenceNumber]";

    // sp_getapplock resources are limited to 255 characters.
    private string ClaimLockResource
    {
        get
        {
            var resource = $"NimBus.Outbox.Claim/{_options.Schema}.{_options.TableName}";
            return resource.Length <= 255 ? resource : resource[..255];
        }
    }

    /// <summary>
    /// Ensures the outbox table exists. Call on startup if AutoCreateTable is enabled.
    /// Also runs idempotent migrations so deployments that pre-date the W3C trace-context
    /// columns, the <c>SequenceNumber</c> dispatch order, or the claiming and retry columns gain
    /// them on next startup. Every added column is nullable, so adding it is a metadata-only
    /// change that does not rewrite existing rows.
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
                    [SequenceNumber]      BIGINT NULL CONSTRAINT [DF_{_options.TableName}_SequenceNumber] DEFAULT (NEXT VALUE FOR {SequenceName}),
                    [AttemptCount]        INT NULL,
                    [NextAttemptAtUtc]    DATETIME2 NULL,
                    [LastError]           NVARCHAR({MaxLastErrorLength}) NULL,
                    [ParkedAtUtc]         DATETIME2 NULL,
                    [LeaseUntilUtc]       DATETIME2 NULL,
                    [ClaimedBy]           NVARCHAR(256) NULL
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

                IF COL_LENGTH('{_options.FullTableName}', 'AttemptCount') IS NULL
                    ALTER TABLE {_options.FullTableName} ADD [AttemptCount] INT NULL;

                IF COL_LENGTH('{_options.FullTableName}', 'NextAttemptAtUtc') IS NULL
                    ALTER TABLE {_options.FullTableName} ADD [NextAttemptAtUtc] DATETIME2 NULL;

                IF COL_LENGTH('{_options.FullTableName}', 'LastError') IS NULL
                    ALTER TABLE {_options.FullTableName} ADD [LastError] NVARCHAR({MaxLastErrorLength}) NULL;

                IF COL_LENGTH('{_options.FullTableName}', 'ParkedAtUtc') IS NULL
                    ALTER TABLE {_options.FullTableName} ADD [ParkedAtUtc] DATETIME2 NULL;

                IF COL_LENGTH('{_options.FullTableName}', 'LeaseUntilUtc') IS NULL
                    ALTER TABLE {_options.FullTableName} ADD [LeaseUntilUtc] DATETIME2 NULL;

                IF COL_LENGTH('{_options.FullTableName}', 'ClaimedBy') IS NULL
                    ALTER TABLE {_options.FullTableName} ADD [ClaimedBy] NVARCHAR(256) NULL;

                IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('{_options.FullTableName}') AND name = 'IX_OutboxMessages_DispatchOrder')
                    CREATE NONCLUSTERED INDEX IX_OutboxMessages_DispatchOrder ON {_options.FullTableName} ([SequenceNumber], [CreatedAtUtc]) WHERE [DispatchedAtUtc] IS NULL;

                IF EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('{_options.FullTableName}') AND name = 'IX_OutboxMessages_Pending')
                    DROP INDEX IX_OutboxMessages_Pending ON {_options.FullTableName};";

        // The session-blocked check of ClaimPendingAsync looks up a session's other pending rows.
        // A separate batch, so the batch above has added the INCLUDE columns before this one is
        // compiled.
        var indexSql = $@"
                IF NOT EXISTS (SELECT * FROM sys.indexes WHERE object_id = OBJECT_ID('{_options.FullTableName}') AND name = 'IX_OutboxMessages_PendingSession')
                    CREATE NONCLUSTERED INDEX IX_OutboxMessages_PendingSession ON {_options.FullTableName} ([SessionId])
                        INCLUDE ([ParkedAtUtc], [NextAttemptAtUtc], [LeaseUntilUtc])
                        WHERE [DispatchedAtUtc] IS NULL;";

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using (var command = new SqlCommand(sql, connection))
        {
            command.Parameters.AddWithValue("@Schema", _options.Schema);
            command.Parameters.AddWithValue("@TableName", _options.TableName);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var indexCommand = new SqlCommand(indexSql, connection))
        {
            await indexCommand.ExecuteNonQueryAsync(cancellationToken);
        }
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

    /// <inheritdoc />
    /// <remarks>
    /// This is a plain read: it neither leases the rows nor skips rows that are parked, waiting
    /// for a retry or leased. <see cref="OutboxDispatcher"/> uses <see cref="ClaimPendingAsync"/>.
    /// </remarks>
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

    /// <inheritdoc />
    /// <remarks>
    /// Deletes in batches of <see cref="SqlServerOutboxOptions.PurgeBatchSize"/> rows, each its own
    /// short autocommit statement, so a large backlog never holds one long lock or escalates to a
    /// table lock. Cancellation is honoured between batches; rows deleted by earlier batches stay
    /// deleted.
    /// </remarks>
    public async Task<int> PurgeDispatchedAsync(TimeSpan olderThan, CancellationToken cancellationToken = default)
    {
        var sql = $@"DELETE TOP (@BatchSize) FROM {_options.FullTableName} WHERE [DispatchedAtUtc] IS NOT NULL AND [DispatchedAtUtc] < @CutoffTime";
        var cutoff = DateTime.UtcNow.Subtract(olderThan);
        var batchSize = _options.PurgeBatchSize;
        var total = 0;

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await using var command = new SqlCommand(sql, connection);
            command.Parameters.Add("@BatchSize", SqlDbType.Int).Value = batchSize;
            command.Parameters.Add("@CutoffTime", SqlDbType.DateTime2).Value = cutoff;
            var deleted = await command.ExecuteNonQueryAsync(cancellationToken);
            total += deleted;
            if (deleted < batchSize)
                return total;
        }
    }

    /// <inheritdoc />
    /// <remarks>
    /// Claimers are serialised by an exclusive <c>sp_getapplock</c> held for the claim
    /// transaction, so a claimer always sees the leases another claimer committed (under both
    /// locking READ COMMITTED and READ_COMMITTED_SNAPSHOT) and claimers cannot deadlock on each
    /// other's rows. Publishers' inserts and dispatch checkpoints do not take the lock. When the
    /// lock cannot be acquired within <see cref="SqlServerOutboxOptions.ClaimLockTimeout"/>, no
    /// rows are claimed this time.
    /// </remarks>
    public async Task<IReadOnlyList<OutboxMessage>> ClaimPendingAsync(
        int batchSize,
        string claimantId,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        ValidateClaimant(claimantId);
        var leaseMilliseconds = ToMilliseconds(leaseDuration, nameof(leaseDuration));
        ArgumentOutOfRangeException.ThrowIfLessThan(leaseMilliseconds, 1, nameof(leaseDuration));

        // A row is claimable when it is ready itself and no OTHER pending row of its session is
        // parked, waiting for a retry or leased: a blocked session contributes no rows, so it
        // neither fills the batch nor lets a later row overtake the one that blocks it. The
        // subquery sees the table as it was before this statement's own updates.
        var sql = $@"
                SET XACT_ABORT ON;
                BEGIN TRANSACTION;

                DECLARE @LockResult INT;
                EXEC @LockResult = sp_getapplock
                    @Resource = @LockResource,
                    @LockMode = 'Exclusive',
                    @LockOwner = 'Transaction',
                    @LockTimeout = @LockTimeoutMs;

                IF @LockResult >= 0
                BEGIN
                    DECLARE @Now DATETIME2 = SYSUTCDATETIME();

                    WITH [Claimable] AS (
                        SELECT TOP (@BatchSize) [o].*
                        FROM {_options.FullTableName} AS [o] WITH (UPDLOCK, ROWLOCK, READPAST)
                        WHERE [o].[DispatchedAtUtc] IS NULL
                          AND [o].[ParkedAtUtc] IS NULL
                          AND ([o].[NextAttemptAtUtc] IS NULL OR [o].[NextAttemptAtUtc] <= @Now)
                          AND ([o].[LeaseUntilUtc] IS NULL OR [o].[LeaseUntilUtc] <= @Now)
                          AND NOT EXISTS (
                              SELECT 1
                              FROM {_options.FullTableName} AS [b]
                              WHERE [o].[SessionId] IS NOT NULL
                                AND [o].[SessionId] <> N''
                                AND [b].[SessionId] = [o].[SessionId]
                                AND [b].[Id] <> [o].[Id]
                                AND [b].[DispatchedAtUtc] IS NULL
                                AND ([b].[ParkedAtUtc] IS NOT NULL
                                     OR [b].[NextAttemptAtUtc] > @Now
                                     OR [b].[LeaseUntilUtc] > @Now))
                        ORDER BY [o].[SequenceNumber] ASC, [o].[CreatedAtUtc] ASC
                    )
                    UPDATE [Claimable]
                    SET [LeaseUntilUtc] = DATEADD(MILLISECOND, @LeaseMs, @Now),
                        [ClaimedBy] = @ClaimedBy
                    OUTPUT
                        inserted.[Id], inserted.[MessageId], inserted.[To], inserted.[EventTypeId],
                        inserted.[SessionId], inserted.[CorrelationId], inserted.[Payload],
                        inserted.[EnqueueDelayMinutes], inserted.[CreatedAtUtc],
                        inserted.[ScheduledEnqueueTimeUtc], inserted.[TraceParent], inserted.[TraceState],
                        ISNULL(inserted.[AttemptCount], 0), inserted.[SequenceNumber];
                END;

                COMMIT TRANSACTION;";

        var claimed = new List<(OutboxMessage Message, long? SequenceNumber)>();

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@LockResource", SqlDbType.NVarChar, 255).Value = ClaimLockResource;
        command.Parameters.Add("@LockTimeoutMs", SqlDbType.Int).Value = ToMilliseconds(_options.ClaimLockTimeout, nameof(_options.ClaimLockTimeout));
        command.Parameters.Add("@BatchSize", SqlDbType.Int).Value = batchSize;
        command.Parameters.Add("@LeaseMs", SqlDbType.Int).Value = leaseMilliseconds;
        command.Parameters.Add("@ClaimedBy", SqlDbType.NVarChar, 256).Value = claimantId;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var message = new OutboxMessage
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
                    TraceState = reader.IsDBNull(11) ? null : reader.GetString(11),
                    AttemptCount = reader.GetInt32(12),
                };
                claimed.Add((message, reader.IsDBNull(13) ? null : reader.GetInt64(13)));
            }
        }

        // OUTPUT returns rows in no particular order; restore the dispatch order of
        // GetPendingAsync (NULL sequence numbers, from before the column existed, first).
        claimed.Sort(CompareDispatchOrder);
        var result = new List<OutboxMessage>(claimed.Count);
        foreach (var (message, _) in claimed)
        {
            result.Add(message);
        }

        return result;
    }

    /// <inheritdoc />
    public async Task RecordDispatchFailureAsync(
        string id,
        string claimantId,
        string error,
        TimeSpan retryDelay,
        bool park,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ValidateClaimant(claimantId);
        var retryMilliseconds = park ? 0 : ToMilliseconds(retryDelay, nameof(retryDelay));
        ArgumentOutOfRangeException.ThrowIfLessThan(retryMilliseconds, 0, nameof(retryDelay));

        var sql = $@"
                DECLARE @Now DATETIME2 = SYSUTCDATETIME();
                UPDATE {_options.FullTableName}
                SET [AttemptCount] = ISNULL([AttemptCount], 0) + 1,
                    [LastError] = @Error,
                    [NextAttemptAtUtc] = CASE WHEN @Park = 1 THEN NULL ELSE DATEADD(MILLISECOND, @RetryMs, @Now) END,
                    [ParkedAtUtc] = CASE WHEN @Park = 1 THEN @Now ELSE NULL END,
                    [LeaseUntilUtc] = NULL
                WHERE [Id] = @Id
                  AND [DispatchedAtUtc] IS NULL
                  AND [ClaimedBy] = @ClaimedBy
                  AND [LeaseUntilUtc] IS NOT NULL;";

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.NVarChar, 128).Value = id;
        command.Parameters.Add("@ClaimedBy", SqlDbType.NVarChar, 256).Value = claimantId;
        command.Parameters.Add("@Error", SqlDbType.NVarChar, MaxLastErrorLength).Value = Truncate(error ?? string.Empty, MaxLastErrorLength);
        command.Parameters.Add("@RetryMs", SqlDbType.Int).Value = retryMilliseconds;
        command.Parameters.Add("@Park", SqlDbType.Bit).Value = park;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <inheritdoc />
    public async Task ReleaseClaimsAsync(
        string claimantId,
        IEnumerable<string> ids,
        CancellationToken cancellationToken = default)
    {
        ValidateClaimant(claimantId);
        ArgumentNullException.ThrowIfNull(ids);
        var idList = new List<string>(ids);
        if (idList.Count == 0)
            return;

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        for (var offset = 0; offset < idList.Count; offset += 100)
        {
            var count = Math.Min(100, idList.Count - offset);
            await using var command = new SqlCommand { Connection = connection };
            var paramNames = new string[count];
            for (var i = 0; i < count; i++)
            {
                paramNames[i] = $"@Id{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
                command.Parameters.Add(paramNames[i], SqlDbType.NVarChar, 128).Value = idList[offset + i];
            }

            command.Parameters.Add("@ClaimedBy", SqlDbType.NVarChar, 256).Value = claimantId;
            command.CommandText = $@"
                UPDATE {_options.FullTableName}
                SET [LeaseUntilUtc] = NULL
                WHERE [ClaimedBy] = @ClaimedBy
                  AND [DispatchedAtUtc] IS NULL
                  AND [Id] IN ({string.Join(", ", paramNames)});";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Returns parked rows to dispatch: clears their parking, attempt count and retry time (the
    /// last error is kept for reference) so the dispatcher claims them on its next poll, in their
    /// original order. Call it after fixing whatever made the rows fail.
    /// </summary>
    /// <param name="id">The row to requeue, or <c>null</c> to requeue every parked row.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of rows requeued.</returns>
    public async Task<int> RequeueParkedAsync(string? id = null, CancellationToken cancellationToken = default)
    {
        var sql = $@"
                UPDATE {_options.FullTableName}
                SET [ParkedAtUtc] = NULL,
                    [AttemptCount] = 0,
                    [NextAttemptAtUtc] = NULL,
                    [LeaseUntilUtc] = NULL
                WHERE [ParkedAtUtc] IS NOT NULL
                  AND [DispatchedAtUtc] IS NULL
                  AND (@Id IS NULL OR [Id] = @Id);";

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        command.Parameters.Add("@Id", SqlDbType.NVarChar, 128).Value = (object?)id ?? DBNull.Value;
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Returns the number of parked rows: rows the dispatcher stopped retrying, each of which
    /// also holds back the later rows of its session.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The number of parked, undispatched rows.</returns>
    public async Task<long> GetParkedCountAsync(CancellationToken cancellationToken = default)
    {
        var sql = $@"SELECT COUNT_BIG(*) FROM {_options.FullTableName} WHERE [DispatchedAtUtc] IS NULL AND [ParkedAtUtc] IS NOT NULL";

        await using var connection = new SqlConnection(_options.ConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = new SqlCommand(sql, connection);
        var result = await command.ExecuteScalarAsync(cancellationToken);
        return result is long count ? count : 0;
    }

    private static int CompareDispatchOrder((OutboxMessage Message, long? SequenceNumber) left, (OutboxMessage Message, long? SequenceNumber) right)
    {
        // Matches ORDER BY [SequenceNumber] ASC, [CreatedAtUtc] ASC: SQL Server sorts NULL first.
        if (left.SequenceNumber.HasValue != right.SequenceNumber.HasValue)
            return left.SequenceNumber.HasValue ? 1 : -1;

        var bySequence = Nullable.Compare(left.SequenceNumber, right.SequenceNumber);
        return bySequence != 0 ? bySequence : left.Message.CreatedAtUtc.CompareTo(right.Message.CreatedAtUtc);
    }

    private static int ToMilliseconds(TimeSpan value, string parameterName)
    {
        var milliseconds = Math.Ceiling(value.TotalMilliseconds);
        if (milliseconds > int.MaxValue)
            throw new ArgumentOutOfRangeException(parameterName, value, "The duration must be shorter than 24 days.");
        return (int)milliseconds;
    }

    private static void ValidateClaimant(string claimantId)
    {
        ArgumentException.ThrowIfNullOrEmpty(claimantId);
        if (claimantId.Length > 256)
            throw new ArgumentOutOfRangeException(nameof(claimantId), "The claimant id must be at most 256 characters.");
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength];


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
        if (value.Contains(']', StringComparison.Ordinal) || value.Contains('\'', StringComparison.Ordinal) || value.Contains(';', StringComparison.Ordinal) || value.Contains("--", StringComparison.Ordinal))
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
