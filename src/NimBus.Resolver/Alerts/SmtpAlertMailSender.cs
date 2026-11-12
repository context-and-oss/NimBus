using System;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Resolver.Alerts;

/// <summary>
/// Sends endpoint alert mail over SMTP: Azure Communication Services, Microsoft 365, a SendGrid
/// relay, or Mailpit locally. Each delivery is bounded by <see cref="AlertMailOptions.Timeout"/>.
/// </summary>
public class SmtpAlertMailSender : IAlertMailSender
{
    private readonly AlertMailOptions _options;

    /// <summary>Creates a sender.</summary>
    /// <param name="options">The alert mail settings.</param>
    public SmtpAlertMailSender(AlertMailOptions options) => _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <inheritdoc />
    public async Task SendAsync(AlertMail mail, CancellationToken cancellationToken = default)
    {
        using var message = BuildMessage(mail);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);
        await DeliverAsync(message, timeout.Token).ConfigureAwait(false);
    }

    /// <summary>Builds the SMTP message: a plain-text body with an HTML alternative.</summary>
    /// <param name="mail">The composed mail.</param>
    public MailMessage BuildMessage(AlertMail mail)
    {
        ArgumentNullException.ThrowIfNull(mail);

        var message = new MailMessage
        {
            From = new MailAddress(_options.From!, _options.FromName),
            Subject = mail.Subject,
            SubjectEncoding = Encoding.UTF8,
            Body = mail.TextBody,
            BodyEncoding = Encoding.UTF8,
            IsBodyHtml = false,
        };

        try
        {
            message.To.Add(new MailAddress(mail.To));
            message.AlternateViews.Add(
                AlternateView.CreateAlternateViewFromString(mail.HtmlBody, Encoding.UTF8, MediaTypeNames.Text.Html));
            return message;
        }
        catch
        {
            message.Dispose();
            throw;
        }
    }

    /// <summary>Hands the built message to the SMTP server. Virtual so tests can observe delivery.</summary>
    /// <param name="message">The built message.</param>
    /// <param name="cancellationToken">Cancels the delivery.</param>
    protected virtual async Task DeliverAsync(MailMessage message, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(_options.Smtp.Host, _options.Smtp.Port)
        {
            EnableSsl = _options.Smtp.UseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };

        if (!string.IsNullOrEmpty(_options.Smtp.Username))
        {
            client.Credentials = new NetworkCredential(_options.Smtp.Username, _options.Smtp.Password);
        }

        await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);
    }
}
