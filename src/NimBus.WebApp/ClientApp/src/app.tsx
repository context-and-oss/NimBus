import { lazy, Suspense } from "react";
import { Navigate, Route, Routes, useLocation } from "react-router-dom";
import Sidebar from "components/sidebar";
import Topbar from "components/topbar";
import Footer from "components/footer";
import Loading from "components/loading/loading";
import { Navigation } from "models/navigation";
import { ToastProvider } from "components/ui/toast";
import { CommandPaletteProvider } from "components/command-palette";
import { ThemeProvider } from "hooks/use-theme";
import { isDashboardTokenMode } from "lib/monitor-access";

// Pages are route-level code-split: the initial bundle carries only the shell
// (sidebar/topbar/footer) and whichever route the user lands on. Each page —
// and its heavy, page-specific deps (recharts on Insights, react-d3-tree on
// event details) — is fetched on demand when its route is first visited.
const EndpointDetails = lazy(() => import("pages/endpoint-details"));
const EventDetails = lazy(() => import("pages/event-details"));
const EndpointsList = lazy(() => import("pages/endpoints-list"));
const EventTypesList = lazy(() => import("pages/event-types-list"));
const EventTypeDetails = lazy(() => import("pages/event-type-details"));
const MessagesList = lazy(() => import("pages/messages-list"));
const Operations = lazy(() => import("pages/operations"));
const Topology = lazy(() => import("pages/topology"));
const Settings = lazy(() => import("pages/settings"));
const AccessControl = lazy(() => import("pages/access-control"));
const Metrics = lazy(() => import("pages/metrics"));
const Flow = lazy(() => import("pages/flow"));
const Heartbeat = lazy(() => import("pages/heartbeat"));
const Insights = lazy(() => import("pages/insights"));
const FailedMessages = lazy(() => import("pages/failed-messages"));
const Monitor = lazy(() => import("pages/monitor"));
const AuditsList = lazy(() => import("pages/audits-list"));
const Simulate = lazy(() => import("pages/simulate"));

const navigation: Navigation = [
  {
    name: "EndpointDetails",
    path: "/Endpoints/Details/:id",
    header: false,
    render: () => <EndpointDetails />,
  },
  {
    name: "EventDetails",
    path: "/Message/Index/:endpointId/:id",
    header: false,
    render: () => <EventDetails />,
  },
  {
    name: "EventDetails",
    path: "/Message/Index/:endpointId/:id/:backindex",
    header: false,
    render: () => <EventDetails />,
  },
  {
    name: "Endpoints",
    path: "/Endpoints",
    header: true,
    render: () => <EndpointsList />,
  },
  {
    name: "Event Types",
    path: "/EventTypes",
    header: true,
    render: () => <EventTypesList />,
  },
  {
    name: "EventTypeDetails",
    path: "/EventTypes/Details/:id",
    header: false,
    render: () => <EventTypeDetails />,
  },
  {
    name: "Messages",
    path: "/Messages",
    header: true,
    render: () => <MessagesList />,
  },
  {
    name: "Failed messages",
    path: "/Failed",
    header: true,
    render: () => <FailedMessages />,
  },
  {
    name: "Metrics",
    path: "/Metrics",
    header: true,
    render: () => <Metrics />,
  },
  {
    name: "Flow",
    path: "/Flow",
    header: true,
    render: () => <Flow />,
  },
  {
    name: "Heartbeat",
    path: "/Heartbeat",
    header: true,
    render: () => <Heartbeat />,
  },
  {
    name: "Monitor",
    path: "/Monitor",
    header: true,
    render: () => <Monitor />,
  },
  {
    name: "Insights",
    path: "/Insights",
    header: true,
    render: () => <Insights />,
  },
  {
    name: "Audit Log",
    path: "/Audits",
    header: true,
    render: () => <AuditsList />,
  },
  {
    name: "Operations",
    path: "/Operations/:operation?",
    header: true,
    render: () => <Operations />,
  },
  {
    name: "Topology",
    path: "/Topology/:view?",
    header: true,
    render: () => <Topology />,
  },
  {
    name: "Settings",
    path: "/Settings/:feature?",
    header: true,
    render: () => <Settings />,
  },
  {
    // Spec 038 §12.1: the Admin page was split into Operations, Topology and
    // Settings. Its tabs had no URLs, so every old link lands on Operations.
    name: "Admin",
    path: "/Admin",
    header: false,
    render: () => <Navigate to="/Operations" replace />,
  },
  {
    name: "Simulate",
    path: "/Simulate",
    header: true,
    render: () => <Simulate />,
  },
  {
    name: "Access Control",
    path: "/AccessControl",
    header: true,
    render: () => <AccessControl />,
  },
  {
    name: "Front",
    path: "/",
    header: false,
    render: () => <EndpointsList />,
  },
];

// Routes that should render full-bleed without the sidebar/topbar/footer.
// The wall display is meant to be opened on a kiosk PC and read from across
// a room — every pixel of chrome would just be lost real estate.
const FULLSCREEN_ROUTES = ["/Monitor"];

function App() {
  // A wall opened with a dashboard token may call the Monitor feed and nothing else, so the
  // palette (which loads the caller's access on mount) stays off.
  const tokenMode = isDashboardTokenMode();
  return (
    <ThemeProvider>
      <ToastProvider>
        <CommandPaletteProvider disabled={tokenMode}>
          <AppShell />
        </CommandPaletteProvider>
      </ToastProvider>
    </ThemeProvider>
  );
}

function AppShell() {
  const location = useLocation();
  const isFullscreen = FULLSCREEN_ROUTES.some(
    (r) =>
      location.pathname === r ||
      location.pathname.toLowerCase() === r.toLowerCase(),
  );

  const routes = (
    <Suspense
      fallback={
        <div className="flex flex-1 items-center justify-center p-8">
          <Loading />
        </div>
      }
    >
      <Routes>
        {navigation
          .filter((x) => x.render)
          .map((route) => (
            <Route
              key={route.path}
              path={route.path}
              element={route.render!()}
            />
          ))}
      </Routes>
    </Suspense>
  );

  if (isFullscreen) {
    return <div className="min-h-screen">{routes}</div>;
  }

  return (
    <div className="flex min-h-screen bg-background">
      <Sidebar />
      <div className="flex flex-col flex-1 min-w-0">
        <Topbar />
        <main className="flex-1 flex flex-col min-w-0">{routes}</main>
        <Footer />
      </div>
    </div>
  );
}

export default App;
