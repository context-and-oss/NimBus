import * as api from "api-client";

/** Header carrying a Monitor dashboard token; the server admits it on /api/monitor/feed/* only. */
export const DASHBOARD_TOKEN_HEADER = "X-NimBus-Dashboard-Token";

/**
 * The dashboard token in the URL fragment, `/Monitor#token=nbdt_…`. A fragment is never sent to
 * the server, so the token stays out of request logs and Referer headers. It is read once per
 * page load and kept in memory; it is never written to storage, logs or error messages.
 */
export function readDashboardToken(hash: string = window.location.hash): string | undefined {
  const params = new URLSearchParams(hash.startsWith("#") ? hash.slice(1) : hash);
  const token = params.get("token")?.trim();
  return token ? token : undefined;
}

/** True when the page is the Monitor opened with a dashboard token: the read-only wall mode. */
export function isDashboardTokenMode(
  location: { pathname: string; hash: string } = window.location,
): boolean {
  return location.pathname.toLowerCase() === "/monitor" && readDashboardToken(location.hash) !== undefined;
}

/**
 * The API client the Monitor uses. With a token, every request carries it in the header and no
 * cookies, so the wall is a token-only caller even in a browser that is signed in.
 */
export function createMonitorClient(token?: string): api.Client {
  if (!token) return new api.Client(api.CookieAuth());

  const tokenFetch = (url: RequestInfo, init?: RequestInit): Promise<Response> => {
    const headers = new Headers(init?.headers);
    headers.set(DASHBOARD_TOKEN_HEADER, token);
    return window.fetch(url, { ...init, headers, credentials: "omit" });
  };
  return new api.Client(api.CookieAuth(), undefined, { fetch: tokenFetch });
}

/** Why the feed refused the caller, from an api-client error. */
export type FeedAccessError = "unauthorized" | "forbidden";

export function feedAccessError(error: unknown): FeedAccessError | undefined {
  const status = (error as { status?: unknown } | null)?.status;
  if (status === 401) return "unauthorized";
  if (status === 403) return "forbidden";
  return undefined;
}
