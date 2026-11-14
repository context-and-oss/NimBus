using System;

namespace NimBus.Extensions.Notifications;

/// <summary>
/// Options for <see cref="TeamsChannel"/> — posts an Adaptive Card to a Microsoft Teams
/// Incoming Webhook (connector) URL.
/// </summary>
public sealed class TeamsChannelOptions : NotificationChannelOptions
{
    /// <summary>
    /// The Teams Incoming Webhook (connector) URL to POST the Adaptive Card to.
    /// </summary>
    public string ConnectorUrl { get; set; } = null!;

    internal override void Validate()
    {
        if (string.IsNullOrWhiteSpace(ConnectorUrl) || !Uri.TryCreate(ConnectorUrl, UriKind.Absolute, out _))
        {
            throw new ArgumentException(
                "TeamsChannelOptions.ConnectorUrl must be a non-empty absolute URL.", nameof(ConnectorUrl));
        }
    }
}
