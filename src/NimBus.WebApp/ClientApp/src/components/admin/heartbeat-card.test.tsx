import { describe, it, expect, afterEach, beforeEach, vi } from "vitest";
import {
  cleanup,
  render,
  screen,
  fireEvent,
  waitFor,
  within,
} from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import moment from "moment";
import { formatMoment } from "functions/endpoint.functions";
import HeartbeatCard from "./heartbeat-card";
import { renderInPanel } from "../../test-utils/render-in-panel";

const mocks = vi.hoisted(() => ({
  getAdminHeartbeatSettings: vi.fn(),
  putAdminHeartbeatSettings: vi.fn(),
  postAdminHeartbeatSend: vi.fn(),
  getAdminHeartbeatOverview: vi.fn(),
  putAdminHeartbeatEndpointEnabled: vi.fn(),
  subscribeHeartbeatUpdates: vi.fn(),
}));

vi.mock("api-client", async () => {
  const actual: typeof import("api-client") =
    await vi.importActual("api-client");
  class FakeClient {
    getAdminHeartbeatSettings = mocks.getAdminHeartbeatSettings;
    putAdminHeartbeatSettings = mocks.putAdminHeartbeatSettings;
    postAdminHeartbeatSend = mocks.postAdminHeartbeatSend;
    getAdminHeartbeatOverview = mocks.getAdminHeartbeatOverview;
    putAdminHeartbeatEndpointEnabled = mocks.putAdminHeartbeatEndpointEnabled;
  }
  return { ...actual, Client: FakeClient, CookieAuth: () => ({}) };
});

// No real hub connection in tests — see platform-services-card.test.tsx.
vi.mock("lib/grid-events-connection", () => ({
  subscribeHeartbeatUpdates: mocks.subscribeHeartbeatUpdates,
}));

function overviewRow(overrides: Record<string, unknown> = {}) {
  return {
    endpointId: "crm",
    isHeartbeatEnabled: true,
    status: "On",
    roundTripMs: 120,
    sdkVersion: "1.4.2",
    ...overrides,
  };
}

beforeEach(() => {
  mocks.getAdminHeartbeatSettings.mockReset().mockResolvedValue({
    enabled: true,
    intervalSeconds: 300,
    timeoutSeconds: 60,
  });
  mocks.putAdminHeartbeatSettings
    .mockReset()
    .mockImplementation((body: unknown) => Promise.resolve(body));
  mocks.postAdminHeartbeatSend.mockReset().mockResolvedValue({ count: 2 });
  mocks.getAdminHeartbeatOverview
    .mockReset()
    .mockResolvedValue([overviewRow()]);
  mocks.putAdminHeartbeatEndpointEnabled
    .mockReset()
    .mockResolvedValue(undefined);
  mocks.subscribeHeartbeatUpdates
    .mockReset()
    .mockReturnValue({ dispose: () => {} });
});

function renderCard() {
  return render(
    <MemoryRouter>
      <HeartbeatCard />
    </MemoryRouter>,
  );
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
});

describe("HeartbeatCard", () => {
  it("renders the stored schedule and the clamp minimums", async () => {
    renderCard();

    const interval = (await screen.findByLabelText(
      "Interval seconds",
    )) as HTMLInputElement;
    const timeout = screen.getByLabelText(
      "Timeout seconds",
    ) as HTMLInputElement;
    const toggle = screen.getByRole("switch", {
      name: "Scheduled heartbeat enabled",
    });

    await waitFor(() => expect(interval.value).toBe("300"));
    expect(timeout.value).toBe("60");
    expect(toggle.getAttribute("aria-checked")).toBe("true");
    expect(interval.min).toBe("30");
    expect(timeout.min).toBe("5");
    expect(screen.getByRole("heading", { name: "Heartbeat" })).toBeTruthy();
    expect(
      screen.getByText("Endpoint health, round-trip latency, and SDK version."),
    ).toBeTruthy();
  });

  it("saves the values as typed — the server owns the clamping", async () => {
    renderCard();

    const interval = (await screen.findByLabelText(
      "Interval seconds",
    )) as HTMLInputElement;
    await waitFor(() => expect(interval.value).toBe("300"));

    fireEvent.change(interval, { target: { value: "45" } });
    fireEvent.change(screen.getByLabelText("Timeout seconds"), {
      target: { value: "10" },
    });
    fireEvent.click(
      screen.getByRole("switch", { name: "Scheduled heartbeat enabled" }),
    );
    fireEvent.click(screen.getByRole("button", { name: "Save" }));

    await waitFor(() =>
      expect(mocks.putAdminHeartbeatSettings).toHaveBeenCalledWith(
        expect.objectContaining({
          enabled: false,
          intervalSeconds: 45,
          timeoutSeconds: 10,
        }),
      ),
    );
  });

  it("sends a heartbeat now and reports how many endpoints it reached", async () => {
    renderCard();

    fireEvent.click(await screen.findByRole("button", { name: "Send now" }));

    await waitFor(() =>
      expect(mocks.postAdminHeartbeatSend).toHaveBeenCalledTimes(1),
    );
    expect(
      await screen.findByText("Heartbeat sent to 2 endpoint(s)."),
    ).toBeTruthy();
  });

  it("links operational status to the heartbeat page", async () => {
    renderCard();

    const link = await screen.findByRole("link", { name: "Heartbeat page" });
    expect(link.getAttribute("href")).toBe("/Heartbeat");
  });

  it("opts an endpoint out of the fan-out", async () => {
    renderCard();

    fireEvent.click(
      await screen.findByRole("switch", {
        name: "Include crm in heartbeat probes",
      }),
    );

    await waitFor(() =>
      expect(mocks.putAdminHeartbeatEndpointEnabled).toHaveBeenCalledWith(
        "crm",
        expect.objectContaining({ enabled: false }),
      ),
    );
  });

  it("opts an excluded endpoint back in", async () => {
    mocks.getAdminHeartbeatOverview.mockResolvedValue([
      overviewRow({ isHeartbeatEnabled: false }),
    ]);
    renderCard();

    fireEvent.click(
      await screen.findByRole("switch", {
        name: "Include crm in heartbeat probes",
      }),
    );

    await waitFor(() =>
      expect(mocks.putAdminHeartbeatEndpointEnabled).toHaveBeenCalledWith(
        "crm",
        expect.objectContaining({ enabled: true }),
      ),
    );
  });

  it("surfaces a failed load instead of an empty 'no endpoints' table", async () => {
    mocks.getAdminHeartbeatOverview.mockRejectedValue(new Error("boom"));
    renderCard();

    const alert = await screen.findByRole("alert");
    expect(alert.textContent).toContain("boom");
    await waitFor(() => expect(screen.queryByText("No endpoints.")).toBeNull());
  });

  it("filters endpoints by heartbeat inclusion", async () => {
    mocks.getAdminHeartbeatOverview.mockResolvedValue([
      overviewRow({ endpointId: "included", isHeartbeatEnabled: true }),
      overviewRow({ endpointId: "excluded", isHeartbeatEnabled: false }),
    ]);
    renderCard();

    const filter = await screen.findByLabelText("Filter endpoints");
    await screen.findByText("included");
    fireEvent.change(filter, { target: { value: "excluded" } });

    expect(screen.queryByText("included")).toBeNull();
    expect(screen.getByText("excluded")).toBeTruthy();
  });

  // Spec 038 slice 2: inside the Settings panel frame.
  describe("in the Settings panel", () => {
    it("counts unsaved schedule edits and discards them", async () => {
      const { onDirtyChange } = renderInPanel(<HeartbeatCard />, "heartbeat");
      const interval = (await screen.findByLabelText("Interval seconds")) as HTMLInputElement;
      await waitFor(() => expect(interval.value).toBe("300"));

      fireEvent.change(interval, { target: { value: "120" } });
      fireEvent.click(screen.getByRole("switch", { name: "Scheduled heartbeat enabled" }));

      expect(onDirtyChange).toHaveBeenLastCalledWith(2);
      fireEvent.click(screen.getByRole("button", { name: "Discard" }));
      expect(interval.value).toBe("300");
      expect(onDirtyChange).toHaveBeenLastCalledWith(0);
    });

    it("shows only the Probing tab's controls", async () => {
      renderInPanel(<HeartbeatCard />, "heartbeat");

      expect(await screen.findByLabelText("Interval seconds")).toBeTruthy();
      expect(screen.queryByLabelText("Filter endpoints")).toBeNull();
    });
  });

  it("keeps unsaved schedule edits when sending a heartbeat now", async () => {
    renderCard();
    const interval = (await screen.findByLabelText("Interval seconds")) as HTMLInputElement;
    await waitFor(() => expect(interval.value).toBe("300"));
    fireEvent.change(interval, { target: { value: "120" } });

    fireEvent.click(screen.getByRole("button", { name: "Send now" }));

    await screen.findByText("Heartbeat sent to 2 endpoint(s).");
    expect(interval.value).toBe("120");
  });

  describe("endpoints table", () => {
    const inspector = () => within(screen.getByRole("complementary", { name: "Inspector" }));

    it("shows each endpoint's last status and round trip", async () => {
      renderCard();

      const table = within(await screen.findByRole("table", { name: "Heartbeat endpoints" }));
      const row = within((await table.findByRole("button", { name: "crm" })).closest("tr") as HTMLElement);
      expect(row.getByText("On", { selector: "td > *" })).toBeTruthy();
      expect(row.getByText("120 ms")).toBeTruthy();
    });

    it("asks for a selection while no endpoint is selected", async () => {
      renderCard();

      await screen.findByRole("button", { name: "crm" });
      expect(inspector().getByText("Select an endpoint to see its last probe.")).toBeTruthy();
    });

    it("shows the selected endpoint's last probe in the inspector", async () => {
      mocks.getAdminHeartbeatOverview.mockResolvedValue([
        overviewRow({ lastStartTime: moment("2026-11-18T09:00:00Z"), lastReceivedTime: moment("2026-11-18T09:00:01Z") }),
        overviewRow({ endpointId: "erp", isHeartbeatEnabled: false, status: "Off", roundTripMs: undefined }),
      ]);
      renderCard();

      const select = await screen.findByRole("button", { name: "crm" });
      fireEvent.click(select);

      expect(select.getAttribute("aria-pressed")).toBe("true");
      expect(inspector().getByRole("heading", { name: "crm" })).toBeTruthy();
      expect(inspector().getByText("Included")).toBeTruthy();
      expect(inspector().getByText("Answered the last heartbeat.")).toBeTruthy();
      expect(inspector().getByText("120 ms")).toBeTruthy();
      expect(inspector().getByText("1.4.2")).toBeTruthy();
      expect(inspector().getByText(formatMoment(moment("2026-11-18T09:00:00Z"), true))).toBeTruthy();
      expect(inspector().getByText(formatMoment(moment("2026-11-18T09:00:01Z"), true))).toBeTruthy();
      expect(inspector().getByRole("link", { name: /Heartbeat page/ }).getAttribute("href")).toBe("/Heartbeat");

      fireEvent.click(screen.getByRole("button", { name: "erp" }));
      expect(select.getAttribute("aria-pressed")).toBe("false");
      expect(inspector().getByRole("heading", { name: "erp" })).toBeTruthy();
      expect(inspector().getByText("Excluded")).toBeTruthy();
    });

    it("keeps one Included switch per endpoint, and it still saves with the row selected", async () => {
      renderCard();

      fireEvent.click(await screen.findByRole("button", { name: "crm" }));
      // getByRole throws on a second switch with the same name.
      fireEvent.click(screen.getByRole("switch", { name: "Include crm in heartbeat probes" }));

      await waitFor(() =>
        expect(mocks.putAdminHeartbeatEndpointEnabled).toHaveBeenCalledWith(
          "crm",
          expect.objectContaining({ enabled: false }),
        ),
      );
    });

    it("clears the inspector when the filter hides the selected endpoint", async () => {
      renderCard();

      fireEvent.click(await screen.findByRole("button", { name: "crm" }));
      fireEvent.change(screen.getByLabelText("Filter endpoints"), { target: { value: "excluded" } });

      expect(inspector().getByText("Select an endpoint to see its last probe.")).toBeTruthy();
    });

    it("opens the Settings panel's inspector drawer on the selected endpoint", async () => {
      renderInPanel(<HeartbeatCard />, "heartbeat", "endpoints");

      fireEvent.click(await screen.findByRole("button", { name: "crm" }));

      expect(screen.getAllByRole("complementary", { name: "Inspector" })).toHaveLength(1);
      expect(inspector().getByRole("heading", { name: "crm" })).toBeTruthy();
      expect(screen.queryByLabelText("Interval seconds")).toBeNull();
    });
  });
});
