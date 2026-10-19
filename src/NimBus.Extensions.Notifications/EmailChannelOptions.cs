using System;

namespace NimBus.Extensions.Notifications;

/// <summary>
/// Options for <see cref="EmailChannel"/>. Supports two interchangeable providers:
/// <see cref="EmailProvider.SendGrid"/> (HTTPS API key) and <see cref="EmailProvider.Smtp"/>
/// (host/port/credentials). Exactly one provider is active per registration.
/// </summary>
public sealed class EmailChannelOptions : NotificationChannelOptions
{
    /// <summary>The delivery provider. Default: <see cref="EmailProvider.SendGrid"/>.</summary>
    public EmailProvider Provider { get; set; } = EmailProvider.SendGrid;

    /// <summary>SendGrid API key (required when <see cref="Provider"/> is <see cref="EmailProvider.SendGrid"/>).</summary>
    public string? ApiKey { get; set; }

    /// <summary>SMTP host (required when <see cref="Provider"/> is <see cref="EmailProvider.Smtp"/>).</summary>
    public string? SmtpHost { get; set; }

    /// <summary>SMTP port. Default: 587.</summary>
    public int SmtpPort { get; set; } = 587;

    /// <summary>SMTP username (optional; omit for anonymous relays).</summary>
    public string? SmtpUser { get; set; }

    /// <summary>SMTP password (optional; omit for anonymous relays).</summary>
    public string? SmtpPassword { get; set; }

    /// <summary>Whether to enable SSL/TLS on the SMTP connection. Default: true.</summary>
    public bool SmtpUseSsl { get; set; } = true;

    /// <summary>The From address.</summary>
    public string From { get; set; } = null!;

    /// <summary>One or more recipient addresses.</summary>
    public string[] To { get; set; } = null!;

    internal override void Validate()
    {
        if (string.IsNullOrWhiteSpace(From))
        {
            throw new ArgumentException("EmailChannelOptions.From must be set.", nameof(From));
        }

        if (To == null || To.Length == 0 || Array.TrueForAll(To, string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("EmailChannelOptions.To must contain at least one address.", nameof(To));
        }

        switch (Provider)
        {
            case EmailProvider.SendGrid when string.IsNullOrWhiteSpace(ApiKey):
                throw new ArgumentException(
                    "EmailChannelOptions.ApiKey must be set when Provider is SendGrid.", nameof(ApiKey));
            case EmailProvider.Smtp when string.IsNullOrWhiteSpace(SmtpHost):
                throw new ArgumentException(
                    "EmailChannelOptions.SmtpHost must be set when Provider is Smtp.", nameof(SmtpHost));
        }
    }
}
