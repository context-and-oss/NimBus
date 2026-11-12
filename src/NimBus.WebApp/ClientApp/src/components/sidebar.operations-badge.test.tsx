import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor } from "@testing-library/react";
import { MemoryRouter } from "react-router-dom";

// Spec 038 §5.1: Operations shows how many endpoints have receive paused, from the
// status counts the sidebar already polls for the Failed badge (no extra request).
const stubFetch = (counts: unknown) => {
  const mock = vi.fn((url: RequestInfo | URL) => {
    const path = String(url);
    const body = path.endsWith("/api/v1/endpoint/status/count")
      ? counts
      : path.endsWith("/api/v1/access-control/me")
        ? { canManageAccessControl: true, endpointRoles: [] }
        : {};
    return Promise.resolve(
      new Response(JSON.stringify(body), {
        status: 200,
        headers: { "Content-Type": "application/json" },
      }),
    );
  });
  vi.stubGlobal("fetch", mock);
  window.fetch = mock as unknown as typeof fetch;
  return mock;
};

async function renderSidebar(path = "/Endpoints") {
  const { default: Sidebar } = await import("components/sidebar");
  render(
    <MemoryRouter initialEntries={[path]}>
      <Sidebar />
    </MemoryRouter>,
  );
}

describe("Sidebar Operations badge", () => {
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

  it("counts endpoints whose receive is disabled, and nothing else", async () => {
    stubFetch([
      { endpointId: "a", subscriptionStatus: "disabled" },
      { endpointId: "b", subscriptionStatus: "disabled" },
      { endpointId: "c", subscriptionStatus: "active" },
      // Unknown: the metadata probe failed. Never counted as paused.
      { endpointId: "d", subscriptionStatus: null },
      { endpointId: "e" },
    ]);
    await renderSidebar();

    await waitFor(() => expect(screen.getByLabelText("2 endpoints paused")).toBeTruthy());
    expect(screen.getByRole("link", { name: /Operations/ }).textContent).toContain("2 paused");
  });

  it("shows no badge when nothing is paused", async () => {
    const fetch = stubFetch([{ endpointId: "a", subscriptionStatus: "active" }]);
    await renderSidebar();

    await screen.findByRole("link", { name: /Operations/ });
    await waitFor(() =>
      expect(fetch.mock.calls.some(([u]) => String(u).endsWith("/api/v1/endpoint/status/count"))).toBe(true),
    );
    expect(screen.queryByLabelText(/endpoints? paused/)).toBeNull();
  });

  it("keeps Operations highlighted on an operation's URL", async () => {
    stubFetch([]);
    await renderSidebar("/Operations/dlq");

    const link = await screen.findByRole("link", { name: /Operations/ });
    expect(link.getAttribute("aria-current")).toBe("page");
  });
});
