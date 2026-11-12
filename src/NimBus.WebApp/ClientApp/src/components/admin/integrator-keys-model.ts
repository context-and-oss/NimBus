import { isAntiforgeryRefusal, type ApiProblem } from "api-client/problem";

// Settings → Integrator keys (Spec 045): the shapes of /api/v1/admin/integrator-keys and the rules
// the panel checks before sending. The server enforces the same rules.

export type IntegratorKeyStatus = "active" | "revoked" | "expired";

/** Owner is deliberately not a role a key can carry. */
export type IntegratorKeyRole = "reader" | "contributor";

export interface IntegratorKeyInfo {
  keyId: string;
  /** The non-secret start of the key, nbik_<keyId>, to recognise it. */
  prefix: string;
  name: string;
  role: IntegratorKeyRole;
  /** The endpoints the role applies to; null for every endpoint. */
  endpoints: string[] | null;
  canReadPii: boolean;
  status: IntegratorKeyStatus;
  createdAtUtc: string;
  createdBy: string | null;
  expiresAtUtc: string | null;
  revokedAtUtc: string | null;
  revokedBy: string | null;
  /** Recorded at most every 5 minutes per instance; null when never used. */
  lastUsedAtUtc: string | null;
}

export interface IntegratorKeysState {
  keys: IntegratorKeyInfo[];
  /** The endpoint ids a key can be scoped to. */
  endpoints: string[];
  /** Send as the X-NimBus-CSRF header on writes. */
  csrfToken: string;
}

export interface IntegratorKeyIssued {
  /** The full key. Shown once; the server keeps only its hash. */
  key: string;
  issued: IntegratorKeyInfo;
  state: IntegratorKeysState;
}

export const INTEGRATOR_KEYS_URL = "/api/v1/admin/integrator-keys";

/** The header a caller sends the key in. */
export const API_KEY_HEADER = "X-NimBus-Api-Key";

export const NAME_MAX = 100;

export const ROLE_OPTIONS = [
  { value: "reader", label: "Reader — read endpoints, events and audits" },
  { value: "contributor", label: "Contributor — also resubmit, skip and settle" },
] as const;

export const ROLE_LABEL: Record<IntegratorKeyRole, string> = { reader: "Reader", contributor: "Contributor" };

/** Why a name would be refused, or undefined when it is fine. */
export function validateName(name: string): string | undefined {
  const trimmed = name.trim();
  if (!trimmed) return "Give the key a name, for example the system that will use it.";
  if (trimmed.length > NAME_MAX) return `Use at most ${NAME_MAX} characters.`;
  return undefined;
}

/** Why an endpoint scope would be refused, or undefined when it is fine. */
export function validateScope(allEndpoints: boolean, chosen: string[]): string | undefined {
  if (!allEndpoints && chosen.length === 0) return "Choose at least one endpoint, or allow every endpoint.";
  return undefined;
}

export const EXPIRY_OPTIONS = [
  { value: "90", label: "In 90 days" },
  { value: "30", label: "In 30 days" },
  { value: "365", label: "In 1 year" },
  { value: "never", label: "Never" },
] as const;

export type ExpiryChoice = (typeof EXPIRY_OPTIONS)[number]["value"];

/** The expiry to send for a choice, as a UTC timestamp; null for no expiry. */
export function expiryFrom(choice: ExpiryChoice, now: Date = new Date()): string | null {
  if (choice === "never") return null;
  return new Date(now.getTime() + Number(choice) * 24 * 60 * 60 * 1000).toISOString();
}

/** A ready-to-run call that proves the key works. */
export function exampleCall(key: string, origin: string = window.location.origin): string {
  return `curl -H "${API_KEY_HEADER}: ${key}" ${origin}/api/v1/endpoint/status/count`;
}

/** "All endpoints", or the scoped endpoints joined. */
export const scopeLabel = (key: Pick<IntegratorKeyInfo, "endpoints">) =>
  key.endpoints === null ? "All endpoints" : key.endpoints.join(", ");

export const activeKeyCount = (state: IntegratorKeysState) =>
  state.keys.filter((k) => k.status === "active").length;

/** A readable reason for a refused request, from the server's problem body when there is one. */
export async function problemMessage(response: Response): Promise<string> {
  if (response.status === 401 || response.status === 403)
    return "Only site Owners can manage integrator keys.";
  if (response.status === 501)
    return "This deployment's storage provider cannot store integrator keys.";
  const problem = (await response.json().catch(() => undefined)) as ApiProblem | undefined;
  if (isAntiforgeryRefusal(response.status, problem))
    return "The page's security token expired. Reload and try again.";
  if (problem?.errors?.length) return problem.errors.join(" ");
  if (problem?.detail) return problem.detail;
  return `The request failed (HTTP ${response.status}).`;
}
