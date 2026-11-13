import { cleanup, render, screen } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import App from "./app";

/**
 * A wall opened as /Monitor#token=… may call the Monitor feed and nothing else: any other API
 * would answer 401 to the token and, worse, could carry a signed-in user's cookies. This mounts
 * the whole app, providers included, and records every request it makes.
 */
describe("App on the Monitor with a dashboard token", () => {
  const requests: { url: string; token: string | null; credentials?: RequestCredentials }[] = [];

  beforeEach(() => {
    requests.length = 0;
    Object.defineProperty(window, "matchMedia", {
      configurable: true,
      writable: true,
      value: vi.fn().mockImplementation((query: string) => ({
        matches: false,
        media: query,
        addEventListener: vi.fn(),
        removeEventListener: vi.fn(),
      })),
    });
    window.history.replaceState(null, "", "/Monitor#token=nbdt_wall");
    vi.stubGlobal(
      "fetch",
      vi.fn((input: RequestInfo, init?: RequestInit) => {
        const url = typeof input === "string" ? input : input.url;
        requests.push({
          url,
          token: new Headers(init?.headers).get("X-NimBus-Dashboard-Token"),
          credentials: init?.credentials,
        });
        const body = url.includes("/feed/status")
          ? { endpoints: [], tokenAccess: true, stale: false }
          : url.includes("/feed/metrics")
            ? { lastHour: [], endpoints: [] }
            : { lastThreeDays: [], endpoints: [] };
        return Promise.resolve(new Response(JSON.stringify(body), { status: 200 }));
      }),
    );
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    window.history.replaceState(null, "", "/");
    cleanup();
  });

  it("calls only the three feed operations, each with the token and without cookies", async () => {
    render(
      <MemoryRouter initialEntries={["/Monitor"]}>
        <App />
      </MemoryRouter>,
    );

    expect(await screen.findByText(/read-only dashboard/, {}, { timeout: 5000 })).toBeTruthy();
    // Let every effect that might fetch on mount run.
    await new Promise((resolve) => setTimeout(resolve, 50));

    const paths = new Set(requests.map((r) => new URL(r.url, "https://localhost").pathname));
    expect([...paths].sort()).toEqual([
      "/api/v1/monitor/feed/history",
      "/api/v1/monitor/feed/metrics",
      "/api/v1/monitor/feed/status",
    ]);
    expect(requests.every((r) => r.token === "nbdt_wall" && r.credentials === "omit")).toBe(true);
  });
});
