using System;
using System.Collections.Generic;
using System.Net.Mail;

namespace NimBus.Resolver.Alerts;

/// <summary>
/// Settings for mailing endpoint alert subscribers, bound from <see cref="SectionName"/>.
/// Alert mail is off until both <see cref="AlertSmtpOptions.Host"/> and <see cref="From"/> are set.
/// </summary>
public sealed class AlertMailOptions
{
    /// <summary>The configuration section the options bind from.</summary>
    public const string SectionName = "NimBus:Alerts:Mail";

    /// <summary>The SMTP server.</summary>
    public AlertSmtpOptions Smtp { get; set; } = new();

    /// <summary>The sender address.</summary>
    public string? From { get; set; }

    /// <summary>The sender display name. Default: <c>NimBus</c>.</summary>
    public string FromName { get; set; } = "NimBus";

    /// <summary>
    /// The management WebApp's public URL, used to link to the event and the endpoint.
    /// Links are left out when unset.
    /// </summary>
    public string? WebAppUrl { get; set; }

    /// <summary>How long one delivery may take. Default: 10 seconds.</summary>
    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Whether the mail includes the (truncated) error text. Default: true.</summary>
    public bool IncludeErrorText { get; set; } = true;

    /// <summary>Whether both an SMTP host and a sender address are set.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Smtp.Host) && !string.IsNullOrWhiteSpace(From);

    /// <summary>Returns a message for every invalid setting; empty when the options are valid.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (!MailAddress.TryCreate(From, out _))
        {
            errors.Add($"{SectionName}:From must be a mail address.");
        }

        if (Smtp.Port is < 1 or > 65535)
        {
            errors.Add($"{SectionName}:Smtp:Port must be between 1 and 65535.");
        }

        if (Timeout <= TimeSpan.Zero)
        {
            errors.Add($"{SectionName}:Timeout must be positive.");
        }

        if (!string.IsNullOrWhiteSpace(WebAppUrl)
            && !(Uri.TryCreate(WebAppUrl, UriKind.Absolute, out var url) && (url.Scheme == Uri.UriSchemeHttps || url.Scheme == Uri.UriSchemeHttp)))
        {
            errors.Add($"{SectionName}:WebAppUrl must be an absolute http or https URL.");
        }

        return errors;
    }
}

/// <summary>SMTP connection settings for endpoint alert mail.</summary>
public sealed class AlertSmtpOptions
{
    /// <summary>The SMTP host.</summary>
    public string? Host { get; set; }

    /// <summary>The SMTP port. Default: 587.</summary>
    public int Port { get; set; } = 587;

    /// <summary>Whether to use TLS. Default: true.</summary>
    public bool UseSsl { get; set; } = true;

    /// <summary>The SMTP user name; omit for an anonymous relay.</summary>
    public string? Username { get; set; }

    /// <summary>The SMTP password; omit for an anonymous relay.</summary>
    public string? Password { get; set; }
}
