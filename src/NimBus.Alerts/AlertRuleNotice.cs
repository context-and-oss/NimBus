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
    /// <c>5 or more failed events of type OrderPlaced with an error containing "timeout"</c>.
    /// </summary>
    /// <param name="condition">The condition.</param>
    public static string DescribeCondition(AlertRuleCondition condition)
    {
        ArgumentNullException.ThrowIfNull(condition);

        var text = new StringBuilder();
        text.Append(condition.Threshold.ToString(CultureInfo.InvariantCulture));
        text.Append(condition.Threshold == 1 ? " or more failed event" : " or more failed events");
        if (condition.Statuses is { Count: > 0 } statuses)
        {
            text.Append(CultureInfo.InvariantCulture, $" ({string.Join(", ", statuses)})");
        }

        if (condition.EventTypes is { Count: > 0 } eventTypes)
        {
            text.Append(CultureInfo.InvariantCulture, $" of type {string.Join(" or ", eventTypes)}");
        }

        if (!string.IsNullOrWhiteSpace(condition.ErrorTextContains))
        {
            text.Append(CultureInfo.InvariantCulture, $" with an error containing \"{condition.ErrorTextContains}\"");
        }

        return text.ToString();
    }

    /// <summary>
    /// The WebApp's Failed view for <paramref name="endpointId"/>, filtered like the rule, or null
    /// when <paramref name="webAppUrl"/> is not set. The backlog has no window, so the link asks for
    /// the widest period the view offers.
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

        var query = new List<string> { "endpointId=" + Uri.EscapeDataString(endpointId), "period=30d" };
        query.AddRange(condition.Statuses.Select(s => "status=" + Uri.EscapeDataString(s)));
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
}
