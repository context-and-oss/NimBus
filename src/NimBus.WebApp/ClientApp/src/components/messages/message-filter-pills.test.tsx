import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import * as api from "api-client";
import MessageFilterPills, {
  EMPTY_MESSAGE_FILTER,
  MessageSearchBox,
  toMessageSearchFilter,
} from "./message-filter-pills";

vi.mock("api-client", async () => {
  const actual =
    await vi.importActual<typeof import("api-client")>("api-client");
  class FakeClient {
    getEndpointsAll = () => Promise.resolve(["Crm", "Erp"]);
    getEventTypes = () => Promise.resolve([]);
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

afterEach(cleanup);

const renderPills = (value = EMPTY_MESSAGE_FILTER) => {
  const onApply = vi.fn();
  const onReset = vi.fn();
  render(
    <MessageFilterPills value={value} onApply={onApply} onReset={onReset} />,
  );
  return { onApply, onReset };
};

describe("MessageFilterPills", () => {
  it("applies an enqueued preset and clears a custom range", () => {
    const { onApply } = renderPills({
      ...EMPTY_MESSAGE_FILTER,
      enqueuedFrom: "2026-11-10T08:00",
    });

    fireEvent.click(
      screen.getByRole("button", { name: "Enqueued time range" }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Last hour" }));

    expect(onApply).toHaveBeenCalledWith({
      enqueuedPeriod: api.Period._1h,
      enqueuedFrom: "",
      enqueuedTo: "",
    });
  });

  it("picks a message type", () => {
    const { onApply } = renderPills();

    fireEvent.click(screen.getByRole("button", { name: "Message type" }));
    fireEvent.click(screen.getByRole("button", { name: "ErrorResponse" }));

    expect(onApply).toHaveBeenCalledWith({
      messageType: api.MessageSearchFilterMessageType.ErrorResponse,
    });
  });

  it("picks one endpoint", async () => {
    const { onApply } = renderPills();

    fireEvent.click(screen.getByRole("button", { name: "Endpoint" }));
    fireEvent.click(await screen.findByRole("button", { name: "Erp" }));

    expect(onApply).toHaveBeenCalledWith({ endpointId: "Erp" });
  });

  it("shows applied values on the pills and offers Reset only when filtered", () => {
    renderPills();
    expect(screen.queryByRole("button", { name: "Reset filters" })).toBeNull();
    cleanup();

    const { onReset } = renderPills({
      ...EMPTY_MESSAGE_FILTER,
      messageType: api.MessageSearchFilterMessageType.ErrorResponse,
      eventTypeId: ["CrmAccountCreated", "CrmContactUpdated"],
    });
    expect(
      screen.getByRole("button", { name: "Message type" }).textContent,
    ).toContain("ErrorResponse");
    expect(
      screen.getByRole("button", { name: "Event type" }).textContent,
    ).toContain("CrmAccountCreated +1");
    fireEvent.click(screen.getByRole("button", { name: "Reset filters" }));
    expect(onReset).toHaveBeenCalled();
  });

  it("adds a publisher filter through (+) and removes it from its pill", async () => {
    const { onApply } = renderPills();

    fireEvent.click(screen.getByRole("button", { name: "Add filter" }));
    await screen.findAllByRole("option", { name: "Erp" });
    fireEvent.change(screen.getByLabelText("From (publisher)"), {
      target: { value: "Erp" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));
    expect(onApply).toHaveBeenCalledWith({ from: "Erp", to: "" });
    cleanup();

    const removed = renderPills({ ...EMPTY_MESSAGE_FILTER, from: "Erp" });
    fireEvent.click(
      screen.getByRole("button", { name: "Remove From (publisher)" }),
    );
    expect(removed.onApply).toHaveBeenCalledWith({ from: "" });
  });
});

describe("MessageSearchBox", () => {
  it("sets the ID filters from prefixes, a bare value being an event ID", () => {
    const onApply = vi.fn();
    render(<MessageSearchBox value={EMPTY_MESSAGE_FILTER} onApply={onApply} />);
    const box = screen.getByRole("searchbox", { name: "Search messages" });

    fireEvent.change(box, { target: { value: "session:s1 e-42" } });
    fireEvent.keyDown(box, { key: "Enter" });

    expect(onApply).toHaveBeenCalledWith({
      eventId: "e-42",
      messageId: "",
      sessionId: "s1",
    });
  });
});

describe("toMessageSearchFilter", () => {
  const now = new Date("2026-11-15T12:00:00Z");

  it("starts an enqueued preset its span before now, with no end", () => {
    const filter = toMessageSearchFilter(
      { ...EMPTY_MESSAGE_FILTER, enqueuedPeriod: api.Period._1d },
      now,
    );
    expect(filter.enqueuedAtFrom?.toISOString()).toBe(
      "2026-11-14T12:00:00.000Z",
    );
    expect(filter.enqueuedAtTo).toBeUndefined();
  });

  it("prefers a custom range over a preset", () => {
    const filter = toMessageSearchFilter(
      {
        ...EMPTY_MESSAGE_FILTER,
        enqueuedPeriod: api.Period._1d,
        enqueuedFrom: "2026-11-01T00:00",
        enqueuedTo: "2026-11-02T00:00",
      },
      now,
    );
    expect(filter.enqueuedAtFrom?.toISOString()).toBe(
      new Date(2026, 10, 1).toISOString(),
    );
    expect(filter.enqueuedAtTo?.toISOString()).toBe(
      new Date(2026, 10, 2).toISOString(),
    );
  });

  it("sends every chosen event type", () => {
    const filter = toMessageSearchFilter({
      ...EMPTY_MESSAGE_FILTER,
      eventTypeId: ["A", "B"],
    });
    expect(filter.eventTypeId).toEqual(["A", "B"]);
  });
});
