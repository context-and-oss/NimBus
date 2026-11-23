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
export const WINDOW_MIN = 5;
export const WINDOW_MAX = 1440;
export const WINDOW_DEFAULT = 15;
export const BLOCKED_MIN = 1;
export const BLOCKED_MAX = 1440;
export const BLOCKED_DEFAULT = 30;
export const DEFERRED_MIN = 1;
export const DEFERRED_MAX = 1000;
export const SILENCE_MIN = 5;
export const SILENCE_MAX = 1440;
export const SILENCE_DEFAULT = 60;
export const CATEGORIES_MAX = 8;

/** The condition kinds a rule can watch, in the order the form offers them. */
export const CONDITION_KINDS = ["failure_backlog", "failure_occurrences", "blocked_session", "silence"] as const;
export type ConditionKind = (typeof CONDITION_KINDS)[number];

/** Each kind's name in the form. */
export const KIND_LABELS: Record<ConditionKind, string> = {
  failure_backlog: "Failure backlog",
  failure_occurrences: "Failures in a time window",
  blocked_session: "Blocked session",
  silence: "Silence",
};

const isKind = (value: string | undefined): value is ConditionKind =>
  (CONDITION_KINDS as readonly string[]).includes(value ?? "");

/** Whether a kind counts failures, so takes a threshold, filters and categories. */
export const isCountedKind = (kind: ConditionKind): boolean =>
  kind === "failure_backlog" || kind === "failure_occurrences";

/** A classification category in words: `transient_dependency` reads "Transient dependency". */
export function categoryLabel(category: string): string {
  const words = category.replace(/_/g, " ");
  return words.charAt(0).toUpperCase() + words.slice(1);
}

/** A condition's kind; a missing kind is a failure backlog, as on the server. */
export function kindOf(condition: api.AlertRuleCondition | undefined): ConditionKind {
  const kind = condition?.kind?.trim().toLowerCase();
  return isKind(kind) ? kind : "failure_backlog";
}

/** The kinds this deployment can evaluate. A WebApp that names none evaluates only the backlog. */
export function availableKindsOf(kinds: string[] | undefined): ConditionKind[] {
  const known = CONDITION_KINDS.filter((k) => (kinds ?? []).includes(k));
  return known.length > 0 ? known : ["failure_backlog"];
}

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
  kind: ConditionKind;
  /** Backlog and occurrences. */
  threshold: string;
  statuses: string[];
  eventTypes: string[];
  errorTextContains: string;
  /** Backlog and occurrences: the classification categories counted. None counts every failure. */
  categories: string[];
  /** Occurrences: the window failed attempts are counted in. */
  windowMinutes: string;
  /** Blocked session: how long the oldest blocking event has not changed. Blank means not checked. */
  blockedMinutes: string;
  /** Blocked session: the deferred events at which the rule fires. Blank means not checked. */
  deferredThreshold: string;
  /** Silence: how long nothing completed. */
  silenceMinutes: string;
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

/** Minutes as the notices write them: "45 min", "1 h", "1 h 30 min". */
export function duration(minutes: number | undefined): string {
  const value = minutes ?? 0;
  if (value < 60) return `${value} min`;
  return value % 60 === 0 ? `${value / 60} h` : `${Math.floor(value / 60)} h ${value % 60} min`;
}

const filtersText = (condition: api.AlertRuleCondition, statuses: boolean) => {
  let text = "";
  const statusList = statuses ? (condition.statuses ?? []) : [];
  if (statusList.length > 0) text += ` (${statusList.join(", ")})`;
  const eventTypes = condition.eventTypes ?? [];
  if (eventTypes.length > 0) text += ` of type ${eventTypes.join(" or ")}`;
  if (condition.errorTextContains?.trim()) text += ` with an error containing "${condition.errorTextContains}"`;
  const categories = condition.categories ?? [];
  if (categories.length > 0) text += ` classified as ${categories.map((c) => c.replace(/_/g, " ")).join(" or ")}`;
  return text;
};

const isSet = (n: number | undefined | null): n is number => n !== undefined && n !== null;

/**
 * The condition in words, as the notices write them (AlertRuleText.DescribeCondition), e.g.
 * `5 or more failed events (Failed) of type OrderPlaced`, `3 or more failures in 15 min`,
 * `a session blocked for 30 min, or 50 or more deferred events` or `no completed message for 1 h`.
 */
export function describeCondition(condition: api.AlertRuleCondition | undefined): string {
  const c = condition ?? new api.AlertRuleCondition();
  const threshold = c.threshold ?? 0;
  switch (kindOf(c)) {
    case "failure_occurrences":
      return `${threshold} or more ${plural(threshold, "failure", "failures")} in ${duration(c.windowMinutes)}${filtersText(c, false)}`;
    case "blocked_session": {
      const parts: string[] = [];
      if (isSet(c.minutes)) parts.push(`a session blocked for ${duration(c.minutes)}`);
      if (isSet(c.deferredThreshold))
        parts.push(`${c.deferredThreshold} or more deferred ${plural(c.deferredThreshold, "event", "events")}`);
      return parts.join(", or ");
    }
    case "silence":
      return `no completed message for ${duration(c.minutes)}`;
    default:
      return `${threshold} or more failed ${plural(threshold, "event", "events")}${filtersText(c, true)}`;
  }
}

/** What a rule's last value is, as the notices name it (AlertRuleText.ValueLabel). */
export function valueLabel(condition: api.AlertRuleCondition | undefined): string {
  switch (kindOf(condition)) {
    case "failure_occurrences":
      return "Failures in the window";
    case "blocked_session":
      return "Blocked for (minutes)";
    case "silence":
      return "Silent for (minutes)";
    default:
      return "Failed events";
  }
}

/** How long the condition must stay clear before the rule resolves, in words. */
export function resolveText(rule: api.AlertRuleModel): string {
  const kind = kindOf(rule.condition);
  return kind === "failure_backlog" || kind === "failure_occurrences"
    ? `Resolves after ${rule.resolveAfterMinutes} min below the threshold`
    : `Resolves after ${rule.resolveAfterMinutes} min without the condition`;
}

/**
 * The last read: "–" before the first evaluation. A count is read up to the threshold, so reaching
 * it shows "≥ N"; silence is read up to its minutes the same way. A blocked session shows how long
 * the oldest blocking event has waited, which a rule that watches only deferred events doesn't read.
 */
export function lastValueText(rule: api.AlertRuleModel): string {
  return conditionValueText(rule.condition, rule.state?.lastValue);
}

/** A value of the condition, read as the evaluator reads it; see {@link lastValueText}. */
export function conditionValueText(
  condition: api.AlertRuleCondition | undefined,
  value: number | undefined | null,
): string {
  if (value === undefined || value === null) return "–";
  switch (kindOf(condition)) {
    case "blocked_session":
      return isSet(condition?.minutes) ? duration(value) : "–";
    case "silence": {
      const minutes = condition?.minutes;
      return isSet(minutes) && value >= minutes ? `≥ ${duration(minutes)}` : duration(value);
    }
    default: {
      const threshold = condition?.threshold;
      return threshold !== undefined && value >= threshold ? `≥ ${threshold}` : String(value);
    }
  }
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
    kind: "failure_backlog",
    threshold: "5",
    statuses: [],
    eventTypes: [],
    errorTextContains: "",
    categories: [],
    windowMinutes: String(WINDOW_DEFAULT),
    blockedMinutes: String(BLOCKED_DEFAULT),
    deferredThreshold: "",
    silenceMinutes: String(SILENCE_DEFAULT),
    resolveAfterMinutes: String(RESOLVE_DEFAULT),
    targets: [],
  };
}

/**
 * The draft of an existing rule. Targets keep their ids, so the server keeps them and their secrets.
 * The other kinds' fields keep their defaults, so switching the kind while editing starts from them.
 */
export function draftFromRule(rule: api.AlertRuleModel): RuleDraft {
  const condition = rule.condition;
  const kind = kindOf(condition);
  const text = (n: number | undefined | null) => (isSet(n) ? String(n) : "");
  const defaults = emptyDraft();
  const counted = isCountedKind(kind);
  return {
    name: rule.name ?? "",
    enabled: rule.enabled ?? true,
    kind,
    threshold: counted ? text(condition?.threshold) : defaults.threshold,
    statuses: [...(condition?.statuses ?? [])],
    eventTypes: [...(condition?.eventTypes ?? [])],
    errorTextContains: condition?.errorTextContains ?? "",
    categories: counted ? [...(condition?.categories ?? [])] : [],
    windowMinutes: kind === "failure_occurrences" ? text(condition?.windowMinutes) : defaults.windowMinutes,
    // A blocked-session rule may watch only deferred events; its minutes then stay blank.
    blockedMinutes: kind === "blocked_session" ? text(condition?.minutes) : defaults.blockedMinutes,
    deferredThreshold: kind === "blocked_session" ? text(condition?.deferredThreshold) : defaults.deferredThreshold,
    silenceMinutes: kind === "silence" ? text(condition?.minutes) : defaults.silenceMinutes,
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
  Object.assign(errors, validateCondition(draft));
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

/** The checks of the condition and its resolve time alone: what a preview needs. */
export function validateCondition(draft: RuleDraft): DraftErrors {
  const errors: DraftErrors = {};
  const minutesIn = (min: number, max: number) => `Enter a whole number of minutes from ${min} to ${max}.`;
  const counted = isCountedKind(draft.kind);
  if (counted && !wholeNumberIn(draft.threshold, THRESHOLD_MIN, THRESHOLD_MAX))
    errors.threshold = `Enter a whole number from ${THRESHOLD_MIN} to ${THRESHOLD_MAX}.`;
  if (draft.kind === "failure_occurrences" && !wholeNumberIn(draft.windowMinutes, WINDOW_MIN, WINDOW_MAX))
    errors.windowMinutes = minutesIn(WINDOW_MIN, WINDOW_MAX);
  if (draft.kind === "blocked_session") {
    const minutes = draft.blockedMinutes.trim();
    const deferred = draft.deferredThreshold.trim();
    if (minutes === "" && deferred === "") errors.blocked = "Enter the blocked minutes, the deferred events or both.";
    if (minutes !== "" && !wholeNumberIn(minutes, BLOCKED_MIN, BLOCKED_MAX))
      errors.blockedMinutes = minutesIn(BLOCKED_MIN, BLOCKED_MAX);
    if (deferred !== "" && !wholeNumberIn(deferred, DEFERRED_MIN, DEFERRED_MAX))
      errors.deferredThreshold = `Enter a whole number from ${DEFERRED_MIN} to ${DEFERRED_MAX}.`;
  }
  if (draft.kind === "silence" && !wholeNumberIn(draft.silenceMinutes, SILENCE_MIN, SILENCE_MAX))
    errors.silenceMinutes = minutesIn(SILENCE_MIN, SILENCE_MAX);
  if (!wholeNumberIn(draft.resolveAfterMinutes, RESOLVE_MIN, RESOLVE_MAX))
    errors.resolveAfterMinutes = minutesIn(RESOLVE_MIN, RESOLVE_MAX);
  if (counted && draft.errorTextContains.length > ERROR_TEXT_MAX)
    errors.errorTextContains = `At most ${ERROR_TEXT_MAX} characters.`;
  if (counted && draft.eventTypes.length > EVENT_TYPES_MAX)
    errors.eventTypes = `Pick at most ${EVENT_TYPES_MAX} event types.`;
  if (counted && draft.categories.length > CATEGORIES_MAX)
    errors.categories = `Pick at most ${CATEGORIES_MAX} categories.`;
  return errors;
}

const optionalNumber = (text: string) => (text.trim() === "" ? undefined : Number(text.trim()));

/** The condition of a valid draft, with only the fields its kind uses. Other kinds send no categories. */
function conditionFromDraft(draft: RuleDraft): api.AlertRuleCondition {
  switch (draft.kind) {
    case "blocked_session":
      return new api.AlertRuleCondition({
        kind: draft.kind,
        minutes: optionalNumber(draft.blockedMinutes),
        deferredThreshold: optionalNumber(draft.deferredThreshold),
        categories: [],
      });
    case "silence":
      return new api.AlertRuleCondition({
        kind: draft.kind,
        minutes: Number(draft.silenceMinutes.trim()),
        categories: [],
      });
    default: {
      // Not trimmed: spaces may be part of the text to match. Blank means no filter.
      const errorText = draft.errorTextContains;
      const occurrences = draft.kind === "failure_occurrences";
      return new api.AlertRuleCondition({
        kind: draft.kind,
        eventTypes: [...draft.eventTypes],
        statuses: occurrences ? undefined : [...draft.statuses],
        errorTextContains: errorText.trim() === "" ? undefined : errorText,
        categories: [...draft.categories],
        threshold: Number(draft.threshold.trim()),
        windowMinutes: occurrences ? Number(draft.windowMinutes.trim()) : undefined,
      });
    }
  }
}

/** The request for a valid draft. A kept target sends its id; a new one omits it. */
export function requestFromDraft(draft: RuleDraft): api.AlertRuleRequest {
  return new api.AlertRuleRequest({
    name: draft.name.trim(),
    enabled: draft.enabled,
    condition: conditionFromDraft(draft),
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

/** The preview request for a draft whose condition is valid: its condition and resolve time, nothing else. */
export function previewRequestFromDraft(draft: RuleDraft): api.AlertRulePreviewRequest {
  return new api.AlertRulePreviewRequest({
    condition: conditionFromDraft(draft),
    resolveAfterMinutes: Number(draft.resolveAfterMinutes.trim()),
  });
}

/** Why a preview has no value now, in a sentence. */
export function nowUnavailableText(reason: string): string {
  switch (reason) {
    case "kind_unavailable":
      return "This WebApp's storage can't read this condition, so there is no value now.";
    case "classification_unavailable":
      return "The failure classifications can't be read, so there is no value now.";
    case "classification_scan_limit":
      return "There are too many failures to check their classifications, so there is no value now.";
    default:
      return "The value now can't be read.";
  }
}

/** Why a preview has no replay of the last 24 hours, in a sentence. */
export function replayUnsupportedText(reason: string): string {
  switch (reason) {
    case "no_history":
      return "Backlog and blocked-session rules can't be replayed: deleted events leave no history.";
    case "categories":
      return "Rules with categories can't be replayed: classifications arrive after the failure.";
    case "kind_unavailable":
      return "This WebApp's storage can't replay this condition.";
    default:
      return "This rule can't be replayed.";
  }
}

/** A would-have-fired period in local time: "09:14–09:31", or "since 09:14" while still open. */
export function periodText(period: api.AlertRulePreviewPeriod, now: moment.Moment = moment()): string {
  if (!period.fromUtc) return "";
  const from = formatSince(period.fromUtc, now);
  if (!period.toUtc) return `since ${from}`;
  const sameDay = period.toUtc.clone().local().isSame(period.fromUtc.clone().local(), "day");
  return `${from}–${sameDay ? period.toUtc.clone().local().format("HH:mm") : formatSince(period.toUtc, now)}`;
}
