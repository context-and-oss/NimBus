import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useId,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent,
  type MouseEvent,
  type ReactNode,
} from "react";
import { createPortal } from "react-dom";
import { cn } from "lib/utils";

// The Manage pages' shared layout: a chip bar, a header with stat tiles and
// tabs, a table that takes the full width, and an inspector drawer that slides
// in over the table's right side while a row is selected.

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

interface DrawerValue {
  /** Marks the drawer open until the returned cleanup runs. */
  register: () => () => void;
  /** Selects the visible row before (-1) or after (1) the selected one. */
  step: (direction: -1 | 1) => void;
  canStep: { previous: boolean; next: boolean };
  /** The selected row's select button, which takes focus back when the drawer closes. */
  selectedRow: () => HTMLElement | undefined;
}

// Set inside a workspace that has an inspector: an Inspector there is a drawer.
const DrawerContext = createContext<DrawerValue | null>(null);

/**
 * True inside a workspace whose inspector is a drawer. A component whose
 * inspector holds a form when nothing is selected (Add grant, Issue key) shows
 * that form only on request there; rendered on its own it shows it always.
 */
export const useInspectorDrawer = (): boolean => useContext(DrawerContext) !== null;

/** Marks the button in a row's first cell that selects the row (see RowSelect). */
export const ROW_SELECT_ATTRIBUTE = "data-row-select";

// A click on one of these inside a row belongs to the control, not the row.
const INTERACTIVE =
  'button, a, input, select, textarea, label, summary, [role="menu"], [role="menuitem"], [contenteditable="true"]';

// Up and Down belong to these while they have focus.
const OWNS_ARROWS =
  'input, textarea, select, [contenteditable="true"], [role="menu"], [role="listbox"], [role="radiogroup"], [role="slider"]';

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
  /**
   * The inspector drawer's content. The drawer is open while it holds an
   * Inspector with a title, i.e. while a row is selected.
   */
  inspector?: ReactNode;
  /** Gives the children an inspector drawer to fill through InspectorPortal. */
  inspectorSlot?: boolean;
  /** A wider drawer, for one that hosts a form. */
  wideInspector?: boolean;
  /** Sticks to the bottom of the viewport, e.g. a save bar. */
  footer?: ReactNode;
  children: ReactNode;
}

/**
 * Page frame for Operations, Topology, Settings and Access Control. The table
 * takes the full width; a click anywhere on a row selects it, and the selected
 * row's Inspector slides in from the right over the table, like Claude
 * Console's. Up and Down step through the rows while it is open; Escape and
 * its close button close it.
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
  const [openCount, setOpenCount] = useState(0);
  const [canStep, setCanStep] = useState({ previous: false, next: false });
  const tableRef = useRef<HTMLDivElement>(null);
  const hasInspector = inspectorSlot || inspector !== undefined;
  const open = hasInspector && openCount > 0;

  // The visible rows' select buttons, in table order. A table hidden on
  // another tab keeps its rows mounted, so leave those out.
  const rowButtons = useCallback(
    () =>
      Array.from(
        tableRef.current?.querySelectorAll<HTMLElement>(`[${ROW_SELECT_ATTRIBUTE}]`) ?? [],
      ).filter((b) => !b.closest("[hidden]")),
    [],
  );

  const selectedRow = useCallback(
    () => rowButtons().find((b) => b.getAttribute("aria-pressed") === "true"),
    [rowButtons],
  );

  const step = useCallback(
    (direction: -1 | 1) => {
      const rows = rowButtons();
      const at = rows.findIndex((b) => b.getAttribute("aria-pressed") === "true");
      const target = at < 0 ? undefined : rows[at + direction];
      if (!target) return;
      target.click();
      target.closest("tr")?.scrollIntoView?.({ block: "nearest" });
    },
    [rowButtons],
  );

  // Keep the drawer's Up and Down buttons in step with the selection.
  useEffect(() => {
    const table = tableRef.current;
    if (!open || !table) return;
    const update = () => {
      const rows = rowButtons();
      const at = rows.findIndex((b) => b.getAttribute("aria-pressed") === "true");
      setCanStep((prev) => {
        const next = { previous: at > 0, next: at >= 0 && at < rows.length - 1 };
        return prev.previous === next.previous && prev.next === next.next ? prev : next;
      });
    };
    update();
    const observer = new MutationObserver(update);
    observer.observe(table, {
      subtree: true,
      childList: true,
      attributes: true,
      attributeFilter: ["aria-pressed", "hidden"],
    });
    return () => observer.disconnect();
  }, [open, rowButtons]);

  const register = useCallback(() => {
    setOpenCount((c) => c + 1);
    return () => setOpenCount((c) => c - 1);
  }, []);

  const drawer = useMemo<DrawerValue>(
    () => ({ register, step, canStep, selectedRow }),
    [register, step, canStep, selectedRow],
  );

  // A click anywhere on a row selects it, unless it lands on one of the row's
  // own controls or ends a text selection.
  const onTableClick = (e: MouseEvent<HTMLDivElement>) => {
    const target = e.target as HTMLElement;
    if (target.closest(INTERACTIVE)) return;
    if (window.getSelection?.()?.toString()) return;
    const row = target.closest("tr");
    if (!row || !tableRef.current?.contains(row)) return;
    row.querySelector<HTMLElement>(`[${ROW_SELECT_ATTRIBUTE}]`)?.click();
  };

  return (
    <InspectorSlotContext.Provider value={inspectorSlot ? slot : undefined}>
      <DrawerContext.Provider value={hasInspector ? drawer : null}>
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

          {/* Each row's select button is the keyboard path; this only widens the mouse target. */}
          <div
            ref={tableRef}
            onClick={onTableClick}
            className="min-h-0 min-w-0 flex-1 space-y-3 px-7 py-5"
          >
            {children}
          </div>

          {footer && (
            <div className="sticky bottom-0 z-10 border-t border-border bg-card">
              {footer}
            </div>
          )}
        </div>

        {hasInspector && (
          <div
            data-testid="inspector-drawer"
            data-open={open}
            className={cn(
              "fixed inset-y-0 right-0 z-40 flex w-full max-w-full flex-col border-l border-border bg-card shadow-nb-lg",
              "transition-[transform,visibility] duration-200 ease-out motion-reduce:transition-none",
              wideInspector ? "sm:w-[40rem]" : "sm:w-[32rem]",
              open ? "visible translate-x-0" : "invisible translate-x-full",
            )}
          >
            <div className="min-h-0 flex-1 overflow-y-auto">
              {inspectorSlot ? <div ref={setSlot} /> : inspector}
            </div>
          </div>
        )}
      </DrawerContext.Provider>
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
  /** Shown when nothing is selected. In a workspace the drawer is closed instead. */
  empty?: ReactNode;
  /** In the drawer's top bar after the eyebrow, e.g. the selected row's id. */
  reference?: ReactNode;
  /** Beside the title, e.g. the row's ⋯ menu (RowActions). */
  menu?: ReactNode;
  /** Closes the drawer, usually by clearing the selection. */
  onClose?: () => void;
  children?: ReactNode;
}

/**
 * Details and actions for the selected row. In a workspace it is the drawer:
 * open while it has a title, with a top bar to step through the rows and close
 * it. Elsewhere it renders in place.
 */
export function Inspector(props: InspectorProps) {
  const drawer = useContext(DrawerContext);
  if (drawer && props.title !== undefined)
    return <DrawerInspector {...props} drawer={drawer} />;
  // A workspace's drawer stays closed while nothing is selected.
  if (drawer) return null;
  return <InlineInspector {...props} />;
}

const DRAWER_BUTTON =
  "inline-flex h-8 w-8 items-center justify-center rounded-nb-sm text-muted-foreground transition-colors hover:bg-muted hover:text-foreground disabled:pointer-events-none disabled:opacity-35";

function ChevronIcon({ up }: { up?: boolean }) {
  return (
    <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
      <path d={up ? "m6 15 6-6 6 6" : "m6 9 6 6 6-6"} />
    </svg>
  );
}

function DrawerInspector({
  eyebrow,
  title,
  badge,
  state,
  description,
  reference,
  menu,
  onClose,
  children,
  drawer,
}: InspectorProps & { drawer: DrawerValue }) {
  const { register, step, canStep, selectedRow } = drawer;
  useEffect(register, [register]);

  const close = useCallback(() => {
    if (!onClose) return;
    const row = selectedRow();
    onClose();
    // The close button goes with the drawer; give focus back to the row.
    row?.focus();
  }, [onClose, selectedRow]);

  useEffect(() => {
    const onKey = (e: globalThis.KeyboardEvent) => {
      // A modal or menu on top takes its own keys (they mark them handled).
      if (e.defaultPrevented || document.querySelector('[aria-modal="true"]')) return;
      if (e.key === "Escape") {
        close();
        return;
      }
      if (e.key !== "ArrowUp" && e.key !== "ArrowDown") return;
      if (e.altKey || e.ctrlKey || e.metaKey || e.shiftKey) return;
      if (e.target instanceof Element && e.target.closest(OWNS_ARROWS)) return;
      e.preventDefault();
      step(e.key === "ArrowUp" ? -1 : 1);
    };
    document.addEventListener("keydown", onKey);
    return () => document.removeEventListener("keydown", onKey);
  }, [close, step]);

  return (
    <aside aria-label="Inspector" className="flex min-h-full flex-col">
      <div className="sticky top-0 z-10 flex items-center gap-2.5 bg-card py-3 pl-6 pr-3">
        {eyebrow && <span className="shrink-0 text-[13px] font-semibold">{eyebrow}</span>}
        {reference && (
          <span className="min-w-0 truncate font-mono text-[12px] text-muted-foreground">{reference}</span>
        )}
        <div className="ml-auto flex shrink-0 items-center gap-0.5">
          <button type="button" aria-label="Previous row" title="Previous row (↑)" disabled={!canStep.previous} onClick={() => step(-1)} className={DRAWER_BUTTON}>
            <ChevronIcon up />
          </button>
          <button type="button" aria-label="Next row" title="Next row (↓)" disabled={!canStep.next} onClick={() => step(1)} className={DRAWER_BUTTON}>
            <ChevronIcon />
          </button>
          {onClose && (
            <button type="button" aria-label="Close inspector" title="Close (Esc)" onClick={close} className={DRAWER_BUTTON}>
              <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" strokeWidth="2" strokeLinecap="round" aria-hidden="true">
                <path d="M6 6l12 12M18 6 6 18" />
              </svg>
            </button>
          )}
        </div>
      </div>
      <div className="flex items-start gap-3 border-b border-border px-6 pb-4">
        <h3 className="m-0 flex min-w-0 flex-1 flex-wrap items-center gap-2 text-[19px] font-semibold [overflow-wrap:anywhere]">
          {title}
          {badge}
        </h3>
        {state && <div className="shrink-0 pt-0.5">{state}</div>}
        {menu && <div className="-mr-2 shrink-0">{menu}</div>}
      </div>
      <div className="space-y-4 px-6 py-5">
        {description && (
          <p className="m-0 text-[13.5px] leading-relaxed text-foreground/90">
            {description}
          </p>
        )}
        {children}
      </div>
    </aside>
  );
}

function InlineInspector({
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
