import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import moment from "moment";
import * as api from "api-client";
import ErrorGroupedView from "./error-grouped-view";
import FlowTimeline from "components/event-details/flow-timeline";
import { formatMoment } from "functions/endpoint.functions";

vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast: vi.fn() }),
}));
afterEach(cleanup);

describe("error grouping and timestamps", () => {
  it("groups only failures, including failures without error text", () => {
    const events = [
      "Failed",
      "DeadLettered",
      "Unsupported",
      "Completed",
      "Pending",
      "Deferred",
      "Skipped",
    ].map(
      (status) =>
        new api.Event({
          eventId: status,
          resolutionStatus: status as api.ResolutionStatus,
        }),
    );
    render(
      <MemoryRouter>
        <ErrorGroupedView
          events={events}
          onResubmitEvent={vi.fn()}
          onSkipEvent={vi.fn()}
          isActionableStatus={() => true}
        />
      </MemoryRouter>,
    );
    expect(screen.getByText("3 events in 1 error groups")).toBeTruthy();
    fireEvent.click(screen.getByText("Unknown"));
    expect(screen.queryByText("Completed")).toBeNull();
    expect(screen.queryByText("Pending")).toBeNull();
    expect(screen.getAllByRole("link")).toHaveLength(3);
  });

  it("uses local time in Flow for both messages and audits", () => {
    const ts = moment.parseZone("2026-09-29T22:53:00+02:00");
    render(
      <FlowTimeline
        messages={[
          new api.Message({
            messageType: api.MessageType.EventRequest,
            enqueuedTimeUtc: ts,
          }),
        ]}
        audits={[
          new api.MessageAudit({ auditTimestamp: ts, auditType: "comment" }),
        ]}
      />,
    );
    expect(screen.getAllByText(formatMoment(ts))).toHaveLength(2);
    expect(screen.getByText(/Times shown in local time/)).toBeTruthy();
  });

  it("formats UTC Moment values in local time without mutating them", () => {
    const ts = moment.utc("2026-09-29T20:53:00Z");
    expect(formatMoment(ts)).toBe(
      ts.clone().local().format("DD/MM/YYYY HH:mm:ss.SSS"),
    );
    expect(ts.isUTC()).toBe(true);
  });
});
