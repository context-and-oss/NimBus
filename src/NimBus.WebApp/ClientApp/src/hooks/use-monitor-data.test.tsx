import { act, renderHook } from "@testing-library/react";
import moment from "moment";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import {
  useMonitorData,
  STALE_AFTER_MS,
  TELEMETRY_LEN,
} from "./use-monitor-data";

// State shared with the api-client mock below.
const statusCountCalls = vi.fn();
const putAckCalls = vi.fn();
const deleteAckCalls = vi.fn();
const clientHttp = vi.fn();
let failRequests = false;
let failStatusCode: number | undefined;
let failAckWrites = false;
let failedCount = 0;
let oldestFailureAt: moment.Moment | undefined;
type ServerAck = {
  endpointId: string;
  acknowledgementId: string;
  reason: string;
  acknowledgedBy?: string;
  acknowledgedAt: moment.Moment;
  expiresAt: moment.Moment;
  failedCountAtAcknowledgement: number;
};
let serverAcks: ServerAck[] = [];

function serverAck(overrides: Partial<ServerAck> = {}): ServerAck {
  const at = moment(Date.now());
  return {
    endpointId: "ep-1",
    acknowledgementId: `ack-${Math.random()}`,
    reason: "ERP outage",
    acknowledgedBy: "alice@example.com",
    acknowledgedAt: at,
    expiresAt: at.clone().add(4, "hours"),
    failedCountAtAcknowledgement: 5,
    ...overrides,
  };
}

vi.mock("api-client", () => {
  class MonitorAcknowledgementRequest {
    reason?: string;
    constructor(data?: { reason?: string }) {
      this.reason = data?.reason;
    }
  }
  class Client {
    constructor(_auth?: unknown, _baseUrl?: string, http?: unknown) {
      clientHttp(http);
    }
    getMonitorFeedStatus() {
      statusCountCalls();
      if (failRequests) {
        return Promise.reject(new Error("api down"));
      }
      if (failStatusCode !== undefined) {
        return Promise.reject(Object.assign(new Error("refused"), { status: failStatusCode }));
      }
      return Promise.resolve({
        environment: "prod",
        platformName: "Test.Platform",
        tokenAccess: false,
        stale: false,
        endpoints: [
          {
            status: {
              endpointId: "ep-1",
              failedCount,
              pendingCount: 7,
              deferredCount: 3,
              oldestFailureAt,
            },
            acknowledgement: serverAcks.find((a) => a.endpointId === "ep-1"),
            blockedSessionCount: 2,
            heartbeat: { liveness: "alive" },
          },
        ],
      });
    }
    putMonitorAcknowledgement(
      endpointId: string,
      body: MonitorAcknowledgementRequest,
    ) {
      putAckCalls(endpointId, body.reason);
      if (failAckWrites) return Promise.reject(new Error("forbidden"));
      const saved = serverAck({
        endpointId,
        reason: body.reason ?? "",
        failedCountAtAcknowledgement: failedCount,
      });
      serverAcks = [
        ...serverAcks.filter((a) => a.endpointId !== endpointId),
        saved,
      ];
      return Promise.resolve(saved);
    }
    deleteMonitorAcknowledgement(endpointId: string) {
      deleteAckCalls(endpointId);
      if (failAckWrites) return Promise.reject(new Error("forbidden"));
      serverAcks = serverAcks.filter((a) => a.endpointId !== endpointId);
      return Promise.resolve();
    }
  }
  return { Client, CookieAuth: () => ({}), MonitorAcknowledgementRequest };
});

function resetMockState() {
  statusCountCalls.mockClear();
  putAckCalls.mockClear();
  deleteAckCalls.mockClear();
  clientHttp.mockClear();
  failRequests = false;
  failStatusCode = undefined;
  failAckWrites = false;
  failedCount = 0;
  oldestFailureAt = undefined;
  serverAcks = [];
}

const REFRESH_MS = 5_000;

function setDocumentHidden(hidden: boolean) {
  Object.defineProperty(document, "hidden", {
    configurable: true,
    get: () => hidden,
  });
}

async function flushAsync() {
  // Drain the microtask queue so in-flight fetch promises settle under fake timers.
  for (let i = 0; i < 10; i++) {
    await act(async () => {
      await Promise.resolve();
    });
  }
}

describe("useMonitorData visibility-aware polling", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    resetMockState();
    setDocumentHidden(false);
    // Node 25's experimental localStorage can leave the global undefined in
    // jsdom runs; the hook guards its own access, so just clear when present.
    window.localStorage?.clear?.();
  });

  afterEach(() => {
    vi.useRealTimers();
    setDocumentHidden(false);
  });

  it("skips polling ticks while the tab is hidden", async () => {
    const { unmount } = renderHook(() => useMonitorData());
    await flushAsync();
    expect(statusCountCalls).toHaveBeenCalledTimes(1);

    setDocumentHidden(true);
    await act(async () => {
      vi.advanceTimersByTime(REFRESH_MS * 3);
    });
    await flushAsync();

    expect(statusCountCalls).toHaveBeenCalledTimes(1);
    unmount();
  });

  it("refreshes immediately when the tab becomes visible again", async () => {
    const { unmount } = renderHook(() => useMonitorData());
    await flushAsync();
    expect(statusCountCalls).toHaveBeenCalledTimes(1);

    setDocumentHidden(true);
    await act(async () => {
      vi.advanceTimersByTime(REFRESH_MS * 2);
    });
    await flushAsync();
    expect(statusCountCalls).toHaveBeenCalledTimes(1);

    setDocumentHidden(false);
    await act(async () => {
      document.dispatchEvent(new Event("visibilitychange"));
    });
    await flushAsync();

    expect(statusCountCalls).toHaveBeenCalledTimes(2);
    unmount();
  });

  it("does not flag stale right after resuming a long-hidden tab", async () => {
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();
    expect(result.current.isStale).toBe(false);

    // Hide the tab and let far more than STALE_AFTER_MS pass without polls.
    setDocumentHidden(true);
    failRequests = true; // even the resume fetch fails — banner must still wait
    await act(async () => {
      vi.advanceTimersByTime(STALE_AFTER_MS * 4);
    });
    await flushAsync();

    setDocumentHidden(false);
    await act(async () => {
      document.dispatchEvent(new Event("visibilitychange"));
    });
    await flushAsync();
    // Advance only the 1 Hz staleness tick, well inside the grace window.
    await act(async () => {
      vi.advanceTimersByTime(1_000);
    });

    expect(result.current.isStale).toBe(false);
    unmount();
  });

  it("still flags stale when visible polling keeps failing", async () => {
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();
    expect(result.current.isStale).toBe(false);

    failRequests = true;
    await act(async () => {
      vi.advanceTimersByTime(STALE_AFTER_MS + REFRESH_MS + 1_000);
    });
    await flushAsync();
    // One more 1 Hz tick so `now` re-derives after the last failed poll.
    await act(async () => {
      vi.advanceTimersByTime(1_000);
    });

    expect(result.current.isStale).toBe(true);
    unmount();
  });
});

describe("useMonitorData fleet telemetry", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    resetMockState();
    setDocumentHidden(false);
    window.localStorage?.clear?.();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  async function poll() {
    await act(async () => {
      vi.advanceTimersByTime(REFRESH_MS);
    });
    await flushAsync();
  }

  it("records one fleet sample per poll with failed and backlog totals", async () => {
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();
    failedCount = 4;
    await poll();

    const telemetry = result.current.telemetry;
    expect(telemetry).toHaveLength(2);
    expect(telemetry[0]).toMatchObject({ failed: 0, backlog: 10 });
    expect(telemetry[1]).toMatchObject({ failed: 4, backlog: 10 });
    expect(telemetry[1].t - telemetry[0].t).toBe(REFRESH_MS);
    unmount();
  });

  it("keeps at most TELEMETRY_LEN samples (10 minutes at 5 s)", async () => {
    expect(TELEMETRY_LEN).toBe(120);
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();
    for (let i = 0; i < TELEMETRY_LEN + 3; i++) {
      await poll();
    }

    expect(result.current.telemetry).toHaveLength(TELEMETRY_LEN);
    unmount();
  });
});

describe("useMonitorData fresh failures", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    resetMockState();
    setDocumentHidden(false);
    window.localStorage?.clear?.();
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("does not flag failures that already existed when the page loaded", async () => {
    failedCount = 5;
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();

    expect(result.current.endpoints[0].isFreshFailure).toBe(false);
    unmount();
  });

  it("flags a failure the page saw start", async () => {
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();
    failedCount = 5;
    await act(async () => {
      vi.advanceTimersByTime(REFRESH_MS);
    });
    await flushAsync();

    expect(result.current.endpoints[0].isFreshFailure).toBe(true);
    unmount();
  });
});

describe("useMonitorData failing-since time", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    resetMockState();
    setDocumentHidden(false);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("counts from the server's oldest open failure, even on first load", async () => {
    failedCount = 5;
    const since = Date.now() - 3 * 60 * 60 * 1000;
    oldestFailureAt = moment(since);
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();

    expect(result.current.endpoints[0].firstFailureAt).toBe(since);
    unmount();
  });

  it("falls back to when the page first saw the failure without a server time", async () => {
    failedCount = 5;
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();

    expect(result.current.endpoints[0].firstFailureAt).toBe(Date.now());
    unmount();
  });
});

describe("useMonitorData status feed", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    resetMockState();
    setDocumentHidden(false);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("reads counts, blocked sessions, heartbeat and the deployment from one feed call", async () => {
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();

    expect(statusCountCalls).toHaveBeenCalledTimes(1);
    expect(result.current.endpoints[0]).toMatchObject({
      id: "ep-1",
      blockedSessionCount: 2,
      heartbeat: { liveness: "alive" },
    });
    expect(result.current.feed).toMatchObject({
      environment: "prod",
      platformName: "Test.Platform",
      tokenAccess: false,
    });
    unmount();
  });

  it("uses the cookie session without a token", async () => {
    const { unmount } = renderHook(() => useMonitorData());
    await flushAsync();

    expect(clientHttp).toHaveBeenLastCalledWith(undefined);
    unmount();
  });

  it("sends the token in its header, without cookies, when it has one", async () => {
    const fetchSpy = vi.spyOn(window, "fetch").mockResolvedValue(new Response("{}"));
    const { unmount } = renderHook(() => useMonitorData({ token: "nbdt_abc" }));
    await flushAsync();

    const http = clientHttp.mock.calls.at(-1)![0] as {
      fetch: (url: string, init?: RequestInit) => Promise<Response>;
    };
    await http.fetch("/api/v1/monitor/feed/status", { headers: { Accept: "application/json" } });
    const init = fetchSpy.mock.calls.at(-1)![1] as RequestInit;
    expect(new Headers(init.headers).get("X-NimBus-Dashboard-Token")).toBe("nbdt_abc");
    expect(new Headers(init.headers).get("Accept")).toBe("application/json");
    expect(init.credentials).toBe("omit");
    fetchSpy.mockRestore();
    unmount();
  });

  it.each([
    [401, "unauthorized"],
    [403, "forbidden"],
  ])("reports a %i from the feed as %s", async (status, expected) => {
    failStatusCode = status;
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();

    expect(result.current.accessError).toBe(expected);
    unmount();
  });
});

describe("useMonitorData shared acknowledgements", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    resetMockState();
    setDocumentHidden(false);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  async function poll() {
    await act(async () => {
      vi.advanceTimersByTime(REFRESH_MS);
    });
    await flushAsync();
  }

  it("shows acks from the server without reporting pre-existing ones", async () => {
    failedCount = 5;
    serverAcks = [serverAck()];
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();

    expect(result.current.endpoints[0].ack).toMatchObject({
      reason: "ERP outage",
      acknowledgedBy: "alice@example.com",
      failedAtAck: 5,
    });
    expect(result.current.ticker).toHaveLength(0);
    unmount();
  });

  it("reports an ack placed on another device in the ticker", async () => {
    failedCount = 5;
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();

    serverAcks = [serverAck()];
    await poll();

    expect(result.current.endpoints[0].ack?.reason).toBe("ERP outage");
    expect(result.current.ticker[0]).toMatchObject({
      endpoint: "ep-1",
      kind: "ack",
      detail: "ERP outage · by alice@example.com",
    });
    unmount();
  });

  it("reports the server clearing an ack on recovery", async () => {
    failedCount = 5;
    serverAcks = [serverAck()];
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();

    failedCount = 0;
    serverAcks = [];
    await poll();

    expect(result.current.endpoints[0].ack).toBeUndefined();
    expect(result.current.ticker.map((e) => [e.kind, e.detail])).toContainEqual(
      ["unack", "auto-cleared · recovered"],
    );
    unmount();
  });

  it("saves an ack to the server so other screens see it", async () => {
    failedCount = 5;
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();

    await act(async () => {
      await result.current.ack("ep-1", "known issue");
    });

    expect(putAckCalls).toHaveBeenCalledWith("ep-1", "known issue");
    expect(result.current.endpoints[0].ack?.reason).toBe("known issue");
    expect(result.current.endpoints[0].ack?.id).toBeDefined();

    // The next poll returns the same ack: no duplicate ticker entry.
    await poll();
    expect(result.current.ticker.filter((e) => e.kind === "ack")).toHaveLength(1);
    unmount();
  });

  it("restores the previous state and reports the error when saving fails", async () => {
    failedCount = 5;
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();

    failAckWrites = true;
    await act(async () => {
      await result.current.ack("ep-1", "known issue");
    });

    expect(result.current.endpoints[0].ack).toBeUndefined();
    expect(result.current.actionError).toBe("ep-1: forbidden");
    unmount();
  });

  it("clears an ack on the server", async () => {
    failedCount = 5;
    serverAcks = [serverAck()];
    const { result, unmount } = renderHook(() => useMonitorData());
    await flushAsync();

    await act(async () => {
      await result.current.unack("ep-1");
    });
    await poll();

    expect(deleteAckCalls).toHaveBeenCalledWith("ep-1");
    expect(result.current.endpoints[0].ack).toBeUndefined();
    unmount();
  });
});
