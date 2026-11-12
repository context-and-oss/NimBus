import { useEffect, useMemo, useRef, useState } from "react";
import { Button } from "components/ui/button";
import { Input } from "components/ui/input";
import { Select } from "components/ui/select";
import { Toggle } from "components/ui/toggle";
import { Card, CardContent, CardHeader, CardTitle } from "components/ui/card";
import { Modal, ModalBody, ModalFooter, ModalHeader } from "components/ui/modal";
import { cn } from "lib/utils";
import {
  CAPABILITIES, PERSONAS, READ_TOOL_COUNT, TOTAL_TOOL_COUNT, WORKLOAD_RESUBMITS_PER_DAY, WORKLOAD_RESUBMITS_PER_MESSAGE, describeChanges, listedToolCount, mcpEndpointState, previewAccess, validate,
  type ApprovedClient, type CapabilityKey, type McpActivity, type McpChange, type McpProblem, type McpSettings, type McpState, type Persona,
} from "./mcp-access-model";
import { PanelFooter, PanelSection, useInPanel, usePanelDirty, usePanelReviewing } from "components/settings/panel-frame";

// Spec 037 — Settings → MCP access. Site Owner only. The policy narrows what the
// deployment, Entra scopes and NimBus roles allow, and applies to every instance
// within 30 seconds without a restart.

const FORM_ID = "mcp-access-settings";
const settingsUrl = "/api/admin/mcp/settings";
const turnOffUrl = "/api/admin/mcp/turn-off";
const activityUrl = "/api/admin/mcp/activity?hours=24";
const clone = (s: McpSettings): McpSettings => JSON.parse(JSON.stringify(s)) as McpSettings;
const when = (iso?: string | null) => (iso ? new Date(iso).toLocaleString() : "");

async function problemText(response: Response): Promise<{ text: string; problem?: McpProblem }> {
  const problem = await response.json().catch(() => undefined) as McpProblem | undefined;
  if (response.status === 401 || response.status === 403) return { text: "Only site Owners can change MCP access." };
  if (response.status === 409) return { text: "Another administrator changed these settings. Reload them before reviewing again.", problem };
  // The antiforgery filter answers 400 with no body when the security token is missing or expired.
  if (response.status === 400 && !problem) return { text: "Your security token expired. Reload the page and try again." };
  return { text: problem?.errors?.join(" ") || "The settings could not be saved. Reload and try again.", problem };
}

export default function McpAccessSettings() {
  const [state, setState] = useState<McpState>();
  const [draft, setDraft] = useState<McpSettings>();
  const [activity, setActivity] = useState<McpActivity>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string>();
  const [message, setMessage] = useState<string>();
  const [saving, setSaving] = useState(false);
  const [reviewing, setReviewing] = useState(false);
  const [confirmed, setConfirmed] = useState(false);
  const [serverChanges, setServerChanges] = useState<McpChange[]>();
  const [turnOffOpen, setTurnOffOpen] = useState(false);
  const [reload, setReload] = useState(0);
  const alive = useRef(true);

  function accept(next: McpState) {
    setState(next); setDraft(clone(next.saved)); setReviewing(false); setConfirmed(false); setServerChanges(undefined);
  }

  useEffect(() => {
    alive.current = true;
    const controller = new AbortController();
    setLoading(true);
    void (async () => {
      try {
        const response = await fetch(settingsUrl, { credentials: "same-origin", signal: controller.signal });
        if (!response.ok) throw new Error(response.status === 401 || response.status === 403
          ? "Only site Owners can configure MCP access." : "MCP access settings are unavailable. Check shared storage and try again.");
        const next = await response.json() as McpState;
        if (controller.signal.aborted) return;
        accept(next); setError(undefined);
        const recent = await fetch(activityUrl, { credentials: "same-origin", signal: controller.signal }).catch(() => undefined);
        if (recent?.ok && !controller.signal.aborted) setActivity(await recent.json() as McpActivity);
      } catch (cause) {
        if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : "MCP access settings are unavailable.");
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    })();
    return () => { alive.current = false; controller.abort(); };
  }, [reload]);

  const changes = useMemo(() => (state && draft ? describeChanges(state.saved, draft) : []), [state, draft]);
  const widens = (serverChanges ?? changes).some(c => c.widens);
  const enablesSkip = !!state && !!draft && draft.capabilities.skip && !state.saved.capabilities.skip;
  const inPanel = useInPanel();
  usePanelDirty(changes.length);
  usePanelReviewing(reviewing);

  if (!state || !draft) {
    return <div className="space-y-3"><p role={error ? "alert" : "status"}>{error ?? "Loading MCP access settings…"}</p>
      {error && <Button onClick={() => setReload(v => v + 1)}>Try again</Button>}</div>;
  }

  const deployment = state.deployment;
  const available = deployment.mode !== "disabled";
  const local = deployment.mode === "localDevelopment";
  const active = state.effective ?? state.saved;
  const serving = available && state.policyLoaded && active.enabled;

  const edit = (update: (s: McpSettings) => void) => {
    setDraft(current => { if (!current) return current; const next = clone(current); update(next); return next; });
    setReviewing(false); setConfirmed(false); setServerChanges(undefined); setMessage(undefined); setError(undefined);
  };

  async function write(url: string, method: "PUT" | "POST", body?: unknown) {
    if (!state) return;
    setSaving(true); setError(undefined);
    try {
      const response = await fetch(url, {
        method, credentials: "same-origin",
        headers: { "Content-Type": "application/json", "X-NimBus-CSRF": state.csrfToken },
        body: body === undefined ? undefined : JSON.stringify(body),
      });
      if (!response.ok) {
        const { text, problem } = await problemText(response);
        if (!alive.current) return;
        if (problem?.code === "ConfirmationRequired") { setServerChanges(problem.changes); setReviewing(true); setConfirmed(false); }
        setError(text);
        return;
      }
      const next = await response.json() as McpState;
      if (!alive.current) return;
      accept(next);
      setMessage(method === "POST"
        ? "MCP access is off. Agents get 503 [Disabled] on every instance within 30 seconds."
        : `Saved revision ${next.saved.revision?.slice(0, 8)}. This instance applies it now, the others within 30 seconds.`);
    } catch {
      if (alive.current) setError("The request could not be completed. Reload before retrying.");
    } finally {
      if (alive.current) setSaving(false);
    }
  }

  const save = () => write(settingsUrl, "PUT", { revision: state.saved.revision ?? null, settings: draft, confirmWidening: widens && confirmed });

  // In the Settings panel these sit in its sticky footer, which shows the unsaved count itself.
  const actions = (
    <div className="flex items-center gap-2">
      {!inPanel && changes.length > 0 && <span className="rounded-full bg-status-warning-50 px-2 font-mono text-xs text-status-warning-ink">{changes.length} unsaved change{changes.length === 1 ? "" : "s"}</span>}
      <Button type="button" variant="outline" disabled={saving || changes.length === 0} onClick={() => { setDraft(clone(state.saved)); setReviewing(false); setError(undefined); }}>Discard changes</Button>
      <Button type="submit" form={FORM_ID} disabled={saving || loading || changes.length === 0}>Review changes</Button>
    </div>
  );

  return (
    <form id={FORM_ID} className="w-full min-w-0 space-y-6" onSubmit={event => {
      event.preventDefault();
      const invalid = validate(draft, deployment);
      if (invalid) { setError(invalid); return; }
      setError(undefined); setReviewing(true);
    }}>
      <header className="flex flex-wrap items-start justify-between gap-4">
        <div className="max-w-3xl">
          {!inPanel && <h2 className="text-xl font-semibold">MCP access</h2>}
          <p className="text-muted-foreground">Let AI agents such as Claude Code check endpoint health, investigate failed messages and, for signed-in Contributors, recover them through <code>/mcp</code>. These settings can only narrow what Entra scopes and NimBus roles already allow.</p>
          <p className="mt-2 text-xs text-muted-foreground">
            {state.saved.revision
              ? <>Revision {state.saved.revision.slice(0, 8)} · saved by {state.saved.updatedBy ?? "unknown"} {when(state.saved.updatedAtUtc)} · applies to every instance within 30 s, no restart</>
              : available ? "No saved settings, so everything the deployment allows is on. Saving creates the first revision."
                : "No saved settings yet. Anything you save now applies once the endpoint is set up."}
          </p>
        </div>
        {serving && <Button type="button" variant="outline" colorScheme="red" disabled={saving} onClick={() => setTurnOffOpen(true)}>Turn off now</Button>}
      </header>

      <Banner state={state} />
      {error && <p role="alert" className="text-status-danger">{error}</p>}
      {message && <p role="status" className="text-status-success">{message}</p>}

      <PanelSection tab="overview">
        <Tiles state={state} activity={activity} />
      </PanelSection>

      <div className={inPanel ? "space-y-5" : "grid gap-6 xl:grid-cols-[minmax(0,1.55fr)_minmax(0,1fr)]"}>
        <fieldset disabled={saving || loading} className="min-w-0 space-y-5">
          <PanelSection tab="overview">
            <Card><CardHeader><CardTitle>01 · Activation</CardTitle></CardHeader><CardContent className="space-y-4 pt-4">
              <SettingRow label="Serve the MCP endpoint" description={available
                ? "Agents connect at the URL below and sign in as themselves. When off, every call gets 503 [Disabled] within 30 seconds on all instances, and the settings below are kept."
                : "Unavailable until the deployment sets NimBus__Mcp__Enabled and the MCP app registration."}>
                <Toggle aria-label="Serve the MCP endpoint" checked={draft.enabled} disabled={!available} onChange={v => edit(s => { s.enabled = v; })} />
              </SettingRow>
              <dl className="grid grid-cols-[minmax(0,10rem)_minmax(0,1fr)] gap-x-3 gap-y-1 text-sm">
                <dt className="text-muted-foreground">Endpoint</dt><dd className="font-mono text-xs break-all">{available ? deployment.endpointUrl : "—"}</dd>
                <dt className="text-muted-foreground">Resource metadata</dt><dd className="font-mono text-xs break-all">{deployment.resourceMetadataUrl ?? "—"}</dd>
                <dt className="text-muted-foreground">Server</dt><dd className="font-mono text-xs">nimbus-operator {deployment.serverVersion} · Streamable HTTP, stateless</dd>
              </dl>
              <details className="rounded-md border border-border bg-background">
                <summary className="cursor-pointer px-3 py-2 text-sm font-semibold">Sign-in <span className="ml-2 font-mono text-[10px] uppercase tracking-wider text-muted-foreground">managed by deployment</span></summary>
                <div className="space-y-3 px-3 pb-3 text-sm">
                  <dl className="grid grid-cols-[minmax(0,10rem)_minmax(0,1fr)] gap-x-3 gap-y-1">
                    {local ? <>
                      <dt className="text-muted-foreground">Mode</dt><dd>Local-dev bypass: every call runs as Local Developer, loopback only</dd>
                    </> : <>
                      <dt className="text-muted-foreground">Tenant</dt><dd className="font-mono text-xs break-all">{deployment.tenantId ?? "not set"}</dd>
                      <dt className="text-muted-foreground">MCP resource (API) app ID</dt><dd className="font-mono text-xs break-all">{deployment.clientId ?? "not set"}</dd>
                      <dt className="text-muted-foreground">Application ID URI</dt><dd className="font-mono text-xs break-all">{deployment.applicationIdUri ?? "—"}</dd>
                      <dt className="text-muted-foreground">Browser origins</dt><dd className="text-xs">{deployment.allowedOrigins.length ? deployment.allowedOrigins.join(", ") : "none (desktop and CLI clients only)"}</dd>
                    </>}
                  </dl>
                  <p className="border-l-2 border-border-strong pl-3 text-xs text-muted-foreground">These decide who can get a token for <code>/mcp</code>, so they stay in the WebApp's app settings (<code>NimBus__Mcp__*</code>): changing whom NimBus trusts needs Azure access, not only a NimBus Owner session.</p>
                </div>
              </details>
            </CardContent></Card>
          </PanelSection>

          <PanelSection tab="permissions">
            <Card><CardHeader><CardTitle>02 · What agents may do</CardTitle></CardHeader><CardContent className="space-y-4 pt-4">
              <p className="rounded-md border border-border bg-background px-3 py-2 text-xs text-muted-foreground">
                A tool runs only when <b className="text-primary-600">the switch on this page</b> AND the Entra scope AND the NimBus role on the endpoint AND the message state allow it.
              </p>
              <ul className="divide-y divide-border rounded-md border border-border">
                <li className="flex items-start justify-between gap-4 p-3">
                  <div><p className="text-sm font-semibold">Observe <Risk risk="low" text="read" /></p>
                    <p className="text-xs text-muted-foreground">Endpoint health, messages, history, sessions, metrics, failure evidence, stored classifications, operation outcomes and reports: {READ_TOOL_COUNT} read tools. Never stack traces.</p></div>
                  <span className="shrink-0 rounded-full border border-dashed border-border-strong px-2 py-0.5 font-mono text-[10px] uppercase text-muted-foreground">Always on</span>
                </li>
                {CAPABILITIES.map(cap => (
                  <li key={cap.key} className="space-y-2 p-3">
                    <div className="flex items-start justify-between gap-4">
                      <div className="min-w-0">
                        <p className="text-sm font-semibold">{cap.label} <Risk risk={cap.risk} text={cap.riskText} /></p>
                        <p className="text-xs text-muted-foreground">{cap.description}{cap.key === "classify" && !state.failureIntelligenceEnabled ? " Failure intelligence is off in this deployment." : ""}</p>
                        <p className="mt-1 flex flex-wrap gap-1 font-mono text-[11px]">
                          {cap.tools.map(t => <span key={t} className="rounded bg-muted px-1.5">{t}</span>)}
                          <span className="rounded bg-status-info-50 px-1.5 text-status-info-ink">{cap.scope}</span>
                          <span className="rounded bg-nimbus-purple-50 px-1.5 text-nimbus-purple">{cap.role}</span>
                        </p>
                      </div>
                      <Toggle aria-label={cap.label} checked={draft.capabilities[cap.key]} onChange={v => edit(s => { s.capabilities[cap.key as CapabilityKey] = v; })} />
                    </div>
                    {draft.capabilities[cap.key] && cap.note && <p className="rounded-md border border-status-warning/40 bg-status-warning/10 p-2 text-xs">{cap.note}</p>}
                  </li>
                ))}
              </ul>
              <SettingRow label="Unattended workloads (app-only tokens)" description="Callers with the Nimbus.Observe app role may use the read tools. Workloads never skip or read payloads, and change messages only as the switches below allow.">
                <Toggle aria-label="Allow workload tokens" checked={draft.allowWorkloads} disabled={local} onChange={v => edit(s => { s.allowWorkloads = v; })} />
              </SettingRow>
              <SettingRow label="Workloads may record classifications" description="Workloads with the Nimbus.Classify app role may call nimbus_record_classification. They still need Contributor on the endpoint in Access Control, and Classify failures must be on. Recording is advisory and changes no message. Off by default.">
                <Toggle aria-label="Allow workloads to record classifications" checked={Boolean(draft.allowWorkloadClassification)}
                  disabled={local || !draft.allowWorkloads} onChange={v => edit(s => { s.allowWorkloadClassification = v; })} />
              </SettingRow>
              <SettingRow label="Workloads may mark messages reported" description="Workloads with the Nimbus.Annotate app role may call nimbus_set_message_reported, for example with the ticket they filed. They still need Contributor on the endpoint and Mark reported must be on. Changes nothing on the bus. Off by default.">
                <Toggle aria-label="Allow workloads to mark messages reported" checked={Boolean(draft.allowWorkloadAnnotation)}
                  disabled={local || !draft.allowWorkloads} onChange={v => edit(s => { s.allowWorkloadAnnotation = v; })} />
              </SettingRow>
              <SettingRow label="Workloads may resubmit" description="Workloads with the Nimbus.Resubmit app role may resubmit failed messages without a person, through the operation journal. They still need Contributor on the endpoint and Resubmit messages must be on. Skip always stays with a person. Off by default.">
                <Toggle aria-label="Allow workloads to resubmit" checked={Boolean(draft.allowWorkloadResubmit)}
                  disabled={local || !draft.allowWorkloads} onChange={v => edit(s => { s.allowWorkloadResubmit = v; })} />
              </SettingRow>
              {draft.allowWorkloadResubmit && (
                <div className="space-y-3 rounded-md border p-3">
                  <WorkloadLimitRow label="Resubmits per agent per day" min={0} max={500} fallback={WORKLOAD_RESUBMITS_PER_DAY}
                    description="Each workload client application, over a rolling 24 hours. The per-minute change limit still applies."
                    value={draft.workloadResubmitsPerDay} onChange={v => edit(s => { s.workloadResubmitsPerDay = v; })} />
                  <WorkloadLimitRow label="Unsuccessful resubmits per message" min={1} max={5} fallback={WORKLOAD_RESUBMITS_PER_MESSAGE}
                    description="Across all agents. After this many, NimBus refuses another unattended resubmit and the message stays with a person."
                    value={draft.workloadResubmitsPerMessage} onChange={v => edit(s => { s.workloadResubmitsPerMessage = v; })} />
                </div>
              )}
              <p className="text-xs text-muted-foreground">Switched-off tools are removed from <code>tools/list</code> and from <code>permittedActions</code>; a direct call gets <code>[PermissionDenied]</code>.</p>
            </CardContent></Card>
          </PanelSection>

          <PanelSection tab="who">
            <WhoAndWhere draft={draft} state={state} activity={activity} local={local} edit={edit} />
          </PanelSection>

          <PanelSection tab="limits">
            <Card><CardHeader><CardTitle>04 · Limits</CardTitle></CardHeader><CardContent className="space-y-4 pt-4">
              {!deployment.rateLimitsEnabled && <p className="text-sm text-muted-foreground">Rate limiting is off in this deployment, so there is nothing to lower.</p>}
              <LimitRow label="Tool calls" description={`Every MCP request, per user and client. Deployment maximum ${deployment.requestLimit.permit} per ${deployment.requestLimit.windowSeconds} s.`}
                value={draft.limits.requestsPerWindow} max={deployment.requestLimit.permit} disabled={!deployment.rateLimitsEnabled}
                onChange={v => edit(s => { s.limits.requestsPerWindow = v; })} />
              <LimitRow label="Message changes" description={`Resubmit, skip, report and classify. Deployment maximum ${deployment.mutationLimit.permit} per ${deployment.mutationLimit.windowSeconds} s.`}
                value={draft.limits.mutationsPerWindow} max={deployment.mutationLimit.permit} disabled={!deployment.rateLimitsEnabled}
                onChange={v => edit(s => { s.limits.mutationsPerWindow = v; })} />
              <p className="text-xs text-muted-foreground">Leave a field empty to use the deployment's value. Raise the ceiling in <code>RateLimiting:Mcp</code> and <code>RateLimiting:McpMutations</code>.</p>
            </CardContent></Card>
          </PanelSection>
        </fieldset>

        <aside className="min-w-0 space-y-5">
          <PanelSection tab="permissions">
            <PreviewCard draft={draft} state={state} dirty={changes.length > 0} />
          </PanelSection>
          <PanelSection tab="connect">
            <ConnectCard draft={draft} state={state} />
          </PanelSection>
          <PanelSection tab="activity">
            <ActivityCard activity={activity} />
          </PanelSection>
        </aside>
      </div>

      {reviewing && (
        <section aria-label="Review changes" className="space-y-4 rounded-md border border-primary/40 p-5">
          <h3 className="font-semibold">Review changes</h3>
          <ul className="space-y-1">
            {(serverChanges ?? changes).map(c => (
              <li key={c.text} className="flex items-center gap-2 text-sm">
                <span className={cn("w-20 shrink-0 rounded px-1.5 text-center font-mono text-[10px] font-bold uppercase",
                  c.widens ? "bg-status-warning-50 text-status-warning-ink" : "bg-status-success-50 text-status-success-ink")}>{c.widens ? "widens" : "narrows"}</span>
                {c.text}
              </li>
            ))}
          </ul>
          {widens && (
            <label className="flex gap-2 rounded-md bg-status-warning-50 p-3 text-sm text-status-warning-ink">
              <input type="checkbox" checked={confirmed} onChange={e => setConfirmed(e.target.checked)} />
              {enablesSkip
                ? "I confirm that agents may skip messages for signed-in Contributors, and that a skip can release later messages in a session and can't be undone."
                : "I confirm that agents may do more after this change. Entra scopes and NimBus roles still apply."}
            </label>
          )}
          <div className="flex gap-2">
            <Button type="button" variant="outline" disabled={saving} onClick={() => setReviewing(false)}>Back</Button>
            <Button type="button" disabled={saving || (widens && !confirmed)} onClick={() => void save()}>{saving ? "Saving…" : "Save settings"}</Button>
          </div>
        </section>
      )}

      {inPanel ? <PanelFooter>{actions}</PanelFooter> : (
        <footer className="flex flex-wrap items-center justify-between gap-3 border-t pt-4">
          <p className="text-sm text-muted-foreground">Site Owner only · every change is audited · applies to all instances within 30 s, no restart</p>
          {actions}
        </footer>
      )}

      <Modal isOpen={turnOffOpen} onClose={() => setTurnOffOpen(false)} size="md">
        <ModalHeader onClose={() => setTurnOffOpen(false)}>Turn off MCP access now?</ModalHeader>
        <ModalBody>
          <div className="space-y-3 text-sm text-muted-foreground">
            <p>Every call to <code>/mcp</code> gets <code>503 [Disabled]</code>. This instance stops at once and the others within 30 seconds. A command that has already written its audit row still completes.</p>
            <p>This skips the review step and ignores unsaved edits on this page. It saves a new revision, is audited, and keeps your other settings.</p>
          </div>
        </ModalBody>
        <ModalFooter>
          <Button type="button" variant="outline" onClick={() => setTurnOffOpen(false)}>Cancel</Button>
          <Button type="button" colorScheme="red" disabled={saving} onClick={() => { setTurnOffOpen(false); void write(turnOffUrl, "POST"); }}>Turn off MCP access</Button>
        </ModalFooter>
      </Modal>
    </form>
  );
}

function Banner({ state }: { state: McpState }) {
  const d = state.deployment;
  const active = state.effective ?? state.saved;
  const box = "rounded-md border p-3 text-sm";
  if (d.mode === "disabled") {
    return (
      <section className={cn(box, "border-status-info/30 bg-status-info-50 text-status-info-ink space-y-2")}>
        <p className="font-semibold">MCP isn't set up on this deployment</p>
        <p>The endpoint needs its own Entra app registration, which someone with Azure access creates once. After that, everything on this tab is managed here.</p>
        <ol className="list-decimal space-y-1 pl-5">
          <li>Register the MCP resource in Entra: expose the <code>nimbus.*</code> scopes and add <code>{d.endpointUrl}</code> as a second Application ID URI (see <code>docs/mcp-server.md</code>).</li>
          <li>Set <code>NimBus__Mcp__Enabled=true</code>, <code>NimBus__Mcp__Entra__TenantId</code> and <code>NimBus__Mcp__Entra__ClientId</code> on the WebApp, then restart it.</li>
          <li>Come back here. You can prepare the policy now; it applies once the endpoint exists.</li>
        </ol>
      </section>
    );
  }
  if (!state.policyLoaded) {
    return <p role="alert" className={cn(box, "border-status-danger/40 bg-status-danger-50 text-status-danger-ink")}>This instance could not load the MCP access policy, so agents get 503 [Unavailable] until shared storage is reachable.</p>;
  }
  if (d.mode === "localDevelopment") {
    return <p className={cn(box, "border-status-info/30 bg-status-info-50 text-status-info-ink")}><b>Local development mode.</b> Every call runs as Local Developer with no sign-in, from loopback addresses only. Entra scopes aren't checked; the switches, NimBus roles and message state still are. People and client restrictions don't apply.</p>;
  }
  if (!active.enabled) {
    return <p className={cn(box, "border-status-warning/40 bg-status-warning-50 text-status-warning-ink")}><b>MCP access is off.</b> Agents get 503 [Disabled]. Turned off by {active.updatedBy ?? "an administrator"} {when(active.updatedAtUtc)}. The other settings were kept; turn the endpoint on below and save to restore access.</p>;
  }
  return null;
}

function Tiles({ state, activity }: { state: McpState; activity?: McpActivity }) {
  const d = state.deployment;
  const endpoint = {
    notSetUp: { value: "Not set up", dot: "bg-ink-3", sub: "/mcp is not mapped" },
    unavailable: { value: "Unavailable", dot: "bg-status-danger", sub: "policy not loaded" },
    serving: { value: "Serving", dot: "bg-status-success", sub: `nimbus-operator ${d.serverVersion}` },
    off: { value: "Off", dot: "bg-status-warning", sub: "agents get 503 [Disabled]" },
  }[mcpEndpointState(state)];
  const signIn = d.mode === "entra" ? { value: "Entra ID", sub: d.tenantId ?? "" }
    : d.mode === "localDevelopment" ? { value: "Local developer", sub: "no sign-in · loopback only" } : { value: "—", sub: "no MCP app registration" };
  const byName = (list?: { name: string; count: number }[]) => (list ?? []).map(x => `${x.count} ${x.name}`).join(" · ");
  const tiles = [
    { key: "Endpoint", value: <><span className={cn("inline-block h-2 w-2 rounded-full", endpoint.dot)} />{endpoint.value}</>, sub: endpoint.sub },
    { key: "Sign-in", value: signIn.value, sub: signIn.sub },
    { key: "Agent actions · 24 h", value: activity ? String(activity.actions) : "—", sub: byName(activity?.actionsByType) },
    { key: "Refused · 24 h", value: activity ? String(activity.refused) : "—", sub: byName(activity?.refusedByReason) },
  ];
  return (
    <section aria-label="Active now">
      <p className="mb-2 font-mono text-[10px] uppercase tracking-widest text-ink-3">Active now</p>
      <div className="grid gap-3 sm:grid-cols-2 xl:grid-cols-4">
        {tiles.map(t => (
          <div key={t.key} className="min-w-0 rounded-lg border border-border bg-card px-4 py-3 shadow-xs">
            <p className="font-mono text-[10px] uppercase tracking-widest text-ink-3">{t.key}</p>
            <p className="flex items-center gap-2 text-xl font-bold">{t.value}</p>
            <p className="truncate text-xs text-muted-foreground" title={t.sub}>{t.sub || " "}</p>
          </div>
        ))}
      </div>
    </section>
  );
}

function WhoAndWhere({ draft, state, activity, local, edit }: {
  draft: McpSettings; state: McpState; activity?: McpActivity; local: boolean; edit: (u: (s: McpSettings) => void) => void;
}) {
  const [person, setPerson] = useState("");
  const [client, setClient] = useState<ApprovedClient>({ clientId: "", name: "", mayChange: false });
  const catalog = state.deployment.endpoints;
  const known = new Set(draft.clients.approved.map(c => c.clientId.toLowerCase()));
  const suggestions = (activity?.refusedClients ?? []).filter(c => !known.has(c.clientId.toLowerCase()));

  return (
    <Card><CardHeader><CardTitle>03 · Who and where</CardTitle></CardHeader><CardContent className="space-y-5 pt-4">
      <fieldset disabled={local} className="space-y-2">
        <legend className="text-sm font-semibold">People</legend>
        <p className="text-xs text-muted-foreground">{local ? "Not used in local development." : "Who may connect. NimBus roles still decide what each person sees and changes."}</p>
        <Select aria-label="People" disabled={local} value={draft.people.mode} onChange={e => edit(s => { s.people.mode = e.target.value as McpSettings["people"]["mode"]; })}>
          <option value="all">Everyone with a NimBus role</option>
          <option value="listed">Only these users and groups</option>
        </Select>
        {draft.people.mode === "listed" && <>
          <Chips values={draft.people.principals.map(p => p.principal)} onRemove={v => edit(s => { s.people.principals = s.people.principals.filter(p => p.principal !== v); })} />
          <div className="flex gap-2">
            <Input aria-label="Email or object id" placeholder="pilot@contoso.com or an object id" value={person} onChange={e => setPerson(e.target.value)} />
            <Button type="button" variant="outline" disabled={!person.trim()} onClick={() => { const v = person.trim(); edit(s => { s.people.principals.push({ principal: v }); }); setPerson(""); }}>Add</Button>
          </div>
        </>}
      </fieldset>

      <div className="border-t border-dashed border-border pt-4"><fieldset disabled={local} className="space-y-2">
        <legend className="text-sm font-semibold">Client applications</legend>
        <p className="text-xs text-muted-foreground">Matched on the token's <code>azp</code> claim. Entra pre-authorization still applies.</p>
        <Select aria-label="Client applications" disabled={local} value={draft.clients.mode} onChange={e => edit(s => { s.clients.mode = e.target.value as McpSettings["clients"]["mode"]; })}>
          <option value="any">Any client pre-authorized in Entra</option>
          <option value="approved">Only approved clients</option>
        </Select>
        {draft.clients.mode === "approved" && <>
          <ul className="divide-y divide-border rounded-md border border-border text-sm">
            {draft.clients.approved.map(c => (
              <li key={c.clientId} className="flex items-center justify-between gap-3 p-2">
                <div className="min-w-0"><p>{c.name}</p><p className="font-mono text-[11px] text-muted-foreground">{c.clientId}</p></div>
                <div className="flex items-center gap-2">
                  <Toggle aria-label={`${c.name} may change messages`} checked={c.mayChange} onChange={v => edit(s => { const x = s.clients.approved.find(a => a.clientId === c.clientId); if (x) x.mayChange = v; })} />
                  <Button type="button" variant="ghost" size="sm" aria-label={`Remove ${c.name}`} onClick={() => edit(s => { s.clients.approved = s.clients.approved.filter(a => a.clientId !== c.clientId); })}>×</Button>
                </div>
              </li>
            ))}
            {draft.clients.approved.length === 0 && <li className="p-2 text-status-danger">No approved clients. Every agent will be refused.</li>}
          </ul>
          <div className="grid gap-2 sm:grid-cols-[minmax(0,1fr)_minmax(0,1fr)_auto_auto] sm:items-center">
            <Input aria-label="Client name" placeholder="Claude Code — operations" value={client.name} onChange={e => setClient({ ...client, name: e.target.value })} />
            <Input aria-label="Client ID" placeholder="Application (client) ID" spellCheck={false} value={client.clientId} onChange={e => setClient({ ...client, clientId: e.target.value })} />
            <label className="flex items-center gap-1 text-xs"><input type="checkbox" checked={client.mayChange} onChange={e => setClient({ ...client, mayChange: e.target.checked })} />May change</label>
            <Button type="button" variant="outline" disabled={!client.name.trim() || !client.clientId.trim()}
              onClick={() => { const c = { ...client, name: client.name.trim(), clientId: client.clientId.trim() }; edit(s => { s.clients.approved.push(c); }); setClient({ clientId: "", name: "", mayChange: false }); }}>Approve</Button>
          </div>
          {suggestions.map(s => (
            <p key={s.clientId} className="flex flex-wrap items-center gap-2 rounded-md border border-dashed border-border-strong p-2 text-xs text-muted-foreground">
              Refused recently: <b className="font-mono text-foreground">{s.clientId}</b> · {s.calls} from {s.lastAuditor ?? "unknown"}
              <Button type="button" size="xs" variant="outline" onClick={() => setClient({ clientId: s.clientId, name: "", mayChange: false })}>Approve…</Button>
            </p>
          ))}
        </>}
      </fieldset></div>

      <div className="space-y-2 border-t border-dashed border-border pt-4">
        <p className="text-sm font-semibold">Endpoints</p>
        <p className="text-xs text-muted-foreground">A hidden endpoint behaves as if it did not exist (<code>[EndpointNotFound]</code>).</p>
        <Select aria-label="Agents can see" value={draft.endpoints.visibility} onChange={e => edit(s => { s.endpoints.visibility = e.target.value as McpSettings["endpoints"]["visibility"]; })}>
          <option value="all">Agents see every endpoint the caller can read</option>
          <option value="allExcept">Agents see all except the endpoints below</option>
        </Select>
        {draft.endpoints.visibility === "allExcept" && <EndpointPicker label="Hide an endpoint" catalog={catalog} values={draft.endpoints.hidden}
          onChange={v => edit(s => { s.endpoints.hidden = v; })} />}
        <Select aria-label="Agents can change messages on" value={draft.endpoints.changes} onChange={e => edit(s => { s.endpoints.changes = e.target.value as McpSettings["endpoints"]["changes"]; })}>
          <option value="allVisible">Agents may change messages on every endpoint they can see</option>
          <option value="listed">Agents may change messages only on the endpoints below</option>
        </Select>
        {draft.endpoints.changes === "listed" && <EndpointPicker label="Allow changes on an endpoint"
          catalog={catalog.filter(e => draft.endpoints.visibility === "all" || !draft.endpoints.hidden.includes(e))} values={draft.endpoints.changeOn}
          onChange={v => edit(s => { s.endpoints.changeOn = v; })} />}
      </div>
    </CardContent></Card>
  );
}

function EndpointPicker({ label, catalog, values, onChange }: { label: string; catalog: string[]; values: string[]; onChange: (v: string[]) => void }) {
  const left = catalog.filter(e => !values.includes(e));
  return <>
    <Chips values={values} onRemove={v => onChange(values.filter(x => x !== v))} />
    <Select aria-label={label} value="" onChange={e => { if (e.target.value) onChange([...values, e.target.value]); }}>
      <option value="">{label}…</option>
      {left.map(e => <option key={e} value={e}>{e}</option>)}
    </Select>
  </>;
}

function Chips({ values, onRemove }: { values: string[]; onRemove: (v: string) => void }) {
  if (values.length === 0) return <p className="text-xs text-status-danger">Nothing selected yet.</p>;
  return (
    <ul className="flex flex-wrap gap-1.5">
      {values.map(v => (
        <li key={v} className="flex items-center gap-1 rounded-full border border-border-strong bg-card py-0.5 pl-3 pr-1 text-xs">
          {v}
          <button type="button" aria-label={`Remove ${v}`} className="h-5 w-5 rounded-full bg-muted text-muted-foreground hover:bg-status-danger-50" onClick={() => onRemove(v)}>×</button>
        </li>
      ))}
    </ul>
  );
}

function LimitRow({ label, description, value, max, disabled, onChange }: {
  label: string; description: string; value?: number | null; max: number; disabled: boolean; onChange: (v: number | null) => void;
}) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-3">
      <div><p className="text-sm font-semibold">{label}</p><p className="text-xs text-muted-foreground">{description}</p></div>
      <Input aria-label={label} type="number" min={1} max={max} placeholder={String(max)} className="w-28" disabled={disabled}
        value={value ?? ""} onChange={e => onChange(e.target.value === "" ? null : Number(e.target.value))} />
    </div>
  );
}

function WorkloadLimitRow({ label, description, value, min, max, fallback, onChange }: {
  label: string; description: string; value?: number | null; min: number; max: number; fallback: number; onChange: (v: number | null) => void;
}) {
  return (
    <div className="flex flex-wrap items-center justify-between gap-3">
      <div><p className="text-sm font-semibold">{label}</p><p className="text-xs text-muted-foreground">{description}</p></div>
      <Input aria-label={label} type="number" min={min} max={max} placeholder={String(fallback)} className="w-28"
        value={value ?? ""} onChange={e => onChange(e.target.value === "" ? null : Number(e.target.value))} />
    </div>
  );
}

function SettingRow({ label, description, children }: { label: string; description: string; children: React.ReactNode }) {
  return <div className="flex items-start justify-between gap-4"><div><p className="text-sm font-semibold">{label}</p><p className="text-xs text-muted-foreground">{description}</p></div>{children}</div>;
}

function Risk({ risk, text }: { risk: "data" | "low" | "medium" | "high"; text: string }) {
  const tone = { data: "bg-nimbus-purple-50 text-nimbus-purple", low: "bg-status-success-50 text-status-success-ink",
    medium: "bg-status-warning-50 text-status-warning-ink", high: "bg-status-danger-50 text-status-danger-ink" }[risk];
  return <span className={cn("ml-1 rounded-full px-1.5 py-px align-middle font-mono text-[9.5px] font-bold uppercase tracking-wider", tone)}>{text}</span>;
}

function PreviewCard({ draft, state, dirty }: { draft: McpSettings; state: McpState; dirty: boolean }) {
  const [persona, setPersona] = useState<Persona>("contributor");
  const personas = (Object.keys(PERSONAS) as Persona[]).filter(p => !(state.deployment.mode === "localDevelopment" && p === "workload"));
  const rows = previewAccess(draft, state.deployment, persona, state.failureIntelligenceEnabled);
  return (
    <Card><CardHeader><CardTitle>Effective access</CardTitle><p className="text-xs text-muted-foreground">{dirty ? "Previewing your unsaved edits" : "Saved settings"} · the server is authoritative</p></CardHeader>
      <CardContent className="space-y-3 pt-4">
        <div role="group" aria-label="Preview as" className="flex flex-wrap gap-1 rounded-md border border-border bg-background p-1">
          {personas.map(p => (
            <button key={p} type="button" aria-pressed={persona === p} onClick={() => setPersona(p)}
              className={cn("rounded px-2 py-1 text-xs font-semibold", persona === p ? "bg-card shadow-xs" : "text-muted-foreground")}>{PERSONAS[p].label}</button>
          ))}
        </div>
        <ul className="divide-y divide-border text-xs">
          {rows.map(r => (
            <li key={r.name} className="flex items-center gap-2 py-1.5">
              <span aria-label={r.allowed ? "allowed" : "blocked"} className={r.allowed ? "font-bold text-status-success" : "font-bold text-status-danger"}>{r.allowed ? "✓" : "✕"}</span>
              <span className="flex-1 break-all font-mono">{r.name}</span>
              <span className="text-muted-foreground">{r.why}</span>
            </li>
          ))}
        </ul>
        <p className="text-xs text-muted-foreground"><b className="text-foreground">tools/list</b> returns {listedToolCount(draft, state.deployment)} of {TOTAL_TOOL_COUNT} tools.</p>
      </CardContent></Card>
  );
}

function ConnectCard({ draft, state }: { draft: McpSettings; state: McpState }) {
  const d = state.deployment;
  const clients = draft.clients.mode === "approved" ? draft.clients.approved : [];
  const [clientId, setClientId] = useState<string>();
  const selected = clientId ?? clients[0]?.clientId ?? "<client-id>";
  if (d.mode === "disabled") {
    return <Card><CardHeader><CardTitle>Connect an agent</CardTitle></CardHeader><CardContent className="pt-4 text-sm text-muted-foreground">Connection details appear here once the endpoint is set up.</CardContent></Card>;
  }
  const command = d.mode === "localDevelopment"
    ? `claude mcp add --transport http nimbus ${d.endpointUrl}`
    : `claude mcp add --transport http \\\n  --client-id ${selected} --callback-port <port> \\\n  nimbus ${d.endpointUrl}`;
  const resourceAsClient = !!d.clientId && selected.toLowerCase() === d.clientId.toLowerCase();
  return (
    <Card><CardHeader><CardTitle>Connect an agent</CardTitle></CardHeader><CardContent className="space-y-3 pt-4">
      {clients.length > 1 && (
        <Select aria-label="Client registration" value={selected} onChange={e => setClientId(e.target.value)}>
          {clients.map(c => <option key={c.clientId} value={c.clientId}>{c.name}</option>)}
        </Select>
      )}
      <pre className="overflow-auto whitespace-pre-wrap break-all rounded-md bg-zinc-950 p-3 font-mono text-xs leading-relaxed text-amber-100">{command}</pre>
      <Button type="button" size="sm" variant="outline" onClick={() => void navigator.clipboard?.writeText(command.replace(/\\\n\s*/g, ""))}>Copy command</Button>
      {d.mode === "entra" && <p className="text-xs text-muted-foreground">
        <code>--client-id</code> is an MCP client app registration, not the MCP resource (API) app ID under Sign-in: a public client
        with the redirect URI <code>http://localhost:&lt;port&gt;/callback</code>, the same port as <code>--callback-port</code>, pre-authorized
        on the MCP resource for the <code>nimbus.*</code> scopes it needs.
      </p>}
      {resourceAsClient && <p className="text-xs text-status-danger">This client ID is the MCP resource, which has no redirect URI: Entra refuses its sign-in with AADSTS500113. Approve a separate client registration.</p>}
      <p className="text-xs text-muted-foreground">{d.mode === "localDevelopment"
        ? "No sign-in. Works only on this machine while the Aspire AppHost runs."
        : "Then run /mcp in a claude session and sign in as yourself. Agents act as the person who signs in; there are no shared keys."}</p>
    </CardContent></Card>
  );
}

function ActivityCard({ activity }: { activity?: McpActivity }) {
  const badge = { action: "bg-status-success-50 text-status-success-ink", refused: "bg-status-danger-50 text-status-danger-ink", settings: "bg-status-warning-50 text-status-warning-ink" };
  return (
    <Card><CardHeader><CardTitle>Recent agent activity</CardTitle><p className="text-xs text-muted-foreground">From the audit log: actions, refusals and settings changes. Read-only calls are not audited.</p></CardHeader>
      <CardContent className="pt-4">
        {!activity ? <p className="text-sm text-muted-foreground">Activity is unavailable.</p>
          : activity.items.length === 0 ? <p className="text-sm text-muted-foreground">No agent activity in the last {activity.hours} hours.</p>
            : <ul className="divide-y divide-border text-xs">
              {activity.items.slice(0, 8).map((i, n) => (
                <li key={`${i.atUtc}-${n}`} className="py-2">
                  <p className="font-semibold"><span className={cn("mr-1.5 rounded px-1 font-mono text-[9.5px] uppercase", badge[i.kind])}>{i.kind}</span>
                    {[i.type, i.endpointId, i.detail].filter(Boolean).join(" · ")}</p>
                  <p className="text-muted-foreground">{when(i.atUtc)} · {[i.auditor, i.clientId, i.reason].filter(Boolean).join(" · ")}</p>
                </li>
              ))}
            </ul>}
        {activity?.capped && <p className="mt-2 text-xs text-status-warning-ink">Busy period: some rows were not read, so counts may be low.</p>}
      </CardContent></Card>
  );
}
