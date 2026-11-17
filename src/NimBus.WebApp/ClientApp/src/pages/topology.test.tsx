import { cleanup, fireEvent, render, screen, within } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import * as api from "api-client";
import Topology from "./topology";

const { storageProviderMock, getAdminServicebusTopics } = vi.hoisted(() => ({
  storageProviderMock: vi.fn(),
  getAdminServicebusTopics: vi.fn(),
}));

vi.mock("hooks/app-status", () => ({
  useStorageProvider: storageProviderMock,
}));
vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getAdminServicebusTopics = getAdminServicebusTopics;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});
// The stand-ins show the topic the page hands them and can ask for another.
vi.mock("components/admin/subscription-manager", () => ({
  default: ({ topic, onTopicChange }: { topic?: string | null; onTopicChange?: (t: string | null) => void }) => (
    <div>
      Subscription manager for {topic ?? "all topics"}
      <button type="button" onClick={() => onTopicChange?.("CrmEndpoint")}>
        Open CrmEndpoint from the table
      </button>
    </div>
  ),
}));
vi.mock("components/admin/topology-audit", () => ({
  default: ({ endpoint }: { endpoint?: string | null }) => (
    <div>Topology audit for {endpoint ?? "all endpoints"}</div>
  ),
}));
vi.mock("components/admin/asyncapi-export", () => ({
  default: () => <div>AsyncAPI export</div>,
}));
vi.mock("components/admin/cosmos-container-manager", () => ({
  default: () => <div>Cosmos container manager</div>,
}));

function Location() {
  const { pathname, search } = useLocation();
  return <output aria-label="location">{pathname + search}</output>;
}

const TOPICS = [
  new api.ServiceBusTopicOverview({ name: "ErpEndpoint", status: "Active", isKnownToPlatform: true }),
  new api.ServiceBusTopicOverview({ name: "CrmEndpoint", status: "Disabled", isKnownToPlatform: true }),
  new api.ServiceBusTopicOverview({ name: "legacy-bridge", status: "Active", isKnownToPlatform: false }),
];

beforeEach(() => {
  getAdminServicebusTopics.mockResolvedValue(TOPICS);
});

function renderAt(path: string) {
  return render(
    <MemoryRouter initialEntries={[path]}>
      <Routes>
        <Route path="/Topology/:view?" element={<Topology />} />
      </Routes>
      <Location />
    </MemoryRouter>,
  );
}

const location = () => screen.getByLabelText("location").textContent;

afterEach(() => {
  cleanup();
  vi.resetAllMocks();
});

describe("Topology page", () => {
  it("opens on Subscriptions and offers the AsyncAPI export in the header", () => {
    storageProviderMock.mockReturnValue("SQL Server");
    renderAt("/Topology");

    expect(screen.getByRole("heading", { name: "Topology" })).toBeTruthy();
    expect(
      screen.getByRole("tab", { name: "Subscriptions" }).getAttribute("aria-selected"),
    ).toBe("true");
    expect(screen.getByText(/Subscription manager/)).toBeTruthy();
    expect(screen.getByText("AsyncAPI export")).toBeTruthy();
  });

  it("deep-links to Catalog drift", () => {
    storageProviderMock.mockReturnValue("SQL Server");
    renderAt("/Topology/drift");

    expect(
      screen.getByRole("tab", { name: "Catalog drift" }).getAttribute("aria-selected"),
    ).toBe("true");
    expect(screen.getByText(/Topology audit/)).toBeTruthy();
    expect(screen.queryByText(/Subscription manager/)).toBeNull();
  });

  it("gives each tab its own URL", () => {
    storageProviderMock.mockReturnValue("SQL Server");
    renderAt("/Topology");

    fireEvent.click(screen.getByRole("tab", { name: "Catalog drift" }));

    expect(location()).toBe("/Topology/drift");
    expect(screen.getByText(/Topology audit/)).toBeTruthy();
  });

  it("offers Storage for Cosmos DB", () => {
    storageProviderMock.mockReturnValue("Cosmos DB");
    renderAt("/Topology/storage");

    expect(
      screen.getByRole("tab", { name: "Storage" }).getAttribute("aria-selected"),
    ).toBe("true");
    expect(screen.getByText("Cosmos container manager")).toBeTruthy();
  });

  it("has no Storage view for SQL Server and sends its URL back to Subscriptions", () => {
    storageProviderMock.mockReturnValue("SQL Server");
    renderAt("/Topology/storage");

    expect(screen.queryByRole("tab", { name: "Storage" })).toBeNull();
    expect(screen.queryByText("Cosmos container manager")).toBeNull();
    expect(location()).toBe("/Topology");
  });

  it("waits for the storage provider before judging a Storage deep link", () => {
    storageProviderMock.mockReturnValue(undefined);
    renderAt("/Topology/storage");

    expect(location()).toBe("/Topology/storage");
  });

  it("sends an unknown view back to Subscriptions", () => {
    storageProviderMock.mockReturnValue("SQL Server");
    renderAt("/Topology/nope");

    expect(location()).toBe("/Topology");
  });
});

describe("Topology split view", () => {
  beforeEach(() => storageProviderMock.mockReturnValue("SQL Server"));

  const topicsList = () => screen.getByRole("navigation", { name: "Topics" });

  it("lists the topics by name beside all topics, with what needs attention", async () => {
    renderAt("/Topology");

    const list = topicsList();
    await within(list).findByRole("link", { name: /ErpEndpoint/ });
    const names = within(list)
      .getAllByRole("link")
      .map((l) => l.textContent);
    expect(names[0]).toBe("All topics");
    expect(names.slice(1)).toEqual(["CrmEndpointDisabled", "ErpEndpoint", "legacy-bridgenot in platform"]);
    expect(within(list).getByRole("link", { name: "All topics" }).getAttribute("aria-current")).toBe("page");
    expect(screen.getByText("Subscription manager for all topics")).toBeTruthy();
  });

  it("opens a topic from the list and hands it to the view", async () => {
    renderAt("/Topology");

    fireEvent.click(await within(topicsList()).findByRole("link", { name: /ErpEndpoint/ }));

    expect(location()).toBe("/Topology?topic=ErpEndpoint");
    expect(screen.getByRole("region", { name: "ErpEndpoint" })).toBeTruthy();
    expect(screen.getByText("Subscription manager for ErpEndpoint")).toBeTruthy();
    expect(within(topicsList()).getByRole("link", { name: /ErpEndpoint/ }).getAttribute("aria-current")).toBe("page");
  });

  it("keeps the topic when switching to Catalog drift", async () => {
    renderAt("/Topology?topic=ErpEndpoint");

    fireEvent.click(screen.getByRole("tab", { name: "Catalog drift" }));

    expect(location()).toBe("/Topology/drift?topic=ErpEndpoint");
    expect(screen.getByText("Topology audit for ErpEndpoint")).toBeTruthy();
  });

  it("keeps the view when switching topics", async () => {
    renderAt("/Topology/drift?topic=ErpEndpoint");

    fireEvent.click(await within(topicsList()).findByRole("link", { name: /CrmEndpoint/ }));

    expect(location()).toBe("/Topology/drift?topic=CrmEndpoint");
    expect(screen.getByText("Topology audit for CrmEndpoint")).toBeTruthy();
  });

  it("follows a topic opened from inside the view", () => {
    renderAt("/Topology");

    fireEvent.click(screen.getByRole("button", { name: "Open CrmEndpoint from the table" }));

    expect(location()).toBe("/Topology?topic=CrmEndpoint");
  });

  it("still shows the views when the topics can't be listed", async () => {
    getAdminServicebusTopics.mockRejectedValue(new Error("403"));
    renderAt("/Topology");

    expect(await within(topicsList()).findByText("No topics found.")).toBeTruthy();
    expect(screen.getByText("Subscription manager for all topics")).toBeTruthy();
  });
});
