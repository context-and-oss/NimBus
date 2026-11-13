import { describe, expect, it } from "vitest";
import {
  normalizeTicketId,
  reportedCellState,
  ticketUrl,
} from "./reported.functions";

describe("normalizeTicketId", () => {
  it("trims and accepts common ticket shapes", () => {
    expect(normalizeTicketId("  INC0428771 ")).toBe("INC0428771");
    expect(normalizeTicketId("JIRA-1234")).toBe("JIRA-1234");
    expect(normalizeTicketId("ops.42_a")).toBe("ops.42_a");
  });

  it("returns null for empty input (reported without a ticket)", () => {
    expect(normalizeTicketId("")).toBeNull();
    expect(normalizeTicketId("   ")).toBeNull();
  });

  it("returns undefined for invalid input (validation error)", () => {
    expect(normalizeTicketId("has spaces")).toBeUndefined();
    expect(normalizeTicketId("-leading-dash")).toBeUndefined();
    expect(normalizeTicketId("a".repeat(65))).toBeUndefined();
  });
});

describe("ticketUrl", () => {
  it("fills an endpoint template's {ticketId} placeholder (Spec 044)", () => {
    expect(ticketUrl("https://github.com/example/adapter/issues/{ticketId}", "42")).toBe(
      "https://github.com/example/adapter/issues/42",
    );
  });

  it("substitutes the {ticket} placeholder, URL-encoded", () => {
    expect(ticketUrl("https://x/browse/{ticket}", "JIRA-1")).toBe(
      "https://x/browse/JIRA-1",
    );
  });

  it("returns undefined when no template is configured", () => {
    expect(ticketUrl(undefined, "JIRA-1")).toBeUndefined();
    expect(ticketUrl("", "JIRA-1")).toBeUndefined();
  });
});

describe("reportedCellState", () => {
  it("renders the Report button when not reported", () => {
    expect(reportedCellState({ isReported: false })).toEqual({
      kind: "report",
    });
  });

  it("renders a ticket chip with deep-link when reported with a ticket", () => {
    const state = reportedCellState({
      isReported: true,
      ticketId: "INC1",
      reportedBy: "alice",
      reportedAtFormatted: "01.01.26",
      ticketLinkTemplate: "https://x/{ticket}",
    });
    expect(state).toEqual({
      kind: "ticket",
      ticketId: "INC1",
      href: "https://x/INC1",
      tooltip: "Reported by alice · 01.01.26",
    });
  });

  it("renders a plain badge when reported without a ticket", () => {
    const state = reportedCellState({ isReported: true, reportedBy: "bob" });
    expect(state.kind).toBe("done");
    if (state.kind === "done") {
      expect(state.tooltip).toContain("no ticket");
    }
  });
});
