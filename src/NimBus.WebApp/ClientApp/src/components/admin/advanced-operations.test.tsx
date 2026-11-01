import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { DeleteByStatusCard, SubscriptionPurgeCard } from "./advanced-operations";

const mocks = vi.hoisted(() => ({
  postAdminDeleteByStatusPreview: vi.fn(),
  postAdminDeleteByStatus: vi.fn(),
  postAdminPurgePreview: vi.fn(),
  postAdminPurge: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    postAdminDeleteByStatusPreview = mocks.postAdminDeleteByStatusPreview;
    postAdminDeleteByStatus = mocks.postAdminDeleteByStatus;
    postAdminPurgePreview = mocks.postAdminPurgePreview;
    postAdminPurge = mocks.postAdminPurge;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

const endpoints = [{ value: "crm", label: "crm" }];

beforeEach(() => {
  mocks.postAdminDeleteByStatusPreview.mockReset().mockResolvedValue({ count: 1, blockingCount: 0 });
  mocks.postAdminDeleteByStatus.mockReset().mockResolvedValue({ processed: 1, succeeded: 1, failed: 0 });
  mocks.postAdminPurgePreview.mockReset().mockResolvedValue({ totalScanned: 4, totalMatching: 3, sessionCount: 1 });
  mocks.postAdminPurge.mockReset().mockResolvedValue({ processed: 3, succeeded: 3, failed: 0, errors: [], rowsDeleted: 2 });
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

// Issue #289: the purge deletes the stored Pending and Deferred rows of the messages it removes.
describe("SubscriptionPurgeCard", () => {
  async function openConfirm() {
    render(<SubscriptionPurgeCard endpoints={endpoints} initialEndpoint="crm" />);
    fireEvent.click(screen.getByRole("button", { name: "Preview" }));
    fireEvent.click(await screen.findByRole("button", { name: "Purge 3 messages" }));
  }

  async function purge() {
    await openConfirm();
    // The dialog's confirmation input is the last one asking for the endpoint name.
    fireEvent.change(screen.getAllByPlaceholderText("crm").at(-1)!, { target: { value: "crm" } });
    fireEvent.click(screen.getByRole("button", { name: "Purge subscription" }));
    await waitFor(() => expect(mocks.postAdminPurge).toHaveBeenCalled());
  }

  it("says the confirmation also deletes stored Pending and Deferred rows", async () => {
    await openConfirm();

    const description = screen.getByText(/purge 3 messages from endpoint "crm"/);
    expect(description.textContent).toMatch(/stored Pending and Deferred rows/);
  });

  it("shows how many stored rows the purge deleted", async () => {
    await purge();

    expect((await screen.findByText(/Stored rows deleted/)).textContent).toContain("2");
  });

  it("shows zero stored rows deleted", async () => {
    mocks.postAdminPurge.mockResolvedValue({ processed: 1, succeeded: 1, failed: 0, errors: [], rowsDeleted: 0 });
    await purge();

    expect((await screen.findByText(/Stored rows deleted/)).textContent).toContain("0");
  });
});
