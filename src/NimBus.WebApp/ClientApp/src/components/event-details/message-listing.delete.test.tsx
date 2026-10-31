import { describe, it, expect, afterEach, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import * as api from "api-client";
import { notifyError } from "functions/notifications.functions";
import MessageListing from "./message-listing";

// Event Details → Delete. A Failed event blocks its session, and the server
// refuses to delete it (#288), so the page does not offer Delete for it.
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
  }
  return { ...actual, Client: MockClient, CookieAuth: () => ({}) };
});

afterEach(() => {
  cleanup();
  vi.mocked(notifyError).mockClear();
});

const event = (resolutionStatus: string): api.Event =>
  ({
    eventId: "evt-1",
    sessionId: "sess-1",
    endpointId: "ep-1",
    lastMessageId: "msg-1",
    resolutionStatus,
  }) as unknown as api.Event;

function renderListing(resolutionStatus: string, deleteEvent: () => Promise<void>) {
  return render(
    <MemoryRouter>
      <MessageListing
        eventDetails={event(resolutionStatus)}
        eventTypes={[]}
        skipEvent={vi.fn()}
        resubmitEvent={vi.fn()}
        resubmitEventWithChanges={vi.fn()}
        deleteEvent={deleteEvent}
      />
    </MemoryRouter>,
  );
}

describe("MessageListing Delete", () => {
  it("does not offer Delete for a Failed event", () => {
    renderListing("Failed", vi.fn());
    expect(screen.queryByRole("button", { name: "Delete" })).toBeNull();
    // Skip and Resubmit remain the way out for it.
    expect(screen.getByRole("button", { name: "Skip" })).toBeTruthy();
  });

  it.each(["DeadLettered", "Unsupported"])("offers Delete for a %s event", (status) => {
    renderListing(status, vi.fn());
    expect(screen.getByRole("button", { name: "Delete" })).toBeTruthy();
  });

  it("explains a refused delete instead of failing silently", async () => {
    const refused = Object.assign(new Error("Conflict"), { status: 409 });
    renderListing("DeadLettered", vi.fn().mockRejectedValue(refused));

    fireEvent.click(screen.getByRole("button", { name: "Delete" }));
    const confirm = screen
      .getAllByRole("button", { name: "Delete" })
      .find((button) => button.closest("[role='dialog']"));
    await act(async () => {
      fireEvent.click(confirm!);
    });

    expect(notifyError).toHaveBeenCalledWith(
      "This event blocks its session, so it was not deleted. Skip or resubmit it first.",
    );
  });
});
