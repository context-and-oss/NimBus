import { useCallback, useEffect, useMemo, useState } from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import { Toggle } from "components/ui/toggle";
import { Badge } from "components/ui/badge";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "components/ui/card";
import { formatAuditType } from "functions/audit.functions";
import { PanelFooter, PanelInspector, useInPanel, usePanelDirty } from "components/settings/panel-frame";
import { Inspector, WorkspaceChip } from "components/workspace/workspace";
import {
  RowSelect,
  selectableRowClass,
  WORKSPACE_HEAD_ROW,
  WORKSPACE_TH,
  WorkspaceTableFrame,
} from "components/workspace/selectable-row";

// Presentation only: the server's configurableAuditTypes decides what is listed,
// and a type missing here lands in "Other" rather than disappearing.
const GROUPS: { title: string; types: string[] }[] = [
  {
    title: "Browsing",
    types: ["searchEvents", "getEventDetails", "getEndpointDetails"],
  },
  {
    title: "Message operations",
    types: [
      "resubmit",
      "resubmitWithChanges",
      "skip",
      "completeHandoff",
      "failHandoff",
      "reportEvent",
      "compose",
      "purgeMessages",
      "copyEndpointData",
      "reconcileStalePending",
      "failureClassified",
    ],
  },
  {
    title: "Endpoint control",
    types: [
      "enableEndpoint",
      "disableEndpoint",
      "enableEndpointSend",
      "disableEndpointSend",
      "manageSubscription",
      "acknowledgeEndpoint",
      "clearEndpointAcknowledgement",
      "enableEndpointHeartbeat",
      "disableEndpointHeartbeat",
    ],
  },
  {
    title: "Administration",
    types: [
      "grantRole",
      "revokeRole",
      "updateHeartbeatSettings",
      "sendHeartbeatNow",
      "deleteStorageContainer",
      "updateIntelligenceSettings",
      "updateSimulationSettings",
      "controlSimulation",
      "updateSimulationConfig",
    ],
  },
];

const shieldIcon = (
  <svg
    aria-hidden="true"
    viewBox="0 0 24 24"
    className="h-5 w-5"
    fill="none"
    stroke="currentColor"
    strokeWidth="2"
  >
    <path
      d="M12 3 4 6v6c0 4.5 3.4 8.3 8 9 4.6-.7 8-4.5 8-9V6z"
      strokeLinejoin="round"
    />
    <path d="m9 12 2 2 4-4" strokeLinecap="round" strokeLinejoin="round" />
  </svg>
);

function groupTypes(configurable: string[]) {
  const known = new Set(GROUPS.flatMap((g) => g.types));
  const groups = GROUPS.map((g) => ({
    title: g.title,
    types: g.types.filter((t) => configurable.includes(t)),
  }));
  const other = configurable.filter((t) => !known.has(t));
  if (other.length > 0) groups.push({ title: "Other", types: other });
  return groups.filter((g) => g.types.length > 0);
}

/** Number of action types whose recorded state differs between the two sets. */
function differenceCount(a: Set<string>, b: Set<string>) {
  return [...a].filter((x) => !b.has(x)).length + [...b].filter((x) => !a.has(x)).length;
}

/**
 * Settings → Audit logging. Chooses which operator actions reach the audit log (the Audit
 * Log page, endpoint and event audit trails, and Application Insights). A busy
 * site can switch off high-volume reads such as searches without losing the
 * resubmits and role grants the log exists for.
 */
export default function AuditSettings() {
  const [configurable, setConfigurable] = useState<string[]>([]);
  const [saved, setSaved] = useState<Set<string>>(new Set());
  const [disabled, setDisabled] = useState<Set<string>>(new Set());
  const [loading, setLoading] = useState(true);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [savedNotice, setSavedNotice] = useState(false);
  // The chosen group's title; null shows every group.
  const [groupFilter, setGroupFilter] = useState<string | null>(null);
  const [selectedType, setSelectedType] = useState<string | null>(null);

  const client = useMemo(() => new api.Client(api.CookieAuth()), []);

  const apply = useCallback((settings: api.AuditSettings) => {
    const next = new Set(settings.disabledAuditTypes ?? []);
    setConfigurable(settings.configurableAuditTypes ?? []);
    setSaved(next);
    setDisabled(new Set(next));
  }, []);

  useEffect(() => {
    client
      .getAdminAuditSettings()
      .then((settings) => {
        apply(settings);
        setError(null);
      })
      .catch((err: unknown) =>
        setError(
          err instanceof Error ? err.message : "Failed to load audit settings",
        ),
      )
      .finally(() => setLoading(false));
  }, [client, apply]);

  const groups = useMemo(() => groupTypes(configurable), [configurable]);
  const changes = differenceCount(saved, disabled);
  const dirty = changes > 0;
  usePanelDirty(changes);
  const inPanel = useInPanel();

  function setRecorded(type: string, recorded: boolean) {
    setSavedNotice(false);
    setDisabled((old) => {
      const next = new Set(old);
      if (recorded) next.delete(type);
      else next.add(type);
      return next;
    });
  }

  function setGroupRecorded(types: string[], recorded: boolean) {
    setSavedNotice(false);
    setDisabled((old) => {
      const next = new Set(old);
      for (const t of types) {
        if (recorded) next.delete(t);
        else next.add(t);
      }
      return next;
    });
  }

  async function save() {
    setSaving(true);
    try {
      const result = await client.putAdminAuditSettings(
        new api.AuditSettings({ disabledAuditTypes: [...disabled] }),
      );
      apply(result);
      setError(null);
      setSavedNotice(true);
    } catch (err: unknown) {
      setError(
        err instanceof Error ? err.message : "Failed to save audit settings",
      );
    } finally {
      setSaving(false);
    }
  }

  const recordedCount = configurable.filter((t) => !disabled.has(t)).length;

  // A group that is no longer listed (the server's types changed) falls back to All.
  const activeGroup = groups.find((g) => g.title === groupFilter);
  const rows = (activeGroup ? [activeGroup] : groups).flatMap((g) =>
    g.types.map((type) => ({ type, group: g.title, label: formatAuditType(type) })),
  );
  const activeAllRecorded = activeGroup?.types.every((t) => !disabled.has(t)) ?? false;
  const isChanged = (type: string) => saved.has(type) !== disabled.has(type);

  const selectedGroup = groups.find((g) => selectedType !== null && g.types.includes(selectedType));
  const selected =
    selectedGroup && selectedType !== null
      ? {
          group: selectedGroup.title,
          label: formatAuditType(selectedType),
          recorded: !disabled.has(selectedType),
          changed: isChanged(selectedType),
        }
      : undefined;

  return (
    <Card>
      <CardHeader className="px-5 py-4">
        {!inPanel && (
          <div className="flex items-center gap-2">
            {shieldIcon}
            <CardTitle className="text-xl">Audit logging</CardTitle>
          </div>
        )}
        <CardDescription>
          Choose which operator actions are written to the audit log and
          Application Insights. Access-denied attempts and changes to this
          setting are always recorded.
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

        <div className="flex flex-wrap items-center justify-between gap-3">
          <p className="text-sm text-muted-foreground">
            {loading
              ? "Loading…"
              : `${recordedCount} of ${configurable.length} action types recorded.`}
            {savedNotice && !dirty && " Saved."}
          </p>
          <PanelFooter>
            <div className="flex items-center gap-2">
              <Button
                variant="ghost"
                colorScheme="gray"
                size="sm"
                onClick={() => {
                  setDisabled(new Set(saved));
                  setSavedNotice(false);
                }}
                disabled={!dirty || saving}
              >
                Discard changes
              </Button>
              <Button
                colorScheme="primary"
                size="sm"
                onClick={() => void save()}
                disabled={loading || !dirty || saving}
                isLoading={saving}
              >
                Save
              </Button>
            </div>
          </PanelFooter>
        </div>

        <div className="flex flex-wrap items-center justify-between gap-3">
          <div role="group" aria-label="Group" className="flex min-w-0 flex-wrap items-center gap-1.5">
            <WorkspaceChip selected={!activeGroup} onSelect={() => setGroupFilter(null)}>
              All
            </WorkspaceChip>
            {groups.map((group) => (
              <WorkspaceChip
                key={group.title}
                selected={group.title === activeGroup?.title}
                onSelect={() => setGroupFilter(group.title)}
              >
                {group.title}
              </WorkspaceChip>
            ))}
          </div>
          {activeGroup && (
            <Button
              variant="ghost"
              colorScheme="gray"
              size="xs"
              onClick={() => setGroupRecorded(activeGroup.types, !activeAllRecorded)}
              disabled={saving}
            >
              {activeAllRecorded ? "Record none" : "Record all"}
            </Button>
          )}
        </div>

        <WorkspaceTableFrame>
          <table className="w-full min-w-[30rem] text-sm" aria-label="Action types">
            <thead>
              <tr className={WORKSPACE_HEAD_ROW}>
                <th scope="col" className={WORKSPACE_TH}>Action type</th>
                <th scope="col" className={WORKSPACE_TH}>Group</th>
                <th scope="col" className={WORKSPACE_TH}>Recorded</th>
              </tr>
            </thead>
            <tbody>
              {rows.map((row) => {
                const isSelected = row.type === selectedType;
                return (
                  <tr key={row.type} className={selectableRowClass(isSelected)}>
                    <td className="px-4">
                      <RowSelect
                        selected={isSelected}
                        onSelect={() => setSelectedType(row.type)}
                        after={
                          isChanged(row.type) && (
                            <span className="text-[11px] font-semibold text-status-warning-ink">Changed</span>
                          )
                        }
                      >
                        {row.label}
                      </RowSelect>
                    </td>
                    <td className="px-4 py-2 text-muted-foreground">{row.group}</td>
                    <td className="px-4 py-2">
                      <Toggle
                        checked={!disabled.has(row.type)}
                        onChange={(next) => setRecorded(row.type, next)}
                        disabled={saving}
                        aria-label={`Record ${row.label}`}
                      />
                    </td>
                  </tr>
                );
              })}
              {rows.length === 0 && !loading && !error && (
                <tr>
                  <td colSpan={3} className="py-4 text-center text-muted-foreground">
                    No action types to configure.
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </WorkspaceTableFrame>

        <PanelInspector>
          {selected ? (
            <Inspector
              eyebrow={selected.group}
              title={selected.label}
              state={
                selected.recorded ? (
                  <Badge variant="success">Recorded</Badge>
                ) : (
                  <Badge variant="secondary">Not recorded</Badge>
                )
              }
              description={
                <>
                  {selected.recorded
                    ? `${selected.label} actions are written to the audit log and Application Insights.`
                    : `${selected.label} actions are not written to the audit log or Application Insights.`}
                  {selected.changed && " This change is not saved yet."}
                </>
              }
            />
          ) : (
            <Inspector empty="Select an action type to see what recording it means." />
          )}
        </PanelInspector>
      </CardContent>
    </Card>
  );
}
