import { useEffect, useState, type FormEvent } from "react";
import * as api from "api-client";
import { Badge, type BadgeVariant } from "components/ui/badge";
import { Button } from "components/ui/button";
import { Input } from "components/ui/input";
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

export interface GrantsPanelProps {
  /** "Site" or the endpoint ID; names the scope in labels. */
  scope: string;
  roles: readonly GrantRoleInfo[];
  /** Null while the scope's grants are loading. */
  set: api.AccessControlSet | null;
  busy: boolean;
  /** The signed-in user's email, to warn before they remove their own grant. */
  currentUser?: string;
  onAdd: (role: GrantRole, entry: string) => void;
  onRemove: (role: GrantRole, entry: string) => void;
}

interface Grant {
  key: string;
  role: GrantRoleInfo;
  entry: string;
}

const AGENT_PREFIX = "agent:";

/** What an entry names: an agent of an MCP key, an email address or an Entra object ID. */
export const entryKind = (entry: string): string =>
  entry.toLowerCase().startsWith(AGENT_PREFIX) ? "Agent" : entry.includes("@") ? "Email" : "Object ID";

// Agent keys act as workloads: the server accepts them as Reader or Contributor only.
const agentRoleRefused = (entry: string, role: GrantRole) =>
  entry.trim().toLowerCase().startsWith(AGENT_PREFIX) && (role === "owner" || role === "piiReader");

/**
 * A scope's grants as one table, one row per entry and role, with the selected
 * grant and an Add grant form beside it. Entries are validated server-side.
 */
export function GrantsPanel({ scope, roles, set, busy, currentUser, onAdd, onRemove }: GrantsPanelProps) {
  const grants: Grant[] = set
    ? roles.flatMap((role) => role.pick(set).map((entry) => ({ key: `${role.role}:${entry}`, role, entry })))
    : [];
  const [selectedKey, setSelectedKey] = useState<string>();
  const [confirming, setConfirming] = useState(false);
  const [entry, setEntry] = useState("");
  const [role, setRole] = useState<GrantRole>("reader");

  // A different scope starts with nothing selected and an empty form.
  useEffect(() => {
    setSelectedKey(undefined);
    setConfirming(false);
    setEntry("");
    setRole("reader");
  }, [scope]);

  const selected = grants.find((g) => g.key === selectedKey);
  const refused = agentRoleRefused(entry, role);

  const submit = (e: FormEvent) => {
    e.preventDefault();
    const value = entry.trim();
    if (!value || refused) return;
    onAdd(role, value);
    setEntry("");
  };

  return (
    <div className="grid min-w-0 items-start gap-4 xl:grid-cols-[minmax(0,1fr)_20rem]">
      <div className="min-w-0 overflow-x-auto rounded-nb-md border border-border bg-card">
        <table className="w-full min-w-[32rem] border-collapse text-[13px]" aria-label={`${scope} grants`}>
          <thead>
            <tr className="border-b border-border text-left font-mono text-[10.5px] uppercase tracking-[0.1em] text-muted-foreground">
              <th scope="col" className="px-4 py-2 font-medium">Principal</th>
              <th scope="col" className="px-4 py-2 font-medium">Type</th>
              <th scope="col" className="px-4 py-2 font-medium">Role</th>
            </tr>
          </thead>
          <tbody>
            {!set && (
              <tr>
                <td colSpan={3} className="px-4 py-6 text-center text-muted-foreground">Loading grants…</td>
              </tr>
            )}
            {set && grants.length === 0 && (
              <tr>
                <td colSpan={3} className="px-4 py-6 text-center text-muted-foreground">
                  No grants stored for {scope === "Site" ? "the site" : scope}.
                </td>
              </tr>
            )}
            {grants.map((g) => {
              const isSelected = g.key === selectedKey;
              return (
                <tr
                  key={g.key}
                  className={cn(
                    "border-b border-border last:border-b-0",
                    isSelected ? "bg-primary/10 [&>td:first-child]:shadow-[inset_3px_0_0_var(--color-primary)]" : "hover:bg-muted/50",
                  )}
                >
                  <td className="max-w-0 px-4">
                    <button
                      type="button"
                      aria-pressed={isSelected}
                      onClick={() => {
                        setSelectedKey(g.key);
                        setConfirming(false);
                      }}
                      className="block w-full truncate py-2.5 text-left font-mono font-semibold text-foreground"
                      title={g.entry}
                    >
                      {g.entry}
                    </button>
                  </td>
                  <td className="whitespace-nowrap px-4 text-muted-foreground">{entryKind(g.entry)}</td>
                  <td className="whitespace-nowrap px-4">
                    <Badge variant={g.role.badge} withDot={false}>{g.role.label}</Badge>
                  </td>
                </tr>
              );
            })}
          </tbody>
        </table>
      </div>

      <aside aria-label="Grant details" className="space-y-4">
        {selected && (
          <section aria-label={`${selected.entry} as ${selected.role.label}`} className="space-y-3 rounded-nb-md border border-border bg-card p-4">
            <div className="space-y-1">
              <div className="font-mono text-[10.5px] uppercase tracking-[0.12em] text-muted-foreground">Grant</div>
              <h3 className="m-0 break-all text-[15px] font-semibold">{selected.entry}</h3>
              <div className="flex flex-wrap items-center gap-2 text-[12px] text-muted-foreground">
                <Badge variant={selected.role.badge} withDot={false}>{selected.role.label}</Badge>
                <span>{entryKind(selected.entry)}</span>
                <span>·</span>
                <span>{scope === "Site" ? "Every endpoint" : scope}</span>
              </div>
            </div>
            <p className="m-0 text-[13px] text-muted-foreground">{selected.role.description}</p>
            {(() => {
              const others = grants.filter((g) => g.entry === selected.entry && g.key !== selected.key);
              return others.length > 0 ? (
                <p className="m-0 text-[12.5px] text-muted-foreground">
                  Also {others.map((o) => o.role.label).join(", ")} here.
                </p>
              ) : null;
            })()}
            {currentUser && selected.entry.toLowerCase() === currentUser.toLowerCase() && selected.role.role === "owner" && (
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
                    setSelectedKey(undefined);
                    setConfirming(false);
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
          </section>
        )}

        <form onSubmit={submit} aria-label="Add grant" className="space-y-3 rounded-nb-md border border-border bg-card p-4">
          <h3 className="m-0 text-[15px] font-semibold">Add grant</h3>
          <label className="block space-y-1.5">
            <span className="text-[12.5px] font-medium">Principal</span>
            <Input
              value={entry}
              onChange={(e) => setEntry(e.target.value)}
              placeholder="email, object id or agent:<agentId>"
              disabled={busy || !set}
              className="font-mono text-[13px]"
            />
          </label>
          <fieldset className="m-0 space-y-1.5 border-0 p-0">
            <legend className="mb-1.5 text-[12.5px] font-medium">Role</legend>
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
            <p className="m-0 text-[12px] text-muted-foreground">{roles.find((r) => r.role === role)?.description}</p>
          </fieldset>
          {refused && (
            <p role="alert" className="m-0 rounded-md bg-status-warning-50 p-2.5 text-[12.5px] text-status-warning-ink">
              An agent key can only be Reader or Contributor.
            </p>
          )}
          <Button type="submit" size="sm" disabled={busy || !set || !entry.trim() || refused}>
            Add grant
          </Button>
        </form>
      </aside>
    </div>
  );
}
