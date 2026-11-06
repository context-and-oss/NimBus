import { afterAll, beforeAll, describe, expect, it } from "vitest";
import moment from "moment";
import * as api from "api-client";
import {
  EMPTY_FAILED_FILTER,
  barEnd,
  customRange,
  deferredCountsBySession,
  errorTextOf,
  formatBucket,
  parseSearchQuery,
  rangeParams,
  rangeWindow,
  resolveWindow,
  searchQueryOf,
  selectedWindow,
  toFailedSearchFilter,
  toggleStatus,
  windowParams,
} from "./failed-messages.functions";

// The chart's bars follow the browser's wall clock, so pin the zone: UTC unless a block
// below picks another.
const ORIGINAL_TZ = process.env.TZ;
beforeAll(() => {
  process.env.TZ = "UTC";
});
afterAll(() => {
  if (ORIGINAL_TZ === undefined) delete process.env.TZ;
  else process.env.TZ = ORIGINAL_TZ;
});

describe("in a local time zone (Europe/Berlin)", () => {
  beforeAll(() => {
    process.env.TZ = "Europe/Berlin";
  });
  afterAll(() => {
    process.env.TZ = "UTC";
  });

  // Thursday 5 November 2026, 13:00 CET. Summer time ended on 25 October.
  const now = new Date("2026-11-05T12:00:00Z");

  it("starts 30-day bars at local midnight, like the server", () => {
    const w = resolveWindow(api.Period._30d, now);
    expect(w.from.toISOString()).toBe("2026-10-06T22:00:00.000Z"); // 7 Oct, 00:00 CEST
    expect(w.to.toISOString()).toBe("2026-11-05T23:00:00.000Z"); // 6 Nov, 00:00 CET
  });

  it("aligns 6-hour bars to local hours", () => {
    const w = resolveWindow(api.Period._7d, now);
    expect(w.to.toISOString()).toBe("2026-11-05T17:00:00.000Z"); // 18:00 CET
  });

  it("widens a custom range to local buckets", () => {
    const w = rangeWindow(
      {
        ...EMPTY_FAILED_FILTER,
        rangeStart: "2026-10-28T22:30:00.000Z",
        rangeEnd: "2026-11-04T22:30:00.000Z",
      },
      now,
    );
    expect(w.bucketMinutes).toBe(180);
    expect(w.from.toISOString()).toBe("2026-10-28T20:00:00.000Z"); // 21:00 CET
    expect(w.to.toISOString()).toBe("2026-11-04T23:00:00.000Z"); // 00:00 CET
  });

  it("sizes a legacy bucket link by local days, so 25 October has 25 hours", () => {
    const w = selectedWindow({
      ...EMPTY_FAILED_FILTER,
      period: api.Period._30d,
      bucket: "2026-10-24T22:00:00.000Z",
    });
    expect(w?.to.toISOString()).toBe("2026-10-25T23:00:00.000Z");
  });

  it("ends a bar at the next bar's start, and the last at the window's end", () => {
    const histogram = {
      buckets: [
        new api.FailedHistogramBucket({ start: moment.utc("2026-10-24T22:00:00Z") }),
        new api.FailedHistogramBucket({ start: moment.utc("2026-10-25T23:00:00Z") }),
      ],
      to: moment.utc("2026-10-26T23:00:00Z"),
    };
    expect(barEnd(histogram, 0)?.toISOString()).toBe("2026-10-25T23:00:00.000Z");
    expect(barEnd(histogram, 1)?.toISOString()).toBe("2026-10-26T23:00:00.000Z");
  });

  it("labels a bar with the end it was given", () => {
    // 02:00 CEST to 03:00 CET: two hours, as the repeated hour is one bar.
    expect(
      formatBucket(
        new Date("2026-10-25T00:00:00Z"),
        60,
        new Date("2026-10-25T02:00:00Z"),
      ),
    ).toBe("25/10 02:00–03:00");
  });
});

describe("resolveWindow", () => {
  it("ends at the end of the bucket holding now, like the server", () => {
    const now = new Date("2026-09-25T10:17:30Z");

    const day = resolveWindow(api.Period._1d, now);
    expect(day.to.toISOString()).toBe("2026-09-25T11:00:00.000Z");
    expect(day.from.toISOString()).toBe("2026-09-24T11:00:00.000Z");

    const hour = resolveWindow(api.Period._1h, now);
    expect(hour.to.toISOString()).toBe("2026-09-25T10:20:00.000Z");
    expect(hour.from.toISOString()).toBe("2026-09-25T09:20:00.000Z");
  });

  it("falls back to the default range for an unknown period", () => {
    const now = new Date("2026-09-25T10:17:30Z");
    const w = resolveWindow("nonsense", now);
    expect((w.to.getTime() - w.from.getTime()) / 3_600_000).toBe(24);
  });
});

describe("customRange", () => {
  it("reads a valid custom range from the URL values", () => {
    const r = customRange({
      rangeStart: "2026-09-20T08:00:00.000Z",
      rangeEnd: "2026-09-21T08:00:00.000Z",
    });
    expect(r?.from.toISOString()).toBe("2026-09-20T08:00:00.000Z");
    expect(r?.to.toISOString()).toBe("2026-09-21T08:00:00.000Z");
  });

  it("ignores a missing, reversed, unparsable or over-long range", () => {
    expect(customRange({ rangeStart: "", rangeEnd: "" })).toBeUndefined();
    expect(
      customRange({
        rangeStart: "2026-09-21T08:00:00.000Z",
        rangeEnd: "2026-09-20T08:00:00.000Z",
      }),
    ).toBeUndefined();
    expect(
      customRange({ rangeStart: "x", rangeEnd: "2026-09-20T08:00:00.000Z" }),
    ).toBeUndefined();
    expect(
      customRange({
        rangeStart: "2026-01-01T00:00:00.000Z",
        rangeEnd: "2026-09-20T00:00:00.000Z",
      }),
    ).toBeUndefined();
  });
});

describe("rangeWindow", () => {
  const now = new Date("2026-09-25T10:17:30Z");

  it("uses the preset when no custom range is set", () => {
    const w = rangeWindow({ ...EMPTY_FAILED_FILTER, period: "7d" }, now);
    expect(w.bucketMinutes).toBe(360);
    expect(w.to.toISOString()).toBe("2026-09-25T12:00:00.000Z");
  });

  it("widens a custom range to the smallest bucket keeping it within 60 bars, like the server", () => {
    const short = rangeWindow(
      {
        ...EMPTY_FAILED_FILTER,
        rangeStart: "2026-09-25T10:07:00.000Z",
        rangeEnd: "2026-09-25T13:52:00.000Z",
      },
      now,
    );
    expect(short.bucketMinutes).toBe(5);
    expect(short.from.toISOString()).toBe("2026-09-25T10:05:00.000Z");
    expect(short.to.toISOString()).toBe("2026-09-25T13:55:00.000Z");

    const days = rangeWindow(
      {
        ...EMPTY_FAILED_FILTER,
        rangeStart: "2026-09-22T10:07:00.000Z",
        rangeEnd: "2026-09-25T10:07:00.000Z",
      },
      now,
    );
    expect(days.bucketMinutes).toBe(180);
    expect(days.from.toISOString()).toBe("2026-09-22T09:00:00.000Z");
    expect(days.to.toISOString()).toBe("2026-09-25T12:00:00.000Z");
  });
});

describe("rangeParams", () => {
  it("writes a range as ISO params, or clears it", () => {
    expect(
      rangeParams({
        from: new Date("2026-09-20T08:00:00Z"),
        to: new Date("2026-09-21T08:00:00Z"),
      }),
    ).toEqual({
      rangeStart: "2026-09-20T08:00:00.000Z",
      rangeEnd: "2026-09-21T08:00:00.000Z",
    });
    expect(rangeParams(undefined)).toEqual({ rangeStart: "", rangeEnd: "" });
  });
});

describe("toFailedSearchFilter", () => {
  it("maps the applied values and keeps only failure statuses", () => {
    const filter = toFailedSearchFilter({
      ...EMPTY_FAILED_FILTER,
      endpointId: ["Crm"],
      status: ["DeadLettered", "Completed"],
      errorText: "  timeout ",
      eventId: "",
    });

    expect(filter.endpointIds).toEqual(["Crm"]);
    expect(filter.statuses).toEqual([api.Statuses.DeadLettered]);
    expect(filter.errorText).toBe("timeout");
    expect(filter.eventId).toBeUndefined();
    expect(filter.updatedAtFrom).toBeUndefined();
  });

  it("narrows UpdatedAt to a half-open window", () => {
    const from = new Date("2026-09-25T10:00:00Z");
    const to = new Date("2026-09-25T11:00:00Z");

    const filter = toFailedSearchFilter(EMPTY_FAILED_FILTER, { from, to });

    expect(filter.updatedAtFrom?.toISOString()).toBe(
      "2026-09-25T10:00:00.000Z",
    );
    expect(filter.updatedAtTo?.toISOString()).toBe("2026-09-25T10:59:59.999Z");
  });
});

describe("selectedWindow", () => {
  it("prefers the window params", () => {
    const w = selectedWindow({
      ...EMPTY_FAILED_FILTER,
      windowStart: "2026-09-25T06:00:00.000Z",
      windowEnd: "2026-09-25T18:00:00.000Z",
      bucket: "2026-09-24T00:00:00.000Z",
    });
    expect(w?.from.toISOString()).toBe("2026-09-25T06:00:00.000Z");
    expect(w?.to.toISOString()).toBe("2026-09-25T18:00:00.000Z");
  });

  it("falls back to one bucket of the range for a legacy bucket link", () => {
    const w = selectedWindow({
      ...EMPTY_FAILED_FILTER,
      period: api.Period._7d,
      bucket: "2026-09-25T06:00:00.000Z",
    });
    expect(w?.from.toISOString()).toBe("2026-09-25T06:00:00.000Z");
    expect(w?.to.toISOString()).toBe("2026-09-25T12:00:00.000Z");
  });

  it("sizes a legacy bucket link by the custom range's bucket", () => {
    const w = selectedWindow({
      ...EMPTY_FAILED_FILTER,
      rangeStart: "2026-09-25T10:00:00.000Z",
      rangeEnd: "2026-09-25T14:00:00.000Z",
      bucket: "2026-09-25T11:00:00.000Z",
    });
    expect(w?.to.toISOString()).toBe("2026-09-25T11:05:00.000Z");
  });

  it("is undefined without a valid window", () => {
    expect(selectedWindow(EMPTY_FAILED_FILTER)).toBeUndefined();
    expect(
      selectedWindow({ ...EMPTY_FAILED_FILTER, bucket: "x" }),
    ).toBeUndefined();
    expect(
      selectedWindow({
        ...EMPTY_FAILED_FILTER,
        windowStart: "2026-09-25T18:00:00.000Z",
        windowEnd: "2026-09-25T06:00:00.000Z",
      }),
    ).toBeUndefined();
  });
});

describe("windowParams", () => {
  it("writes a window as ISO params and clears the legacy bucket", () => {
    expect(
      windowParams({
        from: new Date("2026-09-25T06:00:00Z"),
        to: new Date("2026-09-25T12:00:00Z"),
      }),
    ).toEqual({
      bucket: "",
      windowStart: "2026-09-25T06:00:00.000Z",
      windowEnd: "2026-09-25T12:00:00.000Z",
    });
    expect(windowParams(undefined)).toEqual({
      bucket: "",
      windowStart: "",
      windowEnd: "",
    });
  });
});

describe("parseSearchQuery", () => {
  const guid = "b5ec63b6-8a1f-4e02-9c7d-4f3a2b1e0d96";

  it("reads prefixed tokens into their fields and the rest as error text", () => {
    expect(
      parseSearchQuery("event:e1 message:m1 session:s1 503 timeout"),
    ).toEqual({
      eventId: "e1",
      lastMessageId: "m1",
      sessionId: "s1",
      errorText: "503 timeout",
    });
  });

  it("treats a bare GUID as an event ID", () => {
    expect(parseSearchQuery(`  ${guid} `)).toEqual({
      eventId: guid,
      lastMessageId: "",
      sessionId: "",
      errorText: "",
    });
  });

  it("keeps quoted text as error text, even when it looks like a GUID or a prefix", () => {
    expect(parseSearchQuery(`"${guid}"`).errorText).toBe(guid);
    expect(parseSearchQuery(`"event:x failed"`)).toEqual({
      eventId: "",
      lastMessageId: "",
      sessionId: "",
      errorText: "event:x failed",
    });
    expect(parseSearchQuery('session:"a b"').sessionId).toBe("a b");
  });

  it("clears every field for an empty query", () => {
    expect(parseSearchQuery("   ")).toEqual({
      eventId: "",
      lastMessageId: "",
      sessionId: "",
      errorText: "",
    });
  });
});

describe("searchQueryOf", () => {
  it("round-trips through parseSearchQuery", () => {
    const cases = [
      { eventId: "e1", lastMessageId: "", sessionId: "s 1", errorText: "503" },
      { eventId: "", lastMessageId: "m1", sessionId: "", errorText: "a:b" },
      {
        eventId: "",
        lastMessageId: "",
        sessionId: "",
        errorText: "b5ec63b6-8a1f-4e02-9c7d-4f3a2b1e0d96",
      },
      {
        eventId: "b5ec63b6-8a1f-4e02-9c7d-4f3a2b1e0d96",
        lastMessageId: "",
        sessionId: "",
        errorText: "",
      },
    ];
    for (const fields of cases) {
      const query = searchQueryOf({ ...EMPTY_FAILED_FILTER, ...fields });
      expect(parseSearchQuery(query)).toEqual(fields);
    }
  });

  it("is empty when no search field is set", () => {
    expect(searchQueryOf(EMPTY_FAILED_FILTER)).toBe("");
  });
});

describe("toggleStatus", () => {
  it("switches one status off from all, and back to all", () => {
    const off = toggleStatus([], "Failed");
    expect(off).toEqual(["DeadLettered", "Unsupported"]);
    expect(toggleStatus(off, "Failed")).toEqual([]);
  });
});

describe("errorTextOf", () => {
  it("prefers the handler error, then the dead-letter description", () => {
    const withError = new api.Event({
      messageContent: new api.MessageContent({
        errorContent: new api.ErrorContent({ errorText: "boom" }),
      }),
      deadLetterErrorDescription: "dl",
    });
    expect(errorTextOf(withError)).toBe("boom");
    expect(
      errorTextOf(new api.Event({ deadLetterErrorDescription: "dl" })),
    ).toBe("dl");
  });

  it("labels an Unsupported event without an error", () => {
    const unsupported = new api.Event({
      resolutionStatus: api.ResolutionStatus.Unsupported,
    });
    expect(errorTextOf(unsupported)).toMatch(/^Unsupported:/);
  });
});

describe("deferredCountsBySession", () => {
  it("counts deferred events per session", () => {
    expect(
      deferredCountsBySession(["e1_s1", "e2_s1", "e3_s2", "broken"]),
    ).toEqual({ s1: 2, s2: 1 });
  });
});
