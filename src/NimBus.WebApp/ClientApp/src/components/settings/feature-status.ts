import type * as api from "api-client";
import {
  mcpEndpointState,
  type McpActivity,
  type McpState,
} from "components/admin/mcp-access-model";
import { activeKeyCount, type AgentKeysState } from "components/admin/agent-keys-model";

// Status, summary and quick action for each row of the Settings list
// (Spec 038 §7). Pure: the data comes from use-settings-status.

export type StatusTone = "ok" | "warn" | "danger" | "off";

/**
 * A row's inline action. Spec 038 §7.2: a row acts inline only when the action
 * narrows access and applies at once with no consent, key or restart. Anything
 * else opens the panel (`open`) so the feature's own review and confirmation run.
 */
export type QuickAction =
  | { kind: "turn-off-mcp"; label: string }
  | { kind: "send-heartbeat"; label: string }
  | { kind: "link"; label: string; to: string }
  | { kind: "open"; label: string; tab: string };

export interface FeatureStatus {
  tone: StatusTone;
  label: string;
  summary: string;
  /** Counted by the "On" filter. */
  on: boolean;
  quick?: QuickAction;
}

/** The fields of GET /api/admin/failure-intelligence a row needs. */
export interface FiSummary {
  active: { enabled: boolean; includeEventPayload: boolean };
  restartRequired: boolean;
  startupLoadFailed: boolean;
  credentialConfigured: boolean;
  credentialSource: "saved" | "deployment" | "none";
  savedApiKey: "none" | "configured" | "unreadable";
}

/** Each feature's loaded state; undefined while loading, "error" when it could not be read. */
export interface SettingsSnapshot {
  mcp?: { state: McpState; activity?: McpActivity } | "error";
  fi?: FiSummary | "error";
  simulation?: api.SimulationStatus | "error";
  audit?: api.AuditSettings | "error";
  heartbeat?: { settings: api.HeartbeatSettings; overview: api.HeartbeatOverviewRow[] } | "error";
  agentKeys?: AgentKeysState | "error";
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
        quick: { kind: "open", label: "Turn on…", tab: "overview" },
      };
  }
}

function failureIntelligence(fi: NonNullable<SettingsSnapshot["fi"]>): FeatureStatus {
  if (fi === "error") return UNAVAILABLE;
  if (fi.startupLoadFailed)
    return { tone: "danger", label: "Unavailable", summary: "Startup settings could not be loaded", on: false };
  if (fi.savedApiKey === "unreadable")
    return {
      tone: "danger",
      label: "Key unreadable",
      summary: "Enter the API key again or remove it",
      on: fi.active.enabled,
      quick: { kind: "open", label: "Fix key…", tab: "provider" },
    };
  if (fi.restartRequired)
    return { tone: "warn", label: "Restart required", summary: "Saved settings differ from this instance", on: fi.active.enabled };
  const keyConfigured = fi.credentialConfigured || fi.savedApiKey === "configured";
  if (fi.active.enabled) {
    const key = { saved: "saved", deployment: "from deployment", none: "missing" }[fi.credentialSource];
    return {
      tone: "ok",
      label: "On",
      summary: `Payload ${fi.active.includeEventPayload ? "included" : "excluded"} · API key ${key}`,
      on: true,
    };
  }
  return keyConfigured
    ? { tone: "off", label: "Off", summary: "Contributors can't request analysis", on: false, quick: { kind: "open", label: "Turn on…", tab: "provider" } }
    : { tone: "off", label: "Off", summary: "No API key configured", on: false, quick: { kind: "open", label: "Set up…", tab: "provider" } };
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
        quick: { kind: "open", label: "Enable…", tab: "mode" },
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
  // Issuing a key widens access, so it opens the panel rather than acting inline.
  return active > 0
    ? { tone: "ok", label: `${active} active`, summary: "Agents sign in to the agent API with these keys", on: true }
    : {
        tone: "off",
        label: "No active keys",
        summary: "Agents can't call the agent API where sign-in is required",
        on: false,
        quick: { kind: "open", label: "Issue key…", tab: "keys" },
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
    default:
      return undefined;
  }
}
