using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DbUp;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// Hosted service that runs DbUp on startup. Behavior is governed by
/// <see cref="SqlServerMessageStoreOptions.ProvisioningMode"/>:
/// <c>AutoApply</c> upgrades the schema; <c>VerifyOnly</c> fails fast if any
/// embedded script is unapplied.
/// </summary>
internal sealed class SqlServerSchemaInitializer : IHostedService
{
    // Regular SQL Server identifier — letter or underscore start, then letters,
    // digits or underscores. We deliberately exclude `]`, brackets, dots and
    // every other character that could break out of `[ ... ]` quoting in the
    // T-SQL the message store emits. The 128-char cap matches sysname.
    private static readonly Regex SchemaNameRegex = new(
        @"^[A-Za-z_][A-Za-z0-9_]{0,127}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly string[] RequiredTables =
    {
        "DbUpJournal",
        "Messages",
        "UnresolvedEvents",
        "MessageAudits",
        "EndpointSubscriptions",
        "EndpointMetadata",
        "EventSchemas",
        "AccessControl",
        "Heartbeats",
        "HeartbeatSettings",
        "HeartbeatUptimeDays",
        "HeartbeatGaps",
        "ServiceHealth",
        "EndpointAcknowledgements",
        "AuditSettings",
        "McpAccessSettings",
        "AgentApiKeys",
        "AgentApiKeyUsage",
    };

    private static readonly string[] RequiredViews =
    {
        "EndpointEventTypeCounts",
        "FailedMessageInsights",
    };

    private readonly SqlServerMessageStoreOptions _options;
    private readonly ILogger<SqlServerSchemaInitializer> _logger;

    public SqlServerSchemaInitializer(IOptions<SqlServerMessageStoreOptions> options, ILogger<SqlServerSchemaInitializer> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.ConnectionString))
            throw new InvalidOperationException("SqlServerMessageStoreOptions.ConnectionString is required.");

        if (string.IsNullOrWhiteSpace(_options.Schema))
            throw new InvalidOperationException("SqlServerMessageStoreOptions.Schema is required.");

        if (!SchemaNameRegex.IsMatch(_options.Schema))
            throw new InvalidOperationException(
                $"SqlServerMessageStoreOptions.Schema '{_options.Schema}' is not a valid SQL identifier. " +
                "Schema names must start with a letter or underscore and contain only letters, digits or underscores (max 128 chars).");

        if (_options.ProvisioningMode == SchemaProvisioningMode.VerifyOnly)
        {
            await VerifyRequiredArtifacts(cancellationToken).ConfigureAwait(false);

            var verifyAssembly = typeof(SqlServerSchemaInitializer).Assembly;
            var verifyUpgrader = BuildUpgrader(verifyAssembly);
            var pending = verifyUpgrader.GetScriptsToExecute().Select(s => s.Name).ToList();
            if (pending.Count > 0)
            {
                throw new InvalidOperationException(
                    "SQL Server schema is out of date. Run DbUp scripts via the deployment pipeline, " +
                    "or switch to ProvisioningMode.AutoApply for development. Pending scripts: " +
                    string.Join(", ", pending));
            }
            _logger.LogInformation("SQL Server message-store schema verified.");
            return;
        }

        // Multiple NimBus services (Resolver, WebApp, …) hosted in the same Aspire
        // AppHost all run AutoApply on startup against the same database. Serialize
        // EnsureSchemaExists + DbUp under a session-scoped sp_getapplock so they
        // don't race on `CREATE SCHEMA` or on the IF-OBJECT_ID/CREATE TABLE pair.
        // The second runner waits, finds the schema already created and the
        // journal populated, and exits without applying anything.
        //
        // Disable connection pooling for the lock connection: the @LockOwner=Session
        // applock is released only when the SQL session ends, and a pooled
        // SqlConnection.Dispose() returns the physical session to the pool instead
        // of closing it. Without `Pooling=False`, the lock stays held for as long
        // as the pool keeps the session alive — which can far exceed the second
        // runner's 60 s wait — and they all time out. We also explicitly call
        // sp_releaseapplock as belt-and-braces before disposing.
        var lockConnString = new SqlConnectionStringBuilder(_options.ConnectionString)
        {
            Pooling = false,
        }.ConnectionString;
        await using (var lockConn = new SqlConnection(lockConnString))
        {
            await lockConn.OpenAsync(cancellationToken).ConfigureAwait(false);
            await AcquireSchemaUpgradeLock(lockConn, cancellationToken).ConfigureAwait(false);

            try
            {
                // DbUp's journal table lives in the configured schema, and journal setup
                // runs before any DbUp script. Bootstrap the schema directly so journal
                // creation has somewhere to land; 0001_Schema.sql remains idempotent.
                await EnsureSchemaExists(cancellationToken).ConfigureAwait(false);

                var assembly = typeof(SqlServerSchemaInitializer).Assembly;
                var upgrader = BuildUpgrader(assembly);

                var result = upgrader.PerformUpgrade();
                if (!result.Successful)
                {
                    throw new InvalidOperationException(
                        $"DbUp failed to apply NimBus message-store schema: {result.Error?.Message}", result.Error);
                }

                _logger.LogInformation("SQL Server message-store schema upgrade applied {Count} script(s).",
                    result.Scripts.Count());
            }
            finally
            {
                await ReleaseSchemaUpgradeLock(lockConn, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private DbUp.Engine.UpgradeEngine BuildUpgrader(System.Reflection.Assembly assembly) =>
        DeployChanges.To
            .SqlDatabase(_options.ConnectionString)
            .WithScriptsEmbeddedInAssembly(
                assembly,
                name => name.Contains(".Schema.", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .WithVariable("schema", _options.Schema)
            .JournalToSqlTable(_options.Schema, "DbUpJournal")
            .LogToConsole()
            .Build();

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task AcquireSchemaUpgradeLock(SqlConnection conn, CancellationToken cancellationToken)
    {
        const int lockTimeoutMs = 60_000;
        var resource = $"NimBus.Schema.{_options.Schema}";
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXEC @result = sp_getapplock @Resource = @res, @LockMode = N'Exclusive', @LockOwner = N'Session', @LockTimeout = @timeout";
        cmd.CommandTimeout = (lockTimeoutMs / 1000) + 30;
        var resultParam = cmd.Parameters.Add("@result", System.Data.SqlDbType.Int);
        resultParam.Direction = System.Data.ParameterDirection.Output;
        cmd.Parameters.AddWithValue("@res", resource);
        cmd.Parameters.AddWithValue("@timeout", lockTimeoutMs);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var status = resultParam.Value is int i ? i : -999;
        // 0 = lock granted synchronously; 1 = lock granted after waiting for other sessions.
        // Negative values are failures (-1 timeout, -2 cancelled, -3 deadlock, -999 parameter error).
        if (status < 0)
        {
            throw new InvalidOperationException(
                $"Could not acquire schema-upgrade lock for '{resource}' on the SQL Server (sp_getapplock returned {status}). " +
                "Another NimBus instance may be holding the lock; retry once it finishes, or extend the lockTimeout.");
        }
    }

    private async Task ReleaseSchemaUpgradeLock(SqlConnection conn, CancellationToken cancellationToken)
    {
        var resource = $"NimBus.Schema.{_options.Schema}";
        try
        {
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "EXEC sp_releaseapplock @Resource = @res, @LockOwner = N'Session'";
            cmd.Parameters.AddWithValue("@res", resource);
            await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Don't let release failures mask the real error from DbUp; we already
            // disable pooling on this connection so the session ends on Dispose.
            _logger.LogWarning(ex, "Failed to release schema-upgrade lock '{Resource}'; relying on session close.", resource);
        }
    }

    private async Task EnsureSchemaExists(CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
IF NOT EXISTS (SELECT 1 FROM sys.schemas WHERE name = @Schema)
BEGIN
    DECLARE @sql nvarchar(max) = N'CREATE SCHEMA ' + QUOTENAME(@Schema);
    EXEC sp_executesql @sql;
END";
        cmd.Parameters.AddWithValue("@Schema", _options.Schema);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task VerifyRequiredArtifacts(CancellationToken cancellationToken)
    {
        await using var conn = new SqlConnection(_options.ConnectionString);
        await conn.OpenAsync(cancellationToken).ConfigureAwait(false);

        var missing = new List<string>();
        if (!await SchemaExists(conn, cancellationToken).ConfigureAwait(false))
        {
            missing.Add($"schema '{_options.Schema}'");
            missing.AddRange(RequiredTables.Select(Qualified));
            missing.AddRange(RequiredViews.Select(Qualified));
        }
        else
        {
            foreach (var table in RequiredTables)
            {
                if (!await ObjectExists(conn, "U", table, cancellationToken).ConfigureAwait(false))
                    missing.Add(Qualified(table));
            }

            foreach (var view in RequiredViews)
            {
                if (!await ObjectExists(conn, "V", view, cancellationToken).ConfigureAwait(false))
                    missing.Add(Qualified(view));
            }
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "SQL Server message-store schema is missing or out of date. Missing artifacts: " +
                string.Join(", ", missing));
        }
    }

    private async Task<bool> SchemaExists(SqlConnection conn, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(1) FROM sys.schemas WHERE name = @Schema";
        cmd.Parameters.AddWithValue("@Schema", _options.Schema);
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result) > 0;
    }

    private async Task<bool> ObjectExists(SqlConnection conn, string type, string name, CancellationToken cancellationToken)
    {
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = @"
SELECT COUNT(1)
FROM sys.objects o
INNER JOIN sys.schemas s ON s.schema_id = o.schema_id
WHERE s.name = @Schema AND o.name = @Name AND o.type = @Type";
        cmd.Parameters.AddWithValue("@Schema", _options.Schema);
        cmd.Parameters.AddWithValue("@Name", name);
        cmd.Parameters.AddWithValue("@Type", type);
        var result = await cmd.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(result) > 0;
    }

    private string Qualified(string objectName) => $"[{_options.Schema}].[{objectName}]";
}
