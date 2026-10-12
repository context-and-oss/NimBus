import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { act, cleanup, renderHook, waitFor } from "@testing-library/react";
import { useSettingsStatus } from "./use-settings-status";

const mocks = vi.hoisted(() => ({
  getAdminSimulation: vi.fn(),
  getAdminAuditSettings: vi.fn(),
  getAdminHeartbeatSettings: vi.fn(),
  getAdminHeartbeatOverview: vi.fn(),
  postAdminHeartbeatSend: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getAdminSimulation = mocks.getAdminSimulation;
    getAdminAuditSettings = mocks.getAdminAuditSettings;
    getAdminHeartbeatSettings = mocks.getAdminHeartbeatSettings;
    getAdminHeartbeatOverview = mocks.getAdminHeartbeatOverview;
    postAdminHeartbeatSend = mocks.postAdminHeartbeatSend;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

const originalFetch = globalThis.fetch;
const mcpState = { saved: { enabled: true }, policyLoaded: true, deployment: { mode: "entra" }, csrfToken: "csrf-test" };
const fiState = { active: { enabled: false }, savedApiKey: "none" };
const agentKeysState = { keys: [{ keyId: "abcdefghijkl", status: "active" }], csrfToken: "csrf-test" };

function routedFetch(overrides: Record<string, () => Response> = {}) {
  const fetchMock = vi.fn((url: string) => {
    const route = Object.keys(overrides).find((u) => url.startsWith(u));
    if (route) return Promise.resolve(overrides[route]());
    if (url.startsWith("/api/admin/mcp/settings")) return Promise.resolve(Response.json(mcpState));
    if (url.startsWith("/api/admin/mcp/activity")) return Promise.resolve(Response.json({ actions: 3, refused: 1 }));
    if (url.startsWith("/api/admin/failure-intelligence")) return Promise.resolve(Response.json(fiState));
    if (url.startsWith("/api/admin/mcp/turn-off")) return Promise.resolve(Response.json(mcpState));
    if (url.startsWith("/api/admin/agent-keys")) return Promise.resolve(Response.json(agentKeysState));
    return Promise.resolve(new Response(null, { status: 404 }));
  });
  globalThis.fetch = fetchMock as unknown as typeof fetch;
  return fetchMock;
}

beforeEach(() => {
  mocks.getAdminSimulation.mockReset().mockResolvedValue({ allowed: true, enabled: false });
  mocks.getAdminAuditSettings.mockReset().mockResolvedValue({ configurableAuditTypes: [], disabledAuditTypes: [] });
  mocks.getAdminHeartbeatSettings.mockReset().mockResolvedValue({ enabled: true, intervalSeconds: 300 });
  mocks.getAdminHeartbeatOverview.mockReset().mockResolvedValue([]);
  mocks.postAdminHeartbeatSend.mockReset().mockResolvedValue({ count: 4 });
});

afterEach(() => {
  cleanup();
  globalThis.fetch = originalFetch;
});

describe("useSettingsStatus", () => {
  it("loads every feature's state", async () => {
    routedFetch();
    const { result } = renderHook(() => useSettingsStatus());

    await waitFor(() => expect(result.current.snapshot.heartbeat).toBeDefined());
    await waitFor(() => expect(result.current.snapshot.mcp).toBeDefined());
    expect(result.current.snapshot.mcp).toMatchObject({ state: mcpState, activity: { actions: 3 } });
    expect(result.current.snapshot.fi).toMatchObject(fiState);
    expect(result.current.snapshot.simulation).toMatchObject({ allowed: true });
    expect(result.current.snapshot.audit).toBeDefined();
    await waitFor(() => expect(result.current.snapshot.agentKeys).toMatchObject(agentKeysState));
  });

  it("marks only the feature whose API failed", async () => {
    routedFetch({ "/api/admin/failure-intelligence": () => new Response(null, { status: 500 }) });
    mocks.getAdminAuditSettings.mockRejectedValue(new Error("Forbidden"));
    const { result } = renderHook(() => useSettingsStatus());

    await waitFor(() => expect(result.current.snapshot.fi).toBe("error"));
    await waitFor(() => expect(result.current.snapshot.audit).toBe("error"));
    await waitFor(() => expect(result.current.snapshot.mcp).toMatchObject({ state: mcpState }));
  });

  it("turns MCP off with the loaded CSRF token, then re-reads", async () => {
    const fetchMock = routedFetch();
    const { result } = renderHook(() => useSettingsStatus());
    await waitFor(() => expect(result.current.snapshot.mcp).toBeDefined());

    await act(() => result.current.turnOffMcp());

    const call = fetchMock.mock.calls.find(([url]) => url === "/api/admin/mcp/turn-off");
    expect(call?.[1]).toMatchObject({ method: "POST", headers: { "X-NimBus-CSRF": "csrf-test" } });
    await waitFor(() =>
      expect(fetchMock.mock.calls.filter(([url]) => url === "/api/admin/mcp/settings")).toHaveLength(2),
    );
  });

  it("sends a heartbeat and reports how many endpoints it reached", async () => {
    routedFetch();
    const { result } = renderHook(() => useSettingsStatus());

    let count = 0;
    await act(async () => {
      count = await result.current.sendHeartbeat();
    });

    expect(count).toBe(4);
  });
});
