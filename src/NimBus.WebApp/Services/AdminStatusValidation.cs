using System;
using System.Collections.Generic;
using System.Linq;
using NimBus.MessageStore;
using NimBus.MessageStore.Abstractions;

namespace NimBus.WebApp.Services;

internal static class AdminStatusValidation
{
    private static readonly IReadOnlyDictionary<string, string> ResolutionStatusNames =
        CreateResolutionStatusNames();

    // Bulk skip sends each skip to the endpoint, so it accepts only the statuses a skip settles;
    // see SkipRules.BulkSkipSourceStatuses for why the others are refused.
    private static readonly HashSet<string> SkipSourceStatusNames = new(
        SkipRules.BulkSkipSourceStatuses.Select(status => status.ToString()),
        StringComparer.Ordinal);

    public static bool TryNormalizeDeleteStatuses(
        IEnumerable<string>? statuses,
        out List<string> normalized,
        out string error) =>
        TryNormalize(statuses, skippableOnly: false, out normalized, out error);

    public static bool TryNormalizeSkipStatuses(
        IEnumerable<string>? statuses,
        out List<string> normalized,
        out string error) =>
        TryNormalize(statuses, skippableOnly: true, out normalized, out error);

    public static List<string> NormalizeDeleteStatuses(IEnumerable<string>? statuses)
    {
        if (TryNormalizeDeleteStatuses(statuses, out var normalized, out var error))
        {
            return normalized;
        }

        throw new ArgumentException(error, nameof(statuses));
    }

    public static List<string> NormalizeSkipStatuses(IEnumerable<string>? statuses)
    {
        if (TryNormalizeSkipStatuses(statuses, out var normalized, out var error))
        {
            return normalized;
        }

        throw new ArgumentException(error, nameof(statuses));
    }

    private static bool TryNormalize(
        IEnumerable<string>? statuses,
        bool skippableOnly,
        out List<string> normalized,
        out string error)
    {
        normalized = new List<string>();
        error = string.Empty;

        if (statuses == null)
        {
            error = "At least one status is required.";
            return false;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var status in statuses)
        {
            if (string.IsNullOrWhiteSpace(status) ||
                !ResolutionStatusNames.TryGetValue(status, out var canonicalStatus))
            {
                error = $"Unknown resolution status '{status ?? "<null>"}'.";
                return false;
            }

            if (skippableOnly && !SkipSourceStatusNames.Contains(canonicalStatus))
            {
                error = $"Resolution status '{canonicalStatus}' cannot be used as a source for skip. " +
                    $"Skippable statuses: {string.Join(", ", SkipSourceStatusNames.Order(StringComparer.Ordinal))}.";
                return false;
            }

            if (seen.Add(canonicalStatus))
            {
                normalized.Add(canonicalStatus);
            }
        }

        if (normalized.Count == 0)
        {
            error = "At least one status is required.";
            return false;
        }

        return true;
    }

    private static IReadOnlyDictionary<string, string> CreateResolutionStatusNames()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in Enum.GetNames<ResolutionStatus>())
        {
            names.Add(name, name);
        }

        return names;
    }
}
