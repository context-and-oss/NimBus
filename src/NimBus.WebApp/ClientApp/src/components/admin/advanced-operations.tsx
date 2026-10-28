import { useState, useEffect } from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  CardDescription,
} from "components/ui/card";
import { Combobox } from "components/ui/combobox";
import { Input } from "components/ui/input";
import ConfirmDestructiveAction from "./confirm-destructive-action";
import OperationProgress from "./operation-progress";

interface EndpointOption {
  value: string;
  label: string;
}

export default function AdvancedOperations() {
  const [endpoints, setEndpoints] = useState<EndpointOption[]>([]);

  useEffect(() => {
    loadEndpoints();
  }, []);

  async function loadEndpoints() {
    try {
      const client = new api.Client(api.CookieAuth());
      const config = await client.getAdminPlatformConfig();
      const eps = (config.endpoints ?? []).map((ep) => ({
        value: ep.id ?? "",
        label: ep.name ?? ep.id ?? "",
      }));
      setEndpoints(eps);
    } catch {
      // fallback
    }
  }

  return (
    <div className="space-y-6 w-full">
      <SubscriptionPurgeCard endpoints={endpoints} />
      <DeleteByStatusCard endpoints={endpoints} />
      <SkipMessagesCard endpoints={endpoints} />
      <DeleteMessagesByToCard />
      <CopyEndpointCard endpoints={endpoints} />
    </div>
  );
}

// ──────────────────── Subscription Purge ────────────────────────

export function SubscriptionPurgeCard({ endpoints, initialEndpoint }: { endpoints: EndpointOption[]; initialEndpoint?: string }) {
  const [selected, setSelected] = useState<string[]>(initialEndpoint ? [initialEndpoint] : []);
  const [subscription, setSubscription] = useState("");
  const [purgeActive, setPurgeActive] = useState(true);
  const [purgeDeferred, setPurgeDeferred] = useState(true);
  const [before, setBefore] = useState("");
  const [preview, setPreview] = useState<api.PurgePreview | null>(null);
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<api.BulkOperationResult | null>(null);
  const [executing, setExecuting] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);

  function buildRequest(): api.PurgeRequest {
    const req = new api.PurgeRequest();
    if (subscription) req.subscription = subscription;
    const states: string[] = [];
    if (purgeActive) states.push("active");
    if (purgeDeferred) states.push("deferred");
    req.states = states;
    if (before) req.before = new Date(before) as any;
    return req;
  }

  async function handlePreview() {
    if (selected.length === 0) return;
    setLoading(true);
    setPreview(null);
    setResult(null);
    try {
      const client = new api.Client(api.CookieAuth());
      const p = await client.postAdminPurgePreview(selected[0], buildRequest());
      setPreview(p);
    } catch { /* */ } finally { setLoading(false); }
  }

  async function handleExecute() {
    if (selected.length === 0) return;
    setShowConfirm(false);
    setExecuting(true);
    try {
      const client = new api.Client(api.CookieAuth());
      const r = await client.postAdminPurge(selected[0], buildRequest());
      setResult(r);
      setPreview(null);
    } catch { /* */ } finally { setExecuting(false); }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Purge Subscription Messages</CardTitle>
        <CardDescription>
          Purge messages from a Service Bus subscription by state and/or enqueued time
        </CardDescription>
      </CardHeader>
      <CardContent>
        <div className="space-y-4">
          <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
            <div>
              <label className="block text-xs font-medium text-muted-foreground mb-1">Endpoint</label>
              <Combobox options={endpoints} value={selected} onChange={(v) => { setSelected(v); setPreview(null); setResult(null); }} placeholder="Select endpoint..." multiple={false} />
            </div>
            <div>
              <label className="block text-xs font-medium text-muted-foreground mb-1">Subscription (optional)</label>
              <Input value={subscription} onChange={(e) => setSubscription(e.target.value)} placeholder="Defaults to endpoint name" />
            </div>
            <div>
              <label className="block text-xs font-medium text-muted-foreground mb-1">States</label>
              <div className="flex gap-4 mt-2">
                <label className="flex items-center gap-1 text-sm"><input type="checkbox" checked={purgeActive} onChange={(e) => setPurgeActive(e.target.checked)} /> Active</label>
                <label className="flex items-center gap-1 text-sm"><input type="checkbox" checked={purgeDeferred} onChange={(e) => setPurgeDeferred(e.target.checked)} /> Deferred</label>
              </div>
            </div>
            <div>
              <label className="block text-xs font-medium text-muted-foreground mb-1">Before</label>
              <Input type="datetime-local" value={before} onChange={(e) => setBefore(e.target.value)} />
            </div>
          </div>
          <Button onClick={handlePreview} disabled={selected.length === 0 || loading} isLoading={loading} variant="outline">Preview</Button>

          {preview && (
            <div className="bg-blue-50 border border-blue-200 dark:bg-blue-950/30 dark:border-blue-900/60 rounded-md p-4 space-y-2">
              <p className="text-sm">Scanned: <span className="font-bold">{preview.totalScanned}</span></p>
              <p className="text-sm">Matching: <span className="font-bold">{preview.totalMatching}</span></p>
              <p className="text-sm">Sessions: <span className="font-bold">{preview.sessionCount}</span></p>
              {(preview.totalMatching ?? 0) > 0 && (
                <Button colorScheme="red" onClick={() => setShowConfirm(true)} disabled={executing} isLoading={executing} size="sm">
                  Purge {preview.totalMatching} messages
                </Button>
              )}
            </div>
          )}

          {result && <OperationProgress processed={result.processed ?? 0} succeeded={result.succeeded ?? 0} failed={result.failed ?? 0} errors={result.errors} isComplete={true} />}

          <ConfirmDestructiveAction isOpen={showConfirm} onClose={() => setShowConfirm(false)} onConfirm={handleExecute}
            title="Purge Subscription Messages" confirmLabel="Purge subscription" description={`This will purge ${preview?.totalMatching ?? 0} messages from endpoint "${selected[0] ?? ""}".`}
            confirmText={selected[0] ?? ""} isLoading={executing} />
        </div>
      </CardContent>
    </Card>
  );
}

// ──────────────────── Delete by Status ────────────────────────

const ALL_STATUSES: api.AdminDeleteStatus[] = [
  api.AdminDeleteStatus.Failed,
  api.AdminDeleteStatus.Deferred,
  api.AdminDeleteStatus.DeadLettered,
  api.AdminDeleteStatus.Unsupported,
  api.AdminDeleteStatus.Pending,
];

export function DeleteByStatusCard({ endpoints, initialEndpoint }: { endpoints: EndpointOption[]; initialEndpoint?: string }) {
  const [selected, setSelected] = useState<string[]>(initialEndpoint ? [initialEndpoint] : []);
  const [statuses, setStatuses] = useState<Set<api.AdminDeleteStatus>>(new Set());
  const [previewCount, setPreviewCount] = useState<number | null>(null);
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<api.BulkOperationResult | null>(null);
  const [executing, setExecuting] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);

  function toggleStatus(s: api.AdminDeleteStatus) {
    setStatuses((prev) => { const next = new Set(prev); next.has(s) ? next.delete(s) : next.add(s); return next; });
    setPreviewCount(null);
    setResult(null);
  }

  async function handlePreview() {
    if (selected.length === 0 || statuses.size === 0) return;
    setLoading(true);
    setPreviewCount(null);
    setResult(null);
    try {
      const client = new api.Client(api.CookieAuth());
      const req = new api.DeleteByStatusRequest();
      req.statuses = Array.from(statuses);
      const r = await client.postAdminDeleteByStatusPreview(selected[0], req);
      setPreviewCount(r.count ?? 0);
    } catch { /* */ } finally { setLoading(false); }
  }

  async function handleExecute() {
    if (selected.length === 0 || statuses.size === 0) return;
    setShowConfirm(false);
    setExecuting(true);
    try {
      const client = new api.Client(api.CookieAuth());
      const req = new api.DeleteByStatusRequest();
      req.statuses = Array.from(statuses);
      const r = await client.postAdminDeleteByStatus(selected[0], req);
      setResult(r);
      setPreviewCount(null);
    } catch { /* */ } finally { setExecuting(false); }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Delete Events by Status</CardTitle>
        <CardDescription>Delete events filtered by resolution status</CardDescription>
      </CardHeader>
      <CardContent>
        <div className="space-y-4">
          <div className="flex gap-4 items-end">
            <div className="flex-1 max-w-md">
              <Combobox options={endpoints} value={selected} onChange={(v) => { setSelected(v); setPreviewCount(null); setResult(null); }} placeholder="Select endpoint..." label="Endpoint" multiple={false} />
            </div>
          </div>
          <div>
            <label className="block text-xs font-medium text-muted-foreground mb-1">Statuses</label>
            <div className="flex flex-wrap gap-3">
              {ALL_STATUSES.map((s) => (
                <label key={s} className="flex items-center gap-1 text-sm">
                  <input type="checkbox" checked={statuses.has(s)} onChange={() => toggleStatus(s)} /> {s}
                </label>
              ))}
            </div>
          </div>
          <Button onClick={handlePreview} disabled={selected.length === 0 || statuses.size === 0 || loading} isLoading={loading} variant="outline">Preview</Button>

          {previewCount !== null && (
            <div className="bg-blue-50 border border-blue-200 dark:bg-blue-950/30 dark:border-blue-900/60 rounded-md p-4 space-y-2">
              <p className="text-sm">Events matching: <span className="font-bold">{previewCount}</span></p>
              {previewCount > 0 && (
                <Button colorScheme="red" onClick={() => setShowConfirm(true)} disabled={executing} isLoading={executing} size="sm">
                  Delete {previewCount} events
                </Button>
              )}
            </div>
          )}

          {result && <OperationProgress processed={result.processed ?? 0} succeeded={result.succeeded ?? 0} failed={result.failed ?? 0} errors={result.errors} isComplete={true} />}

          <ConfirmDestructiveAction isOpen={showConfirm} onClose={() => setShowConfirm(false)} onConfirm={handleExecute}
            title="Delete Events by Status" confirmLabel="Delete events" description={`This will delete ${previewCount ?? 0} events with status [${Array.from(statuses).join(", ")}] from endpoint "${selected[0] ?? ""}".`}
            confirmText={selected[0] ?? ""} isLoading={executing} />
        </div>
      </CardContent>
    </Card>
  );
}

// ──────────────────── Skip Messages ────────────────────────

// The statuses the server accepts (SkipRules.BulkSkipSourceStatuses): each skip is sent to the
// endpoint, which settles these. Deferred is left out: its parked copy replays and runs once the
// session's block clears, whatever a skip recorded.
const SKIPPABLE_STATUSES: api.AdminSkipSourceStatus[] = [
  api.AdminSkipSourceStatus.Failed,
  api.AdminSkipSourceStatus.DeadLettered,
  api.AdminSkipSourceStatus.Unsupported,
];

export function SkipMessagesCard({ endpoints, initialEndpoint }: { endpoints: EndpointOption[]; initialEndpoint?: string }) {
  const [selected, setSelected] = useState<string[]>(initialEndpoint ? [initialEndpoint] : []);
  const [statuses, setStatuses] = useState<Set<api.AdminSkipSourceStatus>>(new Set());
  const [before, setBefore] = useState("");
  const [previewCount, setPreviewCount] = useState<number | null>(null);
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<api.BulkOperationResult | null>(null);
  const [executing, setExecuting] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);

  function toggleStatus(s: api.AdminSkipSourceStatus) {
    setStatuses((prev) => { const next = new Set(prev); next.has(s) ? next.delete(s) : next.add(s); return next; });
    setPreviewCount(null);
    setResult(null);
  }

  function buildRequest(): api.SkipRequest {
    const req = new api.SkipRequest();
    req.statuses = Array.from(statuses);
    if (before) req.before = new Date(before) as any;
    return req;
  }

  async function handlePreview() {
    if (selected.length === 0 || statuses.size === 0) return;
    setLoading(true);
    setPreviewCount(null);
    setResult(null);
    try {
      const client = new api.Client(api.CookieAuth());
      const r = await client.postAdminSkipPreview(selected[0], buildRequest());
      setPreviewCount(r.count ?? 0);
    } catch { /* */ } finally { setLoading(false); }
  }

  async function handleExecute() {
    if (selected.length === 0 || statuses.size === 0) return;
    setShowConfirm(false);
    setExecuting(true);
    try {
      const client = new api.Client(api.CookieAuth());
      const r = await client.postAdminSkip(selected[0], buildRequest());
      setResult(r);
      setPreviewCount(null);
    } catch { /* */ } finally { setExecuting(false); }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Skip Messages</CardTitle>
        <CardDescription>Send a skip to the endpoint for each matching event, so the sessions they block continue</CardDescription>
      </CardHeader>
      <CardContent>
        <div className="space-y-4">
          <div className="grid grid-cols-2 gap-4">
            <div>
              <Combobox options={endpoints} value={selected} onChange={(v) => { setSelected(v); setPreviewCount(null); setResult(null); }} placeholder="Select endpoint..." label="Endpoint" multiple={false} />
            </div>
            <div>
              <label className="block text-xs font-medium text-muted-foreground mb-1">Before (optional)</label>
              <Input type="datetime-local" value={before} onChange={(e) => setBefore(e.target.value)} />
            </div>
          </div>
          <div>
            <label className="block text-xs font-medium text-muted-foreground mb-1">Source Statuses</label>
            <div className="flex flex-wrap gap-3">
              {SKIPPABLE_STATUSES.map((s) => (
                <label key={s} className="flex items-center gap-1 text-sm">
                  <input type="checkbox" checked={statuses.has(s)} onChange={() => toggleStatus(s)} /> {s}
                </label>
              ))}
            </div>
          </div>
          <Button onClick={handlePreview} disabled={selected.length === 0 || statuses.size === 0 || loading} isLoading={loading} variant="outline">Preview</Button>

          {previewCount !== null && (
            <div className="bg-blue-50 border border-blue-200 dark:bg-blue-950/30 dark:border-blue-900/60 rounded-md p-4 space-y-2">
              <p className="text-sm">Eligible to skip: <span className="font-bold">{previewCount}</span></p>
              {previewCount > 0 && (
                <Button colorScheme="blue" onClick={() => setShowConfirm(true)} disabled={executing} isLoading={executing} size="sm">
                  Skip {previewCount} messages
                </Button>
              )}
            </div>
          )}

          {result && <OperationProgress processed={result.processed ?? 0} succeeded={result.succeeded ?? 0} failed={result.failed ?? 0} errors={result.errors} isComplete={true} />}

          <ConfirmDestructiveAction isOpen={showConfirm} onClose={() => setShowConfirm(false)} onConfirm={handleExecute}
            title="Skip Messages" confirmLabel="Skip messages" tone="primary" description={`This will send a skip for ${previewCount ?? 0} events to endpoint "${selected[0] ?? ""}". Each one shows as Skipped once the endpoint has handled it.`}
            confirmText={selected[0] ?? ""} isLoading={executing} />
        </div>
      </CardContent>
    </Card>
  );
}

// ──────────────────── Delete Messages by To ────────────────────────

export function DeleteMessagesByToCard() {
  const [toField, setToField] = useState("");
  const [previewCount, setPreviewCount] = useState<number | null>(null);
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<api.BulkOperationResult | null>(null);
  const [executing, setExecuting] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);

  async function handlePreview() {
    if (!toField) return;
    setLoading(true);
    setPreviewCount(null);
    setResult(null);
    try {
      const client = new api.Client(api.CookieAuth());
      const req = new api.DeleteByToRequest();
      req.toField = toField;
      const r = await client.postAdminDeleteByToPreview(req);
      setPreviewCount(r.count ?? 0);
    } catch { /* */ } finally { setLoading(false); }
  }

  async function handleExecute() {
    if (!toField) return;
    setShowConfirm(false);
    setExecuting(true);
    try {
      const client = new api.Client(api.CookieAuth());
      const req = new api.DeleteByToRequest();
      req.toField = toField;
      const r = await client.postAdminDeleteByTo(req);
      setResult(r);
      setPreviewCount(null);
    } catch { /* */ } finally { setExecuting(false); }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Delete Messages by To Field</CardTitle>
        <CardDescription>Delete messages from the messages container filtered by the "To" field</CardDescription>
      </CardHeader>
      <CardContent>
        <div className="space-y-4">
          <div className="flex gap-4 items-end">
            <div className="flex-1 max-w-md">
              <label className="block text-xs font-medium text-muted-foreground mb-1">To (Subscriber)</label>
              <Input value={toField} onChange={(e) => { setToField(e.target.value); setPreviewCount(null); setResult(null); }} placeholder="e.g. CrmEndpoint" />
            </div>
            <Button onClick={handlePreview} disabled={!toField || loading} isLoading={loading} variant="outline">Preview</Button>
          </div>

          {previewCount !== null && (
            <div className="bg-blue-50 border border-blue-200 dark:bg-blue-950/30 dark:border-blue-900/60 rounded-md p-4 space-y-2">
              <p className="text-sm">Messages matching: <span className="font-bold">{previewCount}</span></p>
              {previewCount > 0 && (
                <Button colorScheme="red" onClick={() => setShowConfirm(true)} disabled={executing} isLoading={executing} size="sm">
                  Delete {previewCount} messages
                </Button>
              )}
            </div>
          )}

          {result && <OperationProgress processed={result.processed ?? 0} succeeded={result.succeeded ?? 0} failed={result.failed ?? 0} errors={result.errors} isComplete={true} />}

          <ConfirmDestructiveAction isOpen={showConfirm} onClose={() => setShowConfirm(false)} onConfirm={handleExecute}
            title="Delete Messages by To" confirmLabel="Delete messages" description={`This will delete ${previewCount ?? 0} messages where To="${toField}".`}
            confirmText={toField} isLoading={executing} />
        </div>
      </CardContent>
    </Card>
  );
}

// ──────────────────── Copy Endpoint Data ────────────────────────

export function CopyEndpointCard({ endpoints, initialEndpoint }: { endpoints: EndpointOption[]; initialEndpoint?: string }) {
  const [selected, setSelected] = useState<string[]>(initialEndpoint ? [initialEndpoint] : []);
  const [targetConnStr, setTargetConnStr] = useState("");
  const [from, setFrom] = useState("");
  const [to, setTo] = useState("");
  const [statuses, setStatuses] = useState<Set<string>>(new Set());
  const [batchSize, setBatchSize] = useState("");
  const [result, setResult] = useState<api.CopyResult | null>(null);
  const [executing, setExecuting] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);

  function toggleStatus(s: string) {
    setStatuses((prev) => { const next = new Set(prev); next.has(s) ? next.delete(s) : next.add(s); return next; });
  }

  async function handleExecute() {
    if (selected.length === 0 || !targetConnStr) return;
    setShowConfirm(false);
    setExecuting(true);
    try {
      const client = new api.Client(api.CookieAuth());
      const req = new api.CopyRequest();
      req.targetConnectionString = targetConnStr;
      if (from) req.from = new Date(from) as any;
      if (to) req.to = new Date(to) as any;
      if (statuses.size > 0) req.statuses = Array.from(statuses);
      if (batchSize) req.batchSize = parseInt(batchSize, 10);
      const r = await client.postAdminCopy(selected[0], req);
      setResult(r);
    } catch { /* */ } finally { setExecuting(false); }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Copy Endpoint Data</CardTitle>
        <CardDescription>Copy events and messages from this storage instance to another (Cosmos DB only)</CardDescription>
      </CardHeader>
      <CardContent>
        <div className="space-y-4">
          <div className="grid grid-cols-2 gap-4">
            <div>
              <Combobox options={endpoints} value={selected} onChange={(v) => { setSelected(v); setResult(null); }} placeholder="Select endpoint..." label="Endpoint" multiple={false} />
            </div>
            <div>
              <label className="block text-xs font-medium text-muted-foreground mb-1">Target Connection String</label>
              <Input type="password" value={targetConnStr} onChange={(e) => setTargetConnStr(e.target.value)} placeholder="AccountEndpoint=..." />
            </div>
          </div>
          <div className="grid grid-cols-3 gap-4">
            <div>
              <label className="block text-xs font-medium text-muted-foreground mb-1">From (optional)</label>
              <Input type="datetime-local" value={from} onChange={(e) => setFrom(e.target.value)} />
            </div>
            <div>
              <label className="block text-xs font-medium text-muted-foreground mb-1">To (optional)</label>
              <Input type="datetime-local" value={to} onChange={(e) => setTo(e.target.value)} />
            </div>
            <div>
              <label className="block text-xs font-medium text-muted-foreground mb-1">Batch Size (optional)</label>
              <Input type="number" value={batchSize} onChange={(e) => setBatchSize(e.target.value)} placeholder="All" />
            </div>
          </div>
          <div>
            <label className="block text-xs font-medium text-muted-foreground mb-1">Status Filter (optional)</label>
            <div className="flex flex-wrap gap-3">
              {ALL_STATUSES.map((s) => (
                <label key={s} className="flex items-center gap-1 text-sm">
                  <input type="checkbox" checked={statuses.has(s)} onChange={() => toggleStatus(s)} /> {s}
                </label>
              ))}
            </div>
          </div>
          <Button colorScheme="primary" onClick={() => setShowConfirm(true)} disabled={selected.length === 0 || !targetConnStr || executing} isLoading={executing}>
            Copy Data
          </Button>

          {result && (
            <div className="bg-green-50 border border-green-200 dark:bg-green-950/30 dark:border-green-900/60 rounded-md p-4 space-y-1">
              <p className="text-sm">Events copied: <span className="font-bold">{result.eventsCopied}</span></p>
              <p className="text-sm">Messages copied: <span className="font-bold">{result.messagesCopied}</span></p>
            </div>
          )}

          <ConfirmDestructiveAction isOpen={showConfirm} onClose={() => setShowConfirm(false)} onConfirm={handleExecute}
            title="Copy Endpoint Data" confirmLabel="Copy data" description={`This will copy data from endpoint "${selected[0] ?? ""}" to the target instance.`}
            confirmText={selected[0] ?? ""} isLoading={executing} />
        </div>
      </CardContent>
    </Card>
  );
}

// ──────────────────── Delete All Events ────────────────────────

export function DeleteAllEventsCard({ endpoints, initialEndpoint }: { endpoints: EndpointOption[]; initialEndpoint?: string }) {
  const [selected, setSelected] = useState<string[]>(initialEndpoint ? [initialEndpoint] : []);
  const [result, setResult] = useState<api.BulkOperationResult | null>(null);
  const [executing, setExecuting] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);

  async function handleExecute() {
    if (selected.length === 0) return;
    setShowConfirm(false);
    setExecuting(true);
    try {
      const client = new api.Client(api.CookieAuth());
      const r = await client.postAdminDeleteAll(selected[0]);
      setResult(r);
    } catch { /* */ } finally { setExecuting(false); }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Delete All Events</CardTitle>
        <CardDescription>Wipe the endpoint: delete and recreate its subscription and its Deferred subscription (discarding every queued and parked message), then delete all stored events regardless of status and the endpoint's message history.</CardDescription>
      </CardHeader>
      <CardContent>
        <div className="space-y-4">
          <div className="flex gap-4 items-end">
            <div className="flex-1 max-w-md">
              <Combobox options={endpoints} value={selected} onChange={(v) => { setSelected(v); setResult(null); }} placeholder="Select endpoint..." label="Endpoint" multiple={false} />
            </div>
            <Button colorScheme="red" onClick={() => setShowConfirm(true)} disabled={selected.length === 0 || executing} isLoading={executing}>
              Delete All
            </Button>
          </div>

          {result && <OperationProgress processed={result.processed ?? 0} succeeded={result.succeeded ?? 0} failed={result.failed ?? 0} errors={result.errors} isComplete={true} />}

          <ConfirmDestructiveAction isOpen={showConfirm} onClose={() => setShowConfirm(false)} onConfirm={handleExecute}
            title="Delete All Events" confirmLabel="Delete all events" description={`This will permanently wipe endpoint "${selected[0] ?? ""}": its subscription and Deferred subscription are deleted and recreated empty, and every stored event and message-history record for it is deleted. This action cannot be undone.`}
            confirmText={selected[0] ?? ""} isLoading={executing} />
        </div>
      </CardContent>
    </Card>
  );
}
