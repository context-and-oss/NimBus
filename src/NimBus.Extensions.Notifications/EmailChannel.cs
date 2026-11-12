using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using System;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Mail;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Extensions.Notifications;

/// <summary>
/// Notification channel that emails alerts. Supports two interchangeable providers:
/// <see cref="EmailProvider.SendGrid"/> (HTTPS <c>v3/mail/send</c> API with bearer auth) and
/// <see cref="EmailProvider.Smtp"/> (<see cref="SmtpClient"/>). The subject is derived from the
/// notification title; the body from the message and error details (or a custom template).
/// </summary>
public partial class EmailChannel : INotificationChannel
{
    private const string SendGridEndpoint = "https://api.sendgrid.com/v3/mail/send";

    private readonly EmailChannelOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public EmailChannel(EmailChannelOptions options, HttpClient httpClient, ILogger<EmailChannel>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? NullLogger<EmailChannel>.Instance;
    }

    public async Task SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        if (_options.Provider == EmailProvider.SendGrid)
        {
            await SendViaSendGridAsync(notification, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            using var message = BuildSmtpMessage(notification);
            await SendViaSmtpAsync(message, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Subject line for an email notification: <c>[{Severity}] {Title}</c>.</summary>
    [SuppressMessage("Performance", "CA1822:Mark members as static", Justification = "Public instance API; making it static would break callers.")]
    public string BuildSubject(Notification notification) =>
        $"[{notification.Severity}] {notification.Title}";

    /// <summary>Plain-text body for an email notification (template, or message + error details).</summary>
    public string BuildBody(Notification notification)
    {
        if (_options.Template != null)
        {
            return TemplateRenderer.Render(_options.Template, notification);
        }

        var builder = new StringBuilder();
        builder.AppendLine(notification.Message);
        if (!string.IsNullOrEmpty(notification.ErrorDetails))
        {
            builder.AppendLine();
            builder.AppendLine(notification.ErrorDetails);
        }

        return builder.ToString();
    }

    private async Task SendViaSendGridAsync(Notification notification, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.Timeout);

        using var request = BuildSendGridRequest(notification);
        using var response = await _httpClient.SendAsync(request, timeoutCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            LogSendGridEmailNotificationReturnedNonSuccess(_logger, (int)response.StatusCode);

            throw new NotificationDeliveryException(
                $"SendGrid delivery failed with status {(int)response.StatusCode}.");
        }
    }

    /// <summary>Builds the SendGrid <c>v3/mail/send</c> request, including bearer auth.</summary>
    public HttpRequestMessage BuildSendGridRequest(Notification notification)
    {
        var payload = new
        {
            personalizations = new[]
            {
                new { to = _options.To.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => new { email = a }).ToArray() },
            },
            from = new { email = _options.From },
            subject = BuildSubject(notification),
            content = new[]
            {
                new { type = "text/plain", value = BuildBody(notification) },
            },
        };

        var request = new HttpRequestMessage(HttpMethod.Post, SendGridEndpoint)
        {
            Content = new StringContent(JsonConvert.SerializeObject(payload), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        return request;
    }

    /// <summary>Builds the <see cref="MailMessage"/> sent via SMTP, with every recipient added.</summary>
    public MailMessage BuildSmtpMessage(Notification notification)
    {
        var message = new MailMessage
        {
            From = new MailAddress(_options.From),
            Subject = BuildSubject(notification),
            Body = BuildBody(notification),
        };

        foreach (var address in _options.To.Where(a => !string.IsNullOrWhiteSpace(a)))
        {
            message.To.Add(address);
        }

        return message;
    }

    /// <summary>
    /// Sends a built <see cref="MailMessage"/> via SMTP. Virtual so tests can observe delivery
    /// without standing up a live SMTP server.
    /// </summary>
    protected virtual async Task SendViaSmtpAsync(MailMessage message, CancellationToken cancellationToken)
    {
        using var client = new SmtpClient(_options.SmtpHost, _options.SmtpPort)
        {
            EnableSsl = _options.SmtpUseSsl,
        };

        if (!string.IsNullOrEmpty(_options.SmtpUser))
        {
            client.Credentials = new System.Net.NetworkCredential(_options.SmtpUser, _options.SmtpPassword);
        }

        await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "SendGrid email notification returned non-success status {StatusCode}.")]
    private static partial void LogSendGridEmailNotificationReturnedNonSuccess(ILogger logger, int statusCode);
}
