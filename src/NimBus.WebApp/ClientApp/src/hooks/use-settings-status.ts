import { useCallback, useEffect, useMemo, useState } from "react";
import * as api from "api-client";
import type { McpActivity, McpState } from "components/admin/mcp-access-model";
import type { FiSummary, SettingsSnapshot } from "components/settings/feature-status";
import { AGENT_KEYS_URL, type AgentKeysState } from "components/admin/agent-keys-model";

// Loads what the Settings list shows for each feature (Spec 038 §7.1), from the
// same endpoints the feature's own panel uses. Each feature loads on its own, so
// one slow or failing API leaves the other rows intact.

const MCP_SETTINGS = "/api/admin/mcp/settings";
const MCP_ACTIVITY = "/api/admin/mcp/activity?hours=24";
const MCP_TURN_OFF = "/api/admin/mcp/turn-off";
const FAILURE_INTELLIGENCE = "/api/admin/failure-intelligence";

async function json<T>(url: string, signal: AbortSignal): Promise<T> {
  const response = await fetch(url, { credentials: "same-origin", signal });
  if (!response.ok) throw new Error(`${url}: HTTP ${response.status}`);
  return (await response.json()) as T;
}

/** The Settings list's view of every feature; `reload` re-reads them all. */
export function useSettingsStatus() {
  const client = useMemo(() => new api.Client(api.CookieAuth()), []);
  const [snapshot, setSnapshot] = useState<SettingsSnapshot>({});
  const [version, setVersion] = useState(0);

  useEffect(() => {
    const controller = new AbortController();
    const { signal } = controller;
    const set = <K extends keyof SettingsSnapshot>(key: K, value: SettingsSnapshot[K]) => {
      if (!signal.aborted) setSnapshot((s) => ({ ...s, [key]: value }));
    };
    const load = <K extends keyof SettingsSnapshot>(key: K, read: () => Promise<SettingsSnapshot[K]>) =>
      read().then(
        (value) => set(key, value),
        () => set(key, "error"),
      );

    void load("mcp", async () => {
      const state = await json<McpState>(MCP_SETTINGS, signal);
      // Activity is best effort, as on the MCP panel.
      const activity = await json<McpActivity>(MCP_ACTIVITY, signal).catch(() => undefined);
      return { state, activity };
    });
    void load("fi", () => json<FiSummary>(FAILURE_INTELLIGENCE, signal));
    void load("agentKeys", () => json<AgentKeysState>(AGENT_KEYS_URL, signal));
    void load("simulation", () => client.getAdminSimulation());
    void load("audit", () => client.getAdminAuditSettings());
    void load("heartbeat", async () => {
      const [settings, overview] = await Promise.all([
        client.getAdminHeartbeatSettings(),
        client.getAdminHeartbeatOverview(),
      ]);
      return { settings, overview };
    });

    return () => controller.abort();
  }, [client, version]);

  const reload = useCallback(() => setVersion((v) => v + 1), []);

  /** Spec 038 §7.2 quick action: stops all agent access; the MCP panel's turn-off, without opening it. */
  const turnOffMcp = useCallback(async () => {
    const mcp = snapshot.mcp;
    if (!mcp || mcp === "error") throw new Error("MCP access settings are not loaded.");
    const response = await fetch(MCP_TURN_OFF, {
      method: "POST",
      credentials: "same-origin",
      headers: { "Content-Type": "application/json", "X-NimBus-CSRF": mcp.state.csrfToken },
    });
    if (!response.ok) throw new Error(`Turning MCP access off failed (HTTP ${response.status}).`);
    reload();
  }, [snapshot.mcp, reload]);

  /** Spec 038 §7.2 quick action: probes every included endpoint once. Returns how many. */
  const sendHeartbeat = useCallback(async () => {
    const result = await client.postAdminHeartbeatSend();
    reload();
    return result.count ?? 0;
  }, [client, reload]);

  return { snapshot, reload, turnOffMcp, sendHeartbeat };
}
