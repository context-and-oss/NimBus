import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// The Failed item carries the unresolved-failure backlog across endpoints, summed from
// /api/endpoint/status/count. Every other request is answered `{}`.
const stubFetch = (counts: unknown) => {
  const mock = vi.fn((url: RequestInfo | URL) =>
    Promise.resolve(
      new Response(
        JSON.stringify(
          String(url).endsWith("/api/endpoint/status/count") ? counts : {},
        ),
        { status: 200, headers: { "Content-Type": "application/json" } },
      ),
    ),
  );
  vi.stubGlobal("fetch", mock);
  window.fetch = mock as unknown as typeof fetch;
};

describe("Sidebar Failed badge", () => {
  beforeEach(() => {
    vi.resetModules();
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
  });

  afterEach(() => {
    cleanup();
    vi.unstubAllGlobals();
  });

  it("sums failed (which already includes dead-lettered and unsupported) across endpoints", async () => {
    stubFetch([
      {
        endpointId: "Crm",
        failedCount: 22,
        deadletterCount: 4,
        unsupportedCount: 2,
        deferredCount: 9,
      },
      {
        endpointId: "Erp",
        failedCount: 1,
        deadletterCount: 0,
        unsupportedCount: 0,
      },
    ]);
    const { default: Sidebar } = await import("components/sidebar");

    render(
      <MemoryRouter>
        <Sidebar />
      </MemoryRouter>,
    );

    await waitFor(() =>
      expect(screen.getByLabelText("23 unresolved failures")).toBeTruthy(),
    );
    expect(
      screen.getByRole("link", { name: /Failed/ }).getAttribute("href"),
    ).toBe("/Failed");
  });

  it("shows no badge when there are no failures", async () => {
    stubFetch([
      {
        endpointId: "Crm",
        failedCount: 0,
        deadletterCount: 0,
        unsupportedCount: 0,
      },
    ]);
    const { default: Sidebar } = await import("components/sidebar");

    render(
      <MemoryRouter>
        <Sidebar />
      </MemoryRouter>,
    );

    await waitFor(() =>
      expect(screen.getByRole("link", { name: /Failed/ })).toBeTruthy(),
    );
    expect(screen.queryByLabelText(/unresolved failures/)).toBeNull();
  });
});
