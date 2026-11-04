import moment from "moment";
import * as api from "api-client";
import { customRangeError } from "functions/time-range.functions";

/** The resolution statuses the Failed page covers. */
export const FAILED_STATUSES = [
  api.Statuses.Failed,
  api.Statuses.DeadLettered,
  api.Statuses.Unsupported,
] as const;

/** Chart / badge colour per failure status. DeadLettered is darker and Unsupported muted, so they differ in lightness, not just hue. */
export const STATUS_COLORS: Record<string, string> = {
  Failed: "#C2412E",
  DeadLettered: "#6E1F14",
  Unsupported: "#B9B09A",
};

/** Distinguishable series colours for the per-endpoint split. */
export const ENDPOINT_PALETTE = [
  "#3A6FB0",
  "#C98A1B",
  "#2E8F5E",
  "#6B3FA3",
  "#C2412E",
  "#1F7A8C",
  "#8A5E0F",
  "#A33F6B",
];

export interface PeriodOption {
  value: api.Period;
  label: string;
  spanMinutes: number;
  bucketMinutes: number;
}

/**
 * Range presets. Mirrors FailedImplementation.ResolveWindow on the server so the list's
 * time window matches the chart's buckets exactly.
 */
export const PERIOD_OPTIONS: PeriodOption[] = [
  { value: api.Period._1h, label: "1h", spanMinutes: 60, bucketMinutes: 5 },
  { value: api.Period._12h, label: "12h", spanMinutes: 720, bucketMinutes: 30 },
  { value: api.Period._1d, label: "1d", spanMinutes: 1440, bucketMinutes: 60 },
  { value: api.Period._3d, label: "3d", spanMinutes: 4320, bucketMinutes: 180 },
  {
    value: api.Period._7d,
    label: "7d",
    spanMinutes: 10080,
    bucketMinutes: 360,
  },
  {
    value: api.Period._30d,
    label: "30d",
    spanMinutes: 43200,
    bucketMinutes: 1440,
  },
];

export const DEFAULT_PERIOD = api.Period._1d;

export function periodOption(period: string): PeriodOption {
  return (
    PERIOD_OPTIONS.find((p) => p.value === period) ??
    PERIOD_OPTIONS.find((p) => p.value === DEFAULT_PERIOD)!
  );
}

/** The window a preset covers: it ends at the end of the bucket holding `now`. */
export function resolveWindow(
  period: string,
  now: Date,
): { from: Date; to: Date } {
  const option = periodOption(period);
  const bucketMs = option.bucketMinutes * 60_000;
  const to = Math.floor(now.getTime() / bucketMs) * bucketMs + bucketMs;
  return { from: new Date(to - option.spanMinutes * 60_000), to: new Date(to) };
}

// Mirror FailedImplementation.MaxBuckets and CustomBucketSizes.
const MAX_CUSTOM_BUCKETS = 60;
const CUSTOM_BUCKET_MINUTES = [5, 15, 30, 60, 180, 360, 720, 1440];

/** The custom range in the URL, or undefined when a preset applies. */
export function customRange(
  values: Pick<FailedFilterValues, "rangeStart" | "rangeEnd">,
): { from: Date; to: Date } | undefined {
  if (!values.rangeStart || !values.rangeEnd) return undefined;
  const from = new Date(values.rangeStart);
  const to = new Date(values.rangeEnd);
  return customRangeError(from, to) ? undefined : { from, to };
}

/**
 * The chart's window and bucket size: a custom range widened to whole buckets (the smallest
 * bucket that keeps it within 60 bars), else the period preset. Mirrors
 * FailedImplementation.ResolveWindow so the list's window matches the chart's buckets.
 */
export function rangeWindow(
  values: Pick<FailedFilterValues, "period" | "rangeStart" | "rangeEnd">,
  now: Date,
): { from: Date; to: Date; bucketMinutes: number } {
  const custom = customRange(values);
  if (!custom) {
    return {
      ...resolveWindow(values.period, now),
      bucketMinutes: periodOption(values.period).bucketMinutes,
    };
  }
  const span = custom.to.getTime() - custom.from.getTime();
  const bucketMinutes =
    CUSTOM_BUCKET_MINUTES.find(
      (m) => Math.floor(span / (m * 60_000)) <= MAX_CUSTOM_BUCKETS,
    ) ?? CUSTOM_BUCKET_MINUTES[CUSTOM_BUCKET_MINUTES.length - 1];
  const bucketMs = bucketMinutes * 60_000;
  return {
    from: new Date(Math.floor(custom.from.getTime() / bucketMs) * bucketMs),
    to: new Date(Math.ceil(custom.to.getTime() / bucketMs) * bucketMs),
    bucketMinutes,
  };
}

/** The URL values for a custom range (undefined returns to the period preset). */
export function rangeParams(
  range: { from: Date; to: Date } | undefined,
): Pick<FailedFilterValues, "rangeStart" | "rangeEnd"> {
  return {
    rangeStart: range ? range.from.toISOString() : "",
    rangeEnd: range ? range.to.toISOString() : "",
  };
}

/** URL-backed filter state of the Failed page. Strings only, so it round-trips through the query string. */
export type FailedFilterValues = {
  period: string;
  /** ISO start of a custom chart range; with `rangeEnd` it replaces `period`. */
  rangeStart: string;
  /** ISO end of a custom chart range. */
  rangeEnd: string;
  bucket: string;
  endpointId: string[];
  status: string[];
  eventTypeId: string[];
  eventId: string;
  lastMessageId: string;
  sessionId: string;
  from: string;
  to: string;
  errorText: string;
  view: string;
  split: string;
  /** ISO start of the brushed list window ("" for the whole range). */
  windowStart: string;
  /** ISO end (exclusive) of the brushed list window. */
  windowEnd: string;
  /** How the list renders: "items" (default) or "table". */
  display: string;
};

export const EMPTY_FAILED_FILTER: FailedFilterValues = {
  period: DEFAULT_PERIOD,
  rangeStart: "",
  rangeEnd: "",
  bucket: "",
  endpointId: [],
  status: [],
  eventTypeId: [],
  eventId: "",
  lastMessageId: "",
  sessionId: "",
  from: "",
  to: "",
  errorText: "",
  view: "list",
  split: "status",
  windowStart: "",
  windowEnd: "",
  display: "items",
};

/** The subset of filter fields the filter bar edits (the rest is view state). */
export const SEARCH_FIELDS = [
  "endpointId",
  "status",
  "eventTypeId",
  "eventId",
  "lastMessageId",
  "sessionId",
  "from",
  "to",
  "errorText",
] as const;

/**
 * The API filter for the applied values. `window` narrows UpdatedAt to the chart range (or
 * the selected bar); the histogram is requested without it so the chart keeps the whole range.
 */
export function toFailedSearchFilter(
  values: FailedFilterValues,
  window?: { from: Date; to: Date },
): api.FailedSearchFilter {
  const filter = new api.FailedSearchFilter();
  const text = (v: string) => (v.trim() ? v.trim() : undefined);
  if (values.endpointId.length) filter.endpointIds = [...values.endpointId];
  const statuses = values.status.filter((s): s is api.Statuses =>
    (FAILED_STATUSES as readonly string[]).includes(s),
  );
  if (statuses.length) filter.statuses = statuses;
  if (values.eventTypeId.length) filter.eventTypeId = [...values.eventTypeId];
  filter.eventId = text(values.eventId);
  filter.lastMessageId = text(values.lastMessageId);
  filter.sessionId = text(values.sessionId);
  filter.from = text(values.from);
  filter.to = text(values.to);
  filter.errorText = text(values.errorText);
  if (window) {
    filter.updatedAtFrom = moment(window.from);
    // The API's UpdatedAtTo is inclusive; step back one millisecond to keep buckets half-open.
    filter.updatedAtTo = moment(window.to.getTime() - 1);
  }
  return filter;
}

/**
 * The window the list is narrowed to: the brushed `windowStart`/`windowEnd`, else the bar a
 * legacy `bucket` link selected. Undefined for the whole range.
 */
export function selectedWindow(
  values: FailedFilterValues,
): { from: Date; to: Date } | undefined {
  if (values.windowStart && values.windowEnd) {
    const from = new Date(values.windowStart);
    const to = new Date(values.windowEnd);
    return Number.isNaN(from.getTime()) ||
      Number.isNaN(to.getTime()) ||
      from >= to
      ? undefined
      : { from, to };
  }
  if (!values.bucket) return undefined;
  const start = new Date(values.bucket);
  if (Number.isNaN(start.getTime())) return undefined;
  const { bucketMinutes } = rangeWindow(values, new Date());
  return {
    from: start,
    to: new Date(start.getTime() + bucketMinutes * 60_000),
  };
}

/** The URL values for a list window (undefined clears it). Always drops the legacy `bucket`. */
export function windowParams(
  window: { from: Date; to: Date } | undefined,
): Pick<FailedFilterValues, "bucket" | "windowStart" | "windowEnd"> {
  return {
    bucket: "",
    windowStart: window ? window.from.toISOString() : "",
    windowEnd: window ? window.to.toISOString() : "",
  };
}

/** The search fields the single search box reads and writes. */
export type SearchBoxFields = Pick<
  FailedFilterValues,
  "eventId" | "lastMessageId" | "sessionId" | "errorText"
>;

const GUID =
  /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const SEARCH_PREFIXES: Record<string, keyof SearchBoxFields> = {
  event: "eventId",
  message: "lastMessageId",
  session: "sessionId",
};
// prefix:"quoted value" | prefix:value | "quoted text" | word
const SEARCH_TOKEN = /(\w+):"([^"]*)"|(\w+):(\S+)|"([^"]*)"|(\S+)/g;

/**
 * Reads the search box. `event:`, `message:` and `session:` set those fields; a lone bare
 * GUID is an event ID; everything else (and any quoted text) is error text.
 */
export function parseSearchQuery(query: string): SearchBoxFields {
  const fields: SearchBoxFields = {
    eventId: "",
    lastMessageId: "",
    sessionId: "",
    errorText: "",
  };
  const text: string[] = [];
  let quoted = false;
  for (const m of query.matchAll(SEARCH_TOKEN)) {
    const prefix = m[1] ?? m[3];
    const field = prefix ? SEARCH_PREFIXES[prefix.toLowerCase()] : undefined;
    if (field) {
      fields[field] = m[2] ?? m[4] ?? "";
    } else if (m[5] !== undefined) {
      text.push(m[5]);
      quoted = true;
    } else {
      text.push(m[0]);
    }
  }
  const joined = text.join(" ").trim();
  if (!quoted && !fields.eventId && text.length === 1 && GUID.test(joined)) {
    fields.eventId = joined;
  } else {
    fields.errorText = joined;
  }
  return fields;
}

/** The search box text for the applied values; the inverse of {@link parseSearchQuery}. */
export function searchQueryOf(values: SearchBoxFields): string {
  const token = (prefix: string, value: string) =>
    /[\s"]/.test(value) ? `${prefix}:"${value}"` : `${prefix}:${value}`;
  const parts: string[] = [];
  if (values.eventId) parts.push(token("event", values.eventId));
  if (values.lastMessageId) parts.push(token("message", values.lastMessageId));
  if (values.sessionId) parts.push(token("session", values.sessionId));
  const error = values.errorText.trim();
  if (error) {
    parts.push(
      /[:"]/.test(error) || /\s{2,}/.test(error) || GUID.test(error)
        ? `"${error}"`
        : error,
    );
  }
  return parts.join(" ");
}

/** Toggles one failure status. An empty list means all three. */
export function toggleStatus(current: string[], status: string): string[] {
  const on = current.length ? current : [...FAILED_STATUSES];
  const next = on.includes(status)
    ? on.filter((s) => s !== status)
    : [...on, status];
  return next.length === FAILED_STATUSES.length ? [] : next;
}

/**
 * The text a failure is shown by: the handler's error, else what Service Bus recorded when it
 * dead-lettered the message. Mirrors FailedImplementation.ErrorTextOf.
 */
export function errorTextOf(event: api.Event): string | undefined {
  return (
    [
      event.messageContent?.errorContent?.errorText,
      event.deadLetterErrorDescription,
      event.deadLetterReason,
      event.reason,
    ].find((t) => !!t && t.trim().length > 0) ??
    (event.resolutionStatus === api.ResolutionStatus.Unsupported
      ? "Unsupported: no handler for this event type"
      : undefined)
  );
}

/** Deferred-message counts per session from a session-batch response ("eventId_sessionId" ids). */
export function deferredCountsBySession(
  deferredEvents: string[],
): Record<string, number> {
  const counts: Record<string, number> = {};
  for (const id of deferredEvents) {
    const sessionId = id.split("_")[1];
    if (sessionId) counts[sessionId] = (counts[sessionId] ?? 0) + 1;
  }
  return counts;
}

/** Axis / tooltip label for a bucket start. Carries the date once buckets span days. */
export function formatBucket(
  start: moment.Moment | Date | undefined,
  bucketMinutes: number,
  withEnd = false,
): string {
  if (!start) return "";
  const m = moment(start);
  if (bucketMinutes >= 1440) return m.format("DD MMM");
  const label =
    bucketMinutes >= 180 ? m.format("ddd DD HH:mm") : m.format("HH:mm");
  if (!withEnd) return label;
  const end = m.clone().add(bucketMinutes, "minutes");
  return `${m.format("DD/MM HH:mm")}–${end.format("HH:mm")}`;
}

/**
 * Unresolved failures in endpoint status counts. The API's failedCount already includes
 * DeadLettered and Unsupported (Mapper.EndpointStatusCountFromEndpointStateCount).
 */
export function failureBacklog(counts: api.EndpointStatusCount[]): number {
  return counts.reduce((sum, c) => sum + (c.failedCount ?? 0), 0);
}
