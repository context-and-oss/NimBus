using System.Net;
using System.Net.Mail;
using Microsoft.AspNetCore.Identity.UI.Services;
using Microsoft.Extensions.Logging;

namespace NimBus.Extensions.Identity.Services;

/// <summary>
/// Sends emails via SMTP for account confirmation and password reset.
/// </summary>
public partial class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;

    public SmtpEmailSender(NimBusIdentityOptions identityOptions, ILogger<SmtpEmailSender> logger)
    {
        _options = identityOptions.Smtp;
        _logger = logger;
    }

    public async Task SendEmailAsync(string email, string subject, string htmlMessage)
    {
        if (string.IsNullOrEmpty(_options.Host))
        {
            LogSmtpNotConfigured(_logger, email, subject);
            return;
        }

        using var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.UseSsl,
            Credentials = string.IsNullOrEmpty(_options.Username)
                ? null
                : new NetworkCredential(_options.Username, _options.Password)
        };

        var message = new MailMessage
        {
            From = new MailAddress(_options.FromAddress, _options.FromName),
            Subject = subject,
            Body = htmlMessage,
            IsBodyHtml = true
        };
        message.To.Add(email);

        await client.SendMailAsync(message);
        LogEmailSent(_logger, email, subject);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "SMTP not configured. Email to {Email} with subject '{Subject}' was not sent. Configure NimBusIdentity:Smtp to enable email delivery.")]
    private static partial void LogSmtpNotConfigured(ILogger logger, string email, string subject);

    [LoggerMessage(Level = LogLevel.Information, Message = "Email sent to {Email}: {Subject}")]
    private static partial void LogEmailSent(ILogger logger, string email, string subject);
}
