import { useState, type FormEvent, type Ref } from "react";
import * as api from "api-client";
import { Badge, type BadgeVariant } from "components/ui/badge";
import { Button } from "components/ui/button";
import { Input } from "components/ui/input";
import { Inspector, InspectorBlock, InspectorList, InspectorTiles } from "components/workspace/workspace";
import {
  RowSelect,
  selectableRowClass,
  WORKSPACE_HEAD_ROW,
  WORKSPACE_TH,
  WorkspaceTableFrame,
} from "components/workspace/selectable-row";
import { cn } from "lib/utils";

export type GrantRole = "reader" | "contributor" | "owner" | "piiReader";

/** One role a scope can grant. */
export interface GrantRoleInfo {
  role: GrantRole;
  /** Singular name, e.g. "Reader". */
  label: string;
  description: string;
  badge: BadgeVariant;
  pick: (set: api.AccessControlSet) => string[];
}

/** One stored grant: an entry holding a role in a scope. */
export interface Grant {
  key: string;
  role: GrantRoleInfo;
  entry: string;
  /** The name an Owner gave the entry in this scope, if any. */
  label?: string;
}

const AGENT_PREFIX = "agent:";

/** What an entry names: an agent of an MCP key, an email address or an Entra object ID. */
export const entryKind = (entry: string): string =>
  entry.toLowerCase().startsWith(AGENT_PREFIX) ? "Agent" : entry.includes("@") ? "Email" : "Object ID";

// Agent keys act as workloads: the server accepts them as Reader or Contributor only.
const agentRoleRefused = (entry: string, role: GrantRole) =>
  entry.trim().toLowerCase().startsWith(AGENT_PREFIX) && (role === "owner" || role === "piiReader");

/** Names are keyed by the entry trimmed and lowercased. */
const labelOf = (set: api.AccessControlSet, entry: string): string | undefined =>
  set.labels?.[entry.trim().toLowerCase()] || undefined;

/** A scope's grants, one per entry and role, in role order. */
export const grantsOf = (set: api.AccessControlSet | null, roles: readonly GrantRoleInfo[]): Grant[] =>
  set
    ? roles.flatMap((role) =>
        role.pick(set).map((entry) => ({ key: `${role.role}:${entry}`, role, entry, label: labelOf(set, entry) })),
      )
    : [];

/** The longest name the server accepts. */
const MAX_LABEL_LENGTH = 100;

const scopeName = (scope: string) => (scope === "Site" ? "Site" : scope);

export interface GrantsTableProps {
  /** "Site" or the endpoint ID; names the scope in labels. */
  scope: string;
  grants: readonly Grant[];
  /** True while the scope's grants are loading. */
  loading: boolean;
  selectedKey?: string;
  onSelect: (key: string) => void;
}

/** A scope's grants as one table, one row per entry and role. */
export function GrantsTable({ scope, grants, loading, selectedKey, onSelect }: GrantsTableProps) {
  return (
    <WorkspaceTableFrame>
      <table className="w-full min-w-[34rem] border-collapse text-[13px]" aria-label={`${scope} grants`}>
        <thead>
          <tr className={WORKSPACE_HEAD_ROW}>
            <th scope="col" className={WORKSPACE_TH}>Principal</th>
            <th scope="col" className={WORKSPACE_TH}>Type</th>
            <th scope="col" className={WORKSPACE_TH}>Role</th>
            <th scope="col" className={WORKSPACE_TH}>Scope</th>
          </tr>
        </thead>
        <tbody>
          {loading && (
            <tr>
              <td colSpan={4} className="px-4 py-6 text-center text-muted-foreground">Loading grants…</td>
            </tr>
          )}
          {!loading && grants.length === 0 && (
            <tr>
              <td colSpan={4} className="px-4 py-6 text-center text-muted-foreground">
                No grants stored for {scope === "Site" ? "the site" : scope}.
              </td>
            </tr>
          )}
          {grants.map((g) => (
            <tr key={g.key} className={selectableRowClass(g.key === selectedKey)}>
              <td className="max-w-0 px-4">
                <RowSelect
                  selected={g.key === selectedKey}
                  onSelect={() => onSelect(g.key)}
                  mono={!g.label && entryKind(g.entry) !== "Email"}
                  title={g.label ? `${g.label} (${g.entry})` : g.entry}
                >
                  {g.label ? (
                    <>
                      <span className="block truncate">{g.label}</span>
                      <span className="block truncate font-mono text-[12px] font-normal text-muted-foreground">
                        {g.entry}
                      </span>
                    </>
                  ) : (
                    g.entry
                  )}
                </RowSelect>
              </td>
              <td className="whitespace-nowrap px-4 text-muted-foreground">{entryKind(g.entry)}</td>
              <td className="whitespace-nowrap px-4">
                <Badge variant={g.role.badge} withDot={false}>{g.role.label}</Badge>
              </td>
              <td className={cn("whitespace-nowrap px-4", scope !== "Site" && "font-mono text-[12.5px] text-muted-foreground")}>
                {scopeName(scope)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </WorkspaceTableFrame>
  );
}

export interface GrantInspectorProps {
  scope: string;
  roles: readonly GrantRoleInfo[];
  grants: readonly Grant[];
  /** The grant to show; undefined shows the Add grant form. */
  selected?: Grant;
  /** False while the scope's grants are loading. */
  ready: boolean;
  busy: boolean;
  /** The signed-in user's email, to warn before they remove their own grant. */
  currentUser?: string;
  /** Receives the Principal field, so the page can focus it. */
  principalRef?: Ref<HTMLInputElement>;
  onSelect: (key: string | undefined) => void;
  /** Adds a grant; a non-empty label names the entry. */
  onAdd: (role: GrantRole, entry: string, label?: string) => void;
  /** Renames an entry by re-granting its role with the new label; "" removes the name. */
  onRename: (role: GrantRole, entry: string, label: string) => void;
  onRemove: (role: GrantRole, entry: string) => void;
}

/**
 * The selected grant, with its removal, or the form that adds one. Entries are
 * validated server-side. Keyed by scope and selection so each starts fresh.
 */
export function GrantInspector(props: GrantInspectorProps) {
  return props.selected ? (
    <SelectedGrant key={`${props.scope}:${props.selected.key}`} {...props} selected={props.selected} />
  ) : (
    <AddGrant key={props.scope} {...props} />
  );
}

function SelectedGrant({
  scope,
  grants,
  selected,
  busy,
  currentUser,
  onSelect,
  onRemove,
  onRename,
}: GrantInspectorProps & { selected: Grant }) {
  const [confirming, setConfirming] = useState(false);
  const [label, setLabel] = useState(selected.label ?? "");
  const labelChanged = label.trim() !== (selected.label ?? "");
  const others = grants.filter((g) => g.entry === selected.entry && g.key !== selected.key);
  const ownOwner =
    !!currentUser && selected.entry.toLowerCase() === currentUser.toLowerCase() && selected.role.role === "owner";

  return (
    <Inspector
      eyebrow="Grant"
      title={selected.label ?? selected.entry}
      badge={<Badge variant={selected.role.badge} withDot={false}>{selected.role.label}</Badge>}
      description={selected.role.description}
    >
      {selected.label && (
        <p className="m-0 font-mono text-[12.5px] text-muted-foreground [overflow-wrap:anywhere]">{selected.entry}</p>
      )}
      <form
        aria-label="Rename principal"
        className="flex items-end gap-2"
        onSubmit={(e) => {
          e.preventDefault();
          if (labelChanged) onRename(selected.role.role, selected.entry, label.trim());
        }}
      >
        <label className="block min-w-0 flex-1 space-y-1.5">
          <span className="text-[13px] font-semibold">Name</span>
          <Input
            value={label}
            onChange={(e) => setLabel(e.target.value)}
            placeholder="What this principal is for"
            maxLength={MAX_LABEL_LENGTH}
            disabled={busy}
            className="text-[13px]"
          />
        </label>
        <Button type="submit" size="sm" variant="outline" colorScheme="gray" disabled={busy || !labelChanged}>
          Save name
        </Button>
      </form>
      <InspectorTiles
        tiles={[
          { label: "Scope", value: scope === "Site" ? "Site (every endpoint)" : scope },
          { label: "Role", value: selected.role.label },
          { label: "Type", value: entryKind(selected.entry) },
          { label: "Granted in", value: "NimBus storage" },
        ]}
      />
      {others.length > 0 && (
        <InspectorList
          label="Also granted"
          items={others.map((o) => ({
            key: o.key,
            label: o.role.label,
            meta: scopeName(scope),
            actionLabel: `Open the ${o.role.label} grant`,
            onAction: () => onSelect(o.key),
          }))}
        />
      )}
      {ownOwner && (
        <p className="m-0 rounded-md bg-status-warning-50 p-2.5 text-[12.5px] text-status-warning-ink">
          This is your own Owner grant. Removing it can lock you out of this page unless configuration also makes you an Owner.
        </p>
      )}
      {confirming ? (
        <div className="flex flex-wrap gap-2">
          <Button
            size="sm"
            colorScheme="red"
            disabled={busy}
            aria-label={`Remove ${selected.entry} from ${selected.role.label}`}
            onClick={() => {
              onRemove(selected.role.role, selected.entry);
              onSelect(undefined);
            }}
          >
            Confirm remove
          </Button>
          <Button size="sm" variant="outline" colorScheme="gray" onClick={() => setConfirming(false)}>
            Cancel
          </Button>
        </div>
      ) : (
        <Button size="sm" variant="outline" colorScheme="red" disabled={busy} onClick={() => setConfirming(true)}>
          Remove grant…
        </Button>
      )}
    </Inspector>
  );
}

function AddGrant({ scope, roles, ready, busy, principalRef, onAdd }: GrantInspectorProps) {
  const [entry, setEntry] = useState("");
  const [label, setLabel] = useState("");
  const [role, setRole] = useState<GrantRole>("reader");
  const refused = agentRoleRefused(entry, role);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const value = entry.trim();
    if (!value || refused) return;
    onAdd(role, value, label.trim() || undefined);
    setEntry("");
    setLabel("");
  };

  return (
    <Inspector
      eyebrow="New grant"
      title="Add grant"
      description={
        scope === "Site"
          ? "Grants a role on every endpoint. It takes effect on the next request."
          : `Grants a role on ${scope} only. It takes effect on the next request.`
      }
    >
      <form onSubmit={submit} aria-label="Add grant" className="space-y-4">
        <label className="block space-y-1.5">
          <span className="text-[13px] font-semibold">Principal</span>
          <Input
            ref={principalRef}
            value={entry}
            onChange={(e) => setEntry(e.target.value)}
            placeholder="email, object id or agent:<agentId>"
            disabled={busy || !ready}
            className="font-mono text-[13px]"
          />
        </label>
        <label className="block space-y-1.5">
          <span className="text-[13px] font-semibold">
            Name <span className="font-normal text-muted-foreground">(optional)</span>
          </span>
          <Input
            value={label}
            onChange={(e) => setLabel(e.target.value)}
            placeholder="What this principal is for"
            maxLength={MAX_LABEL_LENGTH}
            disabled={busy || !ready}
            className="text-[13px]"
          />
        </label>
        <InspectorBlock>
          <fieldset className="m-0 space-y-1.5 border-0 p-0">
            <legend className="mb-1.5 text-[13px] font-semibold">Role</legend>
            <div className="flex flex-wrap gap-1.5">
              {roles.map((r) => (
                <label
                  key={r.role}
                  className={cn(
                    "cursor-pointer rounded-full border px-3 py-1 text-[12.5px] transition-colors has-focus-visible:ring-2 has-focus-visible:ring-primary",
                    role === r.role
                      ? "border-foreground bg-foreground text-background"
                      : "border-border bg-background text-foreground hover:bg-muted",
                  )}
                >
                  <input
                    type="radio"
                    name={`grant-role-${scope}`}
                    value={r.role}
                    checked={role === r.role}
                    onChange={() => setRole(r.role)}
                    className="sr-only"
                  />
                  {r.label}
                </label>
              ))}
            </div>
            <p className="m-0 text-[12.5px] text-muted-foreground">{roles.find((r) => r.role === role)?.description}</p>
          </fieldset>
        </InspectorBlock>
        {refused && (
          <p role="alert" className="m-0 rounded-md bg-status-warning-50 p-2.5 text-[12.5px] text-status-warning-ink">
            An agent key can only be Reader or Contributor.
          </p>
        )}
        <Button type="submit" size="sm" disabled={busy || !ready || !entry.trim() || refused}>
          Add grant
        </Button>
      </form>
    </Inspector>
  );
}
