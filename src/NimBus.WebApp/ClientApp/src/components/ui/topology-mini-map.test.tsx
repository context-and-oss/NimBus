import { afterEach, describe, expect, it } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { TopologyMiniMap } from "./topology-mini-map";

afterEach(() => {
  cleanup();
});

describe("TopologyMiniMap", () => {
  it("shows a routing label under each consumer that has one (Spec 045)", () => {
    render(
      <MemoryRouter>
        <TopologyMiniMap
          producers={["CrmEndpoint"]}
          consumers={["ErpInstance01", "ErpUnrouted", "Reporting"]}
          consumerNotes={{ ErpInstance01: "key 01", ErpUnrouted: "unmatched" }}
          centerLabel="CustomerCreated"
        />
      </MemoryRouter>,
    );

    expect(screen.getByText("key 01")).toBeDefined();
    expect(screen.getByText("unmatched")).toBeDefined();
    expect(screen.getByText("Reporting").parentElement?.textContent).not.toContain("key");
  });
});
