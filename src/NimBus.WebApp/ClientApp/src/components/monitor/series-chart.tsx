import { cn } from "lib/utils";

/** One bucket of a chart: a value per series key, null where there is no value. */
export interface ChartBucket {
  timestamp: string;
  values: Record<string, number | null | undefined>;
}

export interface ChartSeries {
  key: string;
  label: string;
  /** Tailwind stroke class for lines, written out literally so Tailwind generates it. */
  stroke: string;
  /** Tailwind fill class for bars, dots and legend swatches. */
  fill: string;
}

interface SeriesChartProps {
  /** What the chart shows, for screen readers. */
  ariaLabel: string;
  buckets: ChartBucket[];
  lines: ChartSeries[];
  bar?: ChartSeries;
  formatY: (value: number) => string;
  formatX: (timestamp: string) => string;
  /** Draw the last bucket's segment dashed: it is the current, partial bucket. */
  partialLast?: boolean;
  /** Height of the plot in em. */
  heightEm?: number;
  /** How many x labels to show. */
  xTicks?: number;
}

const W = 600;
const H = 200;

/**
 * A plain SVG line chart with optional bars, sized in em like the rest of the wall. Gaps (null
 * values) break the line; a lone point between gaps is drawn as a dot.
 */
export function SeriesChart({
  ariaLabel,
  buckets,
  lines,
  bar,
  formatY,
  formatX,
  partialLast = false,
  heightEm = 11,
  xTicks = 6,
}: SeriesChartProps) {
  const n = buckets.length;
  const all = buckets.flatMap((b) =>
    [...lines, ...(bar ? [bar] : [])].map((s) => b.values[s.key] ?? 0),
  );
  const max = niceMax(Math.max(0, ...all));
  const x = (i: number) => ((i + 0.5) / Math.max(n, 1)) * W;
  const y = (v: number) => H - (Math.min(v, max) / max) * H;
  const step = W / Math.max(n, 1);
  const tickIndexes = ticks(n, xTicks);

  return (
    <div className="flex gap-[0.6em] min-w-0">
      <div
        className="flex flex-col justify-between w-[4.6em] shrink-0 text-right whitespace-nowrap font-mono text-[0.75em] text-muted-foreground"
        style={{ height: `${heightEm / 0.75}em` }}
        aria-hidden="true"
      >
        <span className="leading-none -translate-y-1/2">{formatY(max)}</span>
        <span className="leading-none">{formatY(max / 2)}</span>
        <span className="leading-none translate-y-1/2">{formatY(0)}</span>
      </div>
      <div className="flex-1 min-w-0 flex flex-col gap-[0.4em]">
        <svg
          viewBox={`0 0 ${W} ${H}`}
          preserveAspectRatio="none"
          role="img"
          aria-label={ariaLabel}
          className="block w-full overflow-visible"
          style={{ height: `${heightEm}em` }}
        >
          {[0, H / 2, H].map((gy) => (
            <line
              key={gy}
              x1={0}
              x2={W}
              y1={gy}
              y2={gy}
              className={gy === H ? "stroke-border-strong" : "stroke-border"}
              strokeDasharray={gy === H ? undefined : "3 4"}
              vectorEffect="non-scaling-stroke"
            />
          ))}
          {bar &&
            buckets.map((b, i) => {
              const v = b.values[bar.key] ?? 0;
              if (v <= 0) return null;
              const top = y(v);
              return (
                <rect
                  key={`${bar.key}-${b.timestamp}`}
                  x={i * step + step * 0.18}
                  y={top}
                  width={step * 0.64}
                  height={H - top}
                  className={cn(bar.fill, partialLast && i === n - 1 && "opacity-50")}
                />
              );
            })}
          {lines.map((series) =>
            segments(buckets, series.key, partialLast).map((segment, index) =>
              segment.points.length === 1 ? (
                <circle
                  key={`${series.key}-${index}`}
                  cx={x(segment.points[0][0])}
                  cy={y(segment.points[0][1])}
                  r={3}
                  className={series.fill}
                />
              ) : (
                <polyline
                  key={`${series.key}-${index}`}
                  points={segment.points.map(([i, v]) => `${x(i).toFixed(1)},${y(v).toFixed(1)}`).join(" ")}
                  fill="none"
                  strokeWidth={2}
                  strokeLinejoin="round"
                  strokeDasharray={segment.dashed ? "4 4" : undefined}
                  className={series.stroke}
                  vectorEffect="non-scaling-stroke"
                />
              ),
            ),
          )}
        </svg>
        <div className="relative h-[1.1em] font-mono text-[0.75em] text-muted-foreground" aria-hidden="true">
          {tickIndexes.map((i) => (
            <span
              key={i}
              className="absolute -translate-x-1/2 whitespace-nowrap"
              style={{ left: `${(x(i) / W) * 100}%` }}
            >
              {i === n - 1 && partialLast ? "now" : formatX(buckets[i].timestamp)}
            </span>
          ))}
        </div>
      </div>
    </div>
  );
}

/** Legend entries for a chart header. */
export function ChartLegend({ lines, bar }: { lines: ChartSeries[]; bar?: ChartSeries }) {
  return (
    <div className="flex flex-wrap gap-x-[1.2em] gap-y-[0.3em] text-[0.85em] text-muted-foreground">
      {lines.map((s) => (
        <span key={s.key} className="inline-flex items-center gap-[0.45em]">
          <svg width="16" height="4" aria-hidden="true">
            <line x1="0" x2="16" y1="2" y2="2" strokeWidth="2" className={s.stroke} />
          </svg>
          {s.label}
        </span>
      ))}
      {bar && (
        <span className="inline-flex items-center gap-[0.45em]">
          <svg width="10" height="10" aria-hidden="true">
            <rect width="10" height="10" rx="2" className={bar.fill} />
          </svg>
          {bar.label}
        </span>
      )}
    </div>
  );
}

interface Segment {
  points: [number, number][];
  dashed: boolean;
}

/** Contiguous runs of values; the step into a partial last bucket is its own dashed run. */
function segments(buckets: ChartBucket[], key: string, partialLast: boolean): Segment[] {
  const result: Segment[] = [];
  let run: [number, number][] = [];
  const lastSolid = partialLast ? buckets.length - 2 : buckets.length - 1;
  for (let i = 0; i <= lastSolid; i++) {
    const v = buckets[i]?.values[key];
    if (v === null || v === undefined) {
      if (run.length) result.push({ points: run, dashed: false });
      run = [];
    } else {
      run.push([i, v]);
    }
  }
  if (run.length) result.push({ points: run, dashed: false });

  if (partialLast && buckets.length >= 2) {
    const a = buckets[buckets.length - 2].values[key];
    const b = buckets[buckets.length - 1].values[key];
    if (a !== null && a !== undefined && b !== null && b !== undefined) {
      result.push({ points: [[buckets.length - 2, a], [buckets.length - 1, b]], dashed: true });
    }
  }
  return result;
}

/** A round upper bound for the axis: 1, 2 or 5 times a power of ten. */
export function niceMax(value: number): number {
  if (value <= 0) return 1;
  const power = 10 ** Math.floor(Math.log10(value));
  for (const m of [1, 2, 5, 10]) {
    if (value <= m * power) return m * power;
  }
  return 10 * power;
}

function ticks(n: number, count: number): number[] {
  if (n <= 0) return [];
  if (n <= count) return Array.from({ length: n }, (_, i) => i);
  const result: number[] = [];
  for (let k = 0; k < count; k++) {
    result.push(Math.round((k * (n - 1)) / (count - 1)));
  }
  return [...new Set(result)];
}
