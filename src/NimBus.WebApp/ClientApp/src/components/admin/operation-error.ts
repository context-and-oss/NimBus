// Turns a rejected Operations API call into a message for the card (#285).

import { problemDetail } from "api-client/problem";

/**
 * True when the storage provider is known and isn't Cosmos DB, so a Cosmos-only operation can't
 * run. An unknown provider (still loading, or unavailable) counts as available: the API's 400 is
 * the backstop then.
 */
export function isCosmosOnlyUnavailable(provider: string | null | undefined): provider is string {
  return provider !== undefined && provider !== null && provider !== "Cosmos DB";
}

/**
 * Describes a failed API call for an Operations card: the HTTP status when there is one and the
 * server's detail, falling back to the error's own message.
 */
export function describeOperationError(error: unknown): string {
  const candidate = error as { status?: unknown; response?: unknown; message?: unknown } | null | undefined;
  const message = typeof candidate?.message === "string" ? candidate.message.trim() : "";
  const status = typeof candidate?.status === "number" ? candidate.status : undefined;

  if (status === undefined) {
    return message ? `The request failed: ${message}` : "The request failed.";
  }

  const detail = problemDetail(candidate?.response) ?? message;
  let text = `The request failed (HTTP ${status})`;
  if (detail) text += `: ${detail}`;
  if (status === 403) text += `${detail ? " —" : ":"} this operation requires the site Owner role.`;
  else if (!/[.!?]$/.test(text)) text += ".";
  return text;
}
