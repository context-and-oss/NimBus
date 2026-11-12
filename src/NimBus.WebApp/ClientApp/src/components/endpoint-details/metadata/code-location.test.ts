import { describe, expect, it } from "vitest";
import { ticketLink, validateRepositoryUrl, validateSourceSystem, validateTicketLinkTemplate } from "./code-location";

describe("endpoint code location", () => {
  it("accepts a plain https repository and an empty one", () => {
    expect(validateRepositoryUrl("https://github.com/example/crm-adapter")).toBeUndefined();
    expect(validateRepositoryUrl("")).toBeUndefined();
    expect(validateRepositoryUrl(undefined)).toBeUndefined();
  });

  it.each([
    ["http://github.com/example/adapter", "absolute https"],
    ["git@github.com:example/adapter.git", "absolute https"],
    ["https://user:secret@github.com/example/adapter", "credentials"],
    ["https://github.com/example/adapter?token=1", "query string"],
    ["https://github.com/example/adapter#readme", "fragment"],
  ])("refuses the repository %s", (url, reason) => {
    expect(validateRepositoryUrl(url)).toContain(reason);
  });

  it("needs the placeholder and an https address in a ticket link", () => {
    expect(validateTicketLinkTemplate("https://jira.example.com/browse/{ticketId}")).toBeUndefined();
    expect(validateTicketLinkTemplate("https://jira.example.com/browse/X-1")).toContain("{ticketId}");
    expect(validateTicketLinkTemplate("javascript:alert({ticketId})")).toContain("https");
  });

  it("builds a ticket's address only from a valid template", () => {
    expect(ticketLink("https://github.com/example/adapter/issues/{ticketId}", "42")).toBe("https://github.com/example/adapter/issues/42");
    expect(ticketLink("javascript:{ticketId}", "42")).toBeUndefined();
    expect(ticketLink(undefined, "42")).toBeUndefined();
  });

  it("bounds the source system", () => {
    expect(validateSourceSystem("Dynamics 365 Sales")).toBeUndefined();
    expect(validateSourceSystem("x".repeat(201))).toContain("200");
  });
});
