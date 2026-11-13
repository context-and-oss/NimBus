import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import * as api from "api-client";
import MessageListing from "./message-listing";

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
  const actual =
    await vi.importActual<typeof import("api-client")>("api-client");
  class MockClient {
    getMe() {
      return Promise.resolve(undefined);
    }
  }
  return { ...actual, Client: MockClient, CookieAuth: () => ({}) };
});
// The card fetches its own status and result; only the props the listing passes are under test.
vi.mock("components/event-details/intelligence-card", () => ({
  default: ({
    messageId,
    highlighted,
  }: {
    messageId: string;
    highlighted?: boolean;
  }) => (
    <div
      data-testid={`card-${messageId}`}
      data-highlighted={String(Boolean(highlighted))}
    />
  ),
}));

afterEach(cleanup);

const event = {
  eventId: "event-1",
  endpointId: "orders",
  lastMessageId: "failure-2",
  resolutionStatus: "Failed",
} as unknown as api.Event;

const failures = [
  {
    eventId: "event-1",
    messageId: "failure-1",
    messageType: "ErrorResponse",
    errorContent: "first",
  },
  {
    eventId: "event-1",
    messageId: "failure-2",
    messageType: "ErrorResponse",
    errorContent: "second",
  },
] as unknown as api.Message[];

function renderAt(url: string) {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <MessageListing
        eventDetails={event}
        messages={failures}
      />
    </MemoryRouter>,
  );
}

describe("MessageListing failure occurrence link (spec 043 §7)", () => {
  it("highlights the failed attempt named by the message query parameter", () => {
    renderAt("/Message/Index/orders/event-1/0?message=failure-2");

    expect(screen.getByTestId("card-failure-2").dataset.highlighted).toBe(
      "true",
    );
    expect(screen.getByTestId("card-failure-1").dataset.highlighted).toBe(
      "false",
    );
  });

  it("highlights nothing without the parameter", () => {
    renderAt("/Message/Index/orders/event-1/0");

    expect(screen.getByTestId("card-failure-1").dataset.highlighted).toBe(
      "false",
    );
    expect(screen.getByTestId("card-failure-2").dataset.highlighted).toBe(
      "false",
    );
  });
});
