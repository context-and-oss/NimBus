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
    render(<AgentKeysSettings />);

    const rows = await screen.findAllByRole("row");
    const enrichment = rows.find((r) => within(r).queryByText("enrichment-agent"))!;
    expect(within(enrichment).getByText("nbak_abcdefghijkl")).toBeTruthy();
    expect(within(enrichment).getByText("Active")).toBeTruthy();
    expect(within(enrichment).getByRole("button", { name: "Revoke key for enrichment-agent" })).toBeTruthy();

    const triage = rows.find((r) => within(r).queryByText("triage-agent"))!;
    expect(within(triage).getByText("Revoked")).toBeTruthy();
    expect(within(triage).getByText("Never")).toBeTruthy();
    expect(within(triage).queryByRole("button", { name: /Revoke/ })).toBeNull();
    expect(within(triage).getByRole("button", { name: "Remove key for triage-agent" })).toBeTruthy();
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
    expect(url).toBe("/api/admin/agent-keys");
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

    await user.click(await screen.findByRole("button", { name: "Revoke key for enrichment-agent" }));
    expect(fetchMock).toHaveBeenCalledTimes(1);

    const dialog = await screen.findByRole("dialog");
    expect(within(dialog).getByText(/stops working on every instance within 30 seconds/)).toBeTruthy();
    await user.click(within(dialog).getByRole("button", { name: "Revoke key" }));

    const [url, init] = fetchMock.mock.calls[1];
    expect(url).toBe("/api/admin/agent-keys/abcdefghijkl/revoke");
    expect(init.method).toBe("POST");
    expect(init.headers["X-NimBus-CSRF"]).toBe("csrf-test");
    expect(await screen.findByText("Revoked")).toBeTruthy();
  });

  it("removes a revoked key after confirmation", async () => {
    const fetchMock = mockFetch(Response.json(state([key({ status: "revoked" })])), Response.json(state([])));
    const user = userEvent.setup();
    render(<AgentKeysSettings />);

    await user.click(await screen.findByRole("button", { name: "Remove key for enrichment-agent" }));
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Remove key" }));

    const [url, init] = fetchMock.mock.calls[1];
    expect(url).toBe("/api/admin/agent-keys/abcdefghijkl");
    expect(init.method).toBe("DELETE");
    expect(await screen.findByText("No agent keys yet")).toBeTruthy();
  });

  it("shows the server's reason when a change is refused", async () => {
    mockFetch(
      Response.json(state()),
      Response.json({ code: "RevisionConflict", errors: ["The agent keys kept changing while saving. Try again."] }, { status: 409 }),
    );
    const user = userEvent.setup();
    render(<AgentKeysSettings />);

    await user.click(await screen.findByRole("button", { name: "Revoke key for enrichment-agent" }));
    await user.click(within(await screen.findByRole("dialog")).getByRole("button", { name: "Revoke key" }));

    expect((await screen.findByRole("alert")).textContent).toContain("kept changing");
  });

  it("explains that only site Owners can manage keys", async () => {
    mockFetch(new Response(null, { status: 403 }));
    render(<AgentKeysSettings />);

    expect((await screen.findByRole("alert")).textContent).toContain("Only site Owners");
  });

  it("guards closing the panel while the form holds input", async () => {
    mockFetch(Response.json(state([])));
    const user = userEvent.setup();
    const { onDirtyChange } = renderInPanel(<AgentKeysSettings />, "agent-keys");

    await user.type(await screen.findByLabelText("Agent id"), "draft");

    expect(onDirtyChange).toHaveBeenLastCalledWith(1);
  });
});
