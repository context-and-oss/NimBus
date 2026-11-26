import { describe, expect, it } from "vitest";
import { ADAPTER_MCP_URL_PLACEHOLDER, BINDING_PLACEHOLDERS, validateAdapterMcpUrl, validateRecordBindings } from "./adapter-mcp";

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
    ).toBe("Binding 2: the path $.accountId is already bound for CrmAccountCreated in binding 1.");
  });

  // A row the user added and left empty must not read as filled in, and the message says which
  // row and which field.
  const valid = binding("CrmAccountCreated", "$.AccountId", "customer");

  it("names an empty new row by its number", () => {
    expect(validateRecordBindings([valid, binding("", "", "")])).toBe(
      "Binding 2 is empty: fill in the event type, the payload path and the entity, or delete it.",
    );
  });

  it.each([
    [binding("", "$.AccountId", "customer"), "Binding 1: the event type is empty."],
    [binding("CrmAccountCreated", "  ", "customer"), "Binding 1: the payload path is empty."],
    [binding("CrmAccountCreated", "$.AccountId", ""), "Binding 1: the entity is empty."],
    [binding("CrmAccountCreated", "", ""), "Binding 1: the payload path and the entity are empty."],
    [binding("", "$.AccountId", ""), "Binding 1: the event type and the entity are empty."],
  ])("names the empty fields of %o", (b, message) => {
    expect(validateRecordBindings([b])).toBe(message);
  });

  it("names the row and the field of an invalid value", () => {
    expect(validateRecordBindings([valid, binding("CrmAccountCreated", "AccountId", "customer")])).toBe(
      "Binding 2: the payload path 'AccountId' must be a dotted path such as $.AccountId, without wildcards, indexes or filters.",
    );
    expect(validateRecordBindings([binding("CrmAccountCreated", "$.AccountId", "customer record")])).toBe(
      "Binding 1: the entity 'customer record' must be a name of letters, digits, '.', '_' or '-'.",
    );
    expect(validateRecordBindings([binding("x".repeat(201), "$.AccountId", "customer")])).toBe(
      "Binding 1: the event type is longer than 200 characters.",
    );
  });
});

describe("example placeholders", () => {
  it("read as examples, never as values", () => {
    expect(BINDING_PLACEHOLDERS).toEqual({ eventTypeId: "e.g. CrmAccountCreated", payloadPath: "e.g. $.AccountId", entity: "e.g. customer" });
    expect(ADAPTER_MCP_URL_PLACEHOLDER).toBe("e.g. https://your-adapter.example.com/api/mcp");
  });
});
