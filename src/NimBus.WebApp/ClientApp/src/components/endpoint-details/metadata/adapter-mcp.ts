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

/**
 * Placeholders that read as examples: a placeholder equal to a real value makes an empty row look
 * filled in.
 */
export const BINDING_PLACEHOLDERS = {
  eventTypeId: "e.g. CrmAccountCreated",
  payloadPath: "e.g. $.AccountId",
  entity: "e.g. customer",
} as const;

export const ADAPTER_MCP_URL_PLACEHOLDER = "e.g. https://your-adapter.example.com/api/mcp";

/** Lighter, italic placeholder text, so an example never looks like an entered value. */
export const EXAMPLE_PLACEHOLDER_CLASS = "placeholder:italic placeholder:text-muted-foreground/60";

const FIELDS = [
  ["eventTypeId", "the event type"],
  ["payloadPath", "the payload path"],
  ["entity", "the entity"],
] as const;

/**
 * Undefined when every binding is valid; otherwise why not, naming the row (1-based) and the
 * field. Mirrors EndpointRecordBindings.Validate word for word.
 */
export function validateRecordBindings(bindings: RecordBindingInput[]): string | undefined {
  const seen = new Map<string, number>();
  for (const [index, b] of bindings.entries()) {
    const row = `Binding ${index + 1}`;
    const values = { eventTypeId: b.eventTypeId?.trim() ?? "", payloadPath: b.payloadPath?.trim() ?? "", entity: b.entity?.trim() ?? "" };
    const empty = FIELDS.filter(([key]) => !values[key]).map(([, name]) => name);
    if (empty.length === FIELDS.length) return `${row} is empty: fill in the event type, the payload path and the entity, or delete it.`;
    if (empty.length > 0) return `${row}: ${empty.join(" and ")} ${empty.length === 1 ? "is" : "are"} empty.`;

    const { eventTypeId: type, payloadPath: path, entity } = values;
    for (const [key, name] of FIELDS) {
      if (values[key].length > MAX_LENGTH) return `${row}: ${name} is longer than ${MAX_LENGTH} characters.`;
    }
    if (!PATH.test(path))
      return `${row}: the payload path '${path}' must be a dotted path such as $.AccountId, without wildcards, indexes or filters.`;
    if (!ENTITY.test(entity)) return `${row}: the entity '${entity}' must be a name of letters, digits, '.', '_' or '-'.`;
    const key = `${type.toUpperCase()}\n${path.toUpperCase()}`;
    const first = seen.get(key);
    if (first !== undefined) return `${row}: the path ${path} is already bound for ${type} in binding ${first}.`;
    seen.set(key, index + 1);
  }
  return undefined;
}
