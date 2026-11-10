import { useEffect, useMemo, useState } from "react";
import moment from "moment";
import type * as api from "api-client";
import { cn } from "lib/utils";
import { canContributeTo, useAccess } from "hooks/use-access";
import {
  useMonitorData,
  type FleetSample,
  type MonitorEndpoint,
  type MonitorFeedInfo,
  type TickerEvent,
} from "hooks/use-monitor-data";
import { useMonitorMetrics } from "hooks/use-monitor-metrics";
import { readDashboardToken } from "lib/monitor-access";
import {
  averageMs,
  bucketStart,
  failRate,
  formatMs,
  formatPercent,
  hourlySlots,
  lastCompleteMinute,
  successRate,
  timeToHandleMs,
} from "lib/monitor-figures";
import { ChartLegend, SeriesChart, type ChartSeries } from "components/monitor/series-chart";

/* Theme-aware status tints, shared with the rest of the app: a dark "ink" hue on the cream
   light theme and a lifted tint on warm-black dark mode. Every fill is flat. */
const DANGER_TEXT = "text-status-danger-ink dark:text-[#E4A096]";
const WARNING_TEXT = "text-status-warning-ink dark:text-[#E7C783]";
const SUCCESS_TEXT = "text-status-success-ink dark:text-[#7FD2A6]";

/** Backlog (pending + deferred) at which an endpoint starts shouting. */
export const BACKLOG_ELEVATED = 100;
/** Backlog at which it gets the loudest treatment the amber band has. */
export const BACKLOG_HIGH = 500;

/**
 * One size knob for the whole wall: every dimension below is in `em`, so the page scales with
 * the screen (about 15 px on a 1080p display and 30 px on 4K).
 */
const WALL_FONT_SIZE = "clamp(12px, 0.78vw, 30px)";

/** Processing slower than this multiple of its 24 h average is called out. */
const SLOWER_THAN_USUAL = 1.5;

export type BacklogLevel = "normal" | "elevated" | "high";

/** How the fleet ring, table and legend classify an endpoint. */
export type EndpointState = "nogo" | "acked" | "hold" | "go";

/**
 * Live Monitor: a wall display for the whole fleet.
 *
 *  - Header: environment, T+ since the oldest open failure, local + UTC clock
 *  - Summary: GO ring and six tiles (unresolved failures, blocked sessions, backlog,
 *    throughput now, success rate and time to handle over the last hour)
 *  - Charts: last hour per minute, last 3 days per hour, time to handle per hour
 *  - One endpoint table, failing first, with ACK for Contributors
 *  - Footer: ticker of recent events and a refresh heartbeat
 *
 * Opened as `/Monitor#token=…` it reads the feed with a dashboard token: read-only, no ACK,
 * and no call to any other API. Without a token it needs the site Reader role.
 */
export default function Monitor() {
  const token = useMemo(() => readDashboardToken(), []);
  return token ? <MonitorWall token={token} canAck={() => false} /> : <SignedInMonitor />;
}

/** The signed-in Monitor: ACK follows the caller's endpoint roles. */
function SignedInMonitor() {
  const { access } = useAccess();
  // Until access resolves, offer ACK and let the server decide: a transient /me failure must
  // not strand an operator without the button.
  const canAck = (endpointId: string) => access === null || canContributeTo(access, endpointId);
  return <MonitorWall canAck={canAck} />;
}

function MonitorWall({ token, canAck }: { token?: string; canAck: (endpointId: string) => boolean }) {
  const data = useMonitorData({ token });
  const { metrics, history } = useMonitorMetrics({ token });
  const [now, setNow] = useState(() => new Date());
  const [showCursor, setShowCursor] = useState(true);
  const readOnly = token !== undefined;

  useEffect(() => {
    const handle = window.setInterval(() => setNow(new Date()), 1000);
    return () => window.clearInterval(handle);
  }, []);

  // Signed out: the login page is reached through the app root, which picks the sign-in mode.
  useEffect(() => {
    if (!readOnly && data.accessError === "unauthorized") window.location.assign("/");
  }, [readOnly, data.accessError]);

  // Auto-hide the cursor after 5 s of stillness so the wall doesn't burn in a stray pointer.
  useEffect(() => {
    let timeout: number | undefined;
    const handleMove = () => {
      setShowCursor(true);
      if (timeout) window.clearTimeout(timeout);
      timeout = window.setTimeout(() => setShowCursor(false), 5000);
    };
    handleMove();
    window.addEventListener("mousemove", handleMove);
    return () => {
      window.removeEventListener("mousemove", handleMove);
      if (timeout) window.clearTimeout(timeout);
    };
  }, []);

  const summary = useMemo(() => summarize(data.endpoints), [data.endpoints]);
  const nowMs = now.getTime();

  if (data.accessError && (readOnly || data.accessError === "forbidden")) {
    return <AccessRefused readOnly={readOnly} />;
  }

  return (
    <div
      className={cn(
        "min-h-screen flex flex-col gap-[0.8em] p-[0.9em]",
        "bg-background text-foreground font-sans font-features-['tnum']",
        showCursor ? "" : "cursor-none",
      )}
      style={{ fontSize: WALL_FONT_SIZE }}
    >
      {data.isStale && <StaleBanner lastRefreshAt={data.lastRefreshAt} now={nowMs} />}
      <div
        className={cn(
          "flex flex-col gap-[0.8em] transition-[opacity,filter]",
          data.isStale ? "opacity-70 grayscale" : "opacity-100",
        )}
      >
        <Hero
          feed={data.feed}
          readOnly={readOnly}
          clock={now}
          unackedFresh={summary.unackedFreshFailures}
          openSince={firstOpenFailureAt(data.endpoints)}
          actionError={data.actionError}
        />
        <SummaryRow endpoints={data.endpoints} summary={summary} metrics={metrics} />
        <ChartsRow metrics={metrics} history={history} now={now} />
        <EndpointTable
          endpoints={data.endpoints}
          metrics={metrics}
          history={history}
          readOnly={readOnly}
          canAck={canAck}
          onAck={data.ack}
          onUnack={data.unack}
          now={nowMs}
        />
        <WallFoot ticker={data.ticker} lastRefreshAt={data.lastRefreshAt} isStale={data.isStale} now={nowMs} />
      </div>
    </div>
  );
}

/* =====================================================================
   Header
   ===================================================================== */

interface HeroProps {
  feed?: MonitorFeedInfo;
  readOnly: boolean;
  clock: Date;
  unackedFresh: number;
  openSince: number | undefined;
  actionError?: string;
}

const Hero = ({ feed, readOnly, clock, unackedFresh, openSince, actionError }: HeroProps) => {
  const upperEnv = (feed?.environment ?? "").toUpperCase();
  // PROD gets a red-tinted pill; anything else borrows the warning amber so it still reads as
  // "this is not prod".
  const envClass =
    upperEnv === "PROD" || upperEnv === "PRD"
      ? cn("bg-status-danger/[0.14] border-status-danger/50", DANGER_TEXT)
      : cn("bg-status-warning/12 border-status-warning/50", WARNING_TEXT);

  return (
    <header className="flex flex-wrap items-center gap-x-[1.6em] gap-y-[0.8em] px-[1.4em] py-[0.8em] rounded-nb-md bg-card border border-border">
      <div className="inline-flex items-center gap-[0.35em] pr-[0.9em] border-r border-border text-[1.8em] font-extrabold tracking-tight">
        <LogoMark />
        <span>
          NimBus<span className="text-primary">.</span>
        </span>
      </div>
      <div className="min-w-0">
        <h1 className="m-0 text-[1.35em] font-bold tracking-tight">Live monitor</h1>
        <div className="font-mono text-[0.85em] text-muted-foreground mt-[0.3em]">
          {feed?.platformName ? `${feed.platformName} · ` : ""}Auto-refresh every 5 s ·{" "}
          {readOnly ? "read-only dashboard" : "ACK to silence known failures"}
          {unackedFresh > 0 && (
            <span className={DANGER_TEXT}>{` · ${unackedFresh} new failure${unackedFresh === 1 ? "" : "s"}`}</span>
          )}
          {actionError && <span className={DANGER_TEXT}>{` · ACK not saved: ${actionError}`}</span>}
        </div>
      </div>
      <div className="flex-1" />
      {upperEnv && (
        <span
          className={cn(
            "font-mono text-[0.9em] tracking-[0.16em] uppercase font-bold",
            "px-[0.9em] py-[0.45em] rounded-nb-sm border inline-flex items-center gap-[0.5em]",
            envClass,
          )}
        >
          <Pulser />
          {upperEnv}
        </span>
      )}
      <div className="pl-[1.6em] border-l border-border">
        <Label>{openSince ? "T+ since first failure" : "No open failures"}</Label>
        <div
          className={cn(
            "font-mono font-medium text-[2.4em] leading-none mt-[0.15em]",
            openSince ? DANGER_TEXT : SUCCESS_TEXT,
          )}
        >
          {openSince ? formatElapsed(clock.getTime() - openSince) : "--:--:--"}
        </div>
      </div>
      <div className="pl-[1.6em] border-l border-border text-right">
        <Label>
          {moment(clock).format("ddd · DD MMM YYYY")} · UTC {moment(clock).utc().format("HH:mm")}
        </Label>
        <div className="font-mono font-medium text-[2.4em] leading-none mt-[0.15em]">{formatClock(clock)}</div>
      </div>
    </header>
  );
};

const Label = ({ children, className }: { children: React.ReactNode; className?: string }) => (
  <div className={cn("font-mono text-[0.75em] tracking-[0.14em] uppercase text-muted-foreground", className)}>
    {children}
  </div>
);

/* =====================================================================
   Summary row: GO ring and six tiles
   ===================================================================== */

interface SummaryShape {
  totalEndpoints: number;
  nogoCount: number;
  ackedCount: number;
  holdCount: number;
  unackedFreshFailures: number;
  /** Failed + dead-lettered + unsupported, as the status API counts it. */
  unresolvedTotal: number;
  deadletterTotal: number;
  unsupportedTotal: number;
  pendingTotal: number;
  deferredTotal: number;
  backlogDeltaPerMin: number;
  /** Undefined when no endpoint reported a count. */
  blockedSessionsTotal: number | undefined;
  topBlocked: { id: string; count: number }[];
}

type Tone = "danger" | "warning" | "neutral";

const TILE_TONE: Record<Tone, string> = {
  danger: "bg-status-danger/10 border-status-danger border-[1.5px]",
  warning: "bg-status-warning/10 border-status-warning border-[1.5px]",
  neutral: "bg-card border-border border",
};

const SummaryRow = ({
  endpoints,
  summary,
  metrics,
}: {
  endpoints: MonitorEndpoint[];
  summary: SummaryShape;
  metrics?: api.MonitorFeedMetrics;
}) => {
  const failedOnly = summary.unresolvedTotal - summary.deadletterTotal - summary.unsupportedTotal;
  const minute = lastCompleteMinute(metrics?.lastHour);
  const fleet = metrics?.fleet;
  const success = successRate(fleet);
  const failures = failRate(fleet) ?? 0;
  const attempts = (fleet?.handled ?? 0) + (fleet?.failed ?? 0);
  const handleMs = timeToHandleMs(fleet);
  const backlog = summary.pendingTotal + summary.deferredTotal;
  const trend = Math.round(summary.backlogDeltaPerMin);

  return (
    <section aria-label="Fleet summary" className="flex flex-wrap gap-[0.7em]">
      <div className="flex-[1.3_1_17em] min-w-0 rounded-nb-md bg-card border border-border px-[1.1em] py-[0.8em] flex items-center gap-[1.1em]">
        <FleetRing endpoints={endpoints} />
        <div className="flex flex-col gap-[0.3em] text-[0.95em]">
          <Label>Endpoints</Label>
          <LegendRow swatch="bg-status-danger" count={summary.nogoCount} label="no-go" />
          <LegendRow swatch="bg-border-strong" count={summary.ackedCount} label="failing, acked" />
          <LegendRow swatch="bg-status-warning" count={summary.holdCount} label="hold · backlog" />
        </div>
      </div>
      <Tile
        label="Unresolved failures"
        tone={summary.unresolvedTotal > 0 ? "danger" : "neutral"}
        value={formatBigNumber(summary.unresolvedTotal)}
        valueClass={summary.unresolvedTotal > 0 ? DANGER_TEXT : undefined}
        sub={`${formatBigNumber(failedOnly)} failed · ${formatBigNumber(summary.deadletterTotal)} dead-lettered · ${formatBigNumber(summary.unsupportedTotal)} unsupported`}
      />
      <Tile
        label="Blocked sessions"
        title="Estimated from stored status: sessions with a failed, dead-lettered or pending hand-off message. A message dead-lettered on its first attempt does not block its session but is counted."
        tone={(summary.blockedSessionsTotal ?? 0) > 0 ? "warning" : "neutral"}
        value={summary.blockedSessionsTotal === undefined ? "–" : formatBigNumber(summary.blockedSessionsTotal)}
        sub={[
          ...summary.topBlocked.map((b) => `${formatBigNumber(b.count)} ${b.id}`),
          `${formatBigNumber(summary.deferredTotal)} deferred`,
        ].join(" · ")}
      />
      <Tile
        label="Backlog"
        tone={backlog >= BACKLOG_ELEVATED ? "warning" : "neutral"}
        value={formatBigNumber(backlog)}
        aside={trend === 0 ? "stable" : `${trend > 0 ? "▲" : "▼"} ${formatBigNumber(Math.abs(trend))} / min`}
        asideClass={trend > 0 ? WARNING_TEXT : undefined}
        sub={`${formatBigNumber(summary.pendingTotal)} pending · ${formatBigNumber(summary.deferredTotal)} deferred`}
      />
      <Tile
        label="Throughput now"
        tone="neutral"
        value={minute ? formatBigNumber(minute.published) : "–"}
        unit="/ min in"
        sub={minute ? `${formatBigNumber(minute.handled)} handled · ${formatBigNumber(minute.failed)} failed per min` : "last complete minute"}
      />
      <Tile
        label="Success rate · last hour"
        tone={success === undefined ? "neutral" : failures >= 0.05 ? "danger" : failures >= 0.01 ? "warning" : "neutral"}
        value={formatPercent(success)}
        valueClass={success !== undefined && failures >= 0.05 ? DANGER_TEXT : undefined}
        sub={attempts === 0 ? "no attempts" : `${formatBigNumber(fleet?.failed ?? 0)} of ${formatBigNumber(attempts)} attempts failed`}
      />
      <Tile
        label="Time to handle · last hour"
        tone="neutral"
        value={formatMs(handleMs)}
        sub={`queue ${formatMs(averageMs(fleet?.queue))} · processing ${formatMs(averageMs(fleet?.processing))}`}
      />
    </section>
  );
};

const Tile = ({
  label,
  title,
  tone,
  value,
  valueClass,
  unit,
  aside,
  asideClass,
  sub,
}: {
  label: string;
  title?: string;
  tone: Tone;
  value: string;
  valueClass?: string;
  unit?: string;
  aside?: string;
  asideClass?: string;
  sub: string;
}) => (
  <div
    className={cn("flex-[1_1_13em] min-w-0 rounded-nb-md px-[1.1em] py-[0.9em] flex flex-col gap-[0.4em]", TILE_TONE[tone])}
    title={title}
  >
    <Label>{label}</Label>
    <div className="flex items-baseline justify-between gap-[0.6em]">
      <span className={cn("font-mono font-bold text-[2.3em] leading-none tracking-tight tabular-nums", valueClass)}>
        {value}
        {unit && <small className="text-[0.4em] font-normal text-muted-foreground ml-[0.4em]">{unit}</small>}
      </span>
      {aside && <span className={cn("font-mono text-[0.85em] font-bold", asideClass)}>{aside}</span>}
    </div>
    <div className="text-[0.85em] text-muted-foreground">{sub}</div>
  </div>
);

const LegendRow = ({ swatch, count, label }: { swatch: string; count: number; label: string }) => (
  <span className="inline-flex items-center gap-[0.5em]">
    <span aria-hidden="true" className={cn("w-[0.7em] h-[0.7em] rounded-[2px]", swatch)} />
    <strong className="tabular-nums">{count}</strong> {label}
  </span>
);

const RING_STROKE: Record<EndpointState, string> = {
  nogo: "stroke-status-danger",
  acked: "stroke-border-strong",
  hold: "stroke-status-warning",
  go: "stroke-status-success",
};

/** One segment per endpoint, A → Z, so a segment never moves between polls. */
const FleetRing = ({ endpoints }: { endpoints: MonitorEndpoint[] }) => {
  const sorted = [...endpoints].sort((a, b) => a.id.localeCompare(b.id));
  const n = sorted.length;
  const go = sorted.filter((e) => endpointState(e) === "go").length;
  const r = 42;
  const gap = n > 1 ? 3.2 / r : 0;
  const point = (a: number) => `${(50 + r * Math.cos(a)).toFixed(2)} ${(50 + r * Math.sin(a)).toFixed(2)}`;
  return (
    <div className="relative grid place-items-center shrink-0">
      <svg viewBox="0 0 100 100" className="w-[6.2em] h-[6.2em]" role="img" aria-label={`${go} of ${n} endpoints GO`}>
        {n <= 1 ? (
          <circle
            cx={50}
            cy={50}
            r={r}
            fill="none"
            strokeWidth={10}
            className={n === 1 ? RING_STROKE[endpointState(sorted[0])] : "stroke-border"}
          />
        ) : (
          sorted.map((e, i) => {
            const a0 = -Math.PI / 2 + (i / n) * 2 * Math.PI + gap / 2;
            const a1 = -Math.PI / 2 + ((i + 1) / n) * 2 * Math.PI - gap / 2;
            return (
              <path
                key={e.id}
                d={`M${point(a0)} A${r} ${r} 0 ${a1 - a0 > Math.PI ? 1 : 0} 1 ${point(a1)}`}
                fill="none"
                strokeWidth={10}
                className={RING_STROKE[endpointState(e)]}
              >
                <title>{e.id}</title>
              </path>
            );
          })
        )}
      </svg>
      <div className="absolute inset-0 grid place-content-center text-center pointer-events-none">
        <span className={cn("text-[1.8em] font-extrabold leading-none", SUCCESS_TEXT)}>{go}</span>
        <span className="font-mono text-[0.7em] tracking-widest text-muted-foreground mt-[0.2em]">/ {n} GO</span>
      </div>
    </div>
  );
};

/* =====================================================================
   Charts
   ===================================================================== */

const THROUGHPUT_LINES: ChartSeries[] = [
  { key: "published", label: "Published", stroke: "stroke-status-info", fill: "fill-status-info" },
  { key: "handled", label: "Handled", stroke: "stroke-status-success", fill: "fill-status-success" },
];
const FAILED_BAR: ChartSeries = { key: "failed", label: "Failed", stroke: "stroke-status-danger", fill: "fill-status-danger" };
const PROCESSING_LINE: ChartSeries[] = [
  { key: "processing", label: "Processing · handler at work", stroke: "stroke-nimbus-purple", fill: "fill-nimbus-purple" },
];
const QUEUED_LINE: ChartSeries[] = [
  { key: "queue", label: "Queued · waiting to be picked up", stroke: "stroke-status-warning", fill: "fill-status-warning" },
];

const countBuckets = (points: api.TimeSeriesDataPoint[] | undefined) =>
  (points ?? []).map((p) => ({
    timestamp: p.timestamp ?? "",
    values: { published: p.published ?? 0, handled: p.handled ?? 0, failed: p.failed ?? 0 },
  }));

const ChartsRow = ({
  metrics,
  history,
  now,
}: {
  metrics?: api.MonitorFeedMetrics;
  history?: api.MonitorFeedHistory;
  now: Date;
}) => {
  const lastHour = countBuckets(metrics?.lastHour);
  const lastDays = countBuckets(history?.lastThreeDays);
  const latencySlots = hourlySlots(history?.latencyLastDay ?? undefined, 24, now);
  const latency = latencySlots.map((slot) => ({
    timestamp: slot.timestamp,
    values: {
      processing: slot.point ? (averageMs(slot.point.processing) ?? null) : null,
      queue: slot.point ? (averageMs(slot.point.queue) ?? null) : null,
    },
  }));
  const latencyNow = latencySlots[latencySlots.length - 1]?.point;

  return (
    <section className="flex flex-wrap gap-[0.7em]">
      <Panel
        title="Last hour"
        note={`per minute${metrics?.stale ? " · data from the last successful read" : ""}`}
        legend={<ChartLegend lines={THROUGHPUT_LINES} bar={FAILED_BAR} />}
      >
        {lastHour.length > 0 ? (
          <SeriesChart
            ariaLabel="Published, handled and failed messages per minute over the last hour"
            buckets={lastHour}
            lines={THROUGHPUT_LINES}
            bar={FAILED_BAR}
            formatY={formatAxisCount}
            formatX={(t) => moment(bucketStart(t)).format("HH:mm")}
            partialLast
          />
        ) : (
          <ChartPlaceholder />
        )}
      </Panel>
      <Panel
        title="Last 3 days"
        note={`per hour · current hour dashed${history?.stale ? " · data from the last successful read" : ""}`}
        legend={<ChartLegend lines={THROUGHPUT_LINES} bar={FAILED_BAR} />}
      >
        {lastDays.length > 0 ? (
          <SeriesChart
            ariaLabel="Published, handled and failed messages per hour over the last three days"
            buckets={lastDays}
            lines={THROUGHPUT_LINES}
            bar={FAILED_BAR}
            formatY={formatAxisCount}
            formatX={(t) => moment(bucketStart(t)).format("ddd HH:mm")}
            partialLast
            xTicks={4}
          />
        ) : (
          <ChartPlaceholder />
        )}
      </Panel>
      <Panel title="Time to handle" note="average per hour · last 24 h · current hour dashed">
        {history?.latencyLastDay === null ? (
          <div className="h-[11em] grid place-content-center text-[0.9em] text-muted-foreground">
            This storage provider does not report timings per hour.
          </div>
        ) : (
          <div className="flex flex-col gap-[0.6em]">
            <LatencyChart
              series={PROCESSING_LINE}
              current={averageMs(latencyNow?.processing)}
              buckets={latency}
              ariaLabel="Average processing time per hour over the last 24 hours"
            />
            <LatencyChart
              series={QUEUED_LINE}
              current={averageMs(latencyNow?.queue)}
              buckets={latency}
              ariaLabel="Average queue wait per hour over the last 24 hours"
            />
          </div>
        )}
      </Panel>
    </section>
  );
};

const LatencyChart = ({
  series,
  current,
  buckets,
  ariaLabel,
}: {
  series: ChartSeries[];
  current: number | undefined;
  buckets: { timestamp: string; values: Record<string, number | null> }[];
  ariaLabel: string;
}) => (
  <div className="flex flex-col gap-[0.9em]">
    <div className="flex items-baseline justify-between gap-[1em]">
      <ChartLegend lines={series} />
      <span className="font-mono text-[0.85em]">
        <strong>{formatMs(current)}</strong> now
      </span>
    </div>
    <SeriesChart
      ariaLabel={ariaLabel}
      buckets={buckets}
      lines={series}
      formatY={(v) => (v === 0 ? "0" : formatMs(v))}
      formatX={(t) => moment(bucketStart(t)).format("HH:mm")}
      partialLast
      heightEm={4.2}
      xTicks={4}
    />
  </div>
);

const Panel = ({
  title,
  note,
  legend,
  children,
}: {
  title: string;
  note: string;
  legend?: React.ReactNode;
  children: React.ReactNode;
}) => (
  <div className="flex-[1_1_26em] min-w-0 rounded-nb-md bg-card border border-border px-[1.2em] py-[0.9em] flex flex-col gap-[0.8em]">
    <div className="flex flex-wrap items-baseline justify-between gap-x-[1em] gap-y-[0.3em]">
      <div className="flex items-baseline gap-[0.6em]">
        <h2 className="m-0 text-[0.85em] font-extrabold tracking-[0.12em] uppercase">{title}</h2>
        <span className="text-[0.8em] text-muted-foreground">{note}</span>
      </div>
      {legend}
    </div>
    {children}
  </div>
);

const ChartPlaceholder = () => (
  <div className="h-[11em] grid place-content-center font-mono text-[0.85em] text-muted-foreground">Loading…</div>
);

/* =====================================================================
   Endpoint table
   ===================================================================== */

const STATE_ORDER: Record<EndpointState, number> = { nogo: 0, acked: 1, hold: 2, go: 3 };

/** Failing first (unacknowledged before acknowledged, larger first), then backlog, then A → Z. */
export function sortForTable(endpoints: MonitorEndpoint[]): MonitorEndpoint[] {
  return [...endpoints].sort((a, b) => {
    const byState = STATE_ORDER[endpointState(a)] - STATE_ORDER[endpointState(b)];
    if (byState !== 0) return byState;
    const state = endpointState(a);
    if (state === "nogo" || state === "acked") {
      const byFailed = (b.status.failedCount ?? 0) - (a.status.failedCount ?? 0);
      if (byFailed !== 0) return byFailed;
    }
    if (state === "hold") {
      const byBacklog = backlogTotal(b) - backlogTotal(a);
      if (byBacklog !== 0) return byBacklog;
    }
    return a.id.localeCompare(b.id);
  });
}

const EndpointTable = ({
  endpoints,
  metrics,
  history,
  readOnly,
  canAck,
  onAck,
  onUnack,
  now,
}: {
  endpoints: MonitorEndpoint[];
  metrics?: api.MonitorFeedMetrics;
  history?: api.MonitorFeedHistory;
  readOnly: boolean;
  canAck: (endpointId: string) => boolean;
  onAck: (id: string, reason?: string) => void;
  onUnack: (id: string) => void;
  now: number;
}) => {
  const lastHour = new Map((metrics?.endpoints ?? []).map((e) => [(e.endpointId ?? "").toLowerCase(), e.counts]));
  const lastDay = new Map((history?.endpoints ?? []).map((e) => [(e.endpointId ?? "").toLowerCase(), e.processing]));
  const rows = sortForTable(endpoints);
  const header = "font-mono text-[0.72em] tracking-[0.1em] uppercase font-medium text-muted-foreground px-[0.8em] py-[0.5em]";

  return (
    <section aria-label="Endpoints" className="rounded-nb-md bg-card border border-border pt-[0.9em] pb-[0.3em]">
      <div className="flex flex-wrap items-baseline justify-between gap-x-[1.5em] gap-y-[0.3em] px-[1.2em] pb-[0.5em]">
        <h2 className="m-0 text-[0.85em] font-extrabold tracking-[0.12em] uppercase">Endpoints</h2>
        <span className="text-[0.8em] text-muted-foreground">
          {endpoints.length} total · failing first, then backlog, then A to Z · counts for the last hour · processing against its 24 h average
        </span>
      </div>
      <div className="overflow-x-auto px-[0.4em]">
        <table className="w-full min-w-[68em] border-collapse font-mono text-[0.9em]">
          <thead>
            <tr>
              <th scope="col" className={cn(header, "text-left")}>Endpoint</th>
              <th scope="col" className={cn(header, "text-left")}>State</th>
              <th scope="col" className={cn(header, "text-right")}>In</th>
              <th scope="col" className={cn(header, "text-right")}>Out</th>
              <th scope="col" className={cn(header, "text-right")}>Unresolved</th>
              <th scope="col" className={cn(header, "text-right")}>Fail %</th>
              <th scope="col" className={cn(header, "text-right")}>Pending</th>
              <th scope="col" className={cn(header, "text-right")}>Deferred</th>
              <th scope="col" className={cn(header, "text-right")}>Processing</th>
              <th scope="col" className={cn(header, "text-right")}>Queue wait</th>
              <th scope="col" className={cn(header, "text-right")}>Heartbeat</th>
            </tr>
          </thead>
          <tbody>
            {rows.map((endpoint) => (
              <EndpointRow
                key={endpoint.id}
                endpoint={endpoint}
                counts={lastHour.get(endpoint.id.toLowerCase())}
                usualProcessing={lastDay.get(endpoint.id.toLowerCase())}
                readOnly={readOnly}
                canAck={canAck(endpoint.id)}
                onAck={() => onAck(endpoint.id)}
                onUnack={() => onUnack(endpoint.id)}
                now={now}
              />
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
};

const ROW_TONE: Record<EndpointState, string> = {
  nogo: "bg-status-danger/10 border-y-[1.5px] border-status-danger",
  acked: "border-b border-dashed border-border-strong",
  hold: "bg-status-warning/10 border-b border-border",
  go: "border-b border-border",
};

const EndpointRow = ({
  endpoint,
  counts,
  usualProcessing,
  readOnly,
  canAck,
  onAck,
  onUnack,
  now,
}: {
  endpoint: MonitorEndpoint;
  counts?: api.MonitorFeedCounts;
  usualProcessing?: api.LatencyStats;
  readOnly: boolean;
  canAck: boolean;
  onAck: () => void;
  onUnack: () => void;
  now: number;
}) => {
  const state = endpointState(endpoint);
  const failing = state === "nogo" || state === "acked";
  const unavailable = endpoint.status.storageStatus === "unavailable";
  const received = (counts?.handled ?? 0) + (counts?.failed ?? 0);
  const fail = failRate(counts);
  const processing = averageMs(counts?.processing);
  const usual = averageMs(usualProcessing);
  const slower = processing !== undefined && usual !== undefined && usual > 0 && processing / usual >= SLOWER_THAN_USUAL;
  const queue = averageMs(counts?.queue);
  const pending = endpoint.status.pendingCount ?? 0;
  const deferred = endpoint.status.deferredCount ?? 0;
  const muted = "text-muted-foreground";
  const cell = "px-[0.8em] py-[0.55em] text-right whitespace-nowrap";

  return (
    <tr className={ROW_TONE[state]}>
      <th scope="row" className={cn("px-[0.8em] py-[0.55em] text-left font-sans text-[1.1em] font-bold", state === "acked" && muted)}>
        {endpoint.id}
        {unavailable && <span className={cn("ml-[0.6em] font-mono text-[0.65em] font-normal", WARNING_TEXT)}>storage unavailable</span>}
      </th>
      <td className="px-[0.8em] py-[0.55em] text-left whitespace-nowrap">
        <span className="inline-flex items-center gap-[0.5em]">
          {/* An operator's "✓ acked" button already says the state. */}
          {!(state === "acked" && !readOnly) && (
            <StateBadge state={state} fresh={endpoint.isFreshFailure && state === "nogo"} />
          )}
          {failing && !readOnly && (
            <AckButton acked={state === "acked"} disabled={!canAck} onAck={onAck} onUnack={onUnack} />
          )}
          {state === "acked" && endpoint.ack && (
            <span className={cn("font-sans text-[0.8em]", muted)} title={endpoint.ack.reason || undefined}>
              {endpoint.ack.acknowledgedBy ? `${shortName(endpoint.ack.acknowledgedBy)} · ` : ""}
              {formatDuration(now - endpoint.ack.ackedAt)} ago
            </span>
          )}
          {state === "nogo" && endpoint.firstFailureAt && (
            <span className={cn("font-sans text-[0.8em]", DANGER_TEXT)}>for {formatDuration(now - endpoint.firstFailureAt)}</span>
          )}
        </span>
      </td>
      <td className={cn(cell, received === 0 && muted)}>{received === 0 ? "–" : formatBigNumber(received)}</td>
      <td className={cn(cell, !counts?.published && muted)}>{counts?.published ? formatBigNumber(counts.published) : "–"}</td>
      <td className={cn(cell, failing ? cn("text-[1.2em] font-bold", state === "nogo" ? DANGER_TEXT : muted) : muted)}>
        {formatBigNumber(endpoint.status.failedCount ?? 0)}
      </td>
      <td className={cn(cell, fail !== undefined && fail > 0 ? cn("font-bold", DANGER_TEXT) : muted)}>
        {fail === undefined ? "–" : formatPercent(fail)}
      </td>
      <td className={cn(cell, pending > 0 ? cn("font-bold", WARNING_TEXT) : muted)}>{formatBigNumber(pending)}</td>
      <td className={cn(cell, deferred > 0 ? cn("font-bold", WARNING_TEXT) : muted)}>{formatBigNumber(deferred)}</td>
      <td className={cell}>
        {processing === undefined ? (
          <span className={muted}>–</span>
        ) : (
          <>
            <span className={slower ? cn("font-bold", WARNING_TEXT) : undefined}>{formatMs(processing)}</span>
            {usual !== undefined && (
              <span className={muted}>
                {" · "}
                {slower ? `${(processing / usual).toFixed(1)}× usual` : "as usual"}
              </span>
            )}
          </>
        )}
      </td>
      <td className={cn(cell, queue === undefined && muted)}>{formatMs(queue)}</td>
      <td className={cell}>
        <HeartbeatCell heartbeat={endpoint.heartbeat} now={now} />
      </td>
    </tr>
  );
};

const HeartbeatCell = ({ heartbeat, now }: { heartbeat?: api.MonitorFeedHeartbeat; now: number }) => {
  if (!heartbeat) return <span className="text-muted-foreground">–</span>;
  const ago = heartbeat.lastAnsweredUtc ? `${formatDuration(now - heartbeat.lastAnsweredUtc.valueOf())} ago` : "never answered";
  switch (heartbeat.liveness) {
    case "alive":
      return <span className="text-muted-foreground">{ago}</span>;
    case "late":
      return <span className={cn("font-bold", WARNING_TEXT)}>late · {ago}</span>;
    case "missing":
      return <span className={cn("font-bold", DANGER_TEXT)}>missing · {ago}</span>;
    default:
      return <span className="text-muted-foreground">not deployed</span>;
  }
};

const STATE_BADGE: Record<EndpointState, { label: string; className: string }> = {
  nogo: { label: "no-go", className: "bg-status-danger text-white" },
  acked: { label: "acked", className: "bg-muted text-muted-foreground" },
  hold: { label: "hold", className: "bg-status-warning text-ink" },
  go: { label: "go", className: cn("bg-status-success/15", SUCCESS_TEXT) },
};

const StateBadge = ({ state, fresh }: { state: EndpointState; fresh: boolean }) => (
  <span
    className={cn(
      "font-mono text-[0.72em] tracking-[0.14em] uppercase font-bold px-[0.7em] py-[0.3em] rounded-nb-sm",
      STATE_BADGE[state].className,
      fresh && "nb-monitor-pulse-card",
    )}
  >
    {STATE_BADGE[state].label}
    {fresh ? " · new" : ""}
  </span>
);

const AckButton = ({
  acked,
  disabled,
  onAck,
  onUnack,
}: {
  acked: boolean;
  /** Read-only viewers still see the ACK state but cannot change it. */
  disabled: boolean;
  onAck: () => void;
  onUnack: () => void;
}) => (
  <button
    type="button"
    disabled={disabled}
    onClick={(e) => {
      e.preventDefault();
      if (acked) onUnack();
      else onAck();
    }}
    className={cn(
      "font-mono text-[0.72em] tracking-[0.14em] uppercase font-bold shrink-0",
      "min-h-[2.2em] px-[0.8em] rounded-nb-sm border",
      acked
        ? cn("bg-status-success/[0.14] border-status-success/40", SUCCESS_TEXT)
        : "bg-transparent text-muted-foreground border-border-strong",
      disabled
        ? "cursor-default"
        : cn("transition-colors cursor-pointer", !acked && "hover:text-foreground hover:border-foreground/40"),
      "focus-visible:outline-solid focus-visible:outline-2 focus-visible:outline-primary focus-visible:outline-offset-2",
    )}
    title={
      disabled
        ? "Acknowledging requires Contributor on this endpoint"
        : acked
          ? "Click to clear the acknowledgement on every Monitor"
          : "Acknowledge on every Monitor (auto-expires after 4 h or on recovery)"
    }
  >
    {acked ? "✓ acked" : "ack"}
  </button>
);

/* =====================================================================
   Access refused, footer, stale banner
   ===================================================================== */

const AccessRefused = ({ readOnly }: { readOnly: boolean }) => (
  <div
    className="min-h-screen grid place-content-center gap-[1em] p-[2em] text-center bg-background text-foreground font-sans"
    style={{ fontSize: WALL_FONT_SIZE }}
    role="alert"
  >
    <div className={cn("text-[2.4em] font-extrabold tracking-tight", readOnly ? DANGER_TEXT : undefined)}>
      {readOnly ? "This dashboard link is invalid, expired or revoked" : "The Monitor requires the Reader role"}
    </div>
    <div className="text-[1.1em] text-muted-foreground max-w-[44em]">
      {readOnly
        ? "Ask a site Owner for a new link from Settings → Dashboard tokens. This screen keeps retrying every 5 seconds."
        : "Ask a site Owner to grant you the site Reader role, or use a dashboard token link."}
    </div>
  </div>
);

const WallFoot = ({
  ticker,
  lastRefreshAt,
  isStale,
  now,
}: {
  ticker: TickerEvent[];
  lastRefreshAt: number | undefined;
  isStale: boolean;
  now: number;
}) => {
  const seconds = lastRefreshAt ? Math.max(0, Math.round((now - lastRefreshAt) / 1000)) : undefined;
  return (
    <footer className="flex flex-wrap items-center gap-x-[1.6em] gap-y-[0.5em] font-mono text-[0.85em] text-muted-foreground">
      <Ticker events={ticker} />
      <span className="inline-flex items-center gap-[0.5em] pl-[1.6em] border-l border-border">
        <span aria-hidden="true" className={cn("w-[0.55em] h-[0.55em] rounded-full", isStale ? "bg-status-danger" : "bg-status-success")} />
        {seconds === undefined
          ? "Waiting for first update"
          : isStale
            ? `No update for ${seconds}s`
            : `Updated ${seconds}s ago · every 5 s`}
      </span>
    </footer>
  );
};

const Ticker = ({ events }: { events: TickerEvent[] }) => {
  // The only thing on the wall that moves in the absence of incidents: it doubles as a liveness
  // signal. Paused on hover so an operator can read a line.
  if (events.length === 0) {
    return <div className="flex-1 min-w-0 overflow-hidden whitespace-nowrap text-muted-foreground/70">Waiting for activity…</div>;
  }
  return (
    <div className="flex-1 min-w-0 overflow-hidden">
      <div className="nb-monitor-ticker whitespace-nowrap">
        {events.map((e) => (
          <TickerItem key={e.id} event={e} />
        ))}
        {/* Duplicate the run so the marquee loop is seamless. */}
        {events.map((e) => (
          <TickerItem key={`${e.id}-dup`} event={e} />
        ))}
      </div>
    </div>
  );
};

const TickerItem = ({ event }: { event: TickerEvent }) => {
  const labelCls = event.kind === "failure" ? DANGER_TEXT : event.kind === "recovery" ? SUCCESS_TEXT : "text-foreground";
  const verb = event.kind === "failure" ? "failed" : event.kind === "recovery" ? "recovered" : event.kind === "ack" ? "acked" : "ack cleared";
  return (
    <span className="inline-block mr-[3em]">
      <span className="text-muted-foreground/70 mr-[0.6em]">{moment(event.t).format("HH:mm:ss")}</span>
      <span className={cn("font-semibold", labelCls)}>{event.endpoint}</span>
      <span className="ml-[0.4em]">
        {verb}
        {event.detail ? ` · ${event.detail}` : ""}
      </span>
    </span>
  );
};

const StaleBanner = ({ lastRefreshAt, now }: { lastRefreshAt: number | undefined; now: number }) => {
  const seconds = lastRefreshAt ? Math.round((now - lastRefreshAt) / 1000) : 0;
  const lastTs = lastRefreshAt ? moment(lastRefreshAt).format("HH:mm:ss") : "never";
  return (
    <div
      className={cn(
        "rounded-nb-md px-[1.2em] py-[0.8em] flex items-center gap-[0.8em]",
        "bg-status-danger/20 border border-status-danger/60 font-mono text-[1em] tracking-wider",
        DANGER_TEXT,
      )}
      role="alert"
    >
      <Pulser />
      <span className="font-bold uppercase">Connection lost</span>
      <span>
        Last update {lastTs} · {seconds}s ago · retrying every 5 s
      </span>
    </div>
  );
};

const Pulser = () => (
  <span aria-hidden="true" className="inline-block w-[0.6em] h-[0.6em] rounded-full bg-current nb-monitor-pulser" />
);

const LogoMark = () => (
  <svg width="1em" height="1em" viewBox="0 0 36 36" fill="none" aria-hidden="true">
    <path
      d="M5 22c0-3.5 2.8-6.3 6.3-6.3.7 0 1.4.1 2 .3.8-3.7 4.1-6.5 8-6.5 4.2 0 7.6 3.2 8.1 7.2 2.7.4 4.8 2.7 4.8 5.5 0 3.1-2.5 5.5-5.5 5.5H11.3C7.8 27.7 5 25 5 22z"
      stroke="currentColor"
      strokeWidth={2.5}
      strokeLinejoin="round"
    />
    <circle cx={29} cy={11} r={3} fill="#E8743C" />
  </svg>
);

/* =====================================================================
   Pure helpers
   ===================================================================== */

/**
 * NO-GO: failing and not acknowledged. ACKED: failing but silenced. HOLD: no failures, but
 * messages queued. GO: nothing failed or queued.
 */
export function endpointState(endpoint: MonitorEndpoint): EndpointState {
  if ((endpoint.status.failedCount ?? 0) > 0) {
    return endpoint.ack ? "acked" : "nogo";
  }
  return backlogTotal(endpoint) > 0 ? "hold" : "go";
}

/**
 * When the page first saw the oldest still-open (unacknowledged) failure: drives the header's
 * T+ clock. Undefined when nothing is failing unacked.
 */
export function firstOpenFailureAt(endpoints: MonitorEndpoint[]): number | undefined {
  let first: number | undefined;
  for (const e of endpoints) {
    if (endpointState(e) !== "nogo" || e.firstFailureAt === undefined) continue;
    if (first === undefined || e.firstFailureAt < first) first = e.firstFailureAt;
  }
  return first;
}

/**
 * Converts cumulative fleet failed totals into new failures per minute, measured over the
 * trailing minute of samples. Resubmits and skips shrink the total; those read as zero.
 */
export function failedPerMinuteSeries(telemetry: FleetSample[]): number[] {
  let start = 0;
  return telemetry.map((sample, i) => {
    while (start < i && sample.t - telemetry[start].t > 60_000) start += 1;
    const from = telemetry[start];
    const dt = sample.t - from.t;
    if (dt <= 0) return 0;
    return Math.max(0, ((sample.failed - from.failed) / dt) * 60_000);
  });
}

/** Pending + deferred: everything queued up in front of an endpoint. */
export function backlogTotal(endpoint: MonitorEndpoint): number {
  return (endpoint.status.pendingCount ?? 0) + (endpoint.status.deferredCount ?? 0);
}

/**
 * How loudly the wall calls out an endpoint's queue depth. Absolute thresholds keep the answer
 * predictable; growth only escalates an already-notable backlog.
 */
export function backlogLevel(endpoint: MonitorEndpoint): BacklogLevel {
  const total = backlogTotal(endpoint);
  if (total >= BACKLOG_HIGH) return "high";
  if (total < BACKLOG_ELEVATED) return "normal";
  return isBacklogGrowing(endpoint) ? "high" : "elevated";
}

function isBacklogGrowing(endpoint: MonitorEndpoint): boolean {
  if (endpoint.samples.length < 2) return false;
  const oldest = endpoint.samples[0];
  const latest = endpoint.samples[endpoint.samples.length - 1];
  return latest.pending + latest.deferred > oldest.pending + oldest.deferred;
}

function summarize(endpoints: MonitorEndpoint[]): SummaryShape {
  const summary: SummaryShape = {
    totalEndpoints: endpoints.length,
    nogoCount: 0,
    ackedCount: 0,
    holdCount: 0,
    unackedFreshFailures: 0,
    unresolvedTotal: 0,
    deadletterTotal: 0,
    unsupportedTotal: 0,
    pendingTotal: 0,
    deferredTotal: 0,
    backlogDeltaPerMin: 0,
    blockedSessionsTotal: undefined,
    topBlocked: [],
  };

  for (const e of endpoints) {
    const state = endpointState(e);
    if (state === "nogo") summary.nogoCount += 1;
    if (state === "acked") summary.ackedCount += 1;
    if (state === "hold") summary.holdCount += 1;
    if (state === "nogo" && e.isFreshFailure) summary.unackedFreshFailures += 1;
    summary.unresolvedTotal += e.status.failedCount ?? 0;
    summary.deadletterTotal += e.status.deadletterCount ?? 0;
    summary.unsupportedTotal += e.status.unsupportedCount ?? 0;
    summary.pendingTotal += e.status.pendingCount ?? 0;
    summary.deferredTotal += e.status.deferredCount ?? 0;
    if (e.blockedSessionCount !== undefined) {
      summary.blockedSessionsTotal = (summary.blockedSessionsTotal ?? 0) + e.blockedSessionCount;
    }
    // Backlog rate over the same sample window the status hook keeps.
    if (e.samples.length >= 2) {
      const oldest = e.samples[0];
      const latest = e.samples[e.samples.length - 1];
      const dt = latest.t - oldest.t;
      if (dt > 0) {
        summary.backlogDeltaPerMin +=
          ((latest.pending + latest.deferred - oldest.pending - oldest.deferred) / dt) * 60_000;
      }
    }
  }

  summary.topBlocked = endpoints
    .filter((e) => (e.blockedSessionCount ?? 0) > 0)
    .sort((a, b) => (b.blockedSessionCount ?? 0) - (a.blockedSessionCount ?? 0) || a.id.localeCompare(b.id))
    .slice(0, 2)
    .map((e) => ({ id: e.id, count: e.blockedSessionCount ?? 0 }));
  return summary;
}

function formatBigNumber(n: number): string {
  if (!Number.isFinite(n)) return "—";
  const abs = Math.abs(n);
  if (abs >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  return Math.round(n).toLocaleString();
}

function formatAxisCount(n: number): string {
  if (n >= 1_000) return `${(n / 1_000).toFixed(n >= 10_000 ? 0 : 1)}k`;
  return Math.round(n).toString();
}

function formatClock(d: Date): string {
  return [d.getHours(), d.getMinutes(), d.getSeconds()].map((p) => p.toString().padStart(2, "0")).join(":");
}

/** HH:MM:SS elapsed, for the T+ clock. */
export function formatElapsed(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  const clock = [Math.floor((s % 86_400) / 3600), Math.floor((s % 3600) / 60), s % 60]
    .map((part) => part.toString().padStart(2, "0"))
    .join(":");
  // The clock counts from the oldest open failure on the server, which can be days old.
  const days = Math.floor(s / 86_400);
  return days > 0 ? `${days}d ${clock}` : clock;
}

/** Compact duration: "16s", "52m 52s", "2h 04m". */
function formatDuration(ms: number): string {
  const s = Math.max(0, Math.floor(ms / 1000));
  if (s < 60) return `${s}s`;
  const m = Math.floor(s / 60);
  if (m < 60) return `${m}m ${(s % 60).toString().padStart(2, "0")}s`;
  const h = Math.floor(m / 60);
  if (h < 24) return `${h}h ${(m % 60).toString().padStart(2, "0")}m`;
  return `${Math.floor(h / 24)}d ${(h % 24).toString().padStart(2, "0")}h`;
}

/** "alice@example.com" → "alice"; display names pass through. */
function shortName(user: string): string {
  const at = user.indexOf("@");
  return at > 0 ? user.slice(0, at) : user;
}
