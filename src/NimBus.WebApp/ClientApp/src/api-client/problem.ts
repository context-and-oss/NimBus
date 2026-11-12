// Reading the RFC 7807 problem every /api error carries (application/problem+json).
import * as api from "./index";

const MAX_DETAIL_LENGTH = 500;

/** The members of an /api error body this client reads. Any other member may also be present. */
export interface ApiProblem {
  type?: string | null;
  title?: string | null;
  status?: number | null;
  detail?: string | null;
  traceId?: string | null;
  code?: string | null;
  errors?: string[] | null;
  [member: string]: unknown;
}

/** The antiforgery refusal's code: the page's security token is missing or expired. */
export const INVALID_ANTIFORGERY_TOKEN = "InvalidAntiforgeryToken";

function parse(body: unknown): ApiProblem | undefined {
  if (body && typeof body === "object") return body as ApiProblem;
  if (typeof body !== "string" || body.trim() === "") return undefined;
  try {
    const parsed: unknown = JSON.parse(body);
    return parsed && typeof parsed === "object" ? (parsed as ApiProblem) : undefined;
  } catch {
    return undefined;
  }
}

/** The problem in a rejected generated-client call, or in a raw error body. Undefined when there is none. */
export function problemOf(error: unknown): ApiProblem | undefined {
  if (error && typeof error === "object" && api.SwaggerException.isSwaggerException(error)) {
    return parse(error.result) ?? parse(error.response);
  }
  return parse(error);
}

/**
 * The server's explanation in an error response body: a problem's `detail`, then its joined
 * `errors`, then its `title`, then a JSON string or plain-text body. Undefined when the body says
 * nothing. Long text is cut at 500 characters.
 */
export function problemDetail(body: unknown): string | undefined {
  let text: string | undefined;
  const problem = parse(body);
  if (problem) {
    if (typeof problem.detail === "string" && problem.detail.trim()) text = problem.detail;
    else if (Array.isArray(problem.errors) && problem.errors.length) text = problem.errors.join(" ");
    else if (typeof problem.title === "string" && problem.title.trim()) text = problem.title;
  } else if (typeof body === "string" && body.trim() !== "") {
    try {
      const parsed: unknown = JSON.parse(body);
      text = typeof parsed === "string" ? parsed : undefined;
    } catch {
      text = body;
    }
  }

  text = text?.trim();
  if (!text) return undefined;
  return text.length > MAX_DETAIL_LENGTH ? `${text.slice(0, MAX_DETAIL_LENGTH)}…` : text;
}

/** A message for a rejected generated-client call: the server's detail, else the error's own message. */
export function errorMessage(error: unknown): string {
  const detail = api.SwaggerException.isSwaggerException(error)
    ? problemDetail(error.result) ?? problemDetail(error.response)
    : undefined;
  if (detail) return detail;
  return error instanceof Error ? error.message : String(error);
}

/** True when a refused write's problem says the page's security token is missing or expired. */
export const isAntiforgeryRefusal = (status: number, problem: { code?: string | null } | undefined): boolean =>
  status === 400 && problem?.code === INVALID_ANTIFORGERY_TOKEN;
