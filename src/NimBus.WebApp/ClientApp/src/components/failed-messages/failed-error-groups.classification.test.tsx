import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import moment from "moment";
import * as api from "api-client";
import { ToastProvider } from "components/ui";
import FailedErrorGroupsView, {
  type FailedGrouping,
} from "./failed-error-groups";

const at = moment("2026-11-24T10:00:00Z");

const ref = (eventId: string, classified: boolean) =>
  new api.FailedEventRef({
    eventId,
    lastMessageId: `m-${eventId}`,
    endpointId: "Crm",
    sessionId: `s-${eventId}`,
    eventTypeId: "OrderPlaced",
    resolutionStatus: "Failed",
    updatedAt: at,
    errorText: `HttpRequestException: 503 for ${eventId}`,
    analysisState: classified
      ? api.InsightAnalysisState.Classified
      : api.InsightAnalysisState.NotAnalyzed,
    category: classified ? "transient_dependency" : undefined,
    guidance: classified ? "RetryMayHelp" : undefined,
  });

const coverage = (classified: number, notAnalyzed: number) =>
  new api.InsightsCoverage({
    total: classified + notAnalyzed,
    analyzed: classified,
    classified,
    uncertain: 0,
    notAnalyzed,
    notEligible: 0,
  });

const group = (
  groupKey: string,
  label: string,
  events: api.FailedEventRef[],
  groupCoverage: api.InsightsCoverage,
) =>
  new api.FailedErrorGroup({
    groupKey,
    label,
    errorCategory: label,
    count: events.length,
    coverage: groupCoverage,
    endpoints: ["Crm"],
    eventTypes: ["OrderPlaced"],
    latestOccurrence: at,
    exampleErrorText: "HttpRequestException: 503",
    subGroups: [
      new api.FailedErrorSubGroup({
        normalizedPattern: "HttpRequestException: 503 for <value>",
        count: events.length,
        coverage: groupCoverage,
        endpoints: ["Crm"],
        eventTypes: ["OrderPlaced"],
        latestOccurrence: at,
        exampleErrorText: "HttpRequestException: 503",
        events,
      }),
    ],
  });

const byCause = (overrides: Partial<api.IFailedErrorGroups> = {}) =>
  new api.FailedErrorGroups({
    total: 3,
    truncated: false,
    enrichment: api.InsightsEnrichmentStatus.Available,
    analysisAvailable: true,
    coverage: coverage(2, 1),
    groups: [
      group(
        "transient_dependency",
        "Transient dependency",
        [ref("aaaa1111", true), ref("bbbb2222", true)],
        coverage(2, 0),
      ),
      group(
        "notAnalyzed",
        "Not analyzed",
        [ref("cccc3333", false)],
        coverage(0, 1),
      ),
    ],
    ...overrides,
  });

const renderView = (
  groups: api.FailedErrorGroups,
  grouping: FailedGrouping,
  onAct = vi.fn(),
) => {
  render(
    <ToastProvider>
      <MemoryRouter>
        <FailedErrorGroupsView
          groups={groups}
          isLoading={false}
          grouping={grouping}
          onAct={onAct}
        />
      </MemoryRouter>
    </ToastProvider>,
  );
  return onAct;
};

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("FailedErrorGroupsView grouped by saved classification", () => {
  it("titles groups by their label and shows coverage overall and per group", () => {
    renderView(byCause(), "cause");

    expect(
      screen.getByRole("columnheader", { name: "Likely cause" }),
    ).toBeTruthy();
    expect(screen.getByText("Transient dependency")).toBeTruthy();
    expect(screen.getByText("Not analyzed")).toBeTruthy();
    expect(screen.getByText("2 of 3 analyzed (67%)")).toBeTruthy();
    // Overall, then one per group; patterns show theirs once expanded.
    expect(screen.getAllByRole("img", { name: /^Coverage:/ })).toHaveLength(3);
  });

  it("expands a group to its failures with each one's cause and guidance", () => {
    renderView(byCause(), "cause");

    fireEvent.click(screen.getByText("Transient dependency"));

    expect(screen.getByText("aaaa1111…")).toBeTruthy();
    expect(
      screen.getAllByText("Transient dependency · Retry may help"),
    ).toHaveLength(2);
  });

  it("resubmits a whole group, saying that saved results are not current instructions", () => {
    const confirm = vi.spyOn(window, "confirm").mockReturnValue(true);
    const onAct = renderView(byCause(), "guidance");

    fireEvent.click(screen.getByRole("button", { name: "Resubmit 2" }));

    expect(confirm.mock.calls[0][0]).toContain(
      "Saved results describe each failure when it was analyzed.",
    );
    const [action, events] = onAct.mock.calls[0];
    expect(action).toBe("Resubmit");
    expect(events.map((e: api.FailedEventRef) => e.eventId)).toEqual([
      "aaaa1111",
      "bbbb2222",
    ]);
  });

  it("says when agents can't record new classifications", () => {
    renderView(byCause({ analysisAvailable: false }), "cause");

    expect(
      screen.getByText(/Agents can't record new classifications/),
    ).toBeTruthy();
  });

  it("shows error groups and the reason when saved classifications can't be read", () => {
    renderView(
      new api.FailedErrorGroups({
        total: 1,
        truncated: false,
        enrichment: api.InsightsEnrichmentStatus.Unavailable,
        groups: [
          new api.FailedErrorGroup({
            groupKey: "HttpRequestException",
            label: "HttpRequestException",
            errorCategory: "HttpRequestException",
            count: 1,
            subGroups: [],
          }),
        ],
      }),
      "cause",
    );

    expect(screen.getByRole("columnheader", { name: "Error" })).toBeTruthy();
    expect(screen.getByRole("status").textContent).toContain(
      "Saved failure classifications can't be read right now.",
    );
    expect(screen.queryByRole("img", { name: /^Coverage:/ })).toBeNull();
  });

  it("stays quiet when failure intelligence is off", () => {
    renderView(
      new api.FailedErrorGroups({
        total: 0,
        truncated: false,
        enrichment: api.InsightsEnrichmentStatus.Disabled,
        groups: [],
      }),
      "error",
    );

    expect(screen.queryByRole("status")).toBeNull();
  });
});
