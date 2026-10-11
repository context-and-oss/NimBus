using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.SqlServer;

internal sealed class SqlServerEndpointMetadataStore : IEndpointMetadataStore
{
    private readonly SqlServerStoreContext _context;

    public SqlServerEndpointMetadataStore(SqlServerStoreContext context) => _context = context;

    private Task<SqlConnection> OpenAsync() => _context.Open();
    private string T(string table) => _context.Table(table);
    // ───────── Endpoint metadata ─────────

    public async Task<EndpointMetadata?> GetEndpointMetadata(string endpointId)
    {
        await using var conn = await OpenAsync();
        var row = await conn.QueryFirstOrDefaultAsync(
            $"SELECT * FROM {T("EndpointMetadata")} WHERE EndpointId = @E",
            new { E = endpointId }, commandTimeout: _context.CommandTimeout);
        if (row == null) return null;
        var metadata = MapMetadataRow(row);
        metadata.Heartbeats = await GetHeartbeats(conn, endpointId);
        return metadata;
    }

    public async Task<List<EndpointMetadata>> GetMetadatas()
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync($"SELECT * FROM {T("EndpointMetadata")}", commandTimeout: _context.CommandTimeout);
        var metadatas = rows.Select(MapMetadataRow).Cast<EndpointMetadata>().ToList();
        await PopulateHeartbeats(conn, metadatas);
        return metadatas;
    }

    public async Task<List<EndpointMetadata>?> GetMetadatas(IEnumerable<string> endpointIds)
    {
        var ids = endpointIds.ToArray();
        if (ids.Length == 0) return new List<EndpointMetadata>();
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("EndpointMetadata")} WHERE EndpointId IN @Ids",
            new { Ids = ids }, commandTimeout: _context.CommandTimeout);
        var metadatas = rows.Select(MapMetadataRow).Cast<EndpointMetadata>().ToList();
        await PopulateHeartbeats(conn, metadatas);
        return metadatas;
    }

    public async Task<bool> SetEndpointMetadata(EndpointMetadata endpointMetadata)
    {
        var sql = $@"
MERGE {T("EndpointMetadata")} AS target
USING (SELECT @EndpointId AS EndpointId) AS source
ON target.EndpointId = source.EndpointId
WHEN MATCHED THEN UPDATE SET
    EndpointOwner = @EndpointOwner,
    EndpointOwnerTeam = @EndpointOwnerTeam,
    EndpointOwnerEmail = @EndpointOwnerEmail,
    IsHeartbeatEnabled = @IsHeartbeatEnabled,
    EndpointHeartbeatStatus = @Status,
    TechnicalContactsJson = @TechnicalContactsJson,
    SubscriptionStatus = @SubscriptionStatus,
    UpdatedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (
    EndpointId, EndpointOwner, EndpointOwnerTeam, EndpointOwnerEmail,
    IsHeartbeatEnabled, EndpointHeartbeatStatus, TechnicalContactsJson, SubscriptionStatus)
VALUES (@EndpointId, @EndpointOwner, @EndpointOwnerTeam, @EndpointOwnerEmail,
    @IsHeartbeatEnabled, @Status, @TechnicalContactsJson, @SubscriptionStatus);";
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(sql, new
        {
            endpointMetadata.EndpointId,
            endpointMetadata.EndpointOwner,
            endpointMetadata.EndpointOwnerTeam,
            endpointMetadata.EndpointOwnerEmail,
            endpointMetadata.IsHeartbeatEnabled,
            Status = endpointMetadata.EndpointHeartbeatStatus?.ToString(),
            TechnicalContactsJson = JsonConvert.SerializeObject(endpointMetadata.TechnicalContacts ?? new List<TechnicalContact>()),
            endpointMetadata.SubscriptionStatus,
        }, commandTimeout: _context.CommandTimeout);
        return rows > 0;
    }

    private static EndpointMetadata MapMetadataRow(dynamic row) => new()
    {
        EndpointId = row.EndpointId,
        EndpointOwner = row.EndpointOwner,
        EndpointOwnerTeam = row.EndpointOwnerTeam,
        EndpointOwnerEmail = row.EndpointOwnerEmail,
        IsHeartbeatEnabled = row.IsHeartbeatEnabled,
        EndpointHeartbeatStatus = Enum.TryParse((string?)row.EndpointHeartbeatStatus, out HeartbeatStatus rollup)
            ? rollup
            : (HeartbeatStatus?)null,
        TechnicalContacts = string.IsNullOrEmpty((string?)row.TechnicalContactsJson)
            ? new List<TechnicalContact>()
            : JsonConvert.DeserializeObject<List<TechnicalContact>>((string)row.TechnicalContactsJson) ?? new List<TechnicalContact>(),
        Heartbeats = new List<Heartbeat>(),
        SubscriptionStatus = row.SubscriptionStatus,
    };

    // ───────── Endpoint heartbeat ─────────

    public async Task<List<EndpointMetadata>> GetMetadatasWithEnabledHeartbeat()
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(
            $"SELECT * FROM {T("EndpointMetadata")} WHERE IsHeartbeatEnabled = 1",
            commandTimeout: _context.CommandTimeout);
        return rows.Select(MapMetadataRow).Cast<EndpointMetadata>().ToList();
    }

    public async Task EnableHeartbeatOnEndpoint(string endpointId, bool enable)
    {
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(
            $@"MERGE {T("EndpointMetadata")} AS target
               USING (SELECT @EndpointId AS EndpointId) AS source ON target.EndpointId = source.EndpointId
               WHEN MATCHED THEN UPDATE SET IsHeartbeatEnabled = @Enable, UpdatedAtUtc = SYSUTCDATETIME()
               WHEN NOT MATCHED THEN INSERT (EndpointId, IsHeartbeatEnabled) VALUES (@EndpointId, @Enable);",
            new { EndpointId = endpointId, Enable = enable }, commandTimeout: _context.CommandTimeout);
    }

    public async Task<bool> SetHeartbeat(Heartbeat heartbeat, string endpointId)
    {
        ArgumentNullException.ThrowIfNull(heartbeat);

        // MERGE on (EndpointId, MessageId): the Pending probe and the answer that
        // settles it share one row instead of accumulating a duplicate.
        var sql = $@"
MERGE {T("Heartbeats")} AS target
USING (SELECT @EndpointId AS EndpointId, @MessageId AS MessageId) AS source
ON target.EndpointId = source.EndpointId
   AND ((target.MessageId = source.MessageId) OR (target.MessageId IS NULL AND source.MessageId IS NULL))
WHEN MATCHED THEN UPDATE SET
    StartTimeUtc = @StartTime,
    ReceivedTimeUtc = @ReceivedTime,
    EndTimeUtc = @EndTime,
    EndpointHeartbeatStatus = @Status,
    SdkVersion = @SdkVersion,
    IntervalSeconds = CASE WHEN @IntervalSeconds > 0 THEN @IntervalSeconds ELSE target.IntervalSeconds END
WHEN NOT MATCHED THEN INSERT (EndpointId, MessageId, StartTimeUtc, ReceivedTimeUtc, EndTimeUtc, EndpointHeartbeatStatus, SdkVersion, IntervalSeconds)
VALUES (@EndpointId, @MessageId, @StartTime, @ReceivedTime, @EndTime, @Status, @SdkVersion, @IntervalSeconds);

WITH ranked AS (
    SELECT Id,
           ROW_NUMBER() OVER (PARTITION BY EndpointId ORDER BY StartTimeUtc DESC, Id DESC) AS rn
    FROM {T("Heartbeats")}
    WHERE EndpointId = @EndpointId
)
DELETE FROM ranked
WHERE rn > @MaxHeartbeats;

MERGE {T("EndpointMetadata")} AS target
USING (SELECT @EndpointId AS EndpointId) AS source
ON target.EndpointId = source.EndpointId
WHEN NOT MATCHED THEN INSERT (EndpointId, EndpointHeartbeatStatus)
VALUES (@EndpointId, @Status);

{RollupSql("m.EndpointId = @EndpointId")}";
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(sql, new
        {
            EndpointId = endpointId,
            heartbeat.MessageId,
            heartbeat.StartTime,
            heartbeat.ReceivedTime,
            heartbeat.EndTime,
            Status = heartbeat.EndpointHeartbeatStatus.ToString(),
            heartbeat.SdkVersion,
            heartbeat.IntervalSeconds,
            MaxHeartbeats = HeartbeatRollup.MaxHeartbeatsPerEndpoint,
            PendingStatus = nameof(HeartbeatStatus.Pending),
        }, commandTimeout: _context.CommandTimeout);
        return rows > 0;
    }

    /// <summary>
    /// Rollup mirrors the most recent settled probe (On/Off/Unsupported); an
    /// in-flight Pending must not mask the last known outcome. Pending only
    /// before the first settled result. Expects @PendingStatus as a parameter.
    /// </summary>
    private string RollupSql(string endpointFilter) => $@"
UPDATE m
SET EndpointHeartbeatStatus = COALESCE(
        settled.EndpointHeartbeatStatus,
        CASE WHEN EXISTS (SELECT 1 FROM {T("Heartbeats")} h WHERE h.EndpointId = m.EndpointId)
             THEN @PendingStatus END,
        m.EndpointHeartbeatStatus),
    UpdatedAtUtc = SYSUTCDATETIME()
FROM {T("EndpointMetadata")} m
OUTER APPLY (
    SELECT TOP 1 h.EndpointHeartbeatStatus
    FROM {T("Heartbeats")} h
    WHERE h.EndpointId = m.EndpointId
      AND h.EndpointHeartbeatStatus <> @PendingStatus
    ORDER BY h.StartTimeUtc DESC, h.Id DESC
) settled
WHERE {endpointFilter};";

    public async Task<List<string>> SweepTimedOutHeartbeats(DateTime cutoffUtc)
    {
        var sql = $@"
DECLARE @swept TABLE (EndpointId NVARCHAR(200));

UPDATE {T("Heartbeats")}
SET EndpointHeartbeatStatus = @OffStatus
OUTPUT inserted.EndpointId INTO @swept
WHERE EndpointHeartbeatStatus = @PendingStatus
  AND StartTimeUtc <= @Cutoff;

{RollupSql("m.EndpointId IN (SELECT DISTINCT EndpointId FROM @swept)")}

SELECT DISTINCT EndpointId FROM @swept;";
        await using var conn = await OpenAsync();
        var swept = await conn.QueryAsync<string>(sql, new
        {
            Cutoff = cutoffUtc,
            OffStatus = nameof(HeartbeatStatus.Off),
            PendingStatus = nameof(HeartbeatStatus.Pending),
        }, commandTimeout: _context.CommandTimeout);
        return swept.ToList();
    }

    public async Task<HeartbeatSettings> GetHeartbeatSettings()
    {
        await using var conn = await OpenAsync();
        var row = await conn.QueryFirstOrDefaultAsync(
            $@"SELECT TOP 1 Id, Enabled, IntervalSeconds, TimeoutSeconds, LastSentAtUtc, LastHeartbeatFoldAtUtc
               FROM {T("HeartbeatSettings")}
               WHERE Id = @Id",
            new { Id = HeartbeatSettings.SingletonId },
            commandTimeout: _context.CommandTimeout);

        return row == null
            ? new HeartbeatSettings()
            : new HeartbeatSettings
            {
                Id = row.Id,
                Enabled = row.Enabled,
                IntervalSeconds = row.IntervalSeconds,
                TimeoutSeconds = row.TimeoutSeconds,
                LastSentAtUtc = row.LastSentAtUtc,
                LastHeartbeatFoldAtUtc = row.LastHeartbeatFoldAtUtc,
            };
    }

    public async Task<bool> SetHeartbeatSettings(HeartbeatSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.Id)) settings.Id = HeartbeatSettings.SingletonId;

        // COALESCE on LastSentAtUtc: the claim owns that field, so an operator edit
        // that carries no value must not reset the send schedule.
        var sql = $@"
MERGE {T("HeartbeatSettings")} AS target
USING (SELECT @Id AS Id) AS source
ON target.Id = source.Id
WHEN MATCHED THEN UPDATE SET
    Enabled = @Enabled,
    IntervalSeconds = @IntervalSeconds,
    TimeoutSeconds = @TimeoutSeconds,
    LastSentAtUtc = COALESCE(@LastSentAtUtc, target.LastSentAtUtc),
    LastHeartbeatFoldAtUtc = COALESCE(@LastHeartbeatFoldAtUtc, target.LastHeartbeatFoldAtUtc),
    UpdatedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (Id, Enabled, IntervalSeconds, TimeoutSeconds, LastSentAtUtc, LastHeartbeatFoldAtUtc)
VALUES (@Id, @Enabled, @IntervalSeconds, @TimeoutSeconds, @LastSentAtUtc, @LastHeartbeatFoldAtUtc);";
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(sql, settings, commandTimeout: _context.CommandTimeout);
        return rows > 0;
    }

    public async Task<AuditSettings> GetAuditSettings()
    {
        await using var conn = await OpenAsync();
        var json = await conn.QuerySingleOrDefaultAsync<string?>(
            $"SELECT DisabledAuditTypes FROM {T("AuditSettings")} WHERE Id = @Id",
            new { Id = AuditSettings.SingletonId },
            commandTimeout: _context.CommandTimeout);

        return new AuditSettings
        {
            DisabledAuditTypes = string.IsNullOrEmpty(json)
                ? new List<string>()
                : JsonConvert.DeserializeObject<List<string>>(json) ?? new List<string>(),
        };
    }

    public async Task<bool> SetAuditSettings(AuditSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.Id)) settings.Id = AuditSettings.SingletonId;

        var sql = $@"
MERGE {T("AuditSettings")} AS target
USING (SELECT @Id AS Id) AS source
ON target.Id = source.Id
WHEN MATCHED THEN UPDATE SET
    DisabledAuditTypes = @DisabledAuditTypes,
    UpdatedAtUtc = SYSUTCDATETIME()
WHEN NOT MATCHED THEN INSERT (Id, DisabledAuditTypes)
VALUES (@Id, @DisabledAuditTypes);";
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(sql, new
        {
            settings.Id,
            DisabledAuditTypes = JsonConvert.SerializeObject(settings.DisabledAuditTypes ?? new List<string>()),
        }, commandTimeout: _context.CommandTimeout);
        return rows > 0;
    }

    public async Task<McpAccessSettings> GetMcpAccessSettings()
    {
        await using var conn = await OpenAsync();
        var json = await conn.QuerySingleOrDefaultAsync<string?>(
            $"SELECT SettingsJson FROM {T("McpAccessSettings")} WHERE Id = @Id",
            new { Id = McpAccessSettings.SingletonId },
            commandTimeout: _context.CommandTimeout);

        return string.IsNullOrEmpty(json)
            ? new McpAccessSettings()
            : JsonConvert.DeserializeObject<McpAccessSettings>(json) ?? new McpAccessSettings();
    }

    public async Task<bool> TrySetMcpAccessSettings(McpAccessSettings settings, string? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.Revision))
            throw new ArgumentException("The policy must carry its new revision.", nameof(settings));
        settings.Id = McpAccessSettings.SingletonId;

        // One statement per branch, so the rows-affected count is the compare-and-set result.
        // The insert's lock hints serialize two first saves; the loser inserts nothing.
        var sql = expectedRevision is null
            ? $@"
INSERT INTO {T("McpAccessSettings")} (Id, Revision, SettingsJson)
SELECT @Id, @Revision, @Json
WHERE NOT EXISTS (SELECT 1 FROM {T("McpAccessSettings")} WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id);"
            : $@"
UPDATE {T("McpAccessSettings")}
SET Revision = @Revision, SettingsJson = @Json, UpdatedAtUtc = SYSUTCDATETIME()
WHERE Id = @Id AND Revision = @Expected;";

        await using var conn = await OpenAsync();
        try
        {
            var rows = await conn.ExecuteAsync(sql, new
            {
                settings.Id,
                settings.Revision,
                Json = JsonConvert.SerializeObject(settings),
                Expected = expectedRevision,
            }, commandTimeout: _context.CommandTimeout);
            return rows == 1;
        }
        catch (SqlException e) when (e.Number is 2627 or 2601)
        {
            // A concurrent first save won the primary key.
            return false;
        }
    }

    public async Task<AgentApiKeySettings> GetAgentApiKeySettings()
    {
        await using var conn = await OpenAsync();
        var json = await conn.QuerySingleOrDefaultAsync<string?>(
            $"SELECT SettingsJson FROM {T("AgentApiKeys")} WHERE Id = @Id",
            new { Id = AgentApiKeySettings.SingletonId },
            commandTimeout: _context.CommandTimeout);

        return string.IsNullOrEmpty(json)
            ? new AgentApiKeySettings()
            : JsonConvert.DeserializeObject<AgentApiKeySettings>(json) ?? new AgentApiKeySettings();
    }

    public async Task<bool> TrySetAgentApiKeySettings(AgentApiKeySettings settings, string? expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (string.IsNullOrWhiteSpace(settings.Revision))
            throw new ArgumentException("The keys must carry their new revision.", nameof(settings));
        settings.Id = AgentApiKeySettings.SingletonId;

        // Same compare-and-set shape as TrySetMcpAccessSettings.
        var sql = expectedRevision is null
            ? $@"
INSERT INTO {T("AgentApiKeys")} (Id, Revision, SettingsJson)
SELECT @Id, @Revision, @Json
WHERE NOT EXISTS (SELECT 1 FROM {T("AgentApiKeys")} WITH (UPDLOCK, HOLDLOCK) WHERE Id = @Id);"
            : $@"
UPDATE {T("AgentApiKeys")}
SET Revision = @Revision, SettingsJson = @Json, UpdatedAtUtc = SYSUTCDATETIME()
WHERE Id = @Id AND Revision = @Expected;";

        await using var conn = await OpenAsync();
        try
        {
            var rows = await conn.ExecuteAsync(sql, new
            {
                settings.Id,
                settings.Revision,
                Json = JsonConvert.SerializeObject(settings),
                Expected = expectedRevision,
            }, commandTimeout: _context.CommandTimeout);
            return rows == 1;
        }
        catch (SqlException e) when (e.Number is 2627 or 2601)
        {
            // A concurrent first save won the primary key.
            return false;
        }
    }

    public async Task<List<AgentApiKeyUsage>> GetAgentApiKeyUsage()
    {
        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync<AgentApiKeyUsage>(
            $"SELECT KeyId, LastUsedAtUtc FROM {T("AgentApiKeyUsage")}",
            commandTimeout: _context.CommandTimeout);
        return rows.ToList();
    }

    public async Task RecordAgentApiKeyUse(string keyId, DateTime usedAtUtc)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyId);
        var sql = $@"
MERGE {T("AgentApiKeyUsage")} WITH (HOLDLOCK) AS target
USING (SELECT @KeyId AS KeyId) AS source
ON target.KeyId = source.KeyId
WHEN MATCHED THEN UPDATE SET LastUsedAtUtc = @UsedAtUtc
WHEN NOT MATCHED THEN INSERT (KeyId, LastUsedAtUtc) VALUES (@KeyId, @UsedAtUtc);";
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync(sql, new { KeyId = keyId, UsedAtUtc = usedAtUtc }, commandTimeout: _context.CommandTimeout);
    }

    public async Task<bool> TryClaimHeartbeatSend(DateTime dueBefore)
    {
        // The rows-affected check is what makes at most one scaled-out instance
        // send per interval.
        await using var conn = await OpenAsync();
        var rows = await conn.ExecuteAsync(
            $@"UPDATE {T("HeartbeatSettings")}
               SET LastSentAtUtc = SYSUTCDATETIME(),
                   UpdatedAtUtc = SYSUTCDATETIME()
               WHERE Id = @Id
                 AND Enabled = 1
                 AND (LastSentAtUtc IS NULL OR LastSentAtUtc <= @DueBefore)",
            new { Id = HeartbeatSettings.SingletonId, DueBefore = dueBefore },
            commandTimeout: _context.CommandTimeout);
        return rows == 1;
    }

    public async Task<List<HeartbeatOverviewItem>> GetHeartbeatOverview()
    {
        // Status = last settled outcome (On/Off/Unsupported); an in-flight Pending
        // probe never masks it. Response fields (round-trip, last seen, SDK version)
        // come from the last actual response — a swept/timed-out row carried none.
        // Mirrors HeartbeatRollup.BuildOverviewItem, which the document-shaped
        // providers use.
        var sql = $@"
WITH latest AS (
    SELECT EndpointId,
           MessageId,
           StartTimeUtc,
           ROW_NUMBER() OVER (PARTITION BY EndpointId ORDER BY StartTimeUtc DESC, Id DESC) AS rn
    FROM {T("Heartbeats")}
),
settled AS (
    SELECT EndpointId,
           EndpointHeartbeatStatus,
           ROW_NUMBER() OVER (PARTITION BY EndpointId ORDER BY StartTimeUtc DESC, Id DESC) AS rn
    FROM {T("Heartbeats")}
    WHERE EndpointHeartbeatStatus <> @PendingStatus
),
responded AS (
    SELECT EndpointId,
           StartTimeUtc,
           ReceivedTimeUtc,
           EndTimeUtc,
           SdkVersion,
           ROW_NUMBER() OVER (PARTITION BY EndpointId ORDER BY StartTimeUtc DESC, Id DESC) AS rn
    FROM {T("Heartbeats")}
    WHERE EndpointHeartbeatStatus IN (@OnStatus, @UnsupportedStatus)
)
SELECT m.EndpointId,
       m.IsHeartbeatEnabled,
       l.MessageId,
       l.StartTimeUtc AS LastStartTime,
       r.ReceivedTimeUtc AS LastReceivedTime,
       r.EndTimeUtc AS LastEndTime,
       CASE
           WHEN r.StartTimeUtc IS NULL OR r.EndTimeUtc IS NULL THEN NULL
           ELSE DATEDIFF_BIG(millisecond, r.StartTimeUtc, r.EndTimeUtc)
       END AS RoundTripMs,
       r.SdkVersion,
       COALESCE(s.EndpointHeartbeatStatus,
                CASE WHEN l.EndpointId IS NOT NULL THEN @PendingStatus END,
                m.EndpointHeartbeatStatus,
                @UnknownStatus) AS Status
FROM {T("EndpointMetadata")} AS m
LEFT JOIN latest AS l
    ON l.EndpointId = m.EndpointId AND l.rn = 1
LEFT JOIN settled AS s
    ON s.EndpointId = m.EndpointId AND s.rn = 1
LEFT JOIN responded AS r
    ON r.EndpointId = m.EndpointId AND r.rn = 1
ORDER BY m.EndpointId";

        await using var conn = await OpenAsync();
        var rows = await conn.QueryAsync(sql, new
        {
            PendingStatus = nameof(HeartbeatStatus.Pending),
            OnStatus = nameof(HeartbeatStatus.On),
            UnsupportedStatus = nameof(HeartbeatStatus.Unsupported),
            UnknownStatus = nameof(HeartbeatStatus.Unknown),
        }, commandTimeout: _context.CommandTimeout);
        return rows.Select(row => new HeartbeatOverviewItem
        {
            EndpointId = row.EndpointId,
            IsHeartbeatEnabled = row.IsHeartbeatEnabled,
            MessageId = row.MessageId ?? string.Empty,
            LastStartTime = row.LastStartTime,
            LastReceivedTime = row.LastReceivedTime,
            LastEndTime = row.LastEndTime,
            RoundTripMs = row.RoundTripMs == null ? null : (long?)row.RoundTripMs,
            SdkVersion = row.SdkVersion ?? string.Empty,
            Status = Enum.TryParse((string?)row.Status, out HeartbeatStatus status)
                ? status
                : HeartbeatStatus.Unknown,
        }).Cast<HeartbeatOverviewItem>().ToList();
    }

    private async Task<List<Heartbeat>> GetHeartbeats(SqlConnection conn, string endpointId)
    {
        var rows = await conn.QueryAsync(
            $@"SELECT MessageId, StartTimeUtc, ReceivedTimeUtc, EndTimeUtc, EndpointHeartbeatStatus, SdkVersion, IntervalSeconds
               FROM {T("Heartbeats")}
               WHERE EndpointId = @EndpointId
               ORDER BY StartTimeUtc",
            new { EndpointId = endpointId },
            commandTimeout: _context.CommandTimeout);

        return rows.Select(MapHeartbeatRow).Cast<Heartbeat>().ToList();
    }

    private async Task PopulateHeartbeats(SqlConnection conn, IReadOnlyCollection<EndpointMetadata> metadatas)
    {
        if (metadatas.Count == 0) return;

        var endpointIds = metadatas.Select(metadata => metadata.EndpointId).ToArray();
        var rows = await conn.QueryAsync(
            $@"SELECT EndpointId, MessageId, StartTimeUtc, ReceivedTimeUtc, EndTimeUtc, EndpointHeartbeatStatus, SdkVersion, IntervalSeconds
               FROM {T("Heartbeats")}
               WHERE EndpointId IN @EndpointIds
               ORDER BY EndpointId, StartTimeUtc",
            new { EndpointIds = endpointIds },
            commandTimeout: _context.CommandTimeout);
        var byEndpoint = rows
            .Select(row => (EndpointId: (string)row.EndpointId, Heartbeat: (Heartbeat)MapHeartbeatRow(row)))
            .GroupBy(row => row.EndpointId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.Select(row => row.Heartbeat).ToList(),
                StringComparer.OrdinalIgnoreCase);

        foreach (var metadata in metadatas)
        {
            metadata.Heartbeats = byEndpoint.TryGetValue(metadata.EndpointId, out var heartbeats)
                ? heartbeats
                : [];
        }
    }

    private static Heartbeat MapHeartbeatRow(dynamic row) => new()
    {
        MessageId = row.MessageId,
        StartTime = row.StartTimeUtc,
        ReceivedTime = row.ReceivedTimeUtc,
        EndTime = row.EndTimeUtc,
        SdkVersion = row.SdkVersion,
        IntervalSeconds = row.IntervalSeconds,
        EndpointHeartbeatStatus = Enum.TryParse((string?)row.EndpointHeartbeatStatus, out HeartbeatStatus status)
            ? status
            : HeartbeatStatus.Unknown,
    };


}
