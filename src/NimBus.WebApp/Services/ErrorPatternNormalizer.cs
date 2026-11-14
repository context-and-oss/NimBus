using System;
using System.Text.RegularExpressions;

namespace NimBus.WebApp.Services;

/// <summary>
/// Normalizes failed-message error texts so that messages differing only by
/// volatile fragments (timestamps, GUIDs, dimension values, quoted identifiers,
/// long numbers, trailing "Action:" advice) collapse into a single pattern for
/// the failed-insights grouping on the metrics dashboard.
/// </summary>
public static class ErrorPatternNormalizer
{
    private static readonly Regex TimestampPattern = new(
        @"\b\d{4}-\d{2}-\d{2}[T\s]\d{2}:\d{2}:\d{2}(?:\.\d+)?(?:Z|[+-]\d{2}:?\d{2})?\b",
        RegexOptions.Compiled);

    private static readonly Regex GuidPattern = new(
        @"[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}",
        RegexOptions.Compiled);

    private static readonly Regex DimensionValuePattern = new(
        @"\bdimension value\s+['""]?[^'"",.;:\s]+['""]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex KeyValuePattern = new(
        @"\bkey\s+['""][^'""]+['""]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex JobIdPattern = new(
        @"\bJobID\s+\[[^\]]+\]",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex QuotedIdentifierPattern = new(
        @"(['""])[A-Za-z0-9][A-Za-z0-9_.:-]{5,}\1",
        RegexOptions.Compiled);

    private static readonly Regex LongNumberPattern = new(
        @"\b\d{4,}\b",
        RegexOptions.Compiled);

    private static readonly Regex ActionSuffix = new(
        @"\.?\s*Action:.*$",
        RegexOptions.Compiled);

    // A short unquoted value appended after the last ": " (e.g. "... update the
    // existing contact: james.burton"). Only applies when an earlier ": " exists,
    // so the reason after a single category colon ("Order rejected: timeout") is kept.
    private static readonly Regex TrailingValuePattern = new(
        @"^(.*:\s.*:\s+)[^:\s]+(?:\s+[^:\s]+){0,2}$",
        RegexOptions.Compiled);

    /// <summary>
    /// Extracts a short, stable category from an error text. The text is
    /// normalized first so errors that differ only by an embedded id or value
    /// (e.g. "Job with JobID {GUID} not found") collapse into one category
    /// instead of one row per id.
    /// </summary>
    /// <param name="errorText">The raw error text; may be null or empty.</param>
    /// <returns>A normalized category string, or "Unknown" when the input is null or empty.</returns>
    public static string ExtractCategory(string? errorText)
    {
        if (string.IsNullOrEmpty(errorText)) return "Unknown";
        var normalized = Normalize(errorText);
        const string timestampPrefix = "<timestamp>:";
        if (normalized.StartsWith(timestampPrefix, StringComparison.Ordinal))
        {
            normalized = normalized[timestampPrefix.Length..].TrimStart();
        }

        if (normalized.StartsWith('['))
        {
            var end = normalized.IndexOf(']', StringComparison.Ordinal);
            if (end > 0) return normalized[..(end + 1)];
        }

        var colon = normalized.IndexOf(':', StringComparison.Ordinal);
        if (colon > 0 && colon < 100) return normalized[..colon];

        return normalized.Length > 100 ? normalized[..100] : normalized;
    }

    /// <summary>
    /// Replaces volatile fragments (timestamps, GUIDs, dimension values, quoted
    /// identifiers, long numbers, a short trailing value after the last colon)
    /// with placeholders and strips any trailing "Action:" advice so equivalent
    /// errors yield an identical pattern string.
    /// </summary>
    /// <param name="errorText">The raw error text; may be null or empty.</param>
    /// <returns>The normalized pattern, or "Unknown" when the input is null or empty.</returns>
    public static string Normalize(string? errorText)
    {
        if (string.IsNullOrEmpty(errorText)) return "Unknown";
        var normalized = TimestampPattern.Replace(errorText, "<timestamp>");
        normalized = GuidPattern.Replace(normalized, "<id>");
        normalized = DimensionValuePattern.Replace(normalized, "dimension value <value>");
        normalized = KeyValuePattern.Replace(normalized, "key '<value>'");
        normalized = JobIdPattern.Replace(normalized, "JobID [<id>]");
        normalized = QuotedIdentifierPattern.Replace(normalized, "$1<value>$1");
        normalized = LongNumberPattern.Replace(normalized, "<number>");
        normalized = ActionSuffix.Replace(normalized, string.Empty).TrimEnd(' ', '.');
        return TrailingValuePattern.Replace(normalized, "$1<value>");
    }
}
