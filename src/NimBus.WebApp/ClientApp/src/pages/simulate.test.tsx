import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import Simulate from "./simulate";
import { simulationStatus } from "components/simulate/simulation-fixtures";

const mocks = vi.hoisted(() => ({
  access: { current: { canManageAccessControl: true } as { canManageAccessControl: boolean } | null },
  getAdminSimulation: vi.fn(),
  putAdminSimulationConfig: vi.fn(),
  postAdminSimulationStart: vi.fn(),
  postAdminSimulationPause: vi.fn(),
  postAdminSimulationStop: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getAdminSimulation = mocks.getAdminSimulation;
    putAdminSimulationConfig = mocks.putAdminSimulationConfig;
    postAdminSimulationStart = mocks.postAdminSimulationStart;
    postAdminSimulationPause = mocks.postAdminSimulationPause;
    postAdminSimulationStop = mocks.postAdminSimulationStop;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

vi.mock("hooks/use-access", () => ({ useAccess: () => ({ access: mocks.access.current }) }));
vi.mock("components/page", () => ({
  default: ({ title, actions, children }: { title: string; actions?: React.ReactNode; children: React.ReactNode }) => (
    <div>
      <h1>{title}</h1>
      {actions}
      {children}
    </div>
  ),
}));

const renderPage = () =>
  render(
    <MemoryRouter>
      <Simulate />
    </MemoryRouter>,
  );

beforeEach(() => {
  mocks.access.current = { canManageAccessControl: true };
  mocks.getAdminSimulation.mockReset().mockResolvedValue(simulationStatus());
  mocks.putAdminSimulationConfig.mockReset().mockImplementation(() => Promise.resolve(simulationStatus()));
  mocks.postAdminSimulationStart.mockReset().mockResolvedValue(simulationStatus({ state: "running" }));
  mocks.postAdminSimulationPause.mockReset().mockResolvedValue(simulationStatus({ state: "paused" }));
  mocks.postAdminSimulationStop.mockReset().mockResolvedValue(simulationStatus({ state: "stopped" }));
});

afterEach(() => cleanup());

const button = (name: string) => screen.getByRole("button", { name }) as HTMLButtonElement;

describe("Simulate page", () => {
  it.each([
    ["stopped", { Start: true, Pause: false, Stop: false }],
    ["running", { Start: false, Pause: true, Stop: true }],
    ["paused", { Resume: true, Pause: false, Stop: true }],
    ["pausing", { Start: false, Pause: false, Stop: false }],
    ["stopping", { Start: false, Pause: false, Stop: false }],
  ] as const)("enables the right buttons while %s", async (state, expected) => {
    mocks.getAdminSimulation.mockResolvedValue(simulationStatus({ state }));
    renderPage();
    await screen.findByTestId("simulation-state");

    for (const [name, enabled] of Object.entries(expected)) {
      expect(button(name).disabled, `${name} while ${state}`).toBe(!enabled);
    }
  });

  // Spec 038 §9.2: the simulator's settings are one click away.
  it("links to the simulation settings", async () => {
    renderPage();

    const link = await screen.findByRole("link", { name: /Simulation settings/ });
    expect(link.getAttribute("href")).toBe("/Settings/simulation");
  });

  it("starts the simulation", async () => {
    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: "Start" }));

    await waitFor(() => expect(mocks.postAdminSimulationStart).toHaveBeenCalledTimes(1));
    await waitFor(() => expect(screen.getByTestId("simulation-state").textContent).toBe("running"));
  });

  it("renders the not-found page for a non-owner without calling the API", async () => {
    mocks.access.current = { canManageAccessControl: false };
    renderPage();

    expect(await screen.findByText("Not Found")).toBeTruthy();
    expect(mocks.getAdminSimulation).not.toHaveBeenCalled();
  });

  it("renders the not-found page when simulate mode is disabled", async () => {
    mocks.getAdminSimulation.mockResolvedValue(simulationStatus({ enabled: false }));
    renderPage();

    expect(await screen.findByText("Not Found")).toBeTruthy();
  });

  it("a failure-mode change sends the whole config", async () => {
    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: "Edit failure mode" }));
    fireEvent.change(screen.getByLabelText("Mode"), { target: { value: "random" } });
    fireEvent.change(screen.getByLabelText("Failure rate"), { target: { value: "250" } });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    await waitFor(() => expect(mocks.putAdminSimulationConfig).toHaveBeenCalledTimes(1));
    const config = mocks.putAdminSimulationConfig.mock.calls[0][0].toJSON();
    expect(config.speed).toBe(1);
    expect(config.publishers).toHaveLength(1);
    expect(config.publishers[0].eventTypes).toHaveLength(2);
    expect(config.subscribers).toHaveLength(1);
    expect(config.subscribers[0].endpointId).toBe("BillingEndpoint");
    expect(config.subscribers[0].failure.mode).toBe("random");
    expect(config.subscribers[0].failure.rate).toBe(100);
  });

  it("presets only touch owned endpoints", async () => {
    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: "Poison one type" }));

    await waitFor(() => expect(mocks.putAdminSimulationConfig).toHaveBeenCalledTimes(1));
    const config = mocks.putAdminSimulationConfig.mock.calls[0][0].toJSON();
    expect(config.subscribers.map((s: { endpointId: string }) => s.endpointId)).toEqual(["BillingEndpoint"]);
    expect(config.subscribers[0].failure.mode).toBe("poison");
    expect(config.subscribers[0].failure.eventTypeIds).toEqual(["OrderPlaced"]);
    expect(config.publishers).toHaveLength(1);
  });

  it("changing speed sends one of the allowed values with the rest of the config", async () => {
    renderPage();
    fireEvent.click(await screen.findByRole("radio", { name: "5×" }));

    await waitFor(() => expect(mocks.putAdminSimulationConfig).toHaveBeenCalledTimes(1));
    const config = mocks.putAdminSimulationConfig.mock.calls[0][0].toJSON();
    expect(config.speed).toBe(5);
    expect(config.publishers[0].eventTypes).toHaveLength(2);
    expect(screen.getAllByRole("radio").map((r) => r.textContent)).toEqual(["0.5×", "1×", "2×", "5×", "10×", "20×"]);
  });

  it("lists External endpoints read-only", async () => {
    renderPage();
    const external = await screen.findByRole("list", { name: "External subscribers" });

    expect(within(external).getByText("WarehouseEndpoint")).toBeTruthy();
    expect(within(external).getByText("handled by its own process")).toBeTruthy();
    expect(within(external).queryByRole("button")).toBeNull();
  });

  it("renders the capped indicator", async () => {
    mocks.getAdminSimulation.mockResolvedValue(simulationStatus({ capped: true, state: "running" }));
    renderPage();

    expect(await screen.findByText("capped at 600/min")).toBeTruthy();
  });

  it("renders feed outcomes", async () => {
    mocks.getAdminSimulation.mockResolvedValue(
      simulationStatus({
        state: "running",
        recent: [
          { at: "2026-09-23T12:00:02Z", endpointId: "BillingEndpoint", eventTypeId: "OrderPlaced", sessionId: "sim-StorefrontEndpoint-001", messageId: "m2", outcome: "threw", attempt: 2, latencyMs: 41, error: "boom" },
          { at: "2026-09-23T12:00:01Z", endpointId: "BillingEndpoint", eventTypeId: "OrderPlaced", sessionId: "sim-StorefrontEndpoint-002", messageId: "m1", outcome: "completed", attempt: 1, latencyMs: 33 },
          { at: "2026-09-23T12:00:00Z", endpointId: "BillingEndpoint", eventTypeId: "OrderPlaced", sessionId: "sim-StorefrontEndpoint-003", messageId: "m0", outcome: "poisoned", attempt: 1, latencyMs: 12, error: "bad" },
        ],
      }),
    );
    renderPage();

    const feed = await screen.findByRole("table", { name: "Recent deliveries" });
    expect(within(feed).getByText("Threw (attempt 2)")).toBeTruthy();
    expect(within(feed).getByText("Completed")).toBeTruthy();
    expect(within(feed).getByText("Poisoned")).toBeTruthy();
    expect(within(feed).getByText("boom")).toBeTruthy();
    expect(within(feed).getByRole("link", { name: "sim-StorefrontEndpoint-001" }).getAttribute("href")).toBe(
      "/Messages?sessionId=sim-StorefrontEndpoint-001",
    );
  });

  it("shows the server's reason when a transition is refused", async () => {
    mocks.postAdminSimulationStart.mockRejectedValue({ errors: ["A Stopping transition is in progress."] });
    renderPage();
    fireEvent.click(await screen.findByRole("button", { name: "Start" }));

    expect((await screen.findByRole("alert")).textContent).toMatch(/Stopping transition/);
  });
});
