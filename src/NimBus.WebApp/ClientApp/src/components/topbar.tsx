import { Link, useLocation, useParams } from "react-router-dom";
import { useTheme } from "hooks/use-theme";
import { useCommandPalette } from "components/command-palette";
import { cn } from "lib/utils";
import {
  OPERATIONS,
  SETTINGS_FEATURES,
  TOPOLOGY_VIEWS,
} from "models/manage-pages";

interface Crumb {
  label: string;
  to?: string;
}

// Derive breadcrumbs from the current pathname. Detail pages (Endpoints/Details/X,
// EventTypes/Details/X, Message/Index/...) get a parent-link crumb so deep-linked
// URLs are self-explaining (design recommendation §10).
function useBreadcrumbs(): Crumb[] {
  const { pathname, search } = useLocation();
  const params = useParams();
  const segs = pathname.split("/").filter(Boolean);

  if (segs.length === 0) return [{ label: "Endpoints" }];

  const top = segs[0];

  switch (top) {
    case "Endpoints":
      if (segs[1] === "Details" && params.id) {
        return [{ label: "Endpoints", to: "/Endpoints" }, { label: params.id }];
      }
      return [{ label: "Endpoints" }];
    case "EventTypes":
      if (segs[1] === "Details" && params.id) {
        return [
          { label: "Event Types", to: "/EventTypes" },
          { label: params.id },
        ];
      }
      return [{ label: "Event Types" }];
    case "Message":
      // /Message/Index/:endpointId/:id[/:backindex]
      if (segs[1] === "Index" && params.endpointId) {
        if (new URLSearchParams(search).get("from") === "failed")
          return [
            { label: "Failed", to: "/Failed" },
            {
              label: params.endpointId,
              to: `/Endpoints/Details/${params.endpointId}`,
            },
            { label: params.id?.slice(0, 8) + "..." },
          ];
        return [
          {
            label: params.endpointId,
            to: `/Endpoints/Details/${params.endpointId}`,
          },
          { label: "Messages" },
          { label: params.id?.slice(0, 8) + "…" || "" },
        ];
      }
      return [{ label: "Messages" }];
    case "Messages":
      return [{ label: "Messages" }];
    case "Metrics":
      return [{ label: "Metrics" }];
    case "Insights":
      return [{ label: "Insights" }];
    case "Audits":
      return [{ label: "Audit Log" }];
    case "Operations": {
      const operation = OPERATIONS.find((o) => o.id === segs[1]?.toLowerCase());
      return operation
        ? [
            { label: "Operations", to: "/Operations" },
            { label: operation.label },
          ]
        : [{ label: "Operations" }];
    }
    case "Topology": {
      const view = TOPOLOGY_VIEWS.find((v) => v.id === segs[1]?.toLowerCase());
      return view
        ? [{ label: "Topology", to: "/Topology" }, { label: view.label }]
        : [{ label: "Topology" }];
    }
    case "Settings": {
      const feature = SETTINGS_FEATURES.find(
        (f) => f.id === segs[1]?.toLowerCase(),
      );
      return feature
        ? [{ label: "Settings", to: "/Settings" }, { label: feature.name }]
        : [{ label: "Settings" }];
    }
    default:
      return [{ label: top }];
  }
}

const Topbar = () => {
  const crumbs = useBreadcrumbs();
  const { resolvedTheme, setTheme } = useTheme();
  const { open: openCommandPalette } = useCommandPalette();

  const toggleTheme = () =>
    setTheme(resolvedTheme === "dark" ? "light" : "dark");

  const isMac =
    typeof navigator !== "undefined" &&
    /Mac|iPhone|iPad/.test(navigator.platform);
  const shortcutLabel = isMac ? "⌘K" : "Ctrl K";

  return (
    <header
      className={cn(
        "flex items-center gap-3.5 px-7 py-3.5",
        "border-b border-border bg-background",
        "sticky top-0 z-10",
      )}
    >
      <nav className="font-mono text-xs text-muted-foreground flex items-center">
        {crumbs.map((crumb, i) => {
          const isLast = i === crumbs.length - 1;
          return (
            <span key={i} className="flex items-center">
              {i > 0 && <span className="mx-1.5">/</span>}
              {crumb.to && !isLast ? (
                <Link
                  to={crumb.to}
                  className="text-muted-foreground hover:text-foreground no-underline"
                >
                  {crumb.label}
                </Link>
              ) : (
                <span className={cn(isLast && "text-foreground font-semibold")}>
                  {crumb.label}
                </span>
              )}
            </span>
          );
        })}
      </nav>

      <div className="flex-1" />
      <span
        className="text-xs text-muted-foreground"
        title="All timestamps use your local time zone unless labelled otherwise"
      >
        Local time ({Intl.DateTimeFormat().resolvedOptions().timeZone})
      </span>

      {/* ⌘K command-palette trigger — recommendation §10 from the design
          handoff. Keyboard users invoke via Cmd+K / Ctrl+K from anywhere
          (wired in CommandPaletteProvider); this button is the same gesture
          for mouse-first users. */}
      <button
        type="button"
        onClick={openCommandPalette}
        className={cn(
          "hidden md:flex items-center gap-2",
          "bg-card border border-border rounded-nb-md",
          "px-3 py-1.5 min-w-[280px] text-muted-foreground text-[13px]",
          "hover:border-border-strong hover:text-foreground transition-colors",
          "cursor-pointer",
        )}
        title={`Open command palette (${shortcutLabel})`}
      >
        <svg width="14" height="14" viewBox="0 0 16 16" fill="none" aria-hidden>
          <circle
            cx="7"
            cy="7"
            r="4.5"
            stroke="currentColor"
            strokeWidth="1.5"
          />
          <path
            d="M10.5 10.5L13 13"
            stroke="currentColor"
            strokeWidth="1.5"
            strokeLinecap="round"
          />
        </svg>
        <span>Jump to endpoint, event, or session…</span>
        <kbd className="ml-auto font-mono text-[10.5px] bg-muted border border-border px-1.5 py-px rounded-sm text-muted-foreground">
          {shortcutLabel}
        </kbd>
      </button>

      <button
        onClick={toggleTheme}
        className={cn(
          "w-8 h-8 rounded-nb-md inline-flex items-center justify-center",
          "bg-card border border-border text-muted-foreground hover:bg-muted",
          "transition-colors",
        )}
        aria-label="Toggle theme"
      >
        {resolvedTheme === "dark" ? (
          <svg width="16" height="16" viewBox="0 0 24 24" fill="none">
            <circle
              cx="12"
              cy="12"
              r="5"
              stroke="currentColor"
              strokeWidth="1.6"
            />
            <path
              d="M12 2v2M12 20v2M2 12h2M20 12h2M4.93 4.93l1.41 1.41M17.66 17.66l1.41 1.41M4.93 19.07l1.41-1.41M17.66 6.34l1.41-1.41"
              stroke="currentColor"
              strokeWidth="1.6"
              strokeLinecap="round"
            />
          </svg>
        ) : (
          <svg width="16" height="16" viewBox="0 0 16 16" fill="none">
            <path
              d="M13 9.5A5.5 5.5 0 0 1 6.5 3a5.5 5.5 0 1 0 6.5 6.5z"
              stroke="currentColor"
              strokeWidth="1.5"
              strokeLinejoin="round"
            />
          </svg>
        )}
      </button>
    </header>
  );
};

export default Topbar;
