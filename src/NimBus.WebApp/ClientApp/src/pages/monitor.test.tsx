import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import Monitor, {
  BACKLOG_ELEVATED,
  BACKLOG_HIGH,
  backlogLevel,
  backlogTotal,
  endpointState,
  failedPerMinuteSeries,
  firstOpenFailureAt,
  formatElapsed,
  sortForTable,
} from "./monitor";
import type {
  FleetSample,
  MonitorEndpoint,
  UseMonitorDataResult,
} from "hooks/use-monitor-data";
import type { UseMonitorMetricsResult } from "hooks/use-monitor-metrics";

const mocks = vi.hoisted(() => ({
  endpoints: [] as MonitorEndpoint[],
  telemetry: [] as FleetSample[],
  access: null as unknown,
  token: undefined as string | undefined,
  accessError: undefined as "unauthorized" | "forbidden" | undefined,
  metrics: {} as UseMonitorMetricsResult,
  useAccess: vi.fn(),
  dataOptions: vi.fn(),
}));

vi.mock("hooks/use-monitor-data", () => ({
  useMonitorData: (options?: { token?: string }): UseMonitorDataResult => {
    mocks.dataOptions(options);
    return {
      endpoints: mocks.endpoints,
      lastRefreshAt: Date.now(),
      loading: false,
      error: undefined,
      accessError: mocks.accessError,
      feed: { environment: "dev", platformName: "Test", tokenAccess: mocks.token !== undefined, serverStale: false },
      isStale: false,
      ticker: [],
      telemetry: mocks.telemetry,
      ack: vi.fn(),
      unack: vi.fn(),
      refresh: vi.fn(),
    };
  },
}));

vi.mock("hooks/use-monitor-metrics", () => ({
  useMonitorMetrics: () => mocks.metrics,
}));

vi.mock("lib/monitor-access", async (importOriginal) => ({
  ...(await importOriginal<typeof import("lib/monitor-access")>()),
  readDashboardToken: () => mocks.token,
}));

vi.mock("hooks/use-access", async (importOriginal) => ({
  ...(await importOriginal<typeof import("hooks/use-access")>()),
  useAccess: () => {
    mocks.useAccess();
    return { access: mocks.access };
  },
}));

interface EndpointOptions {
  id?: string;
  pending?: number;
  deferred?: number;
  failed?: number;
  deadletter?: number;
  unsupported?: number;
  firstFailureAt?: number;
  acked?: boolean;
  acknowledgedBy?: string;
  blocked?: number;
  /** Backlog at the oldest sample — lower than current means "growing". */
  pendingWas?: number;
}

function endpoint({
  id = "ep",
  pending = 0,
  deferred = 0,
  failed = 0,
  deadletter = 0,
  unsupported = 0,
  firstFailureAt,
  acked = false,
  acknowledgedBy,
  blocked,
  pendingWas,
}: EndpointOptions): MonitorEndpoint {
  const now = Date.now();
  const samples =
    pendingWas === undefined
      ? []
      : [
          { t: now - 60_000, failed, pending: pendingWas, deferred },
          { t: now, failed, pending, deferred },
        ];
  return {
    id,
    status: {
      endpointId: id,
      failedCount: failed,
      pendingCount: pending,
      deferredCount: deferred,
      deadletterCount: deadletter,
      unsupportedCount: unsupported,
    } as MonitorEndpoint["status"],
    samples,
    firstFailureAt,
    isFreshFailure: false,
    blockedSessionCount: blocked,
    ack: acked
      ? {
          reason: "",
          ackedAt: now,
          failedAtAck: failed,
          expiresAt: now + 4 * 60 * 60 * 1000,
          acknowledgedBy,
        }
      : undefined,
  };
}

const counts = (published: number, handled: number, failed: number, queueMs?: number, processingMs?: number) => ({
  published,
  handled,
  failed,
  queue: { count: queueMs === undefined ? 0 : 10, avgMs: queueMs ?? 0, minMs: 0, maxMs: 0 },
  processing: { count: processingMs === undefined ? 0 : 10, avgMs: processingMs ?? 0, minMs: 0, maxMs: 0 },
});

afterEach(() => {
  mocks.endpoints = [];
  mocks.telemetry = [];
  mocks.access = null;
  mocks.token = undefined;
  mocks.accessError = undefined;
  mocks.metrics = {};
  mocks.useAccess.mockClear();
  mocks.dataOptions.mockClear();
  vi.unstubAllGlobals();
  cleanup();
});

describe("backlogTotal", () => {
  it("counts pending and deferred together", () => {
    expect(backlogTotal(endpoint({ pending: 10, deferred: 5 }))).toBe(15);
  });
});

describe("backlogLevel", () => {
  it("stays normal below the elevated threshold", () => {
    expect(backlogLevel(endpoint({ pending: BACKLOG_ELEVATED - 1 }))).toBe("normal");
  });

  it("is elevated at the threshold when the queue is not growing", () => {
    expect(backlogLevel(endpoint({ pending: BACKLOG_ELEVATED, pendingWas: BACKLOG_ELEVATED }))).toBe("elevated");
  });

  it("is high at the high threshold", () => {
    expect(backlogLevel(endpoint({ pending: BACKLOG_HIGH }))).toBe("high");
  });

  it("escalates an elevated backlog that is still growing", () => {
    expect(backlogLevel(endpoint({ pending: BACKLOG_ELEVATED + 10, pendingWas: 1 }))).toBe("high");
  });

  it("does not escalate a small backlog just because it is growing", () => {
    expect(backlogLevel(endpoint({ pending: 7, pendingWas: 1 }))).toBe("normal");
  });
});

describe("endpointState", () => {
  it("classifies failing, acknowledged, backlog and healthy endpoints", () => {
    expect(endpointState(endpoint({ failed: 3 }))).toBe("nogo");
    expect(endpointState(endpoint({ failed: 3, acked: true }))).toBe("acked");
    expect(endpointState(endpoint({ pending: 4 }))).toBe("hold");
    expect(endpointState(endpoint({ deferred: 2 }))).toBe("hold");
    expect(endpointState(endpoint({}))).toBe("go");
  });
});

describe("sortForTable", () => {
  it("puts failing first by size, then acknowledged, then backlog by depth, then the rest A to Z", () => {
    const sorted = sortForTable([
      endpoint({ id: "Zeta" }),
      endpoint({ id: "Hold-small", pending: 3 }),
      endpoint({ id: "Acked", failed: 50, acked: true }),
      endpoint({ id: "Small-fail", failed: 2 }),
      endpoint({ id: "Alpha" }),
      endpoint({ id: "Big-fail", failed: 400 }),
      endpoint({ id: "Hold-big", deferred: 90 }),
    ]);

    expect(sorted.map((e) => e.id)).toEqual([
      "Big-fail",
      "Small-fail",
      "Acked",
      "Hold-big",
      "Hold-small",
      "Alpha",
      "Zeta",
    ]);
  });
});

describe("firstOpenFailureAt", () => {
  it("returns the earliest unacknowledged failure and ignores acked ones", () => {
    expect(
      firstOpenFailureAt([
        endpoint({ id: "a", failed: 1, firstFailureAt: 3_000 }),
        endpoint({ id: "b", failed: 1, firstFailureAt: 1_000, acked: true }),
        endpoint({ id: "c", failed: 1, firstFailureAt: 2_000 }),
        endpoint({ id: "d", pending: 9 }),
      ]),
    ).toBe(2_000);
  });

  it("is undefined when nothing is failing unacknowledged", () => {
    expect(firstOpenFailureAt([endpoint({ failed: 1, firstFailureAt: 1_000, acked: true })])).toBeUndefined();
  });
});

describe("formatElapsed", () => {
  it("adds a day count once a failure is older than 24 hours", () => {
    expect(formatElapsed((6 * 3600 + 24 * 60 + 23) * 1000)).toBe("06:24:23");
    expect(formatElapsed(((64 * 24 + 6) * 3600 + 24 * 60 + 23) * 1000)).toBe("64d 06:24:23");
  });
});

describe("failedPerMinuteSeries", () => {
  const sample = (t: number, failed: number): FleetSample => ({ t, failed, backlog: 0 });

  it("turns cumulative failed totals into failures per minute", () => {
    const telemetry = [0, 1, 2, 3].map((i) => sample(i * 5_000, i * 10));
    expect(failedPerMinuteSeries(telemetry)).toEqual([0, 120, 120, 120]);
  });

  it("never goes negative when failures are resubmitted or skipped", () => {
    expect(failedPerMinuteSeries([sample(0, 50), sample(5_000, 20)])).toEqual([0, 0]);
  });
});

describe("Monitor wall", () => {
  it("summarises the fleet in the health ring", () => {
    mocks.endpoints = [
      endpoint({ id: "AEndpoint" }),
      endpoint({ id: "BEndpoint", failed: 2, firstFailureAt: Date.now() }),
      endpoint({ id: "CEndpoint" }),
    ];

    render(<Monitor />);

    expect(screen.getByRole("img", { name: "2 of 3 endpoints healthy" })).toBeDefined();
    expect(screen.getByText("failing")).toBeDefined();
    expect(screen.getByText("queued")).toBeDefined();
  });

  it("names each endpoint's state in plain words with the count behind it", () => {
    mocks.token = "nbdt_abc"; // read-only: every row shows its badge
    mocks.endpoints = [
      endpoint({ id: "Billing", failed: 7, firstFailureAt: Date.now() }),
      endpoint({ id: "Orders", failed: 2, firstFailureAt: Date.now(), acked: true }),
      endpoint({ id: "Agent", pending: 100, deferred: 5 }),
      endpoint({ id: "Erp" }),
    ];

    render(<Monitor />);

    const badge = (id: string) => within(screen.getByRole("row", { name: new RegExp(id) }));
    expect(badge("Billing").getByText("failing · 7")).toBeDefined();
    expect(badge("Orders").getByText("acked · 2")).toBeDefined();
    expect(badge("Agent").getByText("queued · 105")).toBeDefined();
    expect(badge("Erp").getByText("healthy")).toBeDefined();
    // The badge carries the failed count, so the table no longer repeats it.
    expect(screen.queryByRole("columnheader", { name: "Unresolved" })).toBeNull();
  });

  it("splits unresolved failures into failed, dead-lettered and unsupported", () => {
    mocks.endpoints = [endpoint({ id: "Billing", failed: 230, deadletter: 16, unsupported: 4, firstFailureAt: Date.now() })];

    render(<Monitor />);

    expect(screen.getByText("210 failed · 16 dead-lettered · 4 unsupported")).toBeDefined();
  });

  it("adds up blocked sessions and names the endpoints with most", () => {
    mocks.endpoints = [
      endpoint({ id: "Billing", failed: 2, blocked: 9, deferred: 61 }),
      endpoint({ id: "Warehouse", failed: 1, blocked: 8, deferred: 39 }),
      endpoint({ id: "Crm", blocked: 0 }),
    ];

    render(<Monitor />);

    const tile = screen.getByText("Blocked sessions").parentElement!;
    expect(within(tile).getByText("17")).toBeDefined();
    expect(within(tile).getByText("9 Billing · 8 Warehouse · 100 deferred")).toBeDefined();
  });

  it("shows a dash for blocked sessions when the server cannot tell", () => {
    mocks.endpoints = [endpoint({ id: "Billing" })];

    render(<Monitor />);

    expect(within(screen.getByText("Blocked sessions").parentElement!).getByText("–")).toBeDefined();
  });

  it("works out throughput, success rate and time to handle from the metrics feed", () => {
    mocks.endpoints = [endpoint({ id: "Billing" })];
    mocks.metrics = {
      metrics: {
        lastHour: [
          { timestamp: "2026-11-10T12:33", published: 120, handled: 90, failed: 10 },
          { timestamp: "2026-11-10T12:34", published: 7, handled: 1, failed: 0 },
        ],
        fleet: counts(3272, 2497, 263, 4000, 1300),
        endpoints: [],
      } as never,
    };

    render(<Monitor />);

    const throughput = screen.getByText("Throughput now").parentElement!;
    expect(within(throughput).getByText("120")).toBeDefined();
    expect(within(throughput).getByText("90 handled · 10 failed per min")).toBeDefined();
    const success = screen.getByText("Success rate · last hour").parentElement!;
    expect(within(success).getByText("90.5%")).toBeDefined();
    expect(within(success).getByText(`263 of ${(2760).toLocaleString()} attempts failed`)).toBeDefined();
    const handle = screen.getByText("Time to handle · last hour").parentElement!;
    expect(within(handle).getByText("5.3 s")).toBeDefined();
    expect(within(handle).getByText("queue 4.0 s · processing 1.3 s")).toBeDefined();
  });

  it("shows per-endpoint figures and calls out processing slower than usual", () => {
    mocks.endpoints = [endpoint({ id: "Billing", failed: 4, firstFailureAt: Date.now() })];
    mocks.metrics = {
      metrics: {
        lastHour: [],
        fleet: counts(0, 0, 0),
        endpoints: [{ endpointId: "billing", counts: counts(0, 754, 124, 9000, 1600) }],
      } as never,
      history: {
        lastThreeDays: [],
        latencyLastDay: [],
        endpoints: [{ endpointId: "Billing", processing: { count: 100, avgMs: 1000, minMs: 0, maxMs: 0 } }],
      } as never,
    };

    render(<Monitor />);

    const row = screen.getByRole("row", { name: /Billing/ });
    expect(within(row).getByText("878")).toBeDefined();
    expect(within(row).getByText("14.1%")).toBeDefined();
    expect(within(row).getByText("1.6 s")).toBeDefined();
    expect(within(row).getByText(/1\.6× usual/)).toBeDefined();
    expect(within(row).getByText("9.0 s")).toBeDefined();
  });

  it("counts failing time from the oldest open failure", () => {
    mocks.endpoints = [
      endpoint({ id: "BillingEndpoint", failed: 4, firstFailureAt: Date.now() - (3 * 60 + 20) * 60 * 1000 }),
    ];

    render(<Monitor />);

    expect(screen.getByText("for 3h 20m")).toBeDefined();
    expect(screen.getByText("T+ since first failure")).toBeDefined();
  });

  it("names who acknowledged a failure", () => {
    mocks.endpoints = [
      endpoint({ id: "BillingEndpoint", failed: 4, firstFailureAt: Date.now(), acked: true, acknowledgedBy: "alice@example.com" }),
    ];

    render(<Monitor />);

    expect(screen.getByText("alice · 0s ago")).toBeDefined();
  });

  it("shows readers the ACK state without letting them change it", () => {
    mocks.access = {
      siteRole: "reader",
      endpointRoles: [{ endpointId: "billingendpoint", role: "contributor" }],
    };
    mocks.endpoints = [
      endpoint({ id: "BillingEndpoint", failed: 4, firstFailureAt: Date.now() }),
      endpoint({ id: "OrdersEndpoint", failed: 2, firstFailureAt: Date.now(), acked: true }),
    ];

    render(<Monitor />);

    const ack = screen.getByRole("button", { name: "ack" }) as HTMLButtonElement;
    // The acked button stands in for the badge, so it carries the count too.
    const acked = screen.getByRole("button", { name: "✓ acked · 2" }) as HTMLButtonElement;
    expect(ack.disabled).toBe(false);
    expect(acked.disabled).toBe(true);
  });

  it("says so when nothing is failing", () => {
    mocks.endpoints = [endpoint({ id: "AEndpoint" })];

    render(<Monitor />);

    expect(screen.getByText("No open failures")).toBeDefined();
  });

  it("reports a flat backlog as stable", () => {
    mocks.endpoints = [endpoint({ id: "MitHrEndpoint", pending: 880, pendingWas: 879.8 })];

    render(<Monitor />);

    expect(within(screen.getByText("Backlog").parentElement!).getByText("stable")).toBeDefined();
  });
});

describe("Monitor with a dashboard token", () => {
  it("is read-only and never asks for the caller's access", () => {
    mocks.token = "nbdt_abc";
    mocks.endpoints = [endpoint({ id: "BillingEndpoint", failed: 4, firstFailureAt: Date.now() })];

    render(<Monitor />);

    expect(mocks.dataOptions).toHaveBeenCalledWith({ token: "nbdt_abc" });
    expect(mocks.useAccess).not.toHaveBeenCalled();
    expect(screen.queryByRole("button", { name: "ack" })).toBeNull();
    expect(screen.getByText(/read-only dashboard/)).toBeDefined();
  });

  it("says when the token is refused and stays on the page", () => {
    mocks.token = "nbdt_abc";
    mocks.accessError = "unauthorized";
    const assign = vi.fn();
    vi.stubGlobal("location", { ...window.location, assign });

    render(<Monitor />);

    expect(screen.getByText("This dashboard link is invalid, expired or revoked")).toBeDefined();
    expect(assign).not.toHaveBeenCalled();
  });
});

describe("Monitor without a token", () => {
  it("sends a signed-out browser to the app root, which picks the login", () => {
    mocks.accessError = "unauthorized";
    const assign = vi.fn();
    vi.stubGlobal("location", { ...window.location, assign });

    render(<Monitor />);

    expect(assign).toHaveBeenCalledWith("/");
  });

  it("explains that the Reader role is needed", () => {
    mocks.accessError = "forbidden";

    render(<Monitor />);

    expect(screen.getByText("The Monitor requires the Reader role")).toBeDefined();
  });
});
