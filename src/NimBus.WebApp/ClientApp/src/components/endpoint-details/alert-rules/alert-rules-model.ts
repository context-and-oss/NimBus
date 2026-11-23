import moment from "moment";
import * as api from "api-client";
import type { BadgeVariant } from "components/ui/badge";

// Pure helpers behind the endpoint's alert rules (Spec 053): the words for a rule's condition and
// state, the editable draft of a rule, and the client-side checks that mirror the server's. The
// server validates again and has the last word.

/** The statuses a failure backlog counts. None picked counts all three. */
export const FAILURE_STATUSES = ["Failed", "DeadLettered", "Unsupported"] as const;

export const NAME_MAX = 100;
export const THRESHOLD_MIN = 1;
export const THRESHOLD_MAX = 100;
export const RESOLVE_MIN = 1;
export const RESOLVE_MAX = 60;
export const RESOLVE_DEFAULT = 5;
export const ERROR_TEXT_MAX = 200;
export const EVENT_TYPES_MAX = 20;
export const TARGETS_MAX = 10;

/** The evaluator runs every minute; older than this, the page warns that rules are not evaluated. */
export const STALE_AFTER_MINUTES = 5;

export type TargetType = "mail" | "webhook";

/** A target row in the form. `id` is the server's id of a kept target; a new target has none. */
export interface TargetDraft {
  key: string;
  id?: string;
  type: TargetType;
  value: string;
}

/** The form's copy of a rule. Numbers stay text so a cleared field can be reported, not coerced. */
export interface RuleDraft {
  name: string;
  enabled: boolean;
  threshold: string;
  statuses: string[];
  eventTypes: string[];
  errorTextContains: string;
  resolveAfterMinutes: string;
  targets: TargetDraft[];
}

/** Field errors by field name; a target's error is keyed `target:<key>`. */
export type DraftErrors = Partial<Record<string, string>>;

const EMAIL_PATTERN = /^[^\s@]+@[^\s@]+\.[^\s@]{2,}$/;

// The server also accepts http where a local deployment allows it; this only keeps an obviously
// wrong URL from being sent.
const isHttpsUrl = (value: string): boolean => {
  try {
    return new URL(value).protocol === "https:";
  } catch {
    return false;
  }
};

const plural = (n: number, one: string, many: string) => (n === 1 ? one : many);

const quoteList = (items: string[]) => items.join(", ");

/** The condition in words, e.g. `≥ 5 failed events (Failed, DeadLettered) of type OrderPlaced, error contains "timeout"`. */
export function describeCondition(condition: api.AlertRuleCondition | undefined): string {
  const threshold = condition?.threshold ?? 0;
  let text = `≥ ${threshold} failed ${plural(threshold, "event", "events")}`;
  const statuses = condition?.statuses ?? [];
  if (statuses.length > 0) text += ` (${quoteList(statuses)})`;
  const eventTypes = condition?.eventTypes ?? [];
  if (eventTypes.length > 0) text += ` of ${plural(eventTypes.length, "type", "types")} ${quoteList(eventTypes)}`;
  if (condition?.errorTextContains) text += `, error contains "${condition.errorTextContains}"`;
  return text;
}

/** The last backlog read: "–" before the first evaluation, "≥ N" when it reached the threshold (reads stop there). */
export function failedEventsText(rule: api.AlertRuleModel): string {
  const value = rule.state?.lastValue;
  if (value === undefined || value === null) return "–";
  const threshold = rule.condition?.threshold;
  return threshold !== undefined && value >= threshold ? `≥ ${threshold}` : String(value);
}

/** A time of day today, or a date and time on another day. */
export function formatSince(since: moment.Moment, now: moment.Moment = moment()): string {
  const local = since.clone().local();
  return local.isSame(now.clone().local(), "day") ? local.format("HH:mm") : local.format("D MMM HH:mm");
}

/** The status badge of a rule. A disabled rule is not evaluated, so it says so instead of Ok. */
export function ruleStatus(
  rule: api.AlertRuleModel,
  now: moment.Moment = moment(),
): { label: string; variant: BadgeVariant; title?: string } {
  if (!rule.enabled) return { label: "Disabled", variant: "secondary", title: "A disabled rule is not evaluated." };
  if (rule.state?.status === "Firing") {
    const since = rule.state.sinceUtc;
    return {
      label: since ? `Firing since ${formatSince(since, now)}` : "Firing",
      variant: "error",
      title: since ? since.clone().local().format("LLL") : undefined,
    };
  }
  return { label: "Ok", variant: "success" };
}

/** A webhook URL's host, which identifies the receiver without the path's tokens. */
export function webhookHost(url: string): string {
  try {
    return new URL(url).host;
  } catch {
    return url;
  }
}

/** A target as Owners see it: the mail address or the webhook's host. Undefined when the caller may not see it. */
export function targetLabel(target: api.AlertRuleTargetModel): string | undefined {
  if (target.type === "webhook") return target.url ? webhookHost(target.url) : undefined;
  return target.address || undefined;
}

/** The number and kinds of a rule's targets, which is all a Reader sees: "2 targets (mail, webhook)". */
export function targetsSummary(targets: api.AlertRuleTargetModel[] | undefined): string {
  const list = targets ?? [];
  if (list.length === 0) return "No targets";
  const kinds = [...new Set(list.map((t) => t.type ?? "unknown"))];
  return `${list.length} ${plural(list.length, "target", "targets")} (${kinds.join(", ")})`;
}

export type EvaluatorFreshness =
  | { kind: "never"; text: string }
  | { kind: "fresh"; text: string }
  | { kind: "stale"; text: string };

/** When the evaluator last finished, and whether it has stopped running. */
export function evaluatorFreshness(
  lastCompleted: moment.Moment | undefined,
  now: moment.Moment = moment(),
): EvaluatorFreshness {
  if (!lastCompleted) return { kind: "never", text: "Not evaluated yet" };
  if (now.diff(lastCompleted, "minutes", true) > STALE_AFTER_MINUTES) {
    return {
      kind: "stale",
      text: `The rule evaluator has not run for ${lastCompleted.from(now, true)}. Rules are evaluated only while the WebApp runs.`,
    };
  }
  return { kind: "fresh", text: `Last evaluated ${lastCompleted.from(now)}` };
}

let nextKey = 0;
/** A key for a new target row, stable across renders. */
export const newTargetKey = () => `new-${++nextKey}`;

export function emptyDraft(): RuleDraft {
  return {
    name: "",
    enabled: true,
    threshold: "5",
    statuses: [],
    eventTypes: [],
    errorTextContains: "",
    resolveAfterMinutes: String(RESOLVE_DEFAULT),
    targets: [],
  };
}

/** The draft of an existing rule. Targets keep their ids, so the server keeps them and their secrets. */
export function draftFromRule(rule: api.AlertRuleModel): RuleDraft {
  return {
    name: rule.name ?? "",
    enabled: rule.enabled ?? true,
    threshold: String(rule.condition?.threshold ?? ""),
    statuses: [...(rule.condition?.statuses ?? [])],
    eventTypes: [...(rule.condition?.eventTypes ?? [])],
    errorTextContains: rule.condition?.errorTextContains ?? "",
    resolveAfterMinutes: String(rule.resolveAfterMinutes ?? RESOLVE_DEFAULT),
    targets: (rule.targets ?? []).map((t) => ({
      key: t.id ?? newTargetKey(),
      id: t.id,
      type: t.type === "webhook" ? "webhook" : "mail",
      value: (t.type === "webhook" ? t.url : t.address) ?? "",
    })),
  };
}

const wholeNumberIn = (text: string, min: number, max: number) => {
  const trimmed = text.trim();
  if (!/^\d+$/.test(trimmed)) return false;
  const n = Number(trimmed);
  return n >= min && n <= max;
};

/** The checks the server makes, so most mistakes are caught before a round trip. */
export function validateDraft(
  draft: RuleDraft,
  delivery: { mailEnabled: boolean; webhookEnabled: boolean },
): DraftErrors {
  const errors: DraftErrors = {};
  const name = draft.name.trim();
  if (name.length < 1 || name.length > NAME_MAX) errors.name = `Enter a name of 1 to ${NAME_MAX} characters.`;
  if (!wholeNumberIn(draft.threshold, THRESHOLD_MIN, THRESHOLD_MAX))
    errors.threshold = `Enter a whole number from ${THRESHOLD_MIN} to ${THRESHOLD_MAX}.`;
  if (!wholeNumberIn(draft.resolveAfterMinutes, RESOLVE_MIN, RESOLVE_MAX))
    errors.resolveAfterMinutes = `Enter a whole number of minutes from ${RESOLVE_MIN} to ${RESOLVE_MAX}.`;
  if (draft.errorTextContains.length > ERROR_TEXT_MAX)
    errors.errorTextContains = `At most ${ERROR_TEXT_MAX} characters.`;
  if (draft.eventTypes.length > EVENT_TYPES_MAX) errors.eventTypes = `Pick at most ${EVENT_TYPES_MAX} event types.`;
  if (draft.targets.length < 1) errors.targets = "Add at least one target.";
  else if (draft.targets.length > TARGETS_MAX) errors.targets = `A rule has at most ${TARGETS_MAX} targets.`;
  for (const target of draft.targets) {
    const value = target.value.trim();
    const key = `target:${target.key}`;
    if (target.type === "mail") {
      if (!delivery.mailEnabled) errors[key] = "Alert mail is not configured on this WebApp. Remove this target.";
      else if (!EMAIL_PATTERN.test(value)) errors[key] = "Enter a mail address.";
    } else {
      if (!delivery.webhookEnabled) errors[key] = "Alert webhooks are not configured on this WebApp. Remove this target.";
      else if (!isHttpsUrl(value)) errors[key] = "Enter an https URL.";
    }
  }
  return errors;
}

/** The request for a valid draft. A kept target sends its id; a new one omits it. */
export function requestFromDraft(draft: RuleDraft): api.AlertRuleRequest {
  // Not trimmed: spaces may be part of the text to match. Blank means no filter.
  const errorText = draft.errorTextContains;
  return new api.AlertRuleRequest({
    name: draft.name.trim(),
    enabled: draft.enabled,
    condition: new api.AlertRuleCondition({
      kind: "failure_backlog",
      eventTypes: [...draft.eventTypes],
      statuses: [...draft.statuses],
      errorTextContains: errorText.trim() === "" ? undefined : errorText,
      threshold: Number(draft.threshold.trim()),
    }),
    resolveAfterMinutes: Number(draft.resolveAfterMinutes.trim()),
    targets: draft.targets.map(
      (t) =>
        new api.AlertRuleTargetModel({
          id: t.id,
          type: t.type,
          address: t.type === "mail" ? t.value.trim() : undefined,
          url: t.type === "webhook" ? t.value.trim() : undefined,
        }),
    ),
  });
}

/** The request that saves a rule unchanged except for the given fields (Disable/Enable). */
export function requestFromRule(rule: api.AlertRuleModel, changes: { enabled: boolean }): api.AlertRuleRequest {
  return requestFromDraft({ ...draftFromRule(rule), enabled: changes.enabled });
}
