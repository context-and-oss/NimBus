import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  act,
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { MemoryRouter, Route, Routes, useNavigate } from "react-router-dom";
import * as api from "api-client";
import EndpointDetails from "./endpoint-details";

const { subscriptions, eventTypes, audits } = vi.hoisted(() => ({
  subscriptions: vi.fn(),
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
function setup() {
  return render(
    <MemoryRouter initialEntries={["/endpoint/crm"]}>
      <Navigate />
      <Routes>
        <Route path="/endpoint/:id" element={<EndpointDetails />} />
      </Routes>
    </MemoryRouter>,
  );
}
beforeEach(() => {
  vi.clearAllMocks();
  subscriptions.mockResolvedValue([]);
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
    await waitFor(() =>
      expect(
        (screen.getByRole("tab", { name: "Alerts" }) as HTMLButtonElement)
          .disabled,
      ).toBe(false),
    );
    expect(screen.queryByText("ops@example.test")).toBeNull();
    fireEvent.click(screen.getByRole("tab", { name: "Alerts" }));
    expect(await screen.findByText("ops@example.test")).toBeTruthy();
    expect(subscriptions).toHaveBeenCalledTimes(1);
  });

  it("keeps Alerts disabled when there are no subscriptions", async () => {
    setup();
    await waitFor(() => expect(subscriptions).toHaveBeenCalledTimes(1));
    expect(
      (screen.getByRole("tab", { name: "Alerts" }) as HTMLButtonElement)
        .disabled,
    ).toBe(true);
    expect(screen.getByText(/No alert subscriptions/)).toBeTruthy();
    expect(
      screen.getByRole("link", { name: "endpoint list" }).getAttribute("href"),
    ).toBe("/Endpoints");
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
    expect(
      (screen.getByRole("tab", { name: "Alerts" }) as HTMLButtonElement)
        .disabled,
    ).toBe(true);
  });
});
