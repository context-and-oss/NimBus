import { useEffect, useRef, useState } from "react";
import { Badge } from "components/ui/badge";
import { Button } from "components/ui/button";
import { FAILURE_CATEGORIES, GUIDANCE_BADGE, GUIDANCE_MEANING, isFailureGuidance } from "lib/failure-classification";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "components/ui/card";

type IntelligenceCardProps = {
  endpointId: string; eventId: string; messageId: string; resolutionStatus?: string; occurrenceLabel?: string;
  /** The occurrence a link (for example from Insights) asked for: outlined and scrolled into view once. */
  highlighted?: boolean;
};
type IntelligenceStatus = { status: string; canAnalyze: boolean; contractVersion: number };
type Classification = {
  category: string; categoryConfidence: number; retryLikelihood: number; changeRequiredLikelihood: number;
  externalDependencyLikelihood: number; guidance: string; revision: number; provider?: string; model: string; createdAtUtc: string; requestedBy?: string;
  categoryProbabilities?: Record<string, number>; questionSetVersion?: number;
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
  const [busy, setBusy] = useState(true);
  const [error, setError] = useState<string>();
  const [unknownOutcome, setUnknownOutcome] = useState(false);
  const [analysisActive, setAnalysisActive] = useState(false);
  const [pollVersion, setPollVersion] = useState(0);
  const pending = useRef<{ key: string; force: boolean } | undefined>(undefined);
  const pollAttempt = useRef(0);
  const alive = useRef(true);
  const scrolled = useRef(false);
  const eligible = ["failed", "deadlettered"].includes((props.resolutionStatus ?? "").toLowerCase());
  const baseUrl = `/api/v1/integration-intelligence/failures/${encodeURIComponent(props.eventId)}/${encodeURIComponent(props.messageId)}/classification`;

  useEffect(() => { alive.current = true; return () => { alive.current = false; }; }, []);
  useEffect(() => {
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    const load = async () => {
      try {
        const response = await fetch(`/api/v1/integration-intelligence/status?endpointId=${encodeURIComponent(props.endpointId)}`, { headers, signal: controller.signal });
        if (!response.ok) return;
        const capability = await response.json() as IntelligenceStatus;
        if (controller.signal.aborted) return;
        // Spec 043 read-only mode: with no provider configured, saved results stay readable, so the
        // card appears only when one exists and never offers analysis.
        const readOnly = capability.status === "ProviderNotConfigured";
        if ((capability.status !== "Ready" && !readOnly) || capability.contractVersion !== 1) return;
        if (!readOnly) setStatus(capability);
        const latest = await fetch(baseUrl, { headers, signal: controller.signal });
        if (latest.status === 404) return;
        if (readOnly && !latest.ok) return;
        const payload = await latest.json();
        if (controller.signal.aborted) return;
        if (readOnly) setStatus({ ...capability, canAnalyze: false });
        if (latest.ok) { setClassification(payload); setError(undefined); setUnknownOutcome(false); setAnalysisActive(false); }
        else if (payload.code === "AnalysisInProgress") {
          setBusy(true);
          setAnalysisActive(true);
          setError("Analysis is in progress.");
          if (pollAttempt.current++ < 20)
            timer = setTimeout(() => setPollVersion(value => value + 1), Math.min(1000 * 2 ** pollAttempt.current, 5000));
          else setError("Analysis is still in progress. Refresh to check again.");
        } else if (payload.code === "AnalysisOutcomeUnknown") {
          setUnknownOutcome(true);
          setAnalysisActive(false);
          setError("The previous analysis outcome is unknown.");
        } else { setError("Classification is temporarily unavailable."); }
      } catch {
        if (!controller.signal.aborted) setError("Classification is temporarily unavailable.");
      } finally {
        if (!controller.signal.aborted && timer === undefined) setBusy(false);
      }
    };
    void load();
    return () => { controller.abort(); if (timer !== undefined) clearTimeout(timer); };
  }, [props.endpointId, baseUrl, pollVersion]);

  const analyze = async () => {
    if (busy) return;
    setBusy(true);
    setError(undefined);
    setAnalysisActive(false);
    // Transport retries reuse the same identity; explicit re-analysis gets a new one.
    pending.current ??= { key: crypto.randomUUID(), force: Boolean(classification) || unknownOutcome };
    const request = pending.current;
    try {
      const response = await fetch(baseUrl, {
        method: "POST", headers: { ...headers, "Content-Type": "application/json", "Idempotency-Key": request.key },
        body: JSON.stringify({ force: request.force }),
      });
      const payload = await response.json();
      if (!alive.current) return;
      pending.current = undefined;
      if (response.ok) { setClassification(payload.result); setUnknownOutcome(false); setAnalysisActive(false); }
      else if (payload.code === "AnalysisOutcomeUnknown") {
        setUnknownOutcome(true);
        setAnalysisActive(false);
        setError("The previous analysis outcome is unknown.");
      } else if (payload.code === "AnalysisInProgress") {
        setAnalysisActive(true);
        pollAttempt.current = 0;
        setPollVersion(value => value + 1);
        setError("Analysis is in progress.");
      } else { setError("Classification is temporarily unavailable."); }
    } catch {
      if (alive.current) { setError("Connection interrupted. Retry safely with the same request."); setAnalysisActive(false); }
    } finally { if (alive.current) setBusy(false); }
  };

  if (!status || (!eligible && !classification)) return null;
  const probabilities = Object.entries(classification?.categoryProbabilities ?? {})
    .filter(([category]) => category !== classification?.category)
    .sort(([, left], [, right]) => right - left)
    .slice(0, 2);
  const historical = Boolean(classification && (analysisActive || unknownOutcome));
  const guidanceMeaning = classification && isFailureGuidance(classification.guidance) ? GUIDANCE_MEANING[classification.guidance] : undefined;
  const categoryMeaning = FAILURE_CATEGORIES.find(item => item.id === classification?.category)?.meaning;
  const scrollOnce = (node: HTMLDivElement | null) => {
    if (!node || !props.highlighted || scrolled.current) return;
    scrolled.current = true;
    node.scrollIntoView?.({ block: "center" });
  };
  return (
    <div ref={scrollOnce} aria-current={props.highlighted ? "true" : undefined}>
    <Card className={`mb-4 ${props.highlighted ? "border-primary ring-2 ring-primary/40" : "border-primary/30"}`}>
      <CardHeader className="flex-row items-center justify-between gap-4 space-y-0">
        <div>
          <CardTitle className="text-base">Failure intelligence{props.occurrenceLabel ? ` · ${props.occurrenceLabel}` : ""}</CardTitle>
          <CardDescription>Advisory analysis of this failure occurrence. It never retries or changes the message.</CardDescription>
        </div>
        {status.canAnalyze && eligible && <Button size="sm" onClick={analyze} isLoading={busy} disabled={busy}>
          {classification || unknownOutcome ? "Re-analyze" : "Analyze failure"}
        </Button>}
      </CardHeader>
      <CardContent className="space-y-3 text-sm">
        {error && <p role="status" className="text-status-danger">{error}</p>}
        {status.status === "ProviderNotConfigured" &&
          <p className="text-muted-foreground">New analysis is unavailable because no provider is configured.</p>}
        {(unknownOutcome || classification) && status.canAnalyze && eligible &&
          <p>Re-analyzing makes a new provider request and may incur an additional charge.</p>}
        {analysisActive && <p role="status">Analyzing failure…</p>}
        {!classification && !analysisActive && !unknownOutcome && !error &&
          <p>No analysis has been performed.</p>}
        {classification && <>
          {historical && <p role="status" className="text-muted-foreground">Historical result{analysisActive ? "; a newer analysis is running." : "; the latest analysis outcome is unknown."}</p>}
          <div className="grid gap-3 sm:grid-cols-2 lg:grid-cols-4">
            <div><div className="text-muted-foreground">Category</div><strong>{classification.category} ({percent(classification.categoryConfidence)})</strong></div>
            <div><div className="text-muted-foreground">Guidance</div>{isFailureGuidance(classification.guidance)
              ? <Badge variant={GUIDANCE_BADGE[classification.guidance]}>{classification.guidance}</Badge> : <strong>{classification.guidance}</strong>}</div>
            <div><div className="text-muted-foreground">Retry likely</div><strong>{percent(classification.retryLikelihood)}</strong></div>
            <div><div className="text-muted-foreground">Change required</div><strong>{percent(classification.changeRequiredLikelihood)}</strong></div>
            <div><div className="text-muted-foreground">External dependency</div><strong>{percent(classification.externalDependencyLikelihood)}</strong></div>
          </div>
          {(guidanceMeaning || categoryMeaning) && <div className="rounded-md bg-muted p-3">
            {guidanceMeaning && <p>{guidanceMeaning}</p>}
            {categoryMeaning && <p className="text-muted-foreground"><span className="font-mono">{classification.category}</span>: {categoryMeaning}</p>}
          </div>}
          <p className="text-muted-foreground">Revision {classification.revision} · {classification.provider ? `${classification.provider} · ` : ""}{classification.model} · {classification.createdAtUtc} · {classification.requestedBy}</p>
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
        <p className="text-muted-foreground">AI-assisted assessment. Advisory only. NimBus processing and recovery decisions are unchanged.</p>
      </CardContent>
    </Card>
    </div>
  );
}
