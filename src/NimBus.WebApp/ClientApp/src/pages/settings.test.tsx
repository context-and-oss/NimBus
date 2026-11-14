import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import type { SettingsSnapshot } from "components/settings/feature-status";
import Settings from "./settings";

const mocks = vi.hoisted(() => ({
  snapshot: {} as SettingsSnapshot,
  reload: vi.fn(),
  turnOffMcp: vi.fn(),
  sendHeartbeat: vi.fn(),
}));

vi.mock("hooks/use-settings-status", () => ({
  useSettingsStatus: () => ({
    snapshot: mocks.snapshot,
    reload: mocks.reload,
    turnOffMcp: mocks.turnOffMcp,
    sendHeartbeat: mocks.sendHeartbeat,
  }),
}));
vi.mock("functions/notifications.functions", () => ({
  notifySuccess: vi.fn(),
  notifyError: vi.fn(),
}));

// The MCP stand-in reports unsaved edits through the real frame, like the real component.
vi.mock("components/admin/mcp-access-settings", async () => {
  const { useState } = await import("react");
  const { PanelSection, usePanelDirty } = await import("components/settings/panel-frame");
  return {
    default: function McpStandIn() {
      const [edits, setEdits] = useState(0);
      usePanelDirty(edits);
      return (
        <div>
          MCP access settings
          <PanelSection tab="overview"><p>Overview section</p></PanelSection>
          <PanelSection tab="permissions"><p>Permissions section</p></PanelSection>
          <button type="button" onClick={() => setEdits((e) => e + 1)}>
            Make an edit
          </button>
        </div>
      );
    },
  };
});
vi.mock("components/admin/failure-intelligence-settings", () => ({
  default: () => <div>Failure intelligence settings</div>,
}));
vi.mock("components/admin/heartbeat-card", () => ({
  default: () => <div>Heartbeat probing settings</div>,
}));
vi.mock("components/admin/simulation-settings", () => ({
  default: () => <div>Simulation settings</div>,
}));
vi.mock("components/admin/audit-settings", () => ({
  default: () => <div>Audit settings</div>,
}));

function Location() {
  const { pathname, search } = useLocation();
  return <output aria-label="location">{pathname + search}</output>;
}

function renderAt(...entries: string[]) {
  return render(
    <MemoryRouter initialEntries={entries} initialIndex={entries.length - 1}>
      <Routes>
        <Route path="/Settings/:feature?" element={<Settings />} />
      </Routes>
      <Location />
    </MemoryRouter>,
  );
}

const location = () => screen.getByLabelText("location").textContent;
const row = (name: string) => screen.getByRole("link", { name: new RegExp(name) }).closest("li")!;

const SNAPSHOT: SettingsSnapshot = {
  mcp: {
    state: {
      saved: { enabled: true },
      effective: { enabled: true },
      policyLoaded: true,
      deployment: { mode: "entra" },
      failureIntelligenceEnabled: false,
      csrfToken: "csrf",
    } as never,
    activity: { actions: 2, refused: 0 } as never,
  },
  fi: {
    active: { enabled: false, includeEventPayload: false },
    restartRequired: false,
    startupLoadFailed: false,
    credentialConfigured: false,
    credentialSource: "none",
    savedApiKey: "none",
  },
  heartbeat: {
    settings: { enabled: true, intervalSeconds: 300 } as never,
    overview: [{ endpointId: "a" }, { endpointId: "b", isHeartbeatEnabled: false }] as never,
  },
};

beforeEach(() => {
  mocks.snapshot = SNAPSHOT;
  mocks.reload.mockReset();
  mocks.turnOffMcp.mockReset().mockResolvedValue(undefined);
  mocks.sendHeartbeat.mockReset().mockResolvedValue(1);
});

afterEach(() => cleanup());

describe("Settings page", () => {
  it("lists every opt-in feature, grouped, with no panel open", () => {
    renderAt("/Settings");

    expect(screen.getByRole("heading", { name: "Settings" })).toBeTruthy();
    for (const group of ["AI & agents", "Monitoring", "Testing", "Compliance"]) {
      expect(screen.getByRole("heading", { name: group })).toBeTruthy();
    }
    const links = {
      "MCP access": "/Settings/mcp",
      // Its row link carries the Set up call to action.
      "Failure intelligence": "/Settings/failure-intelligence?tab=provider",
      "Heartbeat probing": "/Settings/heartbeat",
      Simulation: "/Settings/simulation",
      "Audit logging": "/Settings/audit",
    };
    for (const [name, href] of Object.entries(links)) {
      expect(
        screen.getByRole("link", { name: new RegExp(name) }).getAttribute("href"),
      ).toBe(href);
    }
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("shows each feature's live status and summary", () => {
    renderAt("/Settings");

    expect(row("MCP access").textContent).toContain("Serving");
    expect(row("MCP access").textContent).toContain("2 actions · 0 refused · 24 h");
    expect(row("Failure intelligence").textContent).toContain("No API key configured");
    expect(row("Heartbeat probing").textContent).toContain("1 of 2 endpoints · every 5 min");
  });

  it("filters by setting contents, not just names", () => {
    renderAt("/Settings");

    fireEvent.change(screen.getByRole("searchbox", { name: "Filter settings" }), {
      target: { value: "payload" },
    });

    expect(screen.getByRole("link", { name: /MCP access/ })).toBeTruthy();
    expect(screen.getByRole("link", { name: /Failure intelligence/ })).toBeTruthy();
    expect(screen.queryByRole("link", { name: /Audit logging/ })).toBeNull();
  });

  it("filters to features that are on", () => {
    renderAt("/Settings");

    fireEvent.click(screen.getByRole("button", { name: "On" }));

    expect(screen.getByRole("link", { name: /MCP access/ })).toBeTruthy();
    expect(screen.queryByRole("link", { name: /Failure intelligence/ })).toBeNull();
  });

  it("turns MCP access off from its row after confirming", async () => {
    renderAt("/Settings");

    fireEvent.click(within(row("MCP access")).getByRole("button", { name: "Turn off now" }));
    const confirm = screen.getByRole("dialog", { name: "Turn off MCP access now?" });
    fireEvent.click(within(confirm).getByRole("button", { name: "Turn off MCP access" }));

    await waitFor(() => expect(mocks.turnOffMcp).toHaveBeenCalledTimes(1));
  });

  it("sends a heartbeat from its row", async () => {
    renderAt("/Settings");

    fireEvent.click(within(row("Heartbeat probing")).getByRole("button", { name: "Send now" }));

    await waitFor(() => expect(mocks.sendHeartbeat).toHaveBeenCalledTimes(1));
  });

  it("labels the row link with an action that needs review, instead of a second control", () => {
    renderAt("/Settings");

    const links = within(row("Failure intelligence")).getAllByRole("link");
    expect(links).toHaveLength(1);
    expect(links[0].textContent).toContain("Set up ›");
    expect(links[0].textContent).not.toContain("Configure");
    expect(within(row("Audit logging")).getByRole("link").textContent).toContain("Configure ›");

    fireEvent.click(links[0]);

    expect(location()).toBe("/Settings/failure-intelligence?tab=provider");
    expect(
      screen.getByRole("tab", { name: "Activation & provider" }).getAttribute("aria-selected"),
    ).toBe("true");
  });

  it("opens a feature's settings in a side panel from its row", () => {
    renderAt("/Settings");

    fireEvent.click(screen.getByRole("link", { name: /MCP access/ }));

    expect(location()).toBe("/Settings/mcp");
    const dialog = screen.getByRole("dialog", { name: "MCP access" });
    expect(dialog.textContent).toContain("MCP access settings");
  });

  it("switches tabs within the panel and keeps the tab in the URL", () => {
    renderAt("/Settings/mcp");
    expect(screen.getByText("Overview section")).toBeTruthy();

    fireEvent.click(screen.getByRole("tab", { name: "Permissions" }));

    expect(location()).toBe("/Settings/mcp?tab=permissions");
    expect(screen.getByText("Permissions section")).toBeTruthy();
    expect(screen.queryByText("Overview section")).toBeNull();
  });

  it("falls back to the first tab for an unknown one", () => {
    renderAt("/Settings/mcp?tab=nope");

    expect(screen.getByRole("tab", { name: "Overview" }).getAttribute("aria-selected")).toBe("true");
  });

  it("deep-links to a feature's panel", () => {
    renderAt("/Settings/failure-intelligence");

    expect(
      screen.getByRole("dialog", { name: "Failure intelligence" }).textContent,
    ).toContain("Failure intelligence settings");
  });

  it("closes the panel back to the list", () => {
    renderAt("/Settings/audit");

    fireEvent.click(screen.getByRole("button", { name: "Close Audit logging" }));

    expect(location()).toBe("/Settings");
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("closes with Escape", () => {
    renderAt("/Settings/heartbeat");

    fireEvent.keyDown(document, { key: "Escape" });

    expect(location()).toBe("/Settings");
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("returns through history when the panel was opened from the list, so Back does not reopen it", () => {
    renderAt("/Settings");
    fireEvent.click(screen.getByRole("link", { name: /Simulation/ }));

    fireEvent.click(screen.getByRole("button", { name: "Close Simulation" }));

    expect(location()).toBe("/Settings");
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  it("re-reads every feature's status when the panel closes", () => {
    renderAt("/Settings/audit");

    fireEvent.click(screen.getByRole("button", { name: "Close Audit logging" }));

    expect(mocks.reload).toHaveBeenCalled();
  });

  it("sends an unknown feature back to the list", () => {
    renderAt("/Settings/nope");

    expect(location()).toBe("/Settings");
    expect(screen.queryByRole("dialog")).toBeNull();
  });

  describe("with unsaved changes", () => {
    function openWithEdits() {
      renderAt("/Settings/mcp");
      fireEvent.click(screen.getByRole("button", { name: "Make an edit" }));
      fireEvent.click(screen.getByRole("button", { name: "Make an edit" }));
    }

    it("asks before closing, and Keep editing keeps the panel open", () => {
      openWithEdits();

      fireEvent.click(screen.getByRole("button", { name: "Close MCP access" }));
      const prompt = screen.getByRole("dialog", { name: "Discard 2 unsaved changes?" });
      fireEvent.click(within(prompt).getByRole("button", { name: "Keep editing" }));

      expect(location()).toBe("/Settings/mcp");
      expect(screen.getByRole("dialog", { name: "MCP access" })).toBeTruthy();
    });

    it("closes after Discard", () => {
      openWithEdits();

      fireEvent.keyDown(document, { key: "Escape" });
      fireEvent.click(
        within(screen.getByRole("dialog", { name: "Discard 2 unsaved changes?" })).getByRole(
          "button",
          { name: "Discard" },
        ),
      );

      expect(location()).toBe("/Settings");
      expect(screen.queryByRole("dialog")).toBeNull();
    });

    it("lets Escape dismiss only the prompt", () => {
      openWithEdits();
      fireEvent.click(screen.getByRole("button", { name: "Close MCP access" }));

      fireEvent.keyDown(document, { key: "Escape" });

      expect(screen.queryByRole("dialog", { name: "Discard 2 unsaved changes?" })).toBeNull();
      expect(screen.getByRole("dialog", { name: "MCP access" })).toBeTruthy();
    });

    it("asks the browser to confirm leaving the page", () => {
      openWithEdits();

      const event = new Event("beforeunload", { cancelable: true });
      window.dispatchEvent(event);

      expect(event.defaultPrevented).toBe(true);
    });
  });
});
