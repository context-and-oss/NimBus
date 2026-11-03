import { afterEach, describe, expect, it, vi } from "vitest";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  within,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import moment from "moment";
import * as api from "api-client";
import { ToastProvider } from "components/ui";
import FailedItemList, {
  type FailedItemListProps,
  failureIdOf,
} from "./failed-item-list";

const event = (eventId: string, extra: Partial<api.IEvent> = {}) =>
  new api.Event({
    eventId,
    endpointId: "Crm",
    sessionId: `s-${eventId}`,
    lastMessageId: `m-${eventId}`,
    eventTypeId: "OrderPlaced",
    resolutionStatus: api.ResolutionStatus.Failed,
    updatedAt: moment("2026-09-28T18:14:22Z"),
    messageContent: new api.MessageContent({
      errorContent: new api.ErrorContent({ errorText: `boom ${eventId}` }),
    }),
    ...extra,
  });

const renderList = (props: Partial<FailedItemListProps> = {}) => {
  const all: FailedItemListProps = {
    events: [event("e1"), event("e2")],
    blocked: {},
    selected: new Set(),
    onSelectedChange: vi.fn(),
    onAct: vi.fn(),
    onOpen: vi.fn(),
    onNarrow: vi.fn(),
    hasMore: false,
    isLoading: false,
    onLoadMore: vi.fn(),
    ...props,
  };
  render(
    <ToastProvider>
      <MemoryRouter>
        <FailedItemList {...all} />
      </MemoryRouter>
    </ToastProvider>,
  );
  return all;
};

afterEach(cleanup);

describe("FailedItemList", () => {
  it("opens the drawer from its event ID and preserves Failed context for full-page links", () => {
    const props = renderList();
    const link = screen.getByRole("link", { name: "e1" });
    expect(link.getAttribute("href")).toContain("?from=failed");
    fireEvent.click(link);
    expect(props.onOpen).toHaveBeenCalledWith(props.events[0]);
  });
  it("shows each failure's status, error text and badges", () => {
    renderList({
      events: [
        event("e1", { resubmitCount: 2, sessionId: "s1" }),
        event("e2", {
          resolutionStatus: api.ResolutionStatus.DeadLettered,
          messageContent: undefined,
          deadLetterReason: "MaxDeliveryCountExceeded",
        }),
      ],
      blocked: { "Crm/s1": 3 },
    });

    const items = within(
      screen.getByRole("list", { name: "Failures" }),
    ).getAllByRole("listitem");
    expect(items).toHaveLength(2);
    expect(within(items[0]).getByText(/- FAILED$/)).toBeTruthy();
    expect(within(items[0]).getByText("boom e1")).toBeTruthy();
    expect(within(items[0]).getByText("3 blocked in session")).toBeTruthy();
    expect(within(items[0]).getByText("resubmitted 2×")).toBeTruthy();
    expect(within(items[1]).getByText(/- DEADLETTERED$/)).toBeTruthy();
    expect(within(items[1]).getByText("MaxDeliveryCountExceeded")).toBeTruthy();
  });

  it("opens a failure from its title instead of navigating", () => {
    const props = renderList();

    fireEvent.click(screen.getAllByRole("link", { name: /- FAILED$/ })[1]);

    expect(props.onOpen).toHaveBeenCalledWith(props.events[1]);
  });

  it("runs a bulk action on the selected failures only", () => {
    const events = [event("e1"), event("e2"), event("e3")];
    const props = renderList({
      events,
      selected: new Set([failureIdOf(events[0]), failureIdOf(events[2])]),
    });

    expect(screen.getByText("2 selected")).toBeTruthy();
    fireEvent.click(screen.getByRole("button", { name: "Resubmit 2" }));

    expect(props.onAct).toHaveBeenCalledWith("Resubmit", [
      events[0],
      events[2],
    ]);
  });

  it("selects every loaded failure from Select all", () => {
    const props = renderList();

    fireEvent.click(screen.getByLabelText("Select all loaded failures"));

    expect(props.onSelectedChange).toHaveBeenCalledWith(
      new Set(["Crm/e1", "Crm/e2"]),
    );
  });

  it("disables bulk actions with nothing selected", () => {
    renderList();

    const bulk = screen.getByRole("toolbar", { name: "Bulk actions" });
    expect(
      (within(bulk).getByRole("button", { name: "Skip" }) as HTMLButtonElement)
        .disabled,
    ).toBe(true);
  });

  it("offers Load more only when the server has more", () => {
    const props = renderList({ hasMore: true });
    fireEvent.click(screen.getByRole("button", { name: "Load more" }));
    expect(props.onLoadMore).toHaveBeenCalledTimes(1);

    cleanup();
    renderList({ hasMore: false });
    expect(screen.queryByRole("button", { name: "Load more" })).toBeNull();
  });
});
