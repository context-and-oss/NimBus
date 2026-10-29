import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { DeleteByStatusCard } from "./advanced-operations";

const mocks = vi.hoisted(() => ({
  postAdminDeleteByStatusPreview: vi.fn(),
  postAdminDeleteByStatus: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    postAdminDeleteByStatusPreview = mocks.postAdminDeleteByStatusPreview;
    postAdminDeleteByStatus = mocks.postAdminDeleteByStatus;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

const endpoints = [{ value: "crm", label: "crm" }];

beforeEach(() => {
  mocks.postAdminDeleteByStatusPreview.mockReset().mockResolvedValue({ count: 1, blockingCount: 0 });
  mocks.postAdminDeleteByStatus.mockReset().mockResolvedValue({ processed: 1, succeeded: 1, failed: 0 });
});

afterEach(() => cleanup());

// Issue #288: the delete keeps an event that blocks its session (a failure, or a pending hand-off),
// because deleting it would leave the session blocked by an event no one can see.
describe("DeleteByStatusCard", () => {
  async function preview(status: string) {
    render(<DeleteByStatusCard endpoints={endpoints} initialEndpoint="crm" />);
    fireEvent.click(screen.getByLabelText(status));
    fireEvent.click(screen.getByRole("button", { name: "Preview" }));
    await waitFor(() => expect(mocks.postAdminDeleteByStatusPreview).toHaveBeenCalled());
  }

  it("says how many events it keeps because they block their session, and how to delete them", async () => {
    mocks.postAdminDeleteByStatusPreview.mockResolvedValue({ count: 1, blockingCount: 2 });
    await preview("Failed");

    const note = await screen.findByText(/block their session/);
    expect(note.textContent).toContain("2");
    expect(note.textContent).toContain("Skip messages");
    expect(note.textContent).toContain("Skipped");
  });

  it("says nothing about kept events when there are none", async () => {
    await preview("Failed");

    await screen.findByText(/Events matching/);
    expect(screen.queryByText(/block their session/)).toBeNull();
  });

  it("can delete skipped events, so skip-then-delete works from here", async () => {
    await preview("Skipped");

    const request = mocks.postAdminDeleteByStatusPreview.mock.calls[0][1];
    expect(request.statuses).toEqual(["Skipped"]);
  });
});
