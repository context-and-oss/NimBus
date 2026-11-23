import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation, useNavigate } from "react-router-dom";
import * as api from "api-client";
import EndpointDetails from "./endpoint-details";

const { subscriptions, alertRules, eventTypes, audits } = vi.hoisted(() => ({
  subscriptions: vi.fn(),
  alertRules: vi.fn(),
  eventTypes: vi.fn(),
  audits: vi.fn(),
}));
vi.mock("api-client", async () => {
  const actual =
    await vi.importActual<typeof import("api-client")>("api-client");
  return {
    ...actual,
    CookieAuth: () => ({}),
    Client: class {
      getEndpointSubscribe = subscriptions;
      getEndpointAlertRules = alertRules;
      getEventtypesByEndpointId = vi.fn().mockResolvedValue({});
      getApiEndpointstatusStatusEndpointName = vi.fn().mockResolvedValue({});
    },
  };
});
vi.mock("components/page", () => ({
  default: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));
vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast: vi.fn() }),
}));
vi.mock("components/endpoint-details/events-panel", () => ({
  default: () => <input aria-label="Message filter" />,
}));
vi.mock("components/endpoint-details/event-types-panel", async () => {
  const { useEffect } = await import("react");
  function EventTypesPanel() {
    useEffect(() => {
      eventTypes();
    }, []);
    return <input aria-label="Event type filter" />;
  }
  return { default: EventTypesPanel };
});
vi.mock("components/endpoint-details/tabs/audit-tab", async () => {
  const { useEffect } = await import("react");
  function AuditTab() {
    useEffect(() => {
      audits();
    }, []);
    return <div>Audit content</div>;
  }
  return { default: AuditTab };
});

function Navigate() {
  const navigate = useNavigate();
  return <button onClick={() => navigate("/endpoint/erp")}>Go to ERP</button>;
}
function BackButton() {
  const navigate = useNavigate();
  return <button onClick={() => navigate(-1)}>Back</button>;
}
function LocationProbe() {
  const location = useLocation();
  return <output aria-label="Location">{location.pathname + location.search}</output>;
}
function setup(url = "/endpoint/crm") {
  return render(
    <MemoryRouter initialEntries={[url]}>
      <Navigate />
      <LocationProbe />
      <Routes>
        <Route path="/endpoint/:id" element={<EndpointDetails />} />
      </Routes>
    </MemoryRouter>,
  );
}
beforeEach(() => {
  vi.clearAllMocks();
  subscriptions.mockResolvedValue([]);
  alertRules.mockResolvedValue(
    api.AlertRuleList.fromJS({ rules: [], canManage: false }),
  );
});
afterEach(cleanup);

describe("endpoint tab loading", () => {
  it("does not load unopened event-type and audit tabs", async () => {
    setup();
    await waitFor(() => expect(subscriptions).toHaveBeenCalledTimes(1));
    expect(eventTypes).not.toHaveBeenCalled();
    expect(audits).not.toHaveBeenCalled();
    expect(screen.queryByLabelText("Event type filter")).toBeNull();
  });

  it("loads on first visit and preserves panel state on subsequent visits", async () => {
    setup();
    fireEvent.change(screen.getByLabelText("Message filter"), {
      target: { value: "Failed" },
    });
    fireEvent.click(screen.getByRole("tab", { name: "Event Types" }));
    fireEvent.change(screen.getByLabelText("Event type filter"), {
      target: { value: "Account" },
    });
    fireEvent.click(screen.getByRole("tab", { name: "Audit" }));
    fireEvent.click(screen.getByRole("tab", { name: "Event Types" }));
    expect(
      (screen.getByLabelText("Event type filter") as HTMLInputElement).value,
    ).toBe("Account");
    fireEvent.click(screen.getByRole("tab", { name: "Messages" }));
    expect(
      (screen.getByLabelText("Message filter") as HTMLInputElement).value,
    ).toBe("Failed");
    expect(eventTypes).toHaveBeenCalledTimes(1);
    expect(audits).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(subscriptions).toHaveBeenCalledTimes(1));
  });

  it("discovers alerts without mounting the panel and reuses the probe result", async () => {
    subscriptions.mockResolvedValue([
      new api.EndpointSubscription({ id: "alert-1", mail: "ops@example.test" }),
    ]);
    setup();
    await waitFor(() => expect(subscriptions).toHaveBeenCalledTimes(1));
    expect(screen.queryByText("ops@example.test")).toBeNull();
    expect(alertRules).not.toHaveBeenCalled();
    fireEvent.click(screen.getByRole("tab", { name: "Alerts" }));
    expect(await screen.findByText("ops@example.test")).toBeTruthy();
    expect(subscriptions).toHaveBeenCalledTimes(1);
  });

  it("enables Alerts without subscriptions and shows the Rules section first", async () => {
    setup();
    await waitFor(() => expect(subscriptions).toHaveBeenCalledTimes(1));
    const alerts = screen.getByRole("tab", { name: "Alerts" }) as HTMLButtonElement;
    expect(alerts.disabled).toBe(false);
    expect(screen.queryByText(/No alert subscriptions/)).toBeNull();
    fireEvent.click(alerts);
    expect(await screen.findByText("No alert rules")).toBeTruthy();
    expect(alertRules).toHaveBeenCalledWith("crm");
    const sections = screen
      .getAllByRole("region")
      .map((r) => r.getAttribute("aria-labelledby"))
      .map((id) => document.getElementById(id!)?.textContent);
    expect(sections).toEqual(["Rules", "Every failure"]);
    expect(screen.getByText(/No alert subscriptions/)).toBeTruthy();
    expect(
      screen.getByRole("link", { name: "endpoint list" }).getAttribute("href"),
    ).toBe("/Endpoints");
  });

  it("opens the tab named in the URL", async () => {
    setup("/endpoint/crm?tab=alerts");
    expect(
      screen.getByRole("tab", { name: "Alerts" }).getAttribute("aria-selected"),
    ).toBe("true");
    expect(await screen.findByText("No alert rules")).toBeTruthy();
    expect(alertRules).toHaveBeenCalledWith("crm");
  });

  it("falls back to Messages for a tab it doesn't know", () => {
    setup("/endpoint/crm?tab=settings");
    expect(
      screen.getByRole("tab", { name: "Messages" }).getAttribute("aria-selected"),
    ).toBe("true");
    expect(screen.getByLabelText("Message filter")).toBeTruthy();
    expect(alertRules).not.toHaveBeenCalled();
  });

  it("keeps the URL on the open tab without adding history entries", () => {
    render(
      <MemoryRouter initialEntries={["/start", "/endpoint/crm?tab=audit&x=1"]} initialIndex={1}>
        <BackButton />
        <LocationProbe />
        <Routes>
          <Route path="/endpoint/:id" element={<EndpointDetails />} />
          <Route path="/start" element={<p>Start page</p>} />
        </Routes>
      </MemoryRouter>,
    );
    const location = () => screen.getByLabelText("Location").textContent;
    expect(screen.getByText("Audit content")).toBeTruthy();

    fireEvent.click(screen.getByRole("tab", { name: "Alerts" }));
    expect(location()).toBe("/endpoint/crm?tab=alerts&x=1");
    fireEvent.click(screen.getByRole("tab", { name: "Messages" }));
    expect(location()).toBe("/endpoint/crm?x=1");
    // The tab opened from the URL stays mounted, like any visited tab.
    fireEvent.click(screen.getByRole("tab", { name: "Audit" }));
    expect(audits).toHaveBeenCalledTimes(1);

    fireEvent.click(screen.getByText("Back"));
    expect(screen.getByText("Start page")).toBeTruthy();
  });

  it("resets visited panels on endpoint navigation and ignores stale alert probes", async () => {
    let resolveCrm!: (value: api.EndpointSubscription[]) => void;
    subscriptions.mockImplementation((id: string) =>
      id === "crm"
        ? new Promise((resolve) => {
            resolveCrm = resolve;
          })
        : Promise.resolve([]),
    );
    setup();
    fireEvent.click(screen.getByRole("tab", { name: "Audit" }));
    fireEvent.click(screen.getByText("Go to ERP"));
    await waitFor(() => expect(subscriptions).toHaveBeenCalledWith("erp"));
    await act(async () =>
      resolveCrm([new api.EndpointSubscription({ id: "crm-only" })]),
    );
    expect(
      screen
        .getByRole("tab", { name: "Messages" })
        .getAttribute("aria-selected"),
    ).toBe("true");
    expect(screen.queryByText("Audit content")).toBeNull();
    fireEvent.click(screen.getByRole("tab", { name: "Alerts" }));
    expect(screen.getByText(/No alert subscriptions/)).toBeTruthy();
    expect(alertRules).toHaveBeenCalledWith("erp");
    expect(alertRules).not.toHaveBeenCalledWith("crm");
  });
});
