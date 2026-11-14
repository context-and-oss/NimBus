import { useState } from "react";
import * as api from "api-client";
import Page from "components/page";
import NotFoundPage from "components/not-found-page";
import Loading from "components/loading/loading";
import { Button } from "components/ui/button";
import { useAccess } from "hooks/use-access";
import { SettingsLink } from "components/settings/settings-link";
import StatusStrip from "components/simulate/status-strip";
import ScenarioBar from "components/simulate/scenario-bar";
import SpeedControl from "components/simulate/speed-control";
import PublishersCard from "components/simulate/publishers-card";
import SubscribersCard from "components/simulate/subscribers-card";
import FailureModeDialog from "components/simulate/failure-mode-dialog";
import LiveFeed from "components/simulate/live-feed";
import { buildConfig, problemMessage, useSimulationStatus } from "components/simulate/simulation-utils";

const POLL_MS = 2_000;
const NOT_FOUND = "The page you are looking for does not exist.";

/**
 * Simulate: drives synthetic traffic through the real platform. Site Owners
 * only, and only while simulate mode is enabled in an allowed environment;
 * anyone else gets the not-found page.
 */
export default function Simulate() {
  const { access } = useAccess();
  if (!access) return <Loading />;
  if (!access.canManageAccessControl) return <NotFoundPage errMsg={NOT_FOUND} />;
  return <SimulateConsole />;
}

function SimulateConsole() {
  const { client, status, accept, error: loadError, loaded } = useSimulationStatus({ pollMs: POLL_MS });
  const [busy, setBusy] = useState(false);
  const [error, setError] = useState<string>();
  const [editing, setEditing] = useState<string>();

  if (!status) {
    if (loaded && loadError) return <NotFoundPage errMsg={NOT_FOUND} />;
    return <Loading />;
  }
  if (!status.allowed || !status.enabled) return <NotFoundPage errMsg={NOT_FOUND} />;

  const state = (status.state ?? "stopped").toLowerCase();
  const transitioning = state === "pausing" || state === "stopping";

  async function run(action: () => Promise<api.SimulationStatus>): Promise<boolean> {
    setBusy(true);
    setError(undefined);
    try {
      accept(await action());
      return true;
    } catch (cause) {
      setError(problemMessage(cause));
      return false;
    } finally {
      setBusy(false);
    }
  }

  const putConfig = (config: api.SimulationConfig) => run(() => client.putAdminSimulationConfig(config));

  // The page is site-Owner only, so its settings link needs no further check.
  const actions = (
    <div className="flex gap-2">
      <SettingsLink to="/Settings/simulation">Simulation settings</SettingsLink>
      <Button
        disabled={busy || transitioning || !(state === "stopped" || state === "paused")}
        onClick={() => void run(() => client.postAdminSimulationStart())}
      >
        {state === "paused" ? "Resume" : "Start"}
      </Button>
      <Button
        variant="outline"
        disabled={busy || transitioning || state !== "running"}
        onClick={() => void run(() => client.postAdminSimulationPause())}
      >
        Pause
      </Button>
      <Button
        variant="outline"
        colorScheme="red"
        disabled={busy || transitioning || !(state === "running" || state === "paused")}
        onClick={() => void run(() => client.postAdminSimulationStop())}
      >
        Stop
      </Button>
    </div>
  );

  return (
    <Page
      title="Simulate"
      subtitle={`Synthetic traffic on ${status.environment ?? "this environment"}. Simulated sessions start with "${status.sessionPrefix ?? "sim-"}".`}
      actions={actions}
    >
      <div className="space-y-6 p-6">
        {error && (
          <p role="alert" className="text-status-danger">
            {error}
          </p>
        )}
        <StatusStrip status={status} />
        <div className="flex flex-wrap items-center justify-between gap-4">
          <ScenarioBar status={status} disabled={busy} onApply={(config) => void putConfig(config)} />
          <div className="flex items-center gap-2">
            <span className="font-mono text-[10px] uppercase tracking-wider text-muted-foreground">Speed</span>
            <SpeedControl
              speed={status.config?.speed ?? 1}
              disabled={busy}
              onChange={(speed) => void putConfig(buildConfig(status, { speed }))}
            />
          </div>
        </div>
        <div className="grid gap-6 xl:grid-cols-2">
          <PublishersCard status={status} disabled={busy} onChange={(config) => void putConfig(config)} />
          <SubscribersCard status={status} disabled={busy} onEdit={setEditing} />
        </div>
        <LiveFeed recent={status.recent ?? []} />
      </div>
      {editing && (
        <FailureModeDialog
          status={status}
          endpointId={editing}
          saving={busy}
          onClose={() => setEditing(undefined)}
          onSave={(config) =>
            void putConfig(config).then((ok) => {
              if (ok) setEditing(undefined);
            })
          }
        />
      )}
    </Page>
  );
}
