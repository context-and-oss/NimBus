import type * as api from "api-client";
import {
  mcpEndpointState,
  type McpActivity,
  type McpState,
} from "components/admin/mcp-access-model";
import { activeKeyCount, activeKeysByUse, expiresSoon, type AgentKeysState } from "components/admin/agent-keys-model";
import { activeTokenCount, type DashboardTokensState } from "components/admin/dashboard-tokens-model";

// Status, summary, call to action and quick action for each row of the Settings list
// (Spec 038 §7). Pure: the data comes from use-settings-status.

export type StatusTone = "ok" | "warn" | "danger" | "off";

/**
 * A row's inline action. Spec 038 §7.2: a row acts inline only when the action
 * narrows access and applies at once with no consent, key or restart. Anything
 * else is the row's call to action ({@link FeatureStatus.cta}), which opens the
 * panel so the feature's own review and confirmation run.
 */
export type QuickAction =
  | { kind: "turn-off-mcp"; label: string }
  | { kind: "send-heartbeat"; label: string }
  | { kind: "link"; label: string; to: string };

/** Replaces the row link's "Configure" label and opens the panel on `tab`. */
export interface CallToAction {
  label: string;
  tab: string;
}

export interface FeatureStatus {
  tone: StatusTone;
  label: string;
  summary: string;
  /** Counted by the "On" filter. */
  on: boolean;
  cta?: CallToAction;
  quick?: QuickAction;
}

/** The fields of GET /api/admin/failure-intelligence a row needs. */
export interface FiSummary {
  active: { enabled: boolean; includeEventPayload: boolean };
  restartRequired: boolean;
  startupLoadFailed: boolean;
}

/** Each feature's loaded state; undefined while loading, "error" when it could not be read. */
export interface SettingsSnapshot {
  mcp?: { state: McpState; activity?: McpActivity } | "error";
  fi?: FiSummary | "error";
  simulation?: api.SimulationStatus | "error";
  audit?: api.AuditSettings | "error";
  heartbeat?: { settings: api.HeartbeatSettings; overview: api.HeartbeatOverviewRow[] } | "error";
  agentKeys?: AgentKeysState | "error";
  dashboardTokens?: DashboardTokensState | "error";
}

const UNAVAILABLE: FeatureStatus = {
  tone: "off",
  label: "Unavailable",
  summary: "The current state could not be loaded",
  on: false,
};

const every = (seconds: number) =>
  seconds % 60 === 0 ? `${seconds / 60} min` : `${seconds} s`;

function mcp(data: NonNullable<SettingsSnapshot["mcp"]>): FeatureStatus {
  if (data === "error") return UNAVAILABLE;
  const { state, activity } = data;
  switch (mcpEndpointState(state)) {
    case "notSetUp":
      return { tone: "off", label: "Not set up", summary: "Needs NimBus__Mcp__Enabled and an app registration", on: false };
    case "unavailable":
      return { tone: "danger", label: "Unavailable", summary: "The access policy could not be loaded", on: false };
    case "serving":
      return {
        tone: "ok",
        label: "Serving",
        summary: activity
          ? `${activity.actions} actions · ${activity.refused} refused · 24 h`
          : "Agents can connect at /mcp",
        on: true,
        quick: { kind: "turn-off-mcp", label: "Turn off now" },
      };
    case "off":
      return {
        tone: "off",
        label: "Off",
        summary: "Agents get 503 [Disabled]",
        on: false,
        cta: { label: "Turn on", tab: "overview" },
      };
  }
}

function failureIntelligence(fi: NonNullable<SettingsSnapshot["fi"]>): FeatureStatus {
  if (fi === "error") return UNAVAILABLE;
  if (fi.startupLoadFailed)
    return { tone: "danger", label: "Unavailable", summary: "Startup settings could not be loaded", on: false };
  if (fi.restartRequired)
    return { tone: "warn", label: "Restart required", summary: "Saved settings differ from this instance", on: fi.active.enabled };
  if (fi.active.enabled)
    return {
      tone: "ok",
      label: "On",
      summary: `Agents record over MCP · payload ${fi.active.includeEventPayload ? "included" : "excluded"}`,
      on: true,
    };
  return { tone: "off", label: "Off", summary: "Agents can't record classifications", on: false, cta: { label: "Turn on", tab: "provider" } };
}

function simulation(status: NonNullable<SettingsSnapshot["simulation"]>): FeatureStatus {
  if (status === "error") return UNAVAILABLE;
  const environment = status.environment?.trim() || "this environment";
  if (!status.allowed)
    return { tone: "danger", label: "Blocked", summary: `Not available in ${environment}`, on: false };
  const autoStop = `auto-stop ${status.settings?.autoStopMinutes ?? "?"} min`;
  return status.enabled
    ? {
        tone: "ok",
        label: "On",
        summary: `${status.state ?? "stopped"} · ${autoStop}`,
        on: true,
        quick: { kind: "link", label: "Open Simulate", to: "/Simulate" },
      }
    : {
        tone: "off",
        label: "Off",
        summary: `Allowed in ${environment} · ${autoStop}`,
        on: false,
        cta: { label: "Enable", tab: "mode" },
      };
}

function audit(settings: NonNullable<SettingsSnapshot["audit"]>): FeatureStatus {
  if (settings === "error") return UNAVAILABLE;
  const configurable = settings.configurableAuditTypes ?? [];
  const disabled = new Set(settings.disabledAuditTypes ?? []);
  const recorded = configurable.filter((t) => !disabled.has(t)).length;
  return {
    tone: recorded > 0 ? "ok" : "warn",
    label: `${recorded} of ${configurable.length} recorded`,
    summary: "Access-denied attempts and settings changes are always recorded",
    on: recorded > 0,
  };
}

function heartbeat(data: NonNullable<SettingsSnapshot["heartbeat"]>): FeatureStatus {
  if (data === "error") return UNAVAILABLE;
  // An endpoint with no explicit flag is probed (heartbeat-card treats null as included).
  const included = data.overview.filter((r) => r.isHeartbeatEnabled !== false).length;
  const enabled = data.settings.enabled ?? false;
  return {
    tone: enabled ? "ok" : "off",
    label: enabled ? "On" : "Off",
    summary: `${included} of ${data.overview.length} endpoints · every ${every(data.settings.intervalSeconds ?? 300)}`,
    on: enabled,
    quick: { kind: "send-heartbeat", label: "Send now" },
  };
}

function agentKeys(state: NonNullable<SettingsSnapshot["agentKeys"]>): FeatureStatus {
  if (state === "error") return UNAVAILABLE;
  const active = activeKeyCount(state);
  const byUse = activeKeysByUse(state);
  // Spec 052: an MCP key that expires within 14 days needs rotating before its agent stops.
  if (state.keys.some((k) => expiresSoon(k)))
    return { tone: "warn", label: "Needs attention", summary: "An MCP key expires within 14 days: rotate it", on: true, cta: { label: "Review keys", tab: "keys" } };
  const summary = byUse.mcp > 0
    ? `${byUse.agentApi} for the agent API · ${byUse.mcp} for MCP`
    : "Agents sign in to the agent API with these keys";
  // Issuing a key widens access, so it is the call to action rather than an inline action.
  return active > 0
    ? { tone: "ok", label: `${active} active`, summary, on: true }
    : {
        tone: "off",
        label: "No active keys",
        summary: "Agents can't call the agent API where sign-in is required",
        on: false,
        cta: { label: "Issue key", tab: "keys" },
      };
}

function dashboardTokens(state: NonNullable<SettingsSnapshot["dashboardTokens"]>): FeatureStatus {
  if (state === "error") return UNAVAILABLE;
  const active = activeTokenCount(state);
  // Issuing a token widens access, so it is the call to action rather than an inline action.
  return active > 0
    ? { tone: "ok", label: `${active} active`, summary: "Wall displays show the Monitor with these links", on: true }
    : {
        tone: "off",
        label: "No active tokens",
        summary: "A wall display has to sign in to show the Monitor",
        on: false,
        cta: { label: "Issue token", tab: "tokens" },
      };
}

/** A row's status, or undefined while that feature's state is still loading. */
export function featureStatus(featureId: string, snapshot: SettingsSnapshot): FeatureStatus | undefined {
  switch (featureId) {
    case "mcp":
      return snapshot.mcp && mcp(snapshot.mcp);
    case "failure-intelligence":
      return snapshot.fi && failureIntelligence(snapshot.fi);
    case "simulation":
      return snapshot.simulation && simulation(snapshot.simulation);
    case "audit":
      return snapshot.audit && audit(snapshot.audit);
    case "heartbeat":
      return snapshot.heartbeat && heartbeat(snapshot.heartbeat);
    case "agent-keys":
      return snapshot.agentKeys && agentKeys(snapshot.agentKeys);
    case "dashboard-tokens":
      return snapshot.dashboardTokens && dashboardTokens(snapshot.dashboardTokens);
    default:
      return undefined;
  }
}
