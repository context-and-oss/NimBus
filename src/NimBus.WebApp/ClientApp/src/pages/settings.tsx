import { useCallback, useEffect, useState, type ReactNode } from "react";
import { Link, Navigate, useLocation, useNavigate, useParams, useSearchParams } from "react-router-dom";
import { Badge, type BadgeVariant } from "components/ui/badge";
import { Button } from "components/ui/button";
import { Modal, ModalBody, ModalFooter, ModalHeader } from "components/ui/modal";
import { SettingsPanelFrame } from "components/settings/panel-frame";
import { featureStatus, type FeatureStatus, type StatusTone } from "components/settings/feature-status";
import { WorkspaceChip, WorkspaceStat, type ChipTone } from "components/workspace/workspace";
import McpAccessSettings from "components/admin/mcp-access-settings";
import FailureIntelligenceSettings from "components/admin/failure-intelligence-settings";
import HeartbeatCard from "components/admin/heartbeat-card";
import SimulationSettings from "components/admin/simulation-settings";
import AuditSettings from "components/admin/audit-settings";
import AgentKeysSettings from "components/admin/agent-keys-settings";
import DashboardTokensSettings from "components/admin/dashboard-tokens-settings";
import { useSettingsStatus } from "hooks/use-settings-status";
import { notifyError, notifySuccess } from "functions/notifications.functions";
import { SETTINGS_FEATURES, type SettingsFeature } from "models/manage-pages";

/** Each feature's existing settings component, keyed by its URL segment. */
const PANELS: Record<string, () => ReactNode> = {
  mcp: () => <McpAccessSettings />,
  "failure-intelligence": () => <FailureIntelligenceSettings />,
  "agent-keys": () => <AgentKeysSettings />,
  heartbeat: () => <HeartbeatCard />,
  "dashboard-tokens": () => <DashboardTokensSettings />,
  simulation: () => <SimulationSettings />,
  audit: () => <AuditSettings />,
};

const BADGE: Record<StatusTone, BadgeVariant> = {
  ok: "success",
  warn: "warning",
  danger: "error",
  off: "secondary",
};

const CHIP: Record<StatusTone, ChipTone> = { ok: "ok", warn: "warn", danger: "danger", off: "off" };

function StatusBadge({ status }: { status?: FeatureStatus }) {
  return status ? <Badge variant={BADGE[status.tone]}>{status.label}</Badge> : null;
}

/**
 * Opt-in capabilities as a workspace (Spec 038 §7, §8): one chip per feature
 * with its live state, and the chosen feature's settings below, at
 * /Settings/:feature?tab=:tab. /Settings alone shows the first feature. Site
 * Owner only, like every /api/admin/* call the chips and settings make.
 */
export default function Settings() {
  const { feature } = useParams();
  const [searchParams, setSearchParams] = useSearchParams();
  const navigate = useNavigate();
  const location = useLocation();
  const { snapshot, reload, turnOffMcp, sendHeartbeat } = useSettingsStatus();
  const [dirty, setDirty] = useState(0);
  // The feature to open once the unsaved edits are discarded.
  const [confirmDiscard, setConfirmDiscard] = useState<string | null>(null);
  const [confirmTurnOff, setConfirmTurnOff] = useState(false);
  const [busy, setBusy] = useState<string>();

  const open = feature ? SETTINGS_FEATURES.find((f) => f.id === feature.toLowerCase()) : SETTINGS_FEATURES[0];
  const requestedTab = searchParams.get("tab");
  const tab = open?.tabs.find((t) => t.id === requestedTab)?.id ?? open?.tabs[0].id ?? "";

  // While edits are unsaved, a reload or closing the tab asks first. Browser
  // Back is not guarded: that needs a data router (Spec 038 slice 2 decision).
  useEffect(() => {
    if (dirty === 0) return;
    const onBeforeUnload = (e: BeforeUnloadEvent) => {
      e.preventDefault();
      e.returnValue = "";
    };
    window.addEventListener("beforeunload", onBeforeUnload);
    return () => window.removeEventListener("beforeunload", onBeforeUnload);
  }, [dirty]);

  // Switching feature re-reads every feature's status, so the chip of the one
  // just left shows what was saved.
  const leave = useCallback(
    (to: string) => {
      setDirty(0);
      setConfirmDiscard(null);
      reload();
      navigate(to);
    },
    [navigate, reload],
  );

  const requestLeave = useCallback(
    (to: string) => (dirty > 0 ? setConfirmDiscard(to) : leave(to)),
    [dirty, leave],
  );

  const selectTab = (next: string) => setSearchParams({ tab: next }, { replace: true, state: location.state });

  async function run(id: string, action: () => Promise<void>) {
    setBusy(id);
    try {
      await action();
    } catch (err: unknown) {
      notifyError(err instanceof Error ? err.message : "The action failed.");
    } finally {
      setBusy(undefined);
    }
  }

  if (!open) return <Navigate to="/Settings" replace />;

  const quickAction = (f: SettingsFeature, status?: FeatureStatus) => {
    const quick = status?.quick;
    if (!quick) return null;
    switch (quick.kind) {
      case "turn-off-mcp":
        return (
          <Button size="sm" variant="outline" colorScheme="red" onClick={() => setConfirmTurnOff(true)}>
            {quick.label}
          </Button>
        );
      case "send-heartbeat":
        return (
          <Button
            size="sm"
            variant="outline"
            colorScheme="gray"
            isLoading={busy === f.id}
            disabled={busy === f.id}
            onClick={() =>
              void run(f.id, async () => {
                const count = await sendHeartbeat();
                notifySuccess(`Heartbeat sent to ${count} endpoint(s).`);
              })
            }
          >
            {quick.label}
          </Button>
        );
      case "link":
        return (
          <Link to={quick.to} className="text-sm font-semibold text-primary">
            {quick.label}
          </Link>
        );
    }
  };

  const openStatus = featureStatus(open.id, snapshot);
  const mcpActivity = open.id === "mcp" && snapshot.mcp && snapshot.mcp !== "error" ? snapshot.mcp.activity : undefined;

  return (
    <>
      <SettingsPanelFrame
        key={open.id}
        title={open.name}
        description={
          <>
            {open.description}
            {openStatus && <span className="ml-1.5 font-mono text-[11.5px]">{openStatus.summary}</span>}
          </>
        }
        status={<StatusBadge status={openStatus} />}
        chips={SETTINGS_FEATURES.map((f) => {
          const status = featureStatus(f.id, snapshot);
          const current = f.id === open.id;
          return (
            <WorkspaceChip
              key={f.id}
              tone={status ? CHIP[status.tone] : undefined}
              title={status ? `${f.name}: ${status.label}. ${status.summary}` : f.name}
              selected={current}
              onSelect={() => {
                if (!current) requestLeave(`/Settings/${f.id}`);
              }}
            >
              {f.name}
            </WorkspaceChip>
          );
        })}
        stats={
          mcpActivity && (
            <>
              <WorkspaceStat label="Actions · 24 h" value={mcpActivity.actions} onClick={() => selectTab("activity")} />
              <WorkspaceStat
                label="Refused"
                value={mcpActivity.refused}
                tone={mcpActivity.refused > 0 ? "warn" : "default"}
                onClick={() => selectTab("activity")}
              />
            </>
          )
        }
        actions={quickAction(open, openStatus)}
        tabs={open.tabs}
        activeTab={tab}
        onTabChange={selectTab}
        onDirtyChange={setDirty}
        applies={open.applies}
      >
        {PANELS[open.id]()}
      </SettingsPanelFrame>

      <Modal
        isOpen={confirmDiscard !== null}
        onClose={() => setConfirmDiscard(null)}
        label={`Discard ${dirty} unsaved ${dirty === 1 ? "change" : "changes"}?`}
        size="md"
      >
        <ModalHeader>
          Discard {dirty} unsaved {dirty === 1 ? "change" : "changes"}?
        </ModalHeader>
        <ModalBody className="text-sm text-muted-foreground">
          Your edits to {open.name} have not been saved.
        </ModalBody>
        <ModalFooter>
          <Button variant="outline" colorScheme="gray" onClick={() => setConfirmDiscard(null)}>
            Keep editing
          </Button>
          <Button colorScheme="red" onClick={() => confirmDiscard && leave(confirmDiscard)}>
            Discard
          </Button>
        </ModalFooter>
      </Modal>

      <Modal
        isOpen={confirmTurnOff}
        onClose={() => setConfirmTurnOff(false)}
        label="Turn off MCP access now?"
        size="md"
      >
        <ModalHeader>Turn off MCP access now?</ModalHeader>
        <ModalBody className="space-y-2 text-sm">
          <p>
            Every agent call gets 503 [Disabled] within 30 seconds on all instances. The other MCP
            settings are kept; turning it back on is reviewed as a change that widens access.
          </p>
        </ModalBody>
        <ModalFooter>
          <Button variant="outline" colorScheme="gray" onClick={() => setConfirmTurnOff(false)}>
            Cancel
          </Button>
          <Button
            colorScheme="red"
            isLoading={busy === "mcp"}
            onClick={() =>
              void run("mcp", async () => {
                await turnOffMcp();
                setConfirmTurnOff(false);
                notifySuccess("MCP access is off. Agents get 503 [Disabled] on every instance within 30 seconds.");
              })
            }
          >
            Turn off MCP access
          </Button>
        </ModalFooter>
      </Modal>
    </>
  );
}
