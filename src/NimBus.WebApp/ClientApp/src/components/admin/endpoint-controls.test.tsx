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

const table = () => screen.getByRole("table", { name: "Endpoints" });
const nameButton = (name: string) => within(table()).getByRole("button", { name });
const row = (name: string) => nameButton(name).closest("tr")!;
const inspector = () => screen.getByRole("complementary", { name: "Inspector" });

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
    expect(cells.slice(1, 4)).toEqual(["6", "2", "3"]);
    expect(within(row("ep-2")).getAllByRole("cell").slice(1, 4).map((c) => c.textContent)).toEqual(["—", "—", "—"]);
  });

  it("shows the selected endpoint in the inspector, with its switches and backlog", async () => {
    render(
      <EndpointControlsCard
        endpoints={endpoints}
        counts={[{ endpointId: "ep-1", failedCount: 5, deadletterCount: 1, pendingCount: 0 } as never]}
      />,
    );
    await screen.findByRole("switch", { name: "Receive ep-1" });
    expect(within(inspector()).getByText(/Select an endpoint/)).toBeTruthy();

    fireEvent.click(nameButton("ep-1"));

    expect(nameButton("ep-1").getAttribute("aria-pressed")).toBe("true");
    await waitFor(() => expect(within(inspector()).getAllByText("Paused").length).toBeGreaterThan(0));
    expect(within(inspector()).getByText("Failed").nextElementSibling?.textContent).toBe("5");
    expect(within(inspector()).getByRole("switch", { name: "ep-1 receives" }).getAttribute("aria-checked")).toBe("false");

    // The inspector's switch is the same switch: enabling applies at once.
    fireEvent.click(within(inspector()).getByRole("switch", { name: "ep-1 receives" }));
    await waitFor(() => expect(mocks.postEndpointSubscriptionstatus).toHaveBeenCalledWith("ep-1", "enable"));
  });

  it("opens an operation for the selected endpoint from the inspector", async () => {
    const onOperate = vi.fn();
    render(<EndpointControlsCard endpoints={endpoints} onOperate={onOperate} initialSelected="ep-1" />);
    await screen.findByRole("switch", { name: "Receive ep-1" });

    fireEvent.click(within(inspector()).getByRole("button", { name: "Open Bulk resubmit failed for ep-1" }));
    expect(onOperate).toHaveBeenCalledWith("resubmit", "ep-1");

    fireEvent.click(within(inspector()).getByRole("button", { name: "Open Delete dead-lettered for ep-1" }));
    expect(onOperate).toHaveBeenLastCalledWith("dlq", "ep-1");
    // An operation that doesn't start from an endpoint isn't offered.
    expect(within(inspector()).queryByRole("button", { name: /Delete by To field/ })).toBeNull();
  });

  it("lists no operations when the page can't open them", async () => {
    render(<EndpointControlsCard endpoints={endpoints} initialSelected="ep-1" />);
    await screen.findByRole("switch", { name: "Receive ep-1" });

    expect(within(inspector()).queryByText("Operations on this endpoint")).toBeNull();
  });

  it("keeps reporting statuses while hidden, and draws nothing", async () => {
    const onStatusChange = vi.fn();
    render(<EndpointControlsCard endpoints={endpoints} onStatusChange={onStatusChange} hidden />);

    await waitFor(() =>
      expect(onStatusChange).toHaveBeenLastCalledWith({ "ep-1": { receive: "disabled", send: "active" } }),
    );
    expect(screen.queryByRole("table")).toBeNull();
    expect(screen.queryByRole("complementary")).toBeNull();
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

      expect(nameButton("crm-adapter")).toBeTruthy();
      expect(within(table()).queryByRole("button", { name: "erp-adapter" })).toBeNull();
    });

    it("shows only endpoints needing attention when asked, marking the paused one", async () => {
      const { rerender } = render(<EndpointControlsCard endpoints={two} />);
      await waitFor(() =>
        expect(screen.getByRole("switch", { name: "Receive erp-adapter" }).getAttribute("aria-checked")).toBe("false"),
      );

      rerender(<EndpointControlsCard endpoints={two} attentionOnly />);
      expect(nameButton("erp-adapter")).toBeTruthy();
      expect(within(row("erp-adapter")).getAllByRole("cell")[0].textContent).toContain("Paused");
      expect(within(table()).queryByRole("button", { name: "crm-adapter" })).toBeNull();
    });

    it("says so when nothing needs attention", async () => {
      mocks.getEndpointSubscriptionstatus.mockResolvedValue("active");
      render(<EndpointControlsCard endpoints={two} attentionOnly />);

      expect(await screen.findByText("Nothing needs attention.")).toBeTruthy();
    });
  });
});
