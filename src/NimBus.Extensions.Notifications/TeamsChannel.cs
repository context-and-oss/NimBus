using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NimBus.Extensions.Notifications;

/// <summary>
/// Notification channel that posts an Adaptive Card to a Microsoft Teams Incoming Webhook
/// (connector) URL. The card surfaces the severity, title, message and the key identifiers /
/// error details as facts, colour-coded by severity. A custom
/// <see cref="NotificationChannelOptions.Template"/> overrides the card body.
/// </summary>
public class TeamsChannel : INotificationChannel
{
    // Teams Incoming Webhooks reject payloads beyond ~28 KB; keep the long fields well under that.
    private const int MaxFieldLength = 24 * 1024;

    private readonly TeamsChannelOptions _options;
    private readonly HttpClient _httpClient;
    private readonly ILogger _logger;

    public TeamsChannel(TeamsChannelOptions options, HttpClient httpClient, ILogger<TeamsChannel>? logger = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _logger = logger ?? NullLogger<TeamsChannel>.Instance;
    }

    public async Task SendAsync(Notification notification, CancellationToken cancellationToken = default)
    {
        var body = _options.Template != null
            ? TemplateRenderer.Render(_options.Template, notification, jsonEncodeValues: true)
            : BuildAdaptiveCard(notification);

        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.Timeout);

        using var response = await _httpClient.PostAsync(_options.ConnectorUrl, content, timeoutCts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Teams notification to {Host} returned non-success status {StatusCode}.",
                SafeHost(_options.ConnectorUrl), (int)response.StatusCode);

            throw new NotificationDeliveryException(
                $"Teams delivery failed with status {(int)response.StatusCode}.");
        }
    }

    /// <summary>
    /// Builds the Adaptive Card (schema 1.4) message envelope as a JSON string.
    /// </summary>
    public static string BuildAdaptiveCard(Notification notification)
    {
        var facts = new List<object>();
        AddFact(facts, "Severity", notification.Severity.ToString());
        AddFact(facts, "Event Id", notification.EventId);
        AddFact(facts, "Event Type", notification.EventTypeId);
        AddFact(facts, "Message Id", notification.MessageId);
        AddFact(facts, "Correlation Id", notification.CorrelationId);
        AddFact(facts, "Error", Truncate(notification.ErrorDetails));

        var cardBody = new List<object>
        {
            new
            {
                type = "TextBlock",
                text = notification.Title ?? string.Empty,
                weight = "Bolder",
                size = "Large",
                color = SeverityColor(notification.Severity),
                wrap = true,
            },
            new
            {
                type = "TextBlock",
                text = Truncate(notification.Message) ?? string.Empty,
                wrap = true,
            },
            new
            {
                type = "FactSet",
                facts,
            },
        };

        var envelope = new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    content = new
                    {
                        schema = "http://adaptivecards.io/schemas/adaptive-card.json",
                        type = "AdaptiveCard",
                        version = "1.4",
                        body = cardBody,
                    },
                },
            },
        };

        // "$schema" is not a valid C# identifier; serialize then fix the key name.
        return JsonConvert.SerializeObject(envelope).Replace("\"schema\":", "\"$schema\":");
    }

    private static void AddFact(List<object> facts, string title, string? value)
    {
        if (!string.IsNullOrEmpty(value))
        {
            facts.Add(new { title, value });
        }
    }

    [return: NotNullIfNotNull(nameof(value))]
    private static string? Truncate(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length <= MaxFieldLength)
        {
            return value;
        }

        return value.Substring(0, MaxFieldLength) + "… (truncated)";
    }

    private static string SeverityColor(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Critical => "attention",
        NotificationSeverity.Error => "warning",
        NotificationSeverity.Warning => "accent",
        _ => "good",
    };

    private static string SafeHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "(invalid-url)";
}
