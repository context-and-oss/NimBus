import { describe, expect, it } from "vitest";
import { manageResults } from "./manage-results";

const titles = (query: string, storageProvider?: string) =>
  manageResults(query, storageProvider).map((r) => `${r.kind}: ${r.title} → ${r.route}`);

// Spec 038 §10: site Owners can jump to the Manage pages, operations and settings.
describe("manageResults", () => {
  it("returns nothing for an empty query", () => {
    expect(manageResults("  ")).toEqual([]);
  });

  it("finds a setting's tab by what it contains, not just its name", () => {
    expect(titles("rate")).toEqual(
      expect.arrayContaining([
        "setting: MCP access › Limits → /Settings/mcp?tab=limits",
        "setting: Simulation › Simulate mode → /Settings/simulation?tab=mode",
      ]),
    );
  });

  it("finds a feature by name", () => {
    expect(titles("failure intel")).toContain(
      "setting: Failure intelligence → /Settings/failure-intelligence",
    );
  });

  it("finds an operation", () => {
    expect(titles("dead-letter")).toContain("operation: Delete dead-lettered → /Operations/dlq");
  });

  it("finds the pages and the Topology views", () => {
    expect(titles("drift")).toContain("page: Topology › Catalog drift → /Topology/drift");
    expect(titles("operations")).toContain("page: Operations → /Operations");
  });

  it("offers the Storage view only on Cosmos DB, like the Topology page", () => {
    const storage = "page: Topology › Storage → /Topology/storage";
    expect(titles("storage", "Cosmos DB")).toContain(storage);
    expect(titles("storage", "SQL Server")).not.toContain(storage);
    expect(titles("storage")).not.toContain(storage);
  });

  it("lists pages, then operations, then settings", () => {
    const kinds = manageResults("e").map((r) => r.kind);
    const order = ["page", "operation", "setting"];
    expect([...kinds].sort((a, b) => order.indexOf(a) - order.indexOf(b))).toEqual(kinds);
  });

  it("caps each section", () => {
    const counts = manageResults("e").reduce<Record<string, number>>(
      (acc, r) => ({ ...acc, [r.kind]: (acc[r.kind] ?? 0) + 1 }),
      {},
    );
    for (const count of Object.values(counts)) expect(count).toBeLessThanOrEqual(8);
  });
});
