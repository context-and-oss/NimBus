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
const chips = () => screen.getByRole("group", { name: "Settings" });
const chip = (name: string) => within(chips()).getByRole("button", { name });

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
  it("offers every opt-in feature as a chip and opens on the first", () => {
    renderAt("/Settings");

    expect(within(chips()).getAllByRole("button").map((b) => b.textContent)).toEqual([
      "MCP access",
      "Failure intelligence",
      "Agent keys",
      "Heartbeat probing",
      "Dashboard tokens",
      "Simulation",
      "Audit logging",
    ]);
    expect(chip("MCP access").getAttribute("aria-pressed")).toBe("true");
    expect(screen.getByRole("heading", { level: 1, name: "MCP access" })).toBeTruthy();
    expect(screen.getByText("MCP access settings")).toBeTruthy();
    // No redirect: /Settings is the first feature.
    expect(location()).toBe("/Settings");
  });

  it("shows each feature's live status on its chip and the open one's in the header", () => {
    renderAt("/Settings");

    expect(chip("MCP access").getAttribute("title")).toBe("MCP access: Serving. 2 actions · 0 refused · 24 h");
    expect(chip("Failure intelligence").getAttribute("title")).toContain("Off");
    expect(chip("Heartbeat probing").getAttribute("title")).toContain("1 of 2 endpoints · every 5 min");
    // Not loaded yet: the chip still opens the feature.
    expect(chip("Simulation").getAttribute("title")).toBe("Simulation");

    expect(screen.getByText("Serving")).toBeTruthy();
    expect(screen.getByText("2 actions · 0 refused · 24 h")).toBeTruthy();
  });

  it("puts MCP's 24-hour figures in the header as shortcuts to Activity", () => {
    renderAt("/Settings/mcp");

    const actions = screen.getByRole("button", { name: /Actions · 24 h/ });
    expect(actions.textContent).toContain("2");
    fireEvent.click(actions);

    expect(location()).toBe("/Settings/mcp?tab=activity");
    expect(screen.getByRole("tab", { name: "Activity" }).getAttribute("aria-selected")).toBe("true");
  });

  it("leaves turning MCP access off to its own settings, which confirm it", () => {
    renderAt("/Settings");

    // The MCP settings show "Turn off now" on every tab; the header doesn't repeat it.
    expect(screen.queryByRole("button", { name: "Turn off now" })).toBeNull();
  });

  it("sends a heartbeat from the header", async () => {
    renderAt("/Settings/heartbeat");

    fireEvent.click(screen.getByRole("button", { name: "Send now" }));

    await waitFor(() => expect(mocks.sendHeartbeat).toHaveBeenCalledTimes(1));
  });

  it("offers no quick action for a change that needs review", () => {
    mocks.snapshot = {
      ...SNAPSHOT,
      mcp: { state: { ...(SNAPSHOT.mcp as { state: object }).state, saved: { enabled: false }, effective: { enabled: false } } as never },
    };
    renderAt("/Settings");

    expect(screen.getByText("Off")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /Turn o/ })).toBeNull();
  });

  it("switches to another feature from its chip in one click and re-reads every status", () => {
    renderAt("/Settings/mcp");

    fireEvent.click(chip("Heartbeat probing"));

    expect(location()).toBe("/Settings/heartbeat");
    expect(screen.getByRole("heading", { level: 1, name: "Heartbeat probing" })).toBeTruthy();
    expect(screen.getByText("Heartbeat probing settings")).toBeTruthy();
    expect(screen.queryByText("MCP access settings")).toBeNull();
    expect(mocks.reload).toHaveBeenCalledTimes(1);
  });

  it("switches tabs and keeps the tab in the URL", () => {
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

  it("deep-links to a feature and tab", () => {
    renderAt("/Settings/mcp?tab=permissions");

    expect(chip("MCP access").getAttribute("aria-pressed")).toBe("true");
    expect(screen.getByText("Permissions section")).toBeTruthy();
  });

  it("has no tab strip for a single-tab feature, and says when its changes apply", () => {
    renderAt("/Settings/audit");

    expect(screen.queryByRole("tablist")).toBeNull();
    expect(screen.getByText(/No unsaved changes · Applies immediately/)).toBeTruthy();
  });

  it("sends an unknown feature back to the first", () => {
    renderAt("/Settings/nope");

    expect(location()).toBe("/Settings");
    expect(screen.getByText("MCP access settings")).toBeTruthy();
  });

  describe("with unsaved changes", () => {
    const edit = () => {
      renderAt("/Settings/mcp");
      fireEvent.click(screen.getByRole("button", { name: "Make an edit" }));
    };

    it("counts them in the save bar", () => {
      edit();

      expect(screen.getByText("1 unsaved change")).toBeTruthy();
    });

    it("asks before switching to another feature, and Keep editing stays", () => {
      edit();

      fireEvent.click(chip("Heartbeat probing"));
      expect(screen.getByRole("dialog", { name: "Discard 1 unsaved change?" })).toBeTruthy();
      fireEvent.click(screen.getByRole("button", { name: "Keep editing" }));

      expect(location()).toBe("/Settings/mcp");
      expect(screen.getByText("MCP access settings")).toBeTruthy();
    });

    it("opens the other feature after Discard", () => {
      edit();

      fireEvent.click(chip("Heartbeat probing"));
      fireEvent.click(screen.getByRole("button", { name: "Discard" }));

      expect(location()).toBe("/Settings/heartbeat");
      expect(screen.getByText("Heartbeat probing settings")).toBeTruthy();
      expect(screen.queryByRole("dialog")).toBeNull();
    });

    it("switches tabs within the feature without asking", () => {
      edit();

      fireEvent.click(screen.getByRole("tab", { name: "Permissions" }));

      expect(screen.queryByRole("dialog")).toBeNull();
      expect(screen.getByText("1 unsaved change")).toBeTruthy();
    });

    it("asks the browser to confirm leaving the page", () => {
      edit();

      const event = new Event("beforeunload", { cancelable: true });
      window.dispatchEvent(event);

      expect(event.defaultPrevented).toBe(true);
    });
  });
});
