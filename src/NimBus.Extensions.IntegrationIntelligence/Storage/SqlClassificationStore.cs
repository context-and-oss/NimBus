using System.Runtime.CompilerServices;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using DbUp;

namespace NimBus.Extensions.IntegrationIntelligence.Storage;

/// <summary>SQL conditional writes atomically commit reservations, history and owner fencing.</summary>
public sealed class SqlClassificationStore(string connectionString, TimeProvider? clock = null) : AtomicClassificationStore(clock)
{
    private readonly string _connectionString = connectionString;

    /// <summary>The default of <see cref="SchemaUpgradeTimeoutSeconds"/>: 30 minutes, as for the SQL Server message store.</summary>
    public const int DefaultSchemaUpgradeTimeoutSeconds = 1800;

    private readonly int _schemaUpgradeTimeoutSeconds = DefaultSchemaUpgradeTimeoutSeconds;

    /// <summary>
    /// The longest one migration script may run in <see cref="EnsureSchemaAsync"/>, in seconds; 0
    /// sets no limit. Also how long a second instance waits for the migration lock (at least 30 s;
    /// without a limit when this is 0).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int SchemaUpgradeTimeoutSeconds
    {
        get => _schemaUpgradeTimeoutSeconds;
        init
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value);
            _schemaUpgradeTimeoutSeconds = value;
        }
    }

    public async Task EnsureSchemaAsync(CancellationToken cancellationToken = default)
    {
        // The lock holder may keep it for as long as one script may run, so a second instance
        // waits that long (never less than the 30 s it always did; without a limit for none).
        var lockWaitMs = SchemaUpgradeTimeoutSeconds <= 0 || SchemaUpgradeTimeoutSeconds > int.MaxValue / 1000
            ? -1
            : Math.Max(SchemaUpgradeTimeoutSeconds, 30) * 1000;
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var gate = connection.CreateCommand();
        gate.CommandText = "DECLARE @result int; EXEC @result=sp_getapplock @Resource='NimBus.Intelligence.Migrations', @LockMode='Exclusive', @LockOwner='Session', @LockTimeout=@wait; IF @result<0 THROW 51000, 'Classification migration lock unavailable', 1;";
        gate.Parameters.AddWithValue("@wait", lockWaitMs);
        gate.CommandTimeout = lockWaitMs < 0 ? 0 : (lockWaitMs / 1000) + 30;
        await gate.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        gate.Parameters.Clear();
        gate.CommandTimeout = 30;
        try
        {
            var result = DeployChanges.To.SqlDatabase(_connectionString)
                .WithScriptsEmbeddedInAssembly(typeof(SqlClassificationStore).Assembly)
                .JournalToSqlTable("dbo", "IntelligenceSchemaVersions")
                // Not SqlClient's 30 s default: a migration on a large table can take longer.
                .WithExecutionTimeout(TimeSpan.FromSeconds(SchemaUpgradeTimeoutSeconds))
                .Build().PerformUpgrade();
            if (!result.Successful) throw new InvalidOperationException("Classification schema initialization failed.", result.Error);
        }
        finally
        {
            gate.CommandText = "EXEC sp_releaseapplock @Resource='NimBus.Intelligence.Migrations', @LockOwner='Session'";
            await gate.ExecuteNonQueryAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    protected override async Task<(ClassificationDocument Document, string? Version)> ReadAsync(string failureId, CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT StateJson,Version FROM dbo.FailureClassifications WHERE FailureMessageId=@id";
        command.Parameters.AddWithValue("@id", failureId);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false)
            ? (JsonConvert.DeserializeObject<ClassificationDocument>(reader.GetString(0))!, Convert.ToBase64String((byte[])reader[1]))
            : (new ClassificationDocument { FailureMessageId = failureId }, null);
    }

    protected override async Task<bool> TryWriteAsync(ClassificationDocument document, string? version, CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = version is null
            ? "INSERT INTO dbo.FailureClassifications(FailureMessageId,StateJson) VALUES(@id,@json)"
            : "UPDATE dbo.FailureClassifications SET StateJson=@json WHERE FailureMessageId=@id AND Version=@version";
        command.Parameters.AddWithValue("@id", document.FailureMessageId);
        command.Parameters.AddWithValue("@json", JsonConvert.SerializeObject(document));
        if (version is not null) command.Parameters.AddWithValue("@version", Convert.FromBase64String(version));
        try { return await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false) == 1; }
        catch (SqlException exception) when (exception.Number is 2601 or 2627) { return false; }
    }

    /// <summary>
    /// One round trip per batch: the ids travel as a single JSON array parameter, so the batch size
    /// is not limited by SQL Server's 2 100-parameter cap. A missing table raises a SqlException.
    /// </summary>
    protected override async Task<IReadOnlyList<ClassificationDocument>> ReadManyAsync(IReadOnlyList<string> failureIds, CancellationToken ct)
    {
        if (failureIds.Count == 0) return [];
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT c.StateJson FROM OPENJSON(@ids) WITH (Id nvarchar(200) '$') AS i " +
            "INNER JOIN dbo.FailureClassifications AS c ON c.FailureMessageId = i.Id";
        command.Parameters.Add("@ids", System.Data.SqlDbType.NVarChar, -1).Value = JsonConvert.SerializeObject(failureIds);
        var documents = new List<ClassificationDocument>(failureIds.Count);
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            documents.Add(JsonConvert.DeserializeObject<ClassificationDocument>(reader.GetString(0))!);
        return documents;
    }

    protected override async IAsyncEnumerable<ClassificationDocument> EnumerateAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await using var connection = new SqlConnection(_connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT StateJson FROM dbo.FailureClassifications WHERE JSON_VALUE(StateJson,'$.Deleted')='false'";
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        while (await reader.ReadAsync(ct).ConfigureAwait(false)) yield return JsonConvert.DeserializeObject<ClassificationDocument>(reader.GetString(0))!;
    }
}
