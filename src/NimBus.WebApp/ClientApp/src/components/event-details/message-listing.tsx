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
import { Select } from "components/ui/select";
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
import { Link } from "react-router-dom";
import { useEffect, useMemo, useState } from "react";
import { notifyError, notifySuccess } from "functions/notifications.functions";
import {
  describeCommandError,
  isStaleCommand,
} from "functions/operator-command.functions";

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
    startAnchor = earliestOverall === Number.POSITIVE_INFINITY
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

function isResponseMessageType(messageType: unknown): boolean {
  return normalizeMessageType(messageType).endsWith("response");
}

// Stores persist an absent `originatingFrom` inconsistently — Cosmos leaves it
// null, SQL Server normalises it to "" — and the wire default is the literal
// "self" sentinel. None of those is a usable publisher name, so collapse them
// all to empty.
function cleanSender(value: string | undefined): string {
  const trimmed = (value ?? "").trim();
  return trimmed.toLowerCase() === "self" ? "" : trimmed;
}

// A message's sender: prefer `originatingFrom` (the original publisher when
// stamped), fall back to the literal `from`. Uses truthy `||` (not `??`) so an
// empty/"self" originatingFrom falls through to `from` instead of rendering
// blank.
function senderOf(message: api.Message | undefined): string {
  return cleanSender(message?.originatingFrom) || cleanSender(message?.from);
}

// Response messages (ResolutionResponse, ErrorResponse, SkipResponse, …) are
// addressed `To` the platform Resolver — an internal auditing hop, not a
// meaningful routing leg. The operator-relevant lineage is "where the event
// came from → which endpoint handled it". The publisher lives on the
// originating EventRequest message in the history; the response's own `from`
// is the handling endpoint. So for responses we render From = <publisher> and
// To = <handling endpoint>, and never surface the Resolver. Non-response
// messages keep their literal from/to.
export function getRoutingFromTo(
  event:
    | Pick<
        api.Event,
        "from" | "to" | "originatingFrom" | "originatingMessageId" | "messageType"
      >
    | undefined,
  messages?: api.Message[],
): { from: string; to: string } {
  const from = event?.from ?? "";
  const to = event?.to ?? "";
  if (!isResponseMessageType(event?.messageType)) {
    return { from, to };
  }

  const originatingMessage = event?.originatingMessageId
    ? messages?.find((m) => m.messageId === event.originatingMessageId)
    : undefined;
  // Prefer the originating EventRequest's sender (the true publisher); fall
  // back to the event's own originatingFrom, then to its from, so partial
  // histories still render something meaningful. `||` so empty/"self" values
  // fall through rather than rendering a blank From.
  const publisher =
    senderOf(originatingMessage) || cleanSender(event?.originatingFrom) || from;
  return { from: publisher, to: from };
}

// Request message types that carry the original event payload — mirrors the
// backend's resubmit source selection
// (EventImplementation.LatestRequestMessageWithPayload). Handoff control
// requests (HandoffCompleted/FailedRequest) and SkipRequest carry no payload;
// terminal *Response messages may carry a stale/empty payload (notably a
// failed hand-off's ErrorResponse). "processdeferredrequest" exists in Core
// but is folded to Unknown by the current API contract — kept here so the
// filter stays correct if the contract ever surfaces it.
const PAYLOAD_REQUEST_TYPES = new Set([
  "eventrequest",
  "resubmissionrequest",
  "retryrequest",
  "continuationrequest",
  "processdeferredrequest",
]);

// The payload that Resubmit replays: the latest request message that actually
// carries event content — the same rule the backend uses. For a failed
// hand-off the event's own payload is the empty terminal ErrorResponse, so
// both the Payload section and the "Resubmit with changes" modal must use the
// original request payload instead. Returns undefined when no such message is
// in the loaded history (caller falls back to the event payload).
export function getResubmitPayload(
  messages?: api.Message[],
): string | undefined {
  if (!messages?.length) return undefined;
  return messages
    .filter(
      (m) =>
        PAYLOAD_REQUEST_TYPES.has(normalizeMessageType(m.messageType)) &&
        !!m.eventContent,
    )
    .sort(
      (a, b) =>
        (b.enqueuedTimeUtc?.valueOf() ?? 0) -
        (a.enqueuedTimeUtc?.valueOf() ?? 0),
    )[0]?.eventContent;
}

/** Returns every stored failure occurrence represented by the event history. */
export function getFailureOccurrences(
  event: api.Event | undefined,
  messages: api.Message[] | undefined,
): Array<{ eventId: string; messageId: string }> {
  const rows = (messages ?? []).filter((message) => {
    const dynamic = message as api.Message & { deadLetterReason?: string; deadLetterErrorDescription?: string };
    const type = String(message.messageType ?? "").toLowerCase();
    return Boolean(message.messageId && message.eventId &&
      (type.endsWith("errorresponse") || message.errorContent || dynamic.deadLetterReason || dynamic.deadLetterErrorDescription));
  });
  const occurrences = rows.map((message) => ({ eventId: message.eventId!, messageId: message.messageId! }));
  if (occurrences.length > 0) return occurrences.filter((entry, index) =>
    occurrences.findIndex(candidate => candidate.eventId === entry.eventId && candidate.messageId === entry.messageId) === index);
  if (event?.eventId && event.lastMessageId && ["failed", "deadlettered"].includes((event.resolutionStatus ?? "").toLowerCase()))
    return [{ eventId: event.eventId, messageId: event.lastMessageId }];
  return [];
}

// A completed hand-off carries the external system's optional result details
// on the HandoffCompletedRequest's event content (the original request
// payload lives elsewhere). Surface it as a separate "Handoff result" block.
// Returns undefined when there is no completed hand-off or it carried no
// details.
export function getHandoffResult(
  messages?: api.Message[],
): string | undefined {
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

function statusToBadgeVariant(
  status: string | undefined,
):
  | "completed"
  | "failed"
  | "deferred"
  | "deadlettered"
  | "skipped"
  | "unsupported"
  | "pending"
  | "default" {
  switch (status?.toLowerCase()) {
    case "completed":
      return "completed";
    case "failed":
      return "failed";
    case "deferred":
      return "deferred";
    case "deadlettered":
      return "deadlettered";
    case "skipped":
      return "skipped";
    case "unsupported":
      return "unsupported";
    case "pending":
      return "pending";
    default:
      return "default";
  }
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
  eventTypes: api.EventType[];
  skipEvent: (eventId: string, messageId: string) => Promise<void>;
  resubmitEvent: (eventId: string, messageId: string) => Promise<void>;
  resubmitEventWithChanges: (
    eventId: string,
    messageId: string,
    body: api.ResubmitWithChanges,
  ) => Promise<void>;
  deleteEvent?: () => Promise<void>;
  reprocessDeferred?: () => Promise<void>;
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
  /**
   * Message id of the attempt an accepted resubmit or skip acted on. While it
   * is still the event's latest message, every action stays closed: the server
   * would refuse a second command on that attempt, and Delete (by event and
   * session) could remove a newer attempt the page has not shown yet.
   */
  commandAcceptedFor?: string;
}

interface IButtonState {
  isDisabled: boolean;
  text: string;
}

const RESUBMIT_IDLE: IButtonState = { isDisabled: false, text: "Resubmit" };
const RESUBMIT_WITH_CHANGES_IDLE: IButtonState = {
  isDisabled: false,
  text: "Resubmit with changes",
};
const SKIP_IDLE: IButtonState = { isDisabled: false, text: "Skip" };

export default function MessageListing(props: IMessageListingProps) {
  const failureOccurrences = getFailureOccurrences(props.eventDetails, props.messages);
  const [showErrorDetails, setShowErrorDetails] = useState(false);
  const handleErrorDetailsToggle = () => setShowErrorDetails(!showErrorDetails);
  const [isOpen, setIsOpen] = useState(false);
  const onOpen = () => setIsOpen(true);
  const onClose = () => setIsOpen(false);
  const [showDeleteConfirm, setShowDeleteConfirm] = useState(false);
  const deleteBtn: IButtonState = { isDisabled: false, text: "Delete" };
  const [deleteButton, setDeleteButton] = useState(deleteBtn);
  // The original event payload: the latest payload-carrying request message
  // in the loaded history, falling back to the event's own (server-resolved)
  // payload. For a failed hand-off the event's own content is the empty
  // terminal ErrorResponse, so the Payload section and the
  // resubmit-with-changes modal must render/prefill the request payload.
  // Memoised so these filter+sort derivations don't recompute on every render
  // (e.g. a keystroke in a controlled input elsewhere in the subtree); the
  // value only changes when the history or event document does.
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
  const [textAreaValue, setTextAreaValue] = useState(resubmitPayload);
  const [eventTypeIdValue, setEventTypeIdValue] = useState(
    props.eventDetails?.eventTypeId,
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

  // The textarea prefill tracks the resolved request payload — `messages`
  // loads asynchronously after `eventDetails`, so keying on the payload (not
  // the event object) picks up the request payload once the history arrives.
  useEffect(() => {
    setTextAreaValue(resubmitPayload);
  }, [resubmitPayload]);

  useEffect(() => {
    setShowErrorDetails(false);
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

  const [resubmitWithChangesButton, setResubmitWithChangesButton] =
    useState(RESUBMIT_WITH_CHANGES_IDLE);
  const [resubmitButton, setResubmitButton] = useState(RESUBMIT_IDLE);
  const [skipButton, setSkipButton] = useState(SKIP_IDLE);

  const attemptActedOn =
    !!props.commandAcceptedFor &&
    props.commandAcceptedFor === props.eventDetails?.lastMessageId;

  // The event's next attempt (it failed again, or someone else acted on it) has
  // its own message id, and the commands apply to it afresh.
  useEffect(() => {
    setResubmitWithChangesButton(RESUBMIT_WITH_CHANGES_IDLE);
    setResubmitButton(RESUBMIT_IDLE);
    setSkipButton(SKIP_IDLE);
  }, [props.eventDetails?.lastMessageId]);

  const handleInputChange = (e: React.ChangeEvent<HTMLTextAreaElement>) => {
    const inputValue = e.target.value;
    const sanitizedValue = inputValue.replace(/\n/g, "");
    setTextAreaValue(sanitizedValue);
  };

  const handleEventTypeIdChange = (e: React.ChangeEvent<HTMLSelectElement>) => {
    setEventTypeIdValue(e.target.value);
  };

  const isFailedMessage = (status: string | undefined): boolean => {
    if (!status) return false;
    const lowerStatus = status.toLowerCase();
    return (
      lowerStatus === "failed" ||
      lowerStatus === "unsupported" ||
      lowerStatus === "deadlettered"
    );
  };

  const isDeadletteredMessage = (status: string | undefined): boolean => {
    if (!status) return false;
    const lowerStatus = status.toLowerCase();
    return lowerStatus === "deadlettered";
  };

  const isDeferredMessage = (status: string | undefined): boolean => {
    if (!status) return false;
    return status.toLowerCase() === "deferred";
  };

  // PendingHandoff entries are healthy in-flight messages. Resubmit/Skip stay
  // available so operators can override a stuck or wrong external job.
  const isPendingHandoff = (
    status: string | undefined,
    pendingSubStatus: string | null | undefined,
  ): boolean => {
    if (!status || !pendingSubStatus) return false;
    return (
      status.toLowerCase() === "pending" &&
      pendingSubStatus.toLowerCase() === "handoff"
    );
  };

  const isHandoff = isPendingHandoff(
    props.eventDetails?.resolutionStatus,
    props.eventDetails?.pendingSubStatus,
  );

  // Pending handoffs are driven by the Hero card's Complete/Fail actions, so the
  // Details-header resubmit/skip buttons are only for terminally failed events.
  const showFailedActions = isFailedMessage(props.eventDetails?.resolutionStatus);

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
        description: "Marked as failed — will surface under Blocked downstream.",
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

  // Pretty-print the payload / hand-off result once and reuse across the
  // inline blocks and the resubmit-with-changes modal, so a keystroke
  // elsewhere in the subtree doesn't re-run JSON.parse/JSON.stringify over
  // the full payload on every render.
  const formattedPayload = useMemo(
    () => (resubmitPayload ? safeFormatJson(resubmitPayload) : ""),
    [resubmitPayload],
  );
  const formattedHandoffResult = useMemo(
    () => (handoffResult ? safeFormatJson(handoffResult) : ""),
    [handoffResult],
  );

  const reprocessBtn: IButtonState = { isDisabled: false, text: "Reprocess" };
  const [reprocessButton, setReprocessButton] = useState(reprocessBtn);

  const reprocessDeferredClick = async () => {
    setReprocessButton({ text: "Triggering...", isDisabled: true });
    try {
      await props.reprocessDeferred?.();
      setReprocessButton({ text: "Triggered", isDisabled: true });
    } catch {
      setReprocessButton({ text: "Failed", isDisabled: false });
    }
  };

  const skipEventClick = async () => {
    setSkipButton({ text: "Skipping...", isDisabled: true });
    try {
      await props.skipEvent(
        props.eventDetails?.eventId!,
        props.eventDetails?.lastMessageId!,
      );
      setSkipButton({ text: "Skipped", isDisabled: true });
      notifySuccess("Skipped.");
    } catch (caught) {
      notifyError(describeCommandError(caught, "skip"));
      setSkipButton(
        isStaleCommand(caught)
          ? { text: "Changed - refresh", isDisabled: true }
          : { text: "Skip failed", isDisabled: false },
      );
    }
  };

  const resubmitEventClick = async () => {
    setResubmitButton({ text: "Resubmitting...", isDisabled: true });
    try {
      await props.resubmitEvent(
        props.eventDetails?.eventId!,
        props.eventDetails?.lastMessageId!,
      );
      setResubmitButton({ text: "Resubmitted", isDisabled: true });
      notifySuccess("Resubmitted.");
    } catch (caught) {
      notifyError(describeCommandError(caught, "resubmit"));
      setResubmitButton(
        isStaleCommand(caught)
          ? { text: "Changed - refresh", isDisabled: true }
          : { text: "Resubmit failed", isDisabled: false },
      );
    }
  };

  const deleteEventClick = async () => {
    setShowDeleteConfirm(false);
    setDeleteButton({ text: "Deleting...", isDisabled: true });
    try {
      await props.deleteEvent?.();
      setDeleteButton({ text: "Deleted", isDisabled: true });
    } catch {
      setDeleteButton({ text: "Delete failed", isDisabled: false });
    }
  };

  const resubmitEventWithChangesClick = async () => {
    onClose();
    setResubmitWithChangesButton({ text: "Resubmitting...", isDisabled: true });
    const body: api.ResubmitWithChanges = api.ResubmitWithChanges.fromJS({
      eventTypeId: eventTypeIdValue,
      eventContent: textAreaValue,
    });
    try {
      await props.resubmitEventWithChanges(
        props.eventDetails?.eventId!,
        props.eventDetails?.lastMessageId!,
        body,
      );
      setResubmitWithChangesButton({ text: "Resubmitted", isDisabled: true });
      notifySuccess("Resubmitted.");
    } catch (caught) {
      notifyError(describeCommandError(caught, "resubmit"));
      setResubmitWithChangesButton(
        isStaleCommand(caught)
          ? { text: "Changed - refresh", isDisabled: true }
          : { text: "Resubmit failed", isDisabled: false },
      );
    }
  };

  return (
    <div className="w-full">
      {heroState && (
        <div className="mb-5 overflow-hidden rounded-nb-lg border border-border">
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
      <h4 className="text-lg font-semibold flex items-center gap-4">
        Details
        {showFailedActions && (
          <div className="flex gap-2">
            <Button
              size="xs"
              colorScheme="blue"
              disabled={resubmitButton.isDisabled || attemptActedOn}
              onClick={resubmitEventClick}
            >
              {resubmitButton.text}
            </Button>
            <Button
              size="xs"
              colorScheme="green"
              disabled={resubmitWithChangesButton.isDisabled || attemptActedOn}
              onClick={onOpen}
            >
              {resubmitWithChangesButton.text}
            </Button>
            <Button
              size="xs"
              colorScheme="red"
              variant="outline"
              disabled={skipButton.isDisabled || attemptActedOn}
              onClick={skipEventClick}
            >
              {skipButton.text}
            </Button>
            {props.deleteEvent && isFailedMessage(props.eventDetails?.resolutionStatus) && (
              <Button
                size="xs"
                colorScheme="red"
                disabled={deleteButton.isDisabled || attemptActedOn}
                onClick={() => setShowDeleteConfirm(true)}
              >
                {deleteButton.text}
              </Button>
            )}
          </div>
        )}
        {isDeferredMessage(props.eventDetails?.resolutionStatus) && props.reprocessDeferred && (
          <Button
            size="xs"
            colorScheme="blue"
            disabled={reprocessButton.isDisabled}
            onClick={reprocessDeferredClick}
          >
            {reprocessButton.text}
          </Button>
        )}
      </h4>
      <br />
      {/* Timing bar — queue · processing · ack as a stacked horizontal bar.
          Operators glance once instead of scanning three KV rows (rec §09).

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
        props.eventDetails?.processingTimeMs !== undefined) && (() => {
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
            className="mb-4"
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
                {formatDurationMs(totalMs)}{" "}
                ·{" "}
                <span
                  className={
                    isFailedMessage(props.eventDetails?.resolutionStatus)
                      ? "text-status-danger"
                      : "text-status-success"
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
        <PropertySection title="Identifiers">
          <PropertyRow
            label="EventId"
            value={props.eventDetails?.eventId}
            mono
          />
          <PropertyRow
            label="SessionId"
            value={props.eventDetails?.sessionId}
            mono
          />
          <PropertyRow
            label="MessageId"
            value={props.eventDetails?.lastMessageId}
            mono
          />
          {props.eventDetails?.originatingMessageId && (
            <PropertyRow
              label="OriginatingMessageId"
              value={props.eventDetails.originatingMessageId}
              mono
            />
          )}
          {/* Spec 006: render only when the event is deferred AND a blocker
              GUID was parsed AND we have an endpointId to build the link with.
              All three are required — endpointId is part of the route, and a
              row without a valid link would mislead operators. */}
          {props.eventDetails?.resolutionStatus?.toLowerCase() === "deferred" &&
            props.blockedByEventId &&
            props.eventDetails?.endpointId && (
              <PropertyRow
                label="Blocked by"
                value={
                  <Link
                    to={`/Message/Index/${props.eventDetails.endpointId}/${props.blockedByEventId}/0`}
                    className="text-status-info font-semibold no-underline hover:underline"
                  >
                    {props.blockedByEventId}
                  </Link>
                }
                mono
              />
            )}
        </PropertySection>

        <PropertySection title="Details">
          <PropertyRow
            label="EventTypeId"
            value={
              props.eventDetails?.eventTypeId && (
                <Link
                  to={`/EventTypes/Details/${props.eventDetails.eventTypeId}`}
                  className="text-status-info font-semibold no-underline hover:underline"
                >
                  {props.eventDetails.eventTypeId}
                </Link>
              )
            }
          />
          <PropertyRow
            label="From"
            value={
              routing.from ? (
                <Link
                  to={`/Endpoints/Details/${routing.from}`}
                  className="text-status-info font-semibold no-underline hover:underline"
                >
                  {routing.from}
                </Link>
              ) : (
                "—"
              )
            }
          />
          <PropertyRow
            label="To"
            value={
              routing.to ? (
                <Link
                  to={`/Endpoints/Details/${routing.to}`}
                  className="text-status-info font-semibold no-underline hover:underline"
                >
                  {routing.to}
                </Link>
              ) : (
                "—"
              )
            }
          />
          <PropertyRow
            label="Status"
            value={
              <span className="inline-flex items-center gap-2">
                <Badge
                  variant={statusToBadgeVariant(
                    props.eventDetails?.resolutionStatus,
                  )}
                  size="sm"
                >
                  {formatResolutionStatus(
                    props.eventDetails?.resolutionStatus,
                    props.eventDetails?.reason,
                  ) ?? "—"}
                </Badge>
                {isPendingHandoff(
                  props.eventDetails?.resolutionStatus,
                  props.eventDetails?.pendingSubStatus,
                ) && (
                  <Badge variant="info" size="sm">
                    Awaiting external
                  </Badge>
                )}
              </span>
            }
          />
          <PropertyRow
            label="Enqueued"
            value={formatMoment(props.eventDetails?.enqueuedTimeUtc)}
            mono
          />
          <PropertyRow
            label="Queue Time"
            value={formatDurationMs(props.eventDetails?.queueTimeMs)}
            mono
          />
          <PropertyRow
            label="Processing Time"
            value={
              (props.eventDetails?.processingTimeMs ?? 0) >=
              SLOW_PROCESSING_MS ? (
                <Badge variant="warning" size="sm">
                  {formatDurationMs(props.eventDetails?.processingTimeMs)}
                </Badge>
              ) : (
                <span className="font-mono text-[12px] tabular-nums">
                  {formatDurationMs(props.eventDetails?.processingTimeMs)}
                </span>
              )
            }
          />
        </PropertySection>

        {(props.eventDetails?.handoffReason ||
          props.eventDetails?.externalJobId ||
          props.eventDetails?.expectedBy) && (
          <PropertySection title="Handoff details">
            {props.eventDetails?.handoffReason && (
              <PropertyRow
                label="Reason"
                value={props.eventDetails.handoffReason}
              />
            )}
            {props.eventDetails?.externalJobId && (
              <PropertyRow
                label="External Job Id"
                value={props.eventDetails.externalJobId}
                mono
              />
            )}
            {props.eventDetails?.expectedBy && (
              <PropertyRow
                label="Expected By"
                value={formatMoment(props.eventDetails.expectedBy)}
                mono
              />
            )}
          </PropertySection>
        )}
      </PropertyList>
      <br />
      {isFailedMessage(props.eventDetails?.resolutionStatus) && (
        <>
          <h4 className="text-lg font-semibold mt-4">Error</h4>
          <br />
          {props.eventDetails?.messageContent?.errorContent && (
            !isDeadletteredMessage(props.eventDetails?.resolutionStatus) ? (
              <>
                <div className="bg-red-100 border border-red-400 text-red-800 dark:bg-red-950/40 dark:border-red-900/60 dark:text-red-200 p-4 rounded-sm text-sm">
                  {props.eventDetails?.messageContent?.errorContent?.errorText}
                </div>
                <table className="text-sm">
                  <tbody>
                    <tr className="hover:bg-accent">
                      <td className="py-2 pr-4">
                        <b>Exception</b>
                      </td>
                      <td className="py-2">
                        {
                          props.eventDetails?.messageContent?.errorContent
                            ?.exceptionStackTrace
                        }
                      </td>
                    </tr>
                  </tbody>
                </table>
              </>
            ) : (
              <table className="text-sm">
                <tbody>
                  <tr className="hover:bg-accent">
                    <td className="py-2 pr-4">
                      <b>DeadLetter Reason</b>
                    </td>
                    <td className="py-2">
                      {props.eventDetails?.deadLetterReason}
                    </td>
                  </tr>
                  <tr className="hover:bg-accent">
                    <td className="py-2 pr-4">
                      <b>DeadLetter Error Description</b>
                    </td>
                    <td className="py-2">
                      {props.eventDetails?.deadLetterErrorDescription}
                    </td>
                  </tr>
                </tbody>
              </table>
            )
          )}
          <br />
          {failureOccurrences.length > 0 && (
            <div className="space-y-3">
              {failureOccurrences.map((occurrence, index) => (
                <IntelligenceCard
                  key={`${occurrence.eventId}/${occurrence.messageId}`}
                  endpointId={props.eventDetails?.endpointId!}
                  eventId={occurrence.eventId}
                  messageId={occurrence.messageId}
                  resolutionStatus={props.eventDetails?.resolutionStatus}
                  occurrenceLabel={`occurrence ${index + 1}`}
                />
              ))}
            </div>
          )}
        </>
      )}

      {/* Payload — the original event content (the latest request message's
          payload, falling back to the event's own). For a hand-off this is
          the EventRequest, not the terminal settlement message which carries
          no original payload. */}
      {resubmitPayload && isRedacted && (
        <div className="mt-4 flex items-center gap-2.5 bg-card border border-border rounded-nb-md px-4 py-3">
          <svg className="w-4 h-4 shrink-0 text-muted-foreground" viewBox="0 0 16 16" fill="none">
            <rect x="3" y="7" width="10" height="6" rx="1.4" stroke="currentColor" strokeWidth="1.4" />
            <path d="M5.5 7V5.5a2.5 2.5 0 0 1 5 0V7" stroke="currentColor" strokeWidth="1.4" />
          </svg>
          <span className="text-[13px] text-muted-foreground">
            The payload is hidden — viewing raw event payloads requires the PII
            Reader role. A site Owner can grant it on the Access Control page.
          </span>
        </div>
      )}

      {resubmitPayload && !isRedacted && (
        <div className="mt-4">
          {/* Rendered as-is. Sensitive values are already masked server-side for
              callers without the PII Reader role, so there is nothing here to
              blur or reveal — and blurring would only hide the non-sensitive
              fields an operator needs to triage. */}
          <CodeBlock
            title="Payload"
            subtitle="application/json"
            // Copies what is rendered, which is the server-masked payload for a
            // caller without the PII Reader role — the button never reaches past
            // redaction to the raw event content.
            actions={<CopyButton text={formattedPayload} describes="Payload" />}
            linkifyGuid={(_guid) =>
              // Clicking a GUID in the payload jumps to the Messages search
              // pre-filtered to that ID — IDs become first-class navigation
              // bridges between pages (design rec §09 code).
              `/Messages?eventId=${_guid}`
            }
          >
            {formattedPayload}
          </CodeBlock>
        </div>
      )}

      {/* Handoff result — the external system's completion details, when the
          handler supplied any on CompleteHandoff (a HandoffCompletedRequest's
          eventContent). Distinct from the event payload above. */}
      {handoffResult && (
        <div className="mt-4">
          <CodeBlock title="Handoff result" subtitle="application/json">
            {formattedHandoffResult}
          </CodeBlock>
        </div>
      )}

      <Modal isOpen={isOpen} onClose={onClose} size="2xl">
        <ModalHeader onClose={onClose}>Resubmit with changes</ModalHeader>
        <ModalBody>
          <div className="mb-4">
            <label className="block text-sm font-medium text-foreground mb-2">
              Original event:
            </label>
            <pre className="bg-muted p-4 rounded-sm text-sm overflow-x-auto max-h-96">
              {formattedPayload}
            </pre>
          </div>
          <div className="mb-4">
            <label className="block text-sm font-medium text-foreground mb-2">
              Event type:
            </label>
            <Select
              onChange={handleEventTypeIdChange}
              defaultValue={props.eventDetails?.eventTypeId}
            >
              {props.eventTypes.map((et) => (
                <option key={et.id} value={et.id}>
                  {et.id}
                </option>
              ))}
            </Select>
          </div>
          <div>
            <label className="block text-sm font-medium text-foreground mb-2">
              Modified event:
            </label>
            <Textarea
              value={textAreaValue}
              onChange={handleInputChange}
              className="min-h-[300px]"
            />
          </div>
        </ModalBody>
        <ModalFooter>
          <Button variant="outline" onClick={onClose}>
            Close
          </Button>
          <Button onClick={resubmitEventWithChangesClick}>Resubmit</Button>
        </ModalFooter>
      </Modal>

      {/* Comment Section */}
      <CommentSection eventId={props.eventDetails?.eventId} onCommentAdded={props.onCommentAdded} />

      <Modal isOpen={showDeleteConfirm} onClose={() => setShowDeleteConfirm(false)}>
        <ModalHeader>Delete Event</ModalHeader>
        <ModalBody>
          <p className="text-sm">
            This will permanently delete the event from storage.{" "}
            <span className="font-semibold text-red-600 dark:text-red-400">This action cannot be undone.</span>
          </p>
        </ModalBody>
        <ModalFooter>
          <Button variant="outline" onClick={() => setShowDeleteConfirm(false)}>
            Cancel
          </Button>
          <Button colorScheme="red" onClick={deleteEventClick}>
            Delete
          </Button>
        </ModalFooter>
      </Modal>

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

function CommentSection({ eventId, onCommentAdded }: { eventId?: string; onCommentAdded?: () => void }) {
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
      } catch { /* fallback */ }

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
    <div className="mt-6 border-t pt-4">
      <h5 className="text-base font-semibold mb-2">Add Comment</h5>
      <Textarea
        value={comment}
        onChange={(e) => { setComment(e.target.value); setSaved(false); }}
        placeholder="Enter a comment..."
        rows={3}
      />
      <div className="flex items-center gap-2 mt-2">
        <Button size="sm" colorScheme="primary" onClick={submit} disabled={saving || !comment.trim()}>
          {saving ? "Saving..." : "Save"}
        </Button>
        {saved && <span className="text-sm text-green-600 dark:text-green-400">Saved</span>}
      </div>
    </div>
  );
}
