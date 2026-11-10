import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen, waitFor, within } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import moment from "moment";
import * as api from "api-client";
import Insights from "./insights";

const mocks = vi.hoisted(() => ({
  insights: vi.fn(),
  occurrences: vi.fn(),
  patterns: vi.fn(),
  endpoints: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual = await vi.importActual<typeof import("api-client")>("api-client");
  class FakeClient {
    getMetricsFailedInsights = mocks.insights;
    getMetricsFailedInsightsOccurrences = mocks.occurrences;
    getMetricsFailedInsightsPatterns = mocks.patterns;
    getEndpointStatusCountAll = mocks.endpoints;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

// recharts needs layout jsdom doesn't have; the chart isn't under test here.
vi.mock("recharts", () => {
  const Pass = ({ children }: { children?: ReactNode }) => <>{children}</>;
  const Nothing = () => null;
  return { Bar: Nothing, BarChart: Pass, CartesianGrid: Nothing, ResponsiveContainer: Pass, Tooltip: Nothing, XAxis: Nothing, YAxis: Nothing };
});

afterEach(() => {
  cleanup();
  vi.resetAllMocks();
});

const from = moment.utc("2026-11-06T12:00:00Z");
const to = moment.utc("2026-11-07T12:00:00Z");

const coverage = (classified: number, uncertain: number, notAnalyzed: number, notEligible = 0) =>
  new api.InsightsCoverage({
    total: classified + uncertain + notAnalyzed + notEligible,
    analyzed: classified + uncertain,
    classified,
    uncertain,
    notAnalyzed,
    notEligible,
  });

function overview(over: Partial<api.IFailedInsightsOverview> = {}) {
  return new api.FailedInsightsOverview({
    from,
    to,
    totalFailed: 25,
    totalOccurrences: 25,
    enrichment: api.InsightsEnrichmentStatus.Available,
    enrichmentLimit: 10000,
    occurrencesReadAt: moment.utc("2026-11-07T11:59:00Z"),
    classificationsReadAt: moment.utc("2026-11-07T11:59:05Z"),
    coverage: coverage(15, 3, 6, 1),
    groups: [
      new api.ErrorPatternGroup({
        groupKey: "ec:timeout",
        errorCategory: "TimeoutException",
        count: 25,
        endpoints: ["crm"],
        eventTypes: ["CustomerChanged"],
        exampleErrorText: "TimeoutException: calling CRM",
        coverage: coverage(15, 3, 6, 1),
        subGroups: [],
      }),
    ],
    causeGroups: [
      new api.InsightGroup({
        groupKey: "transient_dependency",
        label: "Transient dependency",
        count: 15,
        coverage: coverage(15, 0, 0),
        endpoints: ["crm"],
        eventTypes: ["CustomerChanged"],
        topPatterns: [new api.InsightGroupPattern({ groupKey: "ep:1", normalizedPattern: "Timeout calling CRM", count: 9 })],
        morePatterns: 2,
      }),
      new api.InsightGroup({ groupKey: "notAnalyzed", label: "Not analyzed", count: 10, coverage: coverage(0, 0, 10), endpoints: ["crm"], eventTypes: [], topPatterns: [], morePatterns: 0 }),
    ],
    guidanceGroups: [
      new api.InsightGroup({ groupKey: "RetryMayHelp", label: "Retry may help", count: 15, coverage: coverage(15, 0, 0), endpoints: ["crm"], eventTypes: [], topPatterns: [], morePatterns: 0 }),
      new api.InsightGroup({ groupKey: "notAnalyzed", label: "Not analyzed", count: 10, coverage: coverage(0, 0, 10), endpoints: ["crm"], eventTypes: [], topPatterns: [], morePatterns: 0 }),
    ],
    ...over,
  });
}

function renderAt(url = "/Insights") {
  mocks.endpoints.mockResolvedValue([
    new api.EndpointStatusCount({ endpointId: "crm" }),
    new api.EndpointStatusCount({ endpointId: "erp" }),
  ]);
  return render(
    <MemoryRouter initialEntries={[url]}>
      <Insights />
    </MemoryRouter>,
  );
}

describe("Insights page", () => {
  it("shows error patterns and coverage by default, and switches perspective from the loaded data", async () => {
    mocks.insights.mockResolvedValue(overview());
    renderAt();

    expect(await screen.findByText("TimeoutException")).toBeTruthy();
    expect(screen.getByText("18 of 25 analyzed (72%)")).toBeTruthy();
    expect(screen.getByText(/25 failure occurrences/)).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Likely failure causes" }));
    expect(await screen.findByText("Transient dependency")).toBeTruthy();
    expect(screen.getByRole("button", { name: "Likely failure causes" }).getAttribute("aria-pressed")).toBe("true");
    expect(mocks.insights).toHaveBeenCalledTimes(1);
  });

  it("restores the perspective and endpoint from the URL and loads that scope", async () => {
    mocks.insights.mockResolvedValue(overview());
    renderAt("/Insights?by=guidance&endpoint=crm");

    expect(await screen.findByText("Retry may help")).toBeTruthy();
    expect(mocks.insights).toHaveBeenCalledWith(api.Period._1d, undefined, undefined, "crm", true);
  });

  it("explains why likely causes are unavailable and keeps error patterns", async () => {
    mocks.insights.mockResolvedValue(
      overview({ enrichment: api.InsightsEnrichmentStatus.LimitExceeded, coverage: undefined, causeGroups: undefined, guidanceGroups: undefined }),
    );
    renderAt();

    // The thousands separator follows the machine locale.
    expect(await screen.findByText(/more than 10\D?000 failures/)).toBeTruthy();
    expect(screen.getByText("TimeoutException")).toBeTruthy();
    expect((screen.getByRole("button", { name: "Likely failure causes" }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByText(/analyzed \(/)).toBeNull();
  });

  it("shows error patterns, not an empty period, when a cause link is opened without saved classifications", async () => {
    mocks.insights.mockResolvedValue(
      overview({ enrichment: api.InsightsEnrichmentStatus.LimitExceeded, coverage: undefined, causeGroups: undefined, guidanceGroups: undefined }),
    );
    renderAt("/Insights?by=cause&group=transient_dependency");

    expect(await screen.findByText(/more than 10\D?000 failures/)).toBeTruthy();
    expect(screen.getByText("TimeoutException")).toBeTruthy();
    expect(screen.queryByText("No failure occurrences in this period.")).toBeNull();
    expect(screen.getByRole("button", { name: "Error patterns" }).getAttribute("aria-pressed")).toBe("true");
    // The cause group can't be resolved without classifications, so no drill-down is requested.
    expect(mocks.occurrences).not.toHaveBeenCalled();

    // Opening an error-pattern group from the fallback view drills into that pattern.
    mocks.occurrences.mockResolvedValue(new api.InsightOccurrencePage({ occurrences: [] }));
    fireEvent.click(screen.getByRole("button", { name: "View failures" }));
    await waitFor(() =>
      expect(mocks.occurrences).toHaveBeenCalledWith(from, to, api.InsightsPerspective.ErrorPattern, "ec:timeout", undefined, 50, undefined),
    );
  });

  it("opens a group's failures with the overview's window and pages with the continuation token", async () => {
    mocks.insights.mockResolvedValue(overview());
    mocks.occurrences
      .mockResolvedValueOnce(
        new api.InsightOccurrencePage({
          occurrences: [
            new api.InsightOccurrence({
              messageId: "m1",
              eventId: "e1",
              endpointId: "crm",
              eventTypeId: "CustomerChanged",
              failedAt: moment.utc("2026-11-07T10:00:00Z"),
              normalizedPattern: "Timeout calling CRM",
              analysisState: api.InsightAnalysisState.Classified,
              operationMarker: "analysisRunning",
              category: "transient_dependency",
              categoryConfidence: 0.82,
              guidance: "RetryMayHelp",
              currentStatus: "Completed",
            }),
          ],
          continuationToken: "t2",
        }),
      )
      .mockResolvedValueOnce(new api.InsightOccurrencePage({ occurrences: [] }));
    renderAt("/Insights?by=cause");

    const row = (await screen.findByText("Transient dependency")).closest("tr")!;
    fireEvent.click(within(row).getByRole("button", { name: "View failures" }));

    expect(await screen.findByText("Newer analysis running")).toBeTruthy();
    // Each row opens the exact failed attempt on the details page.
    expect(document.querySelector('a[href^="/Message/Index/crm/e1/0"]')?.getAttribute("href")).toBe("/Message/Index/crm/e1/0?message=m1");
    expect(mocks.occurrences).toHaveBeenLastCalledWith(from, to, api.InsightsPerspective.Cause, "transient_dependency", undefined, 50, undefined);
    expect(screen.getByText("Completed")).toBeTruthy();
    expect(screen.getByText("Retry may help", { selector: "td" })).toBeTruthy();

    fireEvent.click(screen.getByRole("button", { name: "Next page" }));
    await waitFor(() => expect(mocks.occurrences).toHaveBeenCalledTimes(2));
    expect(mocks.occurrences).toHaveBeenLastCalledWith(from, to, api.InsightsPerspective.Cause, "transient_dependency", undefined, 50, "t2");
  });

  it("opens the rest of a group's error patterns", async () => {
    mocks.insights.mockResolvedValue(overview());
    mocks.patterns.mockResolvedValue(
      new api.InsightGroupPatternPage({ patterns: [new api.InsightGroupPattern({ groupKey: "ep:2", normalizedPattern: "Connection reset", count: 3 })] }),
    );
    renderAt("/Insights?by=cause");

    fireEvent.click(await screen.findByRole("button", { name: "and 2 more" }));

    expect(await screen.findByText("Connection reset")).toBeTruthy();
    expect(mocks.patterns).toHaveBeenCalledWith(from, to, api.InsightsPerspective.Cause, "transient_dependency", undefined, 50, undefined);
  });

  it("reads fresh classifications on load and on Refresh, but not on every filter change", async () => {
    mocks.insights.mockResolvedValue(overview());
    renderAt();
    await screen.findByText("TimeoutException");
    expect(mocks.insights).toHaveBeenLastCalledWith(api.Period._1d, undefined, undefined, undefined, true);

    fireEvent.click(screen.getByRole("button", { name: "7d" }));
    await waitFor(() => expect(mocks.insights).toHaveBeenCalledTimes(2));
    expect(mocks.insights).toHaveBeenLastCalledWith(api.Period._7d, undefined, undefined, undefined, undefined);

    fireEvent.click(screen.getByRole("button", { name: "Refresh" }));
    await waitFor(() => expect(mocks.insights).toHaveBeenCalledTimes(3));
    expect(mocks.insights).toHaveBeenLastCalledWith(api.Period._7d, undefined, undefined, undefined, true);
  });

  it("ignores a response that arrives after the scope changed", async () => {
    let releaseFirst: (value: api.FailedInsightsOverview) => void = () => {};
    mocks.insights
      .mockImplementationOnce(() => new Promise((resolve) => { releaseFirst = resolve; }))
      .mockResolvedValueOnce(overview({ groups: [new api.ErrorPatternGroup({ groupKey: "ec:new", errorCategory: "NewerScope", count: 1, endpoints: [], eventTypes: [], subGroups: [] })] }));
    renderAt();

    fireEvent.click(await screen.findByRole("button", { name: "7d" }));
    expect(await screen.findByText("NewerScope")).toBeTruthy();
    releaseFirst(overview({ groups: [new api.ErrorPatternGroup({ groupKey: "ec:old", errorCategory: "OlderScope", count: 1, endpoints: [], eventTypes: [], subGroups: [] })] }));

    await waitFor(() => expect(screen.queryByText("OlderScope")).toBeNull());
    expect(screen.getByText("NewerScope")).toBeTruthy();
  });

  it("notes that Not analyzed includes failures that can no longer be analyzed", async () => {
    mocks.insights.mockResolvedValue(overview({ analysisAvailable: true }));
    renderAt("/Insights?by=cause");

    const row = (await screen.findByText("Not analyzed")).closest("tr")!;
    expect(within(row).getByText("Includes failures that can no longer be analyzed")).toBeTruthy();
    expect(screen.queryByText(/no provider is configured/)).toBeNull();
  });

  it("says when new analysis is unavailable because only saved results can be read", async () => {
    mocks.insights.mockResolvedValue(overview({ analysisAvailable: false }));
    renderAt();

    expect(await screen.findByText(/New analysis is unavailable because no provider is configured/)).toBeTruthy();
  });

  it("says so when the period has no failures", async () => {
    mocks.insights.mockResolvedValue(overview({ groups: [], totalFailed: 0, totalOccurrences: 0, coverage: coverage(0, 0, 0), causeGroups: [], guidanceGroups: [] }));
    renderAt();

    expect((await screen.findAllByText("No failure occurrences in this period.")).length).toBeGreaterThan(0);
  });
});
