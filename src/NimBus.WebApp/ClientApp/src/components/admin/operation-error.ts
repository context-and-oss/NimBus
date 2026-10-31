// Turns a rejected Operations API call into a message for the card (#285).

const MAX_DETAIL_LENGTH = 500;

/**
 * The server's explanation in an error response body: a ProblemDetails `detail`, then its
 * `title`, then a JSON string or plain-text body. Undefined when the body says nothing.
 */
function bodyDetail(body: unknown): string | undefined {
  if (typeof body !== "string" || body.trim() === "") return undefined;

  let text: string | undefined;
  try {
    const parsed: unknown = JSON.parse(body);
    if (typeof parsed === "string") {
      text = parsed;
    } else if (parsed && typeof parsed === "object") {
      const problem = parsed as { detail?: unknown; title?: unknown };
      if (typeof problem.detail === "string" && problem.detail.trim()) text = problem.detail;
      else if (typeof problem.title === "string" && problem.title.trim()) text = problem.title;
    }
  } catch {
    text = body;
  }

  text = text?.trim();
  if (!text) return undefined;
  return text.length > MAX_DETAIL_LENGTH ? `${text.slice(0, MAX_DETAIL_LENGTH)}…` : text;
}

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

  const detail = bodyDetail(candidate?.response) ?? message;
  let text = `The request failed (HTTP ${status})`;
  if (detail) text += `: ${detail}`;
  if (status === 403) text += `${detail ? " —" : ":"} this operation requires the site Owner role.`;
  else if (!/[.!?]$/.test(text)) text += ".";
  return text;
}
