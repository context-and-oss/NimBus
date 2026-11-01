using System;
using System.Data;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace NimBus.MessageStore.SqlServer;

/// <summary>Rows deleted by one retention run, per rule, and how long the run took.</summary>
internal sealed record SqlServerRetentionResult(
    int MessagesDeleted,
    int AuditsDeleted,
    int ArchivedTrackingDeleted,
    int UnresolvedTrackingDeleted,
    TimeSpan Duration);

/// <summary>
/// Applies the SQL Server retention rules once (GH#291), mirroring the Cosmos DB TTLs:
/// <list type="bullet">
/// <item>R1: <c>Messages</c> with <c>CreatedAtUtc</c> older than 90 days.</item>
/// <item>R2: <c>MessageAudits</c> with <c>CreatedAtUtc</c> older than 365 days.</item>
/// <item>R3: <c>UnresolvedEvents</c> with <c>Deleted = 1</c> and <c>UpdatedAtUtc</c> older than 30 days.</item>
/// <item>R4: <c>UnresolvedEvents</c> with <c>Deleted = 0</c> and <c>UpdatedAtUtc</c> older than
/// <see cref="SqlServerMessageStoreOptions.UnresolvedRetentionDays"/> (skipped when -1).</item>
/// </list>
/// Each rule deletes in batches of at most <see cref="BatchSize"/> rows until a batch comes up short,
/// so no statement holds locks on more than one batch.
/// </summary>
internal sealed class SqlServerRetentionRunner
{
    internal const int BatchSize = 5000;
    internal const int MessageRetentionDays = 90;
    internal const int AuditRetentionDays = 365;
    internal const int ArchivedTrackingRetentionDays = 30;

    private readonly SqlServerMessageStoreOptions _options;

    public SqlServerRetentionRunner(SqlServerMessageStoreOptions options)
    {
        _options = options;
    }

    public SqlServerRetentionRunner(IOptions<SqlServerMessageStoreOptions> options)
        : this(options.Value)
    {
    }

    public async Task<SqlServerRetentionResult> RunOnceAsync(CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var now = DateTime.UtcNow;

        await using var connection = new SqlConnection(_options.ConnectionString);
        await SqlServerExceptionTranslation.TranslateAsync(() => connection.OpenAsync(cancellationToken)).ConfigureAwait(false);

        var messages = await DeleteInBatches(
            connection, "Messages", "[CreatedAtUtc] < @Cutoff", now.AddDays(-MessageRetentionDays), cancellationToken).ConfigureAwait(false);
        var audits = await DeleteInBatches(
            connection, "MessageAudits", "[CreatedAtUtc] < @Cutoff", now.AddDays(-AuditRetentionDays), cancellationToken).ConfigureAwait(false);
        var archived = await DeleteInBatches(
            connection, "UnresolvedEvents", "[Deleted] = 1 AND [UpdatedAtUtc] < @Cutoff", now.AddDays(-ArchivedTrackingRetentionDays), cancellationToken).ConfigureAwait(false);

        var unresolved = 0;
        if (_options.UnresolvedRetentionDays != SqlServerMessageStoreOptions.UnlimitedRetentionDays)
        {
            unresolved = await DeleteInBatches(
                connection, "UnresolvedEvents", "[Deleted] = 0 AND [UpdatedAtUtc] < @Cutoff", now.AddDays(-_options.UnresolvedRetentionDays), cancellationToken).ConfigureAwait(false);
        }

        return new SqlServerRetentionResult(messages, audits, archived, unresolved, stopwatch.Elapsed);
    }

    private async Task<int> DeleteInBatches(
        SqlConnection connection, string table, string predicate, DateTime cutoff, CancellationToken cancellationToken)
    {
        var sql = $"DELETE TOP ({BatchSize}) FROM {Table(table)} WHERE {predicate};";
        // DateTime2 explicitly: Dapper's default DbType.DateTime would round the cutoff to ~3 ms.
        var parameters = new DynamicParameters();
        parameters.Add("Cutoff", cutoff, DbType.DateTime2);

        var total = 0;
        int deleted;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var command = new CommandDefinition(
                sql,
                parameters,
                commandTimeout: _options.CommandTimeoutSeconds,
                cancellationToken: cancellationToken);
            deleted = await connection.ExecuteAsync(command).ConfigureAwait(false);
            total += deleted;
        }
        while (deleted >= BatchSize);

        return total;
    }

    private string Table(string table) =>
        $"[{_options.Schema.Replace("]", "]]", StringComparison.Ordinal)}].[{table}]";
}
