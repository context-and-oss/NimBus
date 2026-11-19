import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import AgentKeysSettings from "./agent-keys-settings";
import { renderInPanel } from "../../test-utils/render-in-panel";
import { expiryFrom, validateAgentId, type AgentKeyInfo, type AgentKeysState } from "./agent-keys-model";

const originalFetch = globalThis.fetch;
afterEach(() => {
  cleanup();
  globalThis.fetch = originalFetch;
});

const key = (overrides: Partial<AgentKeyInfo> = {}): AgentKeyInfo => ({
  keyId: "abcdefghijkl",
  prefix: "nbak_abcdefghijkl",
  agentId: "enrichment-agent",
  name: "Production enrichment",
  status: "active",
  createdAtUtc: "2026-10-11T08:00:00Z",
  createdBy: "owner@contoso.com",
  expiresAtUtc: null,
  revokedAtUtc: null,
  revokedBy: null,
  lastUsedAtUtc: "2026-10-11T09:30:00Z",
  ...overrides,
});

const state = (keys: AgentKeyInfo[] = [key()]): AgentKeysState => ({ keys, csrfToken: "csrf-test" });

const table = () => screen.getByRole("table", { name: "Agent keys" });
const inspector = () => screen.getByRole("complementary", { name: "Inspector" });
/** Selects a key's row, which puts the key and its actions in the inspector. */
const selectKey = async (user: ReturnType<typeof userEvent.setup>, agentId: string) =>
  user.click(await within(await screen.findByRole("table", { name: "Agent keys" })).findByRole("button", { name: agentId }));

/** What the panel reads from /api/v1/admin/mcp/settings once it lists an MCP key. */
const mcpStatus = (deployment: { agentKeysEnabled?: boolean } = {}, saved: { allowAgentKeys?: boolean; allowWorkloads?: boolean } = {}) => ({
  deployment: { mode: "entra", agentKeysEnabled: true, endpointUrl: "https://nimbus.example/mcp", ...deployment },
  saved: { allowAgentKeys: true, allowWorkloads: true, ...saved },
});

function mockFetch(...responses: Response[]) {
  const fetchMock = vi.fn();
  for (const r of responses) fetchMock.mockResolvedValueOnce(r);
  globalThis.fetch = fetchMock as typeof fetch;
  return fetchMock;
}

describe("agent keys model", () => {
  it("accepts agent ids the server accepts", () => {
    expect(validateAgentId("enrichment-agent")).toBeUndefined();
    expect(validateAgentId("a.b_c-1")).toBeUndefined();
    expect(validateAgentId("")).toBeTruthy();
    expect(validateAgentId("-dash-first")).toBeTruthy();
    expect(validateAgentId("has space")).toBeTruthy();
    expect(validateAgentId("x".repeat(65))).toBeTruthy();
  });

  it("turns an expiry choice into a UTC timestamp", () => {
    const now = new Date("2026-10-11T12:00:00Z");
    expect(expiryFrom("never", now)).toBeNull();
    expect(expiryFrom("90", now)).toBe("2027-01-09T12:00:00.000Z");
  });
});

describe("Agent keys settings", () => {
  it("lists keys with their agent, status and last use", async () => {
    mockFetch(Response.json(state([
      key(),
      key({ keyId: "mnopqrstuvwx", prefix: "nbak_mnopqrstuvwx", agentId: "triage-agent", name: null, status: "revoked", lastUsedAtUtc: null }),
    ])));
    const user = userEvent.setup();
    render(<AgentKeysSettings />);

    const rows = await screen.findAllByRole("row");
    const enrichment = rows.find((r) => within(r).queryByText("enrichment-agent"))!;
    expect(within(enrichment).getByText("nbak_abcdefghijkl")).toBeTruthy();
    expect(within(enrichment).getByText("Active")).toBeTruthy();
    await selectKey(user, "enrichment-agent");
    expect(within(inspector()).getByRole("button", { name: "Revoke key for enrichment-agent" })).toBeTruthy();

    const triage = rows.find((r) => within(r).queryByText("triage-agent"))!;
    expect(within(triage).getByText("Revoked")).toBeTruthy();
    expect(within(triage).getByText("Never")).toBeTruthy();
    await selectKey(user, "triage-agent");
    expect(within(inspector()).queryByRole("button", { name: /Revoke/ })).toBeNull();
    expect(within(inspector()).getByRole("button", { name: "Remove key for triage-agent" })).toBeTruthy();
  });

  it("offers each key's actions in its row menu, not as row buttons", async () => {
    mockFetch(Response.json(state()));
    const user = userEvent.setup();
    render(<AgentKeysSettings />);

    const headers = within(await screen.findByRole("table", { name: "Agent keys" })).getAllByRole("columnheader");
    expect(headers.map((h) => h.textContent)).toEqual(["Agent and key", "Use", "Status", "Issued", "Last used", "Expires", "Actions"]);
    expect(within(table()).queryByRole("button", { name: /Rotate|Revoke|Remove/ })).toBeNull();

    await user.click(within(table()).getByRole("button", { name: "Actions for enrichment-agent" }));
    const menu = screen.getByRole("menu");
    expect(within(menu).getAllByRole("menuitem").map((i) => i.textContent)).toEqual(["Rotate", "Revoke…"]);
    await user.click(within(menu).getByRole("menuitem", { name: "Revoke…" }));
    expect(within(await screen.findByRole("dialog")).getByRole("button", { name: "Revoke key" })).toBeTruthy();
  });

  it("shows the issue form until a key is selected, then that key's facts and actions", async () => {
    mockFetch(Response.json(state()));
    const user = userEvent.setup();
    render(<AgentKeysSettings />);

    await screen.findByRole("table", { name: "Agent keys" });
    expect(within(inspector()).getByRole("heading", { name: "Issue key" })).toBeTruthy();
    expect(within(inspector()).getByLabelText("Agent id")).toBeTruthy();
    expect(screen.queryByRole("button", { name: /Rotate key|Revoke key|Remove key/ })).toBeNull();

    await selectKey(user, "enrichment-agent");

    expect(within(table()).getByRole("button", { name: "enrichment-agent" }).getAttribute("aria-pressed")).toBe("true");
    expect(within(inspector()).getByRole("heading", { name: "enrichment-agent" })).toBeTruthy();
    expect(within(inspector()).getByText("Agent key")).toBeTruthy();
    expect(within(inspector()).getByText("Active")).toBeTruthy();
    expect(within(inspector()).getByText("Agent API")).toBeTruthy();
    expect(within(inspector()).getByText("nbak_abcdefghijkl")).toBeTruthy();
    expect(within(inspector()).getByText("owner@contoso.com")).toBeTruthy();
    expect(within(inspector()).getByText("No expiry")).toBeTruthy();
    expect(within(inspector()).getByRole("button", { name: "Rotate key for enrichment-agent" })).toBeTruthy();
    expect(within(inspector()).getByRole("button", { name: "Revoke key for enrichment-agent" })).toBeTruthy();
    expect(screen.queryByLabelText("Agent id")).toBeNull();
  });

  it("returns to the issue form, with its draft, from New key", async () => {
    mockFetch(Response.json(state()));
    const user = userEvent.setup();
    render(<AgentKeysSettings />);

    await user.type(await screen.findByLabelText("Agent id"), "draft-agent");
    await selectKey(user, "enrichment-agent");
    await user.click(screen.getByRole("button", { name: "New key" }));

    expect((within(inspector()).getByLabelText("Agent id") as HTMLInputElement).value).toBe("draft-agent");
    expect(within(table()).getByRole("button", { name: "enrichment-agent" }).getAttribute("aria-pressed")).toBe("false");
    expect(screen.queryByRole("button", { name: "Revoke key for enrichment-agent" })).toBeNull();
  });

  it("says so when there are no keys yet", async () => {
    mockFetch(Response.json(state([])));
    render(<AgentKeysSettings />);

    expect(await screen.findByText("No agent keys yet")).toBeTruthy();
  });

  it("issues a key with the antiforgery token and shows it once", async () => {
    const issued = key({ keyId: "zyxwvutsrqpo", prefix: "nbak_zyxwvutsrqpo", agentId: "new-agent", name: null, lastUsedAtUtc: null });
    const fetchMock = mockFetch(
      Response.json(state([])),
      Response.json({ key: "nbak_zyxwvutsrqpo_SECRETSECRETSECRETSECRETSECRETSECRETSEC", issued, state: state([issued]) }),
    );
    const user = userEvent.setup();
    render(<AgentKeysSettings />);

    await user.type(await screen.findByLabelText("Agent id"), "new-agent");
    await user.click(screen.getByRole("button", { name: "Issue key" }));

    const [url, init] = fetchMock.mock.calls[1];
    expect(url).toBe("/api/v1/admin/agent-keys");
    expect(init.method).toBe("POST");
    expect(init.headers["X-NimBus-CSRF"]).toBe("csrf-test");
    expect(JSON.parse(init.body)).toEqual({ agentId: "new-agent", name: null, expiresAtUtc: null });

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText("nbak_zyxwvutsrqpo_SECRETSECRETSECRETSECRETSECRETSECRETSEC")).toBeTruthy();
    expect(within(dialog).getByText(/won't be shown again/)).toBeTruthy();

    await user.click(within(dialog).getByRole("button", { name: "Done" }));
    expect(screen.queryByText(/SECRETSECRET/)).toBeNull();
    expect(screen.getByText("new-agent")).toBeTruthy();
  });

  it("checks the agent id before sending", async () => {
    const fetchMock = mockFetch(Response.json(state([])));
    const user = userEvent.setup();
    render(<AgentKeysSettings />);

    await user.type(await screen.findByLabelText("Agent id"), "has space");
    await user.click(screen.getByRole("button", { name: "Issue key" }));

    expect(screen.getByText(/letters, digits/)).toBeTruthy();
    expect(fetchMock).toHaveBeenCalledTimes(1);
  });

  it("revokes only after confirmation", async () => {
    const revoked = key({ status: "revoked", revokedAtUtc: "2026-10-11T10:00:00Z", revokedBy: "owner@contoso.com" });
    const fetchMock = mockFetch(Response.json(state()), Response.json(state([revoked])));
    const user = userEvent.setup();
    render(<AgentKeysSettings />);

    await selectKey(user, "enrichment-agent");
    await user.click(await screen.findByRole("button", { name: "Revoke key for enrichment-agent" }));
    expect(fetchMock).toHaveBeenCalledTimes(1);

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/stops working on every instance within 30 seconds/)).toBeTruthy();
    await user.click(within(dialog).getByRole("button", { name: "Revoke key" }));

    const [url, init] = fetchMock.mock.calls[1];
    expect(url).toBe("/api/v1/admin/agent-keys/abcdefghijkl/revoke");
    expect(init.method).toBe("POST");
    expect(init.headers["X-NimBus-CSRF"]).toBe("csrf-test");
    expect(await within(table()).findByText("Revoked")).toBeTruthy();
    // The key stays selected, now with Remove as its only action.
    expect(within(inspector()).getByText("Revoked")).toBeTruthy();
    expect(within(inspector()).getByRole("button", { name: "Remove key for enrichment-agent" })).toBeTruthy();
  });

  it("removes a revoked key after confirmation", async () => {
    const fetchMock = mockFetch(Response.json(state([key({ status: "revoked" })])), Response.json(state([])));
    const user = userEvent.setup();
    render(<AgentKeysSettings />);

    await selectKey(user, "enrichment-agent");
    await user.click(await screen.findByRole("button", { name: "Remove key for enrichment-agent" }));
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Remove key" }));

    const [url, init] = fetchMock.mock.calls[1];
    expect(url).toBe("/api/v1/admin/agent-keys/abcdefghijkl");
    expect(init.method).toBe("DELETE");
    expect(await screen.findByText("No agent keys yet")).toBeTruthy();
    // Its row is gone, so the inspector falls back to the issue form.
    expect(within(inspector()).getByLabelText("Agent id")).toBeTruthy();
  });

  it("shows the server's reason when a change is refused", async () => {
    mockFetch(
      Response.json(state()),
      Response.json({ code: "RevisionConflict", errors: ["The agent keys kept changing while saving. Try again."] }, { status: 409 }),
    );
    const user = userEvent.setup();
    render(<AgentKeysSettings />);

    await selectKey(user, "enrichment-agent");
    await user.click(await screen.findByRole("button", { name: "Revoke key for enrichment-agent" }));
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Revoke key" }));

    expect((await screen.findByRole("alert")).textContent).toContain("kept changing");
  });

  it("explains that only site Owners can manage keys", async () => {
    mockFetch(new Response(null, { status: 403 }));
    render(<AgentKeysSettings />);

    expect((await screen.findByRole("alert")).textContent).toContain("Only site Owners");
  });

  // ---- Spec 052: keys for MCP ------------------------------------------------------------------

  it("issues an MCP key with its grant and an expiry, and never offers Never", async () => {
    const issued = key({ keyId: "zyxwvutsrqpo", prefix: "nbak_zyxwvutsrqpo", agentId: "sre-agent", use: "mcp", mcp: { classify: false, annotate: false, resubmit: true, requestsPerWindow: null }, expiresAtUtc: "2099-01-01T00:00:00Z" });
    const fetchMock = mockFetch(
      Response.json(state([])),
      Response.json({ key: "nbak_zyxwvutsrqpo_SECRETSECRETSECRETSECRETSECRETSECRETSEC", issued, state: state([issued]) }),
      Response.json(mcpStatus()),
    );
    const user = userEvent.setup();
    render(<AgentKeysSettings />);

    await user.type(await screen.findByLabelText("Agent id"), "sre-agent");
    await user.selectOptions(screen.getByLabelText("Use"), "mcp");
    expect(within(screen.getByLabelText("Expires")).queryByRole("option", { name: "Never" })).toBeNull();
    expect((screen.getByLabelText("Expires") as HTMLSelectElement).value).toBe("90");
    await user.click(screen.getByRole("checkbox", { name: /Resubmit failed messages/ }));
    await user.click(screen.getByRole("button", { name: "Issue key" }));

    const body = JSON.parse(fetchMock.mock.calls[1][1].body);
    expect(body.use).toBe("mcp");
    expect(body.mcp).toEqual({ classify: false, annotate: false, resubmit: true, requestsPerWindow: null });
    expect(body.expiresAtUtc).toBeTruthy();

    // The once-only dialog says how to connect and that the agent needs an Access Control grant.
    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/Authorization: Bearer/)).toBeTruthy();
    expect(within(dialog).getByText(/agent:sre-agent/)).toBeTruthy();
  });

  it("shows each key's use, rotates an active key and offers to revoke the old one", async () => {
    const old = key({ agentId: "sre-agent", use: "mcp", mcp: { classify: true, annotate: false, resubmit: true, requestsPerWindow: null }, expiresAtUtc: "2099-01-01T00:00:00Z" });
    const successor = key({ ...old, keyId: "zyxwvutsrqpo", prefix: "nbak_zyxwvutsrqpo", replaces: old.keyId });
    const fetchMock = mockFetch(
      Response.json(state([old])),
      Response.json(mcpStatus()),
      Response.json({ key: "nbak_zyxwvutsrqpo_SECRETSECRETSECRETSECRETSECRETSECRETSEC", issued: successor, state: state([successor, old]) }),
      Response.json(state([successor, { ...old, status: "revoked" }])),
    );
    const user = userEvent.setup();
    render(<AgentKeysSettings />);

    const row = (await screen.findAllByRole("row")).find((r) => within(r).queryByText("nbak_abcdefghijkl"))!;
    expect(within(row).getByText("MCP")).toBeTruthy();
    expect(within(row).getByText(/classify, resubmit/)).toBeTruthy();

    await user.click(within(row).getByRole("button", { name: "sre-agent" }));
    expect(within(inspector()).getByText(/classify, resubmit/)).toBeTruthy();
    await user.click(within(inspector()).getByRole("button", { name: "Rotate key for sre-agent" }));
    const [url, init] = fetchMock.mock.calls[2];
    expect(url).toBe("/api/v1/admin/agent-keys/abcdefghijkl/rotate");
    expect(init.method).toBe("POST");

    const dialog = await screen.findByRole("dialog");
    await user.click(within(dialog).getByRole("button", { name: "Revoke the old key" }));
    expect(fetchMock.mock.calls[3][0]).toBe("/api/v1/admin/agent-keys/abcdefghijkl/revoke");
  });

  it("says an MCP key will not work yet while keys are off", async () => {
    mockFetch(
      Response.json(state([key({ agentId: "sre-agent", use: "mcp", mcp: { classify: false, annotate: false, resubmit: false, requestsPerWindow: null }, expiresAtUtc: "2099-01-01T00:00:00Z" })])),
      Response.json(mcpStatus({ agentKeysEnabled: false })),
    );
    render(<AgentKeysSettings />);

    expect(await screen.findByText(/won't work yet/i)).toBeTruthy();
    expect(screen.getByText(/NimBus__Mcp__AgentKeys__Enabled/)).toBeTruthy();
  });

  it("marks an MCP key that expires within 14 days", async () => {
    const soon = new Date(Date.now() + 3 * 24 * 60 * 60 * 1000).toISOString();
    mockFetch(
      Response.json(state([key({ use: "mcp", mcp: { classify: false, annotate: false, resubmit: false, requestsPerWindow: null }, expiresAtUtc: soon })])),
      Response.json(mcpStatus()),
    );
    render(<AgentKeysSettings />);

    expect(await screen.findByText("Expires soon")).toBeTruthy();
  });

  it("guards closing the panel while the form holds input", async () => {
    mockFetch(Response.json(state([])));
    const user = userEvent.setup();
    const { onDirtyChange } = renderInPanel(<AgentKeysSettings />, "agent-keys");

    // In the Settings frame the issue form opens in the drawer on request.
    expect(screen.queryByLabelText("Agent id")).toBeNull();
    await user.click(await screen.findByRole("button", { name: "New key" }));
    await user.type(await screen.findByLabelText("Agent id"), "draft");

    expect(onDirtyChange).toHaveBeenLastCalledWith(1);
  });
});
