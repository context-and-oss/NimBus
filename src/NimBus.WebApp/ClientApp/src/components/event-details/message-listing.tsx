import { getResubmitPayload } from "functions/resubmit.functions";
export { getResubmitPayload } from "functions/resubmit.functions";
export { getRoutingFromTo } from "functions/event-details.functions";
import * as React from "react";
import * as api from "api-client";
import IntelligenceCard from "components/event-details/intelligence-card";
import { Badge } from "components/ui/badge";
import { Button } from "components/ui/button";
import {
  Modal,
  ModalHeader,
  ModalBody,
  ModalFooter,
} from "components/ui/modal";
import { Textarea } from "components/ui/textarea";
import { CodeBlock } from "components/ui/code-block";
import { CopyButton } from "components/common/copy-button";
import { TimingBar } from "components/ui/timing-bar";
import {
  PropertyList,
  PropertySection,
  PropertyRow,
} from "components/ui/property-list";
import HandoffHero, {
  type HandoffState,
} from "components/event-details/handoff-hero";
import { useToast } from "components/ui/toast";
import {
  formatMoment,
  formatResolutionStatus,
} from "functions/endpoint.functions";
import {
  getRoutingFromTo,
  isFailedStatus,
  isPendingHandoff,
} from "functions/event-details.functions";
import { Link, useSearchParams } from "react-router-dom";
import { useEffect, useMemo, useState } from "react";

// Threshold above which we surface an "Above P95" callout next to the timing
// bar. The number is a UI heuristic, not a server-supplied value — the design
// flags slow events so the operator can investigate (rec §09 event-details).
const SLOW_PROCESSING_MS = 1000;

// Lowercased forms of the messageType enum so callers can compare without
// caring about transport casing (see FR-003 of spec 005).
const START_ANCHOR_TYPE = "eventrequest";
const WAITED_LIFECYCLE_TYPES = new Set([
  "deferralresponse",
  "pendinghandoffresponse",
]);
const END_ANCHOR_TYPES = new Set([
  "resolutionresponse",
  "skipresponse",
  "errorresponse",
  "unsupportedresponse",
  "deferralresponse",
  "pendinghandoffresponse",
]);

function toEpochMs(value: unknown): number | undefined {
  if (value === null || value === undefined) return undefined;
  // moment objects expose valueOf(); strings/Dates also have meaningful valueOf().
  const anyVal = value as { valueOf?: () => unknown };
  if (typeof anyVal?.valueOf === "function") {
    const v = anyVal.valueOf();
    if (typeof v === "number" && Number.isFinite(v)) return v;
  }
  if (typeof value === "string") {
    const parsed = Date.parse(value);
    return Number.isFinite(parsed) ? parsed : undefined;
  }
  return undefined;
}

// Simple millisecond delta between two moment-like / string-like timestamps.
// Used as the third-fallback Queue value when the row's `queueTimeMs` is
// missing — matches pre-spec behaviour. Returns `undefined` when either side
// is unparseable so the chain can keep falling back gracefully.
export function diffMs(later: unknown, earlier: unknown): number | undefined {
  const a = toEpochMs(later);
  const b = toEpochMs(earlier);
  if (a === undefined || b === undefined) return undefined;
  return a - b;
}

/**
 * Spec 005 — derive the "queue" portion of a waited lifecycle from the
 * message history.
 *
 * The captured row-level `queueTimeMs` is the millisecond gap between dequeue
 * and pickup of the *last* delivery. For deferred / pending-handoff events
 * that delivery is the replay or the settlement control message — the figure
 * does not reflect the actual wait. This function reconstructs the
 * end-to-end queue span from the history when (and only when) the lifecycle
 * contains at least one `DeferralResponse` or `PendingHandoffResponse`.
 *
 * Pure function: O(n), no `Date.now()`, no DOM access, tolerant of forward-
 * compatible additions to the messageType enum (unknowns are ignored).
 */
export function getHistoryQueueTimeMs(
  messages: api.Message[] | null | undefined,
): number | undefined {
  // FR-002: empty / missing input -> undefined.
  if (!messages || messages.length === 0) return undefined;

  // Normalise once (FR-003) and capture the parsed timestamp alongside so the
  // function stays O(n) over the input.
  const normalised: { type: string; ts: number }[] = [];
  let waited = false;
  for (const m of messages) {
    const rawType = m?.messageType;
    const type = typeof rawType === "string" ? rawType.toLowerCase() : "";
    const ts = toEpochMs(m?.enqueuedTimeUtc);
    if (ts === undefined) continue;
    normalised.push({ type, ts });
    if (WAITED_LIFECYCLE_TYPES.has(type)) waited = true;
  }

  // FR-004: override is gated on lifecycle shape — non-waited events fall
  // through to today's row-level value.
  if (!waited) return undefined;
  if (normalised.length === 0) return undefined;

  // FR-005 start anchor: earliest EventRequest. FR-006 partial-history
  // fallback: earliest message overall, validated by FR-008's non-negative
  // guard.
  let startAnchor: number | undefined;
  let earliestOverall: number = Number.POSITIVE_INFINITY;
  for (const n of normalised) {
    if (n.ts < earliestOverall) earliestOverall = n.ts;
    if (n.type === START_ANCHOR_TYPE) {
      if (startAnchor === undefined || n.ts < startAnchor) startAnchor = n.ts;
    }
  }
  if (startAnchor === undefined) {
    startAnchor =
      earliestOverall === Number.POSITIVE_INFINITY
        ? undefined
        : earliestOverall;
  }
  if (startAnchor === undefined) return undefined;

  // FR-005/FR-007 end anchor: latest message whose normalised type is a
  // recognised lifecycle outcome (deferral/pending-handoff are valid end
  // anchors because still-waiting events have no terminal outcome yet — the
  // segment then reads "wait so far").
  let endAnchor: number | undefined;
  for (const n of normalised) {
    if (!END_ANCHOR_TYPES.has(n.type)) continue;
    if (endAnchor === undefined || n.ts > endAnchor) endAnchor = n.ts;
  }
  if (endAnchor === undefined) return undefined;

  // FR-008: negative spans (clock skew between SB enqueue time and Resolver
  // timestamps) -> undefined so the caller silently falls back to the row
  // value.
  const span = endAnchor - startAnchor;
  if (span < 0) return undefined;
  // FR-009: result is in milliseconds, same units as queueTimeMs.
  return span;
}

// Lowercase a messageType so comparisons tolerate transport casing (same
// rule getHistoryQueueTimeMs applies inline).
function normalizeMessageType(rawType: unknown): string {
  return typeof rawType === "string" ? rawType.toLowerCase() : "";
}


/** Returns every stored failure occurrence represented by the event history. */
export function getFailureOccurrences(
  event: api.Event | undefined,
  messages: api.Message[] | undefined,
): Array<{ eventId: string; messageId: string }> {
  const rows = (messages ?? []).filter((message) => {
    const dynamic = message as api.Message & {
      deadLetterReason?: string;
      deadLetterErrorDescription?: string;
    };
    const type = String(message.messageType ?? "").toLowerCase();
    return Boolean(
      message.messageId &&
      message.eventId &&
      (type.endsWith("errorresponse") ||
        message.errorContent ||
        dynamic.deadLetterReason ||
        dynamic.deadLetterErrorDescription),
    );
  });
  const occurrences = rows.map((message) => ({
    eventId: message.eventId!,
    messageId: message.messageId!,
  }));
  if (occurrences.length > 0)
    return occurrences.filter(
      (entry, index) =>
        occurrences.findIndex(
          (candidate) =>
            candidate.eventId === entry.eventId &&
            candidate.messageId === entry.messageId,
        ) === index,
    );
  if (
    event?.eventId &&
    event.lastMessageId &&
    ["failed", "deadlettered"].includes(
      (event.resolutionStatus ?? "").toLowerCase(),
    )
  )
    return [{ eventId: event.eventId, messageId: event.lastMessageId }];
  return [];
}

// A completed hand-off carries the external system's optional result details
// on the HandoffCompletedRequest's event content (the original request
// payload lives elsewhere). Surface it as a separate "Handoff result" block.
// Returns undefined when there is no completed hand-off or it carried no
// details.
export function getHandoffResult(messages?: api.Message[]): string | undefined {
  if (!messages?.length) return undefined;
  return messages
    .filter(
      (m) =>
        normalizeMessageType(m.messageType) === "handoffcompletedrequest" &&
        !!m.eventContent,
    )
    .sort(
      (a, b) =>
        (b.enqueuedTimeUtc?.valueOf() ?? 0) -
        (a.enqueuedTimeUtc?.valueOf() ?? 0),
    )[0]?.eventContent;
}

// Format a millisecond duration for display: "—" when null, "Xms" under 1s,
// "X.Ys" otherwise. Used for Queue Time / Processing Time on the detail page.
function formatDurationMs(ms: number | null | undefined): string {
  if (ms === null || ms === undefined) return "—";
  if (ms < 1000) return `${ms}ms`;
  return `${(ms / 1000).toFixed(2)}s`;
}

// Pretty-print JSON when valid; pass through verbatim otherwise so we never
// hide bad payloads from operators.
function safeFormatJson(raw: string): string {
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

interface IMessageListingProps {
  eventDetails: api.Event | undefined;
  /**
   * Full message history for the event (same array `FlowTimeline` consumes).
   * Spec 005 (FR-015..FR-017): when populated and the lifecycle contains a
   * deferral or pending-handoff entry, the Queue segment is derived from the
   * history instead of the per-delivery `queueTimeMs`. Omitting it preserves
   * today's behaviour.
   */
  messages?: api.Message[];
  completeHandoff?: (
    endpointId: string,
    eventId: string,
    messageId: string,
    note?: string,
  ) => Promise<void>;
  failHandoff?: (
    endpointId: string,
    eventId: string,
    messageId: string,
    reason: string,
    errorType?: string,
  ) => Promise<void>;
  onCommentAdded?: () => void;
  /**
   * Spec 006: id of the event currently blocking this session, parsed by the
   * page from the most recent deferral error text. Drives the "Blocked by" row
   * in the Properties panel. Omit when not deferred or no GUID could be parsed.
   */
  blockedByEventId?: string;
  /** Number of later events in the session waiting behind this failed event. */
  blockedCount?: number;
  /** Opens the Blocked tab; offered from the error card when events wait. */
  onShowBlocked?: () => void;
}

const LINK_CLASS = "text-status-info font-semibold no-underline hover:underline";
// Narrower label column than PropertyRow's default: the rail is ~340-420px.
const RAIL_ROW_CLASS = "grid-cols-[96px_minmax(0,1fr)] gap-3 px-4 py-2";

/**
 * The Overview tab of the event details page: the error, failure intelligence
 * and payload in the main column; timing, routing, identifiers and the comment
 * box in the right rail. The operator commands live in the page header
 * (EventActions).
 */
export default function MessageListing(props: IMessageListingProps) {
  const failureOccurrences = getFailureOccurrences(
    props.eventDetails,
    props.messages,
  );
  // A link to one failed attempt (Insights drill-down, spec 043 §7) names it with ?message=.
  const [searchParams] = useSearchParams();
  const linkedMessageId = searchParams.get("message") ?? undefined;
  // The original event payload: the latest payload-carrying request message
  // in the loaded history, falling back to the event's own (server-resolved)
  // payload. For a failed hand-off the event's own content is the empty
  // terminal ErrorResponse, so the Payload section must render the request
  // payload. Memoised so these filter+sort derivations don't recompute on
  // every render (e.g. a keystroke in the comment box); the value only
  // changes when the history or event document does.
  const resubmitPayload = useMemo(
    () =>
      getResubmitPayload(props.messages) ??
      props.eventDetails?.messageContent?.eventContent?.eventJson,
    [props.messages, props.eventDetails],
  );
  // A completed hand-off's optional external result details (separate from
  // the original payload), shown in its own block when present.
  const handoffResult = useMemo(
    () => getHandoffResult(props.messages),
    [props.messages],
  );
  // Routing lineage: for response messages, From = the originating publisher
  // (resolved from the EventRequest in the history) and To = the handling
  // endpoint — never the internal Resolver hop.
  const routing = useMemo(
    () => getRoutingFromTo(props.eventDetails, props.messages),
    [props.eventDetails, props.messages],
  );

  const { addToast } = useToast();

  // Handoff hero (Variant B). `heroOverride` lets the card flip to settled/
  // failed the instant the operator acts — the underlying Pending → Completed/
  // Failed transition is asynchronous (the subscriber processes the published
  // control message), so the override reflects operator intent until the event
  // document catches up on the next load.
  const [heroOverride, setHeroOverride] = useState<{
    state: HandoffState;
    note?: string;
    reason?: string;
  } | null>(null);
  const [completeOpen, setCompleteOpen] = useState(false);
  const [failOpen, setFailOpen] = useState(false);
  const [handoffNote, setHandoffNote] = useState("");
  const [failReason, setFailReason] = useState("");
  const [handoffBusy, setHandoffBusy] = useState(false);
  const [operatorName, setOperatorName] = useState<string>("operator");

  useEffect(() => {
    // Reset handoff UI when navigating to a different event.
    setHeroOverride(null);
  }, [props.eventDetails]);

  useEffect(() => {
    let active = true;
    const client = new api.Client(api.CookieAuth());
    client
      .getMe()
      .then((me) => {
        if (active && me?.name) setOperatorName(me.name);
      })
      .catch(() => {
        /* fallback to "operator" */
      });
    return () => {
      active = false;
    };
  }, []);

  const status = props.eventDetails?.resolutionStatus?.toLowerCase();
  const isFailed = isFailedStatus(props.eventDetails?.resolutionStatus);
  const isHandoff = isPendingHandoff(
    props.eventDetails?.resolutionStatus,
    props.eventDetails?.pendingSubStatus,
  );

  // The hero shows for any pending handoff, and stays visible (in its settled/
  // failed form) immediately after the operator acts.
  const heroState: HandoffState | null = heroOverride
    ? heroOverride.state
    : isHandoff
      ? "pending"
      : null;

  const confirmCompleteHandoff = async () => {
    const note = handoffNote.trim();
    setHandoffBusy(true);
    try {
      await props.completeHandoff?.(
        props.eventDetails?.endpointId!,
        props.eventDetails?.eventId!,
        props.eventDetails?.lastMessageId!,
        note || undefined,
      );
      setCompleteOpen(false);
      setHeroOverride({ state: "settled", note: note || undefined });
      addToast({
        title: "Handoff completed",
        description: "Completion published — propagating downstream.",
        variant: "success",
      });
    } catch {
      addToast({
        title: "Could not complete handoff",
        description: "The settlement request failed. Try again.",
        variant: "error",
      });
    } finally {
      setHandoffBusy(false);
    }
  };

  const confirmFailHandoff = async () => {
    const reason = failReason.trim();
    if (!reason) return;
    setHandoffBusy(true);
    try {
      await props.failHandoff?.(
        props.eventDetails?.endpointId!,
        props.eventDetails?.eventId!,
        props.eventDetails?.lastMessageId!,
        reason,
      );
      setFailOpen(false);
      setHeroOverride({ state: "failed", reason });
      addToast({
        title: "Handoff failed",
        description:
          "Marked as failed — will surface under Blocked downstream.",
        variant: "error",
      });
    } catch {
      addToast({
        title: "Could not fail handoff",
        description: "The settlement request failed. Try again.",
        variant: "error",
      });
    } finally {
      setHandoffBusy(false);
    }
  };

  // Masking is per-field and happens server-side, so the payload renders as
  // received — its [Sensitive] values are already masked for callers without
  // the PII Reader role. The server only falls back to replacing the whole
  // payload when it cannot resolve the event type or parse the JSON, and then
  // there is nothing to show at all. The bare "[REDACTED]" is the
  // pre-field-level form, still possible in stored payloads that were
  // resubmitted while whole-payload redaction was in effect.
  const isRedacted =
    resubmitPayload === "[REDACTED]" ||
    resubmitPayload === "[REDACTED:unknown-type]" ||
    resubmitPayload === "[REDACTED:invalid-json]";

  // Pretty-print the payload / hand-off result once, so a keystroke elsewhere
  // in the subtree doesn't re-run JSON.parse/JSON.stringify over the full
  // payload on every render.
  const formattedPayload = useMemo(
    () => (resubmitPayload ? safeFormatJson(resubmitPayload) : ""),
    [resubmitPayload],
  );
  const formattedHandoffResult = useMemo(
    () => (handoffResult ? safeFormatJson(handoffResult) : ""),
    [handoffResult],
  );

  const errorContent = props.eventDetails?.messageContent?.errorContent;
  const stackTrace = errorContent?.exceptionStackTrace?.trim();
  const blockedCount = props.blockedCount ?? 0;

  return (
    <div className="w-full min-w-0 flex flex-col gap-5">
      {heroState && (
        <div className="overflow-hidden rounded-nb-lg border border-border">
          <HandoffHero
            state={heroState}
            reason={props.eventDetails?.handoffReason}
            externalJobId={props.eventDetails?.externalJobId}
            expectedBy={props.eventDetails?.expectedBy}
            settledNote={heroOverride?.note}
            failedReason={heroOverride?.reason}
            resolvedBy={operatorName}
            busy={handoffBusy}
            onComplete={() => {
              setHandoffNote("");
              setCompleteOpen(true);
            }}
            onFail={() => {
              setFailReason("");
              setFailOpen(true);
            }}
          />
        </div>
      )}

      <div className="flex flex-wrap items-start gap-x-6 gap-y-5">
        <div className="flex-[999_1_520px] min-w-0 flex flex-col gap-4">
          {isFailed && (
            <section
              aria-label="Error"
              className="bg-card border border-border rounded-nb-md overflow-hidden"
            >
              <div className="flex items-baseline justify-between gap-3 px-4 py-3 border-b border-border">
                <h2 className="m-0 text-[13px] font-bold">Error</h2>
                {props.eventDetails?.updatedAt && (
                  <span className="font-mono text-[11px] text-muted-foreground">
                    {formatMoment(props.eventDetails.updatedAt)}
                  </span>
                )}
              </div>
              <div className="p-4 flex flex-col gap-3">
                {status === "deadlettered" ? (
                  <dl className="m-0 flex flex-col gap-2 text-sm">
                    <div>
                      <dt className="font-semibold text-muted-foreground text-[12px]">
                        DeadLetter Reason
                      </dt>
                      <dd className="m-0 wrap-break-word">
                        {props.eventDetails?.deadLetterReason || "—"}
                      </dd>
                    </div>
                    <div>
                      <dt className="font-semibold text-muted-foreground text-[12px]">
                        DeadLetter Error Description
                      </dt>
                      <dd className="m-0 wrap-break-word">
                        {props.eventDetails?.deadLetterErrorDescription || "—"}
                      </dd>
                    </div>
                  </dl>
                ) : errorContent?.errorText ? (
                  <div className="flex items-start gap-2.5 rounded-nb-md bg-status-danger-50 text-status-danger-ink dark:bg-red-950/40 dark:text-red-200 px-3.5 py-3">
                    <svg
                      aria-hidden="true"
                      className="w-4 h-4 mt-0.5 shrink-0"
                      viewBox="0 0 16 16"
                      fill="none"
                    >
                      <path
                        d="M8 2.5 14 13H2L8 2.5zM8 6.5v3M8 11.2v.3"
                        stroke="currentColor"
                        strokeWidth="1.5"
                        strokeLinecap="round"
                        strokeLinejoin="round"
                      />
                    </svg>
                    <p className="m-0 text-sm font-semibold wrap-break-word">
                      {errorContent.errorText}
                    </p>
                  </div>
                ) : (
                  <p className="m-0 text-sm text-muted-foreground">
                    No error details were recorded for this attempt.
                  </p>
                )}
                {status !== "deadlettered" && stackTrace && (
                  <div className="flex flex-col gap-1.5">
                    <span className="font-mono text-[10.5px] uppercase tracking-[0.12em] text-muted-foreground">
                      Exception
                    </span>
                    <pre className="m-0 max-h-64 overflow-auto rounded-nb-sm bg-muted px-3.5 py-3 font-mono text-[11.5px] leading-[1.65] text-muted-foreground whitespace-pre-wrap break-all">
                      {errorContent?.exceptionStackTrace}
                    </pre>
                  </div>
                )}
              </div>
              {blockedCount > 0 && (
                <div className="flex flex-wrap items-center justify-between gap-x-4 gap-y-1 px-4 py-2.5 border-t border-border bg-status-warning-50 text-status-warning-ink dark:bg-yellow-950/40 dark:text-yellow-200 text-[13px]">
                  <span className="font-semibold">
                    {`This event holds up its session: ${blockedCount} later ${
                      blockedCount === 1 ? "event is" : "events are"
                    } waiting behind it.`}
                  </span>
                  {props.onShowBlocked && (
                    <button
                      type="button"
                      onClick={props.onShowBlocked}
                      className="bg-transparent border-0 p-0 font-semibold underline underline-offset-2 text-inherit"
                    >
                      View blocked events
                    </button>
                  )}
                </div>
              )}
            </section>
          )}

          {isFailed && failureOccurrences.length > 0 && (
            <div className="flex flex-col">
              {failureOccurrences.map((occurrence, index) => (
                <IntelligenceCard
                  key={`${occurrence.eventId}/${occurrence.messageId}`}
                  endpointId={props.eventDetails?.endpointId!}
                  eventId={occurrence.eventId}
                  messageId={occurrence.messageId}
                  resolutionStatus={props.eventDetails?.resolutionStatus}
                  occurrenceLabel={`occurrence ${index + 1}`}
                  highlighted={linkedMessageId === occurrence.messageId}
                />
              ))}
            </div>
          )}

          {/* Payload — the original event content (the latest request message's
              payload, falling back to the event's own). For a hand-off this is
              the EventRequest, not the terminal settlement message which carries
              no original payload. */}
          {resubmitPayload && isRedacted && (
            <div className="flex items-center gap-2.5 bg-card border border-border rounded-nb-md px-4 py-3">
              <svg
                className="w-4 h-4 shrink-0 text-muted-foreground"
                viewBox="0 0 16 16"
                fill="none"
                aria-hidden="true"
              >
                <rect
                  x="3"
                  y="7"
                  width="10"
                  height="6"
                  rx="1.4"
                  stroke="currentColor"
                  strokeWidth="1.4"
                />
                <path
                  d="M5.5 7V5.5a2.5 2.5 0 0 1 5 0V7"
                  stroke="currentColor"
                  strokeWidth="1.4"
                />
              </svg>
              <span className="text-[13px] text-muted-foreground">
                The payload is hidden — viewing raw event payloads requires the
                PII Reader role. A site Owner can grant it on the Access Control
                page.
              </span>
            </div>
          )}

          {resubmitPayload && !isRedacted && (
            // Rendered as-is. Sensitive values are already masked server-side for
            // callers without the PII Reader role, so there is nothing here to
            // blur or reveal.
            <CodeBlock
              title="Payload"
              subtitle="application/json"
              // Copies what is rendered, which is the server-masked payload for a
              // caller without the PII Reader role — the button never reaches past
              // redaction to the raw event content.
              actions={<CopyButton text={formattedPayload} describes="Payload" />}
              linkifyGuid={(_guid) =>
                // Clicking a GUID in the payload jumps to the Messages search
                // pre-filtered to that ID (design rec §09 code).
                `/Messages?eventId=${_guid}`
              }
            >
              {formattedPayload}
            </CodeBlock>
          )}

          {/* Handoff result — the external system's completion details, when the
              handler supplied any on CompleteHandoff (a HandoffCompletedRequest's
              eventContent). Distinct from the event payload above. */}
          {handoffResult && (
            <CodeBlock title="Handoff result" subtitle="application/json">
              {formattedHandoffResult}
            </CodeBlock>
          )}
        </div>

        <aside
          aria-label="Event facts"
          className="flex-[1_1_340px] min-w-0 flex flex-col gap-4"
        >
          {/* Timing bar — queue · processing · ack as a stacked horizontal bar.

              Spec 005 (FR-020/FR-050): the Queue value is chosen via
              `getHistoryQueueTimeMs(props.messages) ?? event.queueTimeMs ??
              diffMs(event.updatedAt, event.enqueuedTimeUtc)`. The override fires
              only on waited (deferred / pending-handoff) lifecycles so the
              captured row value — which describes only the *last* delivery — does
              not silently report a millisecond for an event that actually spent
              minutes waiting. Use `??` (not `||`) so a measured `0` is preserved.
              DO NOT collapse this chain back to a single `event.queueTimeMs`
              read; that regresses the spec. Processing time stays on the existing
              chain (FR-021). */}
          {(props.eventDetails?.queueTimeMs !== undefined ||
            props.eventDetails?.processingTimeMs !== undefined) &&
            (() => {
              const queueMs =
                getHistoryQueueTimeMs(props.messages) ??
                props.eventDetails?.queueTimeMs ??
                diffMs(
                  props.eventDetails?.updatedAt,
                  props.eventDetails?.enqueuedTimeUtc,
                );
              const processingMs = props.eventDetails?.processingTimeMs;
              // FR-022: Total uses the displayed Queue + Processing — if Queue is
              // overridden, the override participates in the total.
              const totalMs = (queueMs ?? 0) + (processingMs ?? 0);
              return (
                <TimingBar
                  segments={[
                    {
                      label: "Queue",
                      display: formatDurationMs(queueMs),
                      weight: Math.max(queueMs ?? 0, 0),
                      colorClass: "bg-[#BFD8F2]",
                    },
                    {
                      label: "Processing",
                      display: formatDurationMs(processingMs),
                      weight: Math.max(processingMs ?? 0, 0),
                      colorClass: "bg-status-warning",
                    },
                    {
                      label: "Ack",
                      display: "< 1 ms",
                      weight:
                        0.05 * ((queueMs ?? 0) + (processingMs ?? 0) || 1),
                      colorClass: "bg-status-success",
                    },
                  ]}
                  total={
                    <span>
                      {formatDurationMs(totalMs)} ·{" "}
                      <span
                        className={
                          isFailed ? "text-status-danger" : "text-status-success"
                        }
                      >
                        {formatResolutionStatus(
                          props.eventDetails?.resolutionStatus,
                          props.eventDetails?.reason,
                        )}
                      </span>
                    </span>
                  }
                  // FR-023: the slow-processing callout evaluates Processing only,
                  // never the overridden Queue value.
                  trailing={
                    (processingMs ?? 0) >= SLOW_PROCESSING_MS
                      ? `Above ${SLOW_PROCESSING_MS} ms — check downstream sink`
                      : undefined
                  }
                />
              );
            })()}

          <PropertyList>
            <PropertySection title="Routing">
              <PropertyRow
                className={RAIL_ROW_CLASS}
                label="Event type"
                value={
                  props.eventDetails?.eventTypeId ? (
                    <Link
                      to={`/EventTypes/Details/${props.eventDetails.eventTypeId}`}
                      className={LINK_CLASS}
                    >
                      {props.eventDetails.eventTypeId}
                    </Link>
                  ) : (
                    "—"
                  )
                }
              />
              <PropertyRow
                className={RAIL_ROW_CLASS}
                label="From"
                value={
                  routing.from ? (
                    <Link
                      to={`/Endpoints/Details/${routing.from}`}
                      className={LINK_CLASS}
                    >
                      {routing.from}
                    </Link>
                  ) : (
                    "—"
                  )
                }
              />
              <PropertyRow
                className={RAIL_ROW_CLASS}
                label="To"
                value={
                  routing.to ? (
                    <Link
                      to={`/Endpoints/Details/${routing.to}`}
                      className={LINK_CLASS}
                    >
                      {routing.to}
                    </Link>
                  ) : (
                    "—"
                  )
                }
              />
              <PropertyRow
                className={RAIL_ROW_CLASS}
                label="Enqueued"
                value={formatMoment(props.eventDetails?.enqueuedTimeUtc)}
                mono
              />
              {/* Spec 006: render only when the event is deferred AND a blocker
                  GUID was parsed AND we have an endpointId to build the link with.
                  All three are required — endpointId is part of the route, and a
                  row without a valid link would mislead operators. */}
              {status === "deferred" &&
                props.blockedByEventId &&
                props.eventDetails?.endpointId && (
                  <PropertyRow
                    className={RAIL_ROW_CLASS}
                    label="Blocked by"
                    value={
                      <Link
                        to={`/Message/Index/${props.eventDetails.endpointId}/${props.blockedByEventId}/0`}
                        className={LINK_CLASS}
                      >
                        {props.blockedByEventId}
                      </Link>
                    }
                    mono
                  />
                )}
            </PropertySection>

            {(props.eventDetails?.handoffReason ||
              props.eventDetails?.externalJobId ||
              props.eventDetails?.expectedBy) && (
              <PropertySection title="Handoff details">
                {props.eventDetails?.handoffReason && (
                  <PropertyRow
                    className={RAIL_ROW_CLASS}
                    label="Reason"
                    value={props.eventDetails.handoffReason}
                  />
                )}
                {props.eventDetails?.externalJobId && (
                  <PropertyRow
                    className={RAIL_ROW_CLASS}
                    label="External job"
                    value={props.eventDetails.externalJobId}
                    mono
                  />
                )}
                {props.eventDetails?.expectedBy && (
                  <PropertyRow
                    className={RAIL_ROW_CLASS}
                    label="Expected by"
                    value={formatMoment(props.eventDetails.expectedBy)}
                    mono
                  />
                )}
              </PropertySection>
            )}

            <PropertySection title="Identifiers">
              <IdentifierRow
                label="EventId"
                value={props.eventDetails?.eventId}
              />
              <IdentifierRow
                label="SessionId"
                value={props.eventDetails?.sessionId}
              />
              <IdentifierRow
                label="MessageId"
                value={props.eventDetails?.lastMessageId}
              />
              {props.eventDetails?.originatingMessageId && (
                <IdentifierRow
                  label="OriginatingMessageId"
                  value={props.eventDetails.originatingMessageId}
                />
              )}
            </PropertySection>
          </PropertyList>

          <CommentSection
            eventId={props.eventDetails?.eventId}
            onCommentAdded={props.onCommentAdded}
          />
        </aside>
      </div>

      {/* Complete handoff */}
      <Modal isOpen={completeOpen} onClose={() => setCompleteOpen(false)}>
        <ModalHeader onClose={() => setCompleteOpen(false)}>
          Complete handoff?
        </ModalHeader>
        <ModalBody>
          <p className="text-sm text-muted-foreground">
            Mark this event as successfully handed off. A{" "}
            <code className="font-mono text-xs bg-muted px-1.5 py-0.5 rounded-sm">
              HandoffCompletedRequest
            </code>{" "}
            is published to the subscriber, moving it out of pending so it
            propagates downstream.
          </p>
          <div className="mt-3 rounded-nb-md border border-border bg-canvas dark:bg-muted/40 p-3 flex flex-col gap-1.5">
            <div className="flex justify-between font-mono text-[11.5px]">
              <span className="text-muted-foreground">Event</span>
              <b className="font-semibold text-foreground">
                {props.eventDetails?.eventTypeId ?? "—"}
              </b>
            </div>
            {props.eventDetails?.externalJobId && (
              <div className="flex justify-between font-mono text-[11.5px]">
                <span className="text-muted-foreground">External job</span>
                <b className="font-semibold text-foreground">
                  {props.eventDetails.externalJobId}
                </b>
              </div>
            )}
            <div className="flex justify-between font-mono text-[11.5px]">
              <span className="text-muted-foreground">Endpoint</span>
              <b className="font-semibold text-foreground">
                {props.eventDetails?.endpointId ?? "—"}
              </b>
            </div>
          </div>
          <label className="block mt-3.5 mb-1.5 text-xs font-semibold text-foreground">
            Operator note{" "}
            <span className="font-normal text-muted-foreground">
              (optional, written to audit log)
            </span>
          </label>
          <Textarea
            value={handoffNote}
            onChange={(e) => setHandoffNote(e.target.value)}
            placeholder="e.g. Verified batch finished cleanly in the external console."
            rows={3}
          />
        </ModalBody>
        <ModalFooter>
          <Button variant="outline" onClick={() => setCompleteOpen(false)}>
            Cancel
          </Button>
          <Button
            colorScheme="blue"
            isLoading={handoffBusy}
            disabled={handoffBusy}
            onClick={confirmCompleteHandoff}
          >
            Complete handoff
          </Button>
        </ModalFooter>
      </Modal>

      {/* Fail handoff */}
      <Modal isOpen={failOpen} onClose={() => setFailOpen(false)}>
        <ModalHeader onClose={() => setFailOpen(false)}>
          Fail this handoff?
        </ModalHeader>
        <ModalBody>
          <p className="text-sm text-muted-foreground">
            Mark the event as terminally failed. A{" "}
            <code className="font-mono text-xs bg-muted px-1.5 py-0.5 rounded-sm">
              HandoffFailedRequest
            </code>{" "}
            is published; the event surfaces under the{" "}
            <b className="font-semibold text-foreground">Blocked</b> tab on
            dependent endpoints and triggers any failure alerts.
          </p>
          <div className="mt-3 rounded-nb-md border border-border bg-canvas dark:bg-muted/40 p-3 flex flex-col gap-1.5">
            <div className="flex justify-between font-mono text-[11.5px]">
              <span className="text-muted-foreground">Event</span>
              <b className="font-semibold text-foreground">
                {props.eventDetails?.eventTypeId ?? "—"}
              </b>
            </div>
            <div className="flex justify-between font-mono text-[11.5px]">
              <span className="text-muted-foreground">Reversible</span>
              <b className="font-semibold text-foreground">
                No — re-publish needed
              </b>
            </div>
          </div>
          <label className="block mt-3.5 mb-1.5 text-xs font-semibold text-foreground">
            Failure reason{" "}
            <span className="font-semibold text-status-danger">required</span>
          </label>
          <Textarea
            value={failReason}
            onChange={(e) => setFailReason(e.target.value)}
            placeholder="e.g. External system reported permanent failure; batch was rejected."
            rows={3}
          />
        </ModalBody>
        <ModalFooter>
          <Button variant="outline" onClick={() => setFailOpen(false)}>
            Cancel
          </Button>
          <Button
            colorScheme="red"
            isLoading={handoffBusy}
            disabled={handoffBusy || !failReason.trim()}
            onClick={confirmFailHandoff}
          >
            Fail handoff
          </Button>
        </ModalFooter>
      </Modal>
    </div>
  );
}

/** One identifier in the rail: label over the full value, with a copy button. */
function IdentifierRow({ label, value }: { label: string; value?: string }) {
  return (
    <div className="flex items-center gap-2 pl-4 pr-2.5 py-2 border-t border-border">
      <div className="flex-1 min-w-0">
        <dt className="text-[11.5px] font-semibold text-muted-foreground">
          {label}
        </dt>
        <dd className="m-0 font-mono text-[12px] tabular-nums text-foreground break-all">
          {value || "—"}
        </dd>
      </div>
      {value && (
        <CopyButton
          text={value}
          label=""
          describes={label}
          className="shrink-0 border-transparent px-1.5 py-1.5"
        />
      )}
    </div>
  );
}

function CommentSection({
  eventId,
  onCommentAdded,
}: {
  eventId?: string;
  onCommentAdded?: () => void;
}) {
  const [comment, setComment] = useState("");
  const [saving, setSaving] = useState(false);
  const [saved, setSaved] = useState(false);

  const submit = async () => {
    if (!comment.trim() || !eventId) return;
    setSaving(true);
    setSaved(false);
    try {
      const client = new api.Client(api.CookieAuth());
      let userName = "Unknown";
      try {
        const me = await client.getMe();
        if (me?.name) userName = me.name;
      } catch {
        /* fallback */
      }

      const audit = new api.MessageAudit();
      audit.auditorName = userName;
      audit.auditTimestamp = new Date() as any;
      audit.auditType = api.MessageAuditAuditType.Comment;
      audit.comment = comment.trim();

      await client.postMessageAudit(eventId, audit);
      setComment("");
      setSaved(true);
      onCommentAdded?.();
    } catch {
      // error
    } finally {
      setSaving(false);
    }
  };

  return (
    <section
      aria-label="Comment"
      className="bg-card border border-border rounded-nb-md p-4 flex flex-col gap-2.5"
    >
      <div className="flex items-baseline justify-between gap-3">
        <label htmlFor="event-comment" className="text-[13px] font-bold">
          Comment
        </label>
        <span className="text-xs text-muted-foreground">
          Saved to the audit trail
        </span>
      </div>
      <Textarea
        id="event-comment"
        value={comment}
        onChange={(e) => {
          setComment(e.target.value);
          setSaved(false);
        }}
        placeholder="Enter a comment..."
        rows={3}
      />
      <div className="flex items-center justify-end gap-2">
        {saved && (
          <span className="text-sm text-green-600 dark:text-green-400">
            Saved
          </span>
        )}
        <Button
          size="sm"
          variant="outline"
          colorScheme="gray"
          onClick={submit}
          disabled={saving || !comment.trim()}
        >
          {saving ? "Saving..." : "Save"}
        </Button>
      </div>
    </section>
  );
}
