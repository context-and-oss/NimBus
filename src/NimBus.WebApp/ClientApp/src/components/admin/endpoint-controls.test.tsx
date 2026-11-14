import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import { cleanup, render, screen, fireEvent, waitFor, within } from "@testing-library/react";
import { EndpointControlsCard } from "./endpoint-controls";

const mocks = vi.hoisted(() => ({
  getEndpointSubscriptionstatus: vi.fn(),
  getEndpointSendstatus: vi.fn(),
  postEndpointSubscriptionstatus: vi.fn(),
  postEndpointSendstatus: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") = await vi.importActual("api-client");
  class FakeClient {
    getEndpointSubscriptionstatus = mocks.getEndpointSubscriptionstatus;
    getEndpointSendstatus = mocks.getEndpointSendstatus;
    postEndpointSubscriptionstatus = mocks.postEndpointSubscriptionstatus;
    postEndpointSendstatus = mocks.postEndpointSendstatus;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

const endpoints = [{ value: "ep-1", label: "ep-1" }];

beforeEach(() => {
  // Receive already disabled, Send active.
  mocks.getEndpointSubscriptionstatus.mockReset().mockResolvedValue("disabled");
  mocks.getEndpointSendstatus.mockReset().mockResolvedValue("active");
  mocks.postEndpointSubscriptionstatus.mockReset().mockResolvedValue(undefined);
  mocks.postEndpointSendstatus.mockReset().mockResolvedValue(undefined);
});

afterEach(() => cleanup());

const row = (name: string) => screen.getByRole("cell", { name }).closest("tr")!;

describe("EndpointControlsCard", () => {
  it("disables send only after confirming, calling postEndpointSendstatus('disable')", async () => {
    render(<EndpointControlsCard endpoints={endpoints} />);

    const send = await screen.findByRole("switch", { name: "Send ep-1" });
    await waitFor(() => expect(send.getAttribute("aria-checked")).toBe("true"));
    fireEvent.click(send);

    // Confirm modal — nothing sent yet.
    expect(mocks.postEndpointSendstatus).not.toHaveBeenCalled();
    fireEvent.click(await screen.findByRole("button", { name: "Disable send" }));

    await waitFor(() =>
      expect(mocks.postEndpointSendstatus).toHaveBeenCalledWith("ep-1", "disable"),
    );
  });

  it("enables receive immediately (no confirm), calling postEndpointSubscriptionstatus('enable')", async () => {
    render(<EndpointControlsCard endpoints={endpoints} />);

    const receive = await screen.findByRole("switch", { name: "Receive ep-1" });
    await waitFor(() => expect(receive.getAttribute("aria-checked")).toBe("false"));
    fireEvent.click(receive);

    await waitFor(() =>
      expect(mocks.postEndpointSubscriptionstatus).toHaveBeenCalledWith("ep-1", "enable"),
    );
  });

  it("cannot switch an endpoint whose subscription is missing", async () => {
    mocks.getEndpointSubscriptionstatus.mockResolvedValue("not-found");
    render(<EndpointControlsCard endpoints={endpoints} />);

    const receive = await screen.findByRole("switch", { name: "Receive ep-1" });
    await waitFor(() => expect((receive as HTMLButtonElement).disabled).toBe(true));
    expect(within(row("ep-1")).getByText("Missing")).toBeTruthy();
  });

  it("reports each endpoint's loaded receive and send status", async () => {
    const onStatusChange = vi.fn();
    render(<EndpointControlsCard endpoints={endpoints} onStatusChange={onStatusChange} />);

    await waitFor(() =>
      expect(onStatusChange).toHaveBeenLastCalledWith({ "ep-1": { receive: "disabled", send: "active" } }),
    );
  });

  // Spec 038 §6.2: the kill switch and the backlog side by side.
  it("shows each endpoint's counts without double counting", async () => {
    render(
      <EndpointControlsCard
        endpoints={[...endpoints, { value: "ep-2", label: "ep-2" }]}
        counts={[
          { endpointId: "ep-1", failedCount: 6, deadletterCount: 2, pendingCount: 3, unsupportedCount: 1 } as never,
        ]}
      />,
    );
    await screen.findByRole("switch", { name: "Receive ep-1" });

    const cells = within(row("ep-1")).getAllByRole("cell").map((c) => c.textContent);
    // failedCount already includes dead-lettered and unsupported; pendingCount is
    // pending only, so each count is shown as the API sends it.
    expect(cells.slice(3, 6)).toEqual(["6", "2", "3"]);
    expect(within(row("ep-2")).getAllByRole("cell").slice(3, 6).map((c) => c.textContent)).toEqual(["—", "—", "—"]);
  });

  it("opens an operation for an endpoint from its row", async () => {
    const onOperate = vi.fn();
    render(
      <EndpointControlsCard
        endpoints={endpoints}
        counts={[{ endpointId: "ep-1", failedCount: 5 } as never]}
        onOperate={onOperate}
      />,
    );

    fireEvent.click(await screen.findByRole("button", { name: "Resubmit ep-1…" }));
    expect(onOperate).toHaveBeenCalledWith("resubmit", "ep-1");

    fireEvent.click(screen.getByRole("button", { name: "More operations for ep-1" }));
    fireEvent.click(screen.getByRole("menuitem", { name: "Delete dead-lettered" }));
    expect(onOperate).toHaveBeenLastCalledWith("dlq", "ep-1");
  });

  it("offers Resubmit only when the endpoint has failures", async () => {
    render(
      <EndpointControlsCard
        endpoints={endpoints}
        counts={[{ endpointId: "ep-1", failedCount: 0 } as never]}
        onOperate={vi.fn()}
      />,
    );
    await screen.findByRole("switch", { name: "Receive ep-1" });

    expect(screen.queryByRole("button", { name: "Resubmit ep-1…" })).toBeNull();
  });

  describe("filtering", () => {
    const two = [
      { value: "crm-adapter", label: "crm-adapter" },
      { value: "erp-adapter", label: "erp-adapter" },
    ];

    beforeEach(() => {
      // erp-adapter is paused; crm-adapter is healthy with no failures.
      mocks.getEndpointSubscriptionstatus.mockImplementation((id: string) =>
        Promise.resolve(id === "erp-adapter" ? "disabled" : "active"),
      );
    });

    it("filters by name", async () => {
      render(<EndpointControlsCard endpoints={two} />);
      await screen.findByRole("switch", { name: "Receive erp-adapter" });

      fireEvent.change(screen.getByRole("searchbox", { name: "Filter endpoints" }), { target: { value: "crm" } });

      expect(screen.getByRole("cell", { name: "crm-adapter" })).toBeTruthy();
      expect(screen.queryByRole("cell", { name: "erp-adapter" })).toBeNull();
    });

    it("shows only endpoints needing attention when asked", async () => {
      const onAttentionChange = vi.fn();
      const { rerender } = render(
        <EndpointControlsCard endpoints={two} onAttentionChange={onAttentionChange} />,
      );
      await waitFor(() =>
        expect(screen.getByRole("switch", { name: "Receive erp-adapter" }).getAttribute("aria-checked")).toBe("false"),
      );

      fireEvent.click(screen.getByRole("button", { name: "Needs attention" }));
      expect(onAttentionChange).toHaveBeenCalledWith(true);

      rerender(<EndpointControlsCard endpoints={two} attentionOnly onAttentionChange={onAttentionChange} />);
      expect(screen.getByRole("cell", { name: "erp-adapter" })).toBeTruthy();
      expect(screen.queryByRole("cell", { name: "crm-adapter" })).toBeNull();
      expect(screen.getByRole("button", { name: "Needs attention" }).getAttribute("aria-pressed")).toBe("true");
    });
  });
});
