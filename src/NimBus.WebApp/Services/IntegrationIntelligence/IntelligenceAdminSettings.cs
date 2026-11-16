using System.Globalization;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using NimBus.Extensions.IntegrationIntelligence;

namespace NimBus.WebApp.Services.IntegrationIntelligence;

/// <summary>
/// Non-secret, administrator-editable settings. Records saved before v5.0.0 also carry the removed
/// provider's model and timeout; the stores read them with Newtonsoft, which ignores those members.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class IntelligenceAdminSettings
{
    [JsonRequired]
    public bool Enabled { get; set; }
    [JsonRequired]
    public bool IncludeEventPayload { get; set; }
    /// <summary>
    /// Agents may read a failure's scrubbed stack trace (Spec 044 Phase 3). Optional on save, so
    /// clients that predate it keep working; it defaults to off.
    /// </summary>
    public bool IncludeStackTrace { get; set; }
    [JsonRequired]
    public bool IncludeRecentFailureHistory { get; set; } = true;
    [JsonRequired]
    public int MaximumHistoryItems { get; set; } = 5;
    [JsonRequired]
    public string[] AdditionalRedactedKeys { get; set; } = [];
    [JsonRequired]
    public string[] AllowedEndpoints { get; set; } = [];
    [JsonRequired]
    public double MinimumCategoryConfidence { get; set; } = 0.60;
    [JsonRequired]
    public double RetryLikely { get; set; } = 0.75;
    [JsonRequired]
    public double ChangeRequired { get; set; } = 0.75;

    private const string Prefix = "NimBus:IntegrationIntelligence:";
    private const string Module = Prefix + "FailureClassification:";

    /// <summary>Validates all editable fields without echoing supplied values.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (MaximumHistoryItems is < 0 or > 5) errors.Add("History limit must be between 0 and 5.");
        if (!Probability(MinimumCategoryConfidence)) errors.Add("Category confidence must be between 0 and 1.");
        if (!Probability(RetryLikely)) errors.Add("Retry threshold must be between 0 and 1.");
        if (!Probability(ChangeRequired)) errors.Add("Change threshold must be between 0 and 1.");
        if (!Names(AdditionalRedactedKeys)) errors.Add("Redacted keys must contain at most 100 nonempty names of up to 200 characters.");
        if (!Names(AllowedEndpoints)) errors.Add("Endpoints must contain at most 100 nonempty names of up to 200 characters.");
        return errors;
    }

    private static bool Probability(double value) => double.IsFinite(value) && value is >= 0 and <= 1;
    private static bool Names(string[]? values) => values is not null && values.Length <= 100
        && values.All(v => !string.IsNullOrWhiteSpace(v) && v.Length <= 200 && !v.Any(char.IsControl));

    /// <summary>Builds an independent configuration snapshot, replacing arrays rather than merging their tails.</summary>
    public IConfiguration ApplyTo(IConfiguration configuration)
    {
        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        values[Prefix + "Enabled"] = Enabled.ToString();
        values[Module + "Enabled"] = Enabled.ToString();
        values[Module + "Data:IncludeEventPayload"] = IncludeEventPayload.ToString();
        values[Module + "Data:IncludeStackTrace"] = IncludeStackTrace.ToString();
        values[Module + "Data:IncludeRecentFailureHistory"] = IncludeRecentFailureHistory.ToString();
        values[Module + "Data:MaximumHistoryItems"] = MaximumHistoryItems.ToString(CultureInfo.InvariantCulture);
        values[Module + "Thresholds:MinimumCategoryConfidence"] = MinimumCategoryConfidence.ToString(CultureInfo.InvariantCulture);
        values[Module + "Thresholds:RetryLikely"] = RetryLikely.ToString(CultureInfo.InvariantCulture);
        values[Module + "Thresholds:ChangeRequired"] = ChangeRequired.ToString(CultureInfo.InvariantCulture);
        for (var i = 0; i < AllowedEndpoints.Length; i++) values[Module + "AllowedEndpoints:" + i] = AllowedEndpoints[i];
        for (var i = 0; i < AdditionalRedactedKeys.Length; i++) values[Module + "Data:AdditionalRedactedKeys:" + i] = AdditionalRedactedKeys[i];
        return new ConfigurationBuilder().Add(new IntelligenceSettingsConfigurationSource(configuration, values,
            [Module + "AllowedEndpoints", Module + "Data:AdditionalRedactedKeys"])).Build();
    }

    /// <summary>Reads only non-secret settings from the effective startup configuration.</summary>
    public static IntelligenceAdminSettings FromConfiguration(IConfiguration configuration)
    {
        var parent = configuration.GetSection(Prefix.TrimEnd(':')).Get<IntegrationIntelligenceOptions>() ?? new();
        var options = parent.FailureClassification;
        options.ApplyNestedSettings();
        return new IntelligenceAdminSettings
        {
            Enabled = parent.Enabled && options.Enabled,
            IncludeEventPayload = options.IncludeEventPayload,
            IncludeStackTrace = options.IncludeStackTrace,
            IncludeRecentFailureHistory = options.Data.IncludeRecentFailureHistory,
            MaximumHistoryItems = options.MaximumHistoryItems, AdditionalRedactedKeys = options.Data.AdditionalRedactedKeys,
            AllowedEndpoints = options.AllowedEndpoints,
            MinimumCategoryConfidence = options.MinimumCategoryConfidence, RetryLikely = options.RetryLikely,
            ChangeRequired = options.ChangeRequired,
        };
    }
}

/// <summary>
/// Durable configuration revision. The initial, absent revision is "none". Records saved before
/// v5.0.0 may still hold the removed provider's sealed key (<c>ProtectedApiKey</c>); it is ignored
/// on read and dropped by the next save.
/// </summary>
public sealed record IntelligenceSettingsDocument(IntelligenceAdminSettings Settings, string Revision);

/// <summary>Shared configuration persistence, independent from classification results.</summary>
public interface IIntelligenceSettingsStore
{
    Task<IntelligenceSettingsDocument?> ReadAsync(CancellationToken cancellationToken);
    Task<bool> TrySaveAsync(IntelligenceSettingsDocument document, string expectedRevision, CancellationToken cancellationToken);
}
