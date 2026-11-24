import * as api from "api-client";

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
