using System;

namespace NimBus.Extensions.Notifications;

/// <summary>
/// Options for <see cref="WebhookChannel"/> — an HTTP POST to an arbitrary endpoint.
/// </summary>
public sealed class WebhookChannelOptions : NotificationChannelOptions
{
    /// <summary>
    /// The absolute URL the notification is POSTed to.
    /// </summary>
    public string Url { get; set; } = null!;

    internal override void Validate()
    {
        if (string.IsNullOrWhiteSpace(Url) || !Uri.TryCreate(Url, UriKind.Absolute, out _))
        {
            throw new ArgumentException(
                "WebhookChannelOptions.Url must be a non-empty absolute URL.", nameof(Url));
        }
    }
}
