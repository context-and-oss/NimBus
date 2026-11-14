using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.SqlServer;

/// <summary>
/// SQL Server implementation of <see cref="IOperatorOperationStore"/>: one row per operation in
/// <c>OperatorOperations</c> (migration 0026). <c>Version</c> is a GUID rewritten on every update
/// and compared in the <c>WHERE</c> clause.
/// </summary>
internal sealed class SqlServerOperatorOperationStore : IOperatorOperationStore
{
    private const int PrimaryKeyViolation = 2627;
    private const int UniqueIndexViolation = 2601;

    private const string Columns =
        "OperationId, TenantId, Caller, ClientId, IsWorkload, ActorName, IdempotencyKey, ArgumentsHash, Action, " +
        "EndpointId, EventId, SessionId, SourceMessageId, MessageVersion, Reason, Status, ObservedStatus, " +
        "EvidenceMessageId, ObservedAtUtc, Detail, LeaseOwner, LeaseUntilUtc, DispatchedAtUtc, CreatedAtUtc, " +
        "UpdatedAtUtc, ExpiresAtUtc, Version";

    private readonly SqlServerStoreContext _context;

    public SqlServerOperatorOperationStore(SqlServerStoreContext context) => _context = context;

    private string Table => _context.Table("OperatorOperations");

    public async Task<OperatorOperationCreateResult> TryCreateOperatorOperation(OperatorOperation operation)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.OperationId);

        var version = NewVersion();
        var parameters = Parameters(operation, version);
        try
        {
            await using var conn = await _context.Open();
            await conn.ExecuteAsync(
                $"INSERT INTO {Table} ({Columns}) VALUES ({string.Join(", ", Columns.Split(", ").Select(c => "@" + c))})",
                parameters,
                commandTimeout: _context.CommandTimeout);
        }
        catch (SqlException e) when (e.Number is PrimaryKeyViolation or UniqueIndexViolation)
        {
            var existing = await GetOperatorOperation(operation.OperationId)
                ?? throw new InvalidOperationException($"Operation '{operation.OperationId}' collided on insert but cannot be read.");
            return new OperatorOperationCreateResult(false, existing);
        }

        operation.Version = version;
        var stored = operation.Clone();
        return new OperatorOperationCreateResult(true, stored);
    }

    public async Task<OperatorOperation?> GetOperatorOperation(string operationId)
    {
        await using var conn = await _context.Open();
        var row = await conn.QuerySingleOrDefaultAsync<Row>(
            $"SELECT {Columns} FROM {Table} WHERE OperationId = @OperationId",
            new { OperationId = operationId },
            commandTimeout: _context.CommandTimeout);
        return row?.ToOperation();
    }

    public async Task<bool> TryUpdateOperatorOperation(OperatorOperation operation, string expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(operation);
        var version = NewVersion();
        var parameters = Parameters(operation, version);
        parameters.Add("ExpectedVersion", expectedVersion);

        var assignments = string.Join(", ", Columns.Split(", ").Where(c => c != "OperationId").Select(c => $"{c} = @{c}"));
        await using var conn = await _context.Open();
        var rows = await conn.ExecuteAsync(
            $"UPDATE {Table} SET {assignments} WHERE OperationId = @OperationId AND Version = @ExpectedVersion",
            parameters,
            commandTimeout: _context.CommandTimeout);
        if (rows == 0)
            return false;

        operation.Version = version;
        return true;
    }

    public async Task<IReadOnlyList<OperatorOperation>> ListDueOperatorOperations(
        IReadOnlyCollection<OperatorOperationStatus> statuses, DateTime updatedBeforeUtc, int limit)
    {
        if (statuses.Count == 0 || limit <= 0)
            return [];

        var parameters = new DynamicParameters();
        parameters.Add("Limit", limit);
        parameters.Add("UpdatedBefore", updatedBeforeUtc, DbType.DateTime2);
        parameters.Add("Statuses", statuses.Select(s => s.ToString()).ToArray());

        await using var conn = await _context.Open();
        var rows = await conn.QueryAsync<Row>(
            $@"SELECT TOP (@Limit) {Columns} FROM {Table}
               WHERE Status IN @Statuses AND UpdatedAtUtc < @UpdatedBefore
               ORDER BY UpdatedAtUtc",
            parameters,
            commandTimeout: _context.CommandTimeout);
        return rows.Select(r => r.ToOperation()).ToList();
    }

    public async Task<int> CountOperatorOperations(OperatorOperationQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        var where = new StringBuilder("1 = 1");
        var parameters = new DynamicParameters();
        void Filter(string? value, string column)
        {
            if (value is null) return;
            where.Append($" AND {column} = @{column}");
            parameters.Add(column, value);
        }

        Filter(query.Action?.ToString(), "Action");
        Filter(query.TenantId, "TenantId");
        Filter(query.ClientId, "ClientId");
        Filter(query.EndpointId, "EndpointId");
        Filter(query.EventId, "EventId");
        if (query.IsWorkload is { } workload)
        {
            where.Append(" AND IsWorkload = @IsWorkload");
            parameters.Add("IsWorkload", workload);
        }

        if (query.CreatedFromUtc is { } from)
        {
            where.Append(" AND CreatedAtUtc >= @CreatedFrom");
            parameters.Add("CreatedFrom", from, DbType.DateTime2);
        }

        if (query.ExcludedStatuses.Count > 0)
        {
            where.Append(" AND Status NOT IN @ExcludedStatuses");
            parameters.Add("ExcludedStatuses", query.ExcludedStatuses.Select(s => s.ToString()).ToArray());
        }

        if (query.ExcludedOperationId is not null)
        {
            where.Append(" AND OperationId <> @ExcludedOperationId");
            parameters.Add("ExcludedOperationId", query.ExcludedOperationId);
        }

        await using var conn = await _context.Open();
        return await conn.QuerySingleAsync<int>(
            $"SELECT COUNT(*) FROM {Table} WHERE {where}",
            parameters,
            commandTimeout: _context.CommandTimeout);
    }

    public async Task<int> PurgeExpiredOperatorOperations(DateTime nowUtc, int limit)
    {
        if (limit <= 0)
            return 0;

        var parameters = new DynamicParameters();
        parameters.Add("Limit", limit);
        parameters.Add("Now", nowUtc, DbType.DateTime2);
        await using var conn = await _context.Open();
        return await conn.ExecuteAsync(
            $"DELETE TOP (@Limit) FROM {Table} WHERE ExpiresAtUtc < @Now",
            parameters,
            commandTimeout: _context.CommandTimeout);
    }

    private static DynamicParameters Parameters(OperatorOperation o, string version)
    {
        var p = new DynamicParameters();
        p.Add("OperationId", o.OperationId);
        p.Add("TenantId", o.TenantId ?? string.Empty);
        p.Add("Caller", o.Caller ?? string.Empty);
        p.Add("ClientId", o.ClientId);
        p.Add("IsWorkload", o.IsWorkload);
        p.Add("ActorName", o.ActorName);
        p.Add("IdempotencyKey", o.IdempotencyKey ?? string.Empty);
        p.Add("ArgumentsHash", o.ArgumentsHash ?? string.Empty);
        p.Add("Action", o.Action.ToString());
        p.Add("EndpointId", o.EndpointId ?? string.Empty);
        p.Add("EventId", o.EventId ?? string.Empty);
        p.Add("SessionId", o.SessionId ?? string.Empty);
        p.Add("SourceMessageId", o.SourceMessageId ?? string.Empty);
        p.Add("MessageVersion", o.MessageVersion ?? string.Empty);
        p.Add("Reason", o.Reason ?? string.Empty);
        p.Add("Status", o.Status.ToString());
        p.Add("ObservedStatus", o.ObservedStatus);
        p.Add("EvidenceMessageId", o.EvidenceMessageId);
        // Explicit DateTime2: SqlClient otherwise binds DateTime as legacy DATETIME (~3.33 ms rounding).
        p.Add("ObservedAtUtc", o.ObservedAtUtc, DbType.DateTime2);
        p.Add("Detail", o.Detail);
        p.Add("LeaseOwner", o.LeaseOwner);
        p.Add("LeaseUntilUtc", o.LeaseUntilUtc, DbType.DateTime2);
        p.Add("DispatchedAtUtc", o.DispatchedAtUtc, DbType.DateTime2);
        p.Add("CreatedAtUtc", o.CreatedAtUtc, DbType.DateTime2);
        p.Add("UpdatedAtUtc", o.UpdatedAtUtc, DbType.DateTime2);
        p.Add("ExpiresAtUtc", o.ExpiresAtUtc, DbType.DateTime2);
        p.Add("Version", version);
        return p;
    }

    private static string NewVersion() => Guid.NewGuid().ToString("N");

    private static DateTime Utc(DateTime value) => DateTime.SpecifyKind(value, DateTimeKind.Utc);

    private static DateTime? Utc(DateTime? value) => value is null ? null : Utc(value.Value);

    private sealed class Row
    {
        public string OperationId { get; set; } = string.Empty;
        public string TenantId { get; set; } = string.Empty;
        public string Caller { get; set; } = string.Empty;
        public string? ClientId { get; set; }
        public bool IsWorkload { get; set; }
        public string? ActorName { get; set; }
        public string IdempotencyKey { get; set; } = string.Empty;
        public string ArgumentsHash { get; set; } = string.Empty;
        public string Action { get; set; } = string.Empty;
        public string EndpointId { get; set; } = string.Empty;
        public string EventId { get; set; } = string.Empty;
        public string SessionId { get; set; } = string.Empty;
        public string SourceMessageId { get; set; } = string.Empty;
        public string MessageVersion { get; set; } = string.Empty;
        public string Reason { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string? ObservedStatus { get; set; }
        public string? EvidenceMessageId { get; set; }
        public DateTime? ObservedAtUtc { get; set; }
        public string? Detail { get; set; }
        public string? LeaseOwner { get; set; }
        public DateTime? LeaseUntilUtc { get; set; }
        public DateTime? DispatchedAtUtc { get; set; }
        public DateTime CreatedAtUtc { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public DateTime ExpiresAtUtc { get; set; }
        public string Version { get; set; } = string.Empty;

        // datetime2 reads back as DateTimeKind.Unspecified; the columns hold UTC.
        public OperatorOperation ToOperation() => new()
        {
            OperationId = OperationId,
            TenantId = TenantId,
            Caller = Caller,
            ClientId = ClientId,
            IsWorkload = IsWorkload,
            ActorName = ActorName,
            IdempotencyKey = IdempotencyKey,
            ArgumentsHash = ArgumentsHash,
            Action = Enum.Parse<OperatorOperationAction>(Action),
            EndpointId = EndpointId,
            EventId = EventId,
            SessionId = SessionId,
            SourceMessageId = SourceMessageId,
            MessageVersion = MessageVersion,
            Reason = Reason,
            Status = Enum.Parse<OperatorOperationStatus>(Status),
            ObservedStatus = ObservedStatus,
            EvidenceMessageId = EvidenceMessageId,
            ObservedAtUtc = Utc(ObservedAtUtc),
            Detail = Detail,
            LeaseOwner = LeaseOwner,
            LeaseUntilUtc = Utc(LeaseUntilUtc),
            DispatchedAtUtc = Utc(DispatchedAtUtc),
            CreatedAtUtc = Utc(CreatedAtUtc),
            UpdatedAtUtc = Utc(UpdatedAtUtc),
            ExpiresAtUtc = Utc(ExpiresAtUtc),
            Version = Version,
        };
    }
}
