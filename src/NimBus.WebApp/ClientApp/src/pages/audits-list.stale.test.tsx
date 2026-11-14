import { describe, it, expect, afterEach, vi } from "vitest";
import {
  act,
  render,
  screen,
  cleanup,
  fireEvent,
  waitFor,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import moment from "moment";
import * as api from "api-client";
import AuditsList from "./audits-list";

// Each postAuditsSearch call parks its resolver in `auditsDeferreds` so the
// test can settle the two in-flight fetches out of order.
const { auditsDeferreds, postAuditsSearchMock } = vi.hoisted(() => {
  const auditsDeferreds: Array<(value: unknown) => void> = [];
  const postAuditsSearchMock = vi.fn(
    () =>
      new Promise((resolve) => {
        auditsDeferreds.push(resolve);
      }),
  );
  return { auditsDeferreds, postAuditsSearchMock };
});

// DataTable reads the toast provider for action feedback; no-op it.
// No site-Owner lookup in these tests (the page asks to show its settings link).
vi.mock("hooks/use-access", () => ({ useAccess: () => ({ access: null }) }));

vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast: () => {} }),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") =
    await vi.importActual("api-client");
  class FakeClient {
    getEndpointsAll = vi.fn().mockResolvedValue([]);
    postAuditsSearch = postAuditsSearchMock;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

afterEach(() => {
  cleanup();
  auditsDeferreds.length = 0;
  postAuditsSearchMock.mockClear();
});

function auditRow(auditorName: string): api.AuditEntry {
  return Object.assign(new api.AuditEntry(), {
    auditorName,
    eventId: `evt-${auditorName}`,
    createdAt: moment("2026-05-28T09:59:30.000Z"),
  });
}

describe("AuditsList stale-response guard", () => {
  it("keeps the newer fetch's rows when an older fetch resolves last", async () => {
    render(
      <MemoryRouter>
        <AuditsList />
      </MemoryRouter>,
    );

    // Fetch #1 (soon-to-be-stale) fired on mount.
    await waitFor(() =>
      expect(postAuditsSearchMock).toHaveBeenCalledTimes(1),
    );

    // Commit a filter change while #1 is still in flight — this fires fetch #2.
    const auditorInput = screen.getByPlaceholderText("Filter by auditor...");
    fireEvent.change(auditorInput, { target: { value: "alice" } });
    fireEvent.keyDown(auditorInput, { key: "Enter" });

    await waitFor(() =>
      expect(postAuditsSearchMock).toHaveBeenCalledTimes(2),
    );

    // Settle the NEWER fetch first, then the older (stale) one.
    await act(async () => {
      auditsDeferreds[1]({
        audits: [auditRow("fresh-auditor")],
        continuationToken: undefined,
      });
    });
    await act(async () => {
      auditsDeferreds[0]({
        audits: [auditRow("stale-auditor")],
        continuationToken: undefined,
      });
    });

    // The stale response must not clobber the fresher rows.
    await waitFor(() =>
      expect(screen.getByText("fresh-auditor")).toBeTruthy(),
    );
    expect(screen.queryByText("stale-auditor")).toBeNull();
  });
});
