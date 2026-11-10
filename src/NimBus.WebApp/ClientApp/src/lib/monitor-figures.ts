import type * as api from "api-client";

/**
 * Arithmetic behind the Monitor's tiles and table, kept apart from the page so each number's
 * definition is tested on its own. Counts come from the metrics feed: "failed" there means failed
 * attempts (error responses) in the window, not the unresolved state.
 */

/** Published, handled and failed in the last complete minute (the bucket before the current one). */
export function lastCompleteMinute(
  lastHour: api.TimeSeriesDataPoint[] | undefined,
): { published: number; handled: number; failed: number } | undefined {
  if (!lastHour || lastHour.length < 2) return undefined;
  const bucket = lastHour[lastHour.length - 2];
  return {
    published: bucket.published ?? 0,
    handled: bucket.handled ?? 0,
    failed: bucket.failed ?? 0,
  };
}

/** handled ÷ (handled + failed) as a fraction, or undefined when there were no attempts. */
export function successRate(counts: api.MonitorFeedCounts | undefined): number | undefined {
  if (!counts) return undefined;
  const attempts = (counts.handled ?? 0) + (counts.failed ?? 0);
  return attempts === 0 ? undefined : (counts.handled ?? 0) / attempts;
}

/** failed ÷ (handled + failed) as a fraction, or undefined when there were no attempts. */
export function failRate(counts: api.MonitorFeedCounts | undefined): number | undefined {
  const rate = successRate(counts);
  return rate === undefined ? undefined : 1 - rate;
}

/**
 * Average queue wait plus average processing time, each from its own population, or undefined
 * when either is missing.
 */
export function timeToHandleMs(counts: api.MonitorFeedCounts | undefined): number | undefined {
  const queue = averageMs(counts?.queue);
  const processing = averageMs(counts?.processing);
  return queue === undefined || processing === undefined ? undefined : queue + processing;
}

/** A latency aggregate's average, or undefined when it counted nothing. */
export function averageMs(stats: api.LatencyStats | undefined): number | undefined {
  return stats && (stats.count ?? 0) > 0 ? (stats.avgMs ?? 0) : undefined;
}

/** "850 ms", "1.3 s", "4.1 min", "2.0 h"; "–" for undefined. */
export function formatMs(ms: number | undefined): string {
  if (ms === undefined || !Number.isFinite(ms)) return "–";
  if (ms < 1_000) return `${Math.round(ms)} ms`;
  if (ms < 60_000) return `${(ms / 1_000).toFixed(1)} s`;
  if (ms < 3_600_000) return `${(ms / 60_000).toFixed(1)} min`;
  return `${(ms / 3_600_000).toFixed(1)} h`;
}

/** "90.5%"; "–" for undefined. One decimal under 99.95 %, so 99.96 % does not read as 100 %. */
export function formatPercent(fraction: number | undefined): string {
  if (fraction === undefined || !Number.isFinite(fraction)) return "–";
  const percent = fraction * 100;
  if (percent > 0 && percent < 0.05) return "<0.1%";
  if (percent < 100 && percent >= 99.95) return ">99.9%";
  return `${percent.toFixed(percent === 0 || percent === 100 ? 0 : 1)}%`;
}

/**
 * The start of a feed bucket as a Date. Bucket keys are ISO-8601 UTC truncated to the bucket size
 * ("2026-11-10T12:34", "2026-11-10T12", "2026-11-10").
 */
export function bucketStart(timestamp: string): Date {
  let iso = timestamp;
  if (timestamp.length === 10) iso = `${timestamp}T00:00:00Z`;
  else if (timestamp.length === 13) iso = `${timestamp}:00:00Z`;
  else if (timestamp.length === 16) iso = `${timestamp}:00Z`;
  else if (!timestamp.endsWith("Z")) iso = `${timestamp}Z`;
  return new Date(iso);
}

/**
 * Fills a sparse hourly latency series into one entry per hour of the last-day window, in order,
 * with undefined where the hour had no timed outcome.
 */
export function hourlySlots(
  points: api.MonitorFeedLatencyPoint[] | undefined,
  hours: number,
  now: Date,
): { timestamp: string; point?: api.MonitorFeedLatencyPoint }[] {
  const byKey = new Map((points ?? []).map((p) => [p.timestamp ?? "", p]));
  const end = new Date(Date.UTC(now.getUTCFullYear(), now.getUTCMonth(), now.getUTCDate(), now.getUTCHours()));
  const slots: { timestamp: string; point?: api.MonitorFeedLatencyPoint }[] = [];
  for (let i = hours - 1; i >= 0; i--) {
    const key = new Date(end.getTime() - i * 3_600_000).toISOString().slice(0, 13);
    slots.push({ timestamp: key, point: byKey.get(key) });
  }
  return slots;
}
