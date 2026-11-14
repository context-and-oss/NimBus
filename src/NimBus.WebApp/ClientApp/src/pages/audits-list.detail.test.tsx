import { describe, it, expect, afterEach, vi } from "vitest";
import { render, screen, cleanup, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import moment from "moment";
import * as api from "api-client";
import AuditsList from "./audits-list";

// A subscription-admin row records which operation ran in `data`; the Audit Log
// has to surface it, otherwise every operator action reads as a bare
// "manageSubscription" with no way to tell a purge from a detached rule.
const { postAuditsSearchMock } = vi.hoisted(() => ({
  postAuditsSearchMock: vi.fn(),
}));

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
  postAuditsSearchMock.mockReset();
});

const DETACH_RULE_DATA = JSON.stringify({
  topicName: "StorefrontEndpoint",
  subscriptionName: "BillingEndpoint",
  action: "detach-rule:OrderPlaced",
});

function auditEntry(overrides: Partial<api.AuditEntry>): api.AuditEntry {
  return Object.assign(new api.AuditEntry(), {
    auditorName: "Local Developer",
    createdAt: moment("2026-09-04T09:58:34.986Z"),
    ...overrides,
  });
}

async function renderAudits(audits: api.AuditEntry[]) {
  postAuditsSearchMock.mockResolvedValue({
    audits,
    continuationToken: undefined,
  });
  render(
    <MemoryRouter>
      <AuditsList />
    </MemoryRouter>,
  );
  await waitFor(() => expect(postAuditsSearchMock).toHaveBeenCalled());
}

describe("AuditsList action detail", () => {
  it("renders a readable label instead of the raw audit type", async () => {
    await renderAudits([
      auditEntry({ auditType: "manageSubscription", data: DETACH_RULE_DATA }),
    ]);

    expect(await screen.findByText("Manage subscription")).toBeTruthy();
    expect(screen.queryByText("manageSubscription")).toBeNull();
  });

  it("shows the operation recorded in the audit data", async () => {
    await renderAudits([
      auditEntry({ auditType: "manageSubscription", data: DETACH_RULE_DATA }),
    ]);

    // The specific operation must be legible — not just the audit category.
    expect(
      await screen.findByText(/detach-rule:OrderPlaced/),
    ).toBeTruthy();
  });

  it("marks a denied attempt so it cannot be read as a completed action", async () => {
    await renderAudits([
      auditEntry({
        auditType: "manageSubscription",
        data: JSON.stringify({
          topicName: "StorefrontEndpoint",
          subscriptionName: "BillingEndpoint",
          action: "purge",
        }),
        accessDenied: true,
      }),
    ]);

    expect(await screen.findByText("Denied")).toBeTruthy();
  });

  it("leaves a row without data readable", async () => {
    await renderAudits([
      auditEntry({ auditType: "sendHeartbeatNow" }),
    ]);

    expect(await screen.findByText("Send heartbeat now")).toBeTruthy();
  });
});
