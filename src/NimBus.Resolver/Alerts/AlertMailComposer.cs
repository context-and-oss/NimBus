using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text;
using NimBus.MessageStore;
using NimBus.MessageStore.States;

namespace NimBus.Resolver.Alerts;

/// <summary>
/// Writes the subject and bodies of an endpoint alert mail. Every value is HTML-encoded in the
/// HTML body; the event payload is never included because it may hold personal data.
/// </summary>
public sealed class AlertMailComposer
{
    /// <summary>The longest error text a mail carries; longer text is cut and marked with an ellipsis.</summary>
    public const int MaxErrorTextLength = 500;

    private static readonly (int Seconds, string Name)[] FrequencyUnits =
    [
        (604800, "week"),
        (86400, "day"),
        (3600, "hour"),
        (60, "minute"),
    ];

    private readonly AlertMailOptions _options;

    /// <summary>Creates a composer.</summary>
    /// <param name="options">The alert mail settings.</param>
    public AlertMailComposer(AlertMailOptions options) => _options = options ?? throw new ArgumentNullException(nameof(options));

    /// <summary>Composes the mail <paramref name="subscription"/> gets for <paramref name="context"/>.</summary>
    /// <param name="subscription">The subscriber.</param>
    /// <param name="context">The recorded outcome.</param>
    public AlertMail Compose(EndpointSubscription subscription, EndpointAlertContext context)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        ArgumentNullException.ThrowIfNull(context);

        var eventType = string.IsNullOrWhiteSpace(context.EventTypeId) ? "unknown event type" : context.EventTypeId;
        var deadLettered = context.Status == ResolutionStatus.DeadLettered;
        var outcome = deadLettered ? "was dead-lettered" : "failed";
        var status = deadLettered ? "Dead-lettered" : "Failed";
        var time = context.OccurredAtUtc.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
        var errorText = _options.IncludeErrorText ? Truncate(context.ErrorText) : null;
        var (eventUrl, endpointUrl) = Links(context);
        var footer = Footer(subscription);

        var facts = new (string Label, string? Value)[]
        {
            ("Endpoint", context.EndpointId),
            ("Event type", eventType),
            ("Event ID", context.EventId),
            ("Session ID", context.SessionId),
            ("Status", status),
            ("Time", time),
        };

        var subject = SingleLine($"[NimBus] {context.EndpointId}: {eventType} {outcome}");
        return new AlertMail(
            subscription.Mail ?? string.Empty,
            subject,
            HtmlBody(context.EndpointId, eventType, outcome, facts, errorText, eventUrl, endpointUrl, footer),
            TextBody(context.EndpointId, eventType, outcome, facts, errorText, eventUrl, endpointUrl, footer));
    }

    private static string HtmlBody(
        string endpointId,
        string eventType,
        string outcome,
        (string Label, string? Value)[] facts,
        string? errorText,
        string? eventUrl,
        string? endpointUrl,
        string footer)
    {
        var html = new StringBuilder();
        html.Append("<html><body style=\"font-family:Segoe UI,Arial,sans-serif;font-size:14px;color:#1f2328\">");
        html.Append(CultureInfo.InvariantCulture,
            $"<h2 style=\"font-size:18px\">{E(endpointId)}: {E(eventType)} {E(outcome)}</h2>");
        html.Append("<table style=\"border-collapse:collapse\">");
        foreach (var (label, value) in facts.Where(f => !string.IsNullOrEmpty(f.Value)))
        {
            html.Append(CultureInfo.InvariantCulture,
                $"<tr><td style=\"padding:2px 16px 2px 0;color:#59636e\">{E(label)}</td><td style=\"padding:2px 0\">{E(value)}</td></tr>");
        }

        html.Append("</table>");
        if (!string.IsNullOrEmpty(errorText))
        {
            html.Append("<p style=\"margin-bottom:4px\"><b>Error</b></p>");
            html.Append(CultureInfo.InvariantCulture,
                $"<pre style=\"white-space:pre-wrap;background:#f6f8fa;padding:8px;margin-top:0\">{E(errorText)}</pre>");
        }

        if (eventUrl is not null && endpointUrl is not null)
        {
            html.Append(CultureInfo.InvariantCulture,
                $"<p><a href=\"{E(eventUrl)}\">Open the event</a> &middot; <a href=\"{E(endpointUrl)}\">Open the endpoint</a></p>");
        }

        html.Append(CultureInfo.InvariantCulture, $"<p style=\"color:#59636e;font-size:12px\">{E(footer)}</p>");
        html.Append("</body></html>");
        return html.ToString();
    }

    private static string TextBody(
        string endpointId,
        string eventType,
        string outcome,
        (string Label, string? Value)[] facts,
        string? errorText,
        string? eventUrl,
        string? endpointUrl,
        string footer)
    {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{endpointId}: {eventType} {outcome}.");
        text.AppendLine();
        foreach (var (label, value) in facts.Where(f => !string.IsNullOrEmpty(f.Value)))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{label + ":",-12}{value}");
        }

        if (!string.IsNullOrEmpty(errorText))
        {
            text.AppendLine();
            text.AppendLine("Error:");
            text.AppendLine(errorText);
        }

        if (eventUrl is not null && endpointUrl is not null)
        {
            text.AppendLine();
            text.AppendLine(CultureInfo.InvariantCulture, $"Open the event: {eventUrl}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Open the endpoint: {endpointUrl}");
        }

        text.AppendLine();
        text.AppendLine(footer);
        return text.ToString();
    }

    private (string? EventUrl, string? EndpointUrl) Links(EndpointAlertContext context)
    {
        if (string.IsNullOrWhiteSpace(_options.WebAppUrl))
        {
            return (null, null);
        }

        var root = _options.WebAppUrl.TrimEnd('/');
        var endpoint = Uri.EscapeDataString(context.EndpointId);
        return (
            $"{root}/Message/Index/{endpoint}/{Uri.EscapeDataString(context.EventId)}",
            $"{root}/Endpoints/Details/{endpoint}");
    }

    private static string Footer(EndpointSubscription subscription)
    {
        var filter = subscription.EventTypes is { Count: > 0 } eventTypes
            ? $" for {string.Join(", ", eventTypes)}"
            : string.Empty;
        return $"You get this mail because {subscription.Mail} subscribed to alerts on {subscription.EndpointId}{filter}. "
            + $"{DescribeFrequency(subscription.Frequency)} Manage alerts on the endpoint's Alerts tab in NimBus.";
    }

    private static string DescribeFrequency(int seconds)
    {
        if (seconds <= 0)
        {
            return "You get an alert for every failure.";
        }

        foreach (var (unitSeconds, name) in FrequencyUnits)
        {
            if (seconds % unitSeconds == 0)
            {
                var count = seconds / unitSeconds;
                return count == 1
                    ? $"You get at most one alert per {name}."
                    : string.Create(CultureInfo.InvariantCulture, $"You get at most one alert per {count} {name}s.");
            }
        }

        return string.Create(CultureInfo.InvariantCulture,
            $"You get at most one alert per {seconds} {(seconds == 1 ? "second" : "seconds")}.");
    }

    private static string? Truncate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value.Length > MaxErrorTextLength ? value[..MaxErrorTextLength] + "…" : value;
    }

    private static string SingleLine(string value)
        => new(value.Select(c => char.IsControl(c) ? ' ' : c).ToArray());

    private static string E(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);
}
