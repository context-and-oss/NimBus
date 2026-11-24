import { afterEach, describe, expect, it } from "vitest";
import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter, Route, Routes, useLocation } from "react-router-dom";
import InsightsRedirect, { failedUrlForInsights } from "./insights-redirect";

afterEach(cleanup);

describe("failedUrlForInsights", () => {
  it("lands on the error grouping by default", () => {
    expect(failedUrlForInsights("")).toBe("/Failed?view=error");
  });

  it.each([
    ["cause", "cause"],
    ["guidance", "guidance"],
    ["origin", "origin"],
    ["patterns", "error"],
    ["nonsense", "error"],
  ])("maps by=%s to view=%s", (by, view) => {
    expect(failedUrlForInsights(`?by=${by}`)).toBe(`/Failed?view=${view}`);
  });

  it("carries the preset range and the endpoint over", () => {
    expect(
      failedUrlForInsights("?by=guidance&endpoint=Crm&period=7d"),
    ).toBe("/Failed?view=guidance&period=7d&endpointId=Crm");
  });

  it("carries a custom range over instead of the preset", () => {
    const params = new URLSearchParams(
      failedUrlForInsights(
        "?period=1d&from=2026-11-01T00:00:00.000Z&to=2026-11-02T00:00:00.000Z",
      ).split("?")[1],
    );

    expect(params.get("rangeStart")).toBe("2026-11-01T00:00:00.000Z");
    expect(params.get("rangeEnd")).toBe("2026-11-02T00:00:00.000Z");
    expect(params.has("period")).toBe(false);
  });

  it("drops an opened group, whose key named failed attempts", () => {
    expect(failedUrlForInsights("?by=cause&group=notAnalyzed&panel=patterns")).toBe(
      "/Failed?view=cause",
    );
  });
});

describe("InsightsRedirect", () => {
  it("replaces an old Insights link with the Failed page", () => {
    const Where = () => {
      const location = useLocation();
      return <span>{location.pathname + location.search}</span>;
    };
    render(
      <MemoryRouter initialEntries={["/Insights?by=cause&endpoint=Erp"]}>
        <Routes>
          <Route path="/Insights" element={<InsightsRedirect />} />
          <Route path="/Failed" element={<Where />} />
        </Routes>
      </MemoryRouter>,
    );

    expect(screen.getByText("/Failed?view=cause&endpointId=Erp")).toBeTruthy();
  });
});
