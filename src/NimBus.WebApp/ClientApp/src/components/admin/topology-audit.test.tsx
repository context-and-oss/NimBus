import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import TopologyAudit from "./topology-audit";

const { configMock, auditMock } = vi.hoisted(() => ({
  configMock: vi.fn(),
  auditMock: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual =
    await vi.importActual<typeof import("api-client")>("api-client");
  class Client {
    getAdminPlatformConfig = configMock;
    getAdminTopology = auditMock;
    postAdminTopologyRemoveDeprecated = vi.fn();
  }
  return { ...actual, Client, CookieAuth: () => ({}) };
});

afterEach(() => {
  cleanup();
  vi.resetAllMocks();
});

const inSync = (topic: string) => ({
  topicName: topic,
  hasDeprecated: false,
  subscriptions: [
    { name: topic, isDeprecated: false, rules: [{ name: "to-" + topic }] },
  ],
});

describe("TopologyAudit", () => {
  it("audits every endpoint at once and opens one endpoint's detail", async () => {
    configMock.mockResolvedValue({
      endpoints: [
        { name: "CrmEndpoint" },
        { name: "ErpEndpoint" },
        { name: "BrokenEndpoint" },
      ],
    });
    auditMock.mockImplementation(async (endpoint: string) => {
      if (endpoint === "BrokenEndpoint") throw new Error("Topic not found");
      if (endpoint === "CrmEndpoint") return inSync("crmendpoint");
      return {
        topicName: "erpendpoint",
        hasDeprecated: true,
        subscriptions: [
          {
            name: "oldsub",
            isDeprecated: true,
            rules: [{ name: "oldrule", isDeprecated: true }],
          },
          {
            name: "erpendpoint",
            isDeprecated: false,
            rules: [
              { name: "staleevent", isDeprecated: true },
              { name: "to-erpendpoint" },
            ],
          },
        ],
      };
    });
    render(<TopologyAudit />);

    await userEvent.click(
      await screen.findByRole("button", { name: "Audit all endpoints" }),
    );

    const summary = await screen.findByRole("table", {
      name: "Catalog drift by endpoint",
    });
    expect(auditMock).toHaveBeenCalledTimes(3);
    const rows = within(summary).getAllByRole("row").slice(1);
    // Endpoints needing attention come first.
    expect(
      rows.map((r) => within(r).getAllByRole("cell")[0].textContent),
    ).toEqual(["ErpEndpoint", "BrokenEndpoint", "CrmEndpoint"]);
    expect(within(rows[0]).getByText("2 deprecated")).toBeTruthy();
    expect(within(rows[1]).getByText("Topic not found")).toBeTruthy();
    expect(within(rows[2]).getByText("In sync")).toBeTruthy();
    expect(
      screen.getByText(
        "1 of 3 endpoints drift from the catalog; 1 could not be audited.",
      ),
    ).toBeTruthy();

    await userEvent.click(
      within(rows[0]).getByRole("button", { name: "ErpEndpoint" }),
    );
    expect(await screen.findByRole("heading", { name: "erpendpoint" })).toBeTruthy();
    expect(
      screen.getByRole("button", { name: "Remove Deprecated" }),
    ).toBeTruthy();
    // The detail reuses the result already fetched.
    expect(auditMock).toHaveBeenCalledTimes(3);
  });

  it("says when every endpoint matches the catalog", async () => {
    configMock.mockResolvedValue({
      endpoints: [{ name: "CrmEndpoint" }, { name: "ErpEndpoint" }],
    });
    auditMock.mockImplementation(async (endpoint: string) =>
      inSync(endpoint.toLowerCase()),
    );
    render(<TopologyAudit />);

    await userEvent.click(
      await screen.findByRole("button", { name: "Audit all endpoints" }),
    );

    expect(
      await screen.findByText("All 2 audited endpoints match the catalog."),
    ).toBeTruthy();
    expect(screen.getAllByText("In sync")).toHaveLength(2);
  });
});

// The Topology page lists the topics beside this view and chooses the endpoint.
describe("TopologyAudit with the endpoint chosen by the page", () => {
  it("audits the given endpoint at once, without the picker", async () => {
    configMock.mockResolvedValue({ endpoints: [{ name: "CrmEndpoint" }] });
    auditMock.mockResolvedValue(inSync("crmendpoint"));

    render(<TopologyAudit endpoint="CrmEndpoint" />);

    expect(await screen.findByRole("heading", { name: "crmendpoint" })).toBeTruthy();
    expect(auditMock).toHaveBeenCalledWith("CrmEndpoint");
    expect(screen.queryByRole("button", { name: "Audit Topology" })).toBeNull();
    expect(screen.getByRole("button", { name: "Audit again" })).toBeTruthy();
  });

  it("shows only the newest endpoint's audit when the page switches quickly", async () => {
    configMock.mockResolvedValue({ endpoints: [{ name: "CrmEndpoint" }, { name: "ErpEndpoint" }] });
    let resolveCrm: (value: unknown) => void = () => {};
    auditMock.mockImplementation((endpoint: string) =>
      endpoint === "CrmEndpoint"
        ? new Promise((resolve) => {
            resolveCrm = resolve;
          })
        : Promise.resolve(inSync("erpendpoint")),
    );

    const { rerender } = render(<TopologyAudit endpoint="CrmEndpoint" />);
    rerender(<TopologyAudit endpoint="ErpEndpoint" />);
    expect(await screen.findByRole("heading", { name: "erpendpoint" })).toBeTruthy();
    resolveCrm(inSync("crmendpoint"));

    await new Promise((r) => setTimeout(r, 0));
    expect(screen.queryByRole("heading", { name: "crmendpoint" })).toBeNull();
  });

  it("says a topic outside the catalog has nothing to compare", async () => {
    configMock.mockResolvedValue({ endpoints: [{ name: "CrmEndpoint" }] });
    auditMock.mockRejectedValue(new Error("Topic not found"));

    render(<TopologyAudit endpoint="legacy-bridge" />);

    expect(await screen.findByText(/isn.t an endpoint in the catalog/)).toBeTruthy();
    expect(screen.queryByText("Topic not found")).toBeNull();
  });

  it("asks the page to open an endpoint from the all-endpoints summary", async () => {
    configMock.mockResolvedValue({ endpoints: [{ name: "CrmEndpoint" }] });
    auditMock.mockResolvedValue(inSync("crmendpoint"));
    const onEndpointChange = vi.fn();

    render(<TopologyAudit endpoint={null} onEndpointChange={onEndpointChange} />);
    await userEvent.click(await screen.findByRole("button", { name: "Audit all endpoints" }));
    const summary = await screen.findByRole("table", { name: "Catalog drift by endpoint" });
    await userEvent.click(within(summary).getByRole("button", { name: "CrmEndpoint" }));

    expect(onEndpointChange).toHaveBeenCalledWith("CrmEndpoint");
    expect(screen.queryByRole("heading", { name: "crmendpoint" })).toBeNull();
  });
});

describe("TopologyAudit in the workspace", () => {
  it("lists the audited endpoint as the table's one selected row and reports the drift count", async () => {
    configMock.mockResolvedValue({ endpoints: [{ name: "CrmEndpoint" }] });
    auditMock.mockResolvedValue(inSync("crmendpoint"));
    const onDriftCount = vi.fn();

    render(<TopologyAudit endpoint="CrmEndpoint" onDriftCount={onDriftCount} />);

    const summary = await screen.findByRole("table", { name: "Catalog drift by endpoint" });
    expect(within(summary).getByRole("button", { name: "CrmEndpoint" }).getAttribute("aria-pressed")).toBe("true");
    const inspector = screen.getByRole("complementary", { name: "Inspector" });
    expect(within(inspector).getByText(/match the declared catalog/)).toBeTruthy();
    // One endpoint's audit says nothing about the others.
    expect(onDriftCount).toHaveBeenLastCalledWith(undefined);
  });

  it("reports how many endpoints drift once all are audited", async () => {
    configMock.mockResolvedValue({ endpoints: [{ name: "CrmEndpoint" }] });
    auditMock.mockResolvedValue(inSync("crmendpoint"));
    const onDriftCount = vi.fn();

    render(<TopologyAudit endpoint={null} onDriftCount={onDriftCount} />);
    expect(screen.getByText("Audit the endpoints, then select one to see its subscriptions and rules.")).toBeTruthy();
    await userEvent.click(await screen.findByRole("button", { name: "Audit all endpoints" }));

    await screen.findByRole("table", { name: "Catalog drift by endpoint" });
    expect(onDriftCount).toHaveBeenLastCalledWith(0);
  });
});
