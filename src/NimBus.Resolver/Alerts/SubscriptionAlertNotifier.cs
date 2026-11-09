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
/// Mails the endpoint's Alerts-tab subscribers about a failure, at most once per subscription
/// frequency window.
/// </summary>
/// <remarks>
/// A subscriber is mailed when its <c>type</c> includes <c>mail</c>, it has an address, it has
/// no payload filter (payload filters aren't evaluated), and its event-type filter is empty or
/// contains the event's type. With a positive <c>Frequency</c> (seconds) the notifier first
/// claims the window by moving <c>NotifiedAt</c> from the value it read to now, through
/// <see cref="ISubscriptionNotificationStore"/>; concurrent Resolver instances that read the
/// same value lose the compare, so a burst of failures sends one mail. A failed send releases
/// the claim. Each subscriber is handled on its own: one failure never stops the rest.
/// </remarks>
public sealed partial class SubscriptionAlertNotifier : IEndpointAlertNotifier
{
    /// <summary>The <c>NotifiedAt</c> format, shared with the WebApp and existing documents.</summary>
    public const string NotifiedAtFormat = "yyyy-MM-dd HH:mm";

    private const string MailType = "mail";
    private static readonly char[] TypeSeparators = [';', ','];

    private readonly ISubscriptionStore _subscriptions;
    private readonly ISubscriptionNotificationStore _notifications;
    private readonly IAlertMailSender _sender;
    private readonly AlertMailComposer _composer;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<SubscriptionAlertNotifier> _logger;

    /// <summary>Creates a notifier.</summary>
    /// <param name="subscriptions">Reads the endpoint's subscriptions.</param>
    /// <param name="notifications">Claims and releases each subscription's frequency window.</param>
    /// <param name="sender">Delivers the mail.</param>
    /// <param name="composer">Writes the mail.</param>
    /// <param name="timeProvider">The clock.</param>
    /// <param name="logger">The logger.</param>
    public SubscriptionAlertNotifier(
        ISubscriptionStore subscriptions,
        ISubscriptionNotificationStore notifications,
        IAlertMailSender sender,
        AlertMailComposer composer,
        TimeProvider timeProvider,
        ILogger<SubscriptionAlertNotifier> logger)
    {
        _subscriptions = subscriptions ?? throw new ArgumentNullException(nameof(subscriptions));
        _notifications = notifications ?? throw new ArgumentNullException(nameof(notifications));
        _sender = sender ?? throw new ArgumentNullException(nameof(sender));
        _composer = composer ?? throw new ArgumentNullException(nameof(composer));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
                LogEndpointAlertsCouldNotMailSubscription(_logger, ex, subscription.Id, context.EndpointId, context.EventId);
            }
        }
    }

    private bool Matches(EndpointSubscription subscription, EndpointAlertContext context)
    {
        if (!IncludesMail(subscription.Type))
        {
            LogEndpointAlertsSubscriptionOnHasType(_logger, subscription.Id, context.EndpointId, subscription.Type);
            return false;
        }

        if (!string.IsNullOrWhiteSpace(subscription.Payload))
        {
            LogEndpointAlertsSkippingPayloadFilteredSubscription(_logger, subscription.Id, context.EndpointId);
            return false;
        }

        if (string.IsNullOrWhiteSpace(subscription.Mail))
        {
            return false;
        }

        return subscription.EventTypes is not { Count: > 0 } eventTypes
            || (context.EventTypeId is not null && eventTypes.Contains(context.EventTypeId, StringComparer.Ordinal));
    }

    private async Task NotifySubscriberAsync(EndpointSubscription subscription, EndpointAlertContext context, CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var previous = subscription.NotifiedAt;
        var stamp = now.UtcDateTime.ToString(NotifiedAtFormat, CultureInfo.InvariantCulture);

        if (subscription.Frequency <= 0)
        {
            // No window to protect: every failure mails, and NotifiedAt is only a record.
            await _sender.SendAsync(_composer.Compose(subscription, context), cancellationToken).ConfigureAwait(false);
            LogMailed(subscription, context);
            await TrySetNotifiedAtQuietlyAsync(subscription, previous, stamp).ConfigureAwait(false);
            return;
        }

        if (IsInsideWindow(previous, now, subscription.Frequency))
        {
            return;
        }

        // Composed before the claim, so a composing error can't hold the window.
        var mail = _composer.Compose(subscription, context);
        if (!await _notifications.TrySetNotifiedAtAsync(subscription.Id, previous, stamp, cancellationToken).ConfigureAwait(false))
        {
            // Another Resolver instance claimed this window, or the subscription was deleted.
            return;
        }

        try
        {
            await _sender.SendAsync(mail, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Give the window back so the next failure can try again.
            await TrySetNotifiedAtQuietlyAsync(subscription, stamp, previous).ConfigureAwait(false);
            throw;
        }

        LogMailed(subscription, context);
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

    private void LogMailed(EndpointSubscription subscription, EndpointAlertContext context)
        => LogEndpointAlertsMailedSubscriptionOnAbout(_logger, subscription.Id, context.EndpointId, context.EventId, context.Status);

    private static bool IncludesMail(string? type)
        => type is not null
           && type.Split(TypeSeparators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
               .Any(part => string.Equals(part, MailType, StringComparison.OrdinalIgnoreCase));

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

    [LoggerMessage(Level = LogLevel.Warning, Message = "Endpoint alerts: could not read the alert subscriptions of {EndpointId}; no mail sent for event {EventId}.")]
    private static partial void LogEndpointAlertsCouldNotRead(ILogger logger, Exception exception, string endpointId, string eventId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Endpoint alerts: could not mail subscription {SubscriptionId} on {EndpointId} about event {EventId}.")]
    private static partial void LogEndpointAlertsCouldNotMailSubscription(ILogger logger, Exception exception, string subscriptionId, string endpointId, string eventId);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Endpoint alerts: subscription {SubscriptionId} on {EndpointId} has type {Type}; only mail is delivered.")]
    private static partial void LogEndpointAlertsSubscriptionOnHasType(ILogger logger, string subscriptionId, string endpointId, string type);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Endpoint alerts: skipping payload-filtered subscription {SubscriptionId} on {EndpointId}; payload filters aren't evaluated.")]
    private static partial void LogEndpointAlertsSkippingPayloadFilteredSubscription(ILogger logger, string subscriptionId, string endpointId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Endpoint alerts: could not update NotifiedAt on subscription {SubscriptionId}.")]
    private static partial void LogEndpointAlertsCouldNotUpdateNotifiedAt(ILogger logger, Exception exception, string subscriptionId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Endpoint alerts: mailed subscription {SubscriptionId} on {EndpointId} about event {EventId} ({Status}).")]
    private static partial void LogEndpointAlertsMailedSubscriptionOnAbout(ILogger logger, string subscriptionId, string endpointId, string eventId, MessageStore.ResolutionStatus status);
}
