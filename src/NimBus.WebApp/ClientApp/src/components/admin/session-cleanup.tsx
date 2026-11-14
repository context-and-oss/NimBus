import { useMemo, useState } from "react";
import moment from "moment";
import * as api from "api-client";
import { Badge, type BadgeVariant } from "components/ui/badge";
import { Button } from "components/ui/button";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "components/ui/card";
import { Checkbox } from "components/ui/checkbox";
import { Combobox } from "components/ui/combobox";
import { Input } from "components/ui/input";
import ConfirmDestructiveAction from "./confirm-destructive-action";
import OperationErrorAlert from "./operation-error-alert";
import { describeOperationError } from "./operation-error";

interface EndpointOption {
  value: string;
  label: string;
}

const Classification = api.SessionCleanupCandidateClassification;

const CLASSIFICATION: Record<api.SessionCleanupCandidateClassification, { label: string; tone: BadgeVariant }> = {
  [Classification.Idle]: { label: "Idle", tone: "success" },
  [Classification.StaleBlock]: { label: "Stale block", tone: "warning" },
  [Classification.InUse]: { label: "In use", tone: "default" },
};

const DEFAULT_DAYS = 30;
const DEFAULT_MAX = 200;

/**
 * Operations → Clean up session state. Service Bus keeps a session's state after its messages
 * are gone; this finds the sessions of an endpoint's recent events whose state nothing depends
 * on any more and resets it. Preview first; apply resets only the selected sessions, each
 * re-checked under its lock at the version the preview saw.
 */
export function SessionCleanupCard({ endpoints, initialEndpoint }: { endpoints: EndpointOption[]; initialEndpoint?: string }) {
  const [selectedEndpoint, setSelectedEndpoint] = useState<string[]>(initialEndpoint ? [initialEndpoint] : []);
  const [days, setDays] = useState(String(DEFAULT_DAYS));
  const [maxSessions, setMaxSessions] = useState(String(DEFAULT_MAX));
  const [includeStaleBlocks, setIncludeStaleBlocks] = useState(false);
  const [preview, setPreview] = useState<api.SessionCleanupPreview | null>(null);
  const [selected, setSelected] = useState<Set<string>>(new Set());
  const [loading, setLoading] = useState(false);
  const [running, setRunning] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [result, setResult] = useState<api.SessionCleanupResult | null>(null);

  const endpoint = selectedEndpoint[0];
  const sessions = useMemo(() => preview?.sessions ?? [], [preview]);
  const eligible = useMemo(
    () =>
      sessions.filter(
        (s) =>
          s.classification === Classification.Idle ||
          (includeStaleBlocks && s.classification === Classification.StaleBlock),
      ),
    [sessions, includeStaleBlocks],
  );
  const chosen = eligible.filter((s) => selected.has(s.sessionId ?? ""));

  function reset() {
    setPreview(null);
    setSelected(new Set());
    setResult(null);
    setError(null);
  }

  async function handlePreview() {
    if (!endpoint) return;
    setLoading(true);
    reset();
    try {
      const client = new api.Client(api.CookieAuth());
      const parsedDays = Number(days);
      const since = Number.isFinite(parsedDays) && parsedDays > 0 ? moment.utc().subtract(parsedDays, "days") : undefined;
      const parsedMax = Number(maxSessions);
      const p = await client.postAdminSessionCleanupPreview(
        new api.SessionCleanupPreviewRequest({
          updatedSince: since,
          maxSessions: Number.isInteger(parsedMax) && parsedMax > 0 ? parsedMax : undefined,
          includeStaleBlocks,
        }),
        endpoint,
      );
      setPreview(p);
      setSelected(
        new Set(
          (p.sessions ?? [])
            .filter((s) => s.classification === Classification.Idle)
            .map((s) => s.sessionId ?? ""),
        ),
      );
    } catch (err) {
      setError(describeOperationError(err));
    } finally {
      setLoading(false);
    }
  }

  async function handleCleanup() {
    if (!endpoint || chosen.length === 0) return;
    setShowConfirm(false);
    setRunning(true);
    setError(null);
    try {
      const client = new api.Client(api.CookieAuth());
      const r = await client.postAdminSessionCleanup(
        new api.SessionCleanupRequest({
          includeStaleBlocks,
          sessions: chosen.map(
            (s) => new api.SessionCleanupSelection({ sessionId: s.sessionId ?? "", stateVersion: s.stateVersion ?? "" }),
          ),
        }),
        endpoint,
      );
      setResult(r);
      setPreview(null);
      setSelected(new Set());
    } catch (err) {
      setError(describeOperationError(err));
    } finally {
      setRunning(false);
    }
  }

  function toggle(id: string) {
    setSelected((current) => {
      const next = new Set(current);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return next;
    });
  }

  const allChosen = eligible.length > 0 && chosen.length === eligible.length;

  return (
    <div className="space-y-6 w-full">
      <Card>
        <CardHeader>
          <CardTitle>Clean up session state</CardTitle>
          <CardDescription>
            Finds the sessions of an endpoint's recent events that still hold state nothing depends on, and resets it.
            Sessions the store never saw aren't found.
          </CardDescription>
        </CardHeader>
        <CardContent>
          <div className="space-y-4">
            <div className="grid grid-cols-1 md:grid-cols-3 gap-4">
              <Combobox
                options={endpoints}
                value={selectedEndpoint}
                onChange={(v) => {
                  setSelectedEndpoint(v);
                  reset();
                }}
                placeholder="Select endpoint..."
                label="Endpoint"
                multiple={false}
              />
              <div>
                <label htmlFor="session-cleanup-days" className="block text-sm font-medium text-foreground mb-1">
                  Events updated in the last (days)
                </label>
                <Input id="session-cleanup-days" type="number" min={1} value={days} onChange={(e) => setDays(e.target.value)} />
              </div>
              <div>
                <label htmlFor="session-cleanup-max" className="block text-sm font-medium text-foreground mb-1">
                  Most sessions to inspect
                </label>
                <Input
                  id="session-cleanup-max"
                  type="number"
                  min={1}
                  max={1000}
                  value={maxSessions}
                  onChange={(e) => setMaxSessions(e.target.value)}
                />
              </div>
            </div>

            <Checkbox
              label="Also reset stale blocks (the blocking event is no longer failed or pending)"
              checked={includeStaleBlocks}
              onChange={(e) => {
                setIncludeStaleBlocks(e.target.checked);
                if (!e.target.checked)
                  setSelected(
                    (current) =>
                      new Set(
                        [...current].filter(
                          (id) => sessions.find((s) => s.sessionId === id)?.classification === Classification.Idle,
                        ),
                      ),
                  );
              }}
            />

            <Button onClick={handlePreview} disabled={!endpoint || loading} isLoading={loading} variant="outline">
              Preview cleanup
            </Button>

            <OperationErrorAlert message={error} />

            {preview && (
              <div className="space-y-3">
                <p className="m-0 text-sm" role="status">
                  Inspected {preview.inspectedCount} sessions: {preview.emptyCount} hold no state,{" "}
                  {sessions.filter((s) => s.classification === Classification.Idle).length} idle,{" "}
                  {sessions.filter((s) => s.classification === Classification.StaleBlock).length} stale blocks,{" "}
                  {sessions.filter((s) => s.classification === Classification.InUse).length} in use.
                  {preview.truncated && " More sessions matched; run again after this cleanup or narrow the window."}
                </p>

                {sessions.length > 0 && (
                  <div className="max-h-128 overflow-auto rounded-nb-md border border-border">
                    <table className="w-full text-sm">
                      <thead className="sticky top-0 bg-card shadow-[inset_0_-1px_0_0] shadow-border">
                        <tr className="text-left">
                          <th className="px-3 py-2 w-8">
                            <input
                              type="checkbox"
                              aria-label="Select all eligible sessions"
                              checked={allChosen}
                              disabled={eligible.length === 0}
                              onChange={() =>
                                setSelected(allChosen ? new Set() : new Set(eligible.map((s) => s.sessionId ?? "")))
                              }
                            />
                          </th>
                          <th className="px-3 py-2">Session</th>
                          <th className="px-3 py-2">Classification</th>
                          <th className="px-3 py-2">Why</th>
                        </tr>
                      </thead>
                      <tbody>
                        {sessions.map((s) => {
                          const id = s.sessionId ?? "";
                          const canSelect = eligible.includes(s);
                          const c = CLASSIFICATION[s.classification ?? Classification.InUse];
                          return (
                            <tr key={id} className="border-t border-border">
                              <td className="px-3 py-1.5 align-top">
                                <input
                                  type="checkbox"
                                  aria-label={`Reset ${id}`}
                                  checked={canSelect && selected.has(id)}
                                  disabled={!canSelect}
                                  onChange={() => toggle(id)}
                                />
                              </td>
                              <td className="px-3 py-1.5 align-top font-mono text-[11.5px] whitespace-nowrap">{id}</td>
                              <td className="px-3 py-1.5 align-top">
                                <Badge variant={c.tone} size="sm">
                                  {c.label}
                                </Badge>
                              </td>
                              <td className="px-3 py-1.5 align-top text-muted-foreground">{s.reason}</td>
                            </tr>
                          );
                        })}
                      </tbody>
                    </table>
                  </div>
                )}

                <Button
                  colorScheme="red"
                  size="sm"
                  onClick={() => setShowConfirm(true)}
                  disabled={chosen.length === 0 || running}
                  isLoading={running}
                >
                  Reset {chosen.length} {chosen.length === 1 ? "session" : "sessions"}
                </Button>
              </div>
            )}

            {result && (
              <div className="bg-green-50 border border-green-200 dark:bg-green-950/30 dark:border-green-900/60 rounded-md p-4 space-y-2 text-sm">
                <h4 className="m-0 font-medium">Cleanup complete: {result.resetCount} reset</h4>
                {(result.skipped?.length ?? 0) > 0 && (
                  <div>
                    <p className="m-0 text-xs font-medium">Skipped (re-checked and no longer eligible):</p>
                    <ul className="m-0 text-xs space-y-0.5">
                      {result.skipped!.map((s) => (
                        <li key={s.sessionId}>
                          <span className="font-mono">{s.sessionId}</span> — {s.reason}
                        </li>
                      ))}
                    </ul>
                  </div>
                )}
                {(result.errors?.length ?? 0) > 0 && (
                  <div>
                    <p className="m-0 text-xs font-medium text-red-700 dark:text-red-300">Errors:</p>
                    <ul className="m-0 text-xs text-red-600 dark:text-red-400 space-y-0.5">
                      {result.errors!.map((e) => (
                        <li key={e} className="font-mono">
                          {e}
                        </li>
                      ))}
                    </ul>
                  </div>
                )}
              </div>
            )}
          </div>
        </CardContent>
      </Card>

      <ConfirmDestructiveAction
        isOpen={showConfirm}
        onClose={() => setShowConfirm(false)}
        onConfirm={handleCleanup}
        title="Reset session state"
        confirmLabel="Reset session state"
        description={`Resets the session state of ${chosen.length} ${chosen.length === 1 ? "session" : "sessions"} on "${endpoint ?? ""}". Each is re-checked first and skipped if it changed or is no longer idle.`}
        confirmText={endpoint ?? ""}
        isLoading={running}
      />
    </div>
  );
}
