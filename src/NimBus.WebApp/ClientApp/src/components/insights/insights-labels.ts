import * as api from "api-client";
import type { BadgeVariant } from "components/ui/badge";

/** The ways the Insights page groups the same failures (spec 043 §6; fault origin: Spec 044 Phase 3). */
export type InsightsGroupBy = "patterns" | "cause" | "guidance" | "origin";

export const GROUP_BY_OPTIONS: { value: InsightsGroupBy; label: string }[] = [
  { value: "patterns", label: "Error patterns" },
  { value: "cause", label: "Likely failure causes" },
  { value: "guidance", label: "Recovery guidance" },
  { value: "origin", label: "Fault origin" },
];

export const PERSPECTIVE: Record<InsightsGroupBy, api.InsightsPerspective> = {
  patterns: api.InsightsPerspective.ErrorPattern,
  cause: api.InsightsPerspective.Cause,
  guidance: api.InsightsPerspective.Guidance,
  origin: api.InsightsPerspective.FaultOrigin,
};

export const ANALYSIS_STATE: Record<
  api.InsightAnalysisState,
  { label: string; variant: BadgeVariant }
> = {
  [api.InsightAnalysisState.Classified]: { label: "Classified", variant: "success" },
  [api.InsightAnalysisState.Uncertain]: { label: "Uncertain", variant: "warning" },
  [api.InsightAnalysisState.NotAnalyzed]: { label: "Not analyzed", variant: "default" },
  [api.InsightAnalysisState.NotEligible]: { label: "Not eligible", variant: "default" },
  [api.InsightAnalysisState.Unavailable]: { label: "Unavailable", variant: "default" },
};

/** Why a row's latest analysis differs from the result it is counted under. */
export function markerLabel(
  marker: string | undefined,
  state: api.InsightAnalysisState | undefined,
): string | undefined {
  switch (marker) {
    case "analysisRunning":
      return state === api.InsightAnalysisState.Classified ||
        state === api.InsightAnalysisState.Uncertain
        ? "Newer analysis running"
        : "Analysis running";
    case "outcomeUnknown":
      return "Latest outcome unknown";
    case "lastAnalysisFailed":
      return "Latest analysis failed";
    case "identityMismatch":
      return "Result belongs to another source";
    default:
      return undefined;
  }
}

/** Explains why likely causes, recovery guidance and coverage are not shown. */
export function enrichmentMessage(
  status: api.InsightsEnrichmentStatus | undefined,
  limit: number | undefined,
): string | undefined {
  switch (status) {
    case api.InsightsEnrichmentStatus.Disabled:
      return "Failure intelligence is off, so likely causes and recovery guidance aren't available. Error patterns are shown.";
    case api.InsightsEnrichmentStatus.Unavailable:
      return "Saved failure classifications can't be read right now. Error patterns are shown; refresh to try again.";
    case api.InsightsEnrichmentStatus.Unsupported:
      return "This message store doesn't record which failed attempt each failure is, so saved classifications can't be matched. Error patterns are shown.";
    case api.InsightsEnrichmentStatus.LimitExceeded:
      return `This selection has more than ${(limit ?? 0).toLocaleString()} failures. Pick a shorter time range or one endpoint to see likely causes and recovery guidance.`;
    default:
      return undefined;
  }
}

const CAUSE_LABELS: Record<string, string> = {
  transient_dependency: "Transient dependency",
  authentication_configuration: "Authentication / configuration",
  contract_schema: "Contract / schema",
  business_rule: "Business rule",
  missing_reference_data: "Missing reference data",
  application_defect: "Application defect",
  messaging_platform: "Messaging platform",
  unknown: "Unknown",
};

const GUIDANCE_LABELS: Record<string, string> = {
  RetryMayHelp: "Retry may help",
  ChangeLikelyRequired: "Change likely required",
  Investigate: "Investigate",
  Uncertain: "Uncertain",
};

/** A saved category as a reader-facing label; an unrecognized value is shown as stored. */
export const causeLabel = (category: string | undefined) =>
  category ? (CAUSE_LABELS[category] ?? category) : undefined;

/** A saved guidance name as a reader-facing label; an unrecognized value is shown as stored. */
export const guidanceLabel = (guidance: string | undefined) =>
  guidance ? (GUIDANCE_LABELS[guidance] ?? guidance) : undefined;

export const percent = (part: number, whole: number) =>
  whole === 0 ? "0%" : `${Math.round((part / whole) * 100)}%`;
