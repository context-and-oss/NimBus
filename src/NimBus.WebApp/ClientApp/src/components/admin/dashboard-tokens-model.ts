import { isAntiforgeryRefusal, type ApiProblem } from "api-client/problem";

// Settings → Dashboard tokens: the shapes of /api/admin/dashboard-tokens and the rules the
// panel checks before sending. The server enforces the same rules.

export type DashboardTokenStatus = "active" | "revoked" | "expired";

export interface DashboardTokenInfo {
  tokenId: string;
  /** The non-secret start of the token, nbdt_<tokenId>, to recognise it. */
  prefix: string;
  name: string;
  status: DashboardTokenStatus;
  createdAtUtc: string;
  createdBy: string | null;
  expiresAtUtc: string | null;
  revokedAtUtc: string | null;
  revokedBy: string | null;
  /** Recorded at most every 5 minutes per instance; null when never used. */
  lastUsedAtUtc: string | null;
}

export interface DashboardTokensState {
  tokens: DashboardTokenInfo[];
  /** Send as the X-NimBus-CSRF header on writes. */
  csrfToken: string;
}

export interface DashboardTokenIssued {
  /** The full token. Shown once; the server keeps only its hash. */
  token: string;
  issued: DashboardTokenInfo;
  state: DashboardTokensState;
}

export const DASHBOARD_TOKENS_URL = "/api/v1/admin/dashboard-tokens";

export const NAME_MAX = 100;

/** Why a name would be refused, or undefined when it is fine. */
export function validateName(name: string): string | undefined {
  const trimmed = name.trim();
  if (!trimmed) return "Give the token a name, for example the wall it is for.";
  if (trimmed.length > NAME_MAX) return `Use at most ${NAME_MAX} characters.`;
  return undefined;
}

export const EXPIRY_OPTIONS = [
  { value: "365", label: "In 1 year" },
  { value: "90", label: "In 90 days" },
  { value: "30", label: "In 30 days" },
  { value: "never", label: "Never" },
] as const;

export type ExpiryChoice = (typeof EXPIRY_OPTIONS)[number]["value"];

/** The expiry to send for a choice, as a UTC timestamp; null for no expiry. */
export function expiryFrom(choice: ExpiryChoice, now: Date = new Date()): string | null {
  if (choice === "never") return null;
  return new Date(now.getTime() + Number(choice) * 24 * 60 * 60 * 1000).toISOString();
}

/**
 * The Monitor URL a wall opens. The token goes in the fragment, which browsers never send to the
 * server, so it stays out of request logs.
 */
export function monitorUrl(token: string, origin: string = window.location.origin): string {
  return `${origin}/Monitor#token=${encodeURIComponent(token)}`;
}

export const activeTokenCount = (state: DashboardTokensState) =>
  state.tokens.filter((t) => t.status === "active").length;

/** A readable reason for a refused request, from the server's problem body when there is one. */
export async function problemMessage(response: Response): Promise<string> {
  if (response.status === 401 || response.status === 403)
    return "Only site Owners can manage dashboard tokens.";
  if (response.status === 501)
    return "This deployment's storage provider cannot store dashboard tokens.";
  const problem = (await response.json().catch(() => undefined)) as ApiProblem | undefined;
  if (isAntiforgeryRefusal(response.status, problem))
    return "The page's security token expired. Reload and try again.";
  if (problem?.errors?.length) return problem.errors.join(" ");
  if (problem?.detail) return problem.detail;
  return `The request failed (HTTP ${response.status}).`;
}
