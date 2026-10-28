import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { BulkResubmitCard, DeleteDeadLetteredCard, DeleteEventCard } from "./bulk-operations";

const mocks = vi.hoisted(() => ({
  getAdminFailedPreview: vi.fn(),
  postAdminBulkResubmit: vi.fn(),
  deleteAdminEvent: vi.fn(),
  getAdminDeadletteredPreview: vi.fn(),
  postAdminDeleteDeadlettered: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getAdminFailedPreview = mocks.getAdminFailedPreview;
    postAdminBulkResubmit = mocks.postAdminBulkResubmit;
    deleteAdminEvent = mocks.deleteAdminEvent;
    getAdminDeadletteredPreview = mocks.getAdminDeadletteredPreview;
    postAdminDeleteDeadlettered = mocks.postAdminDeleteDeadlettered;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

const endpoints = [
  { value: "crm", label: "crm" },
  { value: "erp", label: "erp" },
];

beforeEach(() => {
  mocks.getAdminFailedPreview.mockReset().mockResolvedValue({ totalFailed: 3, eligibleCount: 3, sampleEventIds: [] });
  mocks.postAdminBulkResubmit.mockReset().mockResolvedValue({ processed: 3, succeeded: 3, failed: 0 });
  mocks.deleteAdminEvent.mockReset().mockResolvedValue(undefined);
  mocks.getAdminDeadletteredPreview.mockReset().mockResolvedValue({ count: 2, queueCount: 5, matchingQueueCount: 2 });
  mocks.postAdminDeleteDeadlettered.mockReset().mockResolvedValue({ processed: 7, succeeded: 7, failed: 0 });
});

afterEach(() => cleanup());

describe("BulkResubmitCard", () => {
  it("starts without an endpoint", () => {
    render(<BulkResubmitCard endpoints={endpoints} />);

    expect((screen.getByRole("button", { name: "Preview" }) as HTMLButtonElement).disabled).toBe(true);
  });

  // Spec 038 §6.2: a row action opens the operation pre-filled with its endpoint.
  it("starts from a pre-filled endpoint", async () => {
    render(<BulkResubmitCard endpoints={endpoints} initialEndpoint="erp" />);

    fireEvent.click(screen.getByRole("button", { name: "Preview" }));

    await waitFor(() => expect(mocks.getAdminFailedPreview).toHaveBeenCalledWith("erp"));
  });

  // Spec 038 §6.4: recovery is confirmed but not presented as destructive.
  it("confirms a resubmit with a plain Resubmit button", async () => {
    render(<BulkResubmitCard endpoints={endpoints} initialEndpoint="erp" />);
    fireEvent.click(screen.getByRole("button", { name: "Preview" }));
    fireEvent.click(await screen.findByRole("button", { name: /^Resubmit/ }));

    const dialog = screen.getByRole("dialog", { name: "Bulk Resubmit Failed Messages" });
    expect(within(dialog).queryByText("This action cannot be undone.")).toBeNull();
    fireEvent.change(within(dialog).getByPlaceholderText("erp"), { target: { value: "erp" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Resubmit" }));

    await waitFor(() => expect(mocks.postAdminBulkResubmit).toHaveBeenCalledWith("erp"));
  });
});

describe("DeleteEventCard", () => {
  // Spec 038 slice 3: single delete gains the typed confirmation every other delete has.
  it("asks for the event ID to be typed before deleting", async () => {
    render(<DeleteEventCard endpoints={endpoints} initialEndpoint="crm" />);
    fireEvent.change(screen.getByPlaceholderText("Enter event ID..."), { target: { value: "evt-42" } });

    fireEvent.click(screen.getByRole("button", { name: "Delete Event" }));

    const dialog = screen.getByRole("dialog", { name: "Delete Single Event" });
    expect(mocks.deleteAdminEvent).not.toHaveBeenCalled();
    const confirm = within(dialog).getByRole("button", { name: "Delete event" }) as HTMLButtonElement;
    expect(confirm.disabled).toBe(true);

    fireEvent.change(within(dialog).getByPlaceholderText("evt-42"), { target: { value: "evt-42" } });
    fireEvent.click(confirm);

    await waitFor(() => expect(mocks.deleteAdminEvent).toHaveBeenCalledWith("crm", "evt-42"));
  });
});

// Issue #286: the delete also removes the stored events' messages from the subscription's
// dead-letter queue. Messages with no stored event stay for Admin → Subscriptions, so the
// preview says how many there are and where to purge them.
describe("DeleteDeadLetteredCard", () => {
  async function preview() {
    render(<DeleteDeadLetteredCard endpoints={endpoints} initialEndpoint="crm" />);
    fireEvent.click(screen.getByRole("button", { name: "Preview" }));
    await waitFor(() => expect(mocks.getAdminDeadletteredPreview).toHaveBeenCalledWith("crm"));
  }

  it("previews the stored events, their queue messages and the rest of the queue", async () => {
    mocks.getAdminDeadletteredPreview.mockResolvedValue({ count: 2, queueCount: 5, matchingQueueCount: 2 });
    await preview();

    expect((await screen.findByText(/Stored dead-lettered events/)).textContent).toContain("2");
    expect(screen.getByText(/Their messages in the dead-letter queue/).textContent).toContain("2");
    expect(screen.getByText(/3 more messages in the dead-letter queue/).textContent).toContain("Admin → Subscriptions");
  });

  it("deletes the stored events and their queue messages", async () => {
    mocks.getAdminDeadletteredPreview.mockResolvedValue({ count: 2, queueCount: 2, matchingQueueCount: 2 });
    await preview();

    fireEvent.click(await screen.findByRole("button", { name: "Delete dead-lettered messages" }));

    const dialog = screen.getByRole("dialog", { name: "Delete Dead-Lettered Messages" });
    expect(dialog.textContent).toContain("2 stored dead-lettered events and their 2 messages in the dead-letter queue");
    fireEvent.change(within(dialog).getByPlaceholderText("crm"), { target: { value: "crm" } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Delete dead-lettered" }));

    await waitFor(() => expect(mocks.postAdminDeleteDeadlettered).toHaveBeenCalledWith("crm"));
  });

  it("offers no delete when only messages without a stored event are left", async () => {
    mocks.getAdminDeadletteredPreview.mockResolvedValue({ count: 0, queueCount: 3, matchingQueueCount: 0 });
    await preview();

    expect((await screen.findByText(/3 more messages in the dead-letter queue/)).textContent).toContain("Purge dead letters");
    expect(screen.queryByRole("button", { name: "Delete dead-lettered messages" })).toBeNull();
  });

  it("says so when the dead-letter queue could not be read", async () => {
    mocks.getAdminDeadletteredPreview.mockResolvedValue({ count: 1, queueCount: null, matchingQueueCount: null });
    await preview();

    expect((await screen.findByText(/Their messages in the dead-letter queue/)).textContent).toContain("unknown");
    expect(screen.getByRole("button", { name: "Delete dead-lettered messages" })).toBeTruthy();
  });
});
