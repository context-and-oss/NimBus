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
    hidden: boolean;
    onOperate: (operation: string, endpointId: string) => void;
    onStatusChange: (rows: Record<string, { receive: string; send: string }>) => void;
  }) => (
    <div>
      {!props.hidden && <p>Endpoints table{props.attentionOnly ? " (needs attention)" : ""}</p>}
      {!props.hidden && (
        <button type="button" onClick={() => props.onOperate("resubmit", "erp")}>
          Resubmit erp
        </button>
      )}
      <button
        type="button"
        onClick={() =>
          props.onStatusChange({
            crm: { receive: "disabled", send: "active" },
            erp: { receive: "active", send: "active" },
          })
        }
      >
        Report statuses{props.hidden ? " (hidden)" : ""}
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
const operations = () => screen.getByRole("table", { name: "Operations" });
const inspector = () => screen.getByRole("complementary", { name: "Inspector" });
const tab = (name: RegExp) => screen.getByRole("tab", { name });

beforeEach(() => {
  mocks.getEndpointStatusCountAll.mockReset().mockResolvedValue([
    { endpointId: "crm", failedCount: 6, deadletterCount: 1, pendingCount: 5, unsupportedCount: 2 },
    { endpointId: "erp", failedCount: 20, deadletterCount: 5, pendingCount: 3, unsupportedCount: 0 },
  ]);
});

afterEach(() => cleanup());

describe("Operations page", () => {
  it("opens on the Endpoints table, with the operations one tab away", () => {
    renderAt("/Operations");

    expect(screen.getByRole("heading", { name: "Operations", level: 1 })).toBeTruthy();
    expect(tab(/Endpoints/).getAttribute("aria-selected")).toBe("true");
    expect(screen.getByText("Endpoints table")).toBeTruthy();
    expect(screen.queryByRole("table", { name: "Operations" })).toBeNull();

    fireEvent.click(tab(/Operations/));

    expect(location()).toBe("/Operations/resubmit");
    expect(screen.getByText("Bulk resubmit card · no endpoint")).toBeTruthy();
    expect(screen.queryByText("Endpoints table")).toBeNull();
  });

  it("lists operations by blast radius, safest first, and filters them by group", () => {
    renderAt("/Operations/resubmit");

    const groups = within(operations())
      .getAllByRole("row")
      .slice(1)
      .map((r) => within(r).getAllByRole("cell")[1].textContent);
    expect([...new Set(groups)]).toEqual(["Recovery", "Cleanup", "Data movement", "Irreversible"]);

    fireEvent.click(within(screen.getByRole("group", { name: "Group" })).getByRole("button", { name: /Irreversible/ }));

    expect(within(operations()).getAllByRole("row").slice(1)).toHaveLength(1);
    expect(within(operations()).getByRole("button", { name: "Delete all events" })).toBeTruthy();
  });

  it("deep-links to an operation with its endpoint pre-filled, shown in the inspector", () => {
    renderAt("/Operations/dlq?endpoint=crm");

    expect(tab(/Operations/).getAttribute("aria-selected")).toBe("true");
    expect(within(operations()).getByRole("button", { name: "Delete dead-lettered" }).getAttribute("aria-pressed")).toBe(
      "true",
    );
    expect(within(inspector()).getByRole("heading", { name: /Delete dead-lettered/ })).toBeTruthy();
    expect(within(inspector()).getByText("Delete dead-lettered card · crm")).toBeTruthy();
    expect(screen.queryByText(/Bulk resubmit card/)).toBeNull();
  });

  it("closes the operation's drawer back to the list, and its row opens it again", () => {
    renderAt("/Operations/dlq?endpoint=crm");

    fireEvent.click(within(inspector()).getByRole("button", { name: "Close inspector" }));

    expect(screen.queryByRole("complementary", { name: "Inspector" })).toBeNull();
    const row = within(operations()).getByRole("button", { name: "Delete dead-lettered" });
    expect(row.getAttribute("aria-pressed")).toBe("false");
    expect(location()).toBe("/Operations/dlq?endpoint=crm");

    fireEvent.click(row);
    expect(within(inspector()).getByText("Delete dead-lettered card · crm")).toBeTruthy();
  });

  it("gives each operation its own URL and keeps the endpoint when switching", () => {
    renderAt("/Operations/resubmit?endpoint=erp");

    fireEvent.click(within(operations()).getByRole("button", { name: "Skip messages" }));

    expect(location()).toBe("/Operations/skip?endpoint=erp");
    expect(screen.getByText("Skip messages card · erp")).toBeTruthy();
  });

  it("does not pre-fill an operation that doesn't start from an endpoint", () => {
    renderAt("/Operations/to?endpoint=erp");

    expect(screen.getByText("Delete by To card · no endpoint")).toBeTruthy();
  });

  it("sends an unknown operation back to the Endpoints table", () => {
    renderAt("/Operations/nope");

    expect(location()).toBe("/Operations");
    expect(screen.getByText("Endpoints table")).toBeTruthy();
  });

  it("opens an operation for an endpoint from the Endpoints table", () => {
    renderAt("/Operations");

    fireEvent.click(screen.getByRole("button", { name: "Resubmit erp" }));

    expect(location()).toBe("/Operations/resubmit?endpoint=erp");
    expect(screen.getByText("Bulk resubmit card · erp")).toBeTruthy();
  });

  it("filters the Endpoints table from the chips", () => {
    renderAt("/Operations");

    fireEvent.click(within(screen.getByRole("group", { name: "Show" })).getByRole("button", { name: "Needs attention" }));
    expect(screen.getByText("Endpoints table (needs attention)")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "All endpoints" }));
    expect(screen.getByText("Endpoints table")).toBeTruthy();
  });

  describe("header figures", () => {
    it("totals the endpoints' counts without double counting", async () => {
      renderAt("/Operations");

      // failedCount already includes dead-lettered and unsupported, and
      // pendingCount is pending only, so nothing is added or taken out.
      await waitFor(() => expect(screen.getByRole("button", { name: /Failed$/ }).textContent).toContain("26"));
      expect(screen.getByRole("button", { name: /Dead-lettered$/ }).textContent).toContain("6");
      expect(screen.getByRole("button", { name: /Pending$/ }).textContent).toContain("8");
    });

    it("jumps to the matching operation", async () => {
      renderAt("/Operations/skip");

      fireEvent.click(await screen.findByRole("button", { name: /Dead-lettered$/ }));
      expect(location()).toBe("/Operations/dlq");

      fireEvent.click(screen.getByRole("button", { name: /Pending$/ }));
      expect(location()).toBe("/Operations/stale");

      fireEvent.click(screen.getByRole("button", { name: /Failed$/ }));
      expect(location()).toBe("/Operations/resubmit");
    });

    it("counts paused endpoints from the table and filters it to those needing attention", async () => {
      renderAt("/Operations");
      fireEvent.click(screen.getByRole("button", { name: "Report statuses" }));

      const paused = await screen.findByRole("button", { name: /Paused$/ });
      await waitFor(() => expect(paused.textContent).toContain("1"));
      fireEvent.click(paused);

      expect(screen.getByText("Endpoints table (needs attention)")).toBeTruthy();
    });

    it("keeps the paused count while an operation is open, and returns to the endpoints from it", async () => {
      renderAt("/Operations/skip");
      // The Endpoints table stays mounted, hidden, so it still reports.
      fireEvent.click(screen.getByRole("button", { name: "Report statuses (hidden)" }));

      const paused = await screen.findByRole("button", { name: /Paused$/ });
      await waitFor(() => expect(paused.textContent).toContain("1"));
      fireEvent.click(paused);

      expect(location()).toBe("/Operations");
      expect(screen.getByText("Endpoints table (needs attention)")).toBeTruthy();
    });
  });
});
