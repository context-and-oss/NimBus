import { describe, expect, it } from "vitest";
import { validateAdapterMcpUrl, validateRecordBindings } from "./adapter-mcp";

describe("adapter MCP address", () => {
  it("accepts a plain https address and an empty one", () => {
    expect(validateAdapterMcpUrl("https://erp-adapter.example.com/api/mcp")).toBeUndefined();
    expect(validateAdapterMcpUrl("")).toBeUndefined();
    expect(validateAdapterMcpUrl(undefined)).toBeUndefined();
  });

  it.each([
    ["http://erp-adapter.example.com/api/mcp", "absolute https"],
    ["erp-adapter.example.com/api/mcp", "absolute https"],
    ["https://user:secret@erp-adapter.example.com/api/mcp", "credentials"],
    ["https://erp-adapter.example.com/api/mcp?code=1", "query string"],
  ])("refuses %s", (url, reason) => {
    expect(validateAdapterMcpUrl(url)).toContain(reason);
  });
});

describe("record bindings", () => {
  const binding = (eventTypeId: string, payloadPath: string, entity: string) => ({ eventTypeId, payloadPath, entity });

  it("accepts dotted paths and none at all", () => {
    expect(validateRecordBindings([binding("CrmAccountCreated", "$.AccountId", "customer")])).toBeUndefined();
    expect(validateRecordBindings([binding("CrmContactCreated", "$.Contact.Id", "contact")])).toBeUndefined();
    expect(validateRecordBindings([])).toBeUndefined();
  });

  it.each([
    [binding("", "$.AccountId", "customer"), "event type"],
    [binding("CrmAccountCreated", "AccountId", "customer"), "dotted path"],
    [binding("CrmAccountCreated", "$..AccountId", "customer"), "dotted path"],
    [binding("CrmAccountCreated", "$.Lines[0].Id", "customer"), "dotted path"],
    [binding("CrmAccountCreated", "$.AccountId", ""), "entity"],
    [binding("CrmAccountCreated", "$.AccountId", "customer record"), "entity"],
  ])("refuses %o", (b, reason) => {
    expect(validateRecordBindings([b])).toContain(reason);
  });

  it("refuses the same path bound twice for one event type", () => {
    expect(
      validateRecordBindings([binding("CrmAccountCreated", "$.AccountId", "customer"), binding("CrmAccountCreated", "$.accountId", "account")]),
    ).toContain("twice");
  });
});
