using System;

namespace NimBus.WebApp.Services;

/// <summary>
/// Validates an endpoint's code location (Spec 044 Phase 3): the adapter's repository address and
/// the issue tracker's link template. Agents clone from and file issues against these, so only
/// plain https addresses are accepted: no credentials, no query string and no fragment.
/// </summary>
public static class EndpointCodeLocation
{
    /// <summary>Longest stored address or template (SQL <c>NVARCHAR(400)</c>).</summary>
    public const int MaximumLength = 400;

    /// <summary>Longest source-system name (SQL <c>NVARCHAR(200)</c>).</summary>
    public const int MaximumSourceSystemLength = 200;

    /// <summary>The placeholder a ticket-link template must contain.</summary>
    public const string TicketPlaceholder = "{ticketId}";

    /// <summary>Null when <paramref name="url"/> is empty or a valid repository address; otherwise why not.</summary>
    /// <param name="url">The address.</param>
    public static string? ValidateRepositoryUrl(string? url)
        => string.IsNullOrWhiteSpace(url) ? null : ValidateHttps(url.Trim(), "The repository address");

    /// <summary>Null when <paramref name="template"/> is empty or a valid ticket-link template; otherwise why not.</summary>
    /// <param name="template">The template.</param>
    public static string? ValidateTicketLinkTemplate(string? template)
    {
        if (string.IsNullOrWhiteSpace(template))
            return null;

        var trimmed = template.Trim();
        if (!trimmed.Contains(TicketPlaceholder, StringComparison.Ordinal))
            return $"The ticket link must contain {TicketPlaceholder}.";

        // Validate the address a real ticket id would produce.
        return ValidateHttps(trimmed.Replace(TicketPlaceholder, "TICKET-1", StringComparison.Ordinal), "The ticket link", allowQuery: true);
    }

    /// <summary>Null when <paramref name="name"/> is empty or short enough; otherwise why not.</summary>
    /// <param name="name">The source-system name.</param>
    public static string? ValidateSourceSystem(string? name)
        => name is { Length: > MaximumSourceSystemLength }
            ? $"The source system is at most {MaximumSourceSystemLength} characters."
            : name is not null && name.AsSpan().ContainsAnyInRange('\0', '\u001f')
                ? "The source system may not contain control characters."
                : null;

    /// <summary>Trims <paramref name="value"/>, or returns null when it is empty.</summary>
    /// <param name="value">The value.</param>
    public static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string? ValidateHttps(string value, string what, bool allowQuery = false)
    {
        if (value.Length > MaximumLength)
            return $"{what} is at most {MaximumLength} characters.";
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || string.IsNullOrEmpty(uri.Host))
            return $"{what} must be an absolute https address.";
        if (!string.IsNullOrEmpty(uri.UserInfo))
            return $"{what} may not contain credentials.";
        if (!string.IsNullOrEmpty(uri.Fragment) || (!allowQuery && !string.IsNullOrEmpty(uri.Query)))
            return $"{what} may not contain a {(string.IsNullOrEmpty(uri.Fragment) ? "query string" : "fragment")}.";
        return null;
    }
}
