import type { ReactNode } from "react";
import { cn } from "lib/utils";

// A workspace table: one row is selected and shown in the inspector. The
// row's first cell holds the button that selects it.

/** Header cell classes shared by the workspace tables. */
export const WORKSPACE_TH = "px-4 py-2 font-medium";

/** Header row classes shared by the workspace tables. */
export const WORKSPACE_HEAD_ROW =
  "border-b border-border text-left font-mono text-[10.5px] uppercase tracking-[0.1em] text-muted-foreground";

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
    "border-b border-border last:border-b-0",
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
