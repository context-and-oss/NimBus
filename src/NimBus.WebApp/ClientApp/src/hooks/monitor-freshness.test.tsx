import { act, cleanup, render, renderHook, screen } from "@testing-library/react";
import moment from "moment";
import { afterEach, beforeEach, expect, it, vi } from "vitest";
import Monitor from "../pages/monitor";
import { useMonitorData } from "./use-monitor-data";
import { useMonitorMetrics, HISTORY_REFRESH_MS, METRICS_REFRESH_MS } from "./use-monitor-metrics";

const mock = vi.hoisted(() => ({ statusStale: false, metricsFail: false, historyFail: false, metricsStale: false }));
const generatedAt = moment.utc("2026-11-10T12:00:00Z");
vi.mock("hooks/use-access", () => ({ useAccess: () => ({ access: null }), canContributeTo: () => false }));
vi.mock("lib/monitor-access", () => ({
  readDashboardToken: () => "wall-token",
  feedAccessError: () => undefined,
  createMonitorClient: () => ({
    getMonitorFeedStatus: async () => ({ stale: mock.statusStale, endpoints: [], generatedAtUtc: generatedAt }),
    getMonitorFeedMetrics: async () => {
      if (mock.metricsFail) throw new Error("metrics failed");
      return { stale: mock.metricsStale, generatedAtUtc: generatedAt, fleet: { handled: 42 } };
    },
    getMonitorFeedHistory: async () => {
      if (mock.historyFail) throw new Error("history failed");
      return { stale: false, generatedAtUtc: generatedAt };
    },
  }),
}));

beforeEach(() => {
  vi.useFakeTimers();
  vi.setSystemTime(new Date("2026-11-10T12:02:00Z"));
  Object.defineProperty(document, "hidden", { configurable: true, value: false });
});
afterEach(() => {
  cleanup();
  vi.useRealTimers();
  Object.assign(mock, { statusStale: false, metricsFail: false, historyFail: false, metricsStale: false });
});
const tick = async (ms = 0) => { await act(async () => { await vi.advanceTimersByTimeAsync(ms); }); };

it("keeps server-stale status stale across polls and clears it on recovery", async () => {
  mock.statusStale = true;
  const { result } = renderHook(() => useMonitorData());
  await tick();
  expect(result.current.isStale).toBe(true);
  expect(result.current.lastRefreshAt).toBe(generatedAt.valueOf());
  await tick(5_000);
  expect(result.current.isStale).toBe(true);
  expect(result.current.lastRefreshAt).toBe(generatedAt.valueOf());
  mock.statusStale = false;
  await tick(5_000);
  expect(result.current.isStale).toBe(false);
});

it("marks failed feeds independently, retains their data and timestamps, and recovers", async () => {
  const { result } = renderHook(() => useMonitorMetrics());
  await tick();
  mock.metricsFail = true;
  await tick(METRICS_REFRESH_MS);
  expect(result.current.metrics?.stale).toBe(true);
  expect(result.current.metrics?.fleet?.handled).toBe(42);
  expect(result.current.metrics?.generatedAtUtc?.valueOf()).toBe(generatedAt.valueOf());
  expect(result.current.history?.stale).toBe(false);
  mock.metricsFail = false;
  mock.historyFail = true;
  await tick(HISTORY_REFRESH_MS);
  expect(result.current.metrics?.stale).toBe(false);
  expect(result.current.history?.stale).toBe(true);
  expect(result.current.history?.generatedAtUtc?.valueOf()).toBe(generatedAt.valueOf());
  mock.historyFail = false;
  await tick(HISTORY_REFRESH_MS);
  expect(result.current.history?.stale).toBe(false);
});

it("shows snapshot age for server-stale status and metrics even while HTTP requests succeed", async () => {
  mock.statusStale = true;
  mock.metricsStale = true;
  render(<Monitor />);
  await tick();
  expect(screen.getByText("Connection lost")).toBeTruthy();
  expect(screen.getByText("Metrics are stale")).toBeTruthy();
  expect(screen.getAllByText(/120s ago/)).toHaveLength(2);
});

it("warns about failed metrics and history polls while status remains fresh", async () => {
  render(<Monitor />);
  await tick();
  mock.metricsFail = true;
  mock.historyFail = true;
  await tick(HISTORY_REFRESH_MS);
  expect(screen.getByText("Metrics are stale")).toBeTruthy();
  expect(screen.getByText("History is stale")).toBeTruthy();
  expect(screen.queryByText("Connection lost")).toBeNull();
});
