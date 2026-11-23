using Microsoft.Azure.Cosmos;
using Newtonsoft.Json;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.MessageStore;

/// <summary>
/// Cosmos DB implementation of <see cref="IAlertRuleStore"/> (Spec 053): one document per rule in
/// the <c>alertrules</c> container (id = rule id, partition key <c>/endpointId</c>), plus the
/// evaluator lease as one document under a reserved partition that is never an endpoint id.
/// </summary>
/// <remarks>
/// The logical <see cref="AlertRule.Version"/> lives in the document. Every conditional write reads
/// the document, compares the version, and writes with <c>IfMatchEtag</c> set to the ETag read, so
/// a write racing between the comparison and the write fails too. Cosmos compares DateTime strings
/// lexically, so the lease's due check reads the document and compares in code.
/// </remarks>
internal sealed class CosmosDbAlertRuleStore : IAlertRuleStore
{
    /// <summary>The partition that holds the evaluator lease; never a valid endpoint id.</summary>
    internal const string EvaluatorPartition = "__nimbus_evaluator__";

    /// <summary>The evaluator lease's document id.</summary>
    internal const string EvaluatorDocumentId = "alert-rule-evaluator";

    private const string FiringStatus = nameof(AlertRuleStatus.Firing);

    // Every rule property except State.Transitions, flattened so the projection needs no object
    // construction in the query.
    private const string EvaluateProjection =
        "c.id, c.endpointId, c.Name, c.Enabled, c.Condition, c.ResolveAfterMinutes, c.Targets, c.CreatedBy, " +
        "c.CreatedAtUtc, c.UpdatedBy, c.UpdatedAtUtc, c.Version, c._etag, " +
        "c.State.Status AS StateStatus, c.State.SinceUtc AS StateSinceUtc, c.State.LastValue AS StateLastValue, " +
        "c.State.UpdatedAtUtc AS StateUpdatedAtUtc, c.State.ConditionFalseSinceUtc AS StateConditionFalseSinceUtc, " +
        "c.State.TransitionSeq AS StateTransitionSeq, c.State.PendingNotices AS StatePendingNotices";

    private readonly Func<Task<ICosmosContainerAdapter>> _getContainer;

    public CosmosDbAlertRuleStore(Func<Task<ICosmosContainerAdapter>> getContainer)
    {
        _getContainer = getContainer;
    }

    public async Task<IReadOnlyList<AlertRule>> ListAlertRulesByEndpoint(string endpointId)
    {
        if (string.IsNullOrEmpty(endpointId) || IsReserved(endpointId))
            return [];

        var query = new QueryDefinition("SELECT * FROM c WHERE c.endpointId = @endpointId")
            .WithParameter("@endpointId", endpointId);
        var rules = await ReadAllAsync<AlertRule>(query, new QueryRequestOptions { PartitionKey = new PartitionKey(endpointId) });
        return rules
            .Select(Normalize)
            .OrderBy(r => r.CreatedAtUtc)
            .ThenBy(r => r.RuleId, StringComparer.Ordinal)
            .ToList();
    }

    public async Task<IReadOnlyList<AlertRule>> ListAlertRulesToEvaluate(int limit)
    {
        if (limit <= 0)
            return [];

        var query = new QueryDefinition(
                $"SELECT TOP @limit {EvaluateProjection} FROM c WHERE c.endpointId != @reserved " +
                "AND (c.Enabled = true OR ARRAY_LENGTH(c.State.PendingNotices) > 0)")
            .WithParameter("@limit", limit)
            .WithParameter("@reserved", EvaluatorPartition);
        var rows = await ReadAllAsync<EvaluateRow>(query, null);
        return rows
            .Select(r => Normalize(r.ToRule()))
            .OrderBy(r => r.EndpointId, StringComparer.Ordinal)
            .ThenBy(r => r.RuleId, StringComparer.Ordinal)
            .Take(limit)
            .ToList();
    }

    public async Task<IReadOnlyList<string>> ListEndpointsWithFiringAlertRules()
    {
        var query = new QueryDefinition(
                "SELECT VALUE c.endpointId FROM c WHERE c.endpointId != @reserved AND c.State.Status = @firing")
            .WithParameter("@reserved", EvaluatorPartition)
            .WithParameter("@firing", FiringStatus);
        var endpoints = await ReadAllAsync<string>(query, null);
        return endpoints.Distinct(StringComparer.Ordinal).ToList();
    }

    public async Task<AlertRule?> GetAlertRule(string endpointId, string ruleId)
    {
        if (string.IsNullOrEmpty(endpointId) || string.IsNullOrEmpty(ruleId) || IsReserved(endpointId))
            return null;

        var container = await _getContainer();
        var current = await ReadAsync(container, endpointId, ruleId);
        return current is null ? null : Normalize(current);
    }

    public async Task<AlertRule?> TrySaveAlertRuleDefinition(AlertRule rule, long? expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.EndpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.RuleId);
        if (IsReserved(rule.EndpointId))
            throw new ArgumentException($"'{EvaluatorPartition}' is reserved and cannot be an endpoint id.", nameof(rule));

        var partitionKey = new PartitionKey(rule.EndpointId);
        var stored = rule.Clone();
        stored.ETag = null;

        var container = await _getContainer();
        try
        {
            ItemResponse<AlertRule> response;
            if (expectedVersion is null)
            {
                stored.Version = 1;
                // Create fails with 409 when the rule exists.
                response = await container.CreateItemAsync(stored, partitionKey, CancellationToken.None);
            }
            else
            {
                var current = await ReadAsync(container, rule.EndpointId, rule.RuleId);
                if (current is null || current.Version != expectedVersion.Value)
                    return null;

                stored.Version = expectedVersion.Value + 1;
                response = await container.ReplaceItemAsync(
                    stored, stored.RuleId, partitionKey, new ItemRequestOptions { IfMatchEtag = current.ETag });
            }

            stored.ETag = response.ETag;
            return Normalize(stored.Clone());
        }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
        {
            return null;
        }
    }

    public async Task<long?> TrySaveAlertRuleState(string endpointId, string ruleId, long expectedVersion, AlertRuleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (string.IsNullOrEmpty(endpointId) || string.IsNullOrEmpty(ruleId) || IsReserved(endpointId))
            return null;

        var container = await _getContainer();
        try
        {
            var current = await ReadAsync(container, endpointId, ruleId);
            if (current is null || current.Version != expectedVersion)
                return null;

            var etag = current.ETag;
            current.State = state.Clone();
            current.Version = expectedVersion + 1;
            await container.ReplaceItemAsync(
                current, ruleId, new PartitionKey(endpointId), new ItemRequestOptions { IfMatchEtag = etag });
            return current.Version;
        }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            return null;
        }
    }

    public async Task<bool> DeleteAlertRule(string endpointId, string ruleId, long expectedVersion)
    {
        if (string.IsNullOrEmpty(endpointId) || string.IsNullOrEmpty(ruleId) || IsReserved(endpointId))
            return false;

        var container = await _getContainer();
        try
        {
            var current = await ReadAsync(container, endpointId, ruleId);
            if (current is null || current.Version != expectedVersion)
                return false;

            await container.DeleteItemAsync<AlertRule>(
                ruleId, new PartitionKey(endpointId), new ItemRequestOptions { IfMatchEtag = current.ETag }, CancellationToken.None);
            return true;
        }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.PreconditionFailed)
        {
            return false;
        }
    }

    public async Task<bool> TryClaimAlertRuleEvaluatorTick(DateTime dueBefore, DateTime nowUtc, string instanceId)
    {
        var container = await _getContainer();
        var partitionKey = new PartitionKey(EvaluatorPartition);
        try
        {
            var response = await ReadLeaseAsync(container);
            if (response is null)
            {
                // The first claim ever; a concurrent creator wins with 409 and this caller loses.
                await container.CreateItemAsync(
                    new EvaluatorLeaseDocument { LastClaimedAtUtc = nowUtc, LastClaimedBy = instanceId },
                    partitionKey,
                    CancellationToken.None);
                return true;
            }

            var lease = response.Resource;
            if (lease.LastClaimedAtUtc is { } last && AsUtc(last).Ticks >= AsUtc(dueBefore).Ticks)
                return false;

            lease.LastClaimedAtUtc = nowUtc;
            lease.LastClaimedBy = instanceId;
            await container.ReplaceItemAsync(
                lease, EvaluatorDocumentId, partitionKey, new ItemRequestOptions { IfMatchEtag = response.ETag });
            return true;
        }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<bool> CompleteAlertRuleEvaluatorTick(DateTime claimedAtUtc, DateTime completedAtUtc)
    {
        var container = await _getContainer();
        try
        {
            var response = await ReadLeaseAsync(container);
            if (response?.Resource.LastClaimedAtUtc is not { } last || AsUtc(last).Ticks != AsUtc(claimedAtUtc).Ticks)
                return false;

            var lease = response.Resource;
            lease.LastCompletedAtUtc = completedAtUtc;
            await container.ReplaceItemAsync(
                lease, EvaluatorDocumentId, new PartitionKey(EvaluatorPartition), new ItemRequestOptions { IfMatchEtag = response.ETag });
            return true;
        }
        catch (CosmosException e) when (e.StatusCode is HttpStatusCode.PreconditionFailed or HttpStatusCode.NotFound)
        {
            return false;
        }
    }

    public async Task<AlertRuleEvaluatorStatus> GetAlertRuleEvaluatorStatus()
    {
        var container = await _getContainer();
        var lease = (await ReadLeaseAsync(container))?.Resource;
        return new AlertRuleEvaluatorStatus
        {
            LastClaimedAtUtc = AsUtc(lease?.LastClaimedAtUtc),
            LastClaimedBy = lease?.LastClaimedBy,
            LastCompletedAtUtc = AsUtc(lease?.LastCompletedAtUtc),
        };
    }

    private static bool IsReserved(string endpointId) => string.Equals(endpointId, EvaluatorPartition, StringComparison.Ordinal);

    // Reads a rule with its ETag; null when it does not exist in the endpoint's partition.
    private static async Task<AlertRule?> ReadAsync(ICosmosContainerAdapter container, string endpointId, string ruleId)
    {
        try
        {
            var response = await container.ReadItemAsync<AlertRule>(ruleId, new PartitionKey(endpointId));
            var rule = response.Resource;
            rule.ETag = response.ETag;
            return rule;
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private static async Task<ItemResponse<EvaluatorLeaseDocument>?> ReadLeaseAsync(ICosmosContainerAdapter container)
    {
        try
        {
            return await container.ReadItemAsync<EvaluatorLeaseDocument>(EvaluatorDocumentId, new PartitionKey(EvaluatorPartition));
        }
        catch (CosmosException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    private async Task<List<T>> ReadAllAsync<T>(QueryDefinition query, QueryRequestOptions? options)
    {
        var container = await _getContainer();
        var results = new List<T>();
        using var iterator = container.GetItemQueryIterator<T>(query, null, options);
        while (iterator.HasMoreResults)
            results.AddRange(await iterator.ReadNextAsync());
        return results;
    }

    private static AlertRule Normalize(AlertRule rule)
    {
        rule.CreatedAtUtc = AsUtc(rule.CreatedAtUtc);
        rule.UpdatedAtUtc = AsUtc(rule.UpdatedAtUtc);
        rule.Condition ??= new AlertRuleCondition();
        rule.Condition.EventTypes ??= [];
        rule.Condition.Statuses ??= [];
        rule.Targets ??= [];
        rule.State ??= new AlertRuleState();
        var state = rule.State;
        state.SinceUtc = AsUtc(state.SinceUtc);
        state.UpdatedAtUtc = AsUtc(state.UpdatedAtUtc);
        state.ConditionFalseSinceUtc = AsUtc(state.ConditionFalseSinceUtc);
        state.PendingNotices ??= [];
        foreach (var notice in state.PendingNotices)
        {
            notice.Target ??= new AlertRuleTarget();
            notice.SinceUtc = AsUtc(notice.SinceUtc);
            notice.CreatedAtUtc = AsUtc(notice.CreatedAtUtc);
        }

        state.Transitions ??= [];
        foreach (var transition in state.Transitions)
            transition.AtUtc = AsUtc(transition.AtUtc);
        return rule;
    }

    private static DateTime? AsUtc(DateTime? value) => value is null ? null : AsUtc(value.Value);

    private static DateTime AsUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    /// <summary>The evaluator lease document, kept under <see cref="EvaluatorPartition"/>.</summary>
    private sealed class EvaluatorLeaseDocument
    {
        [JsonProperty("id")]
        public string Id { get; set; } = EvaluatorDocumentId;

        [JsonProperty("endpointId")]
        public string EndpointId { get; set; } = EvaluatorPartition;

        public DateTime? LastClaimedAtUtc { get; set; }

        public string? LastClaimedBy { get; set; }

        public DateTime? LastCompletedAtUtc { get; set; }
    }

    /// <summary>A rule as <see cref="ListAlertRulesToEvaluate"/> projects it: no transitions.</summary>
    private sealed class EvaluateRow
    {
        [JsonProperty("id")]
        public string RuleId { get; set; } = string.Empty;

        [JsonProperty("endpointId")]
        public string EndpointId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
        public bool Enabled { get; set; }
        public AlertRuleCondition? Condition { get; set; }
        public int ResolveAfterMinutes { get; set; }
        public List<AlertRuleTarget>? Targets { get; set; }
        public string CreatedBy { get; set; } = string.Empty;
        public DateTime CreatedAtUtc { get; set; }
        public string? UpdatedBy { get; set; }
        public DateTime UpdatedAtUtc { get; set; }
        public long Version { get; set; }

        [JsonProperty("_etag")]
        public string? ETag { get; set; }

        [JsonConverter(typeof(Newtonsoft.Json.Converters.StringEnumConverter))]
        public AlertRuleStatus StateStatus { get; set; }

        public DateTime? StateSinceUtc { get; set; }
        public int? StateLastValue { get; set; }
        public DateTime? StateUpdatedAtUtc { get; set; }
        public DateTime? StateConditionFalseSinceUtc { get; set; }
        public long StateTransitionSeq { get; set; }
        public List<AlertRulePendingNotice>? StatePendingNotices { get; set; }

        public AlertRule ToRule() => new()
        {
            RuleId = RuleId,
            EndpointId = EndpointId,
            Name = Name,
            Enabled = Enabled,
            Condition = Condition ?? new AlertRuleCondition(),
            ResolveAfterMinutes = ResolveAfterMinutes,
            Targets = Targets ?? [],
            CreatedBy = CreatedBy,
            CreatedAtUtc = CreatedAtUtc,
            UpdatedBy = UpdatedBy,
            UpdatedAtUtc = UpdatedAtUtc,
            Version = Version,
            ETag = ETag,
            State = new AlertRuleState
            {
                Status = StateStatus,
                SinceUtc = StateSinceUtc,
                LastValue = StateLastValue,
                UpdatedAtUtc = StateUpdatedAtUtc,
                ConditionFalseSinceUtc = StateConditionFalseSinceUtc,
                TransitionSeq = StateTransitionSeq,
                PendingNotices = StatePendingNotices ?? [],
            },
        };
    }
}
