import { isAntiforgeryRefusal, type ApiProblem } from "api-client/problem";

// Settings → Agent keys (Spec 039): the shapes of /api/admin/agent-keys and the rules the
// panel checks before sending. The server enforces the same rules.

export type AgentKeyStatus = "active" | "revoked" | "expired";

export interface AgentKeyInfo {
  keyId: string;
  /** The non-secret start of the key, nbak_<keyId>, to recognise it. */
  prefix: string;
  agentId: string;
  name: string | null;
  status: AgentKeyStatus;
  createdAtUtc: string;
  createdBy: string | null;
  expiresAtUtc: string | null;
  revokedAtUtc: string | null;
  revokedBy: string | null;
  /** Recorded at most every 5 minutes per instance; null when never used. */
  lastUsedAtUtc: string | null;
  /** Where the key authenticates (Spec 052); missing on a server from before keys had a use. */
  use?: AgentKeyUse;
  /** What an MCP key may do; null for an Agent API key. */
  mcp?: AgentKeyMcpGrant | null;
  /** The key id this key rotated, when it was issued by Rotate. */
  replaces?: string | null;
}

/** agentApi opens /api/agent/*; mcp opens /mcp as an unattended workload. */
export type AgentKeyUse = "agentApi" | "mcp";

/** The most an MCP key may do. Observe is implied; each permission still needs its MCP access switch. */
export interface AgentKeyMcpGrant {
  classify: boolean;
  annotate: boolean;
  resubmit: boolean;
  requestsPerWindow: number | null;
}

/** What the panel reads from the MCP access state to say whether MCP keys can work yet. */
export interface McpKeyReadiness {
  deployment: { mode: string; agentKeysEnabled?: boolean; endpointUrl?: string };
  saved: { allowAgentKeys?: boolean; allowWorkloads?: boolean };
}

export const MCP_SETTINGS_URL = "/api/v1/admin/mcp/settings";

/** The grant permissions, in the order the form and the table show them. */
export const MCP_PERMISSIONS = [
  { key: "classify", label: "Record classifications", needs: "Workloads may record classifications" },
  { key: "annotate", label: "Mark messages reported", needs: "Workloads may mark messages reported" },
  { key: "resubmit", label: "Resubmit failed messages", needs: "Workloads may resubmit" },
] as const;

/** The grant summary for the table, for example "observe, classify, resubmit". */
export function grantSummary(grant: AgentKeyMcpGrant | null | undefined): string {
  const held = MCP_PERMISSIONS.filter((p) => grant?.[p.key]).map((p) => p.key);
  return ["observe", ...held].join(", ");
}

/** Why an MCP key won't work yet, or undefined when the deployment and the policy allow keys. */
export function mcpKeyBlocker(readiness: McpKeyReadiness | undefined): string | undefined {
  if (!readiness) return undefined;
  if (readiness.deployment.mode !== "entra")
    return "This deployment has no MCP app registration, and keys work only next to Entra sign-in.";
  if (!readiness.deployment.agentKeysEnabled)
    return "The deployment doesn't accept keys on /mcp yet: set the app setting NimBus__Mcp__AgentKeys__Enabled=true.";
  if (!readiness.saved.allowWorkloads || !readiness.saved.allowAgentKeys)
    return "Turn on Unattended workloads and Agent API keys in Settings → MCP access.";
  return undefined;
}

const DAY_MS = 24 * 60 * 60 * 1000;

/** Whether an active MCP key expires within 14 days, so its owner should rotate it. */
export function expiresSoon(key: Pick<AgentKeyInfo, "use" | "status" | "expiresAtUtc">, now: Date = new Date()): boolean {
  if (key.use !== "mcp" || key.status !== "active" || !key.expiresAtUtc) return false;
  return new Date(key.expiresAtUtc).getTime() - now.getTime() <= 14 * DAY_MS;
}

export interface AgentKeysState {
  keys: AgentKeyInfo[];
  /** Send as the X-NimBus-CSRF header on writes. */
  csrfToken: string;
}

export interface AgentKeyIssued {
  /** The full key. Shown once; the server keeps only its hash. */
  key: string;
  issued: AgentKeyInfo;
  state: AgentKeysState;
}

export const AGENT_KEYS_URL = "/api/v1/admin/agent-keys";

const AGENT_ID = /^[A-Za-z0-9][A-Za-z0-9._-]{0,63}$/;

/** Why an agent id would be refused, or undefined when it is fine. */
export function validateAgentId(agentId: string): string | undefined {
  return AGENT_ID.test(agentId)
    ? undefined
    : "Use 1 to 64 letters, digits, '.', '_' or '-', starting with a letter or digit.";
}

export const NAME_MAX = 100;

export const EXPIRY_OPTIONS = [
  { value: "never", label: "Never" },
  { value: "30", label: "In 30 days" },
  { value: "90", label: "In 90 days" },
  { value: "365", label: "In 1 year" },
] as const;

export type ExpiryChoice = (typeof EXPIRY_OPTIONS)[number]["value"];

/** An MCP key always expires, within a year (Spec 052 §6.10); 90 days is the default. */
export const MCP_EXPIRY_OPTIONS = EXPIRY_OPTIONS.filter((o) => o.value !== "never");
export const MCP_DEFAULT_EXPIRY: ExpiryChoice = "90";

/** The expiry to send for a choice, as a UTC timestamp; null for no expiry. */
export function expiryFrom(choice: ExpiryChoice, now: Date = new Date()): string | null {
  if (choice === "never") return null;
  return new Date(now.getTime() + Number(choice) * 24 * 60 * 60 * 1000).toISOString();
}

export const activeKeyCount = (state: AgentKeysState) =>
  state.keys.filter((k) => k.status === "active").length;

/** Active keys per use; a key without a use is an Agent API key. */
export function activeKeysByUse(state: AgentKeysState): Record<AgentKeyUse, number> {
  const active = state.keys.filter((k) => k.status === "active");
  return {
    agentApi: active.filter((k) => (k.use ?? "agentApi") === "agentApi").length,
    mcp: active.filter((k) => k.use === "mcp").length,
  };
}

/** A readable reason for a refused write, from the server's problem body when there is one. */
export async function problemMessage(response: Response): Promise<string> {
  if (response.status === 401 || response.status === 403)
    return "Only site Owners can manage agent keys.";
  const problem = (await response.json().catch(() => undefined)) as ApiProblem | undefined;
  if (isAntiforgeryRefusal(response.status, problem))
    return "The page's security token expired. Reload and try again.";
  if (problem?.errors?.length) return problem.errors.join(" ");
  if (problem?.detail) return problem.detail;
  return `The request failed (HTTP ${response.status}).`;
}
