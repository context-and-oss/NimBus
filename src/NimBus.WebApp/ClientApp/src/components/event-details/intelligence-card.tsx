import { useEffect, useRef, useState } from "react";
import { Badge } from "components/ui/badge";
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
  const guidanceMeaning = classification && isFailureGuidance(classification.guidance) ? GUIDANCE_MEANING[classification.guidance] : undefined;
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
        <CardTitle className="text-base">Failure intelligence{props.occurrenceLabel ? ` · ${props.occurrenceLabel}` : ""}</CardTitle>
        <CardDescription>Advisory classification of this failure occurrence. It never retries or changes the message.</CardDescription>
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        {error && <p role="status" className="text-status-danger">{error}</p>}
        {status.status === "ReadOnly" &&
          <p className="text-muted-foreground">Agents can't record new classifications in this deployment; saved results are shown.</p>}
        {status.status === "Recordable" &&
          <p className="text-muted-foreground">Agents record classifications over MCP.</p>}
        {classification && <>
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <div><div className="text-muted-foreground">Category</div><strong>{classification.category} ({percent(classification.categoryConfidence)})</strong></div>
            <div><div className="text-muted-foreground">Guidance</div>{isFailureGuidance(classification.guidance)
              ? <Badge variant={GUIDANCE_BADGE[classification.guidance]}>{classification.guidance}</Badge> : <strong>{classification.guidance}</strong>}</div>
            <div><div className="text-muted-foreground">Retry likely</div><strong>{percent(classification.retryLikelihood)}</strong></div>
            <div><div className="text-muted-foreground">Change required</div><strong>{percent(classification.changeRequiredLikelihood)}</strong></div>
            <div><div className="text-muted-foreground">External dependency</div><strong>{percent(classification.externalDependencyLikelihood)}</strong></div>
            <div><div className="text-muted-foreground">Fault origin</div><strong>{faultOriginLabel(classification.faultOrigin)}
              {classification.faultOrigin && classification.faultOriginConfidence != null ? ` (${percent(classification.faultOriginConfidence)})` : ""}</strong></div>
          </div>
          {(guidanceMeaning || categoryMeaning) && <div className="rounded-md bg-muted p-3">
            {guidanceMeaning && <p>{guidanceMeaning}</p>}
            {categoryMeaning && <p className="text-muted-foreground"><span className="font-mono">{classification.category}</span>: {categoryMeaning}</p>}
          </div>}
          {byAgent
            ? <p className="text-muted-foreground">Recorded by an agent · Revision {classification.revision} · Model {classification.model} (as declared)
              {classification.clientId ? ` · Client ${classification.clientId}` : ""} · {classification.createdAtUtc} · {classification.requestedBy}</p>
            : <p className="text-muted-foreground">Revision {classification.revision} · {classification.provider ? `${classification.provider} · ` : ""}{classification.model} · {classification.createdAtUtc} · {classification.requestedBy}</p>}
          {byAgent && classification.rationale && <div>
            <div className="text-muted-foreground">Agent rationale (unverified)</div>
            <p className="whitespace-pre-wrap break-words">{classification.rationale}</p>
          </div>}
          <details>
            <summary className="cursor-pointer">Classification details</summary>
            <div className="mt-2 space-y-1 text-muted-foreground">
              {probabilities.map(([category, probability]) => <div key={category}>{category}: {percent(probability)}</div>)}
              <div>Question set v{classification.questionSetVersion ?? "unknown"}</div>
              {/* No saved result records the guidance thresholds it was made with (spec 043 §7). */}
              <div>Threshold settings not recorded</div>
            </div>
          </details>
        </>}
        <p className="text-muted-foreground">{byAgent ? "Assessment recorded by an agent; NimBus composed the guidance." : "AI-assisted assessment by the built-in provider (before NimBus 5.0)."} Advisory only. NimBus processing and recovery decisions are unchanged.</p>
      </CardContent>
    </Card>
    </div>
  );
}
