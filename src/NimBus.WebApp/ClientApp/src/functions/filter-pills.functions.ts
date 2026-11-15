import moment from "moment";
import { PERIOD_OPTIONS } from "./failed-messages.functions";

// The value format of a datetime-local input, read and written in local time.
const LOCAL_INPUT = "YYYY-MM-DDTHH:mm";

const PERIOD_LABELS: Record<string, string> = {
  "1h": "Last hour",
  "12h": "Last 12 hours",
  "1d": "Last 24 hours",
  "3d": "Last 3 days",
  "7d": "Last 7 days",
  "30d": "Last 30 days",
};

/** A time pill's label for a period option ("Last 7 days"). */
export const periodLabel = (label: string) =>
  PERIOD_LABELS[label] ?? `Last ${label}`;

/**
 * A time filter as the URL keeps it: a relative preset (`period`, "" for any time) or an
 * explicit range of datetime-local strings, either end of which may be open. An explicit
 * range wins over a preset.
 */
export interface TimeRangeValue {
  period: string;
  from: string;
  to: string;
}

/** A date as a datetime-local input value, in local time. */
export function toLocalInput(date: Date): string {
  return moment(date).format(LOCAL_INPUT);
}

/** Parses a datetime-local input value; undefined when empty or invalid. */
export function fromLocalInput(value: string): Date | undefined {
  if (!value) return undefined;
  const parsed = moment(value, LOCAL_INPUT);
  return parsed.isValid() ? parsed.toDate() : undefined;
}

/** Start of a relative preset window ending now; undefined for any time or an unknown preset. */
export function presetStart(period: string, now: Date): Date | undefined {
  const option = PERIOD_OPTIONS.find((p) => p.value === period);
  return option
    ? new Date(now.getTime() - option.spanMinutes * 60_000)
    : undefined;
}

/** The time pill's label for an applied time filter. */
export function timeRangeLabel(value: TimeRangeValue): string {
  const fmt = (raw: string) => {
    const date = fromLocalInput(raw);
    return date ? moment(date).format("DD/MM/YYYY HH:mm") : raw;
  };
  if (value.from && value.to) return `${fmt(value.from)} – ${fmt(value.to)}`;
  if (value.from) return `From ${fmt(value.from)}`;
  if (value.to) return `Until ${fmt(value.to)}`;
  const option = PERIOD_OPTIONS.find((p) => p.value === value.period);
  return option ? periodLabel(option.label) : "Any time";
}

/** True when the time filter narrows the results. */
export const isTimeFiltered = (value: TimeRangeValue) =>
  !!(value.from || value.to || presetStart(value.period, new Date()));

// prefix:"quoted value" | prefix:value | "quoted text" | word
const ID_TOKEN = /(\w+):"([^"]*)"|(\w+):(\S+)|"([^"]*)"|(\S+)/g;

/**
 * Reads an ID search box. `prefix:value` sets the field the prefix names (case-insensitive);
 * anything else is the `bare` field. Every field the prefixes name comes back, "" when unset.
 */
export function parseIdQuery<F extends string>(
  query: string,
  prefixes: Readonly<Record<string, F>>,
  bare: F,
): Record<F, string> {
  const fields = Object.fromEntries(
    Object.values(prefixes).map((f) => [f, ""]),
  ) as Record<F, string>;
  fields[bare] = "";
  const text: string[] = [];
  for (const m of query.matchAll(ID_TOKEN)) {
    const prefix = m[1] ?? m[3];
    const field = prefix ? prefixes[prefix.toLowerCase()] : undefined;
    if (field) fields[field] = m[2] ?? m[4] ?? "";
    else text.push(m[5] ?? m[0]);
  }
  const joined = text.join(" ").trim();
  if (joined) fields[bare] = joined;
  return fields;
}

/** The search box text for applied IDs; the inverse of {@link parseIdQuery}. */
export function idQueryOf<F extends string>(
  values: Readonly<Partial<Record<F, string>>>,
  prefixes: Readonly<Record<string, F>>,
): string {
  return Object.entries(prefixes)
    .map(([prefix, field]) => {
      const value = (values[field as F] ?? "").trim();
      if (!value) return "";
      return /[\s"]/.test(value)
        ? `${prefix}:"${value}"`
        : `${prefix}:${value}`;
    })
    .filter(Boolean)
    .join(" ");
}
