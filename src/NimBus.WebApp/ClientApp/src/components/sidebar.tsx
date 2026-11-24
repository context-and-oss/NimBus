import { useEffect, useState } from "react";
import { NavLink } from "react-router-dom";
import * as api from "api-client";
import { cn } from "lib/utils";
import { failureBacklog } from "functions/failed-messages.functions";
import { useEnv } from "hooks/app-status";
import { useAccess } from "hooks/use-access";
import SidebarUserFooter from "components/sidebar-user-footer";
import { useSimulationStatus } from "components/simulate/simulation-utils";

interface NavItem {
  name: string;
  path: string;
  /** Match path prefixes so /Endpoints/Details/X keeps Endpoints highlighted. */
  matchPrefix?: string;
  icon: React.ReactNode;
  /** Optional trailing badge (e.g. "new") — rendered in the muted pill slot. */
  badge?: string;
  /** Optional attention count — rendered as a status-danger pill when above zero. */
  count?: number;
  /** Endpoints with receive paused — rendered as a status-warning pill when above zero. */
  paused?: number;
}

interface NavGroup {
  label: string;
  items: NavItem[];
}

// Inline SVG glyphs sized 16px to match design's `.si` rule (w/h 14, opacity .95).
const Icon = {
  failed: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <path
        d="M8 2.2 14.2 13H1.8L8 2.2z"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinejoin="round"
      />
      <path
        d="M8 6.5v3"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
      />
      <circle cx="8" cy="11.3" r="0.8" fill="currentColor" />
    </svg>
  ),
  endpoints: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <rect
        x="2"
        y="3"
        width="12"
        height="10"
        rx="1.5"
        stroke="currentColor"
        strokeWidth="1.4"
      />
      <path d="M2 6h12" stroke="currentColor" strokeWidth="1.4" />
    </svg>
  ),
  eventTypes: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <path
        d="M2 4h6M2 8h12M2 12h9"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
      />
    </svg>
  ),
  messages: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <path
        d="M2 4h12v8H2z"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinejoin="round"
      />
      <path
        d="M2 4l6 5 6-5"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinejoin="round"
      />
    </svg>
  ),
  metrics: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <path
        d="M2 13V3M2 13h12M5 10V7M8 10V5M11 10V8"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
      />
    </svg>
  ),
  audit: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <path
        d="M3 2h7l3 3v9H3z"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinejoin="round"
      />
      <path
        d="M5 8h6M5 11h4"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
      />
    </svg>
  ),
  operations: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <path
        d="M13 8a5 5 0 1 1-1.5-3.6"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
      />
      <path
        d="M13 2v3h-3"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  ),
  topology: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <circle cx="8" cy="3" r="1.6" stroke="currentColor" strokeWidth="1.4" />
      <circle cx="3" cy="12.5" r="1.6" stroke="currentColor" strokeWidth="1.4" />
      <circle cx="13" cy="12.5" r="1.6" stroke="currentColor" strokeWidth="1.4" />
      <path
        d="M7.2 4.4L3.8 11M8.8 4.4l3.4 6.6M4.6 12.5h6.8"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
      />
    </svg>
  ),
  settings: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <circle cx="8" cy="8" r="2.5" stroke="currentColor" strokeWidth="1.4" />
      <path
        d="M8 1.5v2M8 12.5v2M1.5 8h2M12.5 8h2M3.3 3.3l1.4 1.4M11.3 11.3l1.4 1.4M3.3 12.7l1.4-1.4M11.3 4.7l1.4-1.4"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
      />
    </svg>
  ),
  flow: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <circle cx="3.5" cy="5" r="2" stroke="currentColor" strokeWidth="1.4" />
      <circle cx="12.5" cy="11" r="2" stroke="currentColor" strokeWidth="1.4" />
      <path
        d="M5.5 6.5c2.5 1.5 2.5 2.5 5 4"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
      />
      <path
        d="M8.7 10.9l1.8.4.4-1.8"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  ),
  heartbeat: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <path
        d="M1.5 8h3l1.5-4 3 8 1.5-4h4"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
        strokeLinejoin="round"
      />
    </svg>
  ),
  monitor: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <rect
        x="1.5"
        y="3"
        width="13"
        height="9"
        rx="1.4"
        stroke="currentColor"
        strokeWidth="1.4"
      />
      <path
        d="M6 14h4M8 12v2"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
      />
    </svg>
  ),
  simulate: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <path
        d="M4 3l9 5-9 5z"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinejoin="round"
      />
      <path
        d="M1.5 5.5v5"
        stroke="currentColor"
        strokeWidth="1.4"
        strokeLinecap="round"
      />
    </svg>
  ),
  access: (
    <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none">
      <rect
        x="3"
        y="7"
        width="10"
        height="6"
        rx="1.4"
        stroke="currentColor"
        strokeWidth="1.4"
      />
      <path
        d="M5.5 7V5.5a2.5 2.5 0 0 1 5 0V7"
        stroke="currentColor"
        strokeWidth="1.4"
      />
      <circle cx="8" cy="10" r="1" fill="currentColor" />
    </svg>
  ),
};

const NAV: NavGroup[] = [
  {
    label: "Observe",
    items: [
      {
        name: "Endpoints",
        path: "/Endpoints",
        matchPrefix: "/Endpoints",
        icon: Icon.endpoints,
      },
      {
        name: "Event Types",
        path: "/EventTypes",
        matchPrefix: "/EventTypes",
        icon: Icon.eventTypes,
      },
      {
        name: "Messages",
        path: "/Messages",
        matchPrefix: "/Message",
        icon: Icon.messages,
      },
      {
        name: "Failed",
        path: "/Failed",
        icon: Icon.failed,
      },
      { name: "Metrics", path: "/Metrics", icon: Icon.metrics },
      {
        name: "Flow",
        path: "/Flow",
        icon: Icon.flow,
        badge: "live",
      },
      { name: "Heartbeat", path: "/Heartbeat", icon: Icon.heartbeat },
      {
        name: "Monitor",
        path: "/Monitor",
        icon: Icon.monitor,
        badge: "wall",
      },
    ],
  },
  {
    label: "Diagnose",
    items: [
      { name: "Audit Log", path: "/Audits", icon: Icon.audit },
    ],
  },
  {
    label: "Manage",
    items: [
      {
        name: "Operations",
        path: "/Operations",
        matchPrefix: "/Operations",
        icon: Icon.operations,
      },
      {
        name: "Topology",
        path: "/Topology",
        matchPrefix: "/Topology",
        icon: Icon.topology,
      },
      {
        name: "Settings",
        path: "/Settings",
        matchPrefix: "/Settings",
        icon: Icon.settings,
      },
      { name: "Simulate", path: "/Simulate", icon: Icon.simulate },
      { name: "Access Control", path: "/AccessControl", icon: Icon.access },
    ],
  },
];

// Render-side gating only — the server enforces every role check. Operations,
// Topology and Settings are site-Owner surfaces; Access Control also serves
// endpoint Owners. Items stay
// hidden until the access lookup resolves so role-less users never see a
// flash of manage links.
const useVisibleNav = (): NavGroup[] => {
  const { access } = useAccess();
  const canManageSite = access?.canManageAccessControl ?? false;
  const ownsEndpoint =
    access?.endpointRoles?.some(
      (r) => r.role === api.EndpointRoleInfoRole.Owner,
    ) ?? false;

  // The simulator's status is fetched only for site Owners; everyone else
  // never calls the admin API from the sidebar.
  const { status: simulation } = useSimulationStatus({
    enabled: canManageSite,
    pollMs: SIMULATION_POLL_MS,
  });
  const showSimulate =
    canManageSite && !!simulation?.allowed && !!simulation?.enabled;

  return NAV.map((group) => {
    if (group.label !== "Manage") return group;
    return {
      ...group,
      items: group.items
        .filter((item) => {
          if (SITE_OWNER_PAGES.includes(item.path)) return canManageSite;
          if (item.path === "/Simulate") return showSimulate;
          return canManageSite || ownsEndpoint;
        })
        .map((item) =>
          item.path === "/Simulate"
            ? { ...item, badge: simulationBadge(simulation?.state) }
            : item,
        ),
    };
  }).filter((group) => group.items.length > 0);
};

// The pages that replaced Admin (Spec 038): every API they call is site-Owner only.
const SITE_OWNER_PAGES = ["/Operations", "/Topology", "/Settings"];

const SIMULATION_POLL_MS = 15_000;

const FAILED_POLL_MS = 60_000;

// Unresolved failures (Failed + DeadLettered + Unsupported) across all endpoints, for the
// Failed item's attention badge. Fail-soft: no badge when the counts can't be read.
// Spec 038 §5.1: endpoints whose receive is disabled. A null subscriptionStatus is
// "unknown" (the metadata probe failed), never paused. Send status is not in this
// payload, so send-paused endpoints show on the Operations page only.
const pausedEndpoints = (counts: api.EndpointStatusCount[]): number =>
  counts.filter((c) => c.subscriptionStatus === "disabled").length;

// One poll feeds both the Failed and the Operations badge.
const useFailedBacklog = (): { failed?: number; paused?: number } => {
  const [count, setCount] = useState<{ failed?: number; paused?: number }>({});
  useEffect(() => {
    let cancelled = false;
    const load = async () => {
      try {
        const counts = await new api.Client(
          api.CookieAuth(),
        ).getEndpointStatusCountAll();
        if (cancelled || !Array.isArray(counts)) return;
        setCount({ failed: failureBacklog(counts), paused: pausedEndpoints(counts) });
      } catch {
        // No badge rather than a broken sidebar.
      }
    };
    void load();
    const timer = window.setInterval(load, FAILED_POLL_MS);
    return () => {
      cancelled = true;
      window.clearInterval(timer);
    };
  }, []);
  return count;
};

const simulationBadge = (state?: string): string => {
  switch ((state ?? "").toLowerCase()) {
    case "running":
      return "run";
    case "paused":
      return "paused";
    case "pausing":
    case "stopping":
      return "…";
    default:
      return "idle";
  }
};

// Logo mark — soft cloud silhouette + coral droplet (design system §01).
const LogoMark = ({ size = 22 }: { size?: number }) => (
  <svg
    width={size}
    height={size}
    viewBox="0 0 36 36"
    fill="none"
    className="inline-block"
    style={{ transform: "translateY(2px)" }}
  >
    <path
      d="M5 22c0-3.5 2.8-6.3 6.3-6.3.7 0 1.4.1 2 .3.8-3.7 4.1-6.5 8-6.5 4.2 0 7.6 3.2 8.1 7.2 2.7.4 4.8 2.7 4.8 5.5 0 3.1-2.5 5.5-5.5 5.5H11.3C7.8 27.7 5 25 5 22z"
      stroke="#F4F2EA"
      strokeWidth="2.5"
      strokeLinejoin="round"
    />
    <circle cx="29" cy="11" r="3" fill="#E8743C" />
  </svg>
);

/** Browser storage key for the sidebar's icon-only mode, a per-viewer preference. */
export const SIDEBAR_COLLAPSED_KEY = "nimbus.sidebar.collapsed";

// Icon-only mode gives wide pages (Settings, Access Control) the room the
// labels take. It is remembered per browser and is off by default.
const useCollapsed = (): [boolean, () => void] => {
  const [collapsed, setCollapsed] = useState<boolean>(() => {
    try {
      return window.localStorage?.getItem(SIDEBAR_COLLAPSED_KEY) === "true";
    } catch {
      return false;
    }
  });
  const toggle = () =>
    setCollapsed((was) => {
      try {
        window.localStorage?.setItem(SIDEBAR_COLLAPSED_KEY, String(!was));
      } catch {
        // localStorage can be unavailable (private mode, blocked storage); ignore.
      }
      return !was;
    });
  return [collapsed, toggle];
};

const CollapseIcon = ({ collapsed }: { collapsed: boolean }) => (
  <svg className="w-4 h-4" viewBox="0 0 16 16" fill="none" aria-hidden="true">
    <rect x="2" y="2.5" width="12" height="11" rx="1.5" stroke="currentColor" strokeWidth="1.4" />
    <path d="M6 2.5v11" stroke="currentColor" strokeWidth="1.4" />
    <path
      d={collapsed ? "M9 6.5 10.5 8 9 9.5" : "M11 6.5 9.5 8 11 9.5"}
      stroke="currentColor"
      strokeWidth="1.4"
      strokeLinecap="round"
      strokeLinejoin="round"
    />
  </svg>
);

const Sidebar = () => {
  const env = useEnv();
  const backlog = useFailedBacklog();
  const [collapsed, toggleCollapsed] = useCollapsed();
  const nav = useVisibleNav().map((group) => ({
    ...group,
    items: group.items.map((item) =>
      item.path === "/Failed"
        ? { ...item, count: backlog.failed }
        : item.path === "/Operations"
          ? { ...item, paused: backlog.paused }
          : item,
    ),
  }));

  return (
    <aside
      className={cn(
        "bg-ink text-[#C9C1AB] flex flex-col gap-0.5 sticky top-0 h-screen",
        "pt-5 pb-5 border-r border-[#2A2620] shrink-0",
        collapsed ? "w-16 px-2" : "w-[232px] px-3.5",
      )}
    >
      <a
        href="/Endpoints"
        aria-label={collapsed ? "NimBus" : undefined}
        className={cn(
          "flex items-baseline gap-1 pb-1.5 text-[#F4F2EA] font-extrabold text-xl tracking-tight no-underline hover:no-underline",
          collapsed ? "justify-center" : "px-2",
        )}
      >
        <LogoMark size={22} />
        {!collapsed && (
          <>
            <span className="ml-1">NimBus</span>
            <span className="text-primary">.</span>
          </>
        )}
      </a>
      <div
        className={cn(
          "pb-3.5 mb-2 border-b border-[#2A2620] font-mono text-[10px] text-[#6F6A5C] tracking-wider leading-tight",
          collapsed ? "text-center" : "px-2",
        )}
      >
        {!collapsed && "A nimbus for your Azure cloud."}
        {env && (
          <span
            className={cn(
              "inline-block align-middle bg-primary-tint text-primary-600 font-mono text-[9px] uppercase tracking-[0.14em] px-1.5 py-px rounded-nb-sm font-bold",
              collapsed ? "max-w-full truncate" : "ml-1.5",
            )}
            title={collapsed ? env : undefined}
          >
            {env}
          </span>
        )}
      </div>

      <div className="flex-1 overflow-y-auto">
        {nav.map((group) => (
          <div key={group.label}>
            <div
              className={cn(
                "font-mono text-[9.5px] text-[#6F6A5C] tracking-widest uppercase mt-3.5 mb-1",
                collapsed ? "mx-2 border-t border-[#2A2620]" : "mx-2.5",
              )}
            >
              <span className={collapsed ? "sr-only" : undefined}>{group.label}</span>
            </div>
            {group.items.map((item) => (
              <NavLink
                key={item.path}
                to={item.path}
                end={!item.matchPrefix}
                title={collapsed ? item.name : undefined}
                className={({ isActive }) => {
                  // For prefix-matched items, NavLink's default isActive
                  // already covers the prefix when `end` is false.
                  const active = isActive;
                  return cn(
                    "relative flex items-center gap-2.5 py-2 rounded-md text-[13px] font-medium no-underline",
                    "transition-colors duration-100",
                    collapsed ? "justify-center px-0" : "px-2.5",
                    active
                      ? "bg-primary/16 text-primary"
                      : "text-[#C9C1AB] hover:bg-white/4 hover:text-[#F4F2EA]",
                  );
                }}
              >
                <span className="opacity-90 shrink-0">{item.icon}</span>
                <span className={collapsed ? "sr-only" : "flex-1 truncate"}>{item.name}</span>
                {(item.count ?? 0) > 0 && (
                  <span
                    className={cn(
                      "font-mono text-[10px] px-1.5 py-px rounded-full font-bold bg-status-danger text-white",
                      collapsed ? "absolute -top-1 right-0 px-1 text-[9px]" : "ml-auto",
                    )}
                    aria-label={`${item.count} unresolved failures`}
                  >
                    {item.count!.toLocaleString()}
                  </span>
                )}
                {(item.paused ?? 0) > 0 && (
                  <span
                    className={cn(
                      "font-mono text-[10px] rounded-full font-bold bg-status-warning-50 text-status-warning-ink",
                      collapsed ? "absolute top-0.5 right-1 h-2 w-2 p-0" : "ml-auto px-1.5 py-px",
                    )}
                    aria-label={`${item.paused} ${item.paused === 1 ? "endpoint" : "endpoints"} paused`}
                  >
                    {!collapsed && `${item.paused!.toLocaleString()} paused`}
                  </span>
                )}
                {item.badge && (
                  <span
                    className={cn(
                      "ml-auto font-mono text-[10px] uppercase tracking-wider px-1.5 py-px rounded-full font-bold",
                      "bg-primary/22 text-primary",
                      collapsed && "sr-only",
                    )}
                  >
                    {item.badge}
                  </span>
                )}
              </NavLink>
            ))}
          </div>
        ))}
      </div>

      <button
        type="button"
        onClick={toggleCollapsed}
        aria-label={collapsed ? "Expand sidebar" : "Collapse sidebar"}
        aria-expanded={!collapsed}
        title={collapsed ? "Expand sidebar" : "Collapse sidebar"}
        className={cn(
          "mb-2 flex items-center gap-2.5 rounded-md py-2 text-[12px] text-[#C9C1AB]",
          "hover:bg-white/4 hover:text-[#F4F2EA] transition-colors",
          "focus:outline-hidden focus:ring-2 focus:ring-primary",
          collapsed ? "justify-center" : "px-2.5",
        )}
      >
        <CollapseIcon collapsed={collapsed} />
        {!collapsed && <span>Collapse</span>}
      </button>

      <SidebarUserFooter compact={collapsed} />
    </aside>
  );
};

export default Sidebar;
