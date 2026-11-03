import { describe, it, expect } from "vitest";
import moment from "moment";
import {
  formatMoment,
  formatResolutionStatus,
  parseBlockedByEventId,
} from "./endpoint.functions";

describe("formatResolutionStatus", () => {
  it("distinguishes only a skipped event with the exact duplicate reason token", () => {
    expect(formatResolutionStatus("Skipped", "DuplicateDetected")).toBe(
      "Skipped (duplicate)",
    );
    expect(formatResolutionStatus("Skipped", "Operator requested skip")).toBe(
      "Skipped",
    );
    expect(formatResolutionStatus("Skipped", "duplicatedetected")).toBe(
      "Skipped",
    );
    expect(formatResolutionStatus("Completed", "DuplicateDetected")).toBe(
      "Completed",
    );
  });
});

// Spec 006 — parser unit tests. The function extracts the blocking event GUID
// out of a StrictMessageHandler-formatted deferral error text. It must be a
// pure, single-pass regex with no logging and no throwing.

const CANONICAL_GUID = "cce3b12a-1234-5678-9abc-def012345678";

describe("parseBlockedByEventId", () => {
  it("extracts the GUID from the canonical 'Session N is blocked by {GUID}' phrase", () => {
    const result = parseBlockedByEventId(
      `Session 0f9e8d7c-1111-2222-3333-444455556666 is blocked by ${CANONICAL_GUID}`,
    );
    expect(result).toBe(CANONICAL_GUID);
  });

  it("extracts the GUID when the canonical phrase is prefix-wrapped by additional context", () => {
    const result = parseBlockedByEventId(
      `Deferral reason: Session N is blocked by ${CANONICAL_GUID}`,
    );
    expect(result).toBe(CANONICAL_GUID);
  });

  it("extracts the GUID when the canonical phrase has trailing/suffix content", () => {
    const result = parseBlockedByEventId(
      `Session N is blocked by ${CANONICAL_GUID}. Retry scheduled for later.`,
    );
    expect(result).toBe(CANONICAL_GUID);
  });

  it("matches the anchor phrase case-insensitively", () => {
    const result = parseBlockedByEventId(
      `SESSION foo IS BLOCKED BY ${CANONICAL_GUID}`,
    );
    expect(result).toBe(CANONICAL_GUID);
  });

  it("returns the GUID exactly as it appears in input (no lowercasing / normalization)", () => {
    const mixed = "CCE3B12A-1234-5678-9abc-DEF012345678";
    const result = parseBlockedByEventId(`Session N is blocked by ${mixed}`);
    expect(result).toBe(mixed);
  });

  it("returns undefined for null input without throwing", () => {
    expect(() => parseBlockedByEventId(null)).not.toThrow();
    expect(parseBlockedByEventId(null)).toBeUndefined();
  });

  it("returns undefined for undefined input without throwing", () => {
    expect(() => parseBlockedByEventId(undefined)).not.toThrow();
    expect(parseBlockedByEventId(undefined)).toBeUndefined();
  });

  it("returns undefined for empty string input", () => {
    expect(parseBlockedByEventId("")).toBeUndefined();
  });

  it("returns undefined when the phrase is present but the GUID is malformed (truncated)", () => {
    // Missing the last 12-char block — should not partially match.
    const result = parseBlockedByEventId(
      `Session N is blocked by cce3b12a-1234-5678-9abc-def`,
    );
    expect(result).toBeUndefined();
  });

  it("returns undefined when the phrase is present but the GUID contains non-hex characters", () => {
    const result = parseBlockedByEventId(
      `Session N is blocked by xxx3b12a-1234-5678-9abc-def012345678`,
    );
    expect(result).toBeUndefined();
  });

  it("returns undefined when the error text does not contain the canonical phrase", () => {
    const result = parseBlockedByEventId(
      `Custom adapter said: session unavailable, please retry.`,
    );
    expect(result).toBeUndefined();
  });
});

describe("formatMoment", () => {
  const ts = moment("2026-05-28T10:04:05.123");

  it("omits milliseconds in the slim format used by message listings", () => {
    expect(formatMoment(ts, true)).toBe("28/05/26 10:04:05");
  });

  it("keeps milliseconds in the full format", () => {
    expect(formatMoment(ts)).toBe("28/05/2026 10:04:05.123");
  });
});
