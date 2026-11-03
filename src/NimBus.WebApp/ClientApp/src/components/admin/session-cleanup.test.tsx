import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import * as api from "api-client";
import moment from "moment";
import { SessionCleanupCard } from "./session-cleanup";

const { previewMock, cleanupMock } = vi.hoisted(() => ({
  previewMock: vi.fn(),
  cleanupMock: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual = await vi.importActual<typeof import("api-client")>("api-client");
  class Client {
    postAdminSessionCleanupPreview = previewMock;
    postAdminSessionCleanup = cleanupMock;
  }
  return { ...actual, Client, CookieAuth: () => ({}) };
});

afterEach(() => {
  cleanup();
  vi.resetAllMocks();
});

const C = api.SessionCleanupCandidateClassification;

function preview() {
  return new api.SessionCleanupPreview({
    inspectedCount: 5,
    emptyCount: 1,
    truncated: false,
    sessions: [
      new api.SessionCleanupCandidate({ sessionId: "idle-1", classification: C.Idle, reason: "Nothing queued, parked or blocked.", stateVersion: "a" }),
      new api.SessionCleanupCandidate({ sessionId: "idle-2", classification: C.Idle, reason: "Nothing queued, parked or blocked.", stateVersion: "b" }),
      new api.SessionCleanupCandidate({ sessionId: "stale-1", classification: C.StaleBlock, reason: "Blocked by event e, which is Completed.", stateVersion: "c" }),
      new api.SessionCleanupCandidate({ sessionId: "busy-1", classification: C.InUse, reason: "3 messages queued on the session.", stateVersion: "d" }),
    ],
  });
}

async function renderAndPreview() {
  render(<SessionCleanupCard endpoints={[{ value: "Crm", label: "Crm" }]} initialEndpoint="Crm" />);
  await userEvent.click(screen.getByRole("button", { name: "Preview cleanup" }));
  await screen.findByText("busy-1");
}

describe("SessionCleanupCard", () => {
  it("previews with the window and cap, and preselects only idle sessions", async () => {
    previewMock.mockResolvedValue(preview());

    await renderAndPreview();

    const [request, endpoint] = previewMock.mock.calls[0];
    expect(endpoint).toBe("Crm");
    expect(request.maxSessions).toBe(200);
    expect(request.includeStaleBlocks).toBe(false);
    expect(moment.isMoment(request.updatedSince)).toBe(true);
    expect(screen.getByRole("status").textContent).toContain("2 idle, 1 stale blocks, 1 in use");
    expect((screen.getByLabelText("Reset idle-1") as HTMLInputElement).checked).toBe(true);
    expect((screen.getByLabelText("Reset stale-1") as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByLabelText("Reset busy-1") as HTMLInputElement).disabled).toBe(true);
    expect(screen.getByRole("button", { name: "Reset 2 sessions" })).toBeTruthy();
  });

  it("resets only the selected sessions with their previewed versions", async () => {
    previewMock.mockResolvedValue(preview());
    cleanupMock.mockResolvedValue(new api.SessionCleanupResult({
      resetCount: 1,
      skipped: [],
      errors: [],
    }));

    await renderAndPreview();
    await userEvent.click(screen.getByLabelText("Reset idle-2"));
    await userEvent.click(screen.getByRole("button", { name: "Reset 1 session" }));
    const dialog = await screen.findByRole("dialog");
    await userEvent.type(within(dialog).getByPlaceholderText("Crm"), "Crm");
    await userEvent.click(within(dialog).getByRole("button", { name: "Reset session state" }));

    await waitFor(() => expect(cleanupMock).toHaveBeenCalledTimes(1));
    const [request, endpoint] = cleanupMock.mock.calls[0];
    expect(endpoint).toBe("Crm");
    expect(request.sessions.map((s: api.SessionCleanupSelection) => [s.sessionId, s.stateVersion])).toEqual([["idle-1", "a"]]);
    expect(await screen.findByText("Cleanup complete: 1 reset")).toBeTruthy();
  });

  it("lets stale blocks be selected when they are included", async () => {
    previewMock.mockResolvedValue(preview());

    await renderAndPreview();
    await userEvent.click(screen.getByLabelText(/Also reset stale blocks/));

    const stale = screen.getByLabelText("Reset stale-1") as HTMLInputElement;
    expect(stale.disabled).toBe(false);
    await userEvent.click(stale);
    expect(screen.getByRole("button", { name: "Reset 3 sessions" })).toBeTruthy();
  });
});
