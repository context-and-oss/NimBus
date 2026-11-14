import * as api from "api-client";

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
        typeof m.messageType === "string" &&
        PAYLOAD_REQUEST_TYPES.has(m.messageType.toLowerCase()) &&
        !!m.eventContent,
    )
    .sort(
      (a, b) =>
        (b.enqueuedTimeUtc?.valueOf() ?? 0) -
        (a.enqueuedTimeUtc?.valueOf() ?? 0),
    )[0]?.eventContent;
}
