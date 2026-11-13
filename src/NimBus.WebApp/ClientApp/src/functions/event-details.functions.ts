import * as api from "api-client";
import type { BadgeVariant } from "components/ui/badge";

/** Statuses that end in an operator decision (resubmit, skip, or delete). */
export function isFailedStatus(status: string | undefined): boolean {
  const lower = status?.toLowerCase();
  return (
    lower === "failed" || lower === "unsupported" || lower === "deadlettered"
  );
}

/** A pending event waiting for an external system to settle a handoff. */
export function isPendingHandoff(
  status: string | undefined,
  pendingSubStatus: string | null | undefined,
): boolean {
  return (
    status?.toLowerCase() === "pending" &&
    pendingSubStatus?.toLowerCase() === "handoff"
  );
}

/** Maps a resolution status to its Badge variant. */
export function statusToBadgeVariant(status: string | undefined): BadgeVariant {
  switch (status?.toLowerCase()) {
    case "completed":
    case "failed":
    case "deferred":
    case "deadlettered":
    case "skipped":
    case "unsupported":
    case "pending":
      return status!.toLowerCase() as BadgeVariant;
    default:
      return "default";
  }
}

// Lowercase a messageType so comparisons tolerate transport casing.
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

/**
 * Routing lineage shown for an event. Response messages (ResolutionResponse,
 * ErrorResponse, SkipResponse, …) are addressed `To` the platform Resolver — an
 * internal auditing hop, not a meaningful routing leg. The operator-relevant
 * lineage is "where the event came from → which endpoint handled it". The
 * publisher lives on the originating EventRequest message in the history; the
 * response's own `from` is the handling endpoint. So for responses this returns
 * From = publisher and To = handling endpoint, and never the Resolver.
 * Non-response messages keep their literal from/to.
 */
export function getRoutingFromTo(
  event:
    | Pick<
        api.Event,
        | "from"
        | "to"
        | "originatingFrom"
        | "originatingMessageId"
        | "messageType"
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
