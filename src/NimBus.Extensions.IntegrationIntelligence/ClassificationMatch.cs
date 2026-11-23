namespace NimBus.Extensions.IntegrationIntelligence;

/// <summary>
/// When a saved classification counts for a failed attempt (spec 043 §5): the rule Insights and
/// alert rules share, so a failure is "classified" in the same way everywhere.
/// </summary>
public static class ClassificationMatch
{
    /// <summary>The category that means no category fits; it never counts as classified.</summary>
    public const string UnknownCategory = "unknown";

    /// <summary>
    /// Whether <paramref name="result"/> names the same source as the failed attempt: the endpoint
    /// (case-insensitive) and the event. A result that names another source is never used.
    /// </summary>
    /// <param name="result">The saved result.</param>
    /// <param name="endpointId">The failed attempt's endpoint.</param>
    /// <param name="eventId">The failed attempt's event.</param>
    public static bool IsSameSource(FailureClassification result, string endpointId, string eventId)
    {
        ArgumentNullException.ThrowIfNull(result);
        return string.Equals(result.EndpointId, endpointId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(result.EventId, eventId, StringComparison.Ordinal);
    }

    /// <summary>
    /// Whether <paramref name="result"/> is a usable classification: a known category other than
    /// <see cref="UnknownCategory"/>, and known guidance other than <see cref="FailureGuidance.Uncertain"/>.
    /// </summary>
    /// <param name="result">The saved result.</param>
    public static bool IsClassified(FailureClassification result)
    {
        ArgumentNullException.ThrowIfNull(result);

        // Stored guidance is a Newtonsoft integer: an out-of-range value deserializes without error.
        return Enum.IsDefined(result.Guidance)
            && result.Guidance != FailureGuidance.Uncertain
            && FailureClassificationQuestionSet.Categories.Contains(result.Category)
            && result.Category != UnknownCategory;
    }

    /// <summary>
    /// Whether the failed attempt of <paramref name="endpointId"/>/<paramref name="eventId"/> is
    /// classified, by its live latest result, into one of <paramref name="categories"/>.
    /// </summary>
    /// <param name="snapshot">The attempt's saved state, or null when it has none.</param>
    /// <param name="endpointId">The failed attempt's endpoint.</param>
    /// <param name="eventId">The failed attempt's event.</param>
    /// <param name="categories">The categories that count.</param>
    public static bool Matches(ClassificationSnapshot? snapshot, string endpointId, string eventId, IReadOnlyCollection<string> categories)
    {
        ArgumentNullException.ThrowIfNull(categories);
        var latest = snapshot is { Deleted: false } ? snapshot.Latest : null;
        return latest is not null
            && IsSameSource(latest, endpointId, eventId)
            && IsClassified(latest)
            && categories.Contains(latest.Category, StringComparer.Ordinal);
    }
}
