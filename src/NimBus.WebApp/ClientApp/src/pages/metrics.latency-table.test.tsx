import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import * as api from "api-client";
import Metrics from "./metrics";

const mocks = vi.hoisted(() => ({
  overview: vi.fn(),
  latency: vi.fn(),
  timeseries: vi.fn(),
  byEventType: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual =
    await vi.importActual<typeof import("api-client")>("api-client");
  class FakeClient {
    getMetricsOverview = mocks.overview;
    getMetricsLatency = mocks.latency;
    getMetricsTimeseries = mocks.timeseries;
    getMetricsTimeseriesByEventtype = mocks.byEventType;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

// recharts needs layout jsdom doesn't have; the charts aren't under test here.
vi.mock("recharts", () => {
  const Pass = ({ children }: { children?: ReactNode }) => <>{children}</>;
  const Nothing = () => null;
  return {
    Bar: Nothing,
    BarChart: Pass,
    CartesianGrid: Nothing,
    Legend: Nothing,
    Line: Nothing,
    LineChart: Pass,
    ResponsiveContainer: Pass,
    Tooltip: Nothing,
    XAxis: Nothing,
    YAxis: Nothing,
  };
});

vi.mock("components/metrics/by-event-type-tab", async () => ({
  ...(await vi.importActual<
    typeof import("components/metrics/by-event-type-tab")
  >("components/metrics/by-event-type-tab")),
  default: () => null,
}));

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

const stats = (count: number, avgMs: number, maxMs: number) =>
  new api.LatencyStats({ count, avgMs, minMs: 0, maxMs });

const route = (
  endpointId: string,
  eventTypeId: string,
  queue: api.LatencyStats,
  processing: api.LatencyStats,
) => new api.EndpointLatency({ endpointId, eventTypeId, queue, processing });

describe("Metrics latency by route", () => {
  it("ranks routes by total time (count × avg), not by max processing time", async () => {
    mocks.overview.mockResolvedValue(new api.MetricsOverview());
    mocks.timeseries.mockResolvedValue(null);
    mocks.byEventType.mockResolvedValue(null);
    mocks.latency.mockResolvedValue(
      new api.LatencyOverview({
        latencies: [
          // Slow handler, few messages: max 37 s, total 374 × 6.6 s ≈ 41 min.
          route(
            "CrmEndpoint",
            "ProspectUpdated",
            stats(374, 1_000, 9_000),
            stats(374, 5_600, 37_000),
          ),
          // Fast handler, huge backlog: max 18 s, total 12,738 × 129 s ≈ 457 h.
          route(
            "Nav09Endpoint",
            "AccountUpdated",
            stats(12_738, 129_000, 2_280_000),
            stats(12_738, 40, 18_000),
          ),
          // Tiny route: total 2 × 13 s = 26 s.
          route(
            "CrmEndpoint",
            "SurveyCreated",
            stats(2, 0, 0),
            stats(2, 13_000, 15_000),
          ),
        ],
      }),
    );

    render(
      <MemoryRouter>
        <Metrics />
      </MemoryRouter>,
    );

    const table = await screen.findByRole("table");
    const rows = within(table).getAllByRole("row").slice(1);
    expect(
      rows.map((r) => within(r).getAllByRole("cell")[1].textContent),
    ).toEqual(["AccountUpdated", "ProspectUpdated", "SurveyCreated"]);
    expect(screen.getByText(/sorted by total time/)).toBeTruthy();
    // The total-time column shows count × avg, so the ranking is visible.
    expect(within(rows[0]).getByText("457 h")).toBeTruthy();
    expect(within(rows[1]).getByText("41 min")).toBeTruthy();
    expect(within(rows[2]).getByText("26 s")).toBeTruthy();
  });

  it("shows the route max as the longer of queue and processing max, and flags queue outliers", async () => {
    mocks.overview.mockResolvedValue(new api.MetricsOverview());
    mocks.timeseries.mockResolvedValue(null);
    mocks.byEventType.mockResolvedValue(null);
    mocks.latency.mockResolvedValue(
      new api.LatencyOverview({
        latencies: [
          // Handler is fast (max 400 ms) but one message waited 38 min.
          route(
            "Nav09Endpoint",
            "AccountUpdated",
            stats(12_738, 129_000, 2_280_000),
            stats(12_738, 40, 400),
          ),
          // Both phases fast: not an outlier.
          route(
            "Nav09Endpoint",
            "ContactCreated",
            stats(119, 130, 600),
            stats(119, 180, 875),
          ),
        ],
      }),
    );

    render(
      <MemoryRouter>
        <Metrics />
      </MemoryRouter>,
    );

    const table = await screen.findByRole("table");
    const [, slowQueue, fast] = within(table).getAllByRole("row");
    const maxCell = (row: HTMLElement) => within(row).getAllByRole("cell")[5];

    expect(maxCell(slowQueue).textContent).toContain("38 min");
    expect(maxCell(slowQueue).textContent).toContain("queue");
    expect(within(maxCell(slowQueue)).getByText("outlier")).toBeTruthy();

    expect(maxCell(fast).textContent).toContain("875 ms");
    expect(within(maxCell(fast)).queryByText("outlier")).toBeNull();

    // KPI tiles agree with the table.
    expect(screen.getByText("1 outlier route")).toBeTruthy();
    expect(screen.getAllByText("38 min").length).toBeGreaterThanOrEqual(2);
  });
});
