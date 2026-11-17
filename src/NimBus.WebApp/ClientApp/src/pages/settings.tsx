import { useCallback, useEffect, useState, type ReactNode } from "react";
import {
  Link,
  Navigate,
  useLocation,
  useNavigate,
  useParams,
  useSearchParams,
} from "react-router-dom";
import Page from "components/page";
import { Badge, type BadgeVariant } from "components/ui/badge";
import { Button } from "components/ui/button";
import { Input } from "components/ui/input";
import { Modal, ModalBody, ModalFooter, ModalHeader } from "components/ui/modal";
import { SettingsPanelFrame } from "components/settings/panel-frame";
import {
  featureStatus,
  type FeatureStatus,
  type StatusTone,
} from "components/settings/feature-status";
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
import { cn } from "lib/utils";

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

const GROUPS = [...new Set(SETTINGS_FEATURES.map((f) => f.group))];

const BADGE: Record<StatusTone, BadgeVariant> = {
  ok: "success",
  warn: "warning",
  danger: "error",
  off: "secondary",
};

type Show = "all" | "on" | "off" | "attention";
const SHOW: { id: Show; label: string }[] = [
  { id: "all", label: "All" },
  { id: "on", label: "On" },
  { id: "off", label: "Off" },
  { id: "attention", label: "Needs attention" },
];

const LIST = "/Settings";

/** Set on links into a panel so closing can step back instead of stacking history. */
interface OpenedFromList {
  fromList?: boolean;
}

const searchText = (f: SettingsFeature, status?: FeatureStatus) =>
  [f.name, f.description, f.group, status?.label, ...f.tabs.flatMap((t) => [t.label, t.keywords])]
    .join(" ")
    .toLowerCase();

function matches(f: SettingsFeature, status: FeatureStatus | undefined, show: Show, query: string) {
  if (show === "on" && !status?.on) return false;
  if (show === "off" && (status?.on ?? true)) return false;
  if (show === "attention" && status?.tone !== "warn" && status?.tone !== "danger") return false;
  return !query || searchText(f, status).includes(query.trim().toLowerCase());
}

function StatusBadge({ status }: { status?: FeatureStatus }) {
  return status ? <Badge variant={BADGE[status.tone]}>{status.label}</Badge> : null;
}

/**
 * Opt-in capabilities, one row per feature with its live state (Spec 038 §7).
 * Each opens its existing settings at /Settings/:feature?tab=:tab (§8) in a
 * split view: the features stay listed on the left, so switching between them
 * is one click, and the settings take the rest of the width. Site Owner only,
 * like every /api/admin/* call the rows and panels make.
 */
export default function Settings() {
  const { feature } = useParams();
  const [searchParams, setSearchParams] = useSearchParams();
  const navigate = useNavigate();
  const location = useLocation();
  const { snapshot, reload, turnOffMcp, sendHeartbeat } = useSettingsStatus();
  const [show, setShow] = useState<Show>("all");
  const [query, setQuery] = useState("");
  const [dirty, setDirty] = useState(0);
  // Where to go once the unsaved edits are discarded: the list or another feature.
  const [confirmDiscard, setConfirmDiscard] = useState<string | null>(null);
  const [confirmTurnOff, setConfirmTurnOff] = useState(false);
  const [busy, setBusy] = useState<string>();

  const open = SETTINGS_FEATURES.find((f) => f.id === feature?.toLowerCase());
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

  // Closing, opened from a row: step back, so Back afterwards doesn't reopen the
  // panel. Deep-linked: there is no list entry behind it, so replace instead.
  // Switching to another feature replaces the entry and keeps how it was opened.
  const leave = useCallback(
    (to: string) => {
      setDirty(0);
      setConfirmDiscard(null);
      reload();
      if (to !== LIST) navigate(to, { replace: true, state: location.state });
      else if ((location.state as OpenedFromList | null)?.fromList) navigate(-1);
      else navigate(LIST, { replace: true });
    },
    [location.state, navigate, reload],
  );

  const requestLeave = useCallback(
    (to: string) => (dirty > 0 ? setConfirmDiscard(to) : leave(to)),
    [dirty, leave],
  );

  const requestClose = () => requestLeave(LIST);

  // Escape closes the open feature, unless a prompt is up or focus is in a field.
  useEffect(() => {
    if (!open || confirmDiscard !== null || confirmTurnOff) return;
    const onKeyDown = (e: KeyboardEvent) => {
      if (e.key !== "Escape" || e.defaultPrevented) return;
      const target = e.target;
      if (target instanceof Element && target.closest("input, textarea, select, [contenteditable='true']")) return;
      requestLeave(LIST);
    };
    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [open, confirmDiscard, confirmTurnOff, requestLeave]);

  const selectTab = (next: string) =>
    setSearchParams({ tab: next }, { replace: true, state: location.state });

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

  if (feature && !open) return <Navigate to="/Settings" replace />;

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

  const visible = SETTINGS_FEATURES.filter((f) =>
    matches(f, featureStatus(f.id, snapshot), show, query),
  );

  const dialogs = (
    <>
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
          Your edits to {open?.name ?? "these settings"} have not been saved.
        </ModalBody>
        <ModalFooter>
          <Button variant="outline" colorScheme="gray" onClick={() => setConfirmDiscard(null)}>
            Keep editing
          </Button>
          <Button colorScheme="red" onClick={() => leave(confirmDiscard ?? LIST)}>
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

  if (open) {
    const openStatus = featureStatus(open.id, snapshot);
    return (
      <Page title="Settings" subtitle="Opt-in capabilities for this deployment.">
        <div className="grid w-full min-w-0 gap-5 lg:grid-cols-[17rem_minmax(0,1fr)]">
          <nav
            aria-label="Settings"
            className="hidden h-[calc(100vh-13rem)] min-h-[28rem] flex-col gap-4 overflow-y-auto pr-1 lg:flex"
          >
            {GROUPS.map((group) => (
              <div key={group}>
                <h2 className="m-0 mb-1.5 ml-2 font-mono text-[10.5px] font-medium uppercase tracking-[0.14em] text-muted-foreground">
                  {group}
                </h2>
                <ul className="m-0 list-none space-y-0.5 p-0">
                  {SETTINGS_FEATURES.filter((f) => f.group === group).map((f) => {
                    const status = featureStatus(f.id, snapshot);
                    const current = f.id === open.id;
                    return (
                      <li key={f.id}>
                        <Link
                          to={`/Settings/${f.id}`}
                          aria-current={current ? "page" : undefined}
                          onClick={(e) => {
                            e.preventDefault();
                            if (!current) requestLeave(`/Settings/${f.id}`);
                          }}
                          className={cn(
                            "block rounded-nb-md border px-3 py-2.5 text-foreground no-underline hover:no-underline",
                            current
                              ? "border-border-strong bg-card"
                              : "border-transparent hover:bg-muted/60",
                          )}
                        >
                          <span className="flex flex-wrap items-center gap-2">
                            <span className="text-sm font-semibold">{f.name}</span>
                            <StatusBadge status={status} />
                          </span>
                          {status && (
                            <span className="mt-0.5 block truncate font-mono text-[11px] text-muted-foreground">
                              {status.summary}
                            </span>
                          )}
                        </Link>
                      </li>
                    );
                  })}
                </ul>
              </div>
            ))}
          </nav>

          <section
            aria-label={open.name}
            className="flex h-[calc(100vh-13rem)] min-h-[28rem] min-w-0 flex-col overflow-hidden rounded-nb-md border border-border bg-background"
          >
            <SettingsPanelFrame
              title={open.name}
              status={<StatusBadge status={openStatus} />}
              tabs={open.tabs}
              activeTab={tab}
              onTabChange={selectTab}
              onClose={requestClose}
              onDirtyChange={setDirty}
              applies={open.applies}
            >
              {PANELS[open.id]()}
            </SettingsPanelFrame>
          </section>
        </div>
        {dialogs}
      </Page>
    );
  }

  return (
    <Page title="Settings" subtitle="Opt-in capabilities for this deployment.">
      <div className="w-full max-w-5xl space-y-6">
        <div className="flex flex-wrap items-center gap-3">
          <Input
            type="search"
            aria-label="Filter settings"
            placeholder="Filter, e.g. “payload”, “rate”, “production”"
            className="max-w-md flex-1"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
          <div className="flex flex-wrap gap-1.5">
            {SHOW.map((s) => (
              <button
                key={s.id}
                type="button"
                aria-pressed={show === s.id}
                onClick={() => setShow(s.id)}
                className={cn(
                  "rounded-full border px-3 py-1 text-[12.5px] transition-colors",
                  show === s.id
                    ? "border-foreground bg-foreground text-background"
                    : "border-border bg-card text-muted-foreground hover:text-foreground",
                )}
              >
                {s.label}
              </button>
            ))}
          </div>
        </div>

        {GROUPS.map((group) => {
          const features = visible.filter((f) => f.group === group);
          if (features.length === 0) return null;
          return (
            <section key={group} aria-labelledby={`settings-group-${group}`}>
              <h2
                id={`settings-group-${group}`}
                className="m-0 mb-2 ml-0.5 font-mono text-[10.5px] font-medium uppercase tracking-[0.14em] text-muted-foreground"
              >
                {group}
              </h2>
              <ul className="m-0 list-none divide-y divide-border overflow-hidden rounded-nb-md border border-border bg-card p-0">
                {features.map((f) => {
                  const status = featureStatus(f.id, snapshot);
                  const cta = status?.cta;
                  return (
                    <li key={f.id} className="flex items-center gap-3 pr-4 transition-colors hover:bg-muted/50">
                      <Link
                        to={cta ? `/Settings/${f.id}?tab=${cta.tab}` : `/Settings/${f.id}`}
                        state={{ fromList: true } satisfies OpenedFromList}
                        className="flex min-w-0 flex-1 items-center gap-4 py-3.5 pl-4 text-foreground no-underline hover:no-underline"
                      >
                        <span className="min-w-0 flex-1">
                          <span className="flex flex-wrap items-center gap-2">
                            <span className="text-[15px] font-semibold">{f.name}</span>
                            <StatusBadge status={status} />
                          </span>
                          <span className="block text-sm text-muted-foreground">{f.description}</span>
                        </span>
                        {status && (
                          <span className="hidden max-w-60 text-right font-mono text-[11.5px] text-muted-foreground lg:block">
                            {status.summary}
                          </span>
                        )}
                        <span className="shrink-0 text-sm font-semibold text-primary">{cta?.label ?? "Configure"} ›</span>
                      </Link>
                      {quickAction(f, status)}
                    </li>
                  );
                })}
              </ul>
            </section>
          );
        })}
        {visible.length === 0 && (
          <p className="rounded-nb-md border border-border bg-card p-8 text-center text-muted-foreground">
            No settings match.
          </p>
        )}
      </div>

      {dialogs}
    </Page>
  );
}
