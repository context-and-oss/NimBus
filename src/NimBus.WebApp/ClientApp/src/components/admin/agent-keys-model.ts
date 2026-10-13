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

export const AGENT_KEYS_URL = "/api/admin/agent-keys";

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

/** The expiry to send for a choice, as a UTC timestamp; null for no expiry. */
export function expiryFrom(choice: ExpiryChoice, now: Date = new Date()): string | null {
  if (choice === "never") return null;
  return new Date(now.getTime() + Number(choice) * 24 * 60 * 60 * 1000).toISOString();
}

export const activeKeyCount = (state: AgentKeysState) =>
  state.keys.filter((k) => k.status === "active").length;

/** A readable reason for a refused write, from the server's AgentApiKeyProblem when there is one. */
export async function problemMessage(response: Response): Promise<string> {
  if (response.status === 401 || response.status === 403)
    return "Only site Owners can manage agent keys.";
  if (response.status === 400 && !(response.headers.get("content-type") ?? "").includes("json"))
    return "The page's security token expired. Reload and try again.";
  try {
    const problem = (await response.json()) as { code?: string; errors?: string[] };
    if (problem.errors?.length) return problem.errors.join(" ");
  } catch {
    // Not a problem body; fall through.
  }
  return `The request failed (HTTP ${response.status}).`;
}
