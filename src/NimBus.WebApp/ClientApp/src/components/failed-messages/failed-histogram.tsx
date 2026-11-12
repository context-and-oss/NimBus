import * as React from "react";
import * as api from "api-client";
import {
  Bar,
  BarChart,
  Brush,
  CartesianGrid,
  Cell,
  ResponsiveContainer,
  Tooltip,
  XAxis,
  YAxis,
} from "recharts";
import {
  ENDPOINT_PALETTE,
  FAILED_STATUSES,
  STATUS_COLORS,
  formatBucket,
} from "functions/failed-messages.functions";

const GRID = "#E5DFCE";
const INK3 = "#8A8473";
// Endpoints beyond this many are folded into "Other" in the per-endpoint split.
const MAX_ENDPOINT_SERIES = 7;
const OTHER = "Other";

export interface TimeWindow {
  from: Date;
  to: Date;
}

export interface FailedHistogramProps {
  histogram: api.FailedHistogram | undefined;
  split: "status" | "endpoint";
  /** The list window, or undefined for the whole range. */
  window: TimeWindow | undefined;
  /** Sets the list window (a clicked bar or the brushed range); undefined clears it. */
  onWindowChange: (window: TimeWindow | undefined) => void;
  isLoading: boolean;
}

// Recharts fires the brush's onChange on every drag step; the list reloads once it settles.
const BRUSH_SETTLE_MS = 350;
// The main chart's Y axis is this wide; the brush below it lines up with the plot area.
const Y_AXIS_WIDTH = 40;
const RIGHT_MARGIN = 8;

/** The first and last bar whose start falls inside `window` (every bar without one). */
export function brushIndexes(
  starts: number[],
  window: TimeWindow | undefined,
): { startIndex: number; endIndex: number } {
  const last = Math.max(starts.length - 1, 0);
  if (!window) return { startIndex: 0, endIndex: last };
  const from = window.from.getTime();
  const to = window.to.getTime();
  const inside = starts
    .map((start, i) => (start >= from && start < to ? i : -1))
    .filter((i) => i >= 0);
  return inside.length
    ? { startIndex: inside[0], endIndex: inside[inside.length - 1] }
    : { startIndex: 0, endIndex: last };
}

/**
 * The window a brushed index range covers; undefined when it spans every bar. A bar ends where
 * the next starts (bars across a daylight-saving change differ in length); the last ends at
 * `windowEnd`.
 */
export function windowOfIndexes(
  starts: number[],
  windowEnd: number,
  startIndex: number,
  endIndex: number,
): TimeWindow | undefined {
  if (starts.length === 0) return undefined;
  const first = Math.max(0, Math.min(startIndex, endIndex));
  const last = Math.min(starts.length - 1, Math.max(startIndex, endIndex));
  if (first === 0 && last === starts.length - 1) return undefined;
  return {
    from: new Date(starts[first]),
    to: new Date(starts[last + 1] ?? windowEnd),
  };
}

interface Series {
  key: string;
  label: string;
  color: string;
}

type ChartRow = { start: string; total: number } & Record<
  string,
  number | string
>;

export function histogramSeries(
  histogram: api.FailedHistogram | undefined,
  split: "status" | "endpoint",
): Series[] {
  if (split === "status") {
    return FAILED_STATUSES.map((s) => ({
      key: s,
      label: s,
      color: STATUS_COLORS[s],
    }));
  }

  const endpoints = (histogram?.totals?.byEndpoint ?? []).map(
    (e) => e.endpointId ?? "",
  );
  const shown = endpoints.slice(0, MAX_ENDPOINT_SERIES).map((id, i) => ({
    key: `ep:${id}`,
    label: id,
    color: ENDPOINT_PALETTE[i % ENDPOINT_PALETTE.length],
  }));
  return endpoints.length > MAX_ENDPOINT_SERIES
    ? [...shown, { key: `ep:${OTHER}`, label: OTHER, color: INK3 }]
    : shown;
}

export function histogramRows(
  histogram: api.FailedHistogram | undefined,
  split: "status" | "endpoint",
): ChartRow[] {
  const shownEndpoints = new Set(
    (histogram?.totals?.byEndpoint ?? [])
      .slice(0, MAX_ENDPOINT_SERIES)
      .map((e) => e.endpointId ?? ""),
  );
  return (histogram?.buckets ?? []).map((b) => {
    const row: ChartRow = {
      start: b.start?.toISOString() ?? "",
      total: (b.failed ?? 0) + (b.deadLettered ?? 0) + (b.unsupported ?? 0),
    };
    if (split === "status") {
      row.Failed = b.failed ?? 0;
      row.DeadLettered = b.deadLettered ?? 0;
      row.Unsupported = b.unsupported ?? 0;
    } else {
      for (const [endpointId, count] of Object.entries(b.byEndpoint ?? {})) {
        const key = `ep:${shownEndpoints.has(endpointId) ? endpointId : OTHER}`;
        row[key] = ((row[key] as number | undefined) ?? 0) + count;
      }
    }
    return row;
  });
}


/**
 * Stacked bar chart of unresolved failures per time bucket, split by status or endpoint, with a
 * range brush under it (Application Insights style). The chart always shows the whole range;
 * the brush and a clicked bar only set the list window, and bars outside it are dimmed.
 */
export default function FailedHistogram({
  histogram,
  split,
  window,
  onWindowChange,
  isLoading,
}: FailedHistogramProps) {
  const series = React.useMemo(
    () => histogramSeries(histogram, split),
    [histogram, split],
  );
  const rows = React.useMemo(
    () => histogramRows(histogram, split),
    [histogram, split],
  );
  const starts = React.useMemo(
    () => rows.map((r) => new Date(r.start).getTime()),
    [rows],
  );
  const bucketMinutes = histogram?.bucketMinutes ?? 60;
  const windowEnd =
    histogram?.to?.valueOf() ??
    (starts[starts.length - 1] ?? 0) + bucketMinutes * 60_000;
  const endOf = (index: number) => starts[index + 1] ?? windowEnd;
  const from = window?.from.getTime();
  const to = window?.to.getTime();
  const inWindow = (start: number) =>
    from === undefined || to === undefined || (start >= from && start < to);
  const { startIndex, endIndex } = brushIndexes(starts, window);

  const settle = React.useRef<ReturnType<typeof setTimeout>>(undefined);
  React.useEffect(() => () => clearTimeout(settle.current), []);
  const onBrush = (range: { startIndex?: number; endIndex?: number }) => {
    clearTimeout(settle.current);
    const { startIndex: s, endIndex: e } = range;
    if (s === undefined || e === undefined) return;
    if (s === startIndex && e === endIndex) return;
    settle.current = setTimeout(
      () => onWindowChange(windowOfIndexes(starts, windowEnd, s, e)),
      BRUSH_SETTLE_MS,
    );
  };

  if (!histogram && isLoading) {
    return (
      <div
        className="h-[244px] animate-pulse rounded-md bg-muted"
        aria-label="Loading chart"
      />
    );
  }

  return (
    // Recharts makes its surface focusable; clicking a bar would otherwise ring the whole
    // chart. Keyboard focus keeps its outline.
    <div className="[&_*:focus:not(:focus-visible)]:outline-hidden">
      {split === "endpoint" && (
        <div
          className="mb-2 flex flex-wrap items-center gap-4"
          aria-label="Legend"
        >
          {series.map((s) => (
            <span
              key={s.key}
              className="inline-flex items-center gap-1.5 font-mono text-[11.5px] text-muted-foreground"
            >
              <span
                className="inline-block h-2.5 w-2.5 rounded-xs"
                style={{ background: s.color }}
              />
              {s.label}
            </span>
          ))}
        </div>
      )}
      <ResponsiveContainer width="100%" height={200}>
        <BarChart
          data={rows}
          margin={{ top: 8, right: RIGHT_MARGIN, bottom: 0, left: 0 }}
          barCategoryGap="18%"
        >
          <CartesianGrid stroke={GRID} strokeDasharray="3 3" vertical={false} />
          <XAxis
            dataKey="start"
            tickFormatter={(start: string) =>
              formatBucket(new Date(start), bucketMinutes)
            }
            tick={{ fontSize: 10.5, fill: INK3 }}
            stroke={GRID}
            tickLine={false}
            minTickGap={24}
          />
          <YAxis
            allowDecimals={false}
            tick={{ fontSize: 10.5, fill: INK3 }}
            stroke={GRID}
            tickLine={false}
            width={Y_AXIS_WIDTH}
          />
          <Tooltip
            cursor={{ fill: "rgba(232, 116, 60, 0.08)" }}
            content={({ active, payload }) => {
              const row = payload?.[0]?.payload as ChartRow | undefined;
              if (!active || !row) return null;
              return (
                <div className="min-w-[180px] rounded-md border border-border bg-popover px-3 py-2 text-[11.5px] text-popover-foreground shadow-lg">
                  <div className="mb-1 font-mono text-muted-foreground">
                    {formatBucket(
                      new Date(row.start),
                      bucketMinutes,
                      new Date(endOf(starts.indexOf(new Date(row.start).getTime()))),
                    )}
                  </div>
                  {series.map((s) => (
                    <div
                      key={s.key}
                      className="flex items-center justify-between gap-4"
                    >
                      <span className="inline-flex items-center gap-1.5">
                        <span
                          className="inline-block h-2 w-2 rounded-xs"
                          style={{ background: s.color }}
                        />
                        {s.label}
                      </span>
                      <span className="font-mono font-semibold">
                        {(row[s.key] as number | undefined) ?? 0}
                      </span>
                    </div>
                  ))}
                  <div className="mt-1 border-t border-border pt-1 text-muted-foreground">
                    {row.total > 0
                      ? "Click to show this window"
                      : "No failures"}
                  </div>
                </div>
              );
            }}
          />
          {series.map((s, i) => (
            <Bar
              key={s.key}
              dataKey={s.key}
              stackId="failures"
              fill={s.color}
              isAnimationActive={false}
              cursor="pointer"
              radius={i === series.length - 1 ? [3, 3, 0, 0] : undefined}
              onClick={(_data: unknown, index: number) => {
                const start = starts[index];
                if (start === undefined) return;
                const end = endOf(index);
                onWindowChange(
                  from === start && to === end
                    ? undefined
                    : { from: new Date(start), to: new Date(end) },
                );
              }}
            >
              {rows.map((row, index) => (
                <Cell
                  key={row.start}
                  fillOpacity={inWindow(starts[index]) ? 1 : 0.3}
                />
              ))}
            </Bar>
          ))}
        </BarChart>
      </ResponsiveContainer>
      {rows.length > 1 && (
        <ResponsiveContainer width="100%" height={44}>
          {/* Remounted when the applied window changes, so the brush follows a clicked bar,
              a reset or a shared link; its drag state is its own. */}
          <BarChart
            key={`${startIndex}-${endIndex}-${rows.length}`}
            data={rows}
            margin={{ top: 0, right: RIGHT_MARGIN, bottom: 0, left: Y_AXIS_WIDTH }}
          >
            <Brush
              dataKey="start"
              height={40}
              travellerWidth={8}
              stroke="#E8743C"
              fill="rgba(232, 116, 60, 0.06)"
              startIndex={startIndex}
              endIndex={endIndex}
              onChange={onBrush}
              tickFormatter={(start: string) =>
                formatBucket(new Date(start), bucketMinutes)
              }
              ariaLabel="Time window"
            >
              <BarChart data={rows}>
                <Bar
                  dataKey="total"
                  fill={INK3}
                  fillOpacity={0.45}
                  isAnimationActive={false}
                />
              </BarChart>
            </Brush>
          </BarChart>
        </ResponsiveContainer>
      )}
    </div>
  );
}
