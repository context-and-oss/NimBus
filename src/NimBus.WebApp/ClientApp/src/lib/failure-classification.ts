/**
 * Failure-classification vocabulary shared by the admin explainer, the event-details card and
 * Insights. Mirrors NimBus.Extensions.IntegrationIntelligence: FailureClassificationQuestionSet
 * (v2 for agents, v1 for the deprecated provider) and FailureGuidanceRules.Compose. Keep them in
 * sync when the question set changes.
 */

export type FailureGuidance = "Uncertain" | "RetryMayHelp" | "ChangeLikelyRequired" | "Investigate";

export type GuidanceThresholds = { minimumCategoryConfidence: number; retryLikely: number; changeRequired: number };

export type GuidanceSignals = {
  category: string; categoryConfidence: number; retryLikelihood: number; changeRequiredLikelihood: number;
};

/** The question set the deprecated provider (Jev) answers. */
export const QUESTION_SET_VERSION = 1;

/** The question set agents answer over MCP: version 1 plus the fault origin (Spec 044). */
export const AGENT_QUESTION_SET_VERSION = 2;

/** Where the fault originates (question set v2). Mirrors FailureClassificationQuestionSet.FaultOrigins. */
export const FAULT_ORIGINS = [
  { id: "source_data", label: "Source data", meaning: "The publishing system sent data that is wrong, incomplete or breaks a rule." },
  { id: "adapter_code", label: "Adapter code", meaning: "The adapter's own logic is wrong: a mapping, a null it does not handle, a case it does not support." },
  { id: "target_system", label: "Target system", meaning: "The receiving system or a dependency is unavailable or refuses a valid request." },
  { id: "configuration", label: "Configuration", meaning: "Credentials, permissions, addresses or settings." },
  { id: "platform", label: "Platform", meaning: "NimBus, Service Bus, topology or sessions." },
  { id: "unknown", label: "Unknown", meaning: "Not enough evidence." },
] as const;

/** A fault origin's label; "Not assessed" for a version 1 result, the raw value when unrecognized. */
export const faultOriginLabel = (origin?: string | null): string =>
  origin ? FAULT_ORIGINS.find(item => item.id === origin)?.label ?? origin : "Not assessed";

/** The provider value of a result an agent recorded over MCP. */
export const AGENT_PROVIDER = "Agent";

/** Who made a saved result: an agent, or the deprecated provider by name. */
export const provenanceLabel = (provider?: string | null): string | undefined =>
  !provider ? undefined : provider === AGENT_PROVIDER ? "Agent" : `${provider} (deprecated provider)`;

/** The fixed category list Jev chooses from; any other value is rejected as ProviderInvalidResponse. */
export const FAILURE_CATEGORIES = [
  { id: "transient_dependency", meaning: "A temporary downstream or infrastructure condition that may clear without changing the message, configuration or code.", examples: "timeout, connection reset, HTTP 429, temporary 5xx, throttling, deadlock" },
  { id: "authentication_configuration", meaning: "Credentials, permissions, endpoint URLs, certificates or environment settings are wrong or expired.", examples: "401/403, expired secret, wrong host, missing setting" },
  { id: "contract_schema", meaning: "The data does not satisfy the technical contract.", examples: "malformed JSON, deserialization error, missing required property" },
  { id: "business_rule", meaning: "Technically valid data was rejected by a domain rule.", examples: "invalid state transition, credit limit, closed account" },
  { id: "missing_reference_data", meaning: "A referenced business record does not exist.", examples: "customer not found, order not found, missing master data" },
  { id: "application_defect", meaning: "Evidence points at a programming error.", examples: "NullReferenceException, IndexOutOfRangeException" },
  { id: "messaging_platform", meaning: "NimBus, Service Bus, topology, sessions or transport behaviour caused the failure.", examples: "lock lost, session unavailable, message too large" },
  { id: "unknown", meaning: "There is insufficient evidence or no category fits.", examples: "—" },
] as const;

/** What each guidance value asks the operator to do. */
export const GUIDANCE_MEANING: Record<FailureGuidance, string> = {
  Uncertain: "The category is not confident enough to advise. Read the exception and history yourself.",
  RetryMayHelp: "A temporary dependency problem. A manual resubmit is reasonable once the dependency is healthy.",
  ChangeLikelyRequired: "Data, configuration or code must probably change first. Resubmitting unchanged will likely fail again.",
  Investigate: "The cause is identified, but neither a retry nor a required change is clearly indicated.",
};

export const GUIDANCE_BADGE: Record<FailureGuidance, "secondary" | "success" | "error" | "warning"> = {
  Uncertain: "secondary", RetryMayHelp: "success", ChangeLikelyRequired: "error", Investigate: "warning",
};

export const isFailureGuidance = (value: string): value is FailureGuidance => Object.prototype.hasOwnProperty.call(GUIDANCE_MEANING, value);

/** Applies the ordered guidance rules; `rule` is the zero-based index of the rule that matched. */
export function composeGuidance(signals: GuidanceSignals, thresholds: GuidanceThresholds): { rule: number; guidance: FailureGuidance } {
  if (signals.categoryConfidence < thresholds.minimumCategoryConfidence) return { rule: 0, guidance: "Uncertain" };
  if (signals.category === "transient_dependency" && signals.retryLikelihood >= thresholds.retryLikely) return { rule: 1, guidance: "RetryMayHelp" };
  if (signals.changeRequiredLikelihood >= thresholds.changeRequired) return { rule: 2, guidance: "ChangeLikelyRequired" };
  return { rule: 3, guidance: "Investigate" };
}

export type SampleFailure = GuidanceSignals & {
  label: string; exceptionType: string; exceptionMessage: string; context: string;
  externalDependencyLikelihood: number; categoryProbabilities: Record<string, number>;
};

const sample = (label: string, exceptionType: string, exceptionMessage: string, context: string,
  probabilities: number[], retryLikelihood: number, changeRequiredLikelihood: number, externalDependencyLikelihood: number): SampleFailure => {
  const categoryProbabilities = Object.fromEntries(FAILURE_CATEGORIES.map((item, index) => [item.id, probabilities[index]]));
  const top = probabilities.indexOf(Math.max(...probabilities));
  return { label, exceptionType, exceptionMessage, context, categoryProbabilities, category: FAILURE_CATEGORIES[top].id,
    categoryConfidence: probabilities[top], retryLikelihood, changeRequiredLikelihood, externalDependencyLikelihood };
};

/** Illustrative provider answers — not live data. */
export const SAMPLE_FAILURES: SampleFailure[] = [
  sample("ERP in error mode", "InvalidOperationException", "ERP is in error mode.", "CrmAccountCreated → ErpEndpoint · retry 1/3 · 1 earlier failure, same error",
    [0.64, 0.05, 0.02, 0.14, 0.02, 0.04, 0.03, 0.06], 0.71, 0.38, 0.86),
  sample("Downstream timeout", "TaskCanceledException", "The request to the ERP API timed out after 30s.", "CrmAccountUpdated → ErpEndpoint · retry 3/3 · first failure in session",
    [0.91, 0.02, 0.01, 0.01, 0.01, 0.01, 0.02, 0.01], 0.88, 0.08, 0.93),
  sample("Missing property", "JsonSerializationException", "Required property 'TaxId' not found in JSON.", "CrmAccountCreated → ErpEndpoint · retry 3/3 · 4 earlier failures, same error",
    [0.01, 0.01, 0.87, 0.04, 0.03, 0.02, 0, 0.02], 0.04, 0.94, 0.12),
  sample("Vague exception", "Exception", "Operation failed.", "OrderPlaced → BillingEndpoint · retry 1/3 · no history",
    [0.18, 0.06, 0.05, 0.12, 0.04, 0.14, 0.04, 0.37], 0.35, 0.4, 0.3),
];
