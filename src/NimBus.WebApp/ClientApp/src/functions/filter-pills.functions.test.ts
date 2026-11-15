import { describe, expect, it } from "vitest";
import * as api from "api-client";
import {
  idQueryOf,
  parseIdQuery,
  presetStart,
  timeRangeLabel,
  toLocalInput,
} from "./filter-pills.functions";

const PREFIXES = {
  event: "eventId",
  message: "messageId",
  session: "sessionId",
} as const;
const GUID = "583f1f5f-1c2d-4e5f-8a9b-0c1d2e3f4a5b";

describe("parseIdQuery", () => {
  it("reads prefixed IDs", () => {
    expect(parseIdQuery("session:abc message:m1", PREFIXES, "eventId")).toEqual(
      { eventId: "", messageId: "m1", sessionId: "abc" },
    );
  });

  it("reads an unprefixed value as the bare field", () => {
    expect(parseIdQuery(` ${GUID} `, PREFIXES, "eventId")).toEqual({
      eventId: GUID,
      messageId: "",
      sessionId: "",
    });
  });

  it("reads quoted values and ignores prefix case", () => {
    expect(
      parseIdQuery('Session:"order 42" event:e1', PREFIXES, "eventId"),
    ).toEqual({ eventId: "e1", messageId: "", sessionId: "order 42" });
  });

  it("treats an unknown prefix as bare text", () => {
    expect(parseIdQuery("foo:bar", PREFIXES, "eventId").eventId).toBe(
      "foo:bar",
    );
  });

  it("clears every field for an empty query", () => {
    expect(parseIdQuery("   ", PREFIXES, "eventId")).toEqual({
      eventId: "",
      messageId: "",
      sessionId: "",
    });
  });
});

describe("idQueryOf", () => {
  it("writes the applied IDs back as prefixed tokens", () => {
    expect(
      idQueryOf(
        { eventId: "e1", messageId: "", sessionId: "order 42" },
        PREFIXES,
      ),
    ).toBe('event:e1 session:"order 42"');
  });

  it("round-trips through parseIdQuery", () => {
    const values = { eventId: GUID, messageId: "m 1", sessionId: "s1" };
    expect(
      parseIdQuery(idQueryOf(values, PREFIXES), PREFIXES, "eventId"),
    ).toEqual(values);
  });
});

describe("presetStart", () => {
  const now = new Date("2026-11-15T12:00:00Z");

  it("is undefined for any time", () => {
    expect(presetStart("", now)).toBeUndefined();
  });

  it("steps back the preset's span from now", () => {
    expect(presetStart(api.Period._7d, now)?.toISOString()).toBe(
      "2026-11-08T12:00:00.000Z",
    );
    expect(presetStart(api.Period._1h, now)?.toISOString()).toBe(
      "2026-11-15T11:00:00.000Z",
    );
  });

  it("is undefined for an unknown preset", () => {
    expect(presetStart("bogus", now)).toBeUndefined();
  });
});

describe("timeRangeLabel", () => {
  it("names a preset", () => {
    expect(timeRangeLabel({ period: api.Period._7d, from: "", to: "" })).toBe(
      "Last 7 days",
    );
  });

  it("reads any time when nothing is set", () => {
    expect(timeRangeLabel({ period: "", from: "", to: "" })).toBe("Any time");
  });

  it("formats a custom range, including open-ended ones", () => {
    expect(
      timeRangeLabel({
        period: "",
        from: "2026-11-10T08:00",
        to: "2026-11-11T09:30",
      }),
    ).toBe("10/11/2026 08:00 – 11/11/2026 09:30");
    expect(
      timeRangeLabel({ period: "", from: "2026-11-10T08:00", to: "" }),
    ).toBe("From 10/11/2026 08:00");
    expect(
      timeRangeLabel({ period: "", from: "", to: "2026-11-11T09:30" }),
    ).toBe("Until 11/11/2026 09:30");
  });

  it("prefers an explicit range over a preset", () => {
    expect(
      timeRangeLabel({
        period: api.Period._7d,
        from: "2026-11-10T08:00",
        to: "",
      }),
    ).toBe("From 10/11/2026 08:00");
  });
});

describe("toLocalInput", () => {
  it("formats a date for a datetime-local input in local time", () => {
    expect(toLocalInput(new Date(2026, 10, 15, 7, 5))).toBe("2026-11-15T07:05");
  });
});
