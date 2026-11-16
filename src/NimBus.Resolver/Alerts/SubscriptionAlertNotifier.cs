using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using NimBus.MessageStore.Abstractions;
using NimBus.MessageStore.States;

namespace NimBus.Resolver.Alerts;

/// <summary>
/// Notifies the endpoint's Alerts-tab subscribers about a failure, by mail, by webhook or both,
/// at most once per subscription frequency window.
/// </summary>
/// <remarks>
/// A subscriber is notified when its <c>type</c> names a way of delivery that is switched on
/// (<c>mail</c> with an address, <c>webhook</c> with a URL the sender accepts), it has no payload
/// filter (payload filters aren't evaluated), and its event-type filter is empty or contains the
/// event's type. With a positive <c>Frequency</c> (seconds) the notifier first claims the window
/// by moving <c>NotifiedAt</c> from the value it read to now, through
/// <see cref="ISubscriptionNotificationStore"/>; concurrent Resolver instances that read the
/// same value lose the compare, so a burst of failures notifies once. The claim is released when
/// nothing could be delivered. Each subscriber is handled on its own: one failure never stops
/// the rest.
/// </remarks>
public sealed partial class SubscriptionAlertNotifier : IEndpointAlertNotifier, IDisposable
{
    /// <summary>The <c>NotifiedAt</c> format, shared with the WebApp and existing documents.</summary>
    public const string NotifiedAtFormat = "yyyy-MM-dd HH:mm";

    private const string MailChannel = "mail";
    private const string WebhookChannel = "webhook";

    private readonly ISubscriptionStore _subscriptions;
    private readonly ISubscriptionNotificationStore _notifications;
    private readonly IAlertMailSender? _mailSender;
    private readonly AlertMailComposer? _composer;
    private readonly IAlertWebhookSender? _webhookSender;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SubscriptionAlertNotifier> _logger;

    /// <summary>Creates a notifier. At least one of mail and webhook delivery must be given.</summary>
    /// <param name="subscriptions">Reads the endpoint's subscriptions.</param>
    /// <param name="notifications">Claims and releases each subscription's frequency window.</param>
    /// <param name="mailSender">Delivers the mail; null while alert mail is off.</param>
    /// <param name="composer">Writes the mail; required with <paramref name="mailSender"/>.</param>
    /// <param name="webhookSender">Delivers to webhooks; null while alert webhooks are off.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    public SubscriptionAlertNotifier(
        ISubscriptionStore subscriptions,
        ISubscriptionNotificationStore notifications,
        IAlertMailSender? mailSender,
        AlertMailComposer? composer,
        IAlertWebhookSender? webhookSender,
        TimeProvider timeProvider,
        ILogger<SubscriptionAlertNotifier> logger)
    {
        _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        if (mailSender is null && webhookSender is null)
        {
            throw new ArgumentException("A notifier needs a mail sender or a webhook sender.", nameof(mailSender));
        }

        if (mailSender is not null && composer is null)
        {
            throw new ArgumentNullException(nameof(composer));
        }

        _mailSender = mailSender;
        _composer = composer;
        _webhookSender = webhookSender;
    }

    /// <inheritdoc />
    public async Task NotifyAsync(EndpointAlertContext context, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);

        IEnumerable<EndpointSubscription> subscriptions;
        try
        {
            subscriptions = await _subscriptions.GetSubscriptionsOnEndpoint(context.EndpointId).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogEndpointAlertsCouldNotRead(_logger, ex, context.EndpointId, context.EventId);
            return;
        }

        foreach (var subscription in subscriptions.Where(s => Matches(s, context)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await NotifySubscriberAsync(subscription, context, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogEndpointAlertsCouldNotNotifySubscription(_logger, ex, subscription.Id, context.EndpointId, context.EventId);
            }
        }
    }

    /// <summary>Disposes the senders this notifier was given, when they hold resources.</summary>
    public void Dispose()
    {
        (_mailSender as IDisposable)?.Dispose();
        (_webhookSender as IDisposable)?.Dispose();
    }

    private bool Matches(EndpointSubscription subscription, EndpointAlertContext context)
    {
        var byMail = _mailSender is not null && EndpointSubscriptionTypes.Includes(subscription.Type, EndpointSubscriptionTypes.Mail);
        var byWebhook = _webhookSender is not null && EndpointSubscriptionTypes.Includes(subscription.Type, EndpointSubscriptionTypes.Webhook);
        if (!byMail && !byWebhook)
        {
            LogEndpointAlertsSubscriptionOnHasType(_logger, subscription.Id, context.EndpointId, subscription.Type);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(subscription.Payload))
        {
            LogEndpointAlertsSkippingPayloadFilteredSubscription(_logger, subscription.Id, context.EndpointId);
            return false;
        }

        return subscription.EventTypes is not { Count: > 0 } eventTypes
            || (context.EventTypeId is not null && eventTypes.Contains(context.EventTypeId, StringComparer.Ordinal));
    }

    /// <summary>
    /// The ways this subscription is notified now: mail when it names mail and has an address,
    /// webhook when it names webhook and has a URL the sender accepts. The mail is composed here,
    /// before any claim, so a composing error can't hold the window.
    /// </summary>
    private List<Delivery> DeliveriesFor(EndpointSubscription subscription, EndpointAlertContext context)
    {
        var deliveries = new List<Delivery>(2);

        if (_mailSender is { } mailSender
            && EndpointSubscriptionTypes.Includes(subscription.Type, EndpointSubscriptionTypes.Mail)
            && !string.IsNullOrWhiteSpace(subscription.Mail))
        {
            var mail = _composer!.Compose(subscription, context);
            deliveries.Add(new Delivery(MailChannel, ct => mailSender.SendAsync(mail, ct)));
        }

        if (_webhookSender is { } webhookSender
            && EndpointSubscriptionTypes.Includes(subscription.Type, EndpointSubscriptionTypes.Webhook))
        {
            if (webhookSender.CanSendTo(subscription.Url))
            {
                var webhook = new AlertWebhook(subscription.Id, new Uri(subscription.Url!), context);
                deliveries.Add(new Delivery(WebhookChannel, ct => webhookSender.SendAsync(webhook, ct)));
            }
            else
            {
                LogEndpointAlertsSkippingWebhookUrl(_logger, subscription.Id, context.EndpointId);
            }
        }

        return deliveries;
    }

    private async Task NotifySubscriberAsync(EndpointSubscription subscription, EndpointAlertContext context, CancellationToken cancellationToken)
    {
        var deliveries = DeliveriesFor(subscription, context);
        if (deliveries.Count == 0)
        {
            return;
        }

        var now = _timeProvider.GetUtcNow();
        var previous = subscription.NotifiedAt;
        var stamp = now.UtcDateTime.ToString(NotifiedAtFormat, CultureInfo.InvariantCulture);

        if (subscription.Frequency <= 0)
        {
            // No window to protect: every failure notifies, and NotifiedAt is only a record.
            if (await DeliverAsync(deliveries, subscription, context, cancellationToken).ConfigureAwait(false))
            {
                await TrySetNotifiedAtQuietlyAsync(subscription, previous, stamp).ConfigureAwait(false);
            }

            return;
        }

        if (IsInsideWindow(previous, now, subscription.Frequency))
        {
            return;
        }

        if (!await _notifications.TrySetNotifiedAtAsync(subscription.Id, previous, stamp, cancellationToken).ConfigureAwait(false))
        {
            // Another Resolver instance claimed this window, or the subscription was deleted.
            return;
        }

        bool delivered;
        try
        {
            delivered = await DeliverAsync(deliveries, subscription, context, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await TrySetNotifiedAtQuietlyAsync(subscription, stamp, previous).ConfigureAwait(false);
            throw;
        }

        if (!delivered)
        {
            // Nothing went out: give the window back so the next failure can try again.
            await TrySetNotifiedAtQuietlyAsync(subscription, stamp, previous).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Sends through every delivery. A failing one is logged and does not stop the next.
    /// Returns whether at least one went out.
    /// </summary>
    private async Task<bool> DeliverAsync(
        List<Delivery> deliveries, EndpointSubscription subscription, EndpointAlertContext context, CancellationToken cancellationToken)
    {
        var delivered = false;
        foreach (var delivery in deliveries)
        {
            try
            {
                await delivery.SendAsync(cancellationToken).ConfigureAwait(false);
                delivered = true;
                LogEndpointAlertsNotifiedSubscription(_logger, subscription.Id, context.EndpointId, delivery.Channel, context.EventId, context.Status);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                LogEndpointAlertsCouldNotDeliver(_logger, ex, subscription.Id, context.EndpointId, delivery.Channel, context.EventId);
            }
        }

        return delivered;
    }

    private async Task TrySetNotifiedAtQuietlyAsync(EndpointSubscription subscription, string? expected, string? value)
    {
        try
        {
            await _notifications.TrySetNotifiedAtAsync(subscription.Id, expected, value, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            LogEndpointAlertsCouldNotUpdateNotifiedAt(_logger, ex, subscription.Id);
        }
    }

    private static bool IsInsideWindow(string? notifiedAt, DateTimeOffset now, int frequencySeconds)
    {
        if (string.IsNullOrWhiteSpace(notifiedAt)
            || !DateTime.TryParseExact(notifiedAt, NotifiedAtFormat, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var last))
        {
            return false;
        }

        return now.UtcDateTime - last < TimeSpan.FromSeconds(frequencySeconds);
    }

    private sealed record Delivery(string Channel, Func<CancellationToken, Task> SendAsync);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Endpoint alerts: could not read the alert subscriptions of {EndpointId}; nobody was notified about event {EventId}.")]
    private static partial void LogEndpointAlertsCouldNotRead(ILogger logger, Exception exception, string endpointId, string eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Endpoint alerts: could not notify subscription {SubscriptionId} on {EndpointId} about event {EventId}.")]
    private static partial void LogEndpointAlertsCouldNotNotifySubscription(ILogger logger, Exception exception, string subscriptionId, string endpointId, string eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Endpoint alerts: could not notify subscription {SubscriptionId} on {EndpointId} by {Channel} about event {EventId}.")]
    private static partial void LogEndpointAlertsCouldNotDeliver(ILogger logger, Exception exception, string subscriptionId, string endpointId, string channel, string eventId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Endpoint alerts: subscription {SubscriptionId} on {EndpointId} has type {Type}, which is not delivered or is switched off.")]
    private static partial void LogEndpointAlertsSubscriptionOnHasType(ILogger logger, string subscriptionId, string endpointId, string type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Endpoint alerts: skipping payload-filtered subscription {SubscriptionId} on {EndpointId}; payload filters aren't evaluated.")]
    private static partial void LogEndpointAlertsSkippingPayloadFilteredSubscription(ILogger logger, string subscriptionId, string endpointId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Endpoint alerts: skipping the webhook of subscription {SubscriptionId} on {EndpointId}; its URL is not one alerts are sent to (https is required).")]
    private static partial void LogEndpointAlertsSkippingWebhookUrl(ILogger logger, string subscriptionId, string endpointId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Endpoint alerts: could not update NotifiedAt on subscription {SubscriptionId}.")]
    private static partial void LogEndpointAlertsCouldNotUpdateNotifiedAt(ILogger logger, Exception exception, string subscriptionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Endpoint alerts: notified subscription {SubscriptionId} on {EndpointId} by {Channel} about event {EventId} ({Status}).")]
    private static partial void LogEndpointAlertsNotifiedSubscription(ILogger logger, string subscriptionId, string endpointId, string channel, string eventId, MessageStore.ResolutionStatus status);
}
