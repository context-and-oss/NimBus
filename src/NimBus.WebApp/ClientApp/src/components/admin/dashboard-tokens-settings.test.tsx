import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import DashboardTokensSettings from "./dashboard-tokens-settings";
import {
  expiryFrom,
  monitorUrl,
  validateName,
  type DashboardTokenInfo,
  type DashboardTokensState,
} from "./dashboard-tokens-model";

const originalFetch = globalThis.fetch;
afterEach(() => {
  cleanup();
  globalThis.fetch = originalFetch;
});

const SECRET = "nbdt_zyxwvutsrqpo_SECRETSECRETSECRETSECRETSECRETSECRETSEC";

const token = (overrides: Partial<DashboardTokenInfo> = {}): DashboardTokenInfo => ({
  tokenId: "abcdefghijkl",
  prefix: "nbdt_abcdefghijkl",
  name: "Operations wall",
  status: "active",
  createdAtUtc: "2026-11-10T08:00:00Z",
  createdBy: "owner@contoso.com",
  expiresAtUtc: null,
  revokedAtUtc: null,
  revokedBy: null,
  lastUsedAtUtc: "2026-11-10T09:30:00Z",
  ...overrides,
});

const state = (tokens: DashboardTokenInfo[] = [token()]): DashboardTokensState => ({ tokens, csrfToken: "csrf-test" });

function mockFetch(...responses: Response[]) {
  const fetchMock = vi.fn();
  for (const r of responses) fetchMock.mockResolvedValueOnce(r);
  globalThis.fetch = fetchMock as typeof fetch;
  return fetchMock;
}

describe("dashboard tokens model", () => {
  it("requires a name of at most 100 characters", () => {
    expect(validateName("Operations wall")).toBeUndefined();
    expect(validateName("  ")).toBeTruthy();
    expect(validateName("x".repeat(101))).toBeTruthy();
  });

  it("turns an expiry choice into a UTC timestamp", () => {
    const now = new Date("2026-11-10T12:00:00Z");
    expect(expiryFrom("never", now)).toBeNull();
    expect(expiryFrom("365", now)).toBe("2027-11-10T12:00:00.000Z");
  });

  it("puts the token in the URL fragment, which browsers never send to the server", () => {
    expect(monitorUrl("nbdt_abc_def", "https://nimbus.example")).toBe("https://nimbus.example/Monitor#token=nbdt_abc_def");
  });
});

describe("Dashboard tokens settings", () => {
  it("lists tokens with their status and last use", async () => {
    mockFetch(
      Response.json(
        state([token(), token({ tokenId: "mnopqrstuvwx", prefix: "nbdt_mnopqrstuvwx", name: "Old wall", status: "revoked", lastUsedAtUtc: null })]),
      ),
    );
    render(<DashboardTokensSettings />);

    const rows = await screen.findAllByRole("row");
    const active = rows.find((r) => within(r).queryByText("Operations wall"))!;
    expect(within(active).getByText("nbdt_abcdefghijkl")).toBeTruthy();
    expect(within(active).getByRole("button", { name: "Revoke token Operations wall" })).toBeTruthy();
    const old = rows.find((r) => within(r).queryByText("Old wall"))!;
    expect(within(old).getByText("Revoked")).toBeTruthy();
    expect(within(old).getByRole("button", { name: "Remove token Old wall" })).toBeTruthy();
  });

  it("issues a token, with a one-year expiry by default, and shows the Monitor link once", async () => {
    const issued = token({ tokenId: "zyxwvutsrqpo", prefix: "nbdt_zyxwvutsrqpo", name: "Lobby", lastUsedAtUtc: null });
    const fetchMock = mockFetch(
      Response.json(state([])),
      Response.json({ token: SECRET, issued, state: state([issued]) }),
    );
    const user = userEvent.setup();
    render(<DashboardTokensSettings />);

    await user.type(await screen.findByLabelText("Name"), "Lobby");
    await user.click(screen.getByRole("button", { name: "Issue token" }));

    const [url, init] = fetchMock.mock.calls[1];
    expect(url).toBe("/api/admin/dashboard-tokens");
    expect(init.method).toBe("POST");
    expect(init.headers["X-NimBus-CSRF"]).toBe("csrf-test");
    const body = JSON.parse(init.body);
    expect(body.name).toBe("Lobby");
    expect(new Date(body.expiresAtUtc).getTime() - Date.now()).toBeGreaterThan(364 * 24 * 3600 * 1000);

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(monitorUrl(SECRET))).toBeTruthy();
    expect(within(dialog).getByText(/won't be shown again/)).toBeTruthy();
    expect(within(dialog).getByText(/The link is the credential/)).toBeTruthy();

    await user.click(within(dialog).getByRole("button", { name: "Done" }));
    expect(screen.queryByText(/SECRETSECRET/)).toBeNull();
  });

  it("checks the name before sending", async () => {
    const fetchMock = mockFetch(Response.json(state([])));
    const user = userEvent.setup();
    render(<DashboardTokensSettings />);

    await user.click(await screen.findByRole("button", { name: "Issue token" }));

    expect(screen.getByText(/Give the token a name/)).toBeTruthy();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("revokes only after confirmation", async () => {
    const fetchMock = mockFetch(Response.json(state()), Response.json(state([token({ status: "revoked" })])));
    const user = userEvent.setup();
    render(<DashboardTokensSettings />);

    await user.click(await screen.findByRole("button", { name: "Revoke token Operations wall" }));
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Revoke token" }));

    const [url, init] = fetchMock.mock.calls[1];
    expect(url).toBe("/api/admin/dashboard-tokens/abcdefghijkl/revoke");
    expect(init.method).toBe("POST");
    expect(await screen.findByText("Revoked")).toBeTruthy();
  });

  it("explains that the storage provider cannot store tokens", async () => {
    mockFetch(new Response(null, { status: 501 }));
    render(<DashboardTokensSettings />);

    expect((await screen.findByRole("alert")).textContent).toContain("cannot store dashboard tokens");
  });

  it("explains that only site Owners can manage tokens", async () => {
    mockFetch(new Response(null, { status: 403 }));
    render(<DashboardTokensSettings />);

    expect((await screen.findByRole("alert")).textContent).toContain("Only site Owners");
  });
});
