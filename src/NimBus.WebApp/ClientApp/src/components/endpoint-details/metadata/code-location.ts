// Spec 044 Phase 3: an endpoint's code location. Mirrors EndpointCodeLocation on the server,
// which stays authoritative: agents clone from and file issues against these addresses, so only
// plain https addresses are accepted.

export const MAX_CODE_LOCATION_LENGTH = 400;
export const MAX_SOURCE_SYSTEM_LENGTH = 200;
export const TICKET_PLACEHOLDER = "{ticketId}";

function validateHttps(value: string, what: string, allowQuery: boolean): string | undefined {
  if (value.length > MAX_CODE_LOCATION_LENGTH) return `${what} is at most ${MAX_CODE_LOCATION_LENGTH} characters.`;
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    return `${what} must be an absolute https address.`;
  }
  if (url.protocol !== "https:" || !url.hostname) return `${what} must be an absolute https address.`;
  if (url.username || url.password) return `${what} may not contain credentials.`;
  if (url.hash) return `${what} may not contain a fragment.`;
  if (!allowQuery && url.search) return `${what} may not contain a query string.`;
  return undefined;
}

/** Undefined when `url` is empty or a valid repository address; otherwise why not. */
export function validateRepositoryUrl(url: string | undefined | null): string | undefined {
  const value = url?.trim();
  return value ? validateHttps(value, "The repository address", false) : undefined;
}

/** Undefined when `template` is empty or a valid ticket-link template; otherwise why not. */
export function validateTicketLinkTemplate(template: string | undefined | null): string | undefined {
  const value = template?.trim();
  if (!value) return undefined;
  if (!value.includes(TICKET_PLACEHOLDER)) return `The ticket link must contain ${TICKET_PLACEHOLDER}.`;
  return validateHttps(value.replace(TICKET_PLACEHOLDER, "TICKET-1"), "The ticket link", true);
}

/** Undefined when `name` is short enough; otherwise why not. */
export function validateSourceSystem(name: string | undefined | null): string | undefined {
  return name && name.length > MAX_SOURCE_SYSTEM_LENGTH ? `The source system is at most ${MAX_SOURCE_SYSTEM_LENGTH} characters.` : undefined;
}

/** The ticket's address under `template`, or undefined. */
export function ticketLink(template: string | undefined | null, ticketId: string): string | undefined {
  if (!template || validateTicketLinkTemplate(template)) return undefined;
  return template.trim().replace(TICKET_PLACEHOLDER, encodeURIComponent(ticketId));
}
