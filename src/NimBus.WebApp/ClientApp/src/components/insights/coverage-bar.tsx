import * as api from "api-client";
import { percent } from "./insights-labels";

const SEGMENTS: {
  key: keyof Pick<api.InsightsCoverage, "classified" | "uncertain" | "notAnalyzed" | "notEligible">;
  label: string;
  className: string;
}[] = [
  { key: "classified", label: "classified", className: "bg-status-success" },
  { key: "uncertain", label: "uncertain", className: "bg-status-warning" },
  { key: "notAnalyzed", label: "not analyzed", className: "bg-muted-foreground/40" },
  { key: "notEligible", label: "not eligible", className: "bg-muted-foreground/70" },
];

/** Coverage as a segmented bar with its counts spelled out (spec 043 §5). */
export function CoverageBar({ coverage, compact = false }: { coverage: api.InsightsCoverage; compact?: boolean }) {
  const total = coverage.total ?? 0;
  const description = SEGMENTS.map((s) => `${coverage[s.key] ?? 0} ${s.label}`).join(", ");
  return (
    <span className="inline-flex items-center gap-2">
      <span
        role="img"
        aria-label={`Coverage: ${description}`}
        title={description}
        className={`flex overflow-hidden rounded-sm bg-muted ${compact ? "h-1.5 w-20" : "h-2 w-40"}`}
      >
        {total > 0 &&
          SEGMENTS.map((s) => {
            const count = coverage[s.key] ?? 0;
            return count > 0 ? (
              <span key={s.key} className={s.className} style={{ flexGrow: count }} />
            ) : null;
          })}
      </span>
      {!compact && (
        <span className="text-sm text-muted-foreground">
          {(coverage.analyzed ?? 0).toLocaleString()} of {total.toLocaleString()} analyzed ({percent(coverage.analyzed ?? 0, total)})
        </span>
      )}
    </span>
  );
}
