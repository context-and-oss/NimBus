import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import * as api from "api-client";
import { SessionStateCard } from "./session-state";

const { getMock, postMock } = vi.hoisted(() => ({
  getMock: vi.fn(),
  postMock: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual = await vi.importActual<typeof import("api-client")>("api-client");
  class Client {
    getAdminSessionState = getMock;
    postAdminSessionState = postMock;
  }
  return { ...actual, Client, CookieAuth: () => ({}) };
});

afterEach(() => {
  cleanup();
  vi.resetAllMocks();
});

const endpoints = [{ value: "Crm", label: "Crm" }];

function view(overrides: Partial<api.ISessionStateView> = {}) {
  return new api.SessionStateView({
    sessionId: "order-1",
    locked: false,
    readable: true,
    isEmpty: false,
    stateJson: '{"BlockedByEventId":"evt-1","DeferredCount":0,"NextDeferralSequence":2,"ReplayedThroughSequence":1}',
    stateVersion: "v1",
    blockedByEventId: "evt-1",
    deferredCount: 0,
    nextDeferralSequence: 2,
    replayedThroughSequence: 1,
    legacyDeferredCount: 0,
    blocked: true,
    hasUnreplayedMessages: false,
    activeMessageCount: 3,
    deferredSubscriptionCount: 0,
    storePendingCount: 0,
    storeDeferredCount: 0,
    blockingEventStatus: "Failed",
    ...overrides,
  });
}

async function lookUp() {
  render(<SessionStateCard endpoints={endpoints} initialEndpoint="Crm" />);
  await userEvent.type(screen.getByLabelText("Session ID"), "order-1");
  await userEvent.click(screen.getByRole("button", { name: "Look up session state" }));
  await screen.findByRole("region", { name: "Session state details" });
}

async function confirm(text: string) {
  const dialog = await screen.findByRole("dialog");
  await userEvent.type(within(dialog).getByPlaceholderText(text), text);
  const buttons = within(dialog).getAllByRole("button");
  await userEvent.click(buttons[buttons.length - 1]);
}

describe("SessionStateCard", () => {
  it("shows the stored state, the blocking event and what is queued", async () => {
    getMock.mockResolvedValue(view());

    await lookUp();

    expect(getMock).toHaveBeenCalledWith("Crm", "order-1");
    expect(screen.getByText("Blocked")).toBeTruthy();
    expect(screen.getByText("evt-1")).toBeTruthy();
    expect(screen.getByText("(Failed)")).toBeTruthy();
    expect(screen.getByLabelText("Stored session state").textContent).toContain('"NextDeferralSequence": 2');
  });

  it("says when a receiver holds the session and offers no changes", async () => {
    getMock.mockResolvedValue(view({ locked: true, stateVersion: undefined, message: "A receiver holds the session." }));

    await lookUp();

    expect(screen.getByText("Locked by a receiver")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "Clear block" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Reset state" })).toBeNull();
  });

  it("clears the block at the version it read, after a typed confirmation", async () => {
    getMock.mockResolvedValue(view());
    postMock.mockResolvedValue(view({ blocked: false, blockedByEventId: undefined, stateVersion: "v2" }));

    await lookUp();
    await userEvent.click(screen.getByRole("button", { name: "Clear block" }));
    await confirm("Crm/order-1");

    await waitFor(() => expect(postMock).toHaveBeenCalledTimes(1));
    const [request, endpoint, session] = postMock.mock.calls[0];
    expect(request.action).toBe(api.SessionStateChangeRequestAction.ClearBlock);
    expect(request.expectedVersion).toBe("v1");
    expect(endpoint).toBe("Crm");
    expect(session).toBe("order-1");
    expect(await screen.findByText("Clear block: done.")).toBeTruthy();
    expect(screen.getByText("Not blocked")).toBeTruthy();
  });

  it("sends edited JSON as a replace", async () => {
    getMock.mockResolvedValue(view());
    postMock.mockResolvedValue(view({ stateVersion: "v2" }));

    await lookUp();
    await userEvent.click(screen.getByRole("button", { name: "Edit JSON…" }));
    const editor = screen.getByLabelText("Session state JSON");
    await userEvent.clear(editor);
    await userEvent.type(editor, '{{"DeferredCount":0}');
    await userEvent.click(screen.getByRole("button", { name: "Save state" }));
    await confirm("Crm/order-1");

    await waitFor(() => expect(postMock).toHaveBeenCalledTimes(1));
    const [request] = postMock.mock.calls[0];
    expect(request.action).toBe(api.SessionStateChangeRequestAction.Replace);
    expect(request.stateJson).toBe('{"DeferredCount":0}');
    expect(request.expectedVersion).toBe("v1");
  });

  it("shows the current state when it changed since it was read", async () => {
    getMock.mockResolvedValue(view());
    // The 409 is a problem whose members are the SessionStateView; the client parses it onto .result.
    postMock.mockRejectedValue(
      new api.SwaggerException("Conflict", 409, "", {}, view({ blockedByEventId: "evt-2", stateVersion: "v9" })),
    );

    await lookUp();
    await userEvent.click(screen.getByRole("button", { name: "Reset state" }));
    await confirm("Crm/order-1");

    expect(await screen.findByRole("alert")).toBeTruthy();
    expect(screen.getByRole("alert").textContent).toContain("changed since you read it");
    expect(screen.getByText("evt-2")).toBeTruthy();
  });

  it("disables Mark parked messages replayed while messages are parked", async () => {
    getMock.mockResolvedValue(view({ blocked: false, blockedByEventId: undefined, deferredCount: 1, deferredSubscriptionCount: 1 }));

    await lookUp();

    expect((screen.getByRole("button", { name: "Mark parked messages replayed" }) as HTMLButtonElement).disabled).toBe(true);
  });
});
