import { describe, it, expect, afterEach, vi } from "vitest";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";
import * as api from "api-client";
// Static import: vi.mock is hoisted above it, so the mocks still apply, and the
// component graph loads while the file is collected. A per-test dynamic import
// billed that load (over a second, more under a loaded run) to the first test's
// 5s timeout.
import EventsPanel from "./events-panel";

// Shared mock handles. `getByFilterMock` resolves the events immediately;
// `postSessionsBatchMock` returns a promise we resolve by hand so the test can
// observe the table BEFORE the session-status batch settles.
const { sessionsDeferreds, postSessionsBatchMock, getByFilterMock, statusCountMock } =
  vi.hoisted(() => {
    const sessionsDeferreds: Array<(value: unknown) => void> = [];
    const postSessionsBatchMock = vi.fn(
      () =>
        new Promise((resolve) => {
          sessionsDeferreds.push(resolve);
        }),
    );
    const getByFilterMock = vi.fn();
    // The endpoint-wide totals behind the status summary line.
    const statusCountMock = vi.fn();
    return {
      sessionsDeferreds,
      postSessionsBatchMock,
      getByFilterMock,
      statusCountMock,
    };
  });

// DataTable reads the toast provider for action feedback; no-op it.
vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast: () => {} }),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") =
    await vi.importActual("api-client");
  class FakeClient {
    postApiEventEndpointIdGetByFilter = getByFilterMock;
    postEndpointSessionsBatch = postSessionsBatchMock;
    getEndpointStatusCountId = statusCountMock;
    // EventTypeFiltering fetches the endpoint's event-type catalog on mount.
    getEventtypesByEndpointId = vi.fn().mockResolvedValue({});
    // useTicketLinkTemplate reads the app status on mount.
    getApiAppStats = vi.fn().mockResolvedValue({});
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

afterEach(() => {
  cleanup();
  sessionsDeferreds.length = 0;
  postSessionsBatchMock.mockClear();
  getByFilterMock.mockReset();
  statusCountMock.mockReset();
});

describe("EventsPanel paints before the session-status batch resolves", () => {
  it("renders event rows while postEndpointSessionsBatch is still pending, then hydrates the counts", async () => {
    const failedEvent = Object.assign(new api.Event(), {
      eventId: "evt-1",
      sessionId: "sess-1",
      eventTypeId: "MyUniqueEventType",
      lastMessageId: "msg-1",
      resolutionStatus: api.ResolutionStatus.Failed,
    });
    getByFilterMock.mockResolvedValue({
      events: [failedEvent],
      continuationToken: undefined,
    });

    render(
      <MemoryRouter>
        <EventsPanel endpointId="ep-1" />
      </MemoryRouter>,
    );

    // The row must paint even though the session-status batch has NOT resolved
    // yet — the batch only fills the count columns, so the table should not wait
    // on it. (With the old awaited batch, isLoading stays true and DataTable
    // renders only a spinner, so this text never appears.)
    await waitFor(() =>
      expect(screen.getByText("MyUniqueEventType")).toBeTruthy(),
    );
    expect(postSessionsBatchMock).toHaveBeenCalledTimes(1);
    expect(sessionsDeferreds.length).toBe(1); // batch is still in flight

    // Resolving the batch hydrates the blocked-count chip on the status badge.
    await act(async () => {
      sessionsDeferreds[0]([
        {
          deferredEvents: ["a_sess-1", "b_sess-1", "c_sess-1"],
          pendingEvents: [],
        },
      ]);
    });

    await waitFor(() => expect(screen.getByText(/3 blocked/)).toBeTruthy());
  });
});

describe("EventsPanel id cells filter on click", () => {
  it("provides a direct event details link", async () => {
    getByFilterMock.mockResolvedValue({
      events: [
        new api.Event({
          eventId: "evt-details",
          sessionId: "sess",
          eventTypeId: "DetailsEvent",
          resolutionStatus: api.ResolutionStatus.Completed,
        }),
      ],
    });
    render(
      <MemoryRouter>
        <EventsPanel endpointId="ep-1" />
      </MemoryRouter>,
    );
    expect(
      (await screen.findByRole("link", { name: "evt-deta…" })).getAttribute(
        "href",
      ),
    ).toBe("/Message/Index/ep-1/evt-details/0");
  });
  it("clicking a Session Id re-queries with that session instead of copying", async () => {
    const event = Object.assign(new api.Event(), {
      eventId: "evt-1",
      sessionId: "sess-filter-me",
      eventTypeId: "ClickFilterEvent",
      lastMessageId: "msg-1",
      resolutionStatus: api.ResolutionStatus.Completed,
    });
    getByFilterMock.mockResolvedValue({
      events: [event],
      continuationToken: undefined,
    });

    render(
      <MemoryRouter>
        <EventsPanel endpointId="ep-1" />
      </MemoryRouter>,
    );
    await waitFor(() =>
      expect(screen.getByText("ClickFilterEvent")).toBeTruthy(),
    );
    const initialCalls = getByFilterMock.mock.calls.length;

    fireEvent.click(screen.getByRole("button", { name: "sess-fil…" }));

    await waitFor(() =>
      expect(getByFilterMock.mock.calls.length).toBeGreaterThan(initialCalls),
    );
    const lastRequest = getByFilterMock.mock.lastCall?.[1] as api.SearchRequest;
    expect(lastRequest.eventFilter?.sessionId).toBe("sess-filter-me");
  });
});

describe("EventsPanel duplicate status display", () => {
  it("labels exact duplicate skips while leaving ordinary skips unchanged", async () => {
    const duplicate = Object.assign(new api.Event(), {
      eventId: "evt-duplicate",
      sessionId: "sess-duplicate",
      eventTypeId: "DuplicateEvent",
      lastMessageId: "msg-duplicate",
      resolutionStatus: api.ResolutionStatus.Skipped,
      reason: "DuplicateDetected",
    });
    const ordinarySkip = Object.assign(new api.Event(), {
      eventId: "evt-ordinary-skip",
      sessionId: "sess-ordinary-skip",
      eventTypeId: "OrdinarySkippedEvent",
      lastMessageId: "msg-ordinary-skip",
      resolutionStatus: api.ResolutionStatus.Skipped,
      reason: "Operator requested skip",
    });
    getByFilterMock.mockResolvedValue({
      events: [duplicate, ordinarySkip],
      continuationToken: undefined,
    });

    render(
      <MemoryRouter>
        <EventsPanel endpointId="ep-1" />
      </MemoryRouter>,
    );

    await waitFor(() =>
      expect(screen.getByText("Skipped (duplicate)")).toBeTruthy(),
    );
    expect(screen.getAllByText("Skipped")).toHaveLength(1);
  });
});

describe("EventsPanel row actions and report flag", () => {
  it("shows a flag-only Report button and an Actions menu only on actionable rows", async () => {
    window.sessionStorage.clear();
    const failed = Object.assign(new api.Event(), {
      eventId: "evt-failed",
      sessionId: "sess-failed",
      eventTypeId: "FailedEvent",
      lastMessageId: "msg-failed",
      resolutionStatus: api.ResolutionStatus.Failed,
    });
    const completed = Object.assign(new api.Event(), {
      eventId: "evt-completed",
      sessionId: "sess-completed",
      eventTypeId: "CompletedEvent",
      lastMessageId: "msg-completed",
      resolutionStatus: api.ResolutionStatus.Completed,
    });
    getByFilterMock.mockResolvedValue({
      events: [failed, completed],
      continuationToken: undefined,
    });

    render(
      <MemoryRouter>
        <EventsPanel endpointId="ep-1" />
      </MemoryRouter>,
    );
    await waitFor(() =>
      expect(screen.getByText("CompletedEvent")).toBeTruthy(),
    );

    // Report is a bare flag — the accessible name carries the label.
    const reportButtons = screen.getAllByRole("button", { name: "Report" });
    expect(reportButtons).toHaveLength(2);
    expect(reportButtons[0].textContent?.trim()).toBe("⚑");

    // One ellipsis trigger for the failed row, none for the completed one, and
    // no inline Resubmit/Skip buttons in the rows.
    expect(screen.getAllByRole("button", { name: "Actions" })).toHaveLength(1);
    // (The only Skip/Resubmit buttons left are the disabled bulk ones in the
    // table header.)
    for (const b of screen.getAllByRole("button", { name: "Skip" })) {
      expect((b as HTMLButtonElement).disabled).toBe(true);
    }

    fireEvent.click(screen.getByRole("button", { name: "Actions" }));
    const menu = screen.getByRole("menu");
    expect(
      within(menu).getByRole("menuitem", { name: /Resubmit/ }),
    ).toBeTruthy();
    expect(within(menu).getByRole("menuitem", { name: /Skip/ })).toBeTruthy();
  });
});

describe("EventsPanel payload URL filters", () => {
  it.each([403, 500])(
    "shows HTTP %s search failures instead of empty results",
    async (status) => {
      getByFilterMock.mockRejectedValue({
        status,
        response: "private diagnostics",
      });
      render(
        <MemoryRouter
          initialEntries={["/?payload=95f596ac-42ac-f111-aaab-6045bda15b3a"]}
        >
          <EventsPanel endpointId="ep-1" />
        </MemoryRouter>,
      );
      expect((await screen.findByRole("alert")).textContent).toContain(
        "Search failed",
      );
      expect(screen.queryByText("No events match your filters")).toBeNull();
      expect(screen.queryByText("private diagnostics")).toBeNull();
      expect(screen.getByRole("alert").textContent).toContain(String(status));
    },
  );

  it.each([
    [
      "TestContactRecordForDynamicsIntegration+",
      "TestContactRecordForDynamicsIntegration",
    ],
    ["+++", undefined],
    ["+two+words+", "two words"],
  ])(
    "normalizes pasted whitespace in %s before searching",
    async (encoded, expected) => {
      getByFilterMock.mockResolvedValue({
        events: [],
        continuationToken: undefined,
      });
      render(
        <MemoryRouter initialEntries={[`/?payload=${encoded}`]}>
          <EventsPanel endpointId="ep-1" />
        </MemoryRouter>,
      );
      await waitFor(() => expect(getByFilterMock).toHaveBeenCalled());
      const request = getByFilterMock.mock.calls.at(-1)![1];
      expect(request.eventFilter?.payload).toBe(expected);
    },
  );
});

describe("EventsPanel status summary line", () => {
  // Shows where the summary links navigate to.
  const LocationProbe = () => {
    const location = useLocation();
    return (
      <div data-testid="location">{location.pathname + location.search}</div>
    );
  };

  const summaryItem = (name: RegExp) =>
    within(
      screen.getByRole("navigation", { name: "Endpoint status summary" }),
    ).getByRole("button", { name });

  const pageWithOneFailure = () =>
    getByFilterMock.mockResolvedValue({
      events: [
        Object.assign(new api.Event(), {
          eventId: "evt-tile",
          sessionId: "sess-tile",
          eventTypeId: "TileEvent",
          lastMessageId: "msg-tile",
          resolutionStatus: api.ResolutionStatus.Failed,
        }),
      ],
      continuationToken: undefined,
    });

  const endpointTotals = () =>
    // failedCount already includes DeadLettered; Unsupported is added on top.
    // pendingCount arrives with Unsupported folded in, which the line removes
    // so the number matches what the Pending filter lists.
    statusCountMock.mockResolvedValue({
      endpointId: "ep-1",
      failedCount: 11,
      unsupportedCount: 2,
      deferredCount: 3,
      pendingCount: 19,
    });

  const renderPanel = (url = "/") =>
    render(
      <MemoryRouter initialEntries={[url]}>
        <EventsPanel endpointId="ep-1" />
        <LocationProbe />
      </MemoryRouter>,
    );

  it("shows the endpoint's totals, not just the loaded page", async () => {
    window.sessionStorage.clear();
    pageWithOneFailure();
    endpointTotals();

    renderPanel();

    await waitFor(() =>
      expect(summaryItem(/^13 failed$/)).toBeTruthy(),
    );
    expect(summaryItem(/^3 deferred$/)).toBeTruthy();
    expect(summaryItem(/^17 pending$/)).toBeTruthy();
    expect(statusCountMock).toHaveBeenCalledWith("ep-1");
  });

  it("falls back to the loaded page when the totals can't be read", async () => {
    window.sessionStorage.clear();
    pageWithOneFailure();
    statusCountMock.mockRejectedValue(new Error("unavailable"));

    renderPanel();

    await waitFor(() => expect(screen.getByText("TileEvent")).toBeTruthy());
    expect(summaryItem(/^1 failed$/)).toBeTruthy();
    expect(summaryItem(/^0 pending$/)).toBeTruthy();
  });

  it("disables zero counts", async () => {
    window.sessionStorage.clear();
    pageWithOneFailure();
    statusCountMock.mockResolvedValue({
      endpointId: "ep-1",
      failedCount: 1,
      deferredCount: 0,
      pendingCount: 0,
    });

    renderPanel();

    await waitFor(() =>
      expect(summaryItem(/^1 failed$/).hasAttribute("disabled")).toBe(false),
    );
    expect(summaryItem(/^0 deferred$/).hasAttribute("disabled")).toBe(true);
    expect(summaryItem(/^0 pending$/).hasAttribute("disabled")).toBe(true);
  });

  it("opens the endpoint's failures on the Failed page", async () => {
    window.sessionStorage.clear();
    pageWithOneFailure();
    endpointTotals();

    renderPanel();

    await waitFor(() => expect(summaryItem(/^13 failed$/)).toBeTruthy());
    fireEvent.click(summaryItem(/^13 failed$/));
    expect(screen.getByTestId("location").textContent).toBe(
      "/Failed?endpointId=ep-1",
    );
  });

  it.each([
    [/^3 deferred$/, api.ResolutionStatus.Deferred],
    [/^17 pending$/, api.ResolutionStatus.Pending],
  ])("filters the list when %s is clicked", async (name, status) => {
    window.sessionStorage.clear();
    pageWithOneFailure();
    endpointTotals();

    renderPanel();

    await waitFor(() => expect(summaryItem(name)).toBeTruthy());
    fireEvent.click(summaryItem(name));
    await waitFor(() => {
      const request = getByFilterMock.mock.calls.at(-1)![1];
      expect(request.eventFilter?.resolutionStatus).toEqual([status]);
    });
  });
});

describe("EventsPanel view", () => {
  it("offers only the list view, even for an old grouped-view link", async () => {
    window.sessionStorage.clear();
    getByFilterMock.mockResolvedValue({
      events: [
        Object.assign(new api.Event(), {
          eventId: "evt-old-link",
          sessionId: "sess-old-link",
          eventTypeId: "OldLinkEvent",
          lastMessageId: "msg-old-link",
          resolutionStatus: api.ResolutionStatus.Failed,
        }),
      ],
      continuationToken: undefined,
    });

    render(
      <MemoryRouter initialEntries={["/?viewMode=grouped"]}>
        <EventsPanel endpointId="ep-1" />
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByText("OldLinkEvent")).toBeTruthy());
    expect(screen.queryByRole("button", { name: "Grouped by Error" })).toBeNull();
    expect(screen.queryByRole("button", { name: "List" })).toBeNull();
  });
});
