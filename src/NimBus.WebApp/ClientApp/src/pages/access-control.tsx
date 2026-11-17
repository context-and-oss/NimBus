import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import * as api from "api-client";
import Page from "components/page";
import { GrantsPanel, type GrantRole, type GrantRoleInfo } from "components/access-control/grants-panel";
import { Input } from "components/ui/input";
import { useAccess, invalidateAccess, isOwnerRole } from "hooks/use-access";
import { useToast } from "components/ui/toast";
import { cn } from "lib/utils";

const SITE_ROLES: readonly GrantRoleInfo[] = [
  {
    role: "reader",
    label: "Reader",
    description: "View endpoints, events, metrics and audits across the platform.",
    badge: "info",
    pick: (s) => s.readers ?? [],
  },
  {
    role: "contributor",
    label: "Contributor",
    description: "Everything Readers can, plus resubmit, skip, handoff and compose.",
    badge: "success",
    pick: (s) => s.contributors ?? [],
  },
  {
    role: "owner",
    label: "Owner",
    description:
      "Full management: admin operations, purge, endpoint settings, and these access-control lists.",
    badge: "error",
    pick: (s) => s.owners ?? [],
  },
  {
    role: "piiReader",
    label: "PII Reader",
    description:
      "May reveal payload fields marked [Sensitive]. Owners also need PII Reader to reveal those fields. Other payload fields remain visible.",
    badge: "warning",
    pick: (s) => s.piiReaders ?? [],
  },
];

const ENDPOINT_ROLES: readonly GrantRoleInfo[] = SITE_ROLES.filter((r) => r.role !== "piiReader").map((r) =>
  r.role === "owner"
    ? { ...r, description: "Manage this endpoint: purge, settings, subscriptions and its role grants." }
    : r,
);

/** Above this many endpoints the scope list gets a filter box. */
const ENDPOINT_FILTER_FROM = 8;

const toRoleEnum = (role: GrantRole): api.RoleEntryRole =>
  ({
    reader: api.RoleEntryRole.Reader,
    contributor: api.RoleEntryRole.Contributor,
    owner: api.RoleEntryRole.Owner,
    piiReader: api.RoleEntryRole.PiiReader,
  })[role];

export default function AccessControl() {
  const { access } = useAccess();
  const { addToast } = useToast();
  const client = useMemo(() => new api.Client(api.CookieAuth()), []);

  const canManageSite = access?.canManageAccessControl ?? false;
  const ownedEndpoints = useMemo(
    () =>
      (access?.endpointRoles ?? [])
        .filter((r) => isOwnerRole(r.role))
        .map((r) => r.endpointId ?? "")
        .filter(Boolean),
    [access],
  );

  const [siteSet, setSiteSet] = useState<api.AccessControlSet | null>(null);
  const [endpointIds, setEndpointIds] = useState<string[]>([]);
  const [selectedEndpoint, setSelectedEndpoint] = useState("");
  const [endpointSet, setEndpointSet] = useState<api.AccessControlSet | null>(null);
  const [busy, setBusy] = useState(false);
  const endpointRequestTicket = useRef(0);
  // Which scope the right-hand side shows. Site Owners start on the site
  // roles; endpoint Owners pick one of their endpoints first.
  const [chosenView, setView] = useState<"site" | "endpoint" | null>(null);
  const view = chosenView ?? (canManageSite ? "site" : null);
  const [endpointFilter, setEndpointFilter] = useState("");

  // Site lists (site Owners only).
  useEffect(() => {
    if (!canManageSite) return;
    client.getAccessControl().then(setSiteSet).catch(() => setSiteSet(null));
  }, [canManageSite, client]);

  // Endpoint selector: site Owners manage every endpoint, endpoint Owners
  // only their own.
  useEffect(() => {
    if (canManageSite) {
      client.getEndpointsAll().then((ids) => setEndpointIds(ids ?? [])).catch(() => setEndpointIds([]));
    } else {
      setEndpointIds(ownedEndpoints);
    }
  }, [canManageSite, ownedEndpoints, client]);

  useEffect(() => {
    const ticket = ++endpointRequestTicket.current;
    setEndpointSet(null);
    if (!selectedEndpoint) {
      return;
    }
    client
      .getEndpointAccessControl(selectedEndpoint)
      .then((set) => {
        if (ticket === endpointRequestTicket.current) setEndpointSet(set);
      })
      .catch(() => {
        if (ticket === endpointRequestTicket.current) setEndpointSet(null);
      });
  }, [selectedEndpoint, client]);

  const mutate = useCallback(
    async (fn: () => Promise<api.AccessControlSet>, apply: (s: api.AccessControlSet) => void) => {
      setBusy(true);
      try {
        apply(await fn());
        invalidateAccess();
      } catch (e) {
        addToast({
          title: "Access-control change failed",
          description: e instanceof Error ? e.message : String(e),
          variant: "error",
        });
      } finally {
        setBusy(false);
      }
    },
    [addToast],
  );

  const mutateEndpoint = useCallback(
    (
      endpointId: string,
      fn: (capturedEndpointId: string) => Promise<api.AccessControlSet>,
    ) => {
      const ticket = ++endpointRequestTicket.current;
      return mutate(
        () => fn(endpointId),
        (set) => {
          if (ticket === endpointRequestTicket.current) setEndpointSet(set);
        },
      );
    },
    [mutate],
  );

  const selectEndpoint = useCallback((endpointId: string) => {
    // Invalidate pending loads and mutations before React commits the selection.
    ++endpointRequestTicket.current;
    setEndpointSet(null);
    setSelectedEndpoint(endpointId);
  }, []);

  const roleEntry = (role: GrantRole, entry: string) =>
    new api.RoleEntry({ role: toRoleEnum(role), entry });

  if (access && !canManageSite && ownedEndpoints.length === 0) {
    return (
      <Page title="Access Control">
        <p className="text-muted-foreground text-sm">
          You need Owner access (site-wide or on an endpoint) to manage roles.
          Ask a site Owner to grant it on this page.
        </p>
      </Page>
    );
  }

  const set = view === "site" ? siteSet : endpointSet;
  const visibleEndpoints = endpointFilter.trim()
    ? endpointIds.filter((id) => id.toLowerCase().includes(endpointFilter.trim().toLowerCase()))
    : endpointIds;

  const scopeItem = (key: string, label: string, current: boolean, onSelect: () => void) => (
    <li key={key}>
      <button
        type="button"
        aria-current={current ? "true" : undefined}
        onClick={onSelect}
        className={cn(
          "block w-full truncate rounded-nb-md border px-3 py-2 text-left text-sm",
          current
            ? "border-border-strong bg-card font-semibold text-foreground"
            : "border-transparent text-foreground hover:bg-muted/60",
        )}
        title={label}
      >
        {label}
      </button>
    </li>
  );

  return (
    <Page
      title="Access Control"
      subtitle="Storage-backed roles: Reader < Contributor < Owner, plus the separate PII Reader capability."
    >
      <div className="grid w-full min-w-0 items-start gap-5 lg:grid-cols-[17rem_minmax(0,1fr)]">
        <nav aria-label="Access scopes" className="flex min-w-0 flex-col gap-4">
          {access && (
            <div className="space-y-1.5 rounded-nb-md border border-border bg-card px-3 py-2.5 text-[12.5px]">
              <div className="text-muted-foreground">Your access</div>
              <div className="truncate font-medium" title={access.email ?? access.name ?? undefined}>
                {access.email ?? access.name ?? "current user"}
              </div>
              <div className="flex flex-wrap gap-1.5">
                <span className="rounded-full bg-status-danger-50 px-2 py-0.5 font-mono text-[11px] font-semibold text-status-danger-ink dark:bg-red-950/40 dark:text-red-200">
                  site {String(access.siteRole ?? "none")}
                </span>
                <span
                  className={
                    access.isPiiReader
                      ? "rounded-full bg-status-warning-50 px-2 py-0.5 font-mono text-[11px] font-semibold text-status-warning-ink dark:bg-yellow-950/40 dark:text-yellow-200"
                      : "px-2 py-0.5 font-mono text-[11px] text-muted-foreground"
                  }
                >
                  {access.isPiiReader ? "PII Reader" : "no PII access"}
                </span>
              </div>
            </div>
          )}
          {canManageSite && (
            <div>
              <h2 className="m-0 mb-1.5 ml-2 font-mono text-[10.5px] font-medium uppercase tracking-[0.14em] text-muted-foreground">
                Site-wide
              </h2>
              <ul className="m-0 list-none p-0">
                {scopeItem("site", "Site roles", view === "site", () => {
                  selectEndpoint("");
                  setView("site");
                })}
              </ul>
            </div>
          )}
          <div>
            <h2 className="m-0 mb-1.5 ml-2 font-mono text-[10.5px] font-medium uppercase tracking-[0.14em] text-muted-foreground">
              Endpoint roles
            </h2>
            {endpointIds.length > ENDPOINT_FILTER_FROM && (
              <Input
                type="search"
                aria-label="Filter endpoints"
                placeholder="Filter endpoints"
                value={endpointFilter}
                onChange={(e) => setEndpointFilter(e.target.value)}
                className="mb-2"
              />
            )}
            <ul className="m-0 list-none space-y-0.5 p-0">
              {visibleEndpoints.map((id) =>
                scopeItem(id, id, view === "endpoint" && selectedEndpoint === id, () => {
                  if (view !== "endpoint" || selectedEndpoint !== id) selectEndpoint(id);
                  setView("endpoint");
                }),
              )}
              {visibleEndpoints.length === 0 && (
                <li className="px-3 py-2 text-[12.5px] text-muted-foreground">No endpoints match.</li>
              )}
            </ul>
          </div>
        </nav>

        <section aria-label={view === "site" ? "Site roles" : selectedEndpoint || "Grants"} className="min-w-0 space-y-4">
          {view === null ? (
            <p className="rounded-nb-md border border-border bg-card p-8 text-center text-sm text-muted-foreground">
              Select an endpoint to see and change its grants.
            </p>
          ) : (
            <>
              <div className="space-y-1">
                <h2 className="m-0 text-[19px] font-bold">{view === "site" ? "Site roles" : selectedEndpoint}</h2>
                <p className="m-0 text-[13px] text-muted-foreground">
                  {view === "site"
                    ? "Apply to every endpoint. Roles granted through configuration (Authorization__AdminUserObjectIds, AdminGroupObjectIds or the EIP_Management claim) apply too but aren't listed; only storage-backed grants are."
                    : "Grants on this endpoint only, in addition to what site roles allow."}
                </p>
              </div>
              <GrantsPanel
                scope={view === "site" ? "Site" : selectedEndpoint}
                roles={view === "site" ? SITE_ROLES : ENDPOINT_ROLES}
                set={set}
                busy={busy}
                currentUser={access?.email ?? undefined}
                onAdd={(role, entry) =>
                  view === "site"
                    ? mutate(() => client.postAccessControlRole(roleEntry(role, entry)), setSiteSet)
                    : mutateEndpoint(selectedEndpoint, (endpointId) =>
                        client.postEndpointAccessControlRole(endpointId, roleEntry(role, entry)),
                      )
                }
                onRemove={(role, entry) =>
                  view === "site"
                    ? mutate(() => client.deleteAccessControlRole(roleEntry(role, entry)), setSiteSet)
                    : mutateEndpoint(selectedEndpoint, (endpointId) =>
                        client.deleteEndpointAccessControlRole(endpointId, roleEntry(role, entry)),
                      )
                }
              />
            </>
          )}
        </section>
      </div>
    </Page>
  );
}
