using System.Globalization;
using System.Text;
using NimBus.MessageStore.States;

namespace NimBus.Alerts;

/// <summary>
/// One notice of an alert rule's transition (Spec 053): the rule opened or resolved. The same
/// notice id is sent on every retry, so a receiver can drop a duplicate.
/// </summary>
/// <param name="NoticeId"><c>&lt;ruleId&gt;:&lt;transitionSeq&gt;:&lt;targetId&gt;</c>.</param>
/// <param name="RuleId">The rule's id.</param>
/// <param name="RuleName">The rule's name.</param>
/// <param name="EndpointId">The endpoint the rule watches.</param>
/// <param name="Status">Firing for an opened notice, Ok for a resolved one.</param>
/// <param name="Reason">For a resolved notice: one of <see cref="AlertRuleResolveReasons"/>.</param>
/// <param name="Value">The backlog read, capped at the threshold; equal to the threshold means "at least".</param>
/// <param name="Condition">The rule's condition when the transition happened.</param>
/// <param name="SinceUtc">When the announced status began.</param>
/// <param name="WebUiUrl">The endpoint's failed messages in the WebApp, filtered like the rule, when the WebApp's URL is known.</param>
public sealed record AlertRuleNotice(
    string NoticeId,
    string RuleId,
    string RuleName,
    string EndpointId,
    AlertRuleStatus Status,
    string? Reason,
    int? Value,
    AlertRuleCondition Condition,
    DateTime SinceUtc,
    string? WebUiUrl)
{
    /// <summary>"Firing" or "Resolved", as the notice reports it.</summary>
    public string StatusName => Status == AlertRuleStatus.Firing ? "Firing" : "Resolved";
}

/// <summary>Delivers alert rule notices to webhook targets.</summary>
public interface IAlertRuleWebhookSender
{
    /// <summary>Whether <paramref name="url"/> is a URL this sender delivers to.</summary>
    /// <param name="url">A target's URL.</param>
    bool CanSendTo(string? url);

    /// <summary>
    /// Sends <paramref name="notice"/> to <paramref name="target"/>, signed with the target's derived
    /// secret. Throws when the receiver does not accept it.
    /// </summary>
    /// <param name="notice">The notice.</param>
    /// <param name="target">The webhook target.</param>
    /// <param name="cancellationToken">Cancels the delivery.</param>
    Task SendRuleNoticeAsync(AlertRuleNotice notice, AlertRuleTarget target, CancellationToken cancellationToken = default);
}

/// <summary>Words and links for alert rules, shared by the mail, the evaluator and the API.</summary>
public static class AlertRuleText
{
    /// <summary>
    /// The condition in words, for example
    /// <c>5 or more failed events of type OrderPlaced with an error containing "timeout"</c>,
    /// <c>3 or more failures in 15 min</c>, <c>a session blocked for 30 min, or 50 or more deferred events</c>
    /// or <c>no completed message for 1 h</c>.
    /// </summary>
    /// <param name="condition">The condition.</param>
    public static string DescribeCondition(AlertRuleCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var text = new StringBuilder();
        switch (condition.Kind)
        {
            case AlertRuleConditionKinds.FailureOccurrences:
                text.Append(CultureInfo.InvariantCulture, $"{condition.Threshold} or more {(condition.Threshold == 1 ? "failure" : "failures")} in {Duration(condition.WindowMinutes)}");
                AppendFilters(text, condition, statuses: false);
                return text.ToString();
            case AlertRuleConditionKinds.BlockedSession:
                var parts = new List<string>();
                if (condition.Minutes is { } minutes)
                {
                    parts.Add($"a session blocked for {Duration(minutes)}");
                }

                if (condition.DeferredThreshold is { } deferred)
                {
                    parts.Add(string.Create(CultureInfo.InvariantCulture, $"{deferred} or more deferred {(deferred == 1 ? "event" : "events")}"));
                }

                return string.Join(", or ", parts);
            case AlertRuleConditionKinds.Silence:
                return $"no completed message for {Duration(condition.Minutes)}";
            default:
                text.Append(condition.Threshold.ToString(CultureInfo.InvariantCulture));
                text.Append(condition.Threshold == 1 ? " or more failed event" : " or more failed events");
                AppendFilters(text, condition, statuses: true);
                return text.ToString();
        }
    }

    /// <summary>What a notice's value is: failed events, failures in the window, or minutes.</summary>
    /// <param name="condition">The condition.</param>
    public static string ValueLabel(AlertRuleCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return condition.Kind switch
        {
            AlertRuleConditionKinds.FailureOccurrences => "Failures in the window",
            AlertRuleConditionKinds.BlockedSession => "Blocked for (minutes)",
            AlertRuleConditionKinds.Silence => "Silent for (minutes)",
            _ => "Failed events",
        };
    }

    /// <summary>Why a rule whose condition stopped holding resolved, in words.</summary>
    /// <param name="condition">The condition.</param>
    public static string ClearedText(AlertRuleCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        return condition.Kind switch
        {
            AlertRuleConditionKinds.FailureOccurrences => "The failures in the window dropped below the threshold.",
            AlertRuleConditionKinds.BlockedSession => "No session is blocked any more.",
            AlertRuleConditionKinds.Silence => "Messages completed again.",
            _ => "The failed events were cleared.",
        };
    }

    /// <summary>
    /// The WebApp page a notice links to, or null when <paramref name="webAppUrl"/> is not set: the
    /// Failed view filtered like the rule for the failure conditions, the endpoint otherwise.
    /// </summary>
    /// <param name="webAppUrl">The WebApp's base URL.</param>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="condition">The rule's condition.</param>
    public static string? LinkFor(string? webAppUrl, string endpointId, AlertRuleCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (string.IsNullOrWhiteSpace(webAppUrl))
        {
            return null;
        }

        var root = webAppUrl.TrimEnd('/');
        var endpoint = Uri.EscapeDataString(endpointId);
        return condition.Kind switch
        {
            AlertRuleConditionKinds.BlockedSession => $"{root}/Endpoints/Details/{endpoint}?status=Failed&status=Pending&status=Deferred",
            AlertRuleConditionKinds.Silence => $"{root}/Endpoints/Details/{endpoint}",
            _ => FailedMessagesUrl(webAppUrl, endpointId, condition),
        };
    }

    /// <summary>
    /// The WebApp's Failed view for <paramref name="endpointId"/>, filtered like the rule, or null
    /// when <paramref name="webAppUrl"/> is not set. A backlog has no window, so the link asks for
    /// the widest period the view offers; occurrences ask for the period that covers their window.
    /// </summary>
    /// <param name="webAppUrl">The WebApp's base URL.</param>
    /// <param name="endpointId">The endpoint.</param>
    /// <param name="condition">The rule's condition.</param>
    public static string? FailedMessagesUrl(string? webAppUrl, string endpointId, AlertRuleCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);
        if (string.IsNullOrWhiteSpace(webAppUrl))
        {
            return null;
        }

        var occurrences = condition.Kind == AlertRuleConditionKinds.FailureOccurrences;
        var query = new List<string>
        {
            "endpointId=" + Uri.EscapeDataString(endpointId),
            "period=" + (occurrences ? PeriodCovering(condition.WindowMinutes) : "30d"),
        };
        if (!occurrences)
        {
            query.AddRange(condition.Statuses.Select(s => "status=" + Uri.EscapeDataString(s)));
        }

        query.AddRange(condition.EventTypes.Select(t => "eventTypeId=" + Uri.EscapeDataString(t)));
        if (!string.IsNullOrWhiteSpace(condition.ErrorTextContains))
        {
            query.Add("errorText=" + Uri.EscapeDataString(condition.ErrorTextContains));
        }

        return webAppUrl.TrimEnd('/') + "/Failed?" + string.Join("&", query);
    }

    /// <summary>The notice id for one target of one transition.</summary>
    /// <param name="ruleId">The rule.</param>
    /// <param name="transitionSeq">The transition's number.</param>
    /// <param name="targetId">The target.</param>
    public static string NoticeId(string ruleId, long transitionSeq, string targetId)
        => string.Create(CultureInfo.InvariantCulture, $"{ruleId}:{transitionSeq}:{targetId}");

    /// <summary>"15 min", "2 h" or "1 h 30 min".</summary>
    private static string Duration(int? minutes)
    {
        var value = minutes ?? 0;
        if (value < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{value} min");
        }

        return value % 60 == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{value / 60} h")
            : string.Create(CultureInfo.InvariantCulture, $"{value / 60} h {value % 60} min");
    }

    /// <summary>The Failed view's smallest period preset that covers <paramref name="windowMinutes"/>.</summary>
    private static string PeriodCovering(int? windowMinutes) => (windowMinutes ?? 0) switch
    {
        <= 60 => "1h",
        <= 720 => "12h",
        <= 1440 => "1d",
        _ => "3d",
    };

    private static void AppendFilters(StringBuilder text, AlertRuleCondition condition, bool statuses)
    {
        if (statuses && condition.Statuses is { Count: > 0 } statusList)
        {
            text.Append(CultureInfo.InvariantCulture, $" ({string.Join(", ", statusList)})");
        }

        if (condition.EventTypes is { Count: > 0 } eventTypes)
        {
            text.Append(CultureInfo.InvariantCulture, $" of type {string.Join(" or ", eventTypes)}");
        }

        if (!string.IsNullOrWhiteSpace(condition.ErrorTextContains))
        {
            text.Append(CultureInfo.InvariantCulture, $" with an error containing \"{condition.ErrorTextContains}\"");
        }
    }
}
