using Newtonsoft.Json;

namespace NimBus.Extensions.Notifications;

/// <summary>
/// Substitutes <c>{Placeholder}</c> tokens in a template string with values from a
/// <see cref="Notification"/>. Unknown placeholders are left literal; missing values
/// resolve to an empty string.
/// </summary>
public static class TemplateRenderer
{
    /// <summary>
    /// Renders <paramref name="template"/> against <paramref name="notification"/>, replacing the
    /// supported tokens: <c>{Severity}</c>, <c>{Title}</c>, <c>{Message}</c>, <c>{EventId}</c>,
    /// <c>{EventTypeId}</c>, <c>{MessageId}</c>, <c>{CorrelationId}</c>, <c>{ErrorDetails}</c>.
    /// </summary>
    /// <param name="template">The template string containing <c>{Placeholder}</c> tokens.</param>
    /// <param name="notification">The notification supplying the substitution values.</param>
    /// <param name="jsonEncodeValues">
    /// When <c>true</c>, each substituted value is JSON-string-escaped (quotes, backslashes,
    /// control characters and newlines) so it can be dropped into a JSON template such as
    /// <c>{"title":"{Title}"}</c> without breaking the payload. Channels that POST
    /// <c>application/json</c> (Webhook, Teams) set this; plain-text channels (Email) leave it
    /// <c>false</c>.
    /// </param>
    public static string Render(string template, Notification notification, bool jsonEncodeValues = false)
    {
        if (string.IsNullOrEmpty(template) || notification == null)
        {
            return template ?? string.Empty;
        }

        return template
            .Replace("{Severity}", Encode(notification.Severity.ToString(), jsonEncodeValues))
            .Replace("{Title}", Encode(notification.Title, jsonEncodeValues))
            .Replace("{Message}", Encode(notification.Message, jsonEncodeValues))
            .Replace("{EventId}", Encode(notification.EventId, jsonEncodeValues))
            .Replace("{EventTypeId}", Encode(notification.EventTypeId, jsonEncodeValues))
            .Replace("{MessageId}", Encode(notification.MessageId, jsonEncodeValues))
            .Replace("{CorrelationId}", Encode(notification.CorrelationId, jsonEncodeValues))
            .Replace("{ErrorDetails}", Encode(notification.ErrorDetails, jsonEncodeValues));
    }

    private static string Encode(string? value, bool jsonEncodeValues)
    {
        value ??= string.Empty;
        if (!jsonEncodeValues)
        {
            return value;
        }

        // JsonConvert.ToString yields a fully-escaped, double-quoted JSON string. The template
        // already supplies the surrounding quotes around the placeholder, so strip them and
        // return only the escaped inner content.
        var quoted = JsonConvert.ToString(value);
        return quoted.Substring(1, quoted.Length - 2);
    }
}
