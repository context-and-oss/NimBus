import {
  useEffect,
  useMemo,
  useRef,
  useState,
  type KeyboardEvent,
} from "react";
import { createPortal } from "react-dom";
import { useNavigate } from "react-router-dom";
import { cn } from "lib/utils";
import { Spinner } from "components/ui/spinner";
import { useAccess } from "hooks/use-access";
import { usePaletteSearch, type PaletteResult } from "./use-palette-search";

interface CommandPaletteProps {
  isOpen: boolean;
  onClose: () => void;
}

const SECTION_LABELS: Record<PaletteResult["kind"], string> = {
  page: "Pages",
  operation: "Operations",
  setting: "Settings",
  endpoint: "Endpoints",
  eventType: "Event Types",
  event: "Events",
  session: "Sessions",
};

// Must match the order usePaletteSearch pushes results in (see there).
const SECTION_ORDER: Array<PaletteResult["kind"]> = [
  "page",
  "operation",
  "setting",
  "endpoint",
  "eventType",
  "event",
  "session",
];

const KIND_ICON: Record<PaletteResult["kind"], React.ReactNode> = {
  page: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none" aria-hidden>
      <rect x="2.5" y="2" width="11" height="12" rx="1.4" stroke="currentColor" strokeWidth="1.4" />
      <path d="M5 5.5h6M5 8h6M5 10.5h3.5" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
    </svg>
  ),
  operation: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none" aria-hidden>
      <path d="M13 8a5 5 0 1 1-1.5-3.6" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
      <path d="M13 2v3h-3" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" strokeLinejoin="round" />
    </svg>
  ),
  setting: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none" aria-hidden>
      <circle cx="8" cy="8" r="2.5" stroke="currentColor" strokeWidth="1.4" />
      <path
        d="M8 1.5v2M8 12.5v2M1.5 8h2M12.5 8h2M3.3 3.3l1.4 1.4M11.3 11.3l1.4 1.4M3.3 12.7l1.4-1.4M11.3 4.7l1.4-1.4"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
      />
    </svg>
  ),
  endpoint: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none" aria-hidden>
      <rect x="2" y="3" width="12" height="10" rx="1.5" stroke="currentColor" strokeWidth="1.4" />
      <path d="M2 6h12" stroke="currentColor" strokeWidth="1.4" />
    </svg>
  ),
  eventType: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none" aria-hidden>
      <path d="M2 4h6M2 8h12M2 12h9" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
    </svg>
  ),
  event: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none" aria-hidden>
      <path d="M2 4h12v8H2z" stroke="currentColor" strokeWidth="1.4" strokeLinejoin="round" />
      <path d="M2 4l6 5 6-5" stroke="currentColor" strokeWidth="1.4" strokeLinejoin="round" />
    </svg>
  ),
  session: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none" aria-hidden>
      <circle cx="8" cy="8" r="6" stroke="currentColor" strokeWidth="1.4" />
      <path d="M8 5v3l2 2" stroke="currentColor" strokeWidth="1.4" strokeLinecap="round" />
    </svg>
  ),
};

/**
 * The ⌘K / Ctrl+K command palette. Centred near the top of the viewport,
 * portalled to body, ESC + outside-click close. Local + remote search lives
 * in `usePaletteSearch`; this component is concerned only with input,
 * keyboard navigation, rendering, and routing on selection.
 */
export const CommandPalette: React.FC<CommandPaletteProps> = ({
  isOpen,
  onClose,
}) => {
  const [query, setQuery] = useState("");
  const [highlighted, setHighlighted] = useState(0);
  const inputRef = useRef<HTMLInputElement>(null);
  const listRef = useRef<HTMLDivElement>(null);
  const navigate = useNavigate();

  // Operations and settings are site-Owner surfaces (Spec 038 §10); everyone
  // else keeps today's endpoint, event type, event and session results.
  const canManage = useAccess().access?.canManageAccessControl ?? false;
  const { results, catalogLoading, remoteSearching, remoteError } =
    usePaletteSearch(query, isOpen, canManage);

  // Reset when the palette opens so the previous gesture's state isn't sticky.
  useEffect(() => {
    if (isOpen) {
      setQuery("");
      setHighlighted(0);
      // Defer focus until after the portal mounts.
      const id = window.setTimeout(() => inputRef.current?.focus(), 0);
      return () => window.clearTimeout(id);
    }
  }, [isOpen]);

  // Keep the highlight in-range as results change.
  useEffect(() => {
    if (highlighted >= results.length) {
      setHighlighted(results.length === 0 ? 0 : results.length - 1);
    }
  }, [results.length, highlighted]);

  // Scroll the highlighted row into view when navigating with the keyboard.
  useEffect(() => {
    const node = listRef.current?.querySelector<HTMLElement>(
      `[data-row-index='${highlighted}']`,
    );
    node?.scrollIntoView({ block: "nearest" });
  }, [highlighted]);

  // Lock body scroll while open. Mirrors what the Modal primitive does so the
  // page underneath doesn't shift when the palette opens.
  useEffect(() => {
    if (!isOpen) return;
    const prev = document.body.style.overflow;
    document.body.style.overflow = "hidden";
    return () => {
      document.body.style.overflow = prev;
    };
  }, [isOpen]);

  const grouped = useMemo(() => {
    const map: Record<PaletteResult["kind"], PaletteResult[]> = {
      page: [],
      operation: [],
      setting: [],
      endpoint: [],
      eventType: [],
      event: [],
      session: [],
    };
    for (const r of results) {
      map[r.kind].push(r);
    }
    return map;
  }, [results]);

  const handleSelect = (r: PaletteResult) => {
    onClose();
    // Defer the navigate until after the close transition starts so the
    // route doesn't render under a stale overlay.
    window.requestAnimationFrame(() => navigate(r.route));
  };

  const handleKeyDown = (e: KeyboardEvent<HTMLInputElement>) => {
    if (e.key === "ArrowDown") {
      e.preventDefault();
      setHighlighted((i) => Math.min(i + 1, Math.max(results.length - 1, 0)));
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      setHighlighted((i) => Math.max(i - 1, 0));
    } else if (e.key === "Enter") {
      e.preventDefault();
      const r = results[highlighted];
      if (r) handleSelect(r);
    } else if (e.key === "Escape") {
      e.preventDefault();
      onClose();
    }
  };

  if (!isOpen) return null;

  const trimmed = query.trim();
  const showEmptyHint = trimmed.length === 0;
  const noResults =
    !showEmptyHint &&
    results.length === 0 &&
    !catalogLoading &&
    !remoteSearching;

  return createPortal(
    <div
      className="fixed inset-0 z-50 bg-black/50 flex items-start justify-center pt-[10vh] px-4"
      onMouseDown={(e) => {
        // Only close if the user clicked the overlay itself, not a descendant.
        if (e.target === e.currentTarget) onClose();
      }}
      role="dialog"
      aria-modal="true"
      aria-label="Command palette"
    >
      <div
        className={cn(
          "w-full max-w-xl bg-card text-card-foreground",
          "rounded-nb-lg border border-border shadow-nb-lg overflow-hidden",
          "flex flex-col max-h-[70vh]",
          "animate-zoom-in",
        )}
      >
        <div className="flex items-center gap-2 px-4 py-3 border-b border-border">
          <svg
            className="w-4 h-4 text-muted-foreground shrink-0"
            viewBox="0 0 16 16"
            fill="none"
            aria-hidden
          >
            <circle cx="7" cy="7" r="4.5" stroke="currentColor" strokeWidth="1.5" />
            <path d="M10.5 10.5L13 13" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
          </svg>
          <input
            ref={inputRef}
            type="text"
            autoComplete="off"
            spellCheck={false}
            value={query}
            onChange={(e) => {
              setQuery(e.target.value);
              setHighlighted(0);
            }}
            onKeyDown={handleKeyDown}
            placeholder="Jump to endpoint, event, or session…"
            className={cn(
              "flex-1 bg-transparent outline-hidden border-0 text-foreground",
              "placeholder:text-muted-foreground text-[14px]",
            )}
            aria-label="Search"
            aria-autocomplete="list"
            aria-controls="cmd-palette-results"
            aria-activedescendant={
              results[highlighted]?.key
                ? `cmd-palette-row-${results[highlighted]!.key}`
                : undefined
            }
          />
          {(catalogLoading || remoteSearching) && (
            <Spinner size="sm" color="primary" />
          )}
          <kbd
            className={cn(
              "font-mono text-[10.5px] bg-muted border border-border",
              "px-1.5 py-px rounded-sm text-muted-foreground",
            )}
          >
            esc
          </kbd>
        </div>

        <div
          ref={listRef}
          id="cmd-palette-results"
          role="listbox"
          className="flex-1 overflow-y-auto py-1"
        >
          {showEmptyHint && (
            <EmptyHint catalogLoading={catalogLoading} />
          )}
          {noResults && <NoResults query={trimmed} />}
          {remoteError && !remoteSearching && (
            <div className="px-4 py-3 text-[12.5px] text-status-warning-ink font-mono">
              ID lookup failed — backend unavailable.
            </div>
          )}
          {SECTION_ORDER.map((kind) => {
            const items = grouped[kind];
            if (items.length === 0) return null;
            return (
              <section key={kind}>
                <div
                  className={cn(
                    "px-4 pt-3 pb-1 font-mono text-[10.5px] uppercase",
                    "tracking-[0.12em] text-muted-foreground",
                  )}
                >
                  {SECTION_LABELS[kind]}
                </div>
                <ul className="m-0 p-0 list-none">
                  {items.map((r) => {
                    const flatIndex = results.indexOf(r);
                    const isActive = flatIndex === highlighted;
                    return (
                      <li
                        key={r.key}
                        id={`cmd-palette-row-${r.key}`}
                        role="option"
                        aria-selected={isActive}
                        data-row-index={flatIndex}
                        onMouseEnter={() => setHighlighted(flatIndex)}
                        onMouseDown={(e) => {
                          // mousedown not click — beats the overlay's
                          // onMouseDown that would otherwise close us first.
                          e.preventDefault();
                          handleSelect(r);
                        }}
                        className={cn(
                          "px-4 py-2 flex items-center gap-3 cursor-pointer",
                          "text-[13px]",
                          isActive
                            ? "bg-primary-tint text-primary-600"
                            : "text-foreground",
                        )}
                      >
                        <span
                          className={cn(
                            "shrink-0",
                            isActive
                              ? "text-primary-600"
                              : "text-muted-foreground",
                          )}
                        >
                          {KIND_ICON[r.kind]}
                        </span>
                        <span className="flex-1 min-w-0">
                          <span className="font-semibold truncate block">
                            {r.title}
                          </span>
                          {r.subtitle && (
                            <span
                              className={cn(
                                "font-mono text-[11px] truncate block",
                                isActive
                                  ? "text-primary-600/80"
                                  : "text-muted-foreground",
                              )}
                            >
                              {r.subtitle}
                            </span>
                          )}
                        </span>
                      </li>
                    );
                  })}
                </ul>
              </section>
            );
          })}
        </div>

        <div
          className={cn(
            "px-4 py-2 border-t border-border bg-muted",
            "font-mono text-[10.5px] text-muted-foreground",
            "flex items-center gap-4",
          )}
        >
          <span>↑↓ navigate</span>
          <span>↵ open</span>
          <span>esc close</span>
        </div>
      </div>
    </div>,
    document.body,
  );
};

const EmptyHint: React.FC<{ catalogLoading: boolean }> = ({
  catalogLoading,
}) => (
  <div className="px-4 py-6 text-[12.5px] text-muted-foreground">
    {catalogLoading
      ? "Loading catalog…"
      : "Type to search endpoints, event types, or paste an event ID or session ID. Use ↑↓ Enter to navigate."}
  </div>
);

const NoResults: React.FC<{ query: string }> = ({ query }) => (
  <div className="px-4 py-6 text-[12.5px] text-muted-foreground">
    No matches for <span className="font-mono text-foreground">{query}</span>.
    Try a different name or paste a full event / session ID.
  </div>
);
