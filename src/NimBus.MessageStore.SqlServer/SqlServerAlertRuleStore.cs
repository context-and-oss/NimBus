using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// SQL Server implementation of <see cref="IAlertRuleStore"/> (Spec 053): one row per rule in
/// <c>AlertRules</c> and the evaluator lease as the single row of <c>AlertRuleEvaluator</c>
/// (migration 0029).
/// </summary>
/// <remarks>
/// <c>Version</c> is the logical version: every write sets it to the expected version plus one in
/// a statement whose <c>WHERE</c> clause compares it, so the rows-affected count is the
/// compare-and-set result. The state's transitions are stored apart from the rest of the state, so
/// <see cref="ListAlertRulesToEvaluate"/> never reads them.
/// </remarks>
internal sealed class SqlServerAlertRuleStore : IAlertRuleStore
{
    private const int PrimaryKeyViolation = 2627;
    private const int UniqueIndexViolation = 2601;
    private const int LastClaimedByLength = 200;

    private const string ColumnsWithoutTransitions =
        "EndpointId, RuleId, Name, Enabled, Version, DefinitionJson, StateJson, CreatedAtUtc, UpdatedAtUtc";

    private const string Columns = ColumnsWithoutTransitions + ", TransitionsJson";

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        DateTimeZoneHandling = DateTimeZoneHandling.Utc,
    };

    private readonly SqlServerStoreContext _context;

    public SqlServerAlertRuleStore(SqlServerStoreContext context) => _context = context;

    private string Rules => _context.Table("AlertRules");

    private string Evaluator => _context.Table("AlertRuleEvaluator");

    public async Task<IReadOnlyList<AlertRule>> ListAlertRulesByEndpoint(string endpointId)
    {
        await using var conn = await _context.Open();
        var rows = await conn.QueryAsync<Row>(
            $"SELECT {Columns} FROM {Rules} WHERE EndpointId = @EndpointId ORDER BY CreatedAtUtc, RuleId",
            new { EndpointId = endpointId },
            commandTimeout: _context.CommandTimeout);
        return rows.Select(r => r.ToRule()).ToList();
    }

    public async Task<IReadOnlyList<AlertRule>> ListAlertRulesToEvaluate(int limit)
    {
        if (limit <= 0)
            return [];

        await using var conn = await _context.Open();
        var rows = await conn.QueryAsync<Row>(
            $@"SELECT TOP (@Limit) {ColumnsWithoutTransitions} FROM {Rules}
               WHERE Enabled = 1 OR HasPendingNotices = 1
               ORDER BY EndpointId, RuleId",
            new { Limit = limit },
            commandTimeout: _context.CommandTimeout);
        return rows.Select(r => r.ToRule()).ToList();
    }

    public async Task<IReadOnlyList<string>> ListEndpointsWithFiringAlertRules()
    {
        await using var conn = await _context.Open();
        var endpoints = await conn.QueryAsync<string>(
            $"SELECT DISTINCT EndpointId FROM {Rules} WHERE Status = @Firing",
            new { Firing = nameof(AlertRuleStatus.Firing) },
            commandTimeout: _context.CommandTimeout);
        return endpoints.ToList();
    }

    public async Task<AlertRule?> GetAlertRule(string endpointId, string ruleId)
    {
        await using var conn = await _context.Open();
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            $"SELECT {Columns} FROM {Rules} WHERE EndpointId = @EndpointId AND RuleId = @RuleId",
            new { EndpointId = endpointId, RuleId = ruleId },
            commandTimeout: _context.CommandTimeout);
        return row?.ToRule();
    }

    public async Task<AlertRule?> TrySaveAlertRuleDefinition(AlertRule rule, long? expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.EndpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.RuleId);

        var stored = rule.Clone();
        stored.ETag = null;
        stored.Version = expectedVersion is null ? 1 : expectedVersion.Value + 1;
        var parameters = RuleParameters(stored);
        parameters.Add("Expected", expectedVersion ?? 0);

        // One statement per branch, so the rows-affected count is the compare-and-set result.
        // The insert's lock hints serialize two concurrent creates; the loser inserts nothing.
        var sql = expectedVersion is null
            ? $@"
INSERT INTO {Rules} (EndpointId, RuleId, Name, Enabled, Status, HasPendingNotices, Version, DefinitionJson, StateJson, TransitionsJson, CreatedAtUtc, UpdatedAtUtc)
SELECT @EndpointId, @RuleId, @Name, @Enabled, @Status, @HasPendingNotices, @Version, @DefinitionJson, @StateJson, @TransitionsJson, @CreatedAtUtc, @UpdatedAtUtc
WHERE NOT EXISTS (SELECT 1 FROM {Rules} WITH (UPDLOCK, HOLDLOCK) WHERE EndpointId = @EndpointId AND RuleId = @RuleId);"
            : $@"
UPDATE {Rules}
SET Name = @Name, Enabled = @Enabled, Status = @Status, HasPendingNotices = @HasPendingNotices, Version = @Version,
    DefinitionJson = @DefinitionJson, StateJson = @StateJson, TransitionsJson = @TransitionsJson,
    CreatedAtUtc = @CreatedAtUtc, UpdatedAtUtc = @UpdatedAtUtc
WHERE EndpointId = @EndpointId AND RuleId = @RuleId AND Version = @Expected;";

        try
        {
            await using var conn = await _context.Open();
            var rows = await conn.ExecuteAsync(sql, parameters, commandTimeout: _context.CommandTimeout);
            if (rows != 1)
                return null;
        }
        catch (SqlException e) when (e.Number is PrimaryKeyViolation or UniqueIndexViolation)
        {
            // A concurrent create won the primary key.
            return null;
        }

        return Normalize(stored.Clone());
    }

    public async Task<long?> TrySaveAlertRuleState(string endpointId, string ruleId, long expectedVersion, AlertRuleState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var parameters = new DynamicParameters();
        parameters.Add("EndpointId", endpointId);
        parameters.Add("RuleId", ruleId);
        parameters.Add("Expected", expectedVersion);
        parameters.Add("Version", expectedVersion + 1);
        AddStateParameters(parameters, state);

        await using var conn = await _context.Open();
        var rows = await conn.ExecuteAsync(
            $@"UPDATE {Rules}
               SET Status = @Status, HasPendingNotices = @HasPendingNotices, StateJson = @StateJson,
                   TransitionsJson = @TransitionsJson, Version = @Version
               WHERE EndpointId = @EndpointId AND RuleId = @RuleId AND Version = @Expected",
            parameters,
            commandTimeout: _context.CommandTimeout);
        return rows == 1 ? expectedVersion + 1 : null;
    }

    public async Task<bool> DeleteAlertRule(string endpointId, string ruleId, long expectedVersion)
    {
        await using var conn = await _context.Open();
        var rows = await conn.ExecuteAsync(
            $"DELETE FROM {Rules} WHERE EndpointId = @EndpointId AND RuleId = @RuleId AND Version = @Expected",
            new { EndpointId = endpointId, RuleId = ruleId, Expected = expectedVersion },
            commandTimeout: _context.CommandTimeout);
        return rows == 1;
    }

    public async Task<bool> TryClaimAlertRuleEvaluatorTick(DateTime dueBefore, DateTime nowUtc, string instanceId)
    {
        // One conditional statement: the rows-affected count is what lets exactly one of the
        // scaled-out instances hold a tick. HOLDLOCK serializes two first claims on the empty table.
        var sql = $@"
MERGE {Evaluator} WITH (HOLDLOCK) AS target
USING (SELECT CAST(1 AS TINYINT) AS Id) AS source
ON target.Id = source.Id
WHEN MATCHED AND (target.LastClaimedAtUtc IS NULL OR target.LastClaimedAtUtc < @DueBefore) THEN UPDATE SET
    LastClaimedAtUtc = @Now,
    LastClaimedBy = @InstanceId
WHEN NOT MATCHED THEN INSERT (Id, LastClaimedAtUtc, LastClaimedBy)
VALUES (1, @Now, @InstanceId);";

        var parameters = new DynamicParameters();
        parameters.Add("DueBefore", dueBefore, DbType.DateTime2);
        parameters.Add("Now", nowUtc, DbType.DateTime2);
        parameters.Add("InstanceId", Truncate(instanceId, LastClaimedByLength));

        try
        {
            await using var conn = await _context.Open();
            var rows = await conn.ExecuteAsync(sql, parameters, commandTimeout: _context.CommandTimeout);
            return rows == 1;
        }
        catch (SqlException e) when (e.Number is PrimaryKeyViolation or UniqueIndexViolation)
        {
            // A concurrent first claim inserted the row.
            return false;
        }
    }

    public async Task<bool> CompleteAlertRuleEvaluatorTick(DateTime claimedAtUtc, DateTime completedAtUtc)
    {
        var parameters = new DynamicParameters();
        parameters.Add("Claimed", claimedAtUtc, DbType.DateTime2);
        parameters.Add("Completed", completedAtUtc, DbType.DateTime2);

        await using var conn = await _context.Open();
        var rows = await conn.ExecuteAsync(
            $"UPDATE {Evaluator} SET LastCompletedAtUtc = @Completed WHERE Id = 1 AND LastClaimedAtUtc = @Claimed",
            parameters,
            commandTimeout: _context.CommandTimeout);
        return rows == 1;
    }

    public async Task<AlertRuleEvaluatorStatus> GetAlertRuleEvaluatorStatus()
    {
        await using var conn = await _context.Open();
        var row = await conn.QuerySingleOrDefaultAsync<EvaluatorRow>(
            $"SELECT LastClaimedAtUtc, LastClaimedBy, LastCompletedAtUtc FROM {Evaluator} WHERE Id = 1",
            commandTimeout: _context.CommandTimeout);
        return new AlertRuleEvaluatorStatus
        {
            LastClaimedAtUtc = Utc(row?.LastClaimedAtUtc),
            LastClaimedBy = row?.LastClaimedBy,
            LastCompletedAtUtc = Utc(row?.LastCompletedAtUtc),
        };
    }

    private static DynamicParameters RuleParameters(AlertRule rule)
    {
        var p = new DynamicParameters();
        p.Add("EndpointId", rule.EndpointId);
        p.Add("RuleId", rule.RuleId);
        p.Add("Name", rule.Name ?? string.Empty);
        p.Add("Enabled", rule.Enabled);
        p.Add("Version", rule.Version);
        p.Add("DefinitionJson", JsonConvert.SerializeObject(Definition.From(rule), JsonSettings));
        // Explicit DateTime2: SqlClient otherwise binds DateTime as legacy DATETIME (~3.33 ms rounding).
        p.Add("CreatedAtUtc", rule.CreatedAtUtc, DbType.DateTime2);
        p.Add("UpdatedAtUtc", rule.UpdatedAtUtc, DbType.DateTime2);
        AddStateParameters(p, rule.State ?? new AlertRuleState());
        return p;
    }

    private static void AddStateParameters(DynamicParameters p, AlertRuleState state)
    {
        var withoutTransitions = state.Clone();
        withoutTransitions.Transitions = [];
        p.Add("Status", state.Status.ToString());
        p.Add("HasPendingNotices", state.PendingNotices is { Count: > 0 });
        p.Add("StateJson", JsonConvert.SerializeObject(withoutTransitions, JsonSettings));
        p.Add("TransitionsJson", JsonConvert.SerializeObject(state.Transitions ?? [], JsonSettings));
    }

    private static string? Truncate(string? value, int length)
        => value is null || value.Length <= length ? value : value[..length];

    // datetime2 reads back as DateTimeKind.Unspecified and JSON may carry any kind; the values are UTC.
    private static AlertRule Normalize(AlertRule rule)
    {
        rule.CreatedAtUtc = Utc(rule.CreatedAtUtc);
        rule.UpdatedAtUtc = Utc(rule.UpdatedAtUtc);
        rule.Condition ??= new AlertRuleCondition();
        rule.Condition.EventTypes ??= [];
        rule.Condition.Statuses ??= [];
        rule.Targets ??= [];
        rule.State ??= new AlertRuleState();
        var state = rule.State;
        state.SinceUtc = Utc(state.SinceUtc);
        state.UpdatedAtUtc = Utc(state.UpdatedAtUtc);
        state.ConditionFalseSinceUtc = Utc(state.ConditionFalseSinceUtc);
        state.PendingNotices ??= [];
        foreach (var notice in state.PendingNotices)
        {
            notice.Target ??= new AlertRuleTarget();
            notice.SinceUtc = Utc(notice.SinceUtc);
            notice.CreatedAtUtc = Utc(notice.CreatedAtUtc);
        }

        state.Transitions ??= [];
        foreach (var transition in state.Transitions)
            transition.AtUtc = Utc(transition.AtUtc);
        return rule;
    }

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    private static DateTime? Utc(DateTime? value) => value is null ? null : Utc(value.Value);

    /// <summary>The definition fields that have no column of their own.</summary>
    private sealed class Definition
    {
        public AlertRuleCondition Condition { get; set; } = new();
        public int ResolveAfterMinutes { get; set; }
        public List<AlertRuleTarget> Targets { get; set; } = new();
        public string CreatedBy { get; set; } = string.Empty;
        public string? UpdatedBy { get; set; }

        public static Definition From(AlertRule rule) => new()
        {
            Condition = rule.Condition ?? new AlertRuleCondition(),
            ResolveAfterMinutes = rule.ResolveAfterMinutes,
            Targets = rule.Targets ?? [],
            CreatedBy = rule.CreatedBy ?? string.Empty,
            UpdatedBy = rule.UpdatedBy,
        };
    }

    private sealed class Row
    {
        public string EndpointId { get; set; } = string.Empty;
        public string RuleId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public long Version { get; set; }
        public string DefinitionJson { get; set; } = string.Empty;
        public string StateJson { get; set; } = string.Empty;
        public string? TransitionsJson { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }

        public AlertRule ToRule()
        {
            var definition = JsonConvert.DeserializeObject<Definition>(DefinitionJson, JsonSettings) ?? new Definition();
            var state = JsonConvert.DeserializeObject<AlertRuleState>(StateJson, JsonSettings) ?? new AlertRuleState();
            state.Transitions = string.IsNullOrEmpty(TransitionsJson)
                ? []
                : JsonConvert.DeserializeObject<List<AlertRuleTransition>>(TransitionsJson, JsonSettings) ?? [];

            return Normalize(new AlertRule
            {
                EndpointId = EndpointId,
                RuleId = RuleId,
                Name = Name,
                Enabled = Enabled,
                Condition = definition.Condition,
                ResolveAfterMinutes = definition.ResolveAfterMinutes,
                Targets = definition.Targets,
                CreatedBy = definition.CreatedBy,
                CreatedAtUtc = CreatedAtUtc,
                UpdatedBy = definition.UpdatedBy,
                UpdatedAtUtc = UpdatedAtUtc,
                Version = Version,
                State = state,
            });
        }
    }

    private sealed class EvaluatorRow
    {
        public DateTime? LastClaimedAtUtc { get; set; }
        public string? LastClaimedBy { get; set; }
        public DateTime? LastCompletedAtUtc { get; set; }
    }
}
