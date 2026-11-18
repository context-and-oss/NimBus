import { useEffect, useState } from "react";
import { Link } from "react-router-dom";
import * as api from "api-client";
import { Button } from "components/ui/button";
import { Input } from "components/ui/input";
import { Toggle } from "components/ui/toggle";
import { Badge } from "components/ui/badge";
import { Modal, ModalBody, ModalFooter, ModalHeader } from "components/ui/modal";
import { Card, CardContent, CardDescription, CardHeader, CardTitle } from "components/ui/card";
import {
  LIMITS,
  blockedMessage,
  clamp,
  problemMessage,
  useSimulationStatus,
} from "components/simulate/simulation-utils";
import {
  PanelFooter,
  PanelInspector,
  PanelSection,
  useInPanel,
  usePanelDirty,
} from "components/settings/panel-frame";
import { Inspector, InspectorBlock } from "components/workspace/workspace";
import {
  RowSelect,
  selectableRowClass,
  WORKSPACE_HEAD_ROW,
  WORKSPACE_TH,
  WorkspaceTableFrame,
} from "components/workspace/selectable-row";

interface Draft {
  enabled: boolean;
  autoStopMinutes: number;
  rateCeilingPerMinute: number;
  ownedEndpoints: string[];
}

const toDraft = (status: api.SimulationStatus): Draft => ({
  enabled: status.settings?.enabled ?? false,
  autoStopMinutes: status.settings?.autoStopMinutes ?? 60,
  rateCeilingPerMinute: status.settings?.rateCeilingPerMinute ?? 600,
  ownedEndpoints: [...(status.settings?.ownedEndpoints ?? [])],
});

/** Number of draft fields that differ from the saved settings; each ownership flip counts once. */
const countChanges = (saved: Draft, draft: Draft): number => {
  const owned = new Set(saved.ownedEndpoints);
  const next = new Set(draft.ownedEndpoints);
  return (
    (["enabled", "autoStopMinutes", "rateCeilingPerMinute"] as const).filter((k) => saved[k] !== draft[k]).length +
    [...owned].filter((e) => !next.has(e)).length +
    [...next].filter((e) => !owned.has(e)).length
  );
};

const LIVE_INSTANCE_WARNING =
  "A live instance answered a heartbeat recently and may be competing for this subscription.";

const ownershipBadge = (owned: boolean) => (
  <Badge variant={owned ? "primary" : "secondary"}>{owned ? "Simulated" : "External"}</Badge>
);

/**
 * Settings → Simulation. Switches simulate mode on, bounds it, and hands
 * consuming endpoints to the simulator. Ownership is explicit and defaults to
 * External: a simulated handler competes for the endpoint's real subscription.
 */
export default function SimulationSettings() {
  const { client, status, accept, error: loadError, loaded, refresh } = useSimulationStatus();
  const [draft, setDraft] = useState<Draft>();
  const [confirmOwn, setConfirmOwn] = useState<string>();
  const [selectedId, setSelectedId] = useState<string>();
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string>();
  const [message, setMessage] = useState<string>();

  const inPanel = useInPanel();

  useEffect(() => {
    if (status && !draft) setDraft(toDraft(status));
  }, [status, draft]);

  usePanelDirty(status && draft ? countChanges(toDraft(status), draft) : 0);

  if (!status || !draft) {
    return (
      <div className="space-y-3">
        <p role={loadError ? "alert" : "status"}>
          {loadError ?? (loaded ? "Simulation status is unavailable." : "Loading simulation settings…")}
        </p>
        {loadError && <Button onClick={() => void refresh()}>Try again</Button>}
      </div>
    );
  }

  const allowed = status.allowed ?? false;
  const disabled = !allowed || saving;
  const maxCeiling = status.maxRateCeilingPerMinute ?? 1200;
  const consumers = (status.endpoints ?? []).filter((e) => (e.consumes?.length ?? 0) > 0);
  const ownershipLocked = ["running", "pausing", "stopping"].includes((status.state ?? "").toLowerCase());
  const selected = selectedId === undefined ? undefined : consumers.find((e) => (e.endpointId ?? "") === selectedId);
  // The inspector follows the draft, like the row; it says so while that differs from what is saved.
  const selectedOwned = selected !== undefined && draft.ownedEndpoints.includes(selected.endpointId ?? "");
  const selectedSavedOwned =
    selected !== undefined && (status.settings?.ownedEndpoints ?? []).includes(selected.endpointId ?? "");

  const edit = <K extends keyof Draft>(key: K, value: Draft[K]) => {
    setDraft((current) => (current ? { ...current, [key]: value } : current));
    setMessage(undefined);
  };

  const setOwned = (endpointId: string, owned: boolean) =>
    edit(
      "ownedEndpoints",
      owned
        ? [...draft.ownedEndpoints.filter((e) => e !== endpointId), endpointId]
        : draft.ownedEndpoints.filter((e) => e !== endpointId),
    );

  async function save() {
    if (!draft) return;
    setSaving(true);
    setError(undefined);
    try {
      const next = await client.putAdminSimulationSettings(
        api.SimulationSettings.fromJS({
          enabled: draft.enabled,
          autoStopMinutes: clamp(draft.autoStopMinutes, LIMITS.autoStopMin, LIMITS.autoStopMax),
          rateCeilingPerMinute: clamp(draft.rateCeilingPerMinute, 1, maxCeiling),
          ownedEndpoints: draft.ownedEndpoints,
        }),
      );
      accept(next);
      setDraft(toDraft(next));
      setMessage("Simulation settings saved. Ownership changes apply at the next Start.");
    } catch (cause) {
      setError(problemMessage(cause));
    } finally {
      setSaving(false);
    }
  }

  const actions = (
    <div className="flex gap-2">
      <Button type="button" variant="outline" disabled={saving} onClick={() => setDraft(toDraft(status))}>
        Reset
      </Button>
      <Button type="button" disabled={disabled} onClick={() => void save()}>
        {saving ? "Saving…" : "Save simulation settings"}
      </Button>
    </div>
  );
  const productionNames = status.productionNames ?? [];
  const allowedEnvironments = status.allowedEnvironments ?? [];

  return (
    <div className="w-full min-w-0 space-y-6">
      <header className="flex flex-wrap items-start justify-between gap-3">
        <div>
          {!inPanel && <h2 className="text-xl font-semibold">Simulation</h2>}
          <p className="text-muted-foreground">
            Drive synthetic traffic through the real platform from this WebApp. Never available in production.
          </p>
        </div>
        {allowed && status.enabled && (
          <Link to="/Simulate" className="text-sm font-semibold text-primary">
            Open Simulate →
          </Link>
        )}
      </header>

      {!allowed && (
        <p role="alert" className="rounded-md border border-status-danger/40 bg-status-danger/10 p-3 text-sm">
          {blockedMessage(status)}
        </p>
      )}
      {error && (
        <p role="alert" className="text-status-danger">
          {error}
        </p>
      )}
      {message && (
        <p role="status" className="text-status-success">
          {message}
        </p>
      )}

      <div className={inPanel ? "space-y-6" : "grid gap-6 xl:grid-cols-[minmax(0,1.5fr)_minmax(0,1fr)]"}>
        <div className={inPanel ? "min-w-0" : "min-w-0 space-y-5"}>
          <fieldset disabled={disabled} className="min-w-0">
            <PanelSection tab="mode">
              <Card>
                <CardHeader>
                  <CardTitle>01 · Simulate mode</CardTitle>
                  <CardDescription>State is kept in this WebApp instance and resets on restart.</CardDescription>
                </CardHeader>
                <CardContent className="space-y-4">
                  <div className="flex items-center justify-between gap-4">
                    <div>
                      <p className="text-sm font-semibold">Enable simulate mode</p>
                      <p className="text-xs text-muted-foreground">
                        Adds the Simulate page. Disabling stops a running simulation.
                      </p>
                    </div>
                    <Toggle
                      aria-label="Enable simulate mode"
                      checked={draft.enabled}
                      disabled={disabled}
                      onChange={(value) => edit("enabled", value)}
                    />
                  </div>
                  <div className="grid gap-4 sm:grid-cols-3">
                    <label className="text-sm">
                      Auto-stop (minutes)
                      <Input
                        type="number"
                        min={LIMITS.autoStopMin}
                        max={LIMITS.autoStopMax}
                        value={draft.autoStopMinutes}
                        onChange={(e) => edit("autoStopMinutes", Number(e.target.value))}
                        onBlur={(e) =>
                          edit("autoStopMinutes", clamp(Number(e.target.value), LIMITS.autoStopMin, LIMITS.autoStopMax))
                        }
                      />
                    </label>
                    <label className="text-sm">
                      Rate ceiling (per minute)
                      <Input
                        type="number"
                        min={1}
                        max={maxCeiling}
                        value={draft.rateCeilingPerMinute}
                        onChange={(e) => edit("rateCeilingPerMinute", Number(e.target.value))}
                        onBlur={(e) => edit("rateCeilingPerMinute", clamp(Number(e.target.value), 1, maxCeiling))}
                      />
                      <span className="text-xs text-muted-foreground">Shared by every publisher. Max {maxCeiling}.</span>
                    </label>
                    <label className="text-sm">
                      Session prefix
                      <Input value={status.sessionPrefix ?? ""} readOnly />
                      <span className="text-xs text-muted-foreground">Marks simulated sessions and correlations.</span>
                    </label>
                  </div>
                </CardContent>
              </Card>
            </PanelSection>
          </fieldset>

          {/* Outside the fieldset: a blocked or saving page must still let a row be selected and read. */}
          <PanelSection tab="endpoints">
            <section aria-label="Consuming endpoints" className="min-w-0 space-y-3">
              <div className="space-y-1">
                {!inPanel && <h3 className="m-0 text-lg font-semibold">02 · Consuming endpoints</h3>}
                <p className="m-0 text-sm text-muted-foreground">
                  External endpoints are published to but handled by their own process. Only endpoints the simulator
                  owns get a simulated handler.
                </p>
              </div>
              {ownershipLocked && (
                <p role="status" className="text-sm text-status-warning">
                  Ownership is locked while the simulation runs. Pause or stop it to change ownership.
                </p>
              )}
              <WorkspaceTableFrame>
                <table className="w-full min-w-[28rem] text-sm" aria-label="Consuming endpoints">
                  <thead>
                    <tr className={WORKSPACE_HEAD_ROW}>
                      <th scope="col" className={WORKSPACE_TH}>Endpoint</th>
                      <th scope="col" className={WORKSPACE_TH}>Ownership</th>
                      <th scope="col" className={WORKSPACE_TH}>Simulator owns</th>
                    </tr>
                  </thead>
                  <tbody>
                    {consumers.map((endpoint) => {
                      const id = endpoint.endpointId ?? "";
                      const owned = draft.ownedEndpoints.includes(id);
                      const isSelected = id === selectedId;
                      return (
                        <tr key={id} className={selectableRowClass(isSelected)}>
                          <td className="px-4">
                            <RowSelect
                              mono
                              selected={isSelected}
                              onSelect={() => setSelectedId(id)}
                              after={
                                endpoint.liveInstanceWarning && (
                                  <span
                                    title={LIVE_INSTANCE_WARNING}
                                    className="whitespace-nowrap text-[11px] font-semibold text-status-warning-ink"
                                  >
                                    Live instance
                                  </span>
                                )
                              }
                            >
                              {id}
                            </RowSelect>
                          </td>
                          <td className="px-4 py-2">{ownershipBadge(owned)}</td>
                          <td className="px-4 py-2">
                            <input
                              type="checkbox"
                              aria-label={`Simulator owns ${id}`}
                              checked={owned}
                              disabled={disabled || ownershipLocked}
                              onChange={(e) => (e.target.checked ? setConfirmOwn(id) : setOwned(id, false))}
                            />
                          </td>
                        </tr>
                      );
                    })}
                    {consumers.length === 0 && (
                      <tr>
                        <td colSpan={3} className="py-4 text-center text-muted-foreground">
                          No consuming endpoints.
                        </td>
                      </tr>
                    )}
                  </tbody>
                </table>
              </WorkspaceTableFrame>
            </section>

            <PanelInspector>
              {selected ? (
                <Inspector
                  eyebrow="Endpoint"
                  title={<span className="font-mono text-[16px]">{selected.endpointId}</span>}
                  state={ownershipBadge(selectedOwned)}
                  description={
                    <>
                      {selectedOwned
                        ? "The simulator hosts a simulated handler on this endpoint's real subscription. If the endpoint's own process is running, both compete for the same messages and the results mix."
                        : "This endpoint is published to but handled by its own process. It gets no simulated handler."}
                      {selectedOwned !== selectedSavedOwned && " Not saved yet. Ownership changes apply at the next Start."}
                    </>
                  }
                >
                  {selected.liveInstanceWarning && (
                    <p role="status" className="m-0 text-[13px] text-status-warning-ink">
                      {LIVE_INSTANCE_WARNING}
                    </p>
                  )}
                  <InspectorBlock label="Consumes">
                    <ul className="m-0 list-none divide-y divide-border rounded-nb-md border border-border bg-background p-0">
                      {(selected.consumes ?? []).map((eventType) => (
                        <li key={eventType} className="px-3 py-2 font-mono text-[12px] [overflow-wrap:anywhere]">
                          {eventType}
                        </li>
                      ))}
                    </ul>
                  </InspectorBlock>
                </Inspector>
              ) : (
                <Inspector empty="Select an endpoint to see who owns it." />
              )}
            </PanelInspector>
          </PanelSection>
        </div>

        <PanelSection tab="policy">
          <aside className="min-w-0 space-y-5">
            <Card>
              <CardHeader>
                <CardTitle>Environment policy</CardTitle>
                <CardDescription>
                  Current environment: <span className="font-mono">{status.environment?.trim() || "(not set)"}</span>
                </CardDescription>
              </CardHeader>
              <CardContent>
                <table className="w-full text-sm">
                  <thead>
                    <tr className="text-left text-xs text-muted-foreground">
                      <th className="py-1">Environment</th>
                      <th className="py-1">Simulation</th>
                    </tr>
                  </thead>
                  <tbody>
                    {allowedEnvironments.map((env) => (
                      <tr key={`allowed-${env}`}>
                        <td className="py-1 font-mono">{env}</td>
                        <td className="py-1 text-status-success">allowed</td>
                      </tr>
                    ))}
                    {productionNames.map((env) => (
                      <tr key={`prod-${env}`}>
                        <td className="py-1 font-mono">{env}</td>
                        <td className="py-1 text-status-danger">permanently blocked</td>
                      </tr>
                    ))}
                    <tr>
                      <td className="py-1 font-mono">anything else</td>
                      <td className="py-1 text-muted-foreground">blocked</td>
                    </tr>
                  </tbody>
                </table>
              </CardContent>
            </Card>
          </aside>
        </PanelSection>
      </div>

      {inPanel ? (
        <PanelFooter>{actions}</PanelFooter>
      ) : (
        <footer className="flex flex-wrap justify-between gap-3 border-t pt-4">
          <p className="text-sm text-muted-foreground">Site Owner only · audited · takes effect on this instance</p>
          {actions}
        </footer>
      )}

      <Modal isOpen={confirmOwn !== undefined} onClose={() => setConfirmOwn(undefined)} size="md">
        <ModalHeader onClose={() => setConfirmOwn(undefined)}>Hand {confirmOwn} to the simulator?</ModalHeader>
        <ModalBody className="space-y-2 text-sm">
          <p>
            The simulator will host a handler on <span className="font-mono">{confirmOwn}</span>'s real subscription.
            If the endpoint's own process is running, both compete for the same messages and the results mix.
          </p>
          <p>Only do this when the real subscriber is not running. The change applies at the next Start.</p>
        </ModalBody>
        <ModalFooter>
          <Button variant="outline" onClick={() => setConfirmOwn(undefined)}>
            Cancel
          </Button>
          <Button
            onClick={() => {
              if (confirmOwn) setOwned(confirmOwn, true);
              setConfirmOwn(undefined);
            }}
          >
            Take ownership
          </Button>
        </ModalFooter>
      </Modal>
    </div>
  );
}
