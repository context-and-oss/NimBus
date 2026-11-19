import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  type KeyboardEvent as ReactKeyboardEvent,
  type ReactNode,
} from "react";
import { createPortal } from "react-dom";
import { cn } from "lib/utils";

interface DropdownContextValue {
  close: () => void;
}

const DropdownContext = createContext<DropdownContextValue>({
  close: () => {},
});

export interface DropdownMenuProps {
  /** Content of the trigger button (e.g. an icon). */
  trigger: ReactNode;
  triggerLabel: string;
  /** Menu items: <DropdownItem> / <DropdownSeparator>. */
  children: ReactNode;
  align?: "left" | "right";
  triggerClassName?: string;
  menuClassName?: string;
}

// Click-triggered menu portaled to document.body with position:fixed
// coordinates derived from the trigger's bounding rect — the same approach
// Tooltip takes. That is required inside the data-table, whose horizontal
// scroll container and truncating cells would otherwise clip an in-flow
// absolutely-positioned menu.
export function DropdownMenu({
  trigger,
  triggerLabel,
  children,
  align = "right",
  triggerClassName,
  menuClassName,
}: DropdownMenuProps) {
  const [open, setOpen] = useState(false);
  const [coords, setCoords] = useState<{
    top: number;
    left?: number;
    right?: number;
  }>({ top: 0 });
  const triggerRef = useRef<HTMLButtonElement>(null);
  const menuRef = useRef<HTMLDivElement>(null);

  const close = useCallback(() => setOpen(false), []);

  const computePosition = useCallback(() => {
    const el = triggerRef.current;
    if (!el) return;
    const r = el.getBoundingClientRect();
    const base =
      align === "right"
        ? { right: window.innerWidth - r.right }
        : { left: r.left };
    setCoords({ top: r.bottom + 4, ...base });
  }, [align]);

  // Flip upward when the menu would overflow the bottom of the viewport.
  useLayoutEffect(() => {
    if (!open) return;
    const trig = triggerRef.current;
    const menu = menuRef.current;
    if (!trig || !menu) return;
    const r = trig.getBoundingClientRect();
    const h = menu.offsetHeight;
    if (r.bottom + h + 8 > window.innerHeight && r.top - h - 8 > 0) {
      setCoords((c) => ({ ...c, top: r.top - h - 4 }));
    }
    menu
      .querySelector<HTMLElement>('[role="menuitem"]:not([disabled])')
      ?.focus();
  }, [open]);

  useEffect(() => {
    if (!open) return;
    const onDocMouseDown = (e: MouseEvent) => {
      if (
        !triggerRef.current?.contains(e.target as Node) &&
        !menuRef.current?.contains(e.target as Node)
      ) {
        close();
      }
    };
    // Capture phase, and mark the key handled, so Escape closes this menu
    // and not an inspector drawer or side panel it sits in.
    const onKey = (e: KeyboardEvent) => {
      if (e.key !== "Escape") return;
      e.preventDefault();
      close();
    };
    // Capture scroll on any ancestor (the table's scroll container) so the
    // menu can't drift away from its trigger; dismissing is the simplest
    // correct behaviour.
    const onReflow = () => close();
    document.addEventListener("mousedown", onDocMouseDown);
    document.addEventListener("keydown", onKey, true);
    window.addEventListener("scroll", onReflow, true);
    window.addEventListener("resize", onReflow);
    return () => {
      document.removeEventListener("mousedown", onDocMouseDown);
      document.removeEventListener("keydown", onKey, true);
      window.removeEventListener("scroll", onReflow, true);
      window.removeEventListener("resize", onReflow);
    };
  }, [open, close]);

  const onMenuKeyDown = (e: ReactKeyboardEvent) => {
    if (e.key !== "ArrowDown" && e.key !== "ArrowUp") return;
    e.preventDefault();
    const items = Array.from(
      menuRef.current?.querySelectorAll<HTMLElement>(
        '[role="menuitem"]:not([disabled])',
      ) ?? [],
    );
    if (items.length === 0) return;
    const idx = items.indexOf(document.activeElement as HTMLElement);
    const next =
      e.key === "ArrowDown"
        ? (idx + 1) % items.length
        : (idx - 1 + items.length) % items.length;
    items[next].focus();
  };

  return (
    <span className="relative inline-flex">
      <button
        ref={triggerRef}
        type="button"
        aria-haspopup="menu"
        aria-expanded={open}
        aria-label={triggerLabel}
        onClick={(e) => {
          e.preventDefault();
          e.stopPropagation();
          if (open) {
            close();
          } else {
            computePosition();
            setOpen(true);
          }
        }}
        className={cn(
          "inline-flex h-8 w-8 items-center justify-center rounded-nb-sm text-muted-foreground transition-colors",
          "hover:bg-muted hover:text-foreground",
          open && "bg-muted text-foreground",
          triggerClassName,
        )}
      >
        {trigger}
      </button>
      {open &&
        createPortal(
          <DropdownContext.Provider value={{ close }}>
            <div
              ref={menuRef}
              role="menu"
              onKeyDown={onMenuKeyDown}
              onClick={(e) => e.stopPropagation()}
              style={{
                position: "fixed",
                top: coords.top,
                left: coords.left,
                right: coords.right,
              }}
              className={cn(
                "z-60 min-w-[240px] rounded-nb-md border border-border-strong bg-card p-1 text-sm shadow-nb-lg",
                "animate-fade-in",
                menuClassName,
              )}
            >
              {children}
            </div>
          </DropdownContext.Provider>,
          document.body,
        )}
    </span>
  );
}

export interface DropdownItemProps {
  children: ReactNode;
  onSelect?: () => void;
  icon?: ReactNode;
  /** Right-aligned hint (e.g. a count or "dev only"). */
  trailing?: ReactNode;
  destructive?: boolean;
  disabled?: boolean;
  /** Native tooltip, for a longer description that shouldn't widen the menu. */
  title?: string;
}

export function DropdownItem({
  children,
  onSelect,
  icon,
  trailing,
  destructive = false,
  disabled = false,
  title,
}: DropdownItemProps) {
  const { close } = useContext(DropdownContext);
  return (
    <button
      type="button"
      role="menuitem"
      disabled={disabled}
      tabIndex={-1}
      title={title}
      onClick={(e) => {
        e.preventDefault();
        e.stopPropagation();
        if (disabled) return;
        onSelect?.();
        close();
      }}
      className={cn(
        "flex w-full items-center gap-2.5 rounded-nb-sm px-2.5 py-2 text-left font-medium",
        "focus:outline-hidden disabled:opacity-50 disabled:cursor-not-allowed",
        destructive
          ? "text-status-danger-ink hover:bg-status-danger-50 focus:bg-status-danger-50 dark:text-red-300 dark:hover:bg-red-950/40 dark:focus:bg-red-950/40 [&_svg]:text-status-danger"
          : "text-foreground hover:bg-muted focus:bg-muted [&_svg]:text-muted-foreground",
      )}
    >
      {icon && (
        <span className="inline-flex shrink-0 [&_svg]:h-[15px] [&_svg]:w-[15px]">
          {icon}
        </span>
      )}
      <span className="flex-1">{children}</span>
      {trailing && (
        <span className="ml-auto font-mono text-[10px] uppercase tracking-wide text-muted-foreground">
          {trailing}
        </span>
      )}
    </button>
  );
}

export function DropdownSeparator() {
  return <div className="my-1 mx-0.5 h-px bg-border" role="separator" />;
}
