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

const table = () => screen.getByRole("table", { name: "Dashboard tokens" });
const inspector = () => screen.getByRole("complementary", { name: "Inspector" });
/** Selects a token's row, which puts the token and its action in the inspector. */
const selectToken = async (user: ReturnType<typeof userEvent.setup>, name: string) =>
  user.click(await within(await screen.findByRole("table", { name: "Dashboard tokens" })).findByRole("button", { name }));

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
    const user = userEvent.setup();
    render(<DashboardTokensSettings />);

    const rows = await screen.findAllByRole("row");
    const active = rows.find((r) => within(r).queryByText("Operations wall"))!;
    expect(within(active).getByText("nbdt_abcdefghijkl")).toBeTruthy();
    await selectToken(user, "Operations wall");
    expect(within(inspector()).getByRole("button", { name: "Revoke token Operations wall" })).toBeTruthy();
    const old = rows.find((r) => within(r).queryByText("Old wall"))!;
    expect(within(old).getByText("Revoked")).toBeTruthy();
    await selectToken(user, "Old wall");
    expect(within(inspector()).getByRole("button", { name: "Remove token Old wall" })).toBeTruthy();
  });

  it("keeps the actions out of the table", async () => {
    mockFetch(Response.json(state()));
    render(<DashboardTokensSettings />);

    const headers = within(await screen.findByRole("table", { name: "Dashboard tokens" })).getAllByRole("columnheader");
    expect(headers.map((h) => h.textContent)).toEqual(["Name", "Token", "Status", "Issued", "Last used", "Expires"]);
    expect(within(table()).queryByRole("button", { name: /Revoke|Remove/ })).toBeNull();
  });

  it("shows the issue form until a token is selected, then that token's facts and action", async () => {
    mockFetch(Response.json(state()));
    const user = userEvent.setup();
    render(<DashboardTokensSettings />);

    await screen.findByRole("table", { name: "Dashboard tokens" });
    expect(within(inspector()).getByRole("heading", { name: "Issue token" })).toBeTruthy();
    expect(within(inspector()).getByLabelText("Name")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /Revoke token|Remove token/ })).toBeNull();

    await selectToken(user, "Operations wall");

    expect(within(table()).getByRole("button", { name: "Operations wall" }).getAttribute("aria-pressed")).toBe("true");
    expect(within(inspector()).getByRole("heading", { name: "Operations wall" })).toBeTruthy();
    expect(within(inspector()).getByText("Dashboard token")).toBeTruthy();
    expect(within(inspector()).getByText("Active")).toBeTruthy();
    expect(within(inspector()).getByText("nbdt_abcdefghijkl")).toBeTruthy();
    expect(within(inspector()).getByText("owner@contoso.com")).toBeTruthy();
    expect(within(inspector()).getByText("No expiry")).toBeTruthy();
    expect(within(inspector()).getByRole("button", { name: "Revoke token Operations wall" })).toBeTruthy();
    expect(screen.queryByLabelText("Name")).toBeNull();
  });

  it("returns to the issue form, with its draft, from New token", async () => {
    mockFetch(Response.json(state()));
    const user = userEvent.setup();
    render(<DashboardTokensSettings />);

    await user.type(await screen.findByLabelText("Name"), "Lobby");
    await selectToken(user, "Operations wall");
    await user.click(screen.getByRole("button", { name: "New token" }));

    expect((within(inspector()).getByLabelText("Name") as HTMLInputElement).value).toBe("Lobby");
    expect(within(table()).getByRole("button", { name: "Operations wall" }).getAttribute("aria-pressed")).toBe("false");
    expect(screen.queryByRole("button", { name: "Revoke token Operations wall" })).toBeNull();
  });

  it("falls back to the issue form when the selected token is removed", async () => {
    const fetchMock = mockFetch(Response.json(state([token({ status: "revoked" })])), Response.json(state([])));
    const user = userEvent.setup();
    render(<DashboardTokensSettings />);

    await selectToken(user, "Operations wall");
    await user.click(within(inspector()).getByRole("button", { name: "Remove token Operations wall" }));
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Remove token" }));

    const [url, init] = fetchMock.mock.calls[1];
    expect(url).toBe("/api/v1/admin/dashboard-tokens/abcdefghijkl");
    expect(init.method).toBe("DELETE");
    expect(await screen.findByText("No dashboard tokens yet")).toBeTruthy();
    expect(within(inspector()).getByLabelText("Name")).toBeTruthy();
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
    expect(url).toBe("/api/v1/admin/dashboard-tokens");
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

    await selectToken(user, "Operations wall");
    await user.click(await screen.findByRole("button", { name: "Revoke token Operations wall" }));
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Revoke token" }));

    const [url, init] = fetchMock.mock.calls[1];
    expect(url).toBe("/api/v1/admin/dashboard-tokens/abcdefghijkl/revoke");
    expect(init.method).toBe("POST");
    expect(await within(table()).findByText("Revoked")).toBeTruthy();
    // The token stays selected, now with Remove as its action.
    expect(within(inspector()).getByText("Revoked")).toBeTruthy();
    expect(within(inspector()).getByRole("button", { name: "Remove token Operations wall" })).toBeTruthy();
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
