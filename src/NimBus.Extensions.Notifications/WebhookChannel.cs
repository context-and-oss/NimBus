using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using System;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Extensions.Notifications;

/// <summary>
/// Notification channel that sends an HTTP POST to a configured URL. The body is either a
/// caller-supplied <see cref="NotificationChannelOptions.Template"/> (token-substituted) or a
/// default JSON serialization of the <see cref="Notification"/>. A non-success HTTP status is
/// logged and surfaced as a <see cref="NotificationDeliveryException"/> (caught upstream so it
/// never affects message processing).
/// </summary>
public partial class WebhookChannel : INotificationChannel
{
    private readonly WebhookChannelOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public WebhookChannel(WebhookChannelOptions options, HttpClient httpClient, ILogger<WebhookChannel>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? NullLogger<WebhookChannel>.Instance;
    }

    public async Task SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        var body = _options.Template != null
            ? TemplateRenderer.Render(_options.Template, notification, jsonEncodeValues: true)
            : JsonConvert.SerializeObject(notification);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.Timeout);

        using var response = await _httpClient.PostAsync(_options.Url, content, timeoutCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            LogWebhookNotificationToReturnedNonSuccess(_logger, SafeHost(_options.Url), (int)response.StatusCode);

            throw new NotificationDeliveryException(
                $"Webhook delivery failed with status {(int)response.StatusCode}.");
        }
    }

    private static string SafeHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(invalid-url)";

    [LoggerMessage(Level = LogLevel.Warning, Message = "Webhook notification to {Host} returned non-success status {StatusCode}.")]
    private static partial void LogWebhookNotificationToReturnedNonSuccess(ILogger logger, string host, int statusCode);
}
