import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import * as api from "api-client";
import EndpointFilterPills, {
  EMPTY_ENDPOINT_PILLS,
  statusCountsOf,
  type EndpointFilterPillsProps,
} from "./endpoint-filter-pills";

vi.mock("api-client", async () => {
  const actual =
    await vi.importActual<typeof import("api-client")>("api-client");
  class FakeClient {
    getEndpointsAll = () => Promise.resolve([]);
    getEventTypes = () => Promise.resolve([]);
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

vi.mock("hooks/event-types", () => ({
  getEventTypesByEndpoint: () =>
    Promise.resolve({
      consumes: [{ events: [{ name: "CrmAccountCreated" }] }],
    }),
}));

afterEach(cleanup);

const renderPills = (overrides: Partial<EndpointFilterPillsProps> = {}) => {
  const props: EndpointFilterPillsProps = {
    endpointId: "ep-1",
    value: EMPTY_ENDPOINT_PILLS,
    hideReported: false,
    onHideReportedChange: vi.fn(),
    onApply: vi.fn(),
    onStatusChange: vi.fn(),
    onReset: vi.fn(),
    ...overrides,
  };
  render(<EndpointFilterPills {...props} />);
  return props;
};

describe("EndpointFilterPills", () => {
  it("applies an updated preset", () => {
    const { onApply } = renderPills();

    fireEvent.click(screen.getByRole("button", { name: "Updated time range" }));
    fireEvent.click(screen.getByRole("button", { name: "Last 3 days" }));

    expect(onApply).toHaveBeenCalledWith({
      updatedPeriod: api.Period._3d,
      updatedFrom: "",
      updatedTo: "",
    });
  });

  it("toggles a status and shows its count", () => {
    const { onStatusChange } = renderPills({
      value: { ...EMPTY_ENDPOINT_PILLS, status: ["Failed"] },
      statusCounts: { Failed: 7, Deferred: 2 },
    });
    const pill = screen.getByRole("button", { name: "Status" });
    expect(pill.textContent).toContain("Failed");

    fireEvent.click(pill);
    const deferred = screen.getByRole("checkbox", { name: /Deferred/ });
    expect(deferred.closest("label")?.textContent).toContain("2");
    fireEvent.click(deferred);

    expect(onStatusChange).toHaveBeenCalledWith(["Failed", "Deferred"]);
  });

  it("selects the unresolved statuses in one click, or clears to all", () => {
    const { onStatusChange } = renderPills();

    fireEvent.click(screen.getByRole("button", { name: "Status" }));
    fireEvent.click(screen.getByRole("button", { name: "Unresolved" }));
    expect(onStatusChange).toHaveBeenLastCalledWith([
      api.ResolutionStatus.Failed,
      api.ResolutionStatus.DeadLettered,
      api.ResolutionStatus.Unsupported,
    ]);

    fireEvent.click(screen.getByRole("button", { name: "Status" }));
    fireEvent.click(screen.getByRole("button", { name: "All" }));
    expect(onStatusChange).toHaveBeenLastCalledWith([]);
  });

  it("adds payload, an added range and hide-reported through (+)", () => {
    const { onApply, onHideReportedChange } = renderPills();

    fireEvent.click(screen.getByRole("button", { name: "Add filter" }));
    fireEvent.change(screen.getByLabelText("Payload contains"), {
      target: { value: "orderId:4471" },
    });
    fireEvent.change(screen.getByLabelText("Added"), {
      target: { value: api.Period._7d },
    });
    fireEvent.click(
      screen.getByRole("checkbox", { name: "Hide reported events" }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Apply" }));

    expect(onApply).toHaveBeenCalledWith({
      payload: "orderId:4471",
      addedPeriod: api.Period._7d,
      addedFrom: "",
      addedTo: "",
    });
    expect(onHideReportedChange).toHaveBeenCalledWith(true);
  });

  it("shows set extras as removable pills", () => {
    const { onApply, onHideReportedChange } = renderPills({
      value: {
        ...EMPTY_ENDPOINT_PILLS,
        payload: "orderId:4471",
        addedFrom: "2026-11-10T08:00",
      },
      hideReported: true,
    });

    expect(
      screen.getByRole("button", { name: "Payload contains" }).textContent,
    ).toContain("orderId:4471");
    expect(
      screen.getByRole("button", { name: "Added time range" }).textContent,
    ).toContain("From 10/11/2026 08:00");

    fireEvent.click(
      screen.getByRole("button", { name: "Remove Payload contains" }),
    );
    expect(onApply).toHaveBeenCalledWith({ payload: "" });
    fireEvent.click(
      screen.getByRole("button", { name: "Remove Added time range" }),
    );
    expect(onApply).toHaveBeenCalledWith({
      addedPeriod: "",
      addedFrom: "",
      addedTo: "",
    });
    fireEvent.click(screen.getByRole("button", { name: "Remove Reported" }));
    expect(onHideReportedChange).toHaveBeenCalledWith(false);
  });

  it("offers Reset only when something is filtered", () => {
    renderPills();
    expect(screen.queryByRole("button", { name: "Reset filters" })).toBeNull();
    cleanup();

    const { onReset } = renderPills({ hideReported: true });
    fireEvent.click(screen.getByRole("button", { name: "Reset filters" }));
    expect(onReset).toHaveBeenCalled();
  });
});

describe("statusCountsOf", () => {
  it("splits the API's failed total, which includes dead-lettered and unsupported", () => {
    expect(
      statusCountsOf(
        Object.assign(new api.EndpointStatusCount(), {
          failedCount: 13,
          deadletterCount: 4,
          unsupportedCount: 2,
          deferredCount: 3,
          pendingCount: 17,
        }),
      ),
    ).toEqual({
      Failed: 7,
      DeadLettered: 4,
      Unsupported: 2,
      Deferred: 3,
      Pending: 17,
    });
  });

  it("is empty when the totals are unknown", () => {
    expect(statusCountsOf(undefined)).toEqual({});
  });
});
