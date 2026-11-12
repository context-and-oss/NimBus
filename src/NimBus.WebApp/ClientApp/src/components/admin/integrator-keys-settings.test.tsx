import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import IntegratorKeysSettings from "./integrator-keys-settings";
import {
  exampleCall,
  expiryFrom,
  scopeLabel,
  validateName,
  validateScope,
  type IntegratorKeyInfo,
  type IntegratorKeysState,
} from "./integrator-keys-model";

const originalFetch = globalThis.fetch;
afterEach(() => {
  cleanup();
  globalThis.fetch = originalFetch;
});

const SECRET = "nbik_zyxwvutsrqpo_SECRETSECRETSECRETSECRETSECRETSECRETSEC";

const key = (overrides: Partial<IntegratorKeyInfo> = {}): IntegratorKeyInfo => ({
  keyId: "abcdefghijkl",
  prefix: "nbik_abcdefghijkl",
  name: "ERP reconciliation",
  role: "contributor",
  endpoints: ["Billing"],
  canReadPii: false,
  status: "active",
  createdAtUtc: "2026-11-12T08:00:00Z",
  createdBy: "owner@contoso.com",
  expiresAtUtc: null,
  revokedAtUtc: null,
  revokedBy: null,
  lastUsedAtUtc: "2026-11-12T09:30:00Z",
  ...overrides,
});

const state = (keys: IntegratorKeyInfo[] = [key()]): IntegratorKeysState => ({
  keys,
  endpoints: ["Billing", "Storefront", "Warehouse"],
  csrfToken: "csrf-test",
});

function mockFetch(...responses: Response[]) {
  const fetchMock = vi.fn();
  for (const r of responses) fetchMock.mockResolvedValueOnce(r);
  globalThis.fetch = fetchMock as typeof fetch;
  return fetchMock;
}

describe("integrator keys model", () => {
  it("requires a name of at most 100 characters", () => {
    expect(validateName("ERP job")).toBeUndefined();
    expect(validateName("  ")).toBeTruthy();
    expect(validateName("x".repeat(101))).toBeTruthy();
  });

  it("requires an endpoint when the key is not for every endpoint", () => {
    expect(validateScope(true, [])).toBeUndefined();
    expect(validateScope(false, ["Billing"])).toBeUndefined();
    expect(validateScope(false, [])).toBeTruthy();
  });

  it("turns an expiry choice into a UTC timestamp", () => {
    const now = new Date("2026-11-12T12:00:00Z");
    expect(expiryFrom("never", now)).toBeNull();
    expect(expiryFrom("90", now)).toBe("2027-02-10T12:00:00.000Z");
  });

  it("shows the scope and a call that proves the key works", () => {
    expect(scopeLabel({ endpoints: null })).toBe("All endpoints");
    expect(scopeLabel({ endpoints: ["Billing", "Warehouse"] })).toBe("Billing, Warehouse");
    expect(exampleCall("nbik_a_b", "https://nimbus.example")).toBe(
      'curl -H "X-NimBus-Api-Key: nbik_a_b" https://nimbus.example/api/v1/endpoint/status/count',
    );
  });
});

describe("Integrator keys settings", () => {
  it("lists keys with their access, status and last use", async () => {
    mockFetch(
      Response.json(
        state([
          key(),
          key({ keyId: "mnopqrstuvwx", prefix: "nbik_mnopqrstuvwx", name: "Old report", role: "reader", endpoints: null, canReadPii: true, status: "revoked", lastUsedAtUtc: null }),
        ]),
      ),
    );
    render(<IntegratorKeysSettings />);

    const rows = await screen.findAllByRole("row");
    const active = rows.find((r) => within(r).queryByText("ERP reconciliation"))!;
    expect(within(active).getByText("nbik_abcdefghijkl")).toBeTruthy();
    expect(within(active).getByText("Contributor")).toBeTruthy();
    expect(within(active).getByText("Billing")).toBeTruthy();
    expect(within(active).getByRole("button", { name: "Revoke key ERP reconciliation" })).toBeTruthy();
    const old = rows.find((r) => within(r).queryByText("Old report"))!;
    expect(within(old).getByText("All endpoints")).toBeTruthy();
    expect(within(old).getByText(/PII/)).toBeTruthy();
    expect(within(old).getByRole("button", { name: "Remove key Old report" })).toBeTruthy();
  });

  it("issues a scoped key and shows it once with an example call", async () => {
    const issued = key({ keyId: "zyxwvutsrqpo", prefix: "nbik_zyxwvutsrqpo", name: "Warehouse sync", endpoints: ["Warehouse"], canReadPii: true, lastUsedAtUtc: null });
    const fetchMock = mockFetch(Response.json(state([])), Response.json({ key: SECRET, issued, state: state([issued]) }));
    const user = userEvent.setup();
    render(<IntegratorKeysSettings />);

    await user.type(await screen.findByLabelText("Name"), "Warehouse sync");
    await user.selectOptions(screen.getByLabelText("Role"), "contributor");
    await user.click(screen.getByLabelText("Every endpoint, including ones added later"));
    await user.click(screen.getByLabelText("Warehouse"));
    await user.click(screen.getByLabelText("May read event payloads (PII)"));
    await user.click(screen.getByRole("button", { name: "Issue key" }));

    const [url, init] = fetchMock.mock.calls[1];
    expect(url).toBe("/api/v1/admin/integrator-keys");
    expect(init.method).toBe("POST");
    expect(init.headers["X-NimBus-CSRF"]).toBe("csrf-test");
    const body = JSON.parse(init.body);
    expect(body).toMatchObject({ name: "Warehouse sync", role: "contributor", endpoints: ["Warehouse"], canReadPii: true });
    expect(new Date(body.expiresAtUtc).getTime() - Date.now()).toBeGreaterThan(89 * 24 * 3600 * 1000);

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(SECRET)).toBeTruthy();
    expect(within(dialog).getByText(exampleCall(SECRET))).toBeTruthy();
    expect(within(dialog).getByText(/won't be shown again/)).toBeTruthy();
    expect(within(dialog).getByText("Contributor on Warehouse, with payload access.")).toBeTruthy();

    await user.click(within(dialog).getByRole("button", { name: "Done" }));
    expect(screen.queryByText(/SECRETSECRET/)).toBeNull();
  });

  it("sends a site-wide reader key without endpoints by default", async () => {
    const issued = key({ role: "reader", endpoints: null });
    const fetchMock = mockFetch(Response.json(state([])), Response.json({ key: SECRET, issued, state: state([issued]) }));
    const user = userEvent.setup();
    render(<IntegratorKeysSettings />);

    await user.type(await screen.findByLabelText("Name"), "Reporting");
    await user.click(screen.getByRole("button", { name: "Issue key" }));

    const body = JSON.parse(fetchMock.mock.calls[1][1].body);
    expect(body).toMatchObject({ role: "reader", endpoints: null, canReadPii: false });
  });

  it("checks the name and the scope before sending", async () => {
    const fetchMock = mockFetch(Response.json(state([])));
    const user = userEvent.setup();
    render(<IntegratorKeysSettings />);

    await user.click(await screen.findByRole("button", { name: "Issue key" }));
    expect(screen.getByText(/Give the key a name/)).toBeTruthy();

    await user.type(screen.getByLabelText("Name"), "Scoped");
    await user.click(screen.getByLabelText("Every endpoint, including ones added later"));
    await user.click(screen.getByRole("button", { name: "Issue key" }));
    expect(screen.getByText(/Choose at least one endpoint/)).toBeTruthy();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("revokes only after confirmation", async () => {
    const fetchMock = mockFetch(Response.json(state()), Response.json(state([key({ status: "revoked" })])));
    const user = userEvent.setup();
    render(<IntegratorKeysSettings />);

    await user.click(await screen.findByRole("button", { name: "Revoke key ERP reconciliation" }));
    expect(fetchMock).toHaveBeenCalledTimes(1);
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Revoke key" }));

    const [url, init] = fetchMock.mock.calls[1];
    expect(url).toBe("/api/v1/admin/integrator-keys/abcdefghijkl/revoke");
    expect(init.method).toBe("POST");
    expect(await screen.findByText("Revoked")).toBeTruthy();
  });

  it("shows the server's reason for a refused key", async () => {
    mockFetch(
      Response.json(state([])),
      Response.json({ status: 400, code: "Invalid", errors: ["endpoint 'Payroll' is not defined on this platform."] }, { status: 400 }),
    );
    const user = userEvent.setup();
    render(<IntegratorKeysSettings />);

    await user.type(await screen.findByLabelText("Name"), "Payroll");
    await user.click(screen.getByRole("button", { name: "Issue key" }));

    expect((await screen.findByRole("alert")).textContent).toContain("Payroll");
  });

  it("explains that only site Owners can manage keys", async () => {
    mockFetch(new Response(null, { status: 403 }));
    render(<IntegratorKeysSettings />);

    expect((await screen.findByRole("alert")).textContent).toContain("Only site Owners");
  });
});
