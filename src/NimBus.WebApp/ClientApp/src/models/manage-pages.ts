// URL segments and names for the Manage pages (Spec 038 §5.2). Kept apart from
// the lazily loaded pages so the topbar can name a route without bundling them.

/** Topology views in tab order; each has its own URL: /Topology/:view. */
export const TOPOLOGY_VIEWS = [
  { id: "subscriptions", label: "Subscriptions" },
  { id: "drift", label: "Catalog drift" },
  { id: "storage", label: "Storage" },
] as const;

/**
 * The Topology views this deployment has. Storage manages Cosmos DB containers,
 * so it exists only on that provider; while the provider is unknown it is left out.
 */
export const availableTopologyViews = (storageProvider: string | undefined) =>
  TOPOLOGY_VIEWS.filter((v) => v.id !== "storage" || storageProvider === "Cosmos DB");

export interface SettingsTab {
  /** URL value: /Settings/:feature?tab=:id. */
  id: string;
  label: string;
  /** Words for the Settings filter, so a search finds the tab holding a setting. */
  keywords: string;
}

export interface SettingsFeature {
  /** URL segment: /Settings/:id. */
  id: string;
  group: string;
  name: string;
  description: string;
  /** Panel tabs in order; the first is the default. */
  tabs: readonly SettingsTab[];
  /** When a saved change takes effect, shown in the panel footer. */
  applies: string;
}

/** Opt-in features in display order. A new feature adds an entry here, not a tab. */
export const SETTINGS_FEATURES: readonly SettingsFeature[] = [
  {
    id: "mcp",
    group: "AI & agents",
    name: "MCP access",
    description:
      "Let AI agents check endpoint health, investigate failed messages and recover them through /mcp.",
    applies: "Applies to every instance within 30 s",
    tabs: [
      { id: "overview", label: "Overview", keywords: "serve endpoint url sign-in entra tenant metadata turn off" },
      { id: "permissions", label: "Permissions", keywords: "tools payload resubmit skip classify report workload scope role effective access" },
      { id: "who", label: "Who & where", keywords: "people groups clients client applications endpoints hide" },
      { id: "limits", label: "Limits", keywords: "rate limit tool calls message changes" },
      { id: "connect", label: "Connect", keywords: "claude code connect command url" },
      { id: "activity", label: "Activity", keywords: "audit actions refused recent" },
    ],
  },
  {
    id: "failure-intelligence",
    group: "AI & agents",
    name: "Failure intelligence",
    description:
      "On-demand advisory analysis of failed messages. You control the provider, evidence and endpoint scope.",
    applies: "Restart every instance to apply",
    tabs: [
      { id: "provider", label: "Activation & provider", keywords: "enable provider model api key typesafe" },
      { id: "evidence", label: "Evidence", keywords: "payload redaction redacted keys history evidence" },
      { id: "scope", label: "Scope & guardrails", keywords: "endpoints timeout scope guardrails limits" },
      { id: "classification", label: "How failures are classified", keywords: "thresholds classify confidence guidance" },
    ],
  },
  {
    id: "agent-keys",
    group: "AI & agents",
    name: "Agent keys",
    description:
      "Issue and revoke the keys agents built on NimBus.Agents use to call the agent API.",
    applies: "Applies to every instance within 30 s",
    tabs: [
      { id: "keys", label: "Keys", keywords: "agent api key issue revoke remove expiry last used nimbus.agents" },
    ],
  },
  {
    id: "heartbeat",
    group: "Monitoring",
    name: "Heartbeat probing",
    description: "Which endpoints are probed, how often, and how long to wait for a reply.",
    applies: "Applies on the next probe",
    tabs: [
      { id: "probing", label: "Probing", keywords: "schedule interval timeout send now enabled" },
      { id: "endpoints", label: "Endpoints", keywords: "endpoints include exclude probe" },
    ],
  },
  {
    id: "simulation",
    group: "Testing",
    name: "Simulation",
    description:
      "Drive synthetic traffic through the real platform from this WebApp. Never available in production.",
    applies: "Applies on this instance and resets on restart",
    tabs: [
      { id: "mode", label: "Simulate mode", keywords: "enable auto-stop rate ceiling session prefix" },
      { id: "endpoints", label: "Consuming endpoints", keywords: "ownership owned consuming endpoints" },
      { id: "policy", label: "Environment policy", keywords: "production environment allowed blocked" },
    ],
  },
  {
    id: "audit",
    group: "Compliance",
    name: "Audit logging",
    description: "Which operator actions are written to the audit log and Application Insights.",
    applies: "Applies immediately",
    tabs: [{ id: "types", label: "Action types", keywords: "audit record action types categories" }],
  },
];

/** Blast-radius groups for the Operations list, safest first (Spec 038 §6.3). */
export const OPERATION_GROUPS = [
  { id: "recovery", label: "Recovery", caption: "Safe · reversible", tone: "success" },
  { id: "cleanup", label: "Cleanup", caption: "Changes state · not replayed", tone: "warning" },
  { id: "movement", label: "Data movement", caption: "Cross-environment", tone: "info" },
  { id: "danger", label: "Irreversible", caption: "Audit-logged", tone: "danger" },
] as const;

export type OperationGroupId = (typeof OPERATION_GROUPS)[number]["id"];

export interface OperationInfo {
  /** URL segment: /Operations/:id. */
  id: string;
  group: OperationGroupId;
  label: string;
  description: string;
  /** Whether the form starts from an endpoint, so `?endpoint=` can pre-fill it. */
  takesEndpoint: boolean;
}

/** Bulk operations in list order; the first is the page's default. */
export const OPERATIONS: readonly OperationInfo[] = [
  { id: "resubmit", group: "recovery", label: "Bulk resubmit failed", takesEndpoint: true,
    description: "Resubmit an endpoint's failed messages. Idempotent handlers absorb repeats." },
  { id: "skip", group: "recovery", label: "Skip messages", takesEndpoint: true,
    description: "Mark matching events as Skipped so their sessions continue." },
  { id: "session", group: "recovery", label: "Session purge", takesEndpoint: true,
    description: "Unblock one stuck session by clearing its deferred and blocked messages." },
  { id: "stale", group: "recovery", label: "Reconcile stale pending", takesEndpoint: true,
    description: "Repair rows a redelivered copy left Pending after the original completed." },
  { id: "dlq", group: "cleanup", label: "Delete dead-lettered", takesEndpoint: true,
    description: "Empty an endpoint's dead-letter queue and delete its dead-lettered events, without reprocessing them." },
  { id: "status", group: "cleanup", label: "Delete by status", takesEndpoint: true,
    description: "Delete an endpoint's stored events in the chosen states." },
  { id: "to", group: "cleanup", label: "Delete by To field", takesEndpoint: false,
    description: "Delete messages addressed to one value, across endpoints." },
  { id: "single", group: "cleanup", label: "Delete single event", takesEndpoint: true,
    description: "Delete one event by its ID." },
  { id: "purge", group: "movement", label: "Purge subscription", takesEndpoint: true,
    description: "Drain an endpoint's Service Bus subscription. The messages are gone, not dead-lettered." },
  { id: "copy", group: "movement", label: "Copy endpoint data", takesEndpoint: true,
    description: "Copy an endpoint's stored events to another NimBus instance." },
  { id: "all", group: "danger", label: "Delete all events", takesEndpoint: true,
    description: "Wipe an endpoint: recreate its subscription and Deferred subscription empty, and delete every stored event and message-history record." },
];
