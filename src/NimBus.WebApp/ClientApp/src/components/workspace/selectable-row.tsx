import type { ReactNode } from "react";
import { DropdownItem, DropdownMenu, DropdownSeparator } from "components/ui/dropdown-menu";
import { cn } from "lib/utils";
import { ROW_SELECT_ATTRIBUTE } from "./workspace";

// A workspace table: a click on a row selects it and opens the inspector
// drawer. The row's first cell holds the button that selects it (the keyboard
// path); its last cell can hold the row's ⋯ menu.

/** Header cell classes shared by the workspace tables. */
export const WORKSPACE_TH = "px-4 py-2 font-medium";

/** Header row classes shared by the workspace tables. */
export const WORKSPACE_HEAD_ROW =
  "border-b border-border text-left text-[12.5px] text-muted-foreground";

/** The bordered, sideways-scrolling box around a workspace table. */
export function WorkspaceTableFrame({ children, className }: { children: ReactNode; className?: string }) {
  return (
    <div className={cn("min-w-0 overflow-x-auto rounded-nb-md border border-border bg-card", className)}>
      {children}
    </div>
  );
}

/** Row classes: the selected row is tinted and marked on its leading edge. */
export const selectableRowClass = (selected: boolean) =>
  cn(
    "cursor-pointer border-b border-border transition-colors last:border-b-0",
    selected
      ? "bg-primary/10 [&>td:first-child]:shadow-[inset_3px_0_0_var(--color-primary)]"
      : "hover:bg-muted/50",
  );

export interface RowSelectProps {
  selected: boolean;
  onSelect: () => void;
  mono?: boolean;
  /** Full text for a name that may be cut short. */
  title?: string;
  /** Shown after the name, e.g. a "Paused" note. */
  after?: ReactNode;
  children: ReactNode;
}

/** The button in a row's first cell that selects the row. */
export function RowSelect({ selected, onSelect, mono, title, after, children }: RowSelectProps) {
  return (
    <span className="flex min-w-0 items-center gap-2">
      <button
        type="button"
        aria-pressed={selected}
        onClick={onSelect}
        title={title}
        {...{ [ROW_SELECT_ATTRIBUTE]: "" }}
        className={cn(
          "block min-w-0 truncate py-2.5 text-left text-[13.5px] font-semibold text-foreground",
          mono && "font-mono text-[13px]",
        )}
      >
        {children}
      </button>
      {after}
    </span>
  );
}

export interface RowAction {
  label: string;
  onSelect: () => void;
  /** Shown in red, after a separator from the safe actions. */
  destructive?: boolean;
  disabled?: boolean;
  /** Why the action is disabled, or what it does. */
  title?: string;
}

const MoreIcon = () => (
  <svg width="18" height="18" viewBox="0 0 24 24" aria-hidden="true" fill="currentColor">
    <circle cx="5" cy="12" r="1.7" />
    <circle cx="12" cy="12" r="1.7" />
    <circle cx="19" cy="12" r="1.7" />
  </svg>
);

/**
 * A row's ⋯ menu. Destructive actions come last, in red, below a separator.
 * Renders nothing when there are no actions.
 */
export function RowActions({ label, actions }: { label: string; actions: readonly RowAction[] }) {
  if (actions.length === 0) return null;
  const safe = actions.filter((a) => !a.destructive);
  const destructive = actions.filter((a) => a.destructive);
  const item = (a: RowAction) => (
    <DropdownItem key={a.label} onSelect={a.onSelect} destructive={a.destructive} disabled={a.disabled} title={a.title}>
      {a.label}
    </DropdownItem>
  );
  return (
    <DropdownMenu trigger={<MoreIcon />} triggerLabel={label}>
      {safe.map(item)}
      {safe.length > 0 && destructive.length > 0 && <DropdownSeparator />}
      {destructive.map(item)}
    </DropdownMenu>
  );
}

/** The header cell over the rows' ⋯ menus. */
export function RowActionsHeader() {
  return (
    <th scope="col" className="w-12 px-2 py-2">
      <span className="sr-only">Actions</span>
    </th>
  );
}

/** The cell that holds a row's ⋯ menu. */
export function RowActionsCell({ label, actions }: { label: string; actions: readonly RowAction[] }) {
  return (
    <td className="w-12 px-2 py-1 text-right">
      <RowActions label={label} actions={actions} />
    </td>
  );
}
