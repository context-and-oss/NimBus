import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import * as api from "api-client";
import Page from "components/page";
import { RoleCard } from "components/access-control/role-card";
import { useAccess, invalidateAccess, isOwnerRole } from "hooks/use-access";
import { useToast } from "components/ui/toast";

type SiteRole = "reader" | "contributor" | "owner" | "piiReader";

const SITE_CARDS: Array<{
  role: SiteRole;
  title: string;
  description: string;
  tone: string;
  pick: (set: api.AccessControlSet) => string[];
}> = [
  {
    role: "reader",
    title: "Readers",
    description: "View endpoints, events, metrics and audits across the platform.",
    tone: "bg-status-info",
    pick: (s) => s.readers ?? [],
  },
  {
    role: "contributor",
    title: "Contributors",
    description: "Everything Readers can, plus resubmit, skip, handoff and compose.",
    tone: "bg-status-success",
    pick: (s) => s.contributors ?? [],
  },
  {
    role: "owner",
    title: "Owners",
    description:
      "Full management: admin operations, purge, endpoint settings, and these access-control lists.",
    tone: "bg-status-danger",
    pick: (s) => s.owners ?? [],
  },
  {
    role: "piiReader",
    title: "PII Readers",
    description:
      "May reveal payload fields marked [Sensitive]. Owners also need PII Reader to reveal those fields. Other payload fields remain visible.",
    tone: "bg-status-warning",
    pick: (s) => s.piiReaders ?? [],
  },
];

const ENDPOINT_CARDS = SITE_CARDS.filter((c) => c.role !== "piiReader");

const toRoleEnum = (role: SiteRole): api.RoleEntryRole =>
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

  const siteEntry = (role: SiteRole, entry: string) =>
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

  return (
    <Page
      title="Access Control"
      subtitle="Storage-backed roles: Reader < Contributor < Owner, plus the orthogonal PII Reader capability. Entries are email addresses or Entra object ids."
    >
      {/* Page's content wrapper is a flex ROW — a single column child keeps
          the endpoint section below the site-wide section. */}
      <div className="w-full min-w-0">
      {access && (
        <div className="mb-6 flex flex-wrap items-center gap-x-2 gap-y-1 bg-card border border-border rounded-nb-md px-4 py-3 text-[13px]">
          <span className="text-muted-foreground">Your access:</span>
          <span className="font-medium">{access.email ?? access.name ?? "current user"}</span>
          <span className="text-muted-foreground">—</span>
          <span className="font-mono text-[12px] font-semibold bg-status-danger-50 text-status-danger-ink dark:bg-red-950/40 dark:text-red-200 px-2 py-0.5 rounded-full">
            site {String(access.siteRole ?? "none")}
          </span>
          <span
            className={
              access.isPiiReader
                ? "font-mono text-[12px] font-semibold bg-status-warning-50 text-status-warning-ink dark:bg-yellow-950/40 dark:text-yellow-200 px-2 py-0.5 rounded-full"
                : "font-mono text-[12px] text-muted-foreground px-2 py-0.5"
            }
          >
            {access.isPiiReader ? "PII Reader" : "no PII access"}
          </span>
          <span className="basis-full text-[12px] text-muted-foreground">
            Roles granted via configuration (Authorization__AdminUserObjectIds /
            AdminGroupObjectIds or the EIP_Management claim) apply but do not
            appear as entries in the lists below — only storage-backed grants do.
          </span>
        </div>
      )}
      {canManageSite && (
        <section className="mb-8">
          <h2 className="text-sm font-bold uppercase tracking-wider text-muted-foreground mb-3">
            Site-wide roles
          </h2>
          <div className="grid gap-4 md:grid-cols-2">
            {SITE_CARDS.map((card) => (
              <RoleCard
                key={card.role}
                title={card.title}
                description={card.description}
                tone={card.tone}
                entries={siteSet ? card.pick(siteSet) : []}
                busy={busy || !siteSet}
                onAdd={(entry) =>
                  mutate(() => client.postAccessControlRole(siteEntry(card.role, entry)), setSiteSet)
                }
                onRemove={(entry) =>
                  mutate(() => client.deleteAccessControlRole(siteEntry(card.role, entry)), setSiteSet)
                }
              />
            ))}
          </div>
        </section>
      )}

      <section>
        <h2 className="text-sm font-bold uppercase tracking-wider text-muted-foreground mb-3">
          Endpoint roles
        </h2>
        <div className="flex items-center gap-3 mb-4">
          <label htmlFor="ac-endpoint" className="text-[13px] font-medium">
            Endpoint
          </label>
          <select
            id="ac-endpoint"
            value={selectedEndpoint}
            onChange={(e) => selectEndpoint(e.target.value)}
            className="text-[13px] border border-border rounded-nb-sm px-2 py-1.5 bg-background"
          >
            <option value="">Select an endpoint…</option>
            {endpointIds.map((id) => (
              <option key={id} value={id}>
                {id}
              </option>
            ))}
          </select>
        </div>

        {selectedEndpoint && (
          <div className="grid gap-4 md:grid-cols-3">
            {ENDPOINT_CARDS.map((card) => (
              <RoleCard
                key={card.role}
                title={card.title}
                description={
                  card.role === "owner"
                    ? "Manage this endpoint: purge, settings, subscriptions and its role grants."
                    : card.description
                }
                tone={card.tone}
                entries={endpointSet ? card.pick(endpointSet) : []}
                busy={busy || !endpointSet}
                onAdd={(entry) =>
                  mutateEndpoint(
                    selectedEndpoint,
                    (endpointId) =>
                      client.postEndpointAccessControlRole(
                        endpointId,
                        siteEntry(card.role, entry),
                      ),
                  )
                }
                onRemove={(entry) =>
                  mutateEndpoint(
                    selectedEndpoint,
                    (endpointId) =>
                      client.deleteEndpointAccessControlRole(
                        endpointId,
                        siteEntry(card.role, entry),
                      ),
                  )
                }
              />
            ))}
          </div>
        )}
      </section>
      </div>
    </Page>
  );
}
