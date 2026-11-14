import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import AuditsList from "./audits-list";

const mocks = vi.hoisted(() => ({
  access: { current: null as Record<string, unknown> | null },
}));

vi.mock("hooks/use-access", () => ({ useAccess: () => ({ access: mocks.access.current }) }));
vi.mock("components/ui/toast", () => ({ useToast: () => ({ addToast: () => {} }) }));
vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getEndpointsAll = vi.fn().mockResolvedValue([]);
    postAuditsSearch = vi.fn().mockResolvedValue({ audits: [] });
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

const renderPage = () =>
  render(
    <MemoryRouter>
      <AuditsList />
    </MemoryRouter>,
  );

beforeEach(() => {
  mocks.access.current = null;
});

afterEach(() => cleanup());

// Spec 038 §9.3: what gets recorded is one click away for those who can change it.
describe("Audit Log settings link", () => {
  it("links a site Owner to the audit logging settings", () => {
    mocks.access.current = { canManageAccessControl: true, endpointRoles: [] };
    renderPage();

    expect(screen.getByRole("link", { name: /Configure recording/ }).getAttribute("href")).toBe(
      "/Settings/audit",
    );
  });

  it("does not offer the settings to anyone else", () => {
    mocks.access.current = { canManageAccessControl: false, endpointRoles: [] };
    renderPage();

    expect(screen.queryByRole("link", { name: /Configure recording/ })).toBeNull();
  });
});
