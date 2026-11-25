// Spec 051 §5–6: the adapter's read-only MCP address and the record bindings. Mirrors
// EndpointCodeLocation.ValidateAdapterMcpUrl and EndpointRecordBindings on the server, which stay
// authoritative. A caller takes a record's key from the payload at a binding's path, so a path is
// a plain dotted path, never a query.

const MAX_URL_LENGTH = 400;
const MAX_LENGTH = 200;
const PATH = /^\$(\.[A-Za-z_][A-Za-z0-9_]*)+$/;
const ENTITY = /^[A-Za-z][A-Za-z0-9_.-]*$/;

export interface RecordBindingInput {
  eventTypeId?: string;
  payloadPath?: string;
  entity?: string;
}

/** Undefined when `url` is empty or a valid adapter MCP address; otherwise why not. */
export function validateAdapterMcpUrl(url: string | undefined | null): string | undefined {
  const value = url?.trim();
  if (!value) return undefined;
  const what = "The adapter MCP address";
  if (value.length > MAX_URL_LENGTH) return `${what} is at most ${MAX_URL_LENGTH} characters.`;
  let parsed: URL;
  try {
    parsed = new URL(value);
  } catch {
    return `${what} must be an absolute https address.`;
  }
  if (parsed.protocol !== "https:" || !parsed.hostname) return `${what} must be an absolute https address.`;
  if (parsed.username || parsed.password) return `${what} may not contain credentials.`;
  if (parsed.hash) return `${what} may not contain a fragment.`;
  if (parsed.search) return `${what} may not contain a query string.`;
  return undefined;
}

/** Undefined when every binding is valid; otherwise why not. */
export function validateRecordBindings(bindings: RecordBindingInput[]): string | undefined {
  const seen = new Set<string>();
  for (const b of bindings) {
    const type = b.eventTypeId?.trim() ?? "";
    const path = b.payloadPath?.trim() ?? "";
    const entity = b.entity?.trim() ?? "";
    if (!type || type.length > MAX_LENGTH) return `Each record binding needs an event type of at most ${MAX_LENGTH} characters.`;
    if (!path || path.length > MAX_LENGTH || !PATH.test(path))
      return `The record binding path '${path}' must be a dotted path such as $.AccountId, without wildcards, indexes or filters.`;
    if (!entity || entity.length > MAX_LENGTH || !ENTITY.test(entity))
      return `The record binding entity '${entity}' must be a name of letters, digits, '.', '_' or '-'.`;
    const key = `${type.toUpperCase()}\n${path.toUpperCase()}`;
    if (seen.has(key)) return `The path ${path} is bound twice for ${type}.`;
    seen.add(key);
  }
  return undefined;
}
