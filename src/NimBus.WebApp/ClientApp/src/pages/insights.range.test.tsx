import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import {
  cleanup,
  fireEvent,
  render,
  screen,
  waitFor,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import type { Moment } from "moment";
import * as api from "api-client";
import Insights from "./insights";

const mocks = vi.hoisted(() => ({ insights: vi.fn(), endpoints: vi.fn() }));

vi.mock("api-client", async () => {
  const actual =
    await vi.importActual<typeof import("api-client")>("api-client");
  class FakeClient {
    getMetricsFailedInsights = mocks.insights;
    getEndpointStatusCountAll = mocks.endpoints;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

// recharts needs layout jsdom doesn't have; the chart isn't under test here.
vi.mock("recharts", () => {
  const Pass = ({ children }: { children?: ReactNode }) => <>{children}</>;
  const Nothing = () => null;
  return {
    Bar: Nothing,
    BarChart: Pass,
    CartesianGrid: Nothing,
    ResponsiveContainer: Pass,
    Tooltip: Nothing,
    XAxis: Nothing,
    YAxis: Nothing,
  };
});

afterEach(() => {
  cleanup();
  vi.clearAllMocks();
});

describe("Insights page time range", () => {
  it("loads insights for a custom range, and a preset returns to the period", async () => {
    mocks.insights.mockResolvedValue(
      new api.FailedInsightsOverview({ groups: [], totalFailed: 0 }),
    );
    mocks.endpoints.mockResolvedValue([]);
    render(
      <MemoryRouter>
        <Insights />
      </MemoryRouter>,
    );
    await waitFor(() => expect(mocks.insights).toHaveBeenCalledTimes(1));
    // Spec 043: no endpoint filter, and the first load reads saved classifications afresh.
    expect(mocks.insights).toHaveBeenLastCalledWith(
      api.Period._1d,
      undefined,
      undefined,
      undefined,
      true,
    );

    fireEvent.click(await screen.findByRole("button", { name: "Custom…" }));
    fireEvent.change(screen.getByLabelText("Start time (local)"), {
      target: { value: "2026-09-20T08:00" },
    });
    fireEvent.change(screen.getByLabelText("End time (local)"), {
      target: { value: "2026-09-21T17:30" },
    });
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    await waitFor(() => expect(mocks.insights).toHaveBeenCalledTimes(2));
    const [, from, to] = mocks.insights.mock.lastCall as [
      api.Period,
      Moment,
      Moment,
    ];
    expect([from.toISOString(), to.toISOString()]).toEqual([
      new Date(2026, 8, 20, 8, 0).toISOString(),
      new Date(2026, 8, 21, 17, 30).toISOString(),
    ]);
    expect(
      (
        await screen.findByRole("button", { name: "20/09 08:00–21/09 17:30" })
      ).getAttribute("aria-pressed"),
    ).toBe("true");

    fireEvent.click(screen.getByRole("button", { name: "7d" }));
    await waitFor(() => expect(mocks.insights).toHaveBeenCalledTimes(3));
    expect(mocks.insights).toHaveBeenLastCalledWith(
      api.Period._7d,
      undefined,
      undefined,
      undefined,
      undefined,
    );
  });
});
