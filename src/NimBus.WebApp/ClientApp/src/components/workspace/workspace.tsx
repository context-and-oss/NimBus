import {
  createContext,
  useContext,
  useId,
  useState,
  type KeyboardEvent,
  type ReactNode,
} from "react";
import { createPortal } from "react-dom";
import { cn } from "lib/utils";

// The Manage pages' shared layout: a chip bar, a header with stat tiles and
// tabs, a table that takes the width, and an inspector for the selected row.

const MICRO =
  "font-mono text-[10.5px] font-medium uppercase tracking-[0.12em] text-muted-foreground";

// Undefined outside a workspace with an inspector slot; null until the slot has mounted.
const InspectorSlotContext = createContext<HTMLElement | null | undefined>(
  undefined,
);

/**
 * Puts its children in the workspace's inspector column, from wherever the
 * component that owns the selection renders. Outside such a workspace the
 * children render in place, so the component works and tests on its own.
 */
export function InspectorPortal({ children }: { children: ReactNode }) {
  const slot = useContext(InspectorSlotContext);
  if (slot === undefined) return <>{children}</>;
  return slot ? createPortal(children, slot) : null;
}

export interface WorkspaceProps {
  title: string;
  /** Rendered beside the title, e.g. a status badge. */
  pill?: ReactNode;
  subtitle?: ReactNode;
  /** Names what the chips choose, e.g. "Topic". */
  chipsLabel?: string;
  /** WorkspaceChip elements, and a filter box when the list is long. */
  chips?: ReactNode;
  /** WorkspaceStat elements. */
  stats?: ReactNode;
  actions?: ReactNode;
  /** A WorkspaceTabs element. */
  tabs?: ReactNode;
  /** The right-hand column. Left out, the table section takes the full width. */
  inspector?: ReactNode;
  /** Shows the inspector column for the children to fill through InspectorPortal. */
  inspectorSlot?: boolean;
  /** A wider inspector, for one that hosts a form. */
  wideInspector?: boolean;
  /** Sticks to the bottom of the viewport, e.g. a save bar. */
  footer?: ReactNode;
  children: ReactNode;
}

/**
 * Page frame for Operations, Topology, Settings and Access Control. From `lg`
 * up the inspector stays in view beside the table while the page scrolls;
 * below that it follows the table.
 */
export function Workspace({
  title,
  pill,
  subtitle,
  chipsLabel,
  chips,
  stats,
  actions,
  tabs,
  inspector,
  inspectorSlot,
  wideInspector,
  footer,
  children,
}: WorkspaceProps) {
  const [slot, setSlot] = useState<HTMLElement | null>(null);
  const hasInspector = inspectorSlot || inspector !== undefined;
  return (
    <InspectorSlotContext.Provider value={inspectorSlot ? slot : undefined}>
      <div className="flex min-h-0 flex-1 flex-col">
        {chips && (
          <div className="flex min-h-[3.25rem] flex-wrap items-center gap-x-3 gap-y-2 border-b border-border px-7 py-2.5">
            {chipsLabel && (
              <span aria-hidden="true" className={MICRO}>
                {chipsLabel}
              </span>
            )}
            <div
              role="group"
              aria-label={chipsLabel}
              className="flex min-w-0 flex-1 flex-wrap items-center gap-1.5"
            >
              {chips}
            </div>
          </div>
        )}

        <header
          className={cn(
            "border-b border-border bg-card px-7 pt-5",
            !tabs && "pb-4",
          )}
        >
          <div className="flex flex-wrap items-end gap-x-5 gap-y-3">
            <div className="min-w-[min(100%,18rem)] flex-1">
              <div className="flex flex-wrap items-center gap-x-3 gap-y-1">
                <h1 className="m-0 text-[26px] font-bold tracking-tight">
                  {title}
                </h1>
                {pill}
              </div>
              {subtitle && (
                <div className="mt-1 max-w-3xl text-[13px] text-muted-foreground">
                  {subtitle}
                </div>
              )}
            </div>
            {(stats || actions) && (
              <div className="flex flex-wrap items-center gap-2">
                {stats}
                {actions}
              </div>
            )}
          </div>
          {tabs && <div className="mt-3">{tabs}</div>}
        </header>

        <div
          className={cn(
            "grid min-h-0 flex-1 grid-cols-1",
            hasInspector &&
              (wideInspector
                ? "lg:grid-cols-[minmax(0,1fr)_minmax(24rem,34rem)]"
                : "lg:grid-cols-[minmax(0,1fr)_minmax(20rem,26rem)]"),
          )}
        >
          <div className="min-w-0 space-y-3 px-7 py-5">{children}</div>
          {hasInspector && (
            <div className="min-w-0 border-t border-border bg-card lg:border-l lg:border-t-0">
              <div className="lg:sticky lg:top-16 lg:max-h-[calc(100vh-4rem)] lg:overflow-y-auto">
                {inspectorSlot ? <div ref={setSlot} /> : inspector}
              </div>
            </div>
          )}
        </div>

        {footer && (
          <div className="sticky bottom-0 z-10 border-t border-border bg-card">
            {footer}
          </div>
        )}
      </div>
    </InspectorSlotContext.Provider>
  );
}

export type ChipTone = "ok" | "warn" | "danger" | "off" | "info";

const DOT: Record<ChipTone, string> = {
  ok: "bg-status-success",
  warn: "bg-status-warning",
  danger: "bg-status-danger",
  info: "bg-status-info",
  off: "bg-muted-foreground/50",
};

export interface WorkspaceChipProps {
  selected: boolean;
  onSelect: () => void;
  /** A status dot before the label. */
  tone?: ChipTone;
  count?: number;
  mono?: boolean;
  title?: string;
  children: ReactNode;
}

/** One choice in the chip bar. Exactly one chip of a bar is pressed. */
export function WorkspaceChip({
  selected,
  onSelect,
  tone,
  count,
  mono,
  title,
  children,
}: WorkspaceChipProps) {
  return (
    <button
      type="button"
      aria-pressed={selected}
      onClick={onSelect}
      title={title}
      className={cn(
        "inline-flex max-w-full items-center gap-1.5 rounded-full border px-3 py-1 text-[12.5px] font-medium transition-colors",
        selected
          ? "border-foreground bg-foreground text-background"
          : "border-border bg-card text-foreground hover:border-border-strong",
      )}
    >
      {tone && (
        <span
          aria-hidden="true"
          className={cn("h-1.5 w-1.5 shrink-0 rounded-full", DOT[tone])}
        />
      )}
      <span className={cn("truncate", mono && "font-mono text-[12px]")}>
        {children}
      </span>
      {count !== undefined && (
        <span className="font-mono text-[11px] opacity-80">{count}</span>
      )}
    </button>
  );
}

export interface WorkspaceStatProps {
  label: string;
  value: ReactNode;
  tone?: "default" | "warn" | "danger" | "ok";
  onClick?: () => void;
  title?: string;
}

const STAT_TONE = {
  default: "text-foreground",
  warn: "text-status-warning-ink dark:text-yellow-200",
  danger: "text-status-danger-ink dark:text-red-200",
  ok: "text-status-success-ink dark:text-green-200",
} as const;

/** A compact figure in the header; a shortcut when it has an onClick. */
export function WorkspaceStat({
  label,
  value,
  tone = "default",
  onClick,
  title,
}: WorkspaceStatProps) {
  const Comp = onClick ? "button" : "div";
  return (
    <Comp
      {...(onClick ? { type: "button" as const, onClick } : {})}
      title={title}
      className={cn(
        "flex min-w-[5.5rem] flex-col gap-px rounded-nb-md border border-border bg-background px-3 py-1.5 text-left",
        onClick && "transition-colors hover:border-border-strong",
      )}
    >
      <span
        className={cn(
          "text-[17px] font-semibold leading-tight tabular-nums",
          STAT_TONE[tone],
        )}
      >
        {value}
      </span>
      <span className="font-mono text-[10px] uppercase tracking-[0.06em] text-muted-foreground">
        {label}
      </span>
    </Comp>
  );
}

export interface WorkspaceTabItem {
  id: string;
  label: string;
  count?: ReactNode;
  disabled?: boolean;
  /** Why a disabled tab can't be opened. */
  title?: string;
}

export interface WorkspaceTabsProps {
  /** Accessible name of the tab list. */
  label: string;
  tabs: readonly WorkspaceTabItem[];
  active: string;
  onChange: (id: string) => void;
}

/** The header's tab strip. Left and Right arrows move between tabs. */
export function WorkspaceTabs({
  label,
  tabs,
  active,
  onChange,
}: WorkspaceTabsProps) {
  const onKeyDown = (e: KeyboardEvent<HTMLDivElement>) => {
    if (e.key !== "ArrowRight" && e.key !== "ArrowLeft") return;
    const open = tabs.filter((t) => !t.disabled);
    const at = open.findIndex((t) => t.id === active);
    if (at < 0 || open.length < 2) return;
    e.preventDefault();
    const next =
      open[(at + (e.key === "ArrowRight" ? 1 : open.length - 1)) % open.length];
    onChange(next.id);
    const buttons =
      e.currentTarget.querySelectorAll<HTMLElement>('[role="tab"]');
    buttons[tabs.indexOf(next)]?.focus();
  };

  return (
    <div
      role="tablist"
      aria-label={label}
      onKeyDown={onKeyDown}
      className="-mb-px flex gap-1 overflow-x-auto"
    >
      {tabs.map((tab) => {
        const selected = tab.id === active;
        return (
          <button
            key={tab.id}
            type="button"
            role="tab"
            aria-selected={selected}
            aria-disabled={tab.disabled || undefined}
            tabIndex={selected ? 0 : -1}
            title={tab.title}
            onClick={() => !tab.disabled && onChange(tab.id)}
            className={cn(
              "whitespace-nowrap border-b-2 px-3 py-2 text-[13.5px] transition-colors",
              selected
                ? "border-primary font-semibold text-foreground"
                : "border-transparent text-muted-foreground hover:text-foreground",
              tab.disabled &&
                "cursor-default opacity-60 hover:text-muted-foreground",
            )}
          >
            {tab.label}
            {tab.count !== undefined && (
              <span className="ml-1.5 font-mono text-[11px] font-normal">
                {tab.count}
              </span>
            )}
          </button>
        );
      })}
    </div>
  );
}

export interface WorkspaceSectionProps {
  title: string;
  /** Shown after the title, e.g. the row count. */
  count?: ReactNode;
  intro?: ReactNode;
  /** A control at the section's top right, e.g. Refresh. */
  action?: ReactNode;
  /** A note under the table. */
  foot?: ReactNode;
  children: ReactNode;
}

/** The table section: a heading, what the table is for, and the table. */
export function WorkspaceSection({
  title,
  count,
  intro,
  action,
  foot,
  children,
}: WorkspaceSectionProps) {
  const id = useId();
  return (
    <section aria-labelledby={id} className="min-w-0 space-y-3">
      <div className="flex flex-wrap items-center justify-between gap-3">
        <div className="min-w-0 space-y-0.5">
          <h2 className="m-0 text-[17px] font-semibold">
            <span id={id}>{title}</span>
            {count !== undefined && (
              <span className="ml-1.5 text-[13px] font-medium text-muted-foreground">
                · {count}
              </span>
            )}
          </h2>
          {intro && (
            <p className="m-0 max-w-3xl text-[12.5px] text-muted-foreground">
              {intro}
            </p>
          )}
        </div>
        {action && (
          <div className="flex flex-wrap items-center gap-2">{action}</div>
        )}
      </div>
      {children}
      {foot && (
        <p className="m-0 text-[12.5px] text-muted-foreground">{foot}</p>
      )}
    </section>
  );
}

export interface InspectorProps {
  /** What kind of thing is selected, e.g. "Subscription". */
  eyebrow?: string;
  /** Left out, the inspector shows its empty state. */
  title?: ReactNode;
  /** Beside the title, e.g. a risk or role badge. */
  badge?: ReactNode;
  /** At the top right, e.g. On, Paused. */
  state?: ReactNode;
  description?: ReactNode;
  /** Shown when nothing is selected. */
  empty?: ReactNode;
  children?: ReactNode;
}

/** Details and actions for the selected row. */
export function Inspector({
  eyebrow,
  title,
  badge,
  state,
  description,
  empty,
  children,
}: InspectorProps) {
  return (
    <aside aria-label="Inspector" className="space-y-4 px-6 py-5">
      {title === undefined ? (
        <p className="m-0 px-2 py-10 text-center text-sm text-muted-foreground">
          {empty ?? "Select a row to see its details here."}
        </p>
      ) : (
        <>
          <div className="flex items-start justify-between gap-3">
            <div className="min-w-0 space-y-1">
              {eyebrow && <div className={MICRO}>{eyebrow}</div>}
              <h3 className="m-0 flex flex-wrap items-center gap-2 text-[18px] font-semibold [overflow-wrap:anywhere]">
                {title}
                {badge}
              </h3>
            </div>
            {state && <div className="shrink-0">{state}</div>}
          </div>
          {description && (
            <p className="m-0 text-[13.5px] leading-relaxed text-foreground/90">
              {description}
            </p>
          )}
          {children}
        </>
      )}
    </aside>
  );
}

/** A labelled group inside the inspector. */
export function InspectorBlock({
  label,
  children,
}: {
  label?: string;
  children: ReactNode;
}) {
  return (
    <div className="space-y-2">
      {label && <div className={MICRO}>{label}</div>}
      {children}
    </div>
  );
}

export interface InspectorTile {
  label: string;
  value: ReactNode;
  tone?: WorkspaceStatProps["tone"];
}

/** Key figures of the selected row, two to a line. */
export function InspectorTiles({
  label,
  tiles,
}: {
  label?: string;
  tiles: readonly InspectorTile[];
}) {
  return (
    <InspectorBlock label={label}>
      <dl className="m-0 grid grid-cols-2 gap-1.5">
        {tiles.map((t) => (
          <div
            key={t.label}
            className="min-w-0 rounded-nb-md border border-border bg-background px-2.5 py-2"
          >
            <dt className="text-[11px] text-muted-foreground">{t.label}</dt>
            <dd
              className={cn(
                "m-0 font-mono text-[12px] [overflow-wrap:anywhere]",
                STAT_TONE[t.tone ?? "default"],
              )}
            >
              {t.value}
            </dd>
          </div>
        ))}
      </dl>
    </InspectorBlock>
  );
}

export interface InspectorListItem {
  key: string;
  label: string;
  meta?: string;
  /** The action's visible text; defaults to "Open ›". */
  action?: string;
  /** Accessible name when the visible text alone is ambiguous. */
  actionLabel?: string;
  danger?: boolean;
  onAction: () => void;
}

/** Related things to open from the selected row. */
export function InspectorList({
  label,
  items,
}: {
  label?: string;
  items: readonly InspectorListItem[];
}) {
  return (
    <InspectorBlock label={label}>
      <ul className="m-0 list-none divide-y divide-border rounded-nb-md border border-border bg-background p-0">
        {items.map((item) => (
          <li key={item.key} className="flex items-center gap-2.5 px-3 py-2">
            <span className="min-w-0 flex-1">
              <span
                className={cn(
                  "block text-[13px] font-semibold",
                  item.danger && "text-status-danger",
                )}
              >
                {item.label}
              </span>
              {item.meta && (
                <span className="block text-[11.5px] text-muted-foreground">
                  {item.meta}
                </span>
              )}
            </span>
            <button
              type="button"
              aria-label={item.actionLabel}
              onClick={item.onAction}
              className="whitespace-nowrap py-1 text-[12.5px] font-semibold text-primary-700 hover:underline dark:text-primary-400"
            >
              {item.action ?? "Open ›"}
            </button>
          </li>
        ))}
      </ul>
    </InspectorBlock>
  );
}
