using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace NimBus.MessageStore.States;

/// <summary>
/// An alert rule on one endpoint (Spec 053): a condition the WebApp evaluates every minute, its
/// recipients, and the state the evaluator keeps. The condition kinds are in
/// <see cref="AlertRuleConditionKinds"/>.
/// </summary>
public sealed class AlertRule
{
    /// <summary>The rule id, a GUID in "N" format.</summary>
    [JsonProperty("id")]
    public string RuleId { get; set; } = string.Empty;

    /// <summary>The endpoint the rule watches; also the Cosmos partition key.</summary>
    [JsonProperty("endpointId")]
    public string EndpointId { get; set; } = string.Empty;

    /// <summary>The rule's name, 1–100 characters.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Whether the evaluator evaluates the rule.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>What the rule watches.</summary>
    public AlertRuleCondition Condition { get; set; } = new();

    /// <summary>How long the condition must be false, continuously, before the rule resolves (1–60).</summary>
    public int ResolveAfterMinutes { get; set; } = 5;

    /// <summary>Who is told when the rule opens and resolves.</summary>
    public List<AlertRuleTarget> Targets { get; set; } = new();

    /// <summary>Who created the rule.</summary>
    public string CreatedBy { get; set; } = string.Empty;

    /// <summary>When the rule was created.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>Who last changed the definition.</summary>
    public string? UpdatedBy { get; set; }

    /// <summary>When the definition last changed.</summary>
    public DateTime UpdatedAtUtc { get; set; }

    /// <summary>
    /// The logical version. The store bumps it on every write (definition or state), so a
    /// compare-and-set on it detects any change since the caller read the rule.
    /// </summary>
    public long Version { get; set; }

    /// <summary>The evaluator's state; written by the evaluator, reset by an edit.</summary>
    public AlertRuleState State { get; set; } = new();

    /// <summary>
    /// Cosmos DB's document ETag, read back and used alongside <see cref="Version"/>; never written
    /// into a document.
    /// </summary>
    [JsonProperty("_etag")]
    public string? ETag { get; set; }

    /// <summary>Keeps <see cref="ETag"/> out of serialized documents (Newtonsoft conditional serialization).</summary>
    public bool ShouldSerializeETag() => false;

    /// <summary>A deep copy, so a caller's changes never reach a stored instance by reference.</summary>
    public AlertRule Clone()
    {
        var copy = (AlertRule)MemberwiseClone();
        copy.Condition = Condition.Clone();
        copy.Targets = Targets.Select(t => t.Clone()).ToList();
        copy.State = State.Clone();
        return copy;
    }
}

/// <summary>The condition kinds an <see cref="AlertRuleCondition"/> can have.</summary>
public static class AlertRuleConditionKinds
{
    /// <summary>The events on the endpoint that are failed now (Failed, DeadLettered, Unsupported), with no window.</summary>
    public const string FailureBacklog = "failure_backlog";

    /// <summary>
    /// Failed attempts on the endpoint in the last <see cref="AlertRuleCondition.WindowMinutes"/>,
    /// counted from message history even if the events were resolved later (Phase 2).
    /// </summary>
    public const string FailureOccurrences = "failure_occurrences";

    /// <summary>
    /// A session held blocked: the oldest Failed or handed-off event has not changed for
    /// <see cref="AlertRuleCondition.Minutes"/>, or at least <see cref="AlertRuleCondition.DeferredThreshold"/>
    /// events are deferred on the endpoint (Phase 2).
    /// </summary>
    public const string BlockedSession = "blocked_session";

    /// <summary>No message on the endpoint completed successfully for <see cref="AlertRuleCondition.Minutes"/> (Phase 2).</summary>
    public const string Silence = "silence";

    /// <summary>Every kind, in the order the UI offers them.</summary>
    public static readonly IReadOnlyList<string> All = [FailureBacklog, FailureOccurrences, BlockedSession, Silence];
}

/// <summary>What a rule watches. Which fields apply depends on <see cref="Kind"/>.</summary>
public sealed class AlertRuleCondition
{
    /// <summary>The condition kind, one of <see cref="AlertRuleConditionKinds"/>.</summary>
    public string Kind { get; set; } = AlertRuleConditionKinds.FailureBacklog;

    /// <summary>Event types to count (backlog and occurrences); empty counts every type.</summary>
    public List<string> EventTypes { get; set; } = new();

    /// <summary>Statuses to count (backlog only: Failed, DeadLettered, Unsupported); empty counts all three.</summary>
    public List<string> Statuses { get; set; } = new();

    /// <summary>A case-insensitive substring the error text must contain (backlog and occurrences), up to 200 characters.</summary>
    public string? ErrorTextContains { get; set; }

    /// <summary>The count at which the rule fires (backlog and occurrences, 1–100).</summary>
    public int Threshold { get; set; } = 1;

    /// <summary>The window failed attempts are counted in (occurrences).</summary>
    public int? WindowMinutes { get; set; }

    /// <summary>
    /// Minutes: how long the oldest blocking event has not changed (blocked session), or how long
    /// nothing completed (silence).
    /// </summary>
    public int? Minutes { get; set; }

    /// <summary>The number of deferred events on the endpoint at which a blocked-session rule fires.</summary>
    public int? DeferredThreshold { get; set; }

    /// <summary>A copy.</summary>
    public AlertRuleCondition Clone()
    {
        var copy = (AlertRuleCondition)MemberwiseClone();
        copy.EventTypes = EventTypes.ToList();
        copy.Statuses = Statuses.ToList();
        return copy;
    }
}

/// <summary>How a target is told.</summary>
public enum AlertRuleTargetType
{
    /// <summary>An e-mail to <see cref="AlertRuleTarget.Address"/>.</summary>
    Mail,

    /// <summary>A signed HTTPS POST to <see cref="AlertRuleTarget.Url"/>.</summary>
    Webhook,
}

/// <summary>One recipient of a rule's notices.</summary>
public sealed class AlertRuleTarget
{
    /// <summary>The target id within the rule ("t1", "t2", …); part of the webhook signing secret and of notice ids.</summary>
    public string TargetId { get; set; } = string.Empty;

    /// <summary>How the target is told.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public AlertRuleTargetType Type { get; set; }

    /// <summary>The mail address, for a mail target.</summary>
    public string? Address { get; set; }

    /// <summary>The URL, for a webhook target.</summary>
    public string? Url { get; set; }

    /// <summary>A copy.</summary>
    public AlertRuleTarget Clone() => (AlertRuleTarget)MemberwiseClone();
}

/// <summary>Whether a rule's condition holds.</summary>
public enum AlertRuleStatus
{
    /// <summary>The condition does not hold (or the rule is new, edited or disabled).</summary>
    Ok,

    /// <summary>The condition holds; an <c>opened</c> notice was sent.</summary>
    Firing,
}

/// <summary>Why a rule resolved.</summary>
public static class AlertRuleResolveReasons
{
    /// <summary>The condition stayed false for <see cref="AlertRule.ResolveAfterMinutes"/>.</summary>
    public const string Cleared = "cleared";

    /// <summary>A Firing rule's condition was edited.</summary>
    public const string RuleChanged = "rule_changed";

    /// <summary>A Firing rule was disabled.</summary>
    public const string RuleDisabled = "rule_disabled";

    /// <summary>A Firing rule was deleted.</summary>
    public const string RuleDeleted = "rule_deleted";
}

/// <summary>The evaluator's state for one rule.</summary>
public sealed class AlertRuleState
{
    /// <summary>Whether the rule is firing.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public AlertRuleStatus Status { get; set; } = AlertRuleStatus.Ok;

    /// <summary>When the current status began; null for a rule never evaluated.</summary>
    public DateTime? SinceUtc { get; set; }

    /// <summary>The last backlog read, capped at the threshold (a value equal to the threshold means "at least").</summary>
    public int? LastValue { get; set; }

    /// <summary>
    /// When the evaluator last wrote this state. The evaluator writes only on a change, so this is
    /// not the last evaluation; that is <see cref="AlertRuleEvaluatorStatus.LastCompletedAtUtc"/>.
    /// </summary>
    public DateTime? UpdatedAtUtc { get; set; }

    /// <summary>While Firing: the first evaluation of an unbroken run that found the condition false.</summary>
    public DateTime? ConditionFalseSinceUtc { get; set; }

    /// <summary>The number of the last transition; part of each notice id.</summary>
    public long TransitionSeq { get; set; }

    /// <summary>Notices saved with a transition and not yet delivered.</summary>
    public List<AlertRulePendingNotice> PendingNotices { get; set; } = new();

    /// <summary>The most recent transitions, newest first, at most <see cref="MaxTransitions"/>.</summary>
    public List<AlertRuleTransition> Transitions { get; set; } = new();

    /// <summary>How many transitions a rule keeps.</summary>
    public const int MaxTransitions = 20;

    /// <summary>A deep copy.</summary>
    public AlertRuleState Clone()
    {
        var copy = (AlertRuleState)MemberwiseClone();
        copy.PendingNotices = PendingNotices.Select(n => n.Clone()).ToList();
        copy.Transitions = Transitions.Select(t => t.Clone()).ToList();
        return copy;
    }
}

/// <summary>One change of a rule's status.</summary>
public sealed class AlertRuleTransition
{
    /// <summary>The transition's number, from <see cref="AlertRuleState.TransitionSeq"/>.</summary>
    public long Seq { get; set; }

    /// <summary>The status the rule moved to.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public AlertRuleStatus Status { get; set; }

    /// <summary>When.</summary>
    public DateTime AtUtc { get; set; }

    /// <summary>The backlog read that caused it, when an evaluation did.</summary>
    public int? Value { get; set; }

    /// <summary>For a move to Ok: one of <see cref="AlertRuleResolveReasons"/>.</summary>
    public string? Reason { get; set; }

    /// <summary>A copy.</summary>
    public AlertRuleTransition Clone() => (AlertRuleTransition)MemberwiseClone();
}

/// <summary>
/// A notice saved with a transition and delivered afterwards. It carries a snapshot of its
/// target, so it can still be delivered after the rule's targets change or the rule is disabled.
/// </summary>
public sealed class AlertRulePendingNotice
{
    /// <summary><c>&lt;ruleId&gt;:&lt;transitionSeq&gt;:&lt;targetId&gt;</c>; stays the same across retries.</summary>
    public string NoticeId { get; set; } = string.Empty;

    /// <summary>The target to tell, as it was when the transition happened.</summary>
    public AlertRuleTarget Target { get; set; } = new();

    /// <summary>The status the notice announces.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public AlertRuleStatus Status { get; set; }

    /// <summary>For a resolved notice: one of <see cref="AlertRuleResolveReasons"/>.</summary>
    public string? Reason { get; set; }

    /// <summary>The backlog read the notice reports, capped at the threshold.</summary>
    public int? Value { get; set; }

    /// <summary>When the announced status began.</summary>
    public DateTime SinceUtc { get; set; }

    /// <summary>How many deliveries were tried.</summary>
    public int Attempts { get; set; }

    /// <summary>When the first delivery was tried; a notice is dropped 24 hours after it was created.</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>The last delivery error, for diagnosis (host and status only, never a secret).</summary>
    public string? LastError { get; set; }

    /// <summary>A copy.</summary>
    public AlertRulePendingNotice Clone()
    {
        var copy = (AlertRulePendingNotice)MemberwiseClone();
        copy.Target = Target.Clone();
        return copy;
    }
}

/// <summary>
/// The evaluator lease (Spec 053 revision 5): scaled-out WebApp instances claim one tick at a time,
/// and the store keeps when the last tick was claimed and completed.
/// </summary>
public sealed class AlertRuleEvaluatorStatus
{
    /// <summary>When the current or last tick was claimed.</summary>
    public DateTime? LastClaimedAtUtc { get; set; }

    /// <summary>The instance that claimed it.</summary>
    public string? LastClaimedBy { get; set; }

    /// <summary>When the last tick completed: every rule was evaluated and its changes saved.</summary>
    public DateTime? LastCompletedAtUtc { get; set; }
}
