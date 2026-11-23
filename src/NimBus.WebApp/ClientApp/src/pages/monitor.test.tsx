import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import moment from "moment";
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
  environment: "dev",
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
      feed: { environment: mocks.environment, platformName: "Test", tokenAccess: mocks.token !== undefined, serverStale: false },
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
  heartbeat?: { liveness: string; answeredMsAgo?: number };
  alertRuleFiring?: boolean;
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
  heartbeat,
  alertRuleFiring,
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
    alertRuleFiring,
    heartbeat: heartbeat
      ? ({
          liveness: heartbeat.liveness,
          lastAnsweredUtc: heartbeat.answeredMsAgo === undefined ? undefined : moment(now - heartbeat.answeredMsAgo),
        } as MonitorEndpoint["heartbeat"])
      : undefined,
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

/** A summary tile, found by its label. The endpoint table has a Backlog column too. */
const tile = (label: string) =>
  within(screen.getByRole("region", { name: "Fleet summary" })).getByText(label).parentElement!;

afterEach(() => {
  mocks.endpoints = [];
  mocks.telemetry = [];
  mocks.access = null;
  mocks.token = undefined;
  mocks.accessError = undefined;
  mocks.environment = "dev";
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
  it("summarises the fleet in the health ring and leaves out states no endpoint is in", () => {
    mocks.endpoints = [
      endpoint({ id: "AEndpoint" }),
      endpoint({ id: "BEndpoint", failed: 2, firstFailureAt: Date.now() }),
      endpoint({ id: "CEndpoint" }),
    ];

    render(<Monitor />);

    expect(screen.getByRole("img", { name: "2 of 3 endpoints healthy" })).toBeDefined();
    expect(screen.getByText("failing")).toBeDefined();
    expect(screen.queryByText("acknowledged")).toBeNull();
    expect(screen.queryByText("queued")).toBeNull();
  });

  it("says all healthy when no endpoint is failing or queued", () => {
    mocks.endpoints = [endpoint({ id: "AEndpoint" }), endpoint({ id: "BEndpoint" })];

    render(<Monitor />);

    expect(screen.getByText("all healthy")).toBeDefined();
  });

  it("keeps the header to the platform name", () => {
    mocks.endpoints = [endpoint({ id: "AEndpoint" })];

    render(<Monitor />);

    expect(screen.getByText("Test")).toBeDefined();
    // The footer says when the page last updated; the header no longer repeats the interval.
    expect(screen.queryByText(/Auto-refresh|ACK to silence|read-only/)).toBeNull();
    expect(screen.getByText(/every 5 s/)).toBeDefined();
  });

  it("charts the last hour and the last three days", () => {
    mocks.endpoints = [endpoint({ id: "AEndpoint" })];

    render(<Monitor />);

    expect(screen.getAllByRole("heading", { level: 2 }).map((h) => h.textContent)).toEqual([
      "Last hour",
      "Last 3 days",
      "Endpoints",
    ]);
  });

  it("names each endpoint's state in plain words, with the failed count behind a failing one", () => {
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
    // A queued endpoint's count is its backlog, which the column beside the badge shows.
    expect(badge("Agent").getByText("queued")).toBeDefined();
    expect(badge("Agent").getByText("105")).toBeDefined();
    expect(badge("Agent").getByText("· 5 deferred")).toBeDefined();
    expect(badge("Erp").getByText("healthy")).toBeDefined();
    expect(screen.getAllByRole("columnheader").map((h) => h.textContent)).toEqual([
      "Endpoint",
      "State",
      "Backlog",
      "In",
      "Processing",
      "Heartbeat",
    ]);
  });

  it("splits unresolved failures into failed, dead-lettered and unsupported", () => {
    mocks.endpoints = [endpoint({ id: "Billing", failed: 230, deadletter: 16, unsupported: 4, firstFailureAt: Date.now() })];

    render(<Monitor />);

    expect(screen.getByText("210 failed · 16 dead-lettered · 4 unsupported")).toBeDefined();
  });

  it("leaves parts that are zero out of a tile's breakdown", () => {
    mocks.endpoints = [endpoint({ id: "Billing", failed: 5, pending: 12, firstFailureAt: Date.now() })];

    render(<Monitor />);

    expect(within(tile("Unresolved failures")).getByText("5 failed")).toBeDefined();
    expect(within(tile("Backlog")).getByText("12 pending")).toBeDefined();
  });

  it("says so in the tiles when nothing failed, is blocked or is waiting", () => {
    mocks.endpoints = [endpoint({ id: "Billing", blocked: 0 })];

    render(<Monitor />);

    expect(within(tile("Unresolved failures")).getByText("nothing failed")).toBeDefined();
    expect(within(tile("Blocked sessions")).getByText("nothing blocked")).toBeDefined();
    expect(within(tile("Backlog")).getByText("nothing waiting")).toBeDefined();
  });

  it("adds up blocked sessions and names the endpoint with most", () => {
    mocks.endpoints = [
      endpoint({ id: "Billing", failed: 2, blocked: 9, deferred: 61 }),
      endpoint({ id: "Warehouse", failed: 1, blocked: 8, deferred: 39 }),
      endpoint({ id: "Crm", blocked: 0 }),
    ];

    render(<Monitor />);

    const blocked = tile("Blocked sessions");
    expect(within(blocked).getByText("17")).toBeDefined();
    expect(within(blocked).getByText("9 on Billing")).toBeDefined();
    // Deferred messages are counted once, in the Backlog tile.
    expect(within(blocked).queryByText(/deferred/)).toBeNull();
    expect(within(tile("Backlog")).getByText("100 deferred")).toBeDefined();
  });

  it("shows a dash for blocked sessions when the server cannot tell", () => {
    mocks.endpoints = [endpoint({ id: "Billing" })];

    render(<Monitor />);

    expect(within(tile("Blocked sessions")).getByText("–")).toBeDefined();
    expect(within(tile("Blocked sessions")).getByText("not reported")).toBeDefined();
  });

  it("marks an endpoint whose alert rule is firing, and only that one", () => {
    mocks.endpoints = [
      endpoint({ id: "Billing", failed: 3, firstFailureAt: Date.now(), alertRuleFiring: true }),
      endpoint({ id: "Warehouse", alertRuleFiring: true }),
      endpoint({ id: "Crm", alertRuleFiring: false }),
      endpoint({ id: "Erp" }),
    ];

    render(<Monitor />);

    const row = (id: string) => within(screen.getByRole("row", { name: new RegExp(id) }));
    const marker = row("Billing").getByText("rule firing");
    expect(marker.closest("[title]")?.getAttribute("title")).toBe("An alert rule is firing");
    // A healthy endpoint can have a firing rule too, such as silence.
    expect(row("Warehouse").getByText("rule firing")).toBeDefined();
    expect(row("Crm").queryByText("rule firing")).toBeNull();
    expect(row("Erp").queryByText("rule firing")).toBeNull();
  });

  it("counts the endpoints with a firing rule", () => {
    mocks.endpoints = [
      endpoint({ id: "Billing", alertRuleFiring: true }),
      endpoint({ id: "Warehouse", alertRuleFiring: true }),
      endpoint({ id: "Crm", alertRuleFiring: false }),
      endpoint({ id: "Erp" }),
    ];

    render(<Monitor />);

    const tiles = within(screen.getByRole("region", { name: "Fleet summary" }))
      .getAllByText(/^(Blocked sessions|Rules firing)$/)
      .map((label) => label.textContent);
    expect(tiles).toEqual(["Blocked sessions", "Rules firing"]);
    expect(within(tile("Rules firing")).getByText("2")).toBeDefined();
    expect(within(tile("Rules firing")).getByText("endpoints with a firing rule")).toBeDefined();
  });

  it("says no rule is firing, or shows a dash when no endpoint reports rules", () => {
    mocks.endpoints = [endpoint({ id: "Billing", alertRuleFiring: false }), endpoint({ id: "Erp" })];
    render(<Monitor />);
    expect(within(tile("Rules firing")).getByText("0")).toBeDefined();
    expect(within(tile("Rules firing")).getByText("nothing firing")).toBeDefined();
    cleanup();

    mocks.endpoints = [endpoint({ id: "Billing" }), endpoint({ id: "Erp" })];
    render(<Monitor />);
    expect(within(tile("Rules firing")).getByText("–")).toBeDefined();
    expect(within(tile("Rules firing")).getByText("not reported")).toBeDefined();
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

    // The last hour's success rate rides on the throughput tile instead of a tile of its own.
    const throughput = tile("Throughput now");
    expect(within(throughput).getByText("120")).toBeDefined();
    expect(within(throughput).getByText("90.5% success over the last hour")).toBeDefined();
    expect(screen.queryByText("Success rate · last hour")).toBeNull();
    const handle = tile("Time to handle · last hour");
    expect(within(handle).getByText("5.3 s")).toBeDefined();
    expect(within(handle).getByText("queue 4.0 s · processing 1.3 s")).toBeDefined();
  });

  it("says when the last hour had no attempts to rate", () => {
    mocks.endpoints = [endpoint({ id: "Billing" })];

    render(<Monitor />);

    expect(within(tile("Throughput now")).getByText("no attempts in the last hour")).toBeDefined();
  });

  it("shows per-endpoint figures and calls out failures and processing slower than usual", () => {
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
    expect(within(row).getByText("· 14.1% failed")).toBeDefined();
    expect(within(row).getByText("1.6 s")).toBeDefined();
    expect(within(row).getByText(/1\.6× usual/)).toBeDefined();
    // Queue wait is part of the fleet's time to handle; the table no longer lists it per endpoint.
    expect(within(row).queryByText("9.0 s")).toBeNull();
  });

  it("stays quiet about an endpoint that is behaving normally", () => {
    mocks.endpoints = [endpoint({ id: "Billing", heartbeat: { liveness: "alive", answeredMsAgo: 20_000 } })];
    mocks.metrics = {
      metrics: {
        lastHour: [],
        fleet: counts(0, 0, 0),
        endpoints: [{ endpointId: "billing", counts: counts(0, 100, 0, 50, 1000) }],
      } as never,
      history: {
        lastThreeDays: [],
        latencyLastDay: [],
        endpoints: [{ endpointId: "Billing", processing: { count: 100, avgMs: 1000, minMs: 0, maxMs: 0 } }],
      } as never,
    };

    render(<Monitor />);

    const row = screen.getByRole("row", { name: /Billing/ });
    expect(within(row).getByText("100")).toBeDefined();
    expect(within(row).getByText("1.0 s")).toBeDefined();
    expect(within(row).getByText("ok")).toBeDefined();
    expect(within(row).queryByText(/usual/)).toBeNull();
    expect(within(row).queryByText(/%/)).toBeNull();
    expect(within(row).queryByText(/ago/)).toBeNull();
  });

  it("calls out a heartbeat that is late or missing, with when it last answered", () => {
    mocks.endpoints = [
      endpoint({ id: "Billing", heartbeat: { liveness: "missing", answeredMsAgo: (13 * 60 + 44) * 1000 } }),
      endpoint({ id: "Orders", heartbeat: { liveness: "late", answeredMsAgo: 95_000 } }),
    ];

    render(<Monitor />);

    expect(within(screen.getByRole("row", { name: /Billing/ })).getByText("missing · 13m 44s ago")).toBeDefined();
    expect(within(screen.getByRole("row", { name: /Orders/ })).getByText("late · 1m 35s ago")).toBeDefined();
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

    expect(within(tile("Backlog")).getByText("stable")).toBeDefined();
  });
});

describe("Monitor in the site's colours", () => {
  it("gives every environment the sidebar's coral pill, production included", () => {
    mocks.environment = "prod";
    mocks.endpoints = [endpoint({ id: "AEndpoint" })];

    render(<Monitor />);

    const pill = screen.getByText("PROD");
    expect(pill.className).toContain("bg-primary-tint");
    expect(pill.className).not.toMatch(/status-(danger|warning)/);
  });

  it("marks an alarmed tile with a status dot and leaves its surface plain", () => {
    mocks.endpoints = [endpoint({ id: "Billing", failed: 5, pending: 200, firstFailureAt: Date.now() })];

    render(<Monitor />);

    const failures = tile("Unresolved failures");
    expect(failures.className).toContain("bg-card");
    expect(failures.className).not.toMatch(/bg-status-/);
    expect(failures.querySelector("[aria-hidden='true']")?.className).toContain("bg-status-danger");
    expect(tile("Backlog").querySelector("[aria-hidden='true']")?.className).toContain("bg-status-warning");
    // A tile with nothing to report carries no dot.
    expect(tile("Time to handle · last hour").querySelector("[aria-hidden='true']")).toBeNull();
  });

  it("leaves a failing or queued row untinted: the badge carries the state", () => {
    mocks.token = "nbdt_abc";
    mocks.endpoints = [
      endpoint({ id: "Billing", failed: 7, firstFailureAt: Date.now() }),
      endpoint({ id: "Agent", pending: 100 }),
    ];

    render(<Monitor />);

    for (const id of ["Billing", "Agent"]) {
      expect(screen.getByRole("row", { name: new RegExp(id) }).className).not.toMatch(/bg-status-/);
    }
    const failing = within(screen.getByRole("row", { name: /Billing/ })).getByText("failing · 7");
    expect(failing.className).toContain("rounded-full");
    expect(failing.className).toContain("bg-status-danger");
  });

  it("offers ACK in the action colour only to someone who can use it", () => {
    mocks.access = {
      siteRole: "reader",
      endpointRoles: [{ endpointId: "billingendpoint", role: "contributor" }],
    };
    mocks.endpoints = [
      endpoint({ id: "BillingEndpoint", failed: 4, firstFailureAt: Date.now() }),
      endpoint({ id: "OrdersEndpoint", failed: 2, firstFailureAt: Date.now() }),
    ];

    render(<Monitor />);

    const [billing, orders] = ["BillingEndpoint", "OrdersEndpoint"].map((id) =>
      within(screen.getByRole("row", { name: new RegExp(id) })).getByRole("button", { name: "ack" }),
    );
    expect(billing.className).toContain("border-primary");
    expect(orders.className).not.toContain("border-primary");
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
  });

  it("shows a firing rule as the signed-in wall does", () => {
    mocks.token = "nbdt_abc";
    mocks.endpoints = [endpoint({ id: "BillingEndpoint", alertRuleFiring: true })];

    render(<Monitor />);

    expect(within(screen.getByRole("row", { name: /BillingEndpoint/ })).getByText("rule firing")).toBeDefined();
    expect(within(tile("Rules firing")).getByText("1")).toBeDefined();
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
