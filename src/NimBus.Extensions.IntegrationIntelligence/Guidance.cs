namespace NimBus.Extensions.IntegrationIntelligence;

/// <summary>Composes operator guidance from classification answers without recovery side effects.</summary>
public static class FailureGuidanceRules
{
    /// <summary>Applies the ordered guidance rules to a provider result. Forwards to the answer overload.</summary>
    public static FailureGuidance Compose(FailureIntelligenceProviderResult result, FailureClassificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(result);
        return Compose(result.Category, result.CategoryConfidence, result.RetryLikelihood, result.ChangeRequiredLikelihood, options);
    }

    /// <summary>
    /// Applies the ordered guidance rules from the specification to the answers of a question set,
    /// with the saved thresholds. Agents never supply guidance; NimBus composes it (Spec 044 §6.4).
    /// </summary>
    public static FailureGuidance Compose(
        string category,
        double categoryConfidence,
        double retryLikelihood,
        double changeRequiredLikelihood,
        FailureClassificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(category);
        ArgumentNullException.ThrowIfNull(options);

        if (categoryConfidence < options.MinimumCategoryConfidence)
        {
            return FailureGuidance.Uncertain;
        }

        if (string.Equals(category, "transient_dependency", StringComparison.Ordinal)
            && retryLikelihood >= options.RetryLikely)
        {
            return FailureGuidance.RetryMayHelp;
        }

        if (changeRequiredLikelihood >= options.ChangeRequired)
        {
            return FailureGuidance.ChangeLikelyRequired;
        }

        return FailureGuidance.Investigate;
    }
}
