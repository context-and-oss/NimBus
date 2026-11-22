using System.Collections.Generic;
using NimBus.Alerts;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services.AlertRules;

/// <summary>What one call of <see cref="AlertRuleEvaluator.RunTickAsync"/> did.</summary>
/// <param name="Claimed">Whether this instance held the tick; false when another one did.</param>
/// <param name="Evaluated">Rules evaluated.</param>
/// <param name="Saved">Rules whose state changed and was saved.</param>
public sealed record AlertRuleTickResult(bool Claimed, int Evaluated, int Saved);

/// <summary>
/// One evaluator tick (Spec 053 §5.3, revision 5): claim the tick, read each rule's failure backlog,
/// move rules between Ok and Firing, save a rule only when its state changed (with a pending notice
/// per target for a transition), then deliver pending notices.
/// </summary>
/// <remarks>
/// Every save is conditional on the version read this tick, so an edit, a delete or another
/// instance's newer write wins and this tick's change to that rule is dropped; the next tick sees
/// the new rule.
/// </remarks>
public sealed partial class AlertRuleEvaluator
{
    /// <summary>How often a tick is due.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    /// <summary>The most rules one tick looks at.</summary>
    public const int MaxRulesPerTick = 5000;

    /// <summary>How long an undelivered notice is retried.</summary>
    public static readonly TimeSpan NoticeLifetime = TimeSpan.FromHours(24);

    // A tick is due slightly before a full interval since the last claim, so timer jitter on the
    // instance that held the previous tick does not skip a minute.
    private static readonly TimeSpan ClaimSlack = TimeSpan.FromSeconds(5);

    private readonly IAlertRuleStore _rules;
    private readonly IMessageTrackingStore _events;
    private readonly AlertRuleDelivery _delivery;
    private readonly TimeProvider _time;
    private readonly ILogger<AlertRuleEvaluator> _logger;
    private readonly string _instanceId;

    /// <summary>Creates the evaluator.</summary>
    /// <param name="rules">The rule store.</param>
    /// <param name="events">The message store the backlog is read from.</param>
    /// <param name="delivery">The notice channels.</param>
    /// <param name="time">The clock.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="instanceId">This instance's name, recorded with the tick it claims.</param>
    public AlertRuleEvaluator(
        IAlertRuleStore rules,
        IMessageTrackingStore events,
        AlertRuleDelivery delivery,
        TimeProvider time,
        ILogger<AlertRuleEvaluator> logger,
        string instanceId)
    {
        _rules = rules ?? throw new ArgumentNullException(nameof(rules));
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        _time = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _instanceId = instanceId;
    }

    /// <summary>Runs one tick when this instance can claim it.</summary>
    /// <param name="cancellationToken">Stops the tick between rules.</param>
    public async Task<AlertRuleTickResult> RunTickAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        var previous = await _rules.GetAlertRuleEvaluatorStatus().ConfigureAwait(false);
        if (!await _rules.TryClaimAlertRuleEvaluatorTick(now - Interval + ClaimSlack, now, _instanceId).ConfigureAwait(false))
        {
            return new AlertRuleTickResult(false, 0, 0);
        }

        // Two false samples an hour apart do not prove the backlog stayed low: after a gap of more
        // than two intervals, a Firing rule's resolve time starts again.
        var gap = previous.LastCompletedAtUtc is not { } last || now - last > 2 * Interval;

        var rules = await _rules.ListAlertRulesToEvaluate(MaxRulesPerTick).ConfigureAwait(false);
        var evaluated = 0;
        var saved = 0;
        foreach (var rule in rules)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var (wasEvaluated, wasSaved) = await EvaluateAsync(rule, now, gap, cancellationToken).ConfigureAwait(false);
                evaluated += wasEvaluated ? 1 : 0;
                saved += wasSaved ? 1 : 0;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogRuleFailed(exception, rule.RuleId, rule.EndpointId);
            }
        }

        await _rules.CompleteAlertRuleEvaluatorTick(now, _time.GetUtcNow().UtcDateTime).ConfigureAwait(false);
        return new AlertRuleTickResult(true, evaluated, saved);
    }

    private async Task<(bool Evaluated, bool Saved)> EvaluateAsync(AlertRule rule, DateTime now, bool gap, CancellationToken cancellationToken)
    {
        var state = rule.State.Clone();
        var changed = false;
        if (rule.Enabled)
        {
            var backlog = await ReadBacklogAsync(rule).ConfigureAwait(false);
            changed = Apply(rule, state, backlog, now, gap);
            AlertRuleTelemetry.Evaluated.Add(1);
        }

        var version = rule.Version;
        var savedAny = false;
        if (changed)
        {
            // The transition and its pending notices are saved before anything is sent, so a crash
            // between the two still delivers the notice on the next tick.
            var saved = await _rules.TrySaveAlertRuleState(rule.EndpointId, rule.RuleId, version, state).ConfigureAwait(false);
            if (saved is null)
            {
                LogRuleChangedDuringTick(rule.RuleId, rule.EndpointId);
                return (rule.Enabled, false);
            }

            version = saved.Value;
            savedAny = true;
        }

        if (state.PendingNotices.Count > 0 && await DeliverAsync(rule, state, now, cancellationToken).ConfigureAwait(false))
        {
            var saved = await _rules.TrySaveAlertRuleState(rule.EndpointId, rule.RuleId, version, state).ConfigureAwait(false);
            savedAny |= saved is not null;
        }

        return (rule.Enabled, savedAny);
    }

    /// <summary>
    /// Applies the state machine of spec §5.4 to <paramref name="state"/>; true when it changed.
    /// </summary>
    internal static bool Apply(AlertRule rule, AlertRuleState state, int backlog, DateTime now, bool gap)
    {
        var holds = backlog >= rule.Condition.Threshold;
        var changed = state.LastValue != backlog;
        state.LastValue = backlog;

        if (state.Status == AlertRuleStatus.Ok)
        {
            if (state.SinceUtc is null)
            {
                state.SinceUtc = now;
                changed = true;
            }

            if (holds)
            {
                Transition(rule, state, AlertRuleStatus.Firing, reason: null, backlog, now);
                return true;
            }

            return changed;
        }

        if (holds)
        {
            if (state.ConditionFalseSinceUtc is not null)
            {
                state.ConditionFalseSinceUtc = null;
                changed = true;
            }

            return changed;
        }

        if (state.ConditionFalseSinceUtc is null || gap)
        {
            state.ConditionFalseSinceUtc = now;
            return true;
        }

        if (now - state.ConditionFalseSinceUtc.Value >= TimeSpan.FromMinutes(rule.ResolveAfterMinutes))
        {
            Transition(rule, state, AlertRuleStatus.Ok, AlertRuleResolveReasons.Cleared, backlog, now);
            return true;
        }

        return changed;
    }

    /// <summary>
    /// Records a transition and queues one pending notice per target. Also used by the API when an
    /// edit, a disable or a delete closes an incident.
    /// </summary>
    internal static void Transition(AlertRule rule, AlertRuleState state, AlertRuleStatus status, string? reason, int? value, DateTime now)
    {
        state.TransitionSeq++;
        state.Status = status;
        state.SinceUtc = now;
        state.ConditionFalseSinceUtc = null;
        state.Transitions.Insert(0, new AlertRuleTransition
        {
            Seq = state.TransitionSeq,
            Status = status,
            AtUtc = now,
            Value = value,
            Reason = reason,
        });
        if (state.Transitions.Count > AlertRuleState.MaxTransitions)
        {
            state.Transitions.RemoveRange(AlertRuleState.MaxTransitions, state.Transitions.Count - AlertRuleState.MaxTransitions);
        }

        foreach (var target in rule.Targets)
        {
            state.PendingNotices.Add(new AlertRulePendingNotice
            {
                NoticeId = AlertRuleText.NoticeId(rule.RuleId, state.TransitionSeq, target.TargetId),
                Target = target.Clone(),
                Status = status,
                Reason = reason,
                Value = value,
                SinceUtc = now,
                CreatedAtUtc = now,
            });
        }

        AlertRuleTelemetry.Transitions.Add(1, new KeyValuePair<string, object?>("status", status.ToString()));
    }

    private async Task<int> ReadBacklogAsync(AlertRule rule)
    {
        var condition = rule.Condition;
        var filter = new EventFilter
        {
            EventTypeId = condition.EventTypes.Count > 0 ? condition.EventTypes.ToList() : null,
            ResolutionStatus = condition.Statuses.Count > 0 ? condition.Statuses.ToList() : null,
            ErrorText = string.IsNullOrWhiteSpace(condition.ErrorTextContains) ? null : condition.ErrorTextContains,
        };

        // The rule only asks whether the backlog reaches the threshold, so it reads at most that
        // many failures and never follows the continuation token.
        var page = await _events
            .GetFailedEventsAcrossEndpoints(filter, [rule.EndpointId], continuationToken: null, maxItemCount: condition.Threshold)
            .ConfigureAwait(false);
        return Math.Min(page.Events?.Count() ?? 0, condition.Threshold);
    }

    /// <summary>Tries every pending notice once; true when the pending list changed.</summary>
    private async Task<bool> DeliverAsync(AlertRule rule, AlertRuleState state, DateTime now, CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var pending in state.PendingNotices.ToList())
        {
            var notice = new AlertRuleNotice(
                pending.NoticeId,
                rule.RuleId,
                rule.Name,
                rule.EndpointId,
                pending.Status,
                pending.Reason,
                pending.Value,
                rule.Condition,
                pending.SinceUtc,
                AlertRuleText.FailedMessagesUrl(_delivery.WebAppUrl, rule.EndpointId, rule.Condition));
            try
            {
                await _delivery.DeliverAsync(notice, pending.Target, cancellationToken).ConfigureAwait(false);
                state.PendingNotices.Remove(pending);
                changed = true;
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                AlertRuleTelemetry.NoticeFailures.Add(1, new KeyValuePair<string, object?>("channel", pending.Target.Type.ToString()));
                pending.Attempts++;
                pending.LastError = exception.Message;
                changed = true;
                if (now - pending.CreatedAtUtc >= NoticeLifetime)
                {
                    state.PendingNotices.Remove(pending);
                    AlertRuleTelemetry.NoticesDropped.Add(1);
                    LogNoticeDropped(pending.NoticeId, pending.Attempts, exception.Message);
                }
                else
                {
                    LogNoticeFailed(pending.NoticeId, pending.Attempts, exception.Message);
                }
            }
        }

        return changed;
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Error, Message = "Alert rule {RuleId} on {EndpointId} could not be evaluated.")]
    private partial void LogRuleFailed(Exception exception, string ruleId, string endpointId);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Alert rule {RuleId} on {EndpointId} changed during the tick; its new definition is evaluated next tick.")]
    private partial void LogRuleChangedDuringTick(string ruleId, string endpointId);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Alert rule notice {NoticeId} was not delivered (attempt {Attempts}): {Error}. It is retried next tick.")]
    private partial void LogNoticeFailed(string noticeId, int attempts, string error);

    [LoggerMessage(EventId = 4, Level = LogLevel.Error, Message = "Alert rule notice {NoticeId} was dropped after {Attempts} attempts over 24 hours: {Error}.")]
    private partial void LogNoticeDropped(string noticeId, int attempts, string error);
}
