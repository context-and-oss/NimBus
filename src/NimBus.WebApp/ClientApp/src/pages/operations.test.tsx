import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import OperationsPage from "./operations";

const mocks = vi.hoisted(() => ({
  getEndpointStatusCountAll: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getAdminPlatformConfig = vi.fn().mockResolvedValue({
      endpoints: [
        { id: "crm", name: "crm" },
        { id: "erp", name: "erp" },
      ],
    });
    getEndpointStatusCountAll = mocks.getEndpointStatusCountAll;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

// The Endpoints table is tested on its own; here it only needs to show what the
// page passes it and to raise the page's callbacks.
vi.mock("components/admin/endpoint-controls", () => ({
  EndpointControlsCard: (props: {
    attentionOnly: boolean;
    onOperate: (operation: string, endpointId: string) => void;
    onStatusChange: (rows: Record<string, { receive: string; send: string }>) => void;
  }) => (
    <div>
      <p>Endpoints table{props.attentionOnly ? " (needs attention)" : ""}</p>
      <button type="button" onClick={() => props.onOperate("resubmit", "erp")}>
        Resubmit erp
      </button>
      <button
        type="button"
        onClick={() =>
          props.onStatusChange({
            crm: { receive: "disabled", send: "active" },
            erp: { receive: "active", send: "active" },
          })
        }
      >
        Report statuses
      </button>
    </div>
  ),
}));

// Each card echoes the endpoint it was pre-filled with.
const { card } = vi.hoisted(() => ({
  card:
    (name: string) =>
    ({ initialEndpoint }: { initialEndpoint?: string }) => (
      <p>
        {name} card · {initialEndpoint ?? "no endpoint"}
      </p>
    ),
}));
vi.mock("components/admin/bulk-operations", () => ({
  BulkResubmitCard: card("Bulk resubmit"),
  DeleteDeadLetteredCard: card("Delete dead-lettered"),
  DeleteEventCard: card("Delete single event"),
}));
vi.mock("components/admin/advanced-operations", () => ({
  SubscriptionPurgeCard: card("Purge subscription"),
  DeleteByStatusCard: card("Delete by status"),
  SkipMessagesCard: card("Skip messages"),
  DeleteMessagesByToCard: card("Delete by To"),
  CopyEndpointCard: card("Copy endpoint data"),
  DeleteAllEventsCard: card("Delete all events"),
}));
vi.mock("components/admin/session-management", () => ({ SessionPurgeCard: card("Session purge") }));
vi.mock("components/admin/session-state", () => ({ SessionStateCard: card("Session state") }));
vi.mock("components/admin/session-cleanup", () => ({ SessionCleanupCard: card("Clean up session state") }));
vi.mock("components/admin/stale-pending-reconcile", () => ({
  StalePendingReconcileCard: card("Reconcile stale pending"),
}));

function Location() {
  const { pathname, search } = useLocation();
  return <output aria-label="location">{pathname + search}</output>;
}

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/Operations/:operation?" element={<OperationsPage />} />
      </Routes>
      <Location />
    </MemoryRouter>,
  );
}

const location = () => screen.getByLabelText("location").textContent;
const list = () => screen.getByRole("navigation", { name: "Operations" });

beforeEach(() => {
  mocks.getEndpointStatusCountAll.mockReset().mockResolvedValue([
    { endpointId: "crm", failedCount: 4, deadletterCount: 1, pendingCount: 7, unsupportedCount: 2 },
    { endpointId: "erp", failedCount: 20, deadletterCount: 5, pendingCount: 3, unsupportedCount: 0 },
  ]);
});

afterEach(() => cleanup());

describe("Operations page", () => {
  it("opens on Bulk resubmit, with the Endpoints table above the operations", () => {
    renderAt("/Operations");

    expect(screen.getByRole("heading", { name: "Operations", level: 1 })).toBeTruthy();
    expect(within(list()).getByRole("link", { name: "Bulk resubmit failed" }).getAttribute("aria-current")).toBe("page");
    expect(screen.getByText("Bulk resubmit card · no endpoint")).toBeTruthy();
    const table = screen.getByText("Endpoints table");
    expect(table.compareDocumentPosition(list()) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
  });

  it("groups operations by blast radius, safest first", () => {
    renderAt("/Operations");

    const groups = within(list()).getAllByRole("heading").map((h) => h.textContent);
    expect(groups).toEqual(["Recovery", "Cleanup", "Data movement", "Irreversible"]);
  });

  it("deep-links to an operation with its endpoint pre-filled", () => {
    renderAt("/Operations/dlq?endpoint=crm");

    expect(screen.getByText("Delete dead-lettered card · crm")).toBeTruthy();
    expect(screen.queryByText(/Bulk resubmit card/)).toBeNull();
  });

  it("gives each operation its own URL", () => {
    renderAt("/Operations");

    fireEvent.click(within(list()).getByRole("link", { name: "Skip messages" }));

    expect(location()).toBe("/Operations/skip");
    expect(screen.getByText("Skip messages card · no endpoint")).toBeTruthy();
  });

  it("switches operation from the compact picker", () => {
    renderAt("/Operations");

    fireEvent.change(screen.getByRole("combobox", { name: "Operation" }), { target: { value: "purge" } });

    expect(location()).toBe("/Operations/purge");
  });

  it("sends an unknown operation back to the default", () => {
    renderAt("/Operations/nope");

    expect(location()).toBe("/Operations");
  });

  it("opens an operation for an endpoint from the table's row action", () => {
    renderAt("/Operations/skip");

    fireEvent.click(screen.getByRole("button", { name: "Resubmit erp" }));

    expect(location()).toBe("/Operations/resubmit?endpoint=erp");
    expect(screen.getByText("Bulk resubmit card · erp")).toBeTruthy();
  });

  describe("status strip", () => {
    it("totals the endpoints' counts without double counting", async () => {
      renderAt("/Operations");

      // failedCount already includes dead-lettered; unsupported is added as the
      // sidebar's Failed badge does, and taken out of pending.
      expect((await screen.findByRole("button", { name: /^Failed/ })).textContent).toContain("26");
      expect(screen.getByRole("button", { name: /^Dead-lettered/ }).textContent).toContain("6");
      expect(screen.getByRole("button", { name: /^Pending/ }).textContent).toContain("8");
    });

    it("jumps to the matching operation", async () => {
      renderAt("/Operations/skip");

      fireEvent.click(await screen.findByRole("button", { name: /^Dead-lettered/ }));
      expect(location()).toBe("/Operations/dlq");

      fireEvent.click(screen.getByRole("button", { name: /^Pending/ }));
      expect(location()).toBe("/Operations/stale");

      fireEvent.click(screen.getByRole("button", { name: /^Failed/ }));
      expect(location()).toBe("/Operations/resubmit");
    });

    it("counts paused endpoints from the table and filters it to those needing attention", async () => {
      renderAt("/Operations");
      fireEvent.click(screen.getByRole("button", { name: "Report statuses" }));

      const paused = await screen.findByRole("button", { name: /^Endpoints paused/ });
      await waitFor(() => expect(paused.textContent).toContain("1"));
      fireEvent.click(paused);

      expect(screen.getByText("Endpoints table (needs attention)")).toBeTruthy();
    });
  });
});
