using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Testing.Conformance;

/// <summary>
/// In-memory <see cref="IAlertRuleStore"/> (Spec 053). Rules are keyed by endpoint and rule id;
/// every read and write copies the rule, so no caller holds a stored instance by reference.
/// </summary>
public class InMemoryAlertRuleStore : IAlertRuleStore
{
    private readonly object _lock = new();
    private readonly Dictionary<(string EndpointId, string RuleId), AlertRule> _rules = new();
    private readonly AlertRuleEvaluatorStatus _evaluator = new();

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<AlertRule>> ListAlertRulesByEndpoint(string endpointId)
    {
        lock (_lock)
        {
            IReadOnlyList<AlertRule> rules = _rules.Values
                .Where(r => string.Equals(r.EndpointId, endpointId, StringComparison.Ordinal))
                .OrderBy(r => r.CreatedAtUtc)
                .ThenBy(r => r.RuleId, StringComparer.Ordinal)
                .Select(r => r.Clone())
                .ToList();
            return Task.FromResult(rules);
        }
    }

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<AlertRule>> ListAlertRulesToEvaluate(int limit)
    {
        lock (_lock)
        {
            IReadOnlyList<AlertRule> rules = _rules.Values
                .Where(r => r.Enabled || r.State.PendingNotices.Count > 0)
                .OrderBy(r => r.EndpointId, StringComparer.Ordinal)
                .ThenBy(r => r.RuleId, StringComparer.Ordinal)
                .Take(Math.Max(0, limit))
                .Select(r =>
                {
                    var copy = r.Clone();
                    copy.State.Transitions = new List<AlertRuleTransition>();
                    return copy;
                })
                .ToList();
            return Task.FromResult(rules);
        }
    }

    /// <inheritdoc />
    public virtual Task<IReadOnlyList<string>> ListEndpointsWithFiringAlertRules()
    {
        lock (_lock)
        {
            IReadOnlyList<string> endpoints = _rules.Values
                .Where(r => r.State.Status == AlertRuleStatus.Firing)
                .Select(r => r.EndpointId)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            return Task.FromResult(endpoints);
        }
    }

    /// <inheritdoc />
    public virtual Task<AlertRule?> GetAlertRule(string endpointId, string ruleId)
    {
        lock (_lock)
        {
            return Task.FromResult(_rules.TryGetValue((endpointId, ruleId), out var stored) ? stored.Clone() : null);
        }
    }

    /// <inheritdoc />
    public virtual Task<AlertRule?> TrySaveAlertRuleDefinition(AlertRule rule, long? expectedVersion)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.EndpointId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rule.RuleId);

        var key = (rule.EndpointId, rule.RuleId);
        lock (_lock)
        {
            var exists = _rules.TryGetValue(key, out var current);
            if (expectedVersion is null ? exists : !exists || current!.Version != expectedVersion.Value)
                return Task.FromResult<AlertRule?>(null);

            var stored = rule.Clone();
            stored.Version = expectedVersion is null ? 1 : expectedVersion.Value + 1;
            stored.ETag = null;
            _rules[key] = stored;
            return Task.FromResult<AlertRule?>(stored.Clone());
        }
    }

    /// <inheritdoc />
    public virtual Task<long?> TrySaveAlertRuleState(string endpointId, string ruleId, long expectedVersion, AlertRuleState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        lock (_lock)
        {
            if (!_rules.TryGetValue((endpointId, ruleId), out var stored) || stored.Version != expectedVersion)
                return Task.FromResult<long?>(null);

            stored.State = state.Clone();
            stored.Version = expectedVersion + 1;
            return Task.FromResult<long?>(stored.Version);
        }
    }

    /// <inheritdoc />
    public virtual Task<bool> DeleteAlertRule(string endpointId, string ruleId, long expectedVersion)
    {
        lock (_lock)
        {
            if (!_rules.TryGetValue((endpointId, ruleId), out var stored) || stored.Version != expectedVersion)
                return Task.FromResult(false);

            return Task.FromResult(_rules.Remove((endpointId, ruleId)));
        }
    }

    /// <inheritdoc />
    public virtual Task<bool> TryClaimAlertRuleEvaluatorTick(DateTime dueBefore, DateTime nowUtc, string instanceId)
    {
        lock (_lock)
        {
            if (_evaluator.LastClaimedAtUtc is { } last && last >= dueBefore)
                return Task.FromResult(false);

            _evaluator.LastClaimedAtUtc = nowUtc;
            _evaluator.LastClaimedBy = instanceId;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public virtual Task<bool> CompleteAlertRuleEvaluatorTick(DateTime claimedAtUtc, DateTime completedAtUtc)
    {
        lock (_lock)
        {
            if (_evaluator.LastClaimedAtUtc != claimedAtUtc)
                return Task.FromResult(false);

            _evaluator.LastCompletedAtUtc = completedAtUtc;
            return Task.FromResult(true);
        }
    }

    /// <inheritdoc />
    public virtual Task<AlertRuleEvaluatorStatus> GetAlertRuleEvaluatorStatus()
    {
        lock (_lock)
        {
            return Task.FromResult(new AlertRuleEvaluatorStatus
            {
                LastClaimedAtUtc = _evaluator.LastClaimedAtUtc,
                LastClaimedBy = _evaluator.LastClaimedBy,
                LastCompletedAtUtc = _evaluator.LastCompletedAtUtc,
            });
        }
    }
}
