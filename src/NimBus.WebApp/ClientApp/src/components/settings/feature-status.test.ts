import { describe, expect, it } from "vitest";
import * as api from "api-client";
import type { McpState } from "components/admin/mcp-access-model";
import {
  featureStatus,
  type FiSummary,
  type SettingsSnapshot,
} from "./feature-status";

const mcpState = (
  overrides: Partial<McpState> = {},
  enabled = true,
): McpState =>
  ({
    saved: { enabled } as McpState["saved"],
    effective: { enabled } as McpState["saved"],
    policyLoaded: true,
    deployment: {
      mode: "entra",
      serverVersion: "4.6.0",
    } as McpState["deployment"],
    failureIntelligenceEnabled: false,
    csrfToken: "csrf",
    ...overrides,
  }) as McpState;

const fi = (overrides: Partial<FiSummary> = {}): FiSummary => ({
  active: { enabled: false, includeEventPayload: false },
  restartRequired: false,
  startupLoadFailed: false,
  credentialConfigured: false,
  credentialSource: "none",
  savedApiKey: "none",
  ...overrides,
});

const status = (id: string, snapshot: SettingsSnapshot) =>
  featureStatus(id, snapshot);

describe("featureStatus", () => {
  it("is undefined while a feature's state is loading", () => {
    expect(status("mcp", {})).toBeUndefined();
  });

  it("marks a feature whose state could not be read as unavailable, with no action", () => {
    const s = status("audit", { audit: "error" });
    expect(s).toMatchObject({
      tone: "off",
      label: "Unavailable",
    });
    expect(s?.quick).toBeUndefined();
  });

  describe("MCP access", () => {
    it("offers Turn off now while serving — narrowing, applied at once", () => {
      expect(
        status("mcp", {
          mcp: {
            state: mcpState(),
            activity: { actions: 2, refused: 0 } as never,
          },
        }),
      ).toMatchObject({
        tone: "ok",
        label: "Serving",
        summary: "2 actions · 0 refused · 24 h",
        on: true,
        quick: { kind: "turn-off-mcp", label: "Turn off now" },
      });
    });

    it("never turns MCP on inline: Turn on opens the panel's Overview", () => {
      expect(
        status("mcp", { mcp: { state: mcpState({}, false) } }),
      ).toMatchObject({
        tone: "off",
        label: "Off",
        on: false,
        quick: { kind: "open", label: "Turn on…", tab: "overview" },
      });
    });

    it("explains a deployment without MCP and offers nothing to click", () => {
      const s = status("mcp", {
        mcp: {
          state: mcpState({
            deployment: { mode: "disabled" } as McpState["deployment"],
          }),
        },
      });
      expect(s).toMatchObject({ tone: "off", label: "Not set up" });
      expect(s?.quick).toBeUndefined();
    });

    it("flags a policy that failed to load", () => {
      expect(
        status("mcp", { mcp: { state: mcpState({ policyLoaded: false }) } }),
      ).toMatchObject({ tone: "danger", label: "Unavailable" });
    });
  });

  describe("Failure intelligence", () => {
    it("asks for set-up when no key is configured", () => {
      expect(status("failure-intelligence", { fi: fi() })).toMatchObject({
        tone: "off",
        label: "Off",
        summary: "No API key configured",
        quick: { kind: "open", label: "Set up…", tab: "provider" },
      });
    });

    it("flags a pending restart, which no inline action can fix", () => {
      const s = status("failure-intelligence", {
        fi: fi({ restartRequired: true }),
      });
      expect(s).toMatchObject({ tone: "warn", label: "Restart required" });
      expect(s?.quick).toBeUndefined();
    });

    it("flags a saved key this instance cannot read", () => {
      expect(
        status("failure-intelligence", {
          fi: fi({ savedApiKey: "unreadable", credentialConfigured: true }),
        }),
      ).toMatchObject({
        tone: "danger",
        label: "Key unreadable",
        quick: { kind: "open", tab: "provider" },
      });
    });

    it("summarises an active configuration", () => {
      expect(
        status("failure-intelligence", {
          fi: fi({
            active: { enabled: true, includeEventPayload: true },
            credentialConfigured: true,
            credentialSource: "deployment",
          }),
        }),
      ).toMatchObject({
        tone: "ok",
        label: "On",
        on: true,
        summary: "Payload included · API key from deployment",
      });
    });
  });

  describe("Simulation", () => {
    const sim = (overrides: Partial<api.ISimulationStatus>) =>
      new api.SimulationStatus({
        allowed: true,
        enabled: false,
        environment: "dev",
        settings: new api.SimulationSettings({
          enabled: false,
          autoStopMinutes: 30,
          rateCeilingPerMinute: 120,
          ownedEndpoints: [],
        }),
        ...overrides,
      });

    it("never enables simulation inline: Enable opens Simulate mode", () => {
      expect(status("simulation", { simulation: sim({}) })).toMatchObject({
        tone: "off",
        label: "Off",
        summary: "Allowed in dev · auto-stop 30 min",
        quick: { kind: "open", label: "Enable…", tab: "mode" },
      });
    });

    it("links to the Simulate page when enabled", () => {
      expect(
        status("simulation", { simulation: sim({ enabled: true }) }),
      ).toMatchObject({
        tone: "ok",
        label: "On",
        on: true,
        quick: { kind: "link", label: "Open Simulate", to: "/Simulate" },
      });
    });

    it("shows a blocked environment with no action", () => {
      const s = status("simulation", {
        simulation: sim({ allowed: false, environment: "prod" }),
      });
      expect(s).toMatchObject({
        tone: "danger",
        label: "Blocked",
        summary: "Not available in prod",
      });
      expect(s?.quick).toBeUndefined();
    });
  });

  describe("Agent keys", () => {
    const keys = (...statuses: ("active" | "revoked" | "expired")[]) => ({
      csrfToken: "csrf",
      keys: statuses.map((status, i) => ({ keyId: `k${i}`, status }) as never),
    });

    it("counts active keys", () => {
      expect(status("agent-keys", { agentKeys: keys("active", "active", "revoked") })).toMatchObject({
        tone: "ok",
        label: "2 active",
        on: true,
      });
    });

    it("offers to issue a key when none is active", () => {
      const s = status("agent-keys", { agentKeys: keys("expired") });
      expect(s).toMatchObject({ tone: "off", label: "No active keys", on: false });
      expect(s?.quick).toEqual({ kind: "open", label: "Issue key…", tab: "keys" });
    });

    it("is unavailable when the keys could not be read", () => {
      expect(status("agent-keys", { agentKeys: "error" })).toMatchObject({ label: "Unavailable" });
    });
  });

  describe("Audit logging", () => {
    it("counts recorded action types", () => {
      const s = status("audit", {
        audit: new api.AuditSettings({
          configurableAuditTypes: ["A", "B", "C", "D"],
          disabledAuditTypes: ["B"],
        }),
      });
      expect(s).toMatchObject({
        tone: "ok",
        label: "3 of 4 recorded",
        on: true,
      });
      expect(s?.quick).toBeUndefined();
    });

    it("warns when nothing optional is recorded", () => {
      expect(
        status("audit", {
          audit: new api.AuditSettings({
            configurableAuditTypes: ["A"],
            disabledAuditTypes: ["A"],
          }),
        }),
      ).toMatchObject({ tone: "warn", label: "0 of 1 recorded", on: false });
    });
  });

  describe("Heartbeat probing", () => {
    const heartbeat = (enabled: boolean) => ({
      settings: new api.HeartbeatSettings({
        enabled,
        intervalSeconds: 300,
        timeoutSeconds: 60,
      }),
      overview: [
        new api.HeartbeatOverviewRow({ endpointId: "a" }),
        new api.HeartbeatOverviewRow({
          endpointId: "b",
          isHeartbeatEnabled: true,
        }),
        new api.HeartbeatOverviewRow({
          endpointId: "c",
          isHeartbeatEnabled: false,
        }),
      ],
    });

    it("summarises probed endpoints and the interval, and offers Send now", () => {
      expect(status("heartbeat", { heartbeat: heartbeat(true) })).toMatchObject(
        {
          tone: "ok",
          label: "On",
          summary: "2 of 3 endpoints · every 5 min",
          quick: { kind: "send-heartbeat", label: "Send now" },
        },
      );
    });

    it("shows a paused schedule as off", () => {
      expect(
        status("heartbeat", { heartbeat: heartbeat(false) }),
      ).toMatchObject({
        tone: "off",
        label: "Off",
        on: false,
      });
    });
  });
});
