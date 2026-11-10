import { describe, expect, it } from "vitest";
import type * as api from "api-client";
import {
  bucketStart,
  failRate,
  formatMs,
  formatPercent,
  hourlySlots,
  lastCompleteMinute,
  successRate,
  timeToHandleMs,
} from "./monitor-figures";
import { isDashboardTokenMode, readDashboardToken } from "./monitor-access";

const stats = (count: number, avgMs: number) => ({ count, avgMs, minMs: 0, maxMs: 0 }) as api.LatencyStats;

describe("lastCompleteMinute", () => {
  it("takes the bucket before the current, partial one", () => {
    expect(
      lastCompleteMinute([
        { timestamp: "a", published: 1, handled: 1, failed: 0 },
        { timestamp: "b", published: 120, handled: 90, failed: 10 },
        { timestamp: "c", published: 3, handled: 0, failed: 0 },
      ] as api.TimeSeriesDataPoint[]),
    ).toEqual({ published: 120, handled: 90, failed: 10 });
  });

  it("is undefined without a complete minute", () => {
    expect(lastCompleteMinute(undefined)).toBeUndefined();
    expect(lastCompleteMinute([{ timestamp: "c", published: 3 } as api.TimeSeriesDataPoint])).toBeUndefined();
  });
});

describe("successRate and failRate", () => {
  it("divide by attempts", () => {
    const c = { handled: 3, failed: 1 } as api.MonitorFeedCounts;
    expect(successRate(c)).toBe(0.75);
    expect(failRate(c)).toBe(0.25);
  });

  it("are undefined with no attempts", () => {
    expect(successRate({ handled: 0, failed: 0 } as api.MonitorFeedCounts)).toBeUndefined();
    expect(failRate(undefined)).toBeUndefined();
  });
});

describe("timeToHandleMs", () => {
  it("adds the two averages, each from its own population", () => {
    expect(timeToHandleMs({ queue: stats(3, 200), processing: stats(2, 25) } as api.MonitorFeedCounts)).toBe(225);
  });

  it("is undefined when either timing is missing", () => {
    expect(timeToHandleMs({ queue: stats(3, 200), processing: stats(0, 0) } as api.MonitorFeedCounts)).toBeUndefined();
  });
});

describe("formatting", () => {
  it("formats durations across units", () => {
    expect(formatMs(850)).toBe("850 ms");
    expect(formatMs(1300)).toBe("1.3 s");
    expect(formatMs(246_000)).toBe("4.1 min");
    expect(formatMs(7_200_000)).toBe("2.0 h");
    expect(formatMs(undefined)).toBe("–");
  });

  it("formats percentages without rounding to a misleading 0 or 100", () => {
    expect(formatPercent(0.905)).toBe("90.5%");
    expect(formatPercent(1)).toBe("100%");
    expect(formatPercent(0)).toBe("0%");
    expect(formatPercent(0.9996)).toBe(">99.9%");
    expect(formatPercent(0.0004)).toBe("<0.1%");
    expect(formatPercent(undefined)).toBe("–");
  });

  it("reads bucket keys as UTC", () => {
    expect(bucketStart("2026-11-10T12:34").toISOString()).toBe("2026-11-10T12:34:00.000Z");
    expect(bucketStart("2026-11-10T12").toISOString()).toBe("2026-11-10T12:00:00.000Z");
  });
});

describe("hourlySlots", () => {
  it("fills a sparse series with one slot per hour, ending with the current hour", () => {
    const now = new Date("2026-11-10T12:34:56Z");
    const slots = hourlySlots(
      [{ timestamp: "2026-11-10T10", queue: stats(1, 5), processing: stats(1, 2) } as api.MonitorFeedLatencyPoint],
      3,
      now,
    );

    expect(slots.map((s) => s.timestamp)).toEqual(["2026-11-10T10", "2026-11-10T11", "2026-11-10T12"]);
    expect(slots[0].point).toBeDefined();
    expect(slots[1].point).toBeUndefined();
  });
});

describe("dashboard token in the URL", () => {
  it("is read from the fragment", () => {
    expect(readDashboardToken("#token=nbdt_abc")).toBe("nbdt_abc");
    expect(readDashboardToken("#other=1&token=nbdt_abc")).toBe("nbdt_abc");
    expect(readDashboardToken("")).toBeUndefined();
    expect(readDashboardToken("#token=")).toBeUndefined();
  });

  it("puts only the Monitor into token mode", () => {
    expect(isDashboardTokenMode({ pathname: "/Monitor", hash: "#token=nbdt_abc" })).toBe(true);
    expect(isDashboardTokenMode({ pathname: "/monitor", hash: "#token=nbdt_abc" })).toBe(true);
    expect(isDashboardTokenMode({ pathname: "/Endpoints", hash: "#token=nbdt_abc" })).toBe(false);
    expect(isDashboardTokenMode({ pathname: "/Monitor", hash: "" })).toBe(false);
  });
});
