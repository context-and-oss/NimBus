using System.Text.Json;
using System.Text.Json.Serialization;

namespace NimBus.Extensions.IntegrationIntelligence;

/// <summary>Composes operator guidance from classification answers without recovery side effects.</summary>
public static class FailureGuidanceRules
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter<FailureGuidance>() } };
    private static readonly RuleTemplate Template = LoadTemplate();

    private static RuleTemplate LoadTemplate()
    {
        using var stream = typeof(FailureGuidanceRules).Assembly.GetManifestResourceStream("NimBus.FailureGuidanceRules")!;
        return JsonSerializer.Deserialize<RuleTemplate>(stream, Json)!;
    }

    private sealed record RuleTemplate(int Version, RuleDefinition[] Rules, FailureGuidance Otherwise);

    private sealed record RuleDefinition(string Field, string Comparison, string Threshold, FailureGuidance Guidance, string? Category);

    /// <summary>Describes the effective rules for this recorder, in first-match order.</summary>
    public static GuidanceRuleSet Describe(FailureClassificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var rules = Template.Rules.Select(rule => new GuidanceRule(rule.Field, rule.Comparison, rule.Threshold switch
        {
            "minimumCategoryConfidence" => options.MinimumCategoryConfidence,
            "retryLikely" => options.RetryLikely,
            "changeRequired" => options.ChangeRequired,
            _ => throw new InvalidOperationException($"Unknown guidance threshold {rule.Threshold}."),
        }, rule.Guidance, rule.Category)).ToArray();
        return new GuidanceRuleSet(Template.Version, rules, Template.Otherwise);
    }

    /// <summary>Applies the same ordered rules that the recorder advertises to clients.</summary>
    public static FailureGuidance Compose(
        string category,
        double categoryConfidence,
        double retryLikelihood,
        double changeRequiredLikelihood,
        FailureClassificationOptions options)
    {
        ArgumentNullException.ThrowIfNull(category);
        return Describe(options).Compose(category, categoryConfidence, retryLikelihood, changeRequiredLikelihood);
    }
}

/// <summary>Version 1 uses the first matching rule, or Otherwise when none match.</summary>
public sealed record GuidanceRuleSet(
    int Version,
    IReadOnlyList<GuidanceRule> Rules,
    [property: JsonConverter(typeof(JsonStringEnumConverter<FailureGuidance>))] FailureGuidance Otherwise)
{
    /// <summary>Computes guidance from this snapshot of the server's rules.</summary>
    public FailureGuidance Compose(string category, double categoryConfidence, double retryLikelihood, double changeRequiredLikelihood)
    {
        foreach (var rule in Rules)
        {
            if (rule.Category is not null && !string.Equals(rule.Category, category, StringComparison.Ordinal))
                continue;

            var value = rule.Field switch
            {
                "categoryConfidence" => categoryConfidence,
                "retryLikelihood" => retryLikelihood,
                "changeRequiredLikelihood" => changeRequiredLikelihood,
                _ => throw new InvalidOperationException($"Unknown guidance field {rule.Field}."),
            };
            var matches = rule.Comparison switch
            {
                "lt" => value < rule.Threshold,
                "gte" => value >= rule.Threshold,
                _ => throw new InvalidOperationException($"Unknown guidance comparison {rule.Comparison}."),
            };
            if (matches)
                return rule.Guidance;
        }

        return Otherwise;
    }
}

/// <summary>Compares one answer with a threshold, optionally restricted to an exact category.</summary>
public sealed record GuidanceRule(
    string Field, string Comparison, double Threshold,
    [property: JsonConverter(typeof(JsonStringEnumConverter<FailureGuidance>))] FailureGuidance Guidance,
    string? Category = null);
