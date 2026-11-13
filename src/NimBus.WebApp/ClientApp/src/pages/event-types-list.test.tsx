import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  cleanup,
  render,
  screen,
  waitFor,
  within,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import * as api from "api-client";
import EventTypesList from "./event-types-list";

const mocks = vi.hoisted(() => ({
  getEventTypes: vi.fn(),
  getApiAppStats: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual =
    await vi.importActual<typeof import("api-client")>("api-client");

  class FakeClient {
    getEventTypes = mocks.getEventTypes;
    getApiAppStats = mocks.getApiAppStats;
  }

  return {
    ...actual,
    Client: FakeClient,
    CookieAuth: () => ({}),
  };
});

beforeEach(() => {
  mocks.getApiAppStats.mockReset().mockResolvedValue(
    Object.assign(new api.ApplicationStatus(), {
      env: "dev",
      nimbusVersion: "3.2.1",
      platformName: "EET.Platform",
      platformVersion: "1.0.1",
    }),
  );
  mocks.getEventTypes.mockReset().mockResolvedValue([
    Object.assign(new api.EventType(), {
      id: "orders.created",
      name: "OrderCreated",
      namespace: "NimBus.Sales.Events",
      description: "Published when a sales order is created.",
      producerCount: 1,
      consumerCount: 2,
      producers: ["SalesEndpoint"],
      consumers: ["BillingEndpoint", "WarehouseEndpoint"],
    }),
  ]);
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("EventTypesList", () => {
  it("always renders the table with endpoint names and no view switcher", async () => {
    render(
      <MemoryRouter initialEntries={["/EventTypes?viewMode=cards"]}>
        <EventTypesList />
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByText("OrderCreated")).toBeDefined());

    expect(
      screen.getAllByRole("columnheader").map((header) => header.textContent),
    ).toEqual(["Name", "Namespace", "Producers", "Consumers", "Description"]);
    const table = screen.getByRole("table");
    expect(within(table).getByText("SalesEndpoint")).toBeDefined();
    expect(within(table).getByText("BillingEndpoint")).toBeDefined();
    expect(within(table).getByText("WarehouseEndpoint")).toBeDefined();
    expect(screen.queryByRole("button", { name: "Card view" })).toBeNull();
    expect(screen.queryByRole("button", { name: "Table view" })).toBeNull();
  });

  it("shows each consumer's routing keys and the unmatched consumer (Spec 045)", async () => {
    mocks.getEventTypes.mockResolvedValue([
      Object.assign(new api.EventType(), {
        id: "CustomerCreated",
        name: "CustomerCreated",
        producers: ["CrmEndpoint"],
        consumers: ["ErpInstance01", "ErpInstance02", "ErpUnrouted", "Reporting"],
        routingKeyProperty: "ErpInstance",
        consumerRouting: [
          Object.assign(new api.ConsumerRouting(), { endpoint: "ErpInstance01", routingKeys: ["01"], unmatched: false }),
          Object.assign(new api.ConsumerRouting(), { endpoint: "ErpInstance02", routingKeys: ["02", "03"], unmatched: false }),
          Object.assign(new api.ConsumerRouting(), { endpoint: "ErpUnrouted", routingKeys: [], unmatched: true }),
          Object.assign(new api.ConsumerRouting(), { endpoint: "Reporting", routingKeys: [], unmatched: false }),
        ],
      }),
    ]);

    render(
      <MemoryRouter initialEntries={["/EventTypes"]}>
        <EventTypesList />
      </MemoryRouter>,
    );

    const table = await screen.findByRole("table");
    expect(within(table).getByText("· key 01")).toBeDefined();
    expect(within(table).getByText("· keys 02, 03")).toBeDefined();
    expect(within(table).getByText("· unmatched")).toBeDefined();
    expect(within(table).getByTitle("Reporting")).toBeDefined();
  });

  it("names the platform catalog package in the subtitle once the status has loaded", async () => {
    render(
      <MemoryRouter initialEntries={["/EventTypes"]}>
        <EventTypesList />
      </MemoryRouter>,
    );

    await waitFor(() =>
      expect(
        screen.getByText("1 contract across 1 namespace · EET.Platform 1.0.1"),
      ).toBeDefined(),
    );
  });
});
