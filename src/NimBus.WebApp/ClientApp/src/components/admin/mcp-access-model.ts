// Spec 037 — Settings → MCP access. Types mirror the /api/admin/mcp/* contract in
// api-spec.yaml; the change list and the access preview mirror the server rules
// (McpAccessRules, McpAccessPolicy) for display only. The server stays authoritative.

export type PeopleMode = "all" | "listed";
export type ClientMode = "any" | "approved";
export type Visibility = "all" | "allExcept";
export type ChangeScope = "allVisible" | "listed";

export interface Principal { principal: string; label?: string | null }
export interface ApprovedClient { clientId: string; name: string; mayChange: boolean }

export interface McpSettings {
  revision?: string | null;
  updatedBy?: string | null;
  updatedAtUtc?: string | null;
  enabled: boolean;
  capabilities: { payloads: boolean; report: boolean; classify: boolean; resubmit: boolean; skip: boolean };
  allowWorkloads: boolean;
  /** Spec 044: workloads with the Nimbus.Classify app role may record classifications. Effective only with allowWorkloads. */
  allowWorkloadClassification?: boolean;
  people: { mode: PeopleMode; principals: Principal[] };
  clients: { mode: ClientMode; approved: ApprovedClient[] };
  endpoints: { visibility: Visibility; hidden: string[]; changes: ChangeScope; changeOn: string[] };
  limits: { requestsPerWindow?: number | null; mutationsPerWindow?: number | null };
}

export interface WindowLimit { permit: number; windowSeconds: number }

export interface McpDeployment {
  mode: "disabled" | "localDevelopment" | "entra";
  endpointUrl: string;
  resourceMetadataUrl?: string | null;
  serverVersion: string;
  tenantId?: string | null;
  clientId?: string | null;
  applicationIdUri?: string | null;
  authority?: string | null;
  allowedOrigins: string[];
  rateLimitsEnabled: boolean;
  requestLimit: WindowLimit;
  mutationLimit: WindowLimit;
  endpoints: string[];
}

export interface McpState {
  saved: McpSettings;
  effective?: McpSettings | null;
  policyLoaded: boolean;
  deployment: McpDeployment;
  failureIntelligenceEnabled: boolean;
  csrfToken: string;
}

export type McpEndpointState = "notSetUp" | "unavailable" | "serving" | "off";

/** Whether /mcp is serving right now; shared by the panel's tiles and the Settings list. */
export function mcpEndpointState(state: McpState): McpEndpointState {
  if (state.deployment.mode === "disabled") return "notSetUp";
  if (!state.policyLoaded) return "unavailable";
  return (state.effective ?? state.saved).enabled ? "serving" : "off";
}

export interface McpChange { text: string; widens: boolean }
export interface McpProblem { code: string; errors?: string[]; changes?: McpChange[] }

export interface McpActivityItem {
  atUtc: string; kind: "action" | "refused" | "settings"; type: string;
  endpointId?: string | null; eventId?: string | null; auditor?: string | null;
  clientId?: string | null; reason?: string | null; detail?: string | null;
}
export interface McpActivity {
  hours: number; actions: number; refused: number; capped: boolean;
  actionsByType: { name: string; count: number }[];
  refusedByReason: { name: string; count: number }[];
  items: McpActivityItem[];
  refusedClients: { clientId: string; calls: number; lastAuditor?: string | null; lastAtUtc: string }[];
}

export type CapabilityKey = keyof McpSettings["capabilities"];

export interface CapabilityInfo {
  key: CapabilityKey; label: string; risk: "data" | "low" | "medium" | "high"; riskText: string;
  tools: string[]; scope: string; role: string; description: string; note?: string;
}

export const READ_TOOL_COUNT = 12;
export const TOTAL_TOOL_COUNT = 18;

export const CAPABILITIES: CapabilityInfo[] = [
  { key: "payloads", label: "Raw payloads", risk: "data", riskText: "data", tools: ["nimbus_get_message · includePayload"],
    scope: "nimbus.payload.read", role: "PiiReader",
    description: "Return the stored event payload when an agent asks for it. Without this, agents see status, errors and history only.",
    note: "Payloads go into the agent's model context and may leave your tenant with the AI provider. Only callers who are PiiReader on the endpoint get them." },
  { key: "report", label: "Mark reported", risk: "low", riskText: "low", tools: ["nimbus_set_message_reported"],
    scope: "nimbus.annotate", role: "Contributor",
    description: "Set or clear the \"reported\" marker, optionally with an external ticket ID." },
  { key: "classify", label: "Classify failures", risk: "low", riskText: "low", tools: ["nimbus_record_classification", "nimbus_classify_failure (deprecated)"],
    scope: "nimbus.classify", role: "Contributor",
    description: "Let agents record their classification of a failure. It is advisory, changes no message, and NimBus composes the guidance. The deprecated nimbus_classify_failure also asks the built-in provider, at a cost." },
  { key: "resubmit", label: "Resubmit messages", risk: "medium", riskText: "medium", tools: ["nimbus_prepare_action", "nimbus_resubmit_message"],
    scope: "nimbus.resubmit", role: "Contributor",
    description: "Replay the latest stored payload to the endpoint after a preview, with a state guard and an audit row written first." },
  { key: "skip", label: "Skip messages", risk: "high", riskText: "high", tools: ["nimbus_prepare_action", "nimbus_skip_message"],
    scope: "nimbus.skip", role: "Contributor",
    description: "Mark a message as never to be processed. This can release the later messages waiting in its session and can't be undone.",
    note: "Agents should show the preview and ask before skipping, but NimBus can't verify that a person approved it. Consider leaving Skip to the Web UI until a pilot has run for a while." },
];

const sameSet = (a: string[], b: string[]) => {
  const x = new Set(a.map(v => v.toLowerCase()));
  return a.length === b.length && b.every(v => x.has(v.toLowerCase()));
};

const listChanges = (before: string[], after: string[], added: (v: string) => McpChange, removed: (v: string) => McpChange) => {
  const was = new Set(before.map(v => v.toLowerCase()));
  const now = new Set(after.map(v => v.toLowerCase()));
  return [
    ...after.filter(v => !was.has(v.toLowerCase())).map(added),
    ...before.filter(v => !now.has(v.toLowerCase())).map(removed),
  ];
};

/** The differences from `a` to `b`, each marked as widening or narrowing access (Spec 037 §6.4). */
export function describeChanges(a: McpSettings, b: McpSettings): McpChange[] {
  const out: McpChange[] = [];
  const add = (text: string, widens: boolean) => out.push({ text, widens });

  if (a.enabled !== b.enabled) add(b.enabled ? "Turn the MCP endpoint on" : "Turn the MCP endpoint off", b.enabled);
  const names: Record<CapabilityKey, string> = {
    payloads: "raw payloads", report: "marking messages reported", classify: "classifying failures",
    resubmit: "resubmitting messages", skip: "skipping messages",
  };
  for (const key of Object.keys(names) as CapabilityKey[]) {
    if (a.capabilities[key] !== b.capabilities[key]) add(`${b.capabilities[key] ? "Allow" : "Stop"} ${names[key]}`, b.capabilities[key]);
  }
  if (a.allowWorkloads !== b.allowWorkloads) add(b.allowWorkloads ? "Accept workload (app-only) tokens" : "Refuse workload (app-only) tokens", b.allowWorkloads);
  if (Boolean(a.allowWorkloadClassification) !== Boolean(b.allowWorkloadClassification))
    add(b.allowWorkloadClassification ? "Let workloads record failure classifications" : "Stop workloads recording failure classifications", Boolean(b.allowWorkloadClassification));

  const before = (p: Principal[]) => p.map(x => x.principal);
  if (a.people.mode !== b.people.mode) {
    add(b.people.mode === "all" ? "Let everyone with a NimBus role connect" : `Only listed users and groups may connect (${before(b.people.principals).join(", ")})`, b.people.mode === "all");
  } else if (b.people.mode === "listed" && !sameSet(before(a.people.principals), before(b.people.principals))) {
    out.push(...listChanges(before(a.people.principals), before(b.people.principals),
      v => ({ text: `Let ${v} connect`, widens: true }), v => ({ text: `Stop ${v} connecting`, widens: false })));
  }

  if (a.clients.mode !== b.clients.mode) {
    add(b.clients.mode === "any" ? "Accept any client pre-authorized in Entra" : `Accept only approved clients (${b.clients.approved.map(c => c.name).join(", ")})`, b.clients.mode === "any");
  } else if (b.clients.mode === "approved") {
    const was = new Map(a.clients.approved.map(c => [c.clientId.toLowerCase(), c]));
    const now = new Set(b.clients.approved.map(c => c.clientId.toLowerCase()));
    for (const c of b.clients.approved) {
      const old = was.get(c.clientId.toLowerCase());
      if (!old) add(`Approve client ${c.name}${c.mayChange ? ", which may change messages" : ""}`, true);
      else if (old.mayChange !== c.mayChange) add(c.mayChange ? `Let client ${c.name} change messages` : `Stop client ${c.name} changing messages`, c.mayChange);
    }
    for (const c of a.clients.approved) if (!now.has(c.clientId.toLowerCase())) add(`Remove client ${c.name}`, false);
  }

  if (a.endpoints.visibility !== b.endpoints.visibility) {
    add(b.endpoints.visibility === "all" ? "Show agents every endpoint they can read" : `Hide ${b.endpoints.hidden.join(", ")} from agents`, b.endpoints.visibility === "all");
  } else if (b.endpoints.visibility === "allExcept") {
    out.push(...listChanges(a.endpoints.hidden, b.endpoints.hidden,
      v => ({ text: `Hide ${v} from agents`, widens: false }), v => ({ text: `Show ${v} to agents again`, widens: true })));
  }
  if (a.endpoints.changes !== b.endpoints.changes) {
    add(b.endpoints.changes === "allVisible" ? "Allow changes on every visible endpoint" : `Allow changes only on ${b.endpoints.changeOn.join(", ")}`, b.endpoints.changes === "allVisible");
  } else if (b.endpoints.changes === "listed") {
    out.push(...listChanges(a.endpoints.changeOn, b.endpoints.changeOn,
      v => ({ text: `Allow changes on ${v}`, widens: true }), v => ({ text: `Stop changes on ${v}`, widens: false })));
  }

  const limit = (name: string, x?: number | null, y?: number | null) => {
    if ((x ?? null) === (y ?? null)) return;
    add(`${name}: ${x ?? "deployment value"} → ${y ?? "deployment value"}`, y == null || (x != null && y > x));
  };
  limit("Tool calls per window", a.limits.requestsPerWindow, b.limits.requestsPerWindow);
  limit("Message changes per window", a.limits.mutationsPerWindow, b.limits.mutationsPerWindow);
  return out;
}

/** Client-side mirror of the server's validation, for messages before a round trip. */
export function validate(s: McpSettings, d: McpDeployment): string | undefined {
  if (s.people.mode === "listed" && s.people.principals.length === 0) return "Add at least one user or group, or let everyone with a NimBus role connect.";
  if (s.clients.mode === "approved" && s.clients.approved.length === 0) return "Approve at least one client, or accept any client pre-authorized in Entra.";
  if (s.endpoints.visibility === "allExcept" && s.endpoints.hidden.length === 0) return "Pick at least one endpoint to hide, or show all endpoints.";
  if (s.endpoints.changes === "listed" && s.endpoints.changeOn.length === 0) return "Pick at least one endpoint where agents may change messages.";
  const inRange = (v: number | null | undefined, max: number) => v == null || (Number.isInteger(v) && v >= 1 && v <= max);
  if (!inRange(s.limits.requestsPerWindow, d.requestLimit.permit) || !inRange(s.limits.mutationsPerWindow, d.mutationLimit.permit))
    return "A limit is outside its allowed range.";
  return undefined;
}

export type Persona = "reader" | "contributor" | "pii" | "workload";
export const PERSONAS: Record<Persona, { label: string; delegated: boolean; contributor: boolean; pii: boolean }> = {
  reader: { label: "Reader", delegated: true, contributor: false, pii: false },
  contributor: { label: "Contributor", delegated: true, contributor: true, pii: false },
  pii: { label: "Contributor + PiiReader", delegated: true, contributor: true, pii: true },
  workload: { label: "Workload", delegated: false, contributor: false, pii: false },
};

export interface PreviewRow { name: string; allowed: boolean; why: string }

/** What a caller of the given kind could use under `s` — a preview of the server's rules. */
export function previewAccess(s: McpSettings, d: McpDeployment, persona: Persona, classifyAvailable: boolean): PreviewRow[] {
  const p = PERSONAS[persona];
  const local = d.mode === "localDevelopment";
  const gate = d.mode === "disabled" ? "not set up" : !s.enabled ? "endpoint off" : !p.delegated && !s.allowWorkloads ? "workloads off" : undefined;
  const rows: PreviewRow[] = [{ name: `${READ_TOOL_COUNT} read tools`, allowed: !gate, why: gate ?? (local ? "Reader" : p.delegated ? "nimbus.observe" : "Nimbus.Observe role") }];
  const cap = (name: string, key: CapabilityKey): PreviewRow => {
    if (gate) return { name, allowed: false, why: gate };
    if (!s.capabilities[key]) return { name, allowed: false, why: "switched off here" };
    if (key === "classify" && !classifyAvailable) return { name, allowed: false, why: "Failure intelligence off" };
    // Spec 044 §6.6: a workload may record classifications with the Nimbus.Classify role, when allowed here.
    if (!p.delegated && key === "classify")
      return s.allowWorkloadClassification
        ? { name, allowed: true, why: "Nimbus.Classify role + Contributor" }
        : { name, allowed: false, why: "workload recording off" };
    if (!p.delegated) return { name, allowed: false, why: key === "payloads" ? "no app role for payloads" : "workloads can't change" };
    if (key === "payloads") return p.pii ? { name, allowed: true, why: local ? "PiiReader" : "scope + PiiReader" } : { name, allowed: false, why: "needs PiiReader" };
    return p.contributor ? { name, allowed: true, why: local ? "Contributor" : "scope + Contributor" } : { name, allowed: false, why: "needs Contributor" };
  };
  rows.push(cap("payloads (includePayload)", "payloads"), cap("nimbus_set_message_reported", "report"),
    cap("nimbus_record_classification", "classify"), cap("nimbus_resubmit_message", "resubmit"), cap("nimbus_skip_message", "skip"));
  const prepare = rows[4].allowed || rows[5].allowed;
  rows.push({ name: "nimbus_prepare_action", allowed: prepare, why: gate ?? (prepare ? "resubmit or skip allowed" : "needs resubmit or skip") });
  return rows;
}

/** How many tools `tools/list` returns under `s` for a client that may change messages. */
export function listedToolCount(s: McpSettings, d: McpDeployment): number {
  if (d.mode === "disabled" || !s.enabled) return 0;
  const c = s.capabilities;
  // Classify lists nimbus_record_classification and the deprecated nimbus_classify_failure.
  return READ_TOOL_COUNT + [c.report, c.resubmit, c.skip].filter(Boolean).length + (c.classify ? 2 : 0) + (c.resubmit || c.skip ? 1 : 0);
}
