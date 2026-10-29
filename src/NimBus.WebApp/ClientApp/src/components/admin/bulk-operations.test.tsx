import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { BulkResubmitCard, DeleteEventCard } from "./bulk-operations";

const mocks = vi.hoisted(() => ({
  getAdminFailedPreview: vi.fn(),
  postAdminBulkResubmit: vi.fn(),
  deleteAdminEvent: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getAdminFailedPreview = mocks.getAdminFailedPreview;
    postAdminBulkResubmit = mocks.postAdminBulkResubmit;
    deleteAdminEvent = mocks.deleteAdminEvent;
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

  async function deleteEvent(eventId: string) {
    render(<DeleteEventCard endpoints={endpoints} initialEndpoint="crm" />);
    fireEvent.change(screen.getByPlaceholderText("Enter event ID..."), { target: { value: eventId } });
    fireEvent.click(screen.getByRole("button", { name: "Delete Event" }));
    const dialog = screen.getByRole("dialog", { name: "Delete Single Event" });
    fireEvent.change(within(dialog).getByPlaceholderText(eventId), { target: { value: eventId } });
    fireEvent.click(within(dialog).getByRole("button", { name: "Delete event" }));
  }

  // Issue #288: the API refuses to delete the event a session is blocked on.
  it("says to skip an event that blocks its session instead of claiming it is missing", async () => {
    mocks.deleteAdminEvent.mockRejectedValue({ status: 409 });

    await deleteEvent("evt-42");

    expect((await screen.findByText(/blocks its session/)).textContent).toContain("Skip");
    expect(screen.queryByText("Event not found.")).toBeNull();
  });

  it("reports any other failure as a failure, not as a missing event", async () => {
    mocks.deleteAdminEvent.mockRejectedValue({ status: 500 });

    await deleteEvent("evt-42");

    expect(await screen.findByText(/could not be deleted/)).toBeTruthy();
    expect(screen.queryByText("Event not found.")).toBeNull();
  });
});
