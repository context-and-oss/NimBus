import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import * as api from "api-client";
import {
  GrantInspector,
  GrantsTable,
  grantsOf,
  type GrantRole,
  type GrantRoleInfo,
} from "components/access-control/grants-panel";
import { Button } from "components/ui/button";
import { Input } from "components/ui/input";
import { Workspace, WorkspaceChip, WorkspaceSection } from "components/workspace/workspace";
import { useAccess, invalidateAccess, isOwnerRole } from "hooks/use-access";
import { useToast } from "components/ui/toast";

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

/** Above this many endpoints the scope chips get a filter box. */
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
  // The grant shown in the inspector; none shows the Add grant form.
  const [selectedKey, setSelectedKey] = useState<string>();
  const principalRef = useRef<HTMLInputElement>(null);

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
      <Workspace title="Access Control">
        <p className="text-muted-foreground text-sm">
          You need Owner access (site-wide or on an endpoint) to manage roles.
          Ask a site Owner to grant it on this page.
        </p>
      </Workspace>
    );
  }

  const set = view === "site" ? siteSet : endpointSet;
  const scope = view === "site" ? "Site" : selectedEndpoint;
  const roles = view === "site" ? SITE_ROLES : ENDPOINT_ROLES;
  const grants = grantsOf(set, roles);
  const selected = grants.find((g) => g.key === selectedKey);
  const visibleEndpoints = endpointFilter.trim()
    ? endpointIds.filter((id) => id.toLowerCase().includes(endpointFilter.trim().toLowerCase()))
    : endpointIds;

  const you = access && (
    <>
      {" "}You: <span className="font-medium text-foreground">{access.email ?? access.name ?? "current user"}</span>
      {" · "}site {String(access.siteRole ?? "none")}
      {" · "}
      {access.isPiiReader ? "PII Reader" : "no PII access"}.
    </>
  );

  return (
    <Workspace
      title="Access Control"
      subtitle={
        <>
          Storage-backed roles: Reader &lt; Contributor &lt; Owner, plus the separate PII Reader capability.{you}
        </>
      }
      chipsLabel="Scope"
      chips={
        <>
          {canManageSite && (
            <WorkspaceChip
              selected={view === "site"}
              onSelect={() => {
                selectEndpoint("");
                setView("site");
                setSelectedKey(undefined);
              }}
            >
              Site
            </WorkspaceChip>
          )}
          {visibleEndpoints.map((id) => (
            <WorkspaceChip
              key={id}
              mono
              title={id}
              selected={view === "endpoint" && selectedEndpoint === id}
              onSelect={() => {
                if (view !== "endpoint" || selectedEndpoint !== id) selectEndpoint(id);
                setView("endpoint");
                setSelectedKey(undefined);
              }}
            >
              {id}
            </WorkspaceChip>
          ))}
          {endpointIds.length > 0 && visibleEndpoints.length === 0 && (
            <span className="text-[12.5px] text-muted-foreground">No endpoints match.</span>
          )}
          {endpointIds.length > ENDPOINT_FILTER_FROM && (
            <Input
              type="search"
              aria-label="Filter endpoints"
              placeholder="Filter endpoints"
              value={endpointFilter}
              onChange={(e) => setEndpointFilter(e.target.value)}
              className="ml-auto h-8 w-48"
            />
          )}
        </>
      }
      actions={
        view !== null && (
          <Button
            size="sm"
            onClick={() => {
              setSelectedKey(undefined);
              // The form mounts once the selection clears.
              requestAnimationFrame(() => principalRef.current?.focus());
            }}
          >
            New grant
          </Button>
        )
      }
      inspector={
        view === null ? undefined : (
          <GrantInspector
            scope={scope}
            roles={roles}
            grants={grants}
            selected={selected}
            ready={set !== null}
            busy={busy}
            currentUser={access?.email ?? undefined}
            principalRef={principalRef}
            onSelect={setSelectedKey}
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
        )
      }
    >
      {view === null ? (
        <p className="m-0 rounded-nb-md border border-border bg-card p-8 text-center text-sm text-muted-foreground">
          Select an endpoint to see and change its grants.
        </p>
      ) : (
        <WorkspaceSection
          title="Grants"
          count={set ? grants.length : undefined}
          intro={
            <>
              Entries are email addresses, Entra object IDs, or <code>agent:&lt;agentId&gt;</code> for an MCP agent
              key (Reader or Contributor only).{" "}
              {view === "site"
                ? "Site grants apply to every endpoint."
                : "These apply to this endpoint only, in addition to what site roles allow."}
            </>
          }
          foot={
            view === "site"
              ? "Roles granted through configuration (Authorization__AdminUserObjectIds, AdminGroupObjectIds or the EIP_Management claim) apply too but aren't listed; only storage-backed grants are."
              : undefined
          }
        >
          <GrantsTable
            scope={scope}
            grants={grants}
            loading={set === null}
            selectedKey={selected?.key}
            onSelect={setSelectedKey}
          />
        </WorkspaceSection>
      )}
    </Workspace>
  );
}
