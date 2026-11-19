import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import SimulationSettings from "./simulation-settings";
import { simulationStatus } from "components/simulate/simulation-fixtures";
import { renderInPanel } from "../../test-utils/render-in-panel";

const mocks = vi.hoisted(() => ({
  getAdminSimulation: vi.fn(),
  putAdminSimulationSettings: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getAdminSimulation = mocks.getAdminSimulation;
    putAdminSimulationSettings = mocks.putAdminSimulationSettings;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

const renderTab = () =>
  render(
    <MemoryRouter>
      <SimulationSettings />
    </MemoryRouter>,
  );

beforeEach(() => {
  mocks.getAdminSimulation.mockReset().mockResolvedValue(simulationStatus({ settings: { enabled: true, autoStopMinutes: 60, rateCeilingPerMinute: 600, ownedEndpoints: [] } }));
  mocks.putAdminSimulationSettings.mockReset().mockImplementation((body: { ownedEndpoints: string[] }) =>
    Promise.resolve(simulationStatus({ settings: body })),
  );
});

afterEach(() => cleanup());

describe("Settings → Simulation", () => {
  it("lists consuming endpoints with ownership unchecked by default", async () => {
    renderTab();

    const billing = (await screen.findByLabelText("Simulator owns BillingEndpoint")) as HTMLInputElement;
    const warehouse = screen.getByLabelText("Simulator owns WarehouseEndpoint") as HTMLInputElement;
    expect(billing.checked).toBe(false);
    expect(warehouse.checked).toBe(false);
    expect(screen.queryByLabelText("Simulator owns StorefrontEndpoint")).toBeNull();
    expect(screen.getAllByText("permanently blocked")).toHaveLength(4);
  });

  it("asks for confirmation before taking ownership and saves ownedEndpoints", async () => {
    renderTab();

    fireEvent.click(await screen.findByLabelText("Simulator owns WarehouseEndpoint"));
    expect(screen.getByRole("dialog")).toBeTruthy();
    expect((screen.getByLabelText("Simulator owns WarehouseEndpoint") as HTMLInputElement).checked).toBe(false);

    fireEvent.click(screen.getByRole("button", { name: "Take ownership" }));
    expect((screen.getByLabelText("Simulator owns WarehouseEndpoint") as HTMLInputElement).checked).toBe(true);

    fireEvent.click(screen.getByRole("button", { name: "Save simulation settings" }));
    await waitFor(() => expect(mocks.putAdminSimulationSettings).toHaveBeenCalledTimes(1));
    const body = mocks.putAdminSimulationSettings.mock.calls[0][0];
    expect(body.ownedEndpoints).toEqual(["WarehouseEndpoint"]);
    expect(body.enabled).toBe(true);
    expect(body.autoStopMinutes).toBe(60);
    expect(body.rateCeilingPerMinute).toBe(600);
  });

  it("cancelling the confirmation leaves the endpoint External", async () => {
    renderTab();

    fireEvent.click(await screen.findByLabelText("Simulator owns BillingEndpoint"));
    fireEvent.click(screen.getByRole("button", { name: "Cancel" }));

    expect((screen.getByLabelText("Simulator owns BillingEndpoint") as HTMLInputElement).checked).toBe(false);
  });

  it.each([
    ["environmentMissing", "", /missing or blank/],
    ["notAllowed", "test", /not in NimBus:Simulation:AllowedEnvironments/],
    ["production", "prod", /can never run in production/],
  ])("disables every control when blocked (%s)", async (reason, environment, text) => {
    mocks.getAdminSimulation.mockResolvedValue(simulationStatus({ allowed: false, blockedReason: reason, environment }));
    renderTab();

    expect((await screen.findByRole("alert")).textContent).toMatch(text);
    expect((screen.getByLabelText("Simulator owns BillingEndpoint") as HTMLInputElement).disabled).toBe(true);
    expect((screen.getByRole("switch", { name: "Enable simulate mode" }) as HTMLButtonElement).disabled).toBe(true);
    expect((screen.getByRole("button", { name: "Save simulation settings" }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByRole("link", { name: /Open Simulate/ })).toBeNull();
  });

  it("shows the server's violations when a save is rejected", async () => {
    mocks.putAdminSimulationSettings.mockRejectedValue({ errors: ["Endpoint ownership cannot change while the simulation is running."] });
    renderTab();

    fireEvent.click(await screen.findByRole("button", { name: "Save simulation settings" }));

    expect((await screen.findByRole("alert")).textContent).toMatch(/cannot change while the simulation is running/);
  });

  describe("consuming endpoints table", () => {
    const inspector = () => within(screen.getByRole("complementary", { name: "Inspector" }));

    it("lists each consuming endpoint with its ownership", async () => {
      renderTab();

      const table = within(await screen.findByRole("table", { name: "Consuming endpoints" }));
      const row = within(table.getByRole("button", { name: "BillingEndpoint" }).closest("tr") as HTMLElement);
      expect(row.getByText("External")).toBeTruthy();
      expect(row.getByLabelText("Simulator owns BillingEndpoint")).toBeTruthy();
      expect(table.queryByRole("button", { name: "StorefrontEndpoint" })).toBeNull();
    });

    it("asks for a selection while no endpoint is selected", async () => {
      renderTab();

      await screen.findByRole("table", { name: "Consuming endpoints" });
      expect(inspector().getByText("Select an endpoint to see who owns it.")).toBeTruthy();
    });

    it("explains the selected endpoint's ownership in the inspector", async () => {
      renderTab();

      const select = await screen.findByRole("button", { name: "BillingEndpoint" });
      fireEvent.click(select);

      expect(select.getAttribute("aria-pressed")).toBe("true");
      expect(inspector().getByRole("heading", { name: "BillingEndpoint" })).toBeTruthy();
      expect(inspector().getByText("External")).toBeTruthy();
      expect(inspector().getByText(/handled by its own process/)).toBeTruthy();
      expect(inspector().getByText("OrderDeliveryDetailsCaptured")).toBeTruthy();
      expect(inspector().queryByRole("checkbox")).toBeNull();
    });

    it("follows the draft when the selected endpoint is handed to the simulator", async () => {
      renderTab();

      fireEvent.click(await screen.findByRole("button", { name: "BillingEndpoint" }));
      // getByLabelText throws on a second checkbox with the same name.
      fireEvent.click(screen.getByLabelText("Simulator owns BillingEndpoint"));
      fireEvent.click(screen.getByRole("button", { name: "Take ownership" }));

      expect(inspector().getByText("Simulated")).toBeTruthy();
      expect(inspector().getByText(/simulated handler on this endpoint.s real subscription/)).toBeTruthy();
      expect(inspector().getByText(/Not saved yet/)).toBeTruthy();
    });

    it("warns about a live instance in the row and the inspector", async () => {
      const status = simulationStatus();
      status.endpoints![0].liveInstanceWarning = true;
      mocks.getAdminSimulation.mockResolvedValue(status);
      renderTab();

      const select = await screen.findByRole("button", { name: "BillingEndpoint" });
      expect(within(select.closest("tr") as HTMLElement).getByText("Live instance")).toBeTruthy();
      fireEvent.click(select);

      expect(inspector().getByText(/A live instance answered a heartbeat recently/)).toBeTruthy();
    });

    it("still lets a row be inspected when simulation is blocked", async () => {
      mocks.getAdminSimulation.mockResolvedValue(simulationStatus({ allowed: false, blockedReason: "notAllowed", environment: "test" }));
      renderTab();

      fireEvent.click(await screen.findByRole("button", { name: "WarehouseEndpoint" }));

      expect(inspector().getByRole("heading", { name: "WarehouseEndpoint" })).toBeTruthy();
      expect((screen.getByLabelText("Simulator owns WarehouseEndpoint") as HTMLInputElement).disabled).toBe(true);
    });

    it("opens the Settings panel's inspector drawer on the selected endpoint", async () => {
      renderInPanel(<SimulationSettings />, "simulation", "endpoints");

      await screen.findByRole("table", { name: "Consuming endpoints" });
      expect(screen.queryByRole("complementary", { name: "Inspector" })).toBeNull();
      fireEvent.click(await screen.findByRole("button", { name: "WarehouseEndpoint" }));

      expect(screen.getAllByRole("complementary", { name: "Inspector" })).toHaveLength(1);
      expect(inspector().getByRole("heading", { name: "WarehouseEndpoint" })).toBeTruthy();
    });
  });

  // Spec 038 slice 2: inside the Settings panel frame.
  describe("in the Settings panel", () => {
    it("shows one tab at a time", async () => {
      renderInPanel(<SimulationSettings />, "simulation", "endpoints");

      expect(await screen.findByLabelText("Simulator owns BillingEndpoint")).toBeTruthy();
      expect(screen.queryByRole("switch", { name: "Enable simulate mode" })).toBeNull();
    });

    it("counts unsaved edits, including a confirmed ownership change, and resets them", async () => {
      const { onDirtyChange } = renderInPanel(<SimulationSettings />, "simulation", "endpoints");

      fireEvent.click(await screen.findByLabelText("Simulator owns BillingEndpoint"));
      fireEvent.click(screen.getByRole("button", { name: "Take ownership" }));

      expect(onDirtyChange).toHaveBeenLastCalledWith(1);
      fireEvent.click(screen.getByRole("button", { name: "Reset" }));
      expect(onDirtyChange).toHaveBeenLastCalledWith(0);
    });

    it("closes only the ownership dialog on Escape", async () => {
      renderInPanel(<SimulationSettings />, "simulation", "endpoints");
      fireEvent.click(await screen.findByLabelText("Simulator owns BillingEndpoint"));
      expect(screen.getByRole("button", { name: "Take ownership" })).toBeTruthy();

      fireEvent.keyDown(document, { key: "Escape" });

      expect(screen.queryByRole("button", { name: "Take ownership" })).toBeNull();
      expect(screen.getByLabelText("Simulator owns BillingEndpoint")).toBeTruthy();
    });
  });
});
