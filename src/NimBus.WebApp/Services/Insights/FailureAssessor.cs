using System;
using System.Collections.Generic;
using NimBus.Extensions.IntegrationIntelligence;
using NimBus.WebApp.ManagementApi;

namespace NimBus.WebApp.Services.Insights;

/// <summary>How far a failure's saved analysis got (spec 043 §5).</summary>
internal enum CoverageState { Classified, Uncertain, NotAnalyzed, NotEligible }

/// <summary>One failure's coverage state, perspective keys, the result shown in details and its operation marker.</summary>
internal sealed record Assessment(CoverageState State, string CauseKey, string GuidanceKey, FailureClassification? Result, string? Marker, string OriginKey);

/// <summary>
/// Turns a failure's saved classification into its coverage state and its likely-cause, recovery
/// guidance and fault-origin groups. Shared by Failed Message Insights, which assesses failed
/// attempts, and the Failed page, which assesses unresolved failures.
/// </summary>
internal static class FailureAssessor
{
    internal const string Uncertain = "uncertain";
    internal const string NotAnalyzed = "notAnalyzed";
    internal const string NotEligible = "notEligible";
    internal const string NotAssessed = "notAssessed";

    internal static readonly IReadOnlyDictionary<string, string> CauseLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["transient_dependency"] = "Transient dependency",
        ["authentication_configuration"] = "Authentication / configuration",
        ["contract_schema"] = "Contract / schema",
        ["business_rule"] = "Business rule",
        ["missing_reference_data"] = "Missing reference data",
        ["application_defect"] = "Application defect",
        ["messaging_platform"] = "Messaging platform",
        [Uncertain] = "Uncertain",
        [NotAnalyzed] = "Not analyzed",
        [NotEligible] = "Not eligible",
    };

    internal static readonly IReadOnlyDictionary<string, string> OriginLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["source_data"] = "Source data",
        ["adapter_code"] = "Adapter code",
        ["target_system"] = "Target system",
        ["configuration"] = "Configuration",
        ["platform"] = "Platform",
        ["unknown"] = "Unknown origin",
        [NotAssessed] = "Origin not assessed",
        [Uncertain] = "Uncertain",
        [NotAnalyzed] = "Not analyzed",
        [NotEligible] = "Not eligible",
    };

    internal static readonly IReadOnlyDictionary<string, string> GuidanceLabels = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        [nameof(FailureGuidance.RetryMayHelp)] = "Retry may help",
        [nameof(FailureGuidance.ChangeLikelyRequired)] = "Change likely required",
        [nameof(FailureGuidance.Investigate)] = "Investigate",
        [nameof(FailureGuidance.Uncertain)] = "Uncertain",
        [NotAnalyzed] = "Not analyzed",
        [NotEligible] = "Not eligible",
    };

    /// <summary>
    /// Assesses one failure from the snapshot saved for its failed attempt, or null when none was read.
    /// </summary>
    /// <param name="eligible">Whether the failure can be analyzed at all; a failure without a result is Not analyzed when it is, Not eligible otherwise.</param>
    /// <param name="endpointId">The endpoint the failure is on.</param>
    /// <param name="eventId">The failure's event id.</param>
    /// <param name="snapshot">The saved state of the failed attempt.</param>
    /// <param name="identityMismatch">True when the saved result names another source; it is then never shown, only counted as uncertain.</param>
    internal static Assessment Assess(bool eligible, string endpointId, string eventId, ClassificationSnapshot? snapshot, out bool identityMismatch)
    {
        identityMismatch = false;
        var live = snapshot is { Deleted: false } ? snapshot : null;
        var latest = live?.Latest;
        var marker = Marker(live);
        if (latest is null)
        {
            var state = eligible ? CoverageState.NotAnalyzed : CoverageState.NotEligible;
            var key = eligible ? NotAnalyzed : NotEligible;
            return new Assessment(state, key, key, null, marker, key);
        }

        if (!ClassificationMatch.IsSameSource(latest, endpointId, eventId))
        {
            identityMismatch = true;
            return new Assessment(CoverageState.Uncertain, Uncertain, nameof(FailureGuidance.Uncertain), null, "identityMismatch", Uncertain);
        }

        // Stored guidance is a Newtonsoft integer: an out-of-range value deserializes without error.
        var guidanceKey = Enum.IsDefined(latest.Guidance) ? latest.Guidance.ToString() : nameof(FailureGuidance.Uncertain);
        // Version 1 records have no origin (Spec 044 §6.2); an origin outside the set is unknown.
        var originKey = string.IsNullOrEmpty(latest.FaultOrigin)
            ? NotAssessed
            : FailureClassificationQuestionSet.FaultOrigins.Contains(latest.FaultOrigin) ? latest.FaultOrigin : "unknown";
        return ClassificationMatch.IsClassified(latest)
            ? new Assessment(CoverageState.Classified, latest.Category, guidanceKey, latest, marker, originKey)
            : new Assessment(CoverageState.Uncertain, Uncertain, guidanceKey, latest, marker, originKey);
    }

    /// <summary>
    /// Why the latest operation differs from the counted result: a newer analysis is running, its
    /// outcome is unknown, or it failed (spec 043 §5). Null when the latest operation completed.
    /// </summary>
    internal static string? Marker(ClassificationSnapshot? snapshot) => snapshot?.LatestOperationStatus switch
    {
        "Active" => "analysisRunning",
        "Failed" when snapshot.LatestOperationErrorCode == "AnalysisOutcomeUnknown" => "outcomeUnknown",
        "Failed" => "lastAnalysisFailed",
        _ => null,
    };

    /// <summary>The four-state coverage of a set of assessed failures.</summary>
    internal static InsightsCoverage Coverage(IEnumerable<Assessment> assessments)
    {
        int classified = 0, uncertain = 0, notAnalyzed = 0, notEligible = 0;
        foreach (var assessment in assessments)
        {
            switch (assessment.State)
            {
                case CoverageState.Classified: classified++; break;
                case CoverageState.Uncertain: uncertain++; break;
                case CoverageState.NotAnalyzed: notAnalyzed++; break;
                default: notEligible++; break;
            }
        }
        return new InsightsCoverage
        {
            Total = classified + uncertain + notAnalyzed + notEligible,
            Analyzed = classified + uncertain,
            Classified = classified,
            Uncertain = uncertain,
            NotAnalyzed = notAnalyzed,
            NotEligible = notEligible,
        };
    }
}
