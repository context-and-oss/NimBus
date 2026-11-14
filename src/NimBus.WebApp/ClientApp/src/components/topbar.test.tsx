import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter, Routes, Route } from "react-router-dom";
import Topbar from "./topbar";

vi.mock("hooks/use-theme", () => ({
  useTheme: () => ({ resolvedTheme: "light", setTheme: vi.fn() }),
}));
vi.mock("components/command-palette", () => ({
  useCommandPalette: () => ({ open: vi.fn() }),
}));

const crumbsAt = (path: string) => {
  render(
    <MemoryRouter initialEntries={[path]}>
      <Topbar />
    </MemoryRouter>,
  );
  return screen.getByRole("navigation");
};

afterEach(() => cleanup());

// Spec 038 §5.2: the Manage pages and their sub-views are named in the breadcrumbs.
describe("Topbar breadcrumbs", () => {
  it.each([
    ["/Operations", "Operations"],
    ["/Operations/resubmit", "Operations/Bulk resubmit failed"],
    ["/Operations/all", "Operations/Delete all events"],
    ["/Topology", "Topology"],
    ["/Topology/subscriptions", "Topology/Subscriptions"],
    ["/Topology/drift", "Topology/Catalog drift"],
    ["/Topology/storage", "Topology/Storage"],
    ["/Settings", "Settings"],
    ["/Settings/mcp", "Settings/MCP access"],
    ["/Settings/failure-intelligence", "Settings/Failure intelligence"],
    ["/Settings/heartbeat", "Settings/Heartbeat probing"],
    ["/Settings/simulation", "Settings/Simulation"],
    ["/Settings/audit", "Settings/Audit logging"],
  ])("names %s", (path, expected) => {
    expect(crumbsAt(path).textContent).toBe(expected);
  });

  it("links a sub-view back to its page", () => {
    const nav = crumbsAt("/Settings/mcp");

    const parent = nav.querySelector("a");
    expect(parent?.textContent).toBe("Settings");
    expect(parent?.getAttribute("href")).toBe("/Settings");
  });
});

it("links details opened from Failed back to Failed", () => {
  render(
    <MemoryRouter initialEntries={["/Message/Index/Crm/event-1/0?from=failed"]}>
      <Routes>
        <Route
          path="/Message/Index/:endpointId/:id/:backindex"
          element={<Topbar />}
        />
      </Routes>
    </MemoryRouter>,
  );
  expect(
    screen.getByRole("link", { name: "Failed" }).getAttribute("href"),
  ).toBe("/Failed");
});
