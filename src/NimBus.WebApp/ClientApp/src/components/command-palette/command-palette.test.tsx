import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter, useLocation } from "react-router-dom";
import { CommandPalette } from "./command-palette";

const mocks = vi.hoisted(() => ({
  access: { current: null as Record<string, unknown> | null },
  storageProvider: { current: undefined as string | undefined },
}));

vi.mock("hooks/use-access", () => ({ useAccess: () => ({ access: mocks.access.current }) }));
vi.mock("hooks/app-status", () => ({ useStorageProvider: () => mocks.storageProvider.current }));
vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getEndpointsAll = vi.fn().mockResolvedValue(["DeadLetterRelay"]);
    getEventTypes = vi.fn().mockResolvedValue([]);
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

function Location() {
  const { pathname, search } = useLocation();
  return <output aria-label="location">{pathname + search}</output>;
}

function renderPalette() {
  const onClose = vi.fn();
  render(
    <MemoryRouter>
      <CommandPalette isOpen onClose={onClose} />
      <Location />
    </MemoryRouter>,
  );
  return onClose;
}

const type = (text: string) => fireEvent.change(screen.getByRole("textbox", { name: "Search" }), { target: { value: text } });
const options = () => within(screen.getByRole("listbox")).queryAllByRole("option");

beforeEach(() => {
  mocks.access.current = null;
  mocks.storageProvider.current = undefined;
  // jsdom has no layout, so no scrollIntoView; the palette scrolls the highlighted row.
  Element.prototype.scrollIntoView = vi.fn();
  vi.spyOn(window, "requestAnimationFrame").mockImplementation((cb) => {
    cb(0);
    return 0;
  });
});

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

// Spec 038 §10: site Owners can jump to operations and settings from Ctrl K.
describe("CommandPalette for a site Owner", () => {
  beforeEach(() => {
    mocks.access.current = { canManageAccessControl: true, endpointRoles: [] };
  });

  it("lists matching operations in their own section, before endpoints", async () => {
    renderPalette();
    type("dead");

    expect(await screen.findByText("DeadLetterRelay")).toBeTruthy();
    const titles = options().map((o) => o.textContent ?? "");
    expect(screen.getByText("Operations")).toBeTruthy();
    expect(titles[0]).toContain("Delete dead-lettered");
    expect(titles.findIndex((t) => t.includes("DeadLetterRelay"))).toBeGreaterThan(0);
  });

  it("opens the operation on Enter", async () => {
    const onClose = renderPalette();
    type("dead-letter");
    await screen.findByText("Delete dead-lettered");

    fireEvent.keyDown(screen.getByRole("textbox", { name: "Search" }), { key: "Enter" });

    expect(onClose).toHaveBeenCalled();
    expect(screen.getByLabelText("location").textContent).toBe("/Operations/dlq");
  });

  it("finds a setting's tab by what it holds", async () => {
    renderPalette();
    type("rate");

    expect(await screen.findByText("MCP access › Limits")).toBeTruthy();
    expect(screen.getByText("Settings")).toBeTruthy();
  });

  it("offers the Storage view only on Cosmos DB", async () => {
    mocks.storageProvider.current = "SQL Server";
    renderPalette();
    type("topology");

    expect(await screen.findByText("Topology › Catalog drift")).toBeTruthy();
    expect(screen.queryByText("Topology › Storage")).toBeNull();
  });

  it("offers the Storage view on Cosmos DB", async () => {
    mocks.storageProvider.current = "Cosmos DB";
    renderPalette();
    type("topology");

    expect(await screen.findByText("Topology › Storage")).toBeTruthy();
  });

  it("moves through the results in the order it shows them", async () => {
    renderPalette();
    type("endpoint");
    await screen.findAllByText("Operations");
    const input = screen.getByRole("textbox", { name: "Search" });
    const shown = options().map((o) => o.id);
    expect(shown.length).toBeGreaterThan(2);

    const visited = [input.getAttribute("aria-activedescendant")];
    for (let i = 1; i < shown.length; i++) {
      fireEvent.keyDown(input, { key: "ArrowDown" });
      visited.push(input.getAttribute("aria-activedescendant"));
    }

    expect(visited).toEqual(shown);
  });
});

describe("CommandPalette for everyone else", () => {
  it("offers no operations or settings", async () => {
    mocks.access.current = { canManageAccessControl: false, endpointRoles: [] };
    renderPalette();
    type("dead");

    expect(await screen.findByText("DeadLetterRelay")).toBeTruthy();
    expect(screen.queryByText("Delete dead-lettered")).toBeNull();
    expect(screen.queryByText("Operations")).toBeNull();
  });
});
