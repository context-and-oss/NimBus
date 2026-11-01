import { useCallback, useEffect, useMemo, useState } from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import { Toggle } from "components/ui/toggle";
import {
  Card,
  CardContent,
  CardDescription,
  CardHeader,
  CardTitle,
} from "components/ui/card";
import { formatAuditType } from "functions/audit.functions";
import { PanelFooter, useInPanel, usePanelDirty } from "components/settings/panel-frame";

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

        <div className="grid gap-4 lg:grid-cols-2">
          {groups.map((group) => {
            const allRecorded = group.types.every((t) => !disabled.has(t));
            return (
              <section
                key={group.title}
                aria-label={group.title}
                className="rounded-nb-md border border-border"
              >
                <div className="flex items-center justify-between border-b bg-muted px-3 py-2">
                  <h3 className="text-sm font-semibold">{group.title}</h3>
                  <Button
                    variant="ghost"
                    colorScheme="gray"
                    size="xs"
                    onClick={() => setGroupRecorded(group.types, !allRecorded)}
                    disabled={saving}
                  >
                    {allRecorded ? "Record none" : "Record all"}
                  </Button>
                </div>
                <ul>
                  {group.types.map((type) => {
                    const label = formatAuditType(type);
                    return (
                      <li
                        key={type}
                        className="flex items-center justify-between border-b border-border/50 px-3 py-2 text-sm last:border-b-0"
                      >
                        <span>{label}</span>
                        <Toggle
                          checked={!disabled.has(type)}
                          onChange={(next) => setRecorded(type, next)}
                          disabled={saving}
                          aria-label={`Record ${label}`}
                        />
                      </li>
                    );
                  })}
                </ul>
              </section>
            );
          })}
        </div>
      </CardContent>
    </Card>
  );
}
