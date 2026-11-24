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
  capabilities: {
    payloads: boolean; report: boolean; classify: boolean; resubmit: boolean; skip: boolean;
    /** Spec 035 Phase 3. Off unless a site Owner turns it on; a missing value is off. */
    settleHandoffs?: boolean;
    /** Spec 035 Phase 3. Off unless a site Owner turns it on; a missing value is off. */
    recoverDeferred?: boolean;
    /** Spec 035 Phase 3. Off unless a site Owner turns it on; a missing value is off. */
    editPayloads?: boolean;
  };
  allowWorkloads: boolean;
  /** Spec 044: workloads with the Nimbus.Workload.Classify app role may record classifications. Effective only with allowWorkloads. */
  allowWorkloadClassification?: boolean;
  /** Spec 044 Phase 2: workloads with the Nimbus.Workload.Resubmit app role may resubmit unattended. Effective only with allowWorkloads. */
  allowWorkloadResubmit?: boolean;
  /** Spec 044 Phase 3: workloads with the Nimbus.Workload.Annotate app role may mark messages reported. */
  allowWorkloadAnnotation?: boolean;
  /** Agent API keys issued for MCP may connect as workloads (Spec 052). Off by default. */
  allowAgentKeys?: boolean;
  /** Resubmits per workload client in a rolling 24 hours (0-500, default 20). */
  workloadResubmitsPerDay?: number | null;
  /** Unsuccessful workload resubmits of one message before it stays with a person (1-5, default 2). */
  workloadResubmitsPerMessage?: number | null;
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
  /** Whether the deployment accepts agent API keys on /mcp (NimBus__Mcp__AgentKeys__Enabled, Spec 052). */
  agentKeysEnabled?: boolean;
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
  /** The MCP agent key the call came with (Spec 052); null for Entra callers. */
  agentKeyId?: string | null;
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
  /** A few words under the label in the capability table. */
  hint: string;
}

/** The tools that only read, without nimbus_prepare_action. They have no switch. */
export const READ_TOOL_COUNT = 15;
/** Every tool the server registers: the read tools, nimbus_prepare_action and the eight that change something. */
export const TOTAL_TOOL_COUNT = 24;

export const CAPABILITIES: CapabilityInfo[] = [
  { key: "payloads", label: "Raw payloads", hint: "May contain personal data", risk: "data", riskText: "data", tools: ["nimbus_get_message · includePayload"],
    scope: "nimbus.payload.read", role: "PiiReader",
    description: "Return the stored event payload when an agent asks for it. Without this, agents see status, errors and history only.",
    note: "Payloads go into the agent's model context and may leave your tenant with the AI provider. Only callers who are PiiReader on the endpoint get them." },
  { key: "report", label: "Mark reported", hint: "Link a failed message to a ticket", risk: "low", riskText: "low", tools: ["nimbus_set_message_reported"],
    scope: "nimbus.annotate", role: "Contributor",
    description: "Set or clear the \"reported\" marker, optionally with an external ticket ID." },
  { key: "classify", label: "Classify failures", hint: "Record an advisory classification", risk: "low", riskText: "low", tools: ["nimbus_record_classification"],
    scope: "nimbus.classify", role: "Contributor",
    description: "Let agents record their classification of a failure. It is advisory, changes no message, and NimBus composes the guidance." },
  { key: "resubmit", label: "Resubmit messages", hint: "Replay the stored payload", risk: "medium", riskText: "medium", tools: ["nimbus_prepare_action", "nimbus_resubmit_message"],
    scope: "nimbus.resubmit", role: "Contributor",
    description: "Replay the latest stored payload to the endpoint after a preview, with a state guard and an audit row written first." },
  { key: "skip", label: "Skip messages", hint: "Never processed; may release the session", risk: "high", riskText: "high", tools: ["nimbus_prepare_action", "nimbus_skip_message"],
    scope: "nimbus.skip", role: "Contributor",
    description: "Mark a message as never to be processed. This can release the later messages waiting in its session and can't be undone.",
    note: "Agents should show the preview and ask before skipping, but NimBus can't verify that a person approved it. Consider leaving Skip to the Web UI until a pilot has run for a while." },
  { key: "settleHandoffs", label: "Settle pending handoffs", hint: "Complete or fail a handoff", risk: "medium", riskText: "medium", tools: ["nimbus_prepare_action", "nimbus_settle_handoff"],
    scope: "nimbus.settle", role: "Contributor",
    description: "Complete or fail a message that waits for external work. The endpoint records the outcome without running its handler again.",
    note: "The agent states the outcome of work that happened outside NimBus. NimBus can't check it: the person at the agent must know whether the external job finished." },
  { key: "recoverDeferred", label: "Recover deferred messages", hint: "Reprocess a session's deferred messages", risk: "medium", riskText: "medium", tools: ["nimbus_prepare_action", "nimbus_reprocess_deferred", "nimbus_skip_deferred_tracking"],
    scope: "nimbus.recover", role: "Contributor",
    description: "Reprocess the deferred messages of a session that no failed message still blocks, or mark a stale Deferred tracking record as Skipped after the broker shows no matching message.",
    note: "Skipping a tracking record changes NimBus only. Nothing is removed from Service Bus, and work that is already running is not cancelled." },
  { key: "editPayloads", label: "Resubmit with an edited payload", hint: "Replace the payload, then replay", risk: "high", riskText: "high", tools: ["nimbus_prepare_action", "nimbus_resubmit_with_changes"],
    scope: "nimbus.edit", role: "Contributor + PiiReader",
    description: "Replay a failed message with a payload the agent edited. The event type can't change, masked values are refused, and the audit row records which fields changed, not their values.",
    note: "The endpoint processes what the agent sends in place of the stored payload. Agents should show the changed fields and ask first, but NimBus can't verify that a person approved them." },
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

/** Server defaults for the unattended-resubmit limits (McpAccessSettings). */
export const WORKLOAD_RESUBMITS_PER_DAY = 20;
export const WORKLOAD_RESUBMITS_PER_MESSAGE = 2;

/** The differences from `a` to `b`, each marked as widening or narrowing access (Spec 037 §6.4). */
export function describeChanges(a: McpSettings, b: McpSettings): McpChange[] {
  const out: McpChange[] = [];
  const add = (text: string, widens: boolean) => out.push({ text, widens });

  if (a.enabled !== b.enabled) add(b.enabled ? "Turn the MCP endpoint on" : "Turn the MCP endpoint off", b.enabled);
  const names: Record<CapabilityKey, string> = {
    payloads: "raw payloads", report: "marking messages reported", classify: "classifying failures",
    resubmit: "resubmitting messages", skip: "skipping messages",
    settleHandoffs: "settling pending handoffs", recoverDeferred: "recovering deferred messages",
    editPayloads: "resubmitting with an edited payload",
  };
  for (const key of Object.keys(names) as CapabilityKey[]) {
    // A missing switch is off, as on the server.
    const was = Boolean(a.capabilities[key]);
    const now = Boolean(b.capabilities[key]);
    if (was !== now) add(`${now ? "Allow" : "Stop"} ${names[key]}`, now);
  }
  if (a.allowWorkloads !== b.allowWorkloads) add(b.allowWorkloads ? "Accept workload (app-only) tokens" : "Refuse workload (app-only) tokens", b.allowWorkloads);
  if (Boolean(a.allowWorkloadClassification) !== Boolean(b.allowWorkloadClassification))
    add(b.allowWorkloadClassification ? "Let workloads record failure classifications" : "Stop workloads recording failure classifications", Boolean(b.allowWorkloadClassification));
  if (Boolean(a.allowWorkloadResubmit) !== Boolean(b.allowWorkloadResubmit))
    add(b.allowWorkloadResubmit ? "Let workloads resubmit failed messages unattended" : "Stop workloads resubmitting messages", Boolean(b.allowWorkloadResubmit));
  if (Boolean(a.allowWorkloadAnnotation) !== Boolean(b.allowWorkloadAnnotation))
    add(b.allowWorkloadAnnotation ? "Let workloads mark messages reported" : "Stop workloads marking messages reported", Boolean(b.allowWorkloadAnnotation));
  // Same wording as the server (McpAccessRules): a credential path outside Entra widens access.
  if (Boolean(a.allowAgentKeys) !== Boolean(b.allowAgentKeys))
    add(b.allowAgentKeys ? "Let hosted agents connect with agent API keys" : "Stop accepting agent API keys", Boolean(b.allowAgentKeys));
  const perDay = (s: McpSettings) => s.workloadResubmitsPerDay ?? WORKLOAD_RESUBMITS_PER_DAY;
  const perMessage = (s: McpSettings) => s.workloadResubmitsPerMessage ?? WORKLOAD_RESUBMITS_PER_MESSAGE;
  if (perDay(a) !== perDay(b))
    add(`Workload resubmits per day: ${perDay(a)} → ${perDay(b)}`, perDay(b) > perDay(a));
  if (perMessage(a) !== perMessage(b))
    add(`Unsuccessful workload resubmits per message: ${perMessage(a)} → ${perMessage(b)}`, perMessage(b) > perMessage(a));

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
  const perDay = s.workloadResubmitsPerDay;
  if (perDay != null && (!Number.isInteger(perDay) || perDay < 0 || perDay > 500)) return "Resubmits per agent per day must be between 0 and 500.";
  const perMessage = s.workloadResubmitsPerMessage;
  if (perMessage != null && (!Number.isInteger(perMessage) || perMessage < 1 || perMessage > 5)) return "Unsuccessful resubmits per message must be between 1 and 5.";
  if (s.endpoints.changes === "listed" && s.endpoints.changeOn.length === 0) return "Pick at least one endpoint where agents may change messages.";
  const inRange = (v: number | null | undefined, max: number) => v == null || (Number.isInteger(v) && v >= 1 && v <= max);
  if (!inRange(s.limits.requestsPerWindow, d.requestLimit.permit) || !inRange(s.limits.mutationsPerWindow, d.mutationLimit.permit))
    return "A limit is outside its allowed range.";
  return undefined;
}

export type Persona = "reader" | "contributor" | "pii" | "workload" | "agentKey";
export const PERSONAS: Record<Persona, { label: string; delegated: boolean; contributor: boolean; pii: boolean }> = {
  reader: { label: "Reader", delegated: true, contributor: false, pii: false },
  contributor: { label: "Contributor", delegated: true, contributor: true, pii: false },
  pii: { label: "Contributor + PiiReader", delegated: true, contributor: true, pii: true },
  workload: { label: "Workload", delegated: false, contributor: false, pii: false },
  // Spec 052: an MCP agent key is a workload whose grant stands in for the app roles.
  agentKey: { label: "Agent key", delegated: false, contributor: false, pii: false },
};

export interface PreviewRow { name: string; allowed: boolean; why: string }

/** The Spec 035 Phase 3 capabilities: delegated callers only, never a workload. */
const RECOVERY_KEYS: CapabilityKey[] = ["settleHandoffs", "recoverDeferred", "editPayloads"];

/** What a caller of the given kind could use under `s` — a preview of the server's rules. */
export function previewAccess(s: McpSettings, d: McpDeployment, persona: Persona, classifyAvailable: boolean): PreviewRow[] {
  const p = PERSONAS[persona];
  const isKey = persona === "agentKey";
  const local = d.mode === "localDevelopment";
  const keyGate = !isKey ? undefined
    : d.mode !== "entra" || !d.agentKeysEnabled ? "deployment refuses keys"
    : !s.allowWorkloads ? "workloads off"
    : !s.allowAgentKeys ? "agent keys off" : undefined;
  const gate = d.mode === "disabled" ? "not set up" : !s.enabled ? "endpoint off" : keyGate ?? (!p.delegated && !s.allowWorkloads ? "workloads off" : undefined);
  const rows: PreviewRow[] = [{ name: `${READ_TOOL_COUNT} read tools`, allowed: !gate, why: gate ?? (local ? "Reader" : p.delegated ? "nimbus.observe" : isKey ? "agent key" : "Nimbus.Workload.Observe role") }];
  // The grant of an MCP key stands in for the workload app role (Spec 052 §6.6).
  const holder = (role: string) => (isKey ? "key grant + switch" : `${role} role`);
  const cap = (name: string, key: CapabilityKey): PreviewRow => {
    if (gate) return { name, allowed: false, why: gate };
    if (!s.capabilities[key]) return { name, allowed: false, why: "switched off here" };
    if (key === "classify" && !classifyAvailable) return { name, allowed: false, why: "Failure intelligence off" };
    // Spec 044 §6.6: a workload may record classifications with the Nimbus.Workload.Classify role, when allowed here.
    if (!p.delegated && key === "classify")
      return s.allowWorkloadClassification
        ? { name, allowed: true, why: `${holder("Nimbus.Workload.Classify")} + Contributor` }
        : { name, allowed: false, why: "workload recording off" };
    // Spec 044 Phase 2: a workload may resubmit with the Nimbus.Workload.Resubmit role, within its budget, when allowed here. Never skip.
    if (!p.delegated && key === "report")
      return s.allowWorkloadAnnotation
        ? { name, allowed: true, why: `${holder("Nimbus.Workload.Annotate")} + Contributor` }
        : { name, allowed: false, why: "workload reporting off" };
    if (!p.delegated && key === "resubmit")
      return s.allowWorkloadResubmit
        ? { name, allowed: true, why: `${holder("Nimbus.Workload.Resubmit")} + Contributor, within budget` }
        : { name, allowed: false, why: "unattended resubmits off" };
    // Spec 035 Phase 3: the recovery tools stay with the agent of a person.
    if (!p.delegated && RECOVERY_KEYS.includes(key)) return { name, allowed: false, why: "workloads never recover" };
    if (!p.delegated) return { name, allowed: false, why: key === "payloads" ? "no app role for payloads" : key === "skip" ? "workloads never skip" : "workloads can't change" };
    if (key === "payloads") return p.pii ? { name, allowed: true, why: local ? "PiiReader" : "scope + PiiReader" } : { name, allowed: false, why: "needs PiiReader" };
    if (!p.contributor) return { name, allowed: false, why: "needs Contributor" };
    // An edited payload replaces every value, the sensitive ones included.
    if (key === "editPayloads")
      return p.pii ? { name, allowed: true, why: local ? "Contributor + PiiReader" : "scope + Contributor + PiiReader" } : { name, allowed: false, why: "needs PiiReader" };
    return { name, allowed: true, why: local ? "Contributor" : "scope + Contributor" };
  };
  // The tools nimbus_prepare_action previews, by capability.
  const prepared: PreviewRow[] = [
    cap("nimbus_resubmit_message", "resubmit"), cap("nimbus_skip_message", "skip"),
    cap("nimbus_settle_handoff", "settleHandoffs"),
    cap("nimbus_reprocess_deferred", "recoverDeferred"), cap("nimbus_skip_deferred_tracking", "recoverDeferred"),
    cap("nimbus_resubmit_with_changes", "editPayloads"),
  ];
  rows.push(cap("payloads (includePayload)", "payloads"), cap("nimbus_set_message_reported", "report"),
    cap("nimbus_record_classification", "classify"), ...prepared);
  const prepare = prepared.some(r => r.allowed);
  rows.push({ name: "nimbus_prepare_action", allowed: prepare, why: gate ?? (prepare ? "a recovery action is allowed" : "needs a recovery action") });
  return rows;
}

/** How many tools `tools/list` returns under `s` for a client that may change messages. */
export function listedToolCount(s: McpSettings, d: McpDeployment): number {
  if (d.mode === "disabled" || !s.enabled) return 0;
  const c = s.capabilities;
  // Recovering deferred messages lists two tools. nimbus_prepare_action is listed while it can preview anything.
  const preparable = [c.resubmit, c.skip, c.settleHandoffs, c.recoverDeferred, c.editPayloads].some(Boolean);
  return READ_TOOL_COUNT + [c.report, c.classify, c.resubmit, c.skip, c.settleHandoffs, c.editPayloads].filter(Boolean).length
    + (c.recoverDeferred ? 2 : 0) + (preparable ? 1 : 0);
}
