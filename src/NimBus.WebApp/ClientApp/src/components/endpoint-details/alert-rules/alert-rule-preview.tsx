import { useEffect, useRef, useState } from "react";
import moment from "moment";
import * as api from "api-client";
import { errorMessage } from "api-client/problem";
import { Badge, Button } from "components/ui";
import { SeriesChart, type ChartSeries } from "components/monitor/series-chart";
import { cn } from "lib/utils";
import {
  conditionValueText,
  duration,
  kindOf,
  nowUnavailableText,
  periodText,
  previewRequestFromDraft,
  replayUnsupportedText,
  validateCondition,
  valueLabel,
  type RuleDraft,
} from "./alert-rules-model";

export interface AlertRulePreviewPanelProps {
  endpointId: string;
  draft: RuleDraft;
}

interface Loaded {
  /** The request the preview answers, to tell when the draft has moved on. */
  key: string;
  condition: api.AlertRuleCondition;
  preview: api.AlertRulePreview;
}

const VALUE_SERIES: ChartSeries = {
  key: "value",
  label: "Value",
  stroke: "stroke-status-info",
  fill: "fill-status-info",
};

const STALE_TEXT = "The condition changed since this preview. Refresh to see the new one.";

/** The axis label of a replayed value: a count, or minutes silent. */
const axisFormatter = (silence: boolean) => (value: number) => {
  if (!silence) return Number.isInteger(value) ? String(value) : value.toFixed(1);
  return value < 120 ? `${Math.round(value)} min` : `${Number((value / 60).toFixed(1))} h`;
};

function failureText(error: unknown): string {
  if (api.SwaggerException.isSwaggerException(error) && error.status === 429)
    return "Too many previews; try again in a minute.";
  return errorMessage(error) || "The preview could not be loaded.";
}

/**
 * The Preview part of the rule form (Spec 053 Phase 3): reads the draft's condition now and, where
 * message history allows, replays the last 24 hours. It runs only when asked, because previews are
 * rate-limited, and marks its answer stale once the condition or resolve time changes.
 */
export default function AlertRulePreviewPanel({ endpointId, draft }: AlertRulePreviewPanelProps) {
  const [client] = useState(() => new api.Client(api.CookieAuth()));
  const [loaded, setLoaded] = useState<Loaded>();
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string>();
  const [asked, setAsked] = useState(false);
  const latest = useRef(0);

  useEffect(
    () => () => {
      // A response after the form closed has nowhere to go.
      latest.current = -1;
    },
    [],
  );

  const invalid = Object.keys(validateCondition(draft)).length > 0;
  const request = invalid ? undefined : previewRequestFromDraft(draft);
  const key = request ? JSON.stringify(request) : undefined;
  const stale = !!loaded && loaded.key !== key;

  async function run() {
    setAsked(true);
    if (!request || !key) return;
    const seq = ++latest.current;
    setLoading(true);
    setError(undefined);
    try {
      const preview = await client.postEndpointAlertRulePreview(endpointId, request);
      if (seq !== latest.current) return;
      setLoaded({ key, condition: request.condition ?? new api.AlertRuleCondition(), preview });
    } catch (e) {
      if (seq !== latest.current) return;
      setLoaded(undefined);
      setError(failureText(e));
    } finally {
      if (seq === latest.current) setLoading(false);
    }
  }

  return (
    <section
      aria-labelledby="alert-rule-preview-title"
      className="flex flex-col gap-3 rounded-md border border-border px-3 py-2.5"
    >
      <div className="flex items-start justify-between gap-3">
        <div>
          <p id="alert-rule-preview-title" className="m-0 text-sm font-medium text-foreground">
            Preview
          </p>
          <p className="m-0 mt-1 text-xs text-muted-foreground">
            Reads the condition now and, where message history allows, replays the last 24 hours. Nothing is saved or
            sent.
          </p>
        </div>
        <Button type="button" size="sm" variant="outline" disabled={loading} onClick={() => void run()}>
          {loaded ? "Refresh" : "Preview"}
        </Button>
      </div>

      {asked && invalid && (
        <p role="alert" className="m-0 text-xs text-status-error-ink">
          Correct the condition to preview it.
        </p>
      )}
      {loading && (
        <p role="status" className="m-0 text-xs text-muted-foreground">
          Previewing…
        </p>
      )}
      {error && (
        <p
          role="alert"
          className="m-0 rounded-md border border-status-error-200 bg-status-error-50 px-3 py-2 text-sm text-status-error-ink"
        >
          {error}
        </p>
      )}
      {loaded && stale && (
        <p role="status" className="m-0 text-xs text-status-warning-ink">
          {STALE_TEXT}
        </p>
      )}
      {loaded && <PreviewResult loaded={loaded} stale={stale} />}
    </section>
  );
}

function PreviewResult({ loaded, stale }: { loaded: Loaded; stale: boolean }) {
  const { condition, preview } = loaded;
  const kind = kindOf(condition);
  const value = conditionValueText(condition, preview.valueNow);
  const holds = preview.holdsNow;
  const replay = preview.replay;
  const periods = replay?.periods ?? [];
  const silence = kind === "silence";
  const threshold = silence ? condition.minutes : condition.threshold;
  // "Silent for (minutes)" names a column; beside a value with its unit, "Silent for" is enough.
  const label = valueLabel(condition).replace(/ \(minutes\)$/, "");
  const series = silence ? "Minutes since the last completed message" : "Failures in the window";
  const hasThreshold = threshold !== undefined && threshold !== null;
  const thresholdText = hasThreshold ? (silence ? duration(threshold) : String(threshold)) : "";
  const buckets = (replay?.series ?? []).map((point) => ({
    timestamp: point.atUtc?.toISOString() ?? "",
    values: { value: point.value },
  }));

  return (
    <div className={cn("flex flex-col gap-3", stale && "opacity-60")}>
      {holds === true || holds === false ? (
        <p className="m-0 flex flex-wrap items-center gap-2 text-sm">
          <Badge variant={holds ? "error" : "success"}>{holds ? "Would fire now" : "Would not fire now"}</Badge>
          {value !== "–" && (
            <span className="text-muted-foreground">
              {label}: <span className="tabular-nums text-foreground">{value}</span>
            </span>
          )}
        </p>
      ) : (
        <p className="m-0 text-sm text-muted-foreground">{nowUnavailableText(preview.nowUnavailableReason ?? "")}</p>
      )}

      {replay ? (
        <div className="flex flex-col gap-2">
          <p className="m-0 text-xs text-muted-foreground">
            {series}, last 24 hours{hasThreshold ? `. Dashed: the rule's ${thresholdText}.` : "."}
          </p>
          {buckets.length > 0 && (
            <div className="text-xs">
              <SeriesChart
                ariaLabel={`${series} over the last 24 hours${hasThreshold ? `, with the rule's ${thresholdText} marked` : ""}`}
                buckets={buckets}
                lines={[VALUE_SERIES]}
                formatY={axisFormatter(silence)}
                formatX={(t) => moment(t).format("HH:mm")}
                heightEm={8}
                xTicks={5}
                reference={hasThreshold ? { value: threshold, stroke: "stroke-status-danger" } : undefined}
              />
            </div>
          )}
          {periods.length === 0 ? (
            <p className="m-0 text-sm">Would not have fired in the last 24 hours.</p>
          ) : (
            <div className="text-sm">
              <p className="m-0">
                Would have fired {periods.length === 1 ? "once" : `${periods.length} times`}:
              </p>
              <ul
                aria-label="Would have fired"
                className="m-0 mt-1 flex list-none flex-wrap gap-x-4 gap-y-1 p-0 tabular-nums"
              >
                {periods.map((period, i) => (
                  <li key={period.fromUtc?.toISOString() ?? i}>{periodText(period)}</li>
                ))}
              </ul>
            </div>
          )}
        </div>
      ) : (
        preview.replayUnsupportedReason && (
          <p className="m-0 text-sm text-muted-foreground">{replayUnsupportedText(preview.replayUnsupportedReason)}</p>
        )
      )}
    </div>
  );
}
