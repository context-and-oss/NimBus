import { useState } from "react";
import * as api from "api-client";
import { Badge, type BadgeVariant } from "components/ui/badge";
import { Button } from "components/ui/button";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "components/ui/card";
import { Combobox } from "components/ui/combobox";
import { Input } from "components/ui/input";
import { StatTile } from "components/ui/stat-tile";
import TruncatedGuid from "components/common/truncated-guid";
import ConfirmDestructiveAction from "./confirm-destructive-action";
import OperationProgress from "./operation-progress";

interface EndpointOption {
  value: string;
  label: string;
}

/**
 * Spec 032. A row that a redelivered request copy reopened after the endpoint had already
 * answered reads Pending forever: the Resolver's outcome is stored, the row just does not
 * reflect it. Preview classifies every Pending row from its stored history; Repair re-applies
 * the stored ResolutionResponse to the rows the rule calls Repairable and nothing else.
 */
const VERDICT_TONE: Record<string, BadgeVariant> = {
  Repairable: "completed",
  HistoryMissing: "warning",
  NoTerminal: "pending",
  LatestTerminalIsError: "warning",
  LatestTerminalIsSkip: "warning",
  LatestTerminalIsDeadLettered: "warning",
  ResponseNotBeforeRow: "pending",
  LaterControlMessage: "warning",
  LaterRequestCopy: "pending",
};

/**
 * Repairs per server round-trip. The server scans every candidate before the cut-off and stops
 * collecting at this many Repairable rows, so a round that comes back with fewer processed than
 * this reached the end of the backlog; a full round means there may be more, and the card asks
 * again. Small enough that one request stays well inside any front-door timeout.
 */
export const REPAIR_BATCH_SIZE = 500;

/** Safety valve on the batch loop: 200 batches is 100 000 repairs, far beyond any real incident. */
const MAX_REPAIR_BATCHES = 200;

/** The card sends UTC, so the local datetime-local value is converted, never pasted through. */
function toIsoUtc(localValue: string): Date | undefined {
  if (!localValue) return undefined;
  const parsed = new Date(localValue);
  return Number.isNaN(parsed.getTime()) ? undefined : parsed;
}

function defaultCutoff(): string {
  const anHourAgo = new Date(Date.now() - 60 * 60 * 1000);
  const offsetMs = anHourAgo.getTimezoneOffset() * 60 * 1000;
  return new Date(anHourAgo.getTime() - offsetMs).toISOString().slice(0, 16);
}

function formatTime(value: unknown): string {
  if (!value) return "—";
  const asAny = value as { toISOString?: () => string };
  return typeof asAny.toISOString === "function"
    ? asAny.toISOString().replace("T", " ").slice(0, 19)
    : String(value);
}

function toCsv(rows: api.StalePendingRow[]): string {
  const header = [
    "eventId",
    "sessionId",
    "eventTypeId",
    "rowMessageType",
    "staleMessageId",
    "rowEnqueuedTimeUtc",
    "rowUpdatedAt",
    "verdict",
    "responseMessageId",
    "responseEnqueuedTimeUtc",
    "detail",
  ];
  const escape = (value: unknown) => `"${String(value ?? "").replace(/"/g, '""')}"`;
  const lines = rows.map((row) =>
    [
      row.eventId,
      row.sessionId,
      row.eventTypeId,
      row.rowMessageType,
      row.staleMessageId,
      formatTime(row.rowEnqueuedTimeUtc),
      formatTime(row.rowUpdatedAt),
      row.verdict,
      row.responseMessageId,
      formatTime(row.responseEnqueuedTimeUtc),
      row.detail,
    ]
      .map(escape)
      .join(","),
  );
  return [header.join(","), ...lines].join("\n");
}

interface RepairTotals {
  processed: number;
  succeeded: number;
  failed: number;
  skipped: number;
  errors: string[];
  batches: number;
}

function addRound(totals: RepairTotals, round: api.StalePendingReconcileResult): RepairTotals {
  return {
    processed: totals.processed + (round.processed ?? 0),
    succeeded: totals.succeeded + (round.succeeded ?? 0),
    failed: totals.failed + (round.failed ?? 0),
    skipped: totals.skipped + (round.skipped ?? 0),
    errors: [...totals.errors, ...(round.errors ?? [])],
    batches: totals.batches + 1,
  };
}

export function StalePendingReconcileCard({
  endpoints,
  initialEndpoint,
}: {
  endpoints: EndpointOption[];
  /** Pre-selects the endpoint, e.g. from an Endpoints table row action. */
  initialEndpoint?: string;
}) {
  const [selected, setSelected] = useState<string[]>(initialEndpoint ? [initialEndpoint] : []);
  const [cutoff, setCutoff] = useState(defaultCutoff);
  const [note, setNote] = useState("");
  const [preview, setPreview] = useState<api.StalePendingPreview | null>(null);
  const [result, setResult] = useState<RepairTotals | null>(null);
  const [error, setError] = useState<string | null>(null);
  const [loading, setLoading] = useState(false);
  const [executing, setExecuting] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);

  const endpointId = selected[0] ?? "";
  const rows = preview?.rows ?? [];
  const repairable = preview?.repairable ?? 0;
  const truncated = preview?.truncated ?? false;
  // A truncated preview lists only the first page of candidates; the repair runs past it.
  const repairLabel = truncated ? "Repair all repairable rows" : `Repair ${repairable} rows`;

  function buildRequest(maxRepairs?: number): api.StalePendingReconcileRequest {
    const request = new api.StalePendingReconcileRequest();
    const before = toIsoUtc(cutoff);
    if (before) request.enqueuedBefore = before as never;
    if (note) request.note = note;
    if (maxRepairs) request.maxRepairs = maxRepairs;
    return request;
  }

  function describeError(caught: unknown): string {
    const swagger = caught as { status?: number; message?: string };
    if (swagger?.status === 400) {
      return "The cut-off must be at least 15 minutes in the past.";
    }
    if (swagger?.status === 403) {
      return "Reconciling stale Pending rows requires the site Owner role.";
    }
    return swagger?.message ?? "The request failed.";
  }

  async function runPreview() {
    if (!endpointId) return;
    setLoading(true);
    setError(null);
    setResult(null);
    try {
      const client = new api.Client(api.CookieAuth());
      setPreview(await client.postAdminStalePendingPreview(endpointId, buildRequest()));
    } catch (caught) {
      setPreview(null);
      setError(describeError(caught));
    } finally {
      setLoading(false);
    }
  }

  /**
   * Repairs in batches until the server reports a short round. Each round is its own audited
   * request; a round that repairs nothing (every row skipped) ends the loop, since the next
   * round would only meet the same rows again.
   */
  async function runRepair() {
    if (!endpointId) return;
    setShowConfirm(false);
    setExecuting(true);
    setError(null);
    let totals: RepairTotals = {
      processed: 0,
      succeeded: 0,
      failed: 0,
      skipped: 0,
      errors: [],
      batches: 0,
    };
    setResult(totals);
    try {
      const client = new api.Client(api.CookieAuth());
      let round: api.StalePendingReconcileResult;
      do {
        round = await client.postAdminStalePendingReconcile(
          endpointId,
          buildRequest(REPAIR_BATCH_SIZE),
        );
        totals = addRound(totals, round);
        setResult(totals);
      } while (
        (round.processed ?? 0) >= REPAIR_BATCH_SIZE &&
        (round.succeeded ?? 0) > 0 &&
        totals.batches < MAX_REPAIR_BATCHES
      );
      if (totals.batches >= MAX_REPAIR_BATCHES) {
        setError(`Stopped after ${MAX_REPAIR_BATCHES} batches. Preview again to continue.`);
      }
      // The repaired rows are no longer Pending: re-read so the table matches the store.
      setPreview(await client.postAdminStalePendingPreview(endpointId, buildRequest()));
    } catch (caught) {
      setError(describeError(caught));
    } finally {
      setExecuting(false);
    }
  }

  function downloadCsv() {
    const blob = new Blob([toCsv(rows)], { type: "text/csv;charset=utf-8" });
    const objectUrl = URL.createObjectURL(blob);
    try {
      // The anchor has to be in the document for a programmatic click to start a
      // download in Firefox — same pattern as asyncapi-export.tsx.
      const anchor = document.createElement("a");
      anchor.href = objectUrl;
      anchor.download = `stale-pending-${endpointId}.csv`;
      document.body.appendChild(anchor);
      anchor.click();
      anchor.remove();
    } finally {
      URL.revokeObjectURL(objectUrl);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Reconcile Stale Pending</CardTitle>
        <CardDescription>
          Re-apply the outcome the Resolver already stored to rows a redelivered request copy
          left Pending. Never resubmits, never skips, never invents a status.
        </CardDescription>
      </CardHeader>
      <CardContent>
        <div className="space-y-4">
          {/* Filters and Preview on one line: the button sits at the end of the row it acts on. */}
          <div className="grid grid-cols-1 gap-4 @2xl:grid-cols-[minmax(0,1.2fr)_minmax(0,1fr)_minmax(0,1fr)_auto] @2xl:items-end">
            <div>
              <label className="block text-xs font-medium text-muted-foreground mb-1">
                Endpoint
              </label>
              <Combobox
                options={endpoints}
                value={selected}
                onChange={(v) => {
                  setSelected(v);
                  setPreview(null);
                  setResult(null);
                  setError(null);
                }}
                placeholder="Select endpoint..."
                multiple={false}
              />
            </div>
            <div>
              <label className="block text-xs font-medium text-muted-foreground mb-1">
                Enqueued before (local, sent as UTC)
              </label>
              <Input
                type="datetime-local"
                value={cutoff}
                onChange={(e) => setCutoff(e.target.value)}
              />
            </div>
            <div>
              <label className="block text-xs font-medium text-muted-foreground mb-1">
                Note (audited)
              </label>
              <Input
                value={note}
                onChange={(e) => setNote(e.target.value)}
                placeholder="Incident or ticket reference"
              />
            </div>
            <Button
              onClick={runPreview}
              disabled={!endpointId || loading}
              isLoading={loading}
              variant="outline"
            >
              Preview
            </Button>
          </div>

          {error && (
            <p role="alert" className="text-sm text-status-danger">
              {error}
            </p>
          )}

          {preview && (
            <div className="space-y-4">
              <div className="grid grid-cols-3 gap-3">
                <StatTile label="Candidates" value={preview.candidates ?? 0} tone="muted" />
                <StatTile label="Repairable" value={repairable} />
                <StatTile
                  label="Operator decision"
                  value={(preview.candidates ?? 0) - repairable}
                  tone="warning"
                />
              </div>

              {/* Actions sit above the table so a 500-row preview never hides them below the fold. */}
              <div className="flex flex-wrap items-center gap-3">
                <Button
                  colorScheme="red"
                  size="sm"
                  disabled={repairable === 0 || executing}
                  isLoading={executing}
                  onClick={() => setShowConfirm(true)}
                >
                  {repairLabel}
                </Button>
                <Button
                  variant="outline"
                  size="sm"
                  onClick={downloadCsv}
                  disabled={rows.length === 0}
                >
                  Download CSV
                </Button>
                <p className="text-xs text-muted-foreground">
                  {truncated
                    ? `Listing the first ${rows.length} candidates. Repair runs in batches of ${REPAIR_BATCH_SIZE} until every repairable row before the cut-off is done.`
                    : rows.length === 0
                      ? "No Pending rows before that cut-off."
                      : `${rows.length} candidates listed.`}
                </p>
              </div>

              {result && (
                <div className="space-y-1">
                  <OperationProgress
                    processed={result.processed}
                    succeeded={result.succeeded}
                    failed={result.failed}
                    errors={result.errors}
                    isComplete={!executing}
                  />
                  <p className="text-xs text-muted-foreground">
                    {result.batches} {result.batches === 1 ? "batch" : "batches"}
                    {result.skipped > 0 && ` · ${result.skipped} skipped`}
                  </p>
                </div>
              )}

              {rows.length > 0 && (
                <div className="max-h-128 overflow-auto rounded-nb-md border border-border">
                  <table className="w-full text-sm">
                    <thead className="sticky top-0 bg-card shadow-[inset_0_-1px_0_0] shadow-border">
                      <tr className="text-left text-xs uppercase text-muted-foreground">
                        <th className="px-3 py-2">Verdict</th>
                        <th className="px-3 py-2">Event</th>
                        <th className="px-3 py-2">Stale message</th>
                        <th className="px-3 py-2">Row enqueued</th>
                        <th className="px-3 py-2">Response</th>
                        <th className="px-3 py-2">Why</th>
                      </tr>
                    </thead>
                    <tbody>
                      {rows.map((row) => (
                        <tr
                          key={`${row.eventId}-${row.sessionId ?? ""}`}
                          className="group border-t border-border"
                        >
                          <td className="px-3 py-1.5 align-top">
                            <Badge variant={VERDICT_TONE[row.verdict ?? ""] ?? "default"} size="sm">
                              {row.verdict}
                            </Badge>
                          </td>
                          <td className="px-3 py-1.5 align-top">
                            <TruncatedGuid guid={row.eventId} />
                          </td>
                          <td className="px-3 py-1.5 align-top">
                            <TruncatedGuid guid={row.staleMessageId} />
                          </td>
                          <td className="px-3 py-1.5 align-top whitespace-nowrap font-mono text-[11.5px]">
                            {formatTime(row.rowEnqueuedTimeUtc)}
                          </td>
                          <td className="px-3 py-1.5 align-top">
                            <TruncatedGuid guid={row.responseMessageId} />
                          </td>
                          <td className="min-w-[18rem] px-3 py-1.5 align-top font-mono text-[11.5px] text-muted-foreground wrap-break-word">
                            {row.detail}
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </div>
          )}

          <ConfirmDestructiveAction
            isOpen={showConfirm}
            onClose={() => setShowConfirm(false)}
            onConfirm={runRepair}
            title="Reconcile Stale Pending"
            description={
              truncated
                ? `This will replace every Repairable Pending row on "${endpointId}" enqueued before the cut-off with the Completed outcome the Resolver already stored, in batches of ${REPAIR_BATCH_SIZE}, including rows beyond the ${rows.length} listed. Every repair is audited.`
                : `This will replace ${repairable} Pending row(s) on "${endpointId}" with the Completed outcome the Resolver already stored. Every repair is audited.`
            }
            confirmText={endpointId}
            confirmLabel={repairLabel}
            tone="primary"
            isLoading={executing}
          />
        </div>
      </CardContent>
    </Card>
  );
}

export default StalePendingReconcileCard;
