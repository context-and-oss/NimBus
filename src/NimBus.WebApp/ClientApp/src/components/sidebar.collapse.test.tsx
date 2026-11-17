import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import Sidebar, { SIDEBAR_COLLAPSED_KEY } from "./sidebar";
import { simulationStatus } from "components/simulate/simulation-fixtures";

const mocks = vi.hoisted(() => ({
  getAdminSimulation: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getAdminSimulation = mocks.getAdminSimulation;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});
vi.mock("hooks/use-access", () => ({
  useAccess: () => ({ access: { canManageAccessControl: true, endpointRoles: [] } }),
}));
vi.mock("hooks/app-status", () => ({ useEnv: () => "dev" }));
vi.mock("components/sidebar-user-footer", () => ({ default: () => null }));

const renderSidebar = () =>
  render(
    <MemoryRouter initialEntries={["/Settings/mcp"]}>
      <Sidebar />
    </MemoryRouter>,
  );

beforeEach(() => {
  window.localStorage.removeItem(SIDEBAR_COLLAPSED_KEY);
  mocks.getAdminSimulation.mockReset().mockResolvedValue(simulationStatus({ enabled: false }));
});

afterEach(() => cleanup());

// The icon-only sidebar gives wide pages such as Settings the room its labels take.
describe("Sidebar collapse", () => {
  it("starts expanded and collapses to icons that keep their names", async () => {
    renderSidebar();
    await waitFor(() => expect(mocks.getAdminSimulation).toHaveBeenCalled());
    expect(screen.getByText("A nimbus for your Azure cloud.")).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Collapse sidebar" }));

    const toggle = screen.getByRole("button", { name: "Expand sidebar" });
    expect(toggle.getAttribute("aria-expanded")).toBe("false");
    expect(screen.queryByText("A nimbus for your Azure cloud.")).toBeNull();
    const settings = screen.getByRole("link", { name: "Settings" });
    expect(settings.getAttribute("aria-current")).toBe("page");
    expect(settings.getAttribute("title")).toBe("Settings");
    expect(window.localStorage.getItem(SIDEBAR_COLLAPSED_KEY)).toBe("true");
  });

  it("remembers the choice for the next page load", async () => {
    window.localStorage.setItem(SIDEBAR_COLLAPSED_KEY, "true");
    renderSidebar();
    await waitFor(() => expect(mocks.getAdminSimulation).toHaveBeenCalled());

    expect(screen.getByRole("button", { name: "Expand sidebar" })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Expand sidebar" }));

    expect(screen.getByText("A nimbus for your Azure cloud.")).toBeTruthy();
    expect(window.localStorage.getItem(SIDEBAR_COLLAPSED_KEY)).toBe("false");
  });
});
