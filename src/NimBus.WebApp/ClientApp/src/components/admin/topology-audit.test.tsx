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
      within(rows[0]).getByRole("button", { name: "View" }),
    );
    expect(await screen.findByText("Topic: erpendpoint")).toBeTruthy();
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
