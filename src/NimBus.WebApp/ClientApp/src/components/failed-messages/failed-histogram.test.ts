import { describe, expect, it } from "vitest";
import { brushIndexes, windowOfIndexes } from "./failed-histogram";

const HOUR = 3_600_000;
const t0 = Date.parse("2026-09-25T00:00:00.000Z");
// Six hourly bars, 00:00 to 05:00.
const starts = [0, 1, 2, 3, 4, 5].map((h) => t0 + h * HOUR);

describe("brushIndexes", () => {
  it("spans every bar without a window", () => {
    expect(brushIndexes(starts, undefined)).toEqual({
      startIndex: 0,
      endIndex: 5,
    });
  });

  it("spans the bars whose start falls in the window", () => {
    expect(
      brushIndexes(starts, {
        from: new Date(t0 + 1 * HOUR),
        to: new Date(t0 + 4 * HOUR),
      }),
    ).toEqual({ startIndex: 1, endIndex: 3 });
  });

  it("falls back to every bar when the window holds none", () => {
    expect(
      brushIndexes(starts, {
        from: new Date(t0 + 10 * HOUR),
        to: new Date(t0 + 11 * HOUR),
      }),
    ).toEqual({ startIndex: 0, endIndex: 5 });
  });
});

describe("windowOfIndexes", () => {
  const end = t0 + 6 * HOUR;

  it("covers the brushed bars through the end of the last one", () => {
    const w = windowOfIndexes(starts, end, 1, 3);
    expect(w?.from.toISOString()).toBe("2026-09-25T01:00:00.000Z");
    expect(w?.to.toISOString()).toBe("2026-09-25T04:00:00.000Z");
  });

  it("ends a bar where the next one starts, so a 25-hour day stays whole", () => {
    // Local days in Berlin around the end of summer time: 24 Oct, 25 Oct (25 h), 26 Oct.
    const days = [
      "2026-10-23T22:00:00.000Z",
      "2026-10-24T22:00:00.000Z",
      "2026-10-25T23:00:00.000Z",
    ].map(Date.parse);
    const w = windowOfIndexes(days, Date.parse("2026-10-26T23:00:00.000Z"), 0, 1);
    expect(w?.to.toISOString()).toBe("2026-10-25T23:00:00.000Z");
  });

  it("is undefined when the brush spans every bar", () => {
    expect(windowOfIndexes(starts, end, 0, 5)).toBeUndefined();
    expect(windowOfIndexes([], end, 0, 0)).toBeUndefined();
  });

  it("round-trips through brushIndexes", () => {
    const w = windowOfIndexes(starts, end, 2, 4);
    expect(brushIndexes(starts, w)).toEqual({ startIndex: 2, endIndex: 4 });
  });
});
