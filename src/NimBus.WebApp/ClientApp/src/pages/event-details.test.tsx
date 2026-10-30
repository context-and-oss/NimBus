import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import { act, cleanup, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter, Route, Routes } from "react-router-dom";
import moment from "moment";
import * as api from "api-client";
import EventDetails, { enrichBlockedItems } from "./event-details";

// Spec 005 (FR-042): histories must flow from EventDetails state into the
// MessageListing `messages` prop. The captured-props mock asserts the
// wiring directly without needing to render the full MessageListing tree.
const capturedProps: { latest?: Record<string, unknown> } = {};
vi.mock("components/event-details/message-listing", () => ({
  default: (props: Record<string, unknown>) => {
    capturedProps.latest = props;
    return <div data-testid="message-listing-stub" />;
  },
}));

// FlowTimeline / BlockedListing / Loading / TabSelection / Page are rendered
// by the page but we only need to inspect the MessageListing wiring. Stub
// the children that pull in heavy deps so the test stays fast and hermetic.
vi.mock("components/event-details/flow-timeline", () => ({
  default: () => <div data-testid="flow-timeline-stub" />,
}));
vi.mock("components/event-details/blocked-listing", () => ({
  default: () => <div data-testid="blocked-stub" />,
}));
vi.mock("components/loading/loading", () => ({
  default: () => <div data-testid="loading-stub" />,
}));

// Mock the api-client so EventDetails resolves data deterministically. Only
// the calls EventDetails actually makes need to be stubbed; everything else
// can stay as the real export.
const sampleHistory: api.Message[] = [
  Object.assign(new api.Message(), {
    messageType: api.MessageType.EventRequest,
    enqueuedTimeUtc: moment("2026-05-28T09:59:30.000Z"),
  }),
  Object.assign(new api.Message(), {
    messageType: api.MessageType.DeferralResponse,
    enqueuedTimeUtc: moment("2026-05-28T10:00:00.000Z"),
  }),
];

// Shared, per-test-controllable client method mocks. Hoisted so the vi.mock
// factory (itself hoisted above imports) can close over them; each test's
// defaults are (re)applied in beforeEach and call history is cleared afterwards.
const clientMocks = vi.hoisted(() => ({
  getEventId: vi.fn(),
  getEventDetailsHistoryId: vi.fn(),
  getMessageAuditsEventId: vi.fn(),
  getEventTypes: vi.fn(),
  getEventBlockedId: vi.fn(),
  getEventIds: vi.fn(),
  postResubmitEventIds: vi.fn(),
  postSkipEventIds: vi.fn(),
  postResubmitWithChangesEventIds: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getEventId = clientMocks.getEventId;
    getEventDetailsHistoryId = clientMocks.getEventDetailsHistoryId;
    getMessageAuditsEventId = clientMocks.getMessageAuditsEventId;
    getEventTypes = clientMocks.getEventTypes;
    getEventBlockedId = clientMocks.getEventBlockedId;
    getEventIds = clientMocks.getEventIds;
    postResubmitEventIds = clientMocks.postResubmitEventIds;
    postSkipEventIds = clientMocks.postSkipEventIds;
    postResubmitWithChangesEventIds = clientMocks.postResubmitWithChangesEventIds;
  }
  return {
    ...actual,
    Client: FakeClient,
    CookieAuth: () => ({}),
  };
});

beforeEach(() => {
  clientMocks.getEventId.mockResolvedValue(
    Object.assign(new api.Event(), {
      eventId: "evt-1",
      sessionId: "sess-1",
      endpointId: "ep-1",
      lastMessageId: "msg-1",
      resolutionStatus: "Completed",
    }),
  );
  clientMocks.getEventDetailsHistoryId.mockResolvedValue(sampleHistory);
  clientMocks.getMessageAuditsEventId.mockResolvedValue([]);
  clientMocks.getEventTypes.mockResolvedValue([]);
  clientMocks.getEventBlockedId.mockResolvedValue({ items: [], total: 0 });
  clientMocks.getEventIds.mockResolvedValue(undefined);
  clientMocks.postResubmitEventIds.mockResolvedValue(undefined);
  clientMocks.postSkipEventIds.mockResolvedValue(undefined);
  clientMocks.postResubmitWithChangesEventIds.mockResolvedValue(undefined);
});

afterEach(() => {
  cleanup();
  vi.resetAllMocks();
  capturedProps.latest = undefined;
});

describe("enrichBlockedItems", () => {
  function blocked(eventId: string, status: string): api.BlockedEvent {
    return Object.assign(new api.BlockedEvent(), {
      eventId,
      originatingId: `orig-${eventId}`,
      status,
    });
  }

  it("fetches every blocked item concurrently instead of one at a time", async () => {
    const items = [
      blocked("e0", "Failed"),
      blocked("e1", "Deadlettered"),
      blocked("e2", "Unsupported"),
      blocked("e3", "Failed"),
      blocked("e4", "Failed"),
    ];
    const getEventIds = vi
      .fn()
      .mockImplementation((eventId: string) =>
        Promise.resolve(Object.assign(new api.Message(), { eventId })),
      );
    const client = { getEventIds } as unknown as api.Client;

    // A sequential await-loop calls getEventIds once, then yields at the first
    // `await`; only the concurrent map fires all N calls in the same tick before
    // the returned promise's first suspension point.
    const pending = enrichBlockedItems(client, items);
    expect(getEventIds).toHaveBeenCalledTimes(items.length);

    await pending;
  });

  it("preserves page order and pairs each message with its status", async () => {
    const items = [
      blocked("e0", "Failed"),
      blocked("e1", "Deadlettered"),
      blocked("e2", "Unsupported"),
    ];
    // Resolve out of call order to prove ordering follows `items`, not arrival.
    const delays: Record<string, number> = { e0: 30, e1: 0, e2: 15 };
    const getEventIds = vi.fn().mockImplementation(
      (eventId: string) =>
        new Promise((resolve) =>
          setTimeout(
            () => resolve(Object.assign(new api.Message(), { eventId })),
            delays[eventId],
          ),
        ),
    );
    const client = { getEventIds } as unknown as api.Client;

    const result = await enrichBlockedItems(client, items);

    expect(result.map((r) => (r.message as { eventId: string }).eventId)).toEqual([
      "e0",
      "e1",
      "e2",
    ]);
    expect(result.map((r) => r.status)).toEqual([
      "Failed",
      "Deadlettered",
      "Unsupported",
    ]);
    expect(getEventIds).toHaveBeenNthCalledWith(1, "e0", "orig-e0");
  });

  it("drops items whose detail fetch resolved to nothing, keeping the rest in order", async () => {
    const items = [
      blocked("keep-0", "Failed"),
      blocked("drop", "Failed"),
      blocked("keep-1", "Failed"),
    ];
    const getEventIds = vi.fn().mockImplementation((eventId: string) =>
      Promise.resolve(
        eventId === "drop" ? undefined : Object.assign(new api.Message(), { eventId }),
      ),
    );
    const client = { getEventIds } as unknown as api.Client;

    const result = await enrichBlockedItems(client, items);

    expect(result).toHaveLength(2);
    expect(result.map((r) => (r.message as { eventId: string }).eventId)).toEqual([
      "keep-0",
      "keep-1",
    ]);
  });
});

describe("EventDetails -> MessageListing wiring (spec 005)", () => {
  it("forwards the fetched history into the MessageListing messages prop", async () => {
    render(
      <MemoryRouter initialEntries={["/Message/Index/ep-1/evt-1/0"]}>
        <Routes>
          <Route
            path="/Message/Index/:endpointId/:id/:backindex"
            element={<EventDetails />}
          />
        </Routes>
      </MemoryRouter>,
    );

    // Wait until the page's effect has resolved and MessageListing has
    // received its props (the captured-props mock fires on every render).
    await waitFor(() => {
      expect(capturedProps.latest).toBeDefined();
      expect(
        (capturedProps.latest as { messages?: api.Message[] }).messages,
      ).toEqual(sampleHistory);
    });
  });
});

describe("EventDetails operator actions", () => {
  it.each(["resubmit", "skip", "resubmit with changes"] as const)(
    "leaves the archived event after a successful %s without fetching it again",
    async (action) => {
      clientMocks.getEventId
        .mockResolvedValueOnce(Object.assign(new api.Event(), {
          eventId: "evt-1",
          sessionId: "sess-1",
          endpointId: "ep-1",
          lastMessageId: "msg-1",
          resolutionStatus: "Failed",
        }))
        .mockRejectedValueOnce({ status: 404 });

      render(
        <MemoryRouter initialEntries={["/Message/Index/ep-1/evt-1/0"]}>
          <Routes>
            <Route path="/Message/Index/:endpointId/:id/:backindex" element={<EventDetails />} />
            <Route path="/Endpoints/Details/:endpointId" element={<div>Endpoint details</div>} />
          </Routes>
        </MemoryRouter>,
      );

      await waitFor(() => expect(capturedProps.latest).toBeDefined());
      await act(async () => {
        if (action === "resubmit") {
          const submit = capturedProps.latest!.resubmitEvent as (eventId: string, messageId: string) => Promise<void>;
          await submit("evt-1", "msg-1");
        } else if (action === "skip") {
          const skip = capturedProps.latest!.skipEvent as (eventId: string, messageId: string) => Promise<void>;
          await skip("evt-1", "msg-1");
        } else {
          const submit = capturedProps.latest!.resubmitEventWithChanges as (
            eventId: string, messageId: string, body: api.ResubmitWithChanges,
          ) => Promise<void>;
          await submit("evt-1", "msg-1", api.ResubmitWithChanges.fromJS({
            eventTypeId: "Demo.Type", eventContent: "{}",
          }));
        }
      });

      const post = action === "resubmit"
        ? clientMocks.postResubmitEventIds
        : action === "skip"
          ? clientMocks.postSkipEventIds
          : clientMocks.postResubmitWithChangesEventIds;
      expect(post).toHaveBeenCalledTimes(1);
      if (action === "resubmit with changes") {
        expect(post).toHaveBeenCalledWith("evt-1", "msg-1", expect.any(api.ResubmitWithChanges));
      } else {
        expect(post).toHaveBeenCalledWith("evt-1", "msg-1");
      }
      expect(clientMocks.getEventId).toHaveBeenCalledTimes(1);
      expect(screen.getByText("Endpoint details")).toBeTruthy();
    },
  );
});

describe("EventDetails independent-request parallelization", () => {
  it("issues history, audits and event-type requests without waiting for the event fetch", async () => {
    // Hold the event fetch open so the only way the other three fire is if they
    // are started independently of the resolved event (which they don't need).
    let resolveEvent!: (event: api.Event) => void;
    clientMocks.getEventId.mockReturnValue(
      new Promise<api.Event>((resolve) => {
        resolveEvent = resolve;
      }),
    );

    render(
      <MemoryRouter initialEntries={["/Message/Index/ep-1/evt-1/0"]}>
        <Routes>
          <Route
            path="/Message/Index/:endpointId/:id/:backindex"
            element={<EventDetails />}
          />
        </Routes>
      </MemoryRouter>,
    );

    // The event fetch is still pending (the page is on its Loading splash), yet
    // the three route-param-keyed requests must already have been issued.
    await waitFor(() => {
      expect(clientMocks.getEventDetailsHistoryId).toHaveBeenCalledTimes(1);
      expect(clientMocks.getMessageAuditsEventId).toHaveBeenCalledTimes(1);
      expect(clientMocks.getEventTypes).toHaveBeenCalledTimes(1);
    });
    expect(clientMocks.getEventId).toHaveBeenCalledTimes(1);

    // Release the event fetch and let the effect settle so no state update
    // leaks past the test.
    resolveEvent(
      Object.assign(new api.Event(), {
        eventId: "evt-1",
        sessionId: "sess-1",
        endpointId: "ep-1",
        resolutionStatus: "Completed",
      }),
    );
    await waitFor(() => expect(capturedProps.latest).toBeDefined());
  });
});
