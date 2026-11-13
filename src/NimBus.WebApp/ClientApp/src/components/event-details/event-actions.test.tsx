import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import { act, cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import * as api from "api-client";
import { notifyError, notifySuccess } from "functions/notifications.functions";
import EventActions from "./event-actions";

vi.mock("functions/notifications.functions", () => ({
  notifySuccess: vi.fn(),
  notifyError: vi.fn(),
  notifyWarning: vi.fn(),
  notifyInfo: vi.fn(),
  notifyWithUndo: vi.fn(),
}));

afterEach(() => {
  cleanup();
});

beforeEach(() => {
  vi.mocked(notifySuccess).mockClear();
  vi.mocked(notifyError).mockClear();
});

const event = (resolutionStatus: string, lastMessageId = "msg-1"): api.Event =>
  ({
    eventId: "evt-1",
    sessionId: "sess-1",
    endpointId: "ep-1",
    lastMessageId,
    resolutionStatus,
  }) as unknown as api.Event;

function renderActions(
  eventDetails: api.Event,
  overrides: Partial<React.ComponentProps<typeof EventActions>> = {},
) {
  const ui = (details: api.Event) => (
    <MemoryRouter>
      <EventActions
        eventDetails={details}
        eventTypes={[]}
        skipEvent={vi.fn()}
        resubmitEvent={vi.fn()}
        resubmitEventWithChanges={vi.fn()}
        deleteEvent={vi.fn()}
        {...overrides}
      />
    </MemoryRouter>
  );
  const view = render(ui(eventDetails));
  return { ...view, rerender: (details: api.Event) => view.rerender(ui(details)) };
}

describe("EventActions visibility", () => {
  it("offers nothing for a completed event", () => {
    const { container } = renderActions(event("Completed"));
    expect(container.querySelectorAll("button")).toHaveLength(0);
  });

  it("offers Reprocess for a deferred event", () => {
    renderActions(event("Deferred"), { reprocessDeferred: vi.fn() });
    expect(screen.getByRole("button", { name: "Reprocess" })).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Resubmit" })).toBeNull();
  });
});

// Event Details → Delete. A Failed event blocks its session, and the server
// refuses to delete it (#288), so the page does not offer Delete for it.
describe("EventActions Delete", () => {
  it("does not offer Delete for a Failed event", () => {
    renderActions(event("Failed"));
    expect(screen.queryByRole("button", { name: "Delete" })).toBeNull();
    // Skip and Resubmit remain the way out for it.
    expect(screen.getByRole("button", { name: "Skip" })).toBeTruthy();
  });

  it.each(["DeadLettered", "Unsupported"])("offers Delete for a %s event", (status) => {
    renderActions(event(status));
    expect(screen.getByRole("button", { name: "Delete" })).toBeTruthy();
  });

  it("explains a refused delete instead of failing silently", async () => {
    const refused = Object.assign(new Error("Conflict"), { status: 409 });
    renderActions(event("DeadLettered"), {
      deleteEvent: vi.fn().mockRejectedValue(refused),
    });

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

// Operator commands: a command the server accepted reads as done, and the
// actions come back once the event's next attempt has been recorded.
describe("EventActions operator commands", () => {
  it.each([
    ["Resubmit", "Resubmitted", "resubmitEvent"],
    ["Skip", "Skipped", "skipEvent"],
  ] as const)(
    "confirms an accepted %s and offers it again for the next attempt",
    async (label, done, command) => {
      const run = vi.fn().mockResolvedValue(undefined);
      const { rerender } = renderActions(event("Failed"), { [command]: run });

      fireEvent.click(screen.getByRole("button", { name: label }));

      const doneButton = await screen.findByRole<HTMLButtonElement>("button", {
        name: done,
      });
      expect(doneButton.disabled).toBe(true);
      expect(run).toHaveBeenCalledWith("evt-1", "msg-1");
      expect(notifySuccess).toHaveBeenCalledWith(`${done}.`);
      expect(notifyError).not.toHaveBeenCalled();

      // The event failed again: a new attempt with its own message id.
      rerender(event("Failed", "msg-2"));
      expect(
        screen.getByRole<HTMLButtonElement>("button", { name: label }).disabled,
      ).toBe(false);
    },
  );

  it("keeps every action closed for the attempt a command was accepted for, even when mounted fresh", () => {
    // Dead-lettered, because a Failed event does not offer Delete (#301).
    const { rerender } = renderActions(event("DeadLettered"), {
      commandAcceptedFor: "msg-1",
    });
    const disabled = (name: string) =>
      screen.getByRole<HTMLButtonElement>("button", { name }).disabled;

    expect(disabled("Resubmit")).toBe(true);
    expect(disabled("Resubmit with changes")).toBe(true);
    expect(disabled("Skip")).toBe(true);
    // Delete removes the row by event and session, so it could remove a newer
    // attempt the page has not shown yet.
    expect(disabled("Delete")).toBe(true);

    rerender(event("DeadLettered", "msg-2"));
    expect(disabled("Resubmit")).toBe(false);
    expect(disabled("Resubmit with changes")).toBe(false);
    expect(disabled("Skip")).toBe(false);
    expect(disabled("Delete")).toBe(false);
  });
});

describe("EventActions resubmit editor", () => {
  it("pretty prints editable JSON and preserves newlines while typing", () => {
    render(
      <MemoryRouter>
        <EventActions
          eventDetails={
            new api.Event({
              eventId: "edit-event",
              lastMessageId: "edit-message",
              eventTypeId: "Order",
              resolutionStatus: api.ResolutionStatus.Failed,
              messageContent: new api.MessageContent({
                eventContent: new api.EventContent({
                  eventJson: '{"amount":1}',
                }),
              }),
            })
          }
          eventTypes={[new api.EventType({ id: "Order" })]}
          messages={[]}
          resubmitEvent={vi.fn()}
          skipEvent={vi.fn()}
          resubmitEventWithChanges={vi.fn()}
        />
      </MemoryRouter>,
    );
    fireEvent.click(
      screen.getByRole("button", { name: "Resubmit with changes" }),
    );
    const editor = screen.getByRole("textbox", { name: "Modified event:" });
    expect((editor as HTMLTextAreaElement).value).toBe('{\n  "amount": 1\n}');
    expect(editor.className).toContain("font-mono");
    fireEvent.change(editor, { target: { value: '{\n  "amount": 2\n}' } });
    expect((editor as HTMLTextAreaElement).value).toContain("\n");
  });
});
