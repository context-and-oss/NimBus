using System;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Resolver.Alerts;

/// <summary>One alert for one webhook subscription.</summary>
/// <param name="SubscriptionId">The subscription's id; its signing secret is derived from it.</param>
/// <param name="Url">The subscription's URL.</param>
/// <param name="Context">The failure. Only its ids, status and time are sent.</param>
public sealed record AlertWebhook(string SubscriptionId, Uri Url, EndpointAlertContext Context);

/// <summary>Delivers an endpoint alert to a webhook.</summary>
public interface IAlertWebhookSender
{
    /// <summary>Whether <paramref name="url"/> is a URL this sender delivers to.</summary>
    /// <param name="url">A subscription's URL.</param>
    bool CanSendTo(string? url);

    /// <summary>Sends <paramref name="webhook"/>. Throws when the receiver does not accept it.</summary>
    /// <param name="webhook">The alert to send.</param>
    /// <param name="cancellationToken">Cancels the delivery.</param>
    Task SendAsync(AlertWebhook webhook, CancellationToken cancellationToken = default);
}

/// <summary>A webhook receiver answered with a status that is not a success.</summary>
public sealed class AlertWebhookDeliveryException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AlertWebhookDeliveryException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message. It must not contain the URL's path or query.</param>
    public AlertWebhookDeliveryException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message. It must not contain the URL's path or query.</param>
    /// <param name="innerException">The cause.</param>
    public AlertWebhookDeliveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>A webhook's host resolves only to addresses the Resolver may not connect to.</summary>
public sealed class AlertWebhookBlockedException : Exception
{
    /// <summary>Creates the exception.</summary>
    public AlertWebhookBlockedException()
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    public AlertWebhookBlockedException(string message)
        : base(message)
    {
    }

    /// <summary>Creates the exception.</summary>
    /// <param name="message">The message.</param>
    /// <param name="innerException">The cause.</param>
    public AlertWebhookBlockedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
