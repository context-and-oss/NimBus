import { useEffect, useRef, useState } from "react";
import { Button } from "components/ui/button";
import { Input } from "components/ui/input";
import { Select } from "components/ui/select";
import { Toggle } from "components/ui/toggle";
import { Card, CardContent, CardHeader, CardTitle } from "components/ui/card";
import FailureClassificationExplainer from "./failure-classification-explainer";
import { PanelFooter, PanelSection, useInPanel, usePanelDirty, usePanelReviewing } from "components/settings/panel-frame";

type Settings = {
  enabled: boolean; includeEventPayload: boolean; includeRecentFailureHistory: boolean;
  /** Spec 044 Phase 3: agents may read the scrubbed stack trace. Optional: older servers omit it. */
  includeStackTrace?: boolean;
  maximumHistoryItems: number; additionalRedactedKeys: string[]; allowedEndpoints: string[];
  minimumCategoryConfidence: number; retryLikely: number; changeRequired: number;
};
type SettingsState = {
  active: Settings; saved: Settings; revision: string; activeRevision: string; restartRequired: boolean;
  startupLoadFailed: boolean;
  csrfToken: string;
};
const url = "/api/v1/admin/failure-intelligence";
const names = (value: string) => value.split(",").map(item => item.trim()).filter(Boolean);
const FORM_ID = "failure-intelligence-settings";
/** Number of edited fields; list fields count once each. */
function countChanges(saved: Settings, draft: Settings, redactedKeys: string, endpoints: string, selected: boolean) {
  const lists = new Set<keyof Settings>(["additionalRedactedKeys", "allowedEndpoints"]);
  const scalars = (Object.keys(saved) as (keyof Settings)[]).filter(key => !lists.has(key) && saved[key] !== draft[key]).length;
  // Names are split on commas, so a comma join compares the lists exactly.
  const differs = (a: string[], b: string[]) => (a.join(",") === b.join(",") ? 0 : 1);
  return scalars + differs(saved.additionalRedactedKeys, names(redactedKeys))
    + differs(saved.allowedEndpoints, selected ? names(endpoints) : []);
}

export default function FailureIntelligenceSettings() {
  const [state, setState] = useState<SettingsState>();
  const [draft, setDraft] = useState<Settings>();
  const [redactedKeys, setRedactedKeys] = useState("");
  const [endpoints, setEndpoints] = useState("");
  const [selected, setSelected] = useState(false);
  const [error, setError] = useState<string>();
  const [message, setMessage] = useState<string>();
  const [saving, setSaving] = useState(false);
  const [loading, setLoading] = useState(true);
  const [review, setReview] = useState(false);
  const [consent, setConsent] = useState(false);
  const [reload, setReload] = useState(0);
  const alive = useRef(true);
  const abortSave = useRef<AbortController | undefined>(undefined);
  const inPanel = useInPanel();
  const changes = state && draft ? countChanges(state.saved, draft, redactedKeys, endpoints, selected) : 0;
  usePanelDirty(changes);
  usePanelReviewing(review);
  function accept(next: SettingsState) {
    setState(next); setDraft(next.saved); setRedactedKeys(next.saved.additionalRedactedKeys.join(", "));
    setEndpoints(next.saved.allowedEndpoints.join(", ")); setSelected(next.saved.allowedEndpoints.length > 0);
    setReview(false); setConsent(false);
  }
  useEffect(() => {
    alive.current = true;
    setLoading(true);
    const controller = new AbortController();
    void (async () => {
      try {
        const response = await fetch(url, { credentials: "same-origin", signal: controller.signal });
        if (!response.ok) throw new Error(response.status === 403 || response.status === 401
          ? "Only site Owners can configure failure intelligence." : "Settings are unavailable. Check shared storage and try again.");
        const next = await response.json() as SettingsState;
        if (!controller.signal.aborted) { accept(next); setError(undefined); }
      } catch (cause) {
        if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : "Settings are unavailable.");
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    })();
    return () => { alive.current = false; controller.abort(); abortSave.current?.abort(); };
  }, [reload]);
  const edit = <K extends keyof Settings>(key: K, value: Settings[K]) => {
    setDraft(current => current ? { ...current, [key]: value } : current);
    setReview(false); setConsent(false); setMessage(undefined);
  };
  async function save() {
    if (!draft || !state || saving || loading) return;
    setSaving(true); setError(undefined);
    const controller = new AbortController(); abortSave.current = controller;
    try {
      const response = await fetch(url, {
        method: "PUT", credentials: "same-origin", signal: controller.signal,
        headers: { "Content-Type": "application/json", "X-NimBus-CSRF": state.csrfToken },
        body: JSON.stringify({ revision: state.revision, payloadSharingAcknowledged: consent,
          settings: { ...draft, additionalRedactedKeys: names(redactedKeys), allowedEndpoints: selected ? names(endpoints) : [] } }),
      });
      if (!response.ok) throw new Error(response.status === 409
        ? "Another administrator changed these settings. Reload saved settings before reviewing your edits again."
        : response.status === 400 ? "Settings were rejected. Check values and reload if your security token has expired."
          : response.status === 401 || response.status === 403 ? "Only site Owners can save settings."
            : "Save could not be confirmed. Reload saved settings before retrying.");
      const next = await response.json() as SettingsState;
      if (alive.current) { accept(next); setMessage("Settings saved. Restart all WebApp instances to apply them."); }
    } catch (cause) {
      if (alive.current) setError(cause instanceof Error ? cause.message : "Save could not be confirmed. Reload before retrying.");
    } finally { if (alive.current) setSaving(false); }
  }
  if (!draft || !state) return <div className="space-y-3"><p role={error ? "alert" : "status"}>{error ?? "Loading failure intelligence settings…"}</p>
    {error && <Button onClick={() => setReload(value => value + 1)}>Try again</Button>}</div>;

  const preview = {
    failure: { eventType: "CrmAccountCreated", endpoint: "ErpEndpoint", status: "Failed", retryCount: 1, retryLimit: 3,
      exception: { type: "InvalidOperationException", message: "ERP is in error mode.", source: "Erp.Adapter" }, deadLetterReason: null },
    recentHistory: draft.includeRecentFailureHistory && draft.maximumHistoryItems > 0
      ? [{ attempt: 1, outcome: "ErrorResponse", errorType: "InvalidOperationException", errorMessage: "ERP is in error mode." }] : [],
    eventPayload: draft.includeEventPayload ? Object.fromEntries(Object.entries({ AccountId: "[redacted]", LegalName: "Example Company", TaxId: "[redacted]", CountryCode: "ES" })
      .map(([key, value]) => [key, names(redactedKeys).some(name => name.toLowerCase() === key.toLowerCase()) ? "[redacted]" : value])) : null,
  };
  const actions = <div className="flex flex-wrap items-center gap-2">
    {!inPanel && changes > 0 && <span className="text-sm font-semibold">{changes} unsaved {changes === 1 ? "change" : "changes"}</span>}
    <Button type="button" variant="outline" disabled={saving || loading} onClick={() => { setLoading(true); setReview(false); setMessage(undefined); setReload(value => value + 1); }}>{loading ? "Loading…" : "Reload saved settings"}</Button>
    <Button type="submit" form={FORM_ID} disabled={saving || loading || changes === 0}>Review changes</Button></div>;
  return <form id={FORM_ID} className="w-full min-w-0 space-y-6" onSubmit={event => {
    event.preventDefault();
    if (selected && names(endpoints).length === 0) { setError("Enter at least one endpoint ID, or select all authorized endpoints."); return; }
    setReview(true); setError(undefined);
  }}>
    <header>{!inPanel && <h2 className="text-xl font-semibold">Failure intelligence</h2>}<p className="text-muted-foreground">Advisory failure classification. Agents record classifications over MCP; you control the evidence they see and the endpoint scope.</p>
      <p className="mt-2 text-sm">Active on this instance: {state.active.enabled ? "enabled" : "disabled"} · payload {state.active.includeEventPayload ? "included" : "excluded"}</p>
      {state.restartRequired && <p role="status" className="mt-2 text-status-warning">Saved settings differ from this instance. Restart all WebApp instances to apply.</p>}
      {state.startupLoadFailed && <p role="alert">Startup settings could not be loaded. Classification is disabled until storage is restored and the WebApp restarts.</p>}
    </header>
    {error && <p role="alert" className="text-status-danger">{error}</p>}
    {message && <p role="status" className="text-status-success">{message}</p>}
    <div className={inPanel ? "space-y-6" : "grid gap-6 xl:grid-cols-[minmax(0,1.5fr)_minmax(0,1fr)]"}>
      <fieldset disabled={saving || loading} className="min-w-0 space-y-5">
        <PanelSection tab="provider">
          <Card><CardHeader><CardTitle>01 · Activation</CardTitle></CardHeader><CardContent className="space-y-4">
            <SettingToggle label="Enable failure intelligence" description="Agents with the classify permission read failure evidence and record classifications over MCP. Nothing runs automatically." checked={draft.enabled} onChange={value => edit("enabled", value)} />
          </CardContent></Card>
        </PanelSection>
        <PanelSection tab="evidence">
          <Card><CardHeader><CardTitle>02 · Failure evidence</CardTitle></CardHeader><CardContent className="space-y-4">
            <SettingToggle label="Agents may see redacted payloads" description="Data.IncludeEventPayload — the evidence agents read (nimbus_get_failure_evidence) carries the event payload with every value marked sensitive replaced." checked={draft.includeEventPayload} onChange={value => edit("includeEventPayload", value)} />
            {draft.includeEventPayload && <p className="rounded-md border border-status-warning/40 bg-status-warning/10 p-3 text-sm">Event data will leave NimBus. Only values marked sensitive in event metadata and the secret-key rules are redacted; unmarked business data remains and can reach agents, their models and the issues they write. Confirm your organization’s data-sharing policy.</p>}
            <SettingToggle label="Agents may see stack traces" description="Data.IncludeStackTrace — nimbus_get_failure_evidence with includeStackTrace returns the failing handler's frames (file paths cut to the file name, sensitive values replaced), so an agent can locate an adapter defect. Only subscribers that set IncludeStackTraceInErrorResponses store a trace." checked={Boolean(draft.includeStackTrace)} onChange={value => edit("includeStackTrace", value)} />
            {draft.includeStackTrace && <p className="rounded-md border border-status-warning/40 bg-status-warning/10 p-3 text-sm">Stack traces name the adapter's classes, methods and source files. Agents may quote them in issues in the adapter's repository.</p>}
            <SettingToggle label="Include recent failure history" description="Earlier failures for the same event, endpoint and session." checked={draft.includeRecentFailureHistory} onChange={value => edit("includeRecentFailureHistory", value)} />
            <label className="block text-sm">Maximum history items<Input type="number" min={0} max={5} required disabled={!draft.includeRecentFailureHistory} value={draft.maximumHistoryItems} onChange={event => edit("maximumHistoryItems", Number(event.target.value))} /></label>
            <label className="block text-sm">Additional redacted keys<Input value={redactedKeys} maxLength={20100} placeholder="TaxId, AccountId" onChange={event => { setRedactedKeys(event.target.value); setReview(false); }} /><span className="text-xs text-muted-foreground">Comma-separated field names. Unverifiable payloads are omitted.</span></label>
          </CardContent></Card>
        </PanelSection>
        <PanelSection tab="scope">
          <Card><CardHeader><CardTitle>03 · Scope &amp; guardrails</CardTitle></CardHeader><CardContent className="space-y-4">
            <label className="block text-sm">Allow new classifications on<Select value={selected ? "selected" : "all"} onChange={event => { setSelected(event.target.value === "selected"); setReview(false); }}><option value="all">All authorized endpoints</option><option value="selected">Selected endpoints</option></Select></label>
            {selected && <label className="block text-sm">Endpoint IDs<Input value={endpoints} required maxLength={20100} onChange={event => { setEndpoints(event.target.value); setReview(false); }} placeholder="ErpEndpoint, CrmEndpoint" /></label>}
            <p className="text-xs text-muted-foreground">Existing permissions still apply. Agents acting for a Contributor record classifications; Readers view saved results. Restricting new classifications does not remove history access.</p>
          </CardContent></Card>
        </PanelSection>
        <PanelSection tab="classification">
          <FailureClassificationExplainer thresholds={draft} onChange={(key, value) => edit(key, value)} />
        </PanelSection>
      </fieldset>
      <aside className="min-w-0 space-y-5"><PanelSection tab="evidence"><Card><CardHeader><CardTitle>What leaves NimBus</CardTitle><p className="text-xs text-muted-foreground">Illustrative ERP failure — not live data or a production redaction check.</p></CardHeader><CardContent>
        <pre className="max-h-[540px] overflow-auto whitespace-pre-wrap break-all rounded-md bg-zinc-950 p-4 text-xs leading-relaxed text-amber-100">{draft.enabled ? JSON.stringify(preview, null, 2) : "Agents can't read failure evidence while this is disabled."}</pre>
        <p className="mt-3 text-xs text-muted-foreground">No operational IDs, timestamps or credentials; a stack trace only when allowed above. Agents read it with nimbus_get_failure_evidence, together with the question set they answer. Evidence is size-limited.</p>
      </CardContent></Card></PanelSection><PanelSection tab="scope"><Card><CardContent className="pt-5 text-sm text-muted-foreground">Saving creates a shared configuration revision. Active requests and this instance’s settings stay unchanged until restart. Restart every instance; this page cannot verify other instances. Disabling does not delete saved classifications.</CardContent></Card></PanelSection></aside>
    </div>
    {review && <section aria-label="Review settings" className="space-y-4 rounded-md border border-primary/40 p-5">
      <h3 className="font-semibold">Review changes</h3><p className="text-sm">{draft.enabled ? "Enable" : "Disable"} failure intelligence · payload {draft.includeEventPayload ? "included after redaction" : "excluded"} · {selected ? endpoints : "all authorized endpoints"}.</p>
      {draft.includeEventPayload && <label className="flex gap-2 text-sm"><input type="checkbox" checked={consent} onChange={event => setConsent(event.target.checked)} />I authorize sharing redacted event payloads with agents; unmarked business data may remain.</label>}
      <div className="flex gap-2"><Button type="button" variant="outline" disabled={saving || loading} onClick={() => setReview(false)}>Back</Button><Button type="button" disabled={saving || loading || (draft.includeEventPayload && !consent)} onClick={() => void save()}>{saving ? "Saving…" : "Save settings"}</Button></div>
    </section>}
    {inPanel ? <PanelFooter>{actions}</PanelFooter>
      : <footer className="flex flex-wrap justify-between gap-3 border-t pt-4"><p className="text-sm text-muted-foreground">Site Owner only · restart required</p>{actions}</footer>}
  </form>;
}

function SettingToggle({ label, description, checked, onChange }: { label: string; description: string; checked: boolean; onChange: (value: boolean) => void }) {
  return <div className="flex items-center justify-between gap-4"><div><p className="text-sm font-semibold">{label}</p><p className="text-xs text-muted-foreground">{description}</p></div><Toggle aria-label={label} checked={checked} onChange={onChange} /></div>;
}
