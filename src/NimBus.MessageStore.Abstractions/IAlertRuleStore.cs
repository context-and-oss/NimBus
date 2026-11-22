using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using NimBus.MessageStore.States;

namespace NimBus.MessageStore.Abstractions;

/// <summary>
/// Alert rules (Spec 053): definitions an endpoint Owner manages, the state the WebApp's evaluator
/// keeps, and the evaluator lease. A separate optional capability (ADR-018): without a registered
/// implementation the WebApp offers no rules and runs no evaluator.
/// </summary>
/// <remarks>
/// Every rule operation names the endpoint and the rule; a rule asked for under another endpoint
/// is not found. Every rule write bumps <see cref="AlertRule.Version"/>, and conditional writes
/// compare it (and, on Cosmos DB, the document ETag), so a write based on an older read never
/// lands. Scaled-out evaluators share one lease per tick (<see cref="TryClaimAlertRuleEvaluatorTick"/>)
/// and write a rule only when its state changes.
/// </remarks>
public interface IAlertRuleStore
{
    /// <summary>The endpoint's rules, with their state and transitions, in creation order.</summary>
    /// <param name="endpointId">The endpoint.</param>
    Task<IReadOnlyList<AlertRule>> ListAlertRulesByEndpoint(string endpointId);

    /// <summary>
    /// The rules an evaluator tick looks at: every enabled rule, and every disabled rule that still
    /// has pending notices. Their <see cref="AlertRuleState.Transitions"/> are left empty.
    /// </summary>
    /// <param name="limit">The most rules to return.</param>
    Task<IReadOnlyList<AlertRule>> ListAlertRulesToEvaluate(int limit);

    /// <summary>The endpoints that have at least one Firing rule.</summary>
    Task<IReadOnlyList<string>> ListEndpointsWithFiringAlertRules();

    /// <summary>The rule, or null when it does not exist on <paramref name="endpointId"/>.</summary>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="ruleId">The rule.</param>
    Task<AlertRule?> GetAlertRule(string endpointId, string ruleId);

    /// <summary>
    /// Writes the whole rule, definition and <see cref="AlertRule.State"/> as given: creates it
    /// when <paramref name="expectedVersion"/> is null, otherwise replaces it when the stored
    /// version still equals <paramref name="expectedVersion"/>. An edit that closes an incident
    /// passes the reset state, with its pending notices, in the same call.
    /// </summary>
    /// <param name="rule">The rule to store.</param>
    /// <param name="expectedVersion">Null to create; otherwise the version the caller read.</param>
    /// <returns>The stored rule with its new version, or null when the rule exists (create) or
    /// changed or no longer exists (replace).</returns>
    Task<AlertRule?> TrySaveAlertRuleDefinition(AlertRule rule, long? expectedVersion);

    /// <summary>
    /// Replaces only the rule's state when the stored version still equals
    /// <paramref name="expectedVersion"/>; the definition is left as stored.
    /// </summary>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="ruleId">The rule.</param>
    /// <param name="expectedVersion">The version the evaluator read this tick.</param>
    /// <param name="state">The new state.</param>
    /// <returns>The new version, or null when the rule changed or no longer exists.</returns>
    Task<long?> TrySaveAlertRuleState(string endpointId, string ruleId, long expectedVersion, AlertRuleState state);

    /// <summary>Deletes the rule when its version still equals <paramref name="expectedVersion"/>.</summary>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="ruleId">The rule.</param>
    /// <param name="expectedVersion">The version the caller read.</param>
    /// <returns>False when the rule changed or does not exist.</returns>
    Task<bool> DeleteAlertRule(string endpointId, string ruleId, long expectedVersion);

    /// <summary>
    /// Claims one evaluator tick: succeeds, and records <paramref name="nowUtc"/> and
    /// <paramref name="instanceId"/>, only when no tick was claimed at or after
    /// <paramref name="dueBefore"/>. Concurrent callers for the same tick: exactly one wins.
    /// </summary>
    /// <param name="dueBefore">The tick is due when the last one was claimed before this.</param>
    /// <param name="nowUtc">The claim time.</param>
    /// <param name="instanceId">The claiming instance, for diagnosis.</param>
    /// <returns>True when this caller holds the tick.</returns>
    Task<bool> TryClaimAlertRuleEvaluatorTick(DateTime dueBefore, DateTime nowUtc, string instanceId);

    /// <summary>Records that the tick claimed at <paramref name="claimedAtUtc"/> completed at <paramref name="completedAtUtc"/>.</summary>
    /// <param name="claimedAtUtc">The claim time passed to <see cref="TryClaimAlertRuleEvaluatorTick"/>.</param>
    /// <param name="completedAtUtc">The completion time.</param>
    /// <returns>False when a newer tick has been claimed since; nothing is written.</returns>
    Task<bool> CompleteAlertRuleEvaluatorTick(DateTime claimedAtUtc, DateTime completedAtUtc);

    /// <summary>The evaluator lease's last claim and completion; empty before the first tick.</summary>
    Task<AlertRuleEvaluatorStatus> GetAlertRuleEvaluatorStatus();
}
