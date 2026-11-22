using NimBus.Alerts;
using NimBus.MessageStore.States;

namespace NimBus.WebApp.Services.AlertRules;

/// <summary>
/// The channels alert rule notices go out on in this WebApp: mail when <c>NimBus:Alerts:Mail</c> is
/// configured, webhooks when <c>NimBus:Alerts:Webhook</c> has a signing key. A channel that is off
/// is null.
/// </summary>
public sealed class AlertRuleDelivery
{
    private readonly IAlertMailSender? _mailSender;
    private readonly AlertMailComposer? _mailComposer;
    private readonly IAlertRuleWebhookSender? _webhookSender;

    /// <summary>Creates the delivery.</summary>
    /// <param name="mailSender">The mail sender, or null when alert mail is off.</param>
    /// <param name="mailComposer">The mail composer, with the mail sender.</param>
    /// <param name="webhookSender">The webhook sender, or null when alert webhooks are off.</param>
    /// <param name="webAppUrl">The WebApp's base URL for links in notices, when known.</param>
    public AlertRuleDelivery(
        IAlertMailSender? mailSender,
        AlertMailComposer? mailComposer,
        IAlertRuleWebhookSender? webhookSender,
        string? webAppUrl)
    {
        _mailSender = mailSender;
        _mailComposer = mailComposer;
        _webhookSender = webhookSender;
        WebAppUrl = webAppUrl;
    }

    /// <summary>The WebApp's base URL for links in notices, when known.</summary>
    public string? WebAppUrl { get; }

    /// <summary>Whether mail targets can be told.</summary>
    public bool MailEnabled => _mailSender is not null && _mailComposer is not null;

    /// <summary>Whether webhook targets can be told.</summary>
    public bool WebhookEnabled => _webhookSender is not null;

    /// <summary>Whether <paramref name="url"/> is a URL the webhook channel delivers to.</summary>
    /// <param name="url">A target's URL.</param>
    public bool CanSendTo(string? url) => _webhookSender?.CanSendTo(url) == true;

    /// <summary>Tells <paramref name="target"/> about <paramref name="notice"/>; throws when it fails or the channel is off.</summary>
    /// <param name="notice">The notice.</param>
    /// <param name="target">The target.</param>
    /// <param name="cancellationToken">Cancels the delivery.</param>
    public Task DeliverAsync(AlertRuleNotice notice, AlertRuleTarget target, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notice);
        ArgumentNullException.ThrowIfNull(target);

        switch (target.Type)
        {
            case AlertRuleTargetType.Mail when MailEnabled && !string.IsNullOrWhiteSpace(target.Address):
                return _mailSender!.SendAsync(_mailComposer!.ComposeRuleNotice(notice, target.Address), cancellationToken);
            case AlertRuleTargetType.Webhook when _webhookSender is not null:
                return _webhookSender.SendRuleNoticeAsync(notice, target, cancellationToken);
            default:
                throw new InvalidOperationException(
                    $"Alert rule {target.Type.ToString().ToLowerInvariant()} delivery is off in this WebApp.");
        }
    }
}
