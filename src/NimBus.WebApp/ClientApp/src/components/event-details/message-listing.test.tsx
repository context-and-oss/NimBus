import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import moment from "moment";
import * as api from "api-client";
import { notifyError, notifySuccess } from "functions/notifications.functions";
import MessageListing, {
  diffMs,
  getHandoffResult,
  getFailureOccurrences,
  getHistoryQueueTimeMs,
  getResubmitPayload,
  getRoutingFromTo,
} from "./message-listing";

// MessageListing internally constructs an api.Client and queries `getMe()` on
// mount to label handoff actions. Stub the toast provider and the api Client
// so the component tests stay hermetic.
vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast: () => {} }),
}));
vi.mock("functions/notifications.functions", () => ({
  notifySuccess: vi.fn(),
  notifyError: vi.fn(),
  notifyWarning: vi.fn(),
  notifyInfo: vi.fn(),
  notifyWithUndo: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual = await vi.importActual<typeof import("api-client")>("api-client");
  class MockClient {
    getMe() {
      return Promise.resolve(undefined);
    }
    postMessageAudit() {
      return Promise.resolve(undefined);
    }
  }
  return {
    ...actual,
    Client: MockClient,
    CookieAuth: () => ({}),
  };
});

const originalFetch = globalThis.fetch;
afterEach(() => {
  cleanup();
  globalThis.fetch = originalFetch;
});

function stubMe() {
  globalThis.fetch = vi.fn().mockResolvedValue(
    new Response(JSON.stringify({ name: "tester" }), {
      status: 200,
      headers: { "Content-Type": "application/json" },
    }),
  ) as unknown as typeof fetch;
}

function msg(
  type: api.MessageType | string,
  timestampIso: string,
): api.Message {
  return {
    messageType: type as api.MessageType,
    enqueuedTimeUtc: moment(timestampIso),
  } as api.Message;
}

// ---------------------------------------------------------------------------
// FR-040 - unit tests for getHistoryQueueTimeMs (7 enumerated cases).
// ---------------------------------------------------------------------------
describe("getHistoryQueueTimeMs (spec 005)", () => {
  it("case 1 - returns undefined for empty / undefined messages", () => {
    expect(getHistoryQueueTimeMs(undefined)).toBeUndefined();
    expect(getHistoryQueueTimeMs(null as unknown as api.Message[])).toBeUndefined();
    expect(getHistoryQueueTimeMs([])).toBeUndefined();
  });

  it("case 2 - returns undefined for a lifecycle with no deferral or pending-handoff", () => {
    const history: api.Message[] = [
      msg(api.MessageType.EventRequest, "2026-05-28T10:00:00.000Z"),
      msg(api.MessageType.ResolutionResponse, "2026-05-28T10:00:00.045Z"),
    ];
    expect(getHistoryQueueTimeMs(history)).toBeUndefined();
  });

  it("case 3 - single deferral round-trip: span = latest end-anchor - first EventRequest", () => {
    const history: api.Message[] = [
      msg(api.MessageType.EventRequest, "2026-05-28T10:00:00.000Z"),
      msg(api.MessageType.DeferralResponse, "2026-05-28T10:00:00.500Z"),
      msg(api.MessageType.EventRequest, "2026-05-28T10:00:30.000Z"), // replay
      msg(api.MessageType.ResolutionResponse, "2026-05-28T10:00:30.050Z"),
    ];
    expect(getHistoryQueueTimeMs(history)).toBe(30_050);
  });

  it("case 4 - pending-handoff that resolves via HandoffCompletedRequest", () => {
    const history: api.Message[] = [
      msg(api.MessageType.EventRequest, "2026-05-28T11:00:00.000Z"),
      msg(api.MessageType.PendingHandoffResponse, "2026-05-28T11:00:00.040Z"),
      msg(api.MessageType.HandoffCompletedRequest, "2026-05-28T11:05:00.000Z"),
      msg(api.MessageType.ResolutionResponse, "2026-05-28T11:05:00.080Z"),
    ];
    expect(getHistoryQueueTimeMs(history)).toBe(300_080);
  });

  it("case 5 - still-pending handoff: end anchor is the latest PendingHandoffResponse", () => {
    const history: api.Message[] = [
      msg(api.MessageType.EventRequest, "2026-05-28T12:00:00.000Z"),
      msg(api.MessageType.PendingHandoffResponse, "2026-05-28T12:02:00.000Z"),
    ];
    expect(getHistoryQueueTimeMs(history)).toBe(120_000);
  });

  it("case 6 - partial history (no EventRequest) falls back to earliest message as start", () => {
    const history: api.Message[] = [
      msg(api.MessageType.DeferralResponse, "2026-05-28T13:00:00.000Z"),
      msg(api.MessageType.ResolutionResponse, "2026-05-28T13:00:10.000Z"),
    ];
    expect(getHistoryQueueTimeMs(history)).toBe(10_000);
  });

  it("case 7 - clock-skewed history (end anchor before start) -> undefined", () => {
    const history: api.Message[] = [
      msg(api.MessageType.EventRequest, "2026-05-28T14:00:00.500Z"),
      msg(api.MessageType.DeferralResponse, "2026-05-28T13:59:59.900Z"),
    ];
    expect(getHistoryQueueTimeMs(history)).toBeUndefined();
  });

  it("normalises messageType casing so transport differences do not change the result", () => {
    const history: api.Message[] = [
      msg("EventRequest", "2026-05-28T15:00:00.000Z"),
      msg("DEFERRALRESPONSE", "2026-05-28T15:00:05.000Z"),
    ];
    expect(getHistoryQueueTimeMs(history)).toBe(5_000);
  });

  it("ignores unknown forward-compat messageType values without throwing", () => {
    const history: api.Message[] = [
      msg("eventRequest", "2026-05-28T16:00:00.000Z"),
      msg("someFutureUnknownType" as api.MessageType, "2026-05-28T16:00:01.000Z"),
      msg("deferralResponse", "2026-05-28T16:00:02.000Z"),
    ];
    expect(getHistoryQueueTimeMs(history)).toBe(2_000);
  });
});

describe("getFailureOccurrences (Spec 033)", () => {
  it("returns each stored failure row, including a dead-letter row without error content", () => {
    const event = { eventId: "event-1", lastMessageId: "last" } as unknown as api.Event;
    const rows = [
      { eventId: "event-1", messageId: "failure-1", messageType: "ErrorResponse", errorContent: "details" },
      { eventId: "event-1", messageId: "failure-2", messageType: "DeadLetter", deadLetterReason: "expired" },
    ] as unknown as api.Message[];

    expect(getFailureOccurrences(event, rows)).toEqual([
      { eventId: "event-1", messageId: "failure-1" },
      { eventId: "event-1", messageId: "failure-2" },
    ]);
  });

  it("falls back to the event last message only when no failure row is available", () => {
    expect(getFailureOccurrences(
      { eventId: "event-1", lastMessageId: "last", resolutionStatus: "DeadLettered" } as unknown as api.Event,
      [],
    )).toEqual([{ eventId: "event-1", messageId: "last" }]);
  });
});

// ---------------------------------------------------------------------------
// FR-041 - timing bar uses overridden value on a deferred fixture.
// ---------------------------------------------------------------------------
describe("MessageListing timing bar (spec 005)", () => {
  function eventFixture(overrides: Partial<api.IEvent>): api.Event {
    return Object.assign(new api.Event(), {
      eventId: "evt-1",
      sessionId: "sess-1",
      lastMessageId: "msg-1",
      endpointId: "ep-1",
      eventTypeId: "Demo.Type",
      from: "ep-1",
      resolutionStatus: "Completed",
      enqueuedTimeUtc: moment("2026-05-28T10:00:30.000Z"),
      updatedAt: moment("2026-05-28T10:00:30.045Z"),
      queueTimeMs: 12,
      processingTimeMs: 45,
      ...overrides,
    });
  }

  const renderListing = (
    event: api.Event,
    messages: api.Message[] | undefined,
  ) =>
    render(
      <MemoryRouter>
        <MessageListing
          eventDetails={event}
          messages={messages}
          eventTypes={[]}
          skipEvent={async () => {}}
          resubmitEvent={async () => {}}
          resubmitEventWithChanges={async () => {}}
        />
      </MemoryRouter>,
    );

  it("uses the history-derived value for a deferred lifecycle", () => {
    stubMe();
    const deferredHistory: api.Message[] = [
      msg(api.MessageType.EventRequest, "2026-05-28T10:00:00.000Z"),
      msg(api.MessageType.DeferralResponse, "2026-05-28T10:00:00.500Z"),
      msg(api.MessageType.EventRequest, "2026-05-28T10:00:30.000Z"),
      msg(api.MessageType.ResolutionResponse, "2026-05-28T10:00:30.045Z"),
    ];
    renderListing(eventFixture({}), deferredHistory);

    expect(screen.getByText(/30\.05s/)).toBeTruthy();
    expect(screen.getByText(/30\.09s/)).toBeTruthy();
  });

  it("uses the row-level queueTimeMs for a non-deferred lifecycle", () => {
    stubMe();
    const linearHistory: api.Message[] = [
      msg(api.MessageType.EventRequest, "2026-05-28T10:00:30.000Z"),
      msg(api.MessageType.ResolutionResponse, "2026-05-28T10:00:30.045Z"),
    ];
    renderListing(eventFixture({}), linearHistory);

    expect(screen.getByText(/Queue\s+12ms/)).toBeTruthy();
  });

  it("preserves a measured zero in queueTimeMs (?? not || for the fallback chain)", () => {
    stubMe();
    renderListing(eventFixture({ queueTimeMs: 0 }), []);
    expect(screen.getByText(/Queue\s+0ms/)).toBeTruthy();
  });

  it("labels an exact duplicate skip in both visible status locations", () => {
    stubMe();
    renderListing(
      eventFixture({
        resolutionStatus: "Skipped",
        reason: "DuplicateDetected",
      }),
      [],
    );

    expect(screen.getAllByText("Skipped (duplicate)")).toHaveLength(2);
  });

  it("leaves an ordinary skipped status unchanged", () => {
    stubMe();
    renderListing(
      eventFixture({
        resolutionStatus: "Skipped",
        reason: "Operator requested skip",
      }),
      [],
    );

    expect(screen.getAllByText("Skipped")).toHaveLength(2);
    expect(screen.queryByText("Skipped (duplicate)")).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// Helper: diffMs is the third-fallback timing source.
// ---------------------------------------------------------------------------
describe("diffMs (spec 005 third-fallback)", () => {
  it("returns the millisecond delta between two moment values", () => {
    expect(
      diffMs(
        moment("2026-05-28T10:00:00.500Z"),
        moment("2026-05-28T10:00:00.000Z"),
      ),
    ).toBe(500);
  });

  it("returns undefined when either side is missing", () => {
    expect(diffMs(undefined, moment())).toBeUndefined();
    expect(diffMs(moment(), undefined)).toBeUndefined();
  });
});

// ---------------------------------------------------------------------------
// Spec 006 — render-conditionality for the "Blocked by" PropertyRow.
// ---------------------------------------------------------------------------
const SPEC_006_ENDPOINT_ID = "endpoint-a";
const SPEC_006_BLOCKED_BY = "cce3b12a-1234-5678-9abc-def012345678";

function spec006BuildEvent(overrides: Partial<api.Event>): api.Event {
  return {
    eventId: "11111111-1111-1111-1111-111111111111",
    sessionId: "session-1",
    lastMessageId: "22222222-2222-2222-2222-222222222222",
    endpointId: SPEC_006_ENDPOINT_ID,
    ...overrides,
  } as unknown as api.Event;
}

function spec006RenderListing(eventDetails: api.Event, blockedByEventId?: string) {
  return render(
    <MemoryRouter>
      <MessageListing
        eventDetails={eventDetails}
        eventTypes={[]}
        skipEvent={vi.fn()}
        resubmitEvent={vi.fn()}
        resubmitEventWithChanges={vi.fn()}
        blockedByEventId={blockedByEventId}
      />
    </MemoryRouter>,
  );
}

describe("MessageListing — Blocked by row (spec 006)", () => {
  beforeEach(() => {
    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      writable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: false,
        media: query,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      })),
    });
  });

  it("renders a 'Blocked by' link for a deferred event with a valid blockedByEventId", () => {
    spec006RenderListing(
      spec006BuildEvent({ resolutionStatus: "Deferred" }),
      SPEC_006_BLOCKED_BY,
    );

    expect(screen.getByText("Blocked by")).toBeTruthy();
    const link = screen.getByRole("link", { name: SPEC_006_BLOCKED_BY });
    expect(link.getAttribute("href")).toBe(
      `/Message/Index/${SPEC_006_ENDPOINT_ID}/${SPEC_006_BLOCKED_BY}/0`,
    );
  });

  it("does not render 'Blocked by' for a deferred event when blockedByEventId is omitted", () => {
    spec006RenderListing(spec006BuildEvent({ resolutionStatus: "Deferred" }), undefined);
    expect(screen.queryByText("Blocked by")).toBeNull();
  });

  it("does not render 'Blocked by' for a non-deferred event even when blockedByEventId is provided", () => {
    spec006RenderListing(spec006BuildEvent({ resolutionStatus: "Completed" }), SPEC_006_BLOCKED_BY);
    expect(screen.queryByText("Blocked by")).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// Original-request payload resolution (ported from DIS 678f1cb3 / c555093d).
// ---------------------------------------------------------------------------
function historyMsg(props: {
  messageType: api.MessageType | string;
  enqueuedTimeUtc?: string;
  eventContent?: string;
}): api.Message {
  return {
    messageType: props.messageType as api.MessageType,
    enqueuedTimeUtc: props.enqueuedTimeUtc
      ? moment(props.enqueuedTimeUtc)
      : undefined,
    eventContent: props.eventContent,
  } as api.Message;
}

describe("getResubmitPayload", () => {
  it("returns the original EventRequest payload for a failed hand-off", () => {
    // The terminal ErrorResponse/HandoffFailedRequest carry no event content;
    // resubmit must replay the EventRequest's payload.
    const result = getResubmitPayload([
      historyMsg({
        messageType: api.MessageType.EventRequest,
        enqueuedTimeUtc: "2026-06-01T23:36:50.000Z",
        eventContent: '{"Fail":true}',
      }),
      historyMsg({
        messageType: api.MessageType.PendingHandoffResponse,
        enqueuedTimeUtc: "2026-06-01T23:36:51.000Z",
        eventContent: '{"Fail":true}',
      }),
      historyMsg({
        messageType: api.MessageType.HandoffFailedRequest,
        enqueuedTimeUtc: "2026-06-01T23:37:02.000Z",
      }),
      historyMsg({
        messageType: api.MessageType.ErrorResponse,
        enqueuedTimeUtc: "2026-06-01T23:37:02.000Z",
      }),
    ]);

    expect(result).toBe('{"Fail":true}');
  });

  it("prefers the latest payload-carrying request (e.g. a prior resubmission)", () => {
    const result = getResubmitPayload([
      historyMsg({
        messageType: api.MessageType.EventRequest,
        enqueuedTimeUtc: "2026-06-01T10:00:00.000Z",
        eventContent: '{"v":1}',
      }),
      historyMsg({
        messageType: api.MessageType.ResubmissionRequest,
        enqueuedTimeUtc: "2026-06-01T11:00:00.000Z",
        eventContent: '{"v":2}',
      }),
    ]);

    expect(result).toBe('{"v":2}');
  });

  it("ignores response messages even when they carry content", () => {
    const result = getResubmitPayload([
      historyMsg({
        messageType: api.MessageType.EventRequest,
        enqueuedTimeUtc: "2026-06-01T10:00:00.000Z",
        eventContent: '{"req":true}',
      }),
      historyMsg({
        messageType: api.MessageType.ErrorResponse,
        enqueuedTimeUtc: "2026-06-01T10:00:01.000Z",
        eventContent: '{"resp":true}',
      }),
    ]);

    expect(result).toBe('{"req":true}');
  });

  it("skips payload-less requests and normalises messageType casing", () => {
    const result = getResubmitPayload([
      historyMsg({
        messageType: "EVENTREQUEST",
        enqueuedTimeUtc: "2026-06-01T10:00:00.000Z",
        eventContent: '{"req":true}',
      }),
      historyMsg({
        messageType: api.MessageType.RetryRequest,
        enqueuedTimeUtc: "2026-06-01T10:05:00.000Z",
        // No eventContent — must not shadow the earlier payload.
      }),
    ]);

    expect(result).toBe('{"req":true}');
  });

  it("returns undefined without history (caller falls back to event payload)", () => {
    expect(getResubmitPayload(undefined)).toBeUndefined();
    expect(getResubmitPayload([])).toBeUndefined();
  });
});

describe("getHandoffResult", () => {
  it("returns the completed hand-off's external result details", () => {
    const result = getHandoffResult([
      historyMsg({
        messageType: api.MessageType.EventRequest,
        enqueuedTimeUtc: "2026-06-01T10:00:00.000Z",
        eventContent: '{"Fail":false}',
      }),
      historyMsg({
        messageType: api.MessageType.HandoffCompletedRequest,
        enqueuedTimeUtc: "2026-06-01T10:05:00.000Z",
        eventContent: '{"batchId":"abc","rows":42}',
      }),
    ]);

    expect(result).toBe('{"batchId":"abc","rows":42}');
  });

  it("returns undefined when the completed hand-off carried no details", () => {
    expect(
      getHandoffResult([
        historyMsg({
          messageType: api.MessageType.HandoffCompletedRequest,
          enqueuedTimeUtc: "2026-06-01T10:05:00.000Z",
        }),
      ]),
    ).toBeUndefined();
  });

  it("returns undefined for a failed hand-off (no result block)", () => {
    expect(
      getHandoffResult([
        historyMsg({
          messageType: api.MessageType.EventRequest,
          eventContent: '{"x":1}',
        }),
        historyMsg({ messageType: api.MessageType.HandoffFailedRequest }),
        historyMsg({ messageType: api.MessageType.ErrorResponse }),
      ]),
    ).toBeUndefined();
  });

  it("returns undefined without history", () => {
    expect(getHandoffResult(undefined)).toBeUndefined();
    expect(getHandoffResult([])).toBeUndefined();
  });
});

// ---------------------------------------------------------------------------
// Routing lineage (ported from DIS 5adc92b7) — response messages render
// From = originating publisher, To = handling endpoint, dropping the internal
// Resolver hop.
// ---------------------------------------------------------------------------
describe("getRoutingFromTo", () => {
  function lineageEvent(props: Partial<api.IEvent>): api.Event {
    return api.Event.fromJS(props);
  }

  function lineageMsg(props: Partial<api.IMessage>): api.Message {
    return api.Message.fromJS(props);
  }

  it("shows publisher → handling endpoint for a response, dropping the Resolver", () => {
    // Response handled by Bob; the originating EventRequest was published by
    // Alice (carried on the request's originatingFrom). The response's own
    // `from`/`originatingFrom` only know about the handling endpoint (Bob).
    const result = getRoutingFromTo(
      lineageEvent({
        messageType: "ResolutionResponse",
        from: "Bob",
        to: "Resolver",
        originatingFrom: "Bob",
        originatingMessageId: "AliceSaidHello-1",
      }),
      [
        lineageMsg({
          messageId: "AliceSaidHello-1",
          messageType: "EventRequest",
          from: "Bob",
          to: "Bob",
          originatingFrom: "Alice",
        }),
      ],
    );

    expect(result).toEqual({ from: "Alice", to: "Bob" });
  });

  it("uses the originating request's literal from when it has no originatingFrom", () => {
    const result = getRoutingFromTo(
      lineageEvent({
        messageType: "ResolutionResponse",
        from: "Bob",
        to: "Resolver",
        originatingMessageId: "AliceSaidHello-1",
      }),
      [
        lineageMsg({
          messageId: "AliceSaidHello-1",
          messageType: "EventRequest",
          from: "Alice",
          to: "Bob",
        }),
      ],
    );

    expect(result).toEqual({ from: "Alice", to: "Bob" });
  });

  it('falls through an empty-string originatingFrom (SQL Server null→"") to from', () => {
    // SQL Server stores a missing originatingFrom as "", which `??` would keep
    // (rendering a blank From). It must fall through to the request's `from`.
    const result = getRoutingFromTo(
      lineageEvent({
        messageType: "ResolutionResponse",
        from: "Bob",
        to: "Resolver",
        originatingFrom: "",
        originatingMessageId: "AliceSaidHello-1",
      }),
      [
        lineageMsg({
          messageId: "AliceSaidHello-1",
          messageType: "EventRequest",
          from: "Alice",
          to: "Bob",
          originatingFrom: "",
        }),
      ],
    );

    expect(result).toEqual({ from: "Alice", to: "Bob" });
  });

  it("treats the 'self' sentinel as no publisher and falls through to from", () => {
    const result = getRoutingFromTo(
      lineageEvent({
        messageType: "ResolutionResponse",
        from: "Bob",
        to: "Resolver",
        originatingMessageId: "AliceSaidHello-1",
      }),
      [
        lineageMsg({
          messageId: "AliceSaidHello-1",
          messageType: "EventRequest",
          from: "Alice",
          to: "Bob",
          originatingFrom: "self",
        }),
      ],
    );

    expect(result).toEqual({ from: "Alice", to: "Bob" });
  });

  it("applies to every response message type", () => {
    const messages = [
      lineageMsg({
        messageId: "AliceSaidHello-1",
        messageType: "EventRequest",
        from: "Bob",
        originatingFrom: "Alice",
      }),
    ];
    for (const messageType of [
      "ErrorResponse",
      "SkipResponse",
      "DeferralResponse",
      "UnsupportedResponse",
      "PendingHandoffResponse",
    ]) {
      expect(
        getRoutingFromTo(
          lineageEvent({
            messageType,
            from: "Bob",
            to: "Resolver",
            originatingMessageId: "AliceSaidHello-1",
          }),
          messages,
        ),
      ).toEqual({ from: "Alice", to: "Bob" });
    }
  });

  it("falls back to the event's own fields when the originating message is missing", () => {
    const result = getRoutingFromTo(
      lineageEvent({
        messageType: "ResolutionResponse",
        from: "Bob",
        to: "Resolver",
        originatingFrom: "Alice",
        originatingMessageId: "AliceSaidHello-1",
      }),
      [],
    );

    expect(result).toEqual({ from: "Alice", to: "Bob" });
  });

  it("falls back to the handling endpoint when nothing else is known", () => {
    const result = getRoutingFromTo(
      lineageEvent({
        messageType: "ResolutionResponse",
        from: "Bob",
        to: "Resolver",
      }),
    );

    expect(result).toEqual({ from: "Bob", to: "Bob" });
  });

  it("leaves non-response messages untouched", () => {
    const result = getRoutingFromTo(
      lineageEvent({
        messageType: "EventRequest",
        from: "Alice",
        to: "Bob",
        originatingFrom: "Alice",
      }),
    );

    expect(result).toEqual({ from: "Alice", to: "Bob" });
  });

  it("handles an undefined event", () => {
    expect(getRoutingFromTo(undefined)).toEqual({ from: "", to: "" });
  });
});

// ---------------------------------------------------------------------------
// Render integration — the Payload block shows the request payload and a
// completed hand-off's details get their own "Handoff result" block.
// ---------------------------------------------------------------------------
describe("MessageListing payload blocks", () => {
  beforeEach(() => {
    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      writable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: false,
        media: query,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      })),
    });
  });

  function renderWithHistory(messages: api.Message[]) {
    stubMe();
    const event = {
      eventId: "evt-1",
      sessionId: "sess-1",
      lastMessageId: "msg-1",
      endpointId: "ep-1",
      eventTypeId: "Demo.Type",
      resolutionStatus: "Completed",
    } as unknown as api.Event;
    return render(
      <MemoryRouter>
        <MessageListing
          eventDetails={event}
          messages={messages}
          eventTypes={[]}
          skipEvent={async () => {}}
          resubmitEvent={async () => {}}
          resubmitEventWithChanges={async () => {}}
        />
      </MemoryRouter>,
    );
  }

  it("renders a 'Handoff result' block for a completed hand-off with details", () => {
    renderWithHistory([
      historyMsg({
        messageType: api.MessageType.EventRequest,
        enqueuedTimeUtc: "2026-06-01T10:00:00.000Z",
        eventContent: '{"Fail":false}',
      }),
      historyMsg({
        messageType: api.MessageType.HandoffCompletedRequest,
        enqueuedTimeUtc: "2026-06-01T10:05:00.000Z",
        eventContent: '{"batchId":"abc"}',
      }),
    ]);

    expect(screen.getByText("Payload")).toBeTruthy();
    expect(screen.getByText("Handoff result")).toBeTruthy();
  });

  it("renders the request payload without a result block when no hand-off completed", () => {
    renderWithHistory([
      historyMsg({
        messageType: api.MessageType.EventRequest,
        enqueuedTimeUtc: "2026-06-01T10:00:00.000Z",
        eventContent: '{"Fail":true}',
      }),
      historyMsg({
        messageType: api.MessageType.ErrorResponse,
        enqueuedTimeUtc: "2026-06-01T10:00:01.000Z",
      }),
    ]);

    expect(screen.getByText("Payload")).toBeTruthy();
    expect(screen.queryByText("Handoff result")).toBeNull();
  });
});

// ---------------------------------------------------------------------------
// Payload derivations are memoised: identical rendered output, but the
// expensive JSON.parse/stringify no longer re-runs on an unrelated re-render
// while the message history is unchanged.
// ---------------------------------------------------------------------------
describe("MessageListing memoised payload derivations", () => {
  // A masked payload (carries "$piiMasked": true) so the "Reveal PII" control
  // renders — clicking it flips internal state and re-renders MessageListing
  // without touching `messages`, the exact "keystroke in a sibling input"
  // scenario the memo guards against.
  const maskedPayload = '{"$piiMasked":true,"ssn":"***"}';

  beforeEach(() => {
    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      writable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: false,
        media: query,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      })),
    });
  });

  function renderMasked() {
    stubMe();
    const event = {
      eventId: "evt-1",
      sessionId: "sess-1",
      lastMessageId: "msg-1",
      endpointId: "ep-1",
      eventTypeId: "Demo.Type",
      resolutionStatus: "Completed",
    } as unknown as api.Event;
    const ui = (
      <MemoryRouter>
        <MessageListing
          eventDetails={event}
          messages={[
            historyMsg({
              messageType: api.MessageType.EventRequest,
              enqueuedTimeUtc: "2026-06-01T10:00:00.000Z",
              eventContent: maskedPayload,
            }),
          ]}
          eventTypes={[]}
          skipEvent={async () => {}}
          resubmitEvent={async () => {}}
          resubmitEventWithChanges={async () => {}}
        />
      </MemoryRouter>
    );
    return { ...render(ui), ui };
  }

  it("renders the masked payload in the clear, with no reveal control", () => {
    const { container } = renderMasked();

    expect(screen.getByText("Payload")).toBeTruthy();
    // Pretty-printed (indented) form of the masked payload, unchanged by memo.
    expect(container.textContent).toContain('"$piiMasked": true');
    expect(container.textContent).toContain('"ssn": "***"');
    // Masking is applied server-side, so there is nothing left to reveal — and
    // nothing is blurred, since only the sensitive values were replaced.
    expect(screen.queryByRole("button", { name: /Reveal PII/ })).toBeNull();
    expect(container.querySelector(".blur-\\[5px\\]")).toBeNull();
  });

  it("formats the payload once and does not reformat on an unrelated re-render", () => {
    const parseSpy = vi.spyOn(JSON, "parse");
    try {
      const { rerender, ui } = renderMasked();
      const payloadParses = () =>
        parseSpy.mock.calls.filter((c) => c[0] === maskedPayload).length;

      // Computed once and shared by the inline Payload block and the (closed)
      // resubmit modal, whose children are still evaluated each render.
      expect(payloadParses()).toBe(1);

      // Re-render with identical props — `messages` is unchanged, so the memo
      // holds and the payload is not re-parsed.
      rerender(ui);
      expect(payloadParses()).toBe(1);
    } finally {
      parseSpy.mockRestore();
    }
  });
});

// ---------------------------------------------------------------------------
// Operator commands: a command the server accepted reads as done, and the
// actions come back once the event's next attempt has been recorded.
// ---------------------------------------------------------------------------
describe("MessageListing operator commands", () => {
  const failed = (lastMessageId: string): api.Event =>
    ({
      eventId: "evt-1",
      sessionId: "sess-1",
      endpointId: "ep-1",
      lastMessageId,
      resolutionStatus: "Failed",
    }) as unknown as api.Event;

  function renderListing(
    eventDetails: api.Event,
    commands: { resubmitEvent?: () => Promise<void>; skipEvent?: () => Promise<void> },
    commandAcceptedFor?: string,
  ) {
    const listing = (details: api.Event) => (
      <MemoryRouter>
        <MessageListing
          eventDetails={details}
          eventTypes={[]}
          skipEvent={commands.skipEvent ?? vi.fn()}
          resubmitEvent={commands.resubmitEvent ?? vi.fn()}
          resubmitEventWithChanges={vi.fn()}
          deleteEvent={vi.fn()}
          commandAcceptedFor={commandAcceptedFor}
        />
      </MemoryRouter>
    );
    const view = render(listing(eventDetails));
    return { rerender: (details: api.Event) => view.rerender(listing(details)) };
  }

  beforeEach(() => {
    vi.mocked(notifySuccess).mockClear();
    vi.mocked(notifyError).mockClear();
  });

  it.each([
    ["Resubmit", "Resubmitted", "resubmitEvent"],
    ["Skip", "Skipped", "skipEvent"],
  ] as const)(
    "confirms an accepted %s and offers it again for the next attempt",
    async (label, done, command) => {
      const run = vi.fn().mockResolvedValue(undefined);
      const { rerender } = renderListing(failed("msg-1"), { [command]: run });

      fireEvent.click(screen.getByRole("button", { name: label }));

      const doneButton = await screen.findByRole<HTMLButtonElement>("button", { name: done });
      expect(doneButton.disabled).toBe(true);
      expect(run).toHaveBeenCalledWith("evt-1", "msg-1");
      expect(notifySuccess).toHaveBeenCalledWith(`${done}.`);
      expect(notifyError).not.toHaveBeenCalled();

      // The event failed again: a new attempt with its own message id.
      rerender(failed("msg-2"));
      expect(screen.getByRole<HTMLButtonElement>("button", { name: label }).disabled).toBe(false);
    },
  );

  it("keeps every action closed for the attempt a command was accepted for, even when mounted fresh", () => {
    // A fresh mount is what switching back to the Message tab does.
    const { rerender } = renderListing(failed("msg-1"), {}, "msg-1");
    const disabled = (name: string) =>
      screen.getByRole<HTMLButtonElement>("button", { name }).disabled;

    expect(disabled("Resubmit")).toBe(true);
    expect(disabled("Resubmit with changes")).toBe(true);
    expect(disabled("Skip")).toBe(true);
    // Delete removes the row by event and session, so it could remove a newer
    // attempt the page has not shown yet.
    expect(disabled("Delete")).toBe(true);

    rerender(failed("msg-2"));
    expect(disabled("Resubmit")).toBe(false);
    expect(disabled("Resubmit with changes")).toBe(false);
    expect(disabled("Skip")).toBe(false);
    expect(disabled("Delete")).toBe(false);
  });
});
