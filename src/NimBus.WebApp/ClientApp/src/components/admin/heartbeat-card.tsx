import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { Link } from "react-router-dom";
import * as api from "api-client";
import { Button } from "components/ui/button";
import { Input } from "components/ui/input";
import { Select } from "components/ui/select";
import { Toggle } from "components/ui/toggle";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "components/ui/card";
import { subscribeHeartbeatUpdates } from "lib/grid-events-connection";
import { formatMoment } from "functions/endpoint.functions";
import {
  PanelFooter,
  PanelInspector,
  PanelSection,
  useInPanel,
  usePanelDirty,
} from "components/settings/panel-frame";
import { Badge } from "components/ui/badge";
import {
  Inspector,
  InspectorTiles,
  type InspectorTile,
} from "components/workspace/workspace";
import {
  RowSelect,
  selectableRowClass,
  WORKSPACE_HEAD_ROW,
  WORKSPACE_TH,
  WorkspaceTableFrame,
} from "components/workspace/selectable-row";
import { cn } from "lib/utils";
import {
  normalizeStatus,
  statusHints,
  statusVariants,
  type HeartbeatStatus,
} from "./heartbeat-status";

/** Fallback poll cadence while the hub is down (see PlatformServicesCard). */
const POLL_MS = 30_000;

const DEFAULT_INTERVAL_SECONDS = 300;
const DEFAULT_TIMEOUT_SECONDS = 60;
type InclusionFilter = "all" | "included" | "excluded";

const activityIcon = (
  <svg
    aria-hidden="true"
    viewBox="0 0 24 24"
    className="h-5 w-5"
    fill="none"
    stroke="currentColor"
    strokeWidth="2"
  >
    <path
      d="M3 12h4l2-7 4 14 2-7h6"
      strokeLinecap="round"
      strokeLinejoin="round"
    />
  </svg>
);

const refreshIcon = (
  <svg
    aria-hidden="true"
    viewBox="0 0 24 24"
    className="h-4 w-4"
    fill="none"
    stroke="currentColor"
    strokeWidth="2"
  >
    <path d="M20 11a8 8 0 1 0-2.3 5.7" strokeLinecap="round" />
    <path d="M20 4v7h-7" strokeLinecap="round" strokeLinejoin="round" />
  </svg>
);

const saveIcon = (
  <svg
    aria-hidden="true"
    viewBox="0 0 24 24"
    className="h-4 w-4"
    fill="none"
    stroke="currentColor"
    strokeWidth="2"
  >
    <path d="M5 4h12l2 2v14H5z" strokeLinejoin="round" />
    <path d="M8 4v6h8V4M8 20v-6h8v6" strokeLinejoin="round" />
  </svg>
);

const sendIcon = (
  <svg
    aria-hidden="true"
    viewBox="0 0 24 24"
    className="h-4 w-4"
    fill="none"
    stroke="currentColor"
    strokeWidth="2"
  >
    <path
      d="m21 3-7.5 18-3.7-7.8L2 9.5z"
      strokeLinecap="round"
      strokeLinejoin="round"
    />
    <path d="m9.8 13.2 4.4-4.4" strokeLinecap="round" />
  </svg>
);

// The schedule fields an operator edits; per-endpoint inclusion saves on click.
const SCHEDULE_FIELDS = [
  "enabled",
  "intervalSeconds",
  "timeoutSeconds",
] as const;

/** Number of schedule fields that differ from the saved settings. */
function countScheduleChanges(
  saved: api.HeartbeatSettings | null,
  draft: api.HeartbeatSettings | null,
): number {
  if (!saved || !draft) return 0;
  return SCHEDULE_FIELDS.filter((field) => saved[field] !== draft[field])
    .length;
}

/** Null means "never configured either way", which is included. */
const isIncluded = (row: api.HeartbeatOverviewRow) =>
  row.isHeartbeatEnabled !== false;

const roundTrip = (row: api.HeartbeatOverviewRow) =>
  row.roundTripMs === undefined || row.roundTripMs === null
    ? "—"
    : `${row.roundTripMs.toLocaleString()} ms`;

const probeTime = (time: api.HeartbeatOverviewRow["lastStartTime"]) =>
  time ? formatMoment(time, true) : "—";

const STATUS_TONE: Record<HeartbeatStatus, InspectorTile["tone"]> = {
  On: "ok",
  Pending: "warn",
  Off: "danger",
  Unsupported: "default",
  Unknown: "default",
};

/**
 * Settings → Heartbeat probing. The scheduled endpoint fan-out: its schedule,
 * a manual send, and the per-endpoint answer table. Adapters answer the probe
 * automatically from the SDK, so a row that never leaves Pending means the
 * endpoint is not draining its subscription.
 */
export default function HeartbeatCard() {
  // `settings` is the draft the form edits; `saved` is what the server holds.
  const [settings, setSettings] = useState<api.HeartbeatSettings | null>(null);
  const [saved, setSaved] = useState<api.HeartbeatSettings | null>(null);
  const savedRef = useRef<api.HeartbeatSettings | null>(null);
  const [overview, setOverview] = useState<api.HeartbeatOverviewRow[]>([]);
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [sending, setSending] = useState(false);
  const [sentCount, setSentCount] = useState<number | null>(null);
  const [busy, setBusy] = useState<Record<string, boolean>>({});
  const [error, setError] = useState<string | null>(null);
  const [live, setLive] = useState(false);
  const [inclusionFilter, setInclusionFilter] =
    useState<InclusionFilter>("all");
  const [selectedId, setSelectedId] = useState<string>();

  const client = useMemo(() => new api.Client(api.CookieAuth()), []);
  const inPanel = useInPanel();

  const changes = countScheduleChanges(saved, settings);
  usePanelDirty(changes);

  // A reload (Refresh, Send now) must not throw away unsaved schedule edits:
  // only the server-owned last-send time is taken into a dirty draft.
  const acceptSaved = useCallback((next: api.HeartbeatSettings) => {
    const previous = savedRef.current;
    savedRef.current = next;
    setSaved(next);
    setSettings((draft) =>
      draft && countScheduleChanges(previous, draft) > 0
        ? new api.HeartbeatSettings({
            ...draft,
            lastSentAtUtc: next.lastSentAtUtc,
          })
        : next,
    );
  }, []);

  const loadOverview = useCallback(async () => {
    setOverview(await client.getAdminHeartbeatOverview());
  }, [client]);

  const loadAll = useCallback(async () => {
    try {
      const [nextSettings, nextOverview] = await Promise.all([
        client.getAdminHeartbeatSettings(),
        client.getAdminHeartbeatOverview(),
      ]);
      acceptSaved(nextSettings);
      setOverview(nextOverview);
      setError(null);
    } catch (err: unknown) {
      // Without this the table falls back to its empty state and a 500 is
      // indistinguishable from "this platform has no endpoints".
      setError(
        err instanceof Error ? err.message : "Failed to load heartbeat state",
      );
    } finally {
      setLoading(false);
    }
  }, [client, acceptSaved]);

  useEffect(() => {
    void loadAll();
  }, [loadAll]);

  useEffect(() => {
    const subscription = subscribeHeartbeatUpdates(
      () => {
        void loadOverview().catch(() => undefined);
      },
      (state) => setLive(state === "connected"),
    );
    return () => subscription.dispose();
  }, [loadOverview]);

  useEffect(() => {
    if (live) return;
    const handle = window.setInterval(() => {
      void loadOverview().catch(() => undefined);
    }, POLL_MS);
    return () => window.clearInterval(handle);
  }, [live, loadOverview]);

  function patchSettings(patch: Partial<api.IHeartbeatSettings>) {
    setSettings(
      (old) => new api.HeartbeatSettings({ ...(old ?? {}), ...patch }),
    );
  }

  // Posted exactly as typed — the server owns the clamps (interval ≥ 30s,
  // timeout in [5s, interval]) and returns the values it actually stored, so
  // an out-of-range entry snaps back visibly instead of being silently edited
  // under the operator's cursor.
  async function saveSettings() {
    if (!settings) return;
    setSaving(true);
    try {
      const stored = await client.putAdminHeartbeatSettings(settings);
      savedRef.current = stored;
      setSaved(stored);
      setSettings(stored);
      setError(null);
    } catch (err: unknown) {
      setError(
        err instanceof Error
          ? err.message
          : "Failed to save heartbeat settings",
      );
    } finally {
      setSaving(false);
    }
  }

  async function sendNow() {
    setSending(true);
    setSentCount(null);
    try {
      const result = await client.postAdminHeartbeatSend();
      setSentCount(result.count ?? 0);
      await loadAll();
    } catch (err: unknown) {
      setError(err instanceof Error ? err.message : "Failed to send heartbeat");
    } finally {
      setSending(false);
    }
  }

  async function setEndpointEnabled(endpointId: string, enabled: boolean) {
    setBusy((prev) => ({ ...prev, [endpointId]: true }));
    try {
      await client.putAdminHeartbeatEndpointEnabled(
        endpointId,
        new api.HeartbeatEndpointEnabledRequest({ enabled }),
      );
      await loadOverview();
      setError(null);
    } catch (err: unknown) {
      setError(
        err instanceof Error
          ? err.message
          : `Failed to update heartbeat for ${endpointId}`,
      );
    } finally {
      setBusy((prev) => ({ ...prev, [endpointId]: false }));
    }
  }

  const enabled = settings?.enabled ?? false;
  const filteredOverview = overview.filter((row) => {
    if (inclusionFilter === "all") return true;
    const included = isIncluded(row);
    return inclusionFilter === "included" ? included : !included;
  });
  // Taken from the filtered rows, so the inspector never shows an endpoint
  // the table has hidden.
  const selected =
    selectedId === undefined
      ? undefined
      : filteredOverview.find((row) => (row.endpointId ?? "") === selectedId);
  const selectedStatus = normalizeStatus(selected?.status);

  return (
    <Card>
      <CardHeader className="px-5 py-4">
        {!inPanel && (
          <div className="flex items-center gap-2">
            {activityIcon}
            <CardTitle className="text-xl">Heartbeat</CardTitle>
          </div>
        )}
        <CardDescription>
          Endpoint health, round-trip latency, and SDK version.
        </CardDescription>
      </CardHeader>
      <CardContent className="space-y-5 p-5">
        {error && (
          <div
            role="alert"
            className="bg-status-danger-50 border border-status-danger/30 dark:bg-red-950/30 dark:border-red-900/60 rounded-nb-md p-4 text-status-danger-ink dark:text-red-200"
          >
            {error}
          </div>
        )}

        <PanelSection tab="probing">
          <div className="space-y-4">
            <div className="grid gap-4 lg:grid-cols-[minmax(15rem,1fr)_13rem_13rem_auto_auto_auto] lg:items-end">
              <div className="flex h-12 items-center gap-3 rounded-nb-md border border-border bg-muted/30 px-4">
                <Toggle
                  checked={enabled}
                  disabled={loading || saving}
                  onChange={(next) => patchSettings({ enabled: next })}
                  aria-label="Scheduled heartbeat enabled"
                />
                <span className="font-semibold">Enabled</span>
              </div>

              <label className="text-sm font-medium text-foreground">
                Interval seconds
                <Input
                  type="number"
                  min={30}
                  className="mt-1 h-12 w-full"
                  value={settings?.intervalSeconds ?? DEFAULT_INTERVAL_SECONDS}
                  disabled={loading || saving}
                  onChange={(event) =>
                    patchSettings({
                      intervalSeconds: Number(event.currentTarget.value),
                    })
                  }
                />
              </label>

              <label className="text-sm font-medium text-foreground">
                Timeout seconds
                <Input
                  type="number"
                  min={5}
                  className="mt-1 h-12 w-full"
                  value={settings?.timeoutSeconds ?? DEFAULT_TIMEOUT_SECONDS}
                  disabled={loading || saving}
                  onChange={(event) =>
                    patchSettings({
                      timeoutSeconds: Number(event.currentTarget.value),
                    })
                  }
                />
              </label>

              <PanelFooter>
                <Button
                  variant="ghost"
                  colorScheme="gray"
                  size={inPanel ? "md" : "lg"}
                  onClick={() => setSettings(saved)}
                  disabled={changes === 0 || saving}
                >
                  Discard
                </Button>
                <Button
                  variant={inPanel ? "solid" : "outline"}
                  colorScheme={inPanel ? "primary" : "gray"}
                  size={inPanel ? "md" : "lg"}
                  leftIcon={saveIcon}
                  onClick={() => void saveSettings()}
                  disabled={!settings || saving}
                  isLoading={saving}
                >
                  Save
                </Button>
              </PanelFooter>

              <Button
                colorScheme="primary"
                size="lg"
                leftIcon={sendIcon}
                onClick={() => void sendNow()}
                disabled={sending}
                isLoading={sending}
              >
                Send now
              </Button>
            </div>

            <p className="text-sm text-muted-foreground">
              Choose which endpoints are probed. Live status, uptime and gaps
              are on the{" "}
              <Link className="text-primary hover:underline" to="/Heartbeat">
                Heartbeat page
              </Link>
              .
            </p>

            {sentCount !== null && (
              <p className="text-xs text-muted-foreground">
                Heartbeat sent to {sentCount} endpoint(s).
              </p>
            )}
          </div>
        </PanelSection>

        <PanelSection tab="endpoints">
          <div className="flex flex-wrap items-center justify-between gap-3">
            <p className="text-sm text-muted-foreground">
              Last scheduled send:{" "}
              {settings?.lastSentAtUtc
                ? formatMoment(settings.lastSentAtUtc)
                : "never"}
            </p>
            <div className="flex items-center gap-3">
              <Select
                aria-label="Filter endpoints"
                className="h-10 w-44"
                value={inclusionFilter}
                onChange={(event) =>
                  setInclusionFilter(
                    event.currentTarget.value as InclusionFilter,
                  )
                }
                options={[
                  { value: "all", label: "All endpoints" },
                  { value: "included", label: "Included" },
                  { value: "excluded", label: "Excluded" },
                ]}
              />
              <Button
                variant="ghost"
                colorScheme="gray"
                size="sm"
                leftIcon={refreshIcon}
                onClick={() => void loadAll()}
                disabled={loading}
              >
                Refresh
              </Button>
            </div>
          </div>

          <WorkspaceTableFrame>
            <table
              className="w-full min-w-[32rem] text-sm"
              aria-label="Heartbeat endpoints"
            >
              <thead>
                <tr className={WORKSPACE_HEAD_ROW}>
                  <th scope="col" className={WORKSPACE_TH}>
                    Endpoint ↑
                  </th>
                  <th scope="col" className={WORKSPACE_TH}>
                    Status
                  </th>
                  <th scope="col" className={cn(WORKSPACE_TH, "text-right")}>
                    Round trip
                  </th>
                  <th scope="col" className={cn(WORKSPACE_TH, "text-right")}>
                    Included
                  </th>
                </tr>
              </thead>
              <tbody>
                {filteredOverview.map((row) => {
                  const included = isIncluded(row);
                  const endpointId = row.endpointId ?? "";
                  const isBusy = busy[endpointId] ?? false;
                  const isSelected = endpointId === selectedId;
                  const status = normalizeStatus(row.status);
                  return (
                    <tr
                      key={endpointId}
                      className={selectableRowClass(isSelected)}
                    >
                      <td className="px-4">
                        <RowSelect
                          mono
                          selected={isSelected}
                          onSelect={() => setSelectedId(endpointId)}
                        >
                          {endpointId}
                        </RowSelect>
                      </td>
                      <td className="px-4 py-2">
                        <Badge variant={statusVariants[status]}>{status}</Badge>
                      </td>
                      <td className="px-4 py-2 text-right tabular-nums">
                        {roundTrip(row)}
                      </td>
                      <td className="px-4 py-2 text-right">
                        <Toggle
                          checked={included}
                          onChange={(next) =>
                            void setEndpointEnabled(endpointId, next)
                          }
                          aria-label={`Include ${endpointId} in heartbeat probes`}
                          disabled={endpointId === "" || isBusy}
                        />
                      </td>
                    </tr>
                  );
                })}
                {filteredOverview.length === 0 && (
                  <tr>
                    <td
                      colSpan={4}
                      className="p-6 text-center text-muted-foreground"
                    >
                      {/* Never claim "no endpoints" when the load failed — the
                        banner above already says what actually happened. */}
                      {loading
                        ? "Loading…"
                        : error
                          ? "—"
                          : overview.length === 0
                            ? "No endpoints."
                            : "No matching endpoints."}
                    </td>
                  </tr>
                )}
              </tbody>
            </table>
          </WorkspaceTableFrame>

          <PanelInspector>
            {selected ? (
              <Inspector
                eyebrow="Endpoint"
                title={
                  <span className="font-mono text-[16px]">
                    {selected.endpointId}
                  </span>
                }
                state={
                  isIncluded(selected) ? (
                    <Badge variant="success">Included</Badge>
                  ) : (
                    <Badge variant="secondary">Excluded</Badge>
                  )
                }
                description={
                  isIncluded(selected)
                    ? statusHints[selectedStatus]
                    : "Excluded from heartbeat probes. The figures below are from its last probe."
                }
                onClose={() => setSelectedId(undefined)}
              >
                <InspectorTiles
                  label="Last probe"
                  tiles={[
                    {
                      label: "Status",
                      value: selectedStatus,
                      tone: STATUS_TONE[selectedStatus],
                    },
                    { label: "Round trip", value: roundTrip(selected) },
                    { label: "Sent", value: probeTime(selected.lastStartTime) },
                    {
                      label: "Answer received",
                      value: probeTime(selected.lastReceivedTime),
                    },
                    {
                      label: "SDK version",
                      value: selected.sdkVersion ?? "—",
                    },
                  ]}
                />
                <p className="m-0 text-[12.5px] text-muted-foreground">
                  Uptime and gaps are on the{" "}
                  <Link
                    className="text-primary hover:underline"
                    to="/Heartbeat"
                  >
                    Heartbeat page
                  </Link>
                  .
                </p>
              </Inspector>
            ) : (
              <Inspector empty="Select an endpoint to see its last probe." />
            )}
          </PanelInspector>
        </PanelSection>
      </CardContent>
    </Card>
  );
}
