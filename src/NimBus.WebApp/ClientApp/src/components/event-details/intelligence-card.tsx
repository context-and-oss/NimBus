import { useEffect, useRef, useState } from "react";
import moment from "moment";
import { Badge } from "components/ui/badge";
import { causeLabel, guidanceLabel } from "components/insights/insights-labels";
import { formatMoment } from "functions/endpoint.functions";
import {
  AGENT_PROVIDER, FAILURE_CATEGORIES, GUIDANCE_BADGE, GUIDANCE_MEANING, faultOriginLabel, isFailureGuidance,
} from "lib/failure-classification";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "components/ui/card";

type IntelligenceCardProps = {
  endpointId: string; eventId: string; messageId: string; resolutionStatus?: string; occurrenceLabel?: string;
  /** The occurrence a link (for example from Insights) asked for: outlined and scrolled into view once. */
  highlighted?: boolean;
};
/** Recordable: agents record over MCP. ReadOnly: saved results only. */
type IntelligenceStatus = { status: string; canRecord?: boolean; contractVersion: number };
type Classification = {
  category: string; categoryConfidence: number; retryLikelihood: number; changeRequiredLikelihood: number;
  externalDependencyLikelihood: number; guidance: string; revision: number; provider?: string; model: string; createdAtUtc: string; requestedBy?: string;
  categoryProbabilities?: Record<string, number>; questionSetVersion?: number;
  /** Spec 044: an agent's answers and provenance. Null on version 1 results (the built-in provider, before NimBus 5.0). */
  faultOrigin?: string | null; faultOriginConfidence?: number | null; clientId?: string | null; rationale?: string | null;
};
const headers = { "api-version": "2" };
const percent = (value: number) => `${Math.round(value * 100)}%`;
/** A rationale longer than this starts clamped to two lines. */
const LONG_RATIONALE = 200;
/** The saved UTC time in the reader's local time; the stored text when it can't be parsed. */
const recordedAt = (utc: string) => {
  const at = moment(utc);
  return at.isValid() ? formatMoment(at, true) : utc;
};

/** One likelihood as a short bar and a percentage. */
function Signal({ label, value }: { label: string; value: number }) {
  const now = Math.round(value * 100);
  return (
    <div>
      <div className="text-muted-foreground">{label}</div>
      <div className="flex items-center gap-2">
        <div role="meter" aria-label={label} aria-valuemin={0} aria-valuemax={100} aria-valuenow={now}
          className="h-1.5 w-20 overflow-hidden rounded-full bg-muted">
          <div className="h-full rounded-full bg-foreground/60" style={{ width: `${now}%` }} />
        </div>
        <strong>{now}%</strong>
      </div>
    </div>
  );
}

/** The agent's own explanation: untrusted plain text, clamped to two lines when long. */
function Rationale({ text }: { text: string }) {
  const [expanded, setExpanded] = useState(false);
  const long = text.length > LONG_RATIONALE || text.split("\n").length > 2;
  return (
    <div>
      <div className="text-muted-foreground">Agent's reasoning (unverified)</div>
      <p className={`whitespace-pre-wrap break-words ${long && !expanded ? "line-clamp-2" : ""}`}>{text}</p>
      {long && <button type="button" aria-expanded={expanded} onClick={() => setExpanded(value => !value)}
        className="mt-1 text-primary underline-offset-2 hover:underline">{expanded ? "Show less" : "Show more"}</button>}
    </div>
  );
}

export default function IntelligenceCard(props: IntelligenceCardProps) {
  // A distinct component instance isolates in-flight responses when navigating between occurrences.
  return <OccurrenceCard key={`${props.endpointId}/${props.eventId}/${props.messageId}`} {...props} />;
}

function OccurrenceCard(props: IntelligenceCardProps) {
  const [status, setStatus] = useState<IntelligenceStatus>();
  const [classification, setClassification] = useState<Classification>();
  const [error, setError] = useState<string>();
  const [pollVersion, setPollVersion] = useState(0);
  const pollAttempt = useRef(0);
  const scrolled = useRef(false);
  const eligible = ["failed", "deadlettered"].includes((props.resolutionStatus ?? "").toLowerCase());
  const baseUrl = `/api/v1/integration-intelligence/failures/${encodeURIComponent(props.eventId)}/${encodeURIComponent(props.messageId)}/classification`;

  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const load = async () => {
      try {
        const response = await fetch(`/api/v1/integration-intelligence/status?endpointId=${encodeURIComponent(props.endpointId)}`, { headers, signal: controller.signal });
        if (!response.ok) return;
        const capability = await response.json() as IntelligenceStatus;
        if (controller.signal.aborted) return;
        // Agents record classifications over MCP (Spec 044); the card shows what they saved and
        // never starts one. Read-only mode (spec 043): saved results stay readable, so the card
        // appears only when one exists.
        const readOnly = capability.status === "ReadOnly";
        if ((capability.status !== "Recordable" && !readOnly) || capability.contractVersion !== 1) return;
        if (!readOnly) setStatus(capability);
        const latest = await fetch(baseUrl, { headers, signal: controller.signal });
        if (latest.status === 404) return;
        if (readOnly && !latest.ok) return;
        const payload = await latest.json();
        if (controller.signal.aborted) return;
        if (readOnly) setStatus(capability);
        if (latest.ok) { setClassification(payload); setError(undefined); }
        else if (payload.code === "AnalysisInProgress") {
          setError("A classification is being stored.");
          if (pollAttempt.current++ < 20)
            timer = setTimeout(() => setPollVersion(value => value + 1), Math.min(1000 * 2 ** pollAttempt.current, 5000));
          else setError("A classification is still being stored. Refresh to check again.");
        } else if (payload.code === "AnalysisOutcomeUnknown") {
          setError("The outcome of the latest classification is unknown.");
        } else { setError("Classification is temporarily unavailable."); }
      } catch {
        if (!controller.signal.aborted) setError("Classification is temporarily unavailable.");
      }
    };
    void load();
    return () => { controller.abort(); if (timer !== undefined) clearTimeout(timer); };
  }, [props.endpointId, baseUrl, pollVersion]);

  if (!status || (!eligible && !classification)) return null;
  // Nothing saved, nothing in flight and nothing to report: no card.
  if (!classification && !error) return null;
  const probabilities = Object.entries(classification?.categoryProbabilities ?? {})
    .filter(([category]) => category !== classification?.category)
    .sort(([, left], [, right]) => right - left)
    .slice(0, 2);
  const guidance = classification?.guidance;
  const guidanceMeaning = guidance && isFailureGuidance(guidance) ? GUIDANCE_MEANING[guidance] : undefined;
  const categoryMeaning = FAILURE_CATEGORIES.find(item => item.id === classification?.category)?.meaning;
  const byAgent = classification?.provider === AGENT_PROVIDER;
  const scrollOnce = (node: HTMLDivElement | null) => {
    if (!node || !props.highlighted || scrolled.current) return;
    scrolled.current = true;
    node.scrollIntoView?.({ block: "center" });
  };
  return (
    <div ref={scrollOnce} aria-current={props.highlighted ? "true" : undefined}>
    <Card className={`mb-4 ${props.highlighted ? "border-primary ring-2 ring-primary/40" : "border-primary/30"}`}>
      <CardHeader>
        <div className="flex flex-wrap items-start justify-between gap-2">
          <CardTitle className="text-base">Failure intelligence{props.occurrenceLabel ? ` · ${props.occurrenceLabel}` : ""}</CardTitle>
          {guidance && <Badge variant={isFailureGuidance(guidance) ? GUIDANCE_BADGE[guidance] : "secondary"}>{guidanceLabel(guidance)}</Badge>}
        </div>
        <CardDescription>Advisory only. It never retries or changes the message.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-4 text-sm">
        {error && <p role="status" className="text-status-danger">{error}</p>}
        {status.status === "ReadOnly" &&
          <p className="text-muted-foreground">Agents can't record new classifications in this deployment; saved results are shown.</p>}
        {classification && <>
          {/* What to do comes first; the numbers behind it follow. */}
          {guidanceMeaning && <p className="text-base">{guidanceMeaning}</p>}
          <div className="grid gap-4 sm:grid-cols-2 lg:grid-cols-[2fr_1fr_1fr_1fr]">
            <div>
              <div className="text-muted-foreground">Likely cause</div>
              <strong>{causeLabel(classification.category)}</strong>
              <div className="text-muted-foreground">{percent(classification.categoryConfidence)} confident</div>
              {categoryMeaning && <p className="mt-1 text-muted-foreground">{categoryMeaning}</p>}
            </div>
            <div>
              <div className="text-muted-foreground">Fault origin</div>
              <strong>{faultOriginLabel(classification.faultOrigin)}</strong>
              {classification.faultOrigin && classification.faultOriginConfidence != null &&
                <div className="text-muted-foreground">{percent(classification.faultOriginConfidence)} confident</div>}
            </div>
            <Signal label="Retry likely" value={classification.retryLikelihood} />
            <Signal label="Change required" value={classification.changeRequiredLikelihood} />
          </div>
          {byAgent && classification.rationale && <Rationale text={classification.rationale} />}
          <details>
            <summary className="cursor-pointer text-muted-foreground">Details</summary>
            <div className="mt-2 space-y-1 text-muted-foreground">
              <div>Category: <span className="font-mono">{classification.category}</span></div>
              {probabilities.length > 0 && <div>Other candidates: {probabilities.map(([category, probability], index) =>
                <span key={category}>{index > 0 ? ", " : ""}<span>{causeLabel(category)}: {percent(probability)}</span></span>)}</div>}
              <div>External dependency: {percent(classification.externalDependencyLikelihood)}</div>
              {byAgent
                ? <div>Recorded by an agent · Revision {classification.revision} · Model {classification.model} (as declared)
                  {classification.clientId ? ` · Client ${classification.clientId}` : ""} · {recordedAt(classification.createdAtUtc)}
                  {classification.requestedBy ? ` · ${classification.requestedBy}` : ""}</div>
                : <div>Revision {classification.revision} · {classification.provider ? `${classification.provider} · ` : ""}{classification.model} · {recordedAt(classification.createdAtUtc)}
                  {classification.requestedBy ? ` · ${classification.requestedBy}` : ""}</div>}
              <div>Question set v{classification.questionSetVersion ?? "unknown"}</div>
              {/* No saved result records the guidance thresholds it was made with (spec 043 §7). */}
              <div>Threshold settings not recorded</div>
              {status.status === "Recordable" && <div>Agents record classifications over MCP.</div>}
            </div>
          </details>
        </>}
        <p className="text-xs text-muted-foreground">{byAgent ? "Assessment recorded by an agent; NimBus composed the guidance." : "AI-assisted assessment by the built-in provider (before NimBus 5.0)."}</p>
      </CardContent>
    </Card>
    </div>
  );
}
