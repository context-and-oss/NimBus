import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { useEffect } from "react";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { Link, MemoryRouter, Route, Routes } from "react-router-dom";
import * as api from "api-client";
import EventTypeDetails from "./event-type-details";

const mocks = vi.hoisted(() => ({
  getEventTypeDetails: vi.fn(),
  examplePayloadMounted: vi.fn(),
  miniMapProps: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual =
    await vi.importActual<typeof import("api-client")>("api-client");

  class FakeClient {
    getEventtypesEventtypeid = mocks.getEventTypeDetails;
  }

  return {
    ...actual,
    Client: FakeClient,
    CookieAuth: () => ({}),
  };
});

vi.mock("components/ui/topology-mini-map", () => ({
  TopologyMiniMap: (props: unknown) => {
    mocks.miniMapProps(props);
    return <div data-testid="topology" />;
  },
}));

vi.mock("components/event-types/event-type-properties-table", () => ({
  default: () => <div data-testid="properties" />,
}));

vi.mock("components/event-types/event-type-example-payload", () => ({
  default: function ExamplePayload({ eventType }: { eventType: api.EventType }) {
    useEffect(() => {
      mocks.examplePayloadMounted(eventType.id);
    }, []); // eslint-disable-line react-hooks/exhaustive-deps -- records mounts only
    return <div data-testid="example-payload" />;
  },
}));

beforeEach(() => {
  mocks.getEventTypeDetails.mockReset().mockResolvedValue(
    Object.assign(new api.EventTypeDetails(), {
      eventType: Object.assign(new api.EventType(), {
        id: "orders.placed",
        name: "OrderPlaced",
        description: "Published when a customer places a new order.",
        namespace: "NimBus.Events.Orders",
        properties: [],
      }),
      codeRepoLink: "https://example.test/source/OrderPlaced.cs",
      producers: ["StorefrontEndpoint"],
      consumers: ["BillingEndpoint", "WarehouseEndpoint"],
    }),
  );
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("EventTypeDetails", () => {
  it("does not show the source repository link", async () => {
    render(
      <MemoryRouter initialEntries={["/EventTypes/Details/orders.placed"]}>
        <Routes>
          <Route
            path="/EventTypes/Details/:id"
            element={<EventTypeDetails />}
          />
        </Routes>
      </MemoryRouter>,
    );

    await waitFor(() => expect(screen.getByText("OrderPlaced")).toBeDefined());

    expect(screen.queryByRole("link", { name: /view source/i })).toBeNull();
  });

  it("shows the routing key and each consumer's keys in the topology (Spec 045)", async () => {
    mocks.getEventTypeDetails.mockResolvedValue(
      Object.assign(new api.EventTypeDetails(), {
        eventType: Object.assign(new api.EventType(), {
          id: "CustomerCreated",
          name: "CustomerCreated",
          properties: [],
          routingKeyProperty: "ErpInstance",
        }),
        producers: ["CrmEndpoint"],
        consumers: ["ErpInstance01", "ErpUnrouted"],
        consumerRouting: [
          Object.assign(new api.ConsumerRouting(), { endpoint: "ErpInstance01", routingKeys: ["01"], unmatched: false }),
          Object.assign(new api.ConsumerRouting(), { endpoint: "ErpUnrouted", routingKeys: [], unmatched: true }),
        ],
      }),
    );

    render(
      <MemoryRouter initialEntries={["/EventTypes/Details/CustomerCreated"]}>
        <Routes>
          <Route
            path="/EventTypes/Details/:id"
            element={<EventTypeDetails />}
          />
        </Routes>
      </MemoryRouter>,
    );

    await waitFor(() =>
      expect(mocks.miniMapProps).toHaveBeenLastCalledWith(
        expect.objectContaining({
          consumerNotes: { ErpInstance01: "key 01", ErpUnrouted: "unmatched" },
          centerMeta: "0 fields · routed by ErpInstance",
        }),
      ),
    );
  });

  it("remounts the example payload for another event type, so a pending send cannot report into it", async () => {
    mocks.getEventTypeDetails.mockImplementation(async (id: string) =>
      Object.assign(new api.EventTypeDetails(), {
        eventType: Object.assign(new api.EventType(), {
          id,
          name: id === "orders.placed" ? "OrderPlaced" : "OrderShipped",
          properties: [],
        }),
        producers: [],
        consumers: [],
      }),
    );

    render(
      <MemoryRouter initialEntries={["/EventTypes/Details/orders.placed"]}>
        <Link to="/EventTypes/Details/orders.shipped">Next type</Link>
        <Routes>
          <Route
            path="/EventTypes/Details/:id"
            element={<EventTypeDetails />}
          />
        </Routes>
      </MemoryRouter>,
    );
    await waitFor(() => expect(screen.getByText("OrderPlaced")).toBeDefined());

    await userEvent.click(screen.getByRole("link", { name: "Next type" }));
    await waitFor(() => expect(screen.getByText("OrderShipped")).toBeDefined());

    expect(mocks.examplePayloadMounted.mock.calls).toEqual([
      ["orders.placed"],
      ["orders.shipped"],
    ]);
  });
});
