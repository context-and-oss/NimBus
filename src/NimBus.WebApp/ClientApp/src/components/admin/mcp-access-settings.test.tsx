import { afterEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import McpAccessSettings from "./mcp-access-settings";
import { renderInPanel } from "../../test-utils/render-in-panel";
import { describeChanges, previewAccess, type McpDeployment, type McpSettings, type McpState } from "./mcp-access-model";

const originalFetch = globalThis.fetch;
afterEach(() => { cleanup(); globalThis.fetch = originalFetch; });

const settings = (): McpSettings => ({
  revision: "11111111-2222-3333-4444-555555555555", updatedBy: "owner@contoso.com", updatedAtUtc: "2026-10-07T08:00:00Z",
  enabled: true,
  capabilities: { payloads: false, report: true, classify: true, resubmit: true, skip: false },
  allowWorkloads: true,
  people: { mode: "all", principals: [] },
  clients: { mode: "any", approved: [] },
  endpoints: { visibility: "all", hidden: [], changes: "allVisible", changeOn: [] },
  limits: { requestsPerWindow: null, mutationsPerWindow: null },
});

const deployment = (mode: McpDeployment["mode"] = "entra"): McpDeployment => ({
  mode, endpointUrl: "https://nimbus.example/mcp", resourceMetadataUrl: mode === "entra" ? "https://nimbus.example/.well-known/oauth-protected-resource/mcp" : null,
  serverVersion: "4.3.0", tenantId: "tenant", clientId: "mcp-app", applicationIdUri: "api://mcp-app", authority: null, allowedOrigins: [],
  rateLimitsEnabled: true, requestLimit: { permit: 60, windowSeconds: 60 }, mutationLimit: { permit: 5, windowSeconds: 60 },
  endpoints: ["CrmEndpoint", "ErpEndpoint"],
});

const state = (overrides: Partial<McpState> = {}): McpState => ({
  saved: settings(), effective: settings(), policyLoaded: true, deployment: deployment(), failureIntelligenceEnabled: true, csrfToken: "csrf-test",
  ...overrides,
});

const activity = { hours: 24, actions: 1, refused: 1, capped: false,
  actionsByType: [{ name: "resubmit", count: 1 }], refusedByReason: [{ name: "client", count: 1 }],
  items: [{ atUtc: "2026-10-07T09:00:00Z", kind: "refused", type: "mcpAccessRefused", reason: "client", clientId: "1b9e5c3a-02f4-4b8e-a6d1-7c2e9f107f04", auditor: "aisha" }],
  refusedClients: [{ clientId: "1b9e5c3a-02f4-4b8e-a6d1-7c2e9f107f04", calls: 4, lastAuditor: "aisha", lastAtUtc: "2026-10-07T09:00:00Z" }] };

function mockFetch(...responses: Response[]) {
  const fetchMock = vi.fn();
  for (const r of responses) fetchMock.mockResolvedValueOnce(r);
  globalThis.fetch = fetchMock as typeof fetch;
  return fetchMock;
}

// Spec 038 slice 2: inside the Settings panel frame.
describe("MCP access settings in the Settings panel", () => {
  it("shows one tab at a time", async () => {
    mockFetch(Response.json(state()), Response.json(activity));
    renderInPanel(<McpAccessSettings />, "mcp", "connect");

    expect(await screen.findByText("Connect an agent")).toBeTruthy();
    expect(screen.queryByText("01 · Activation")).toBeNull();
    expect(screen.queryByRole("region", { name: "Active now" })).toBeNull();
  });

  it("puts the tiles and activation on Overview", async () => {
    mockFetch(Response.json(state()), Response.json(activity));
    renderInPanel(<McpAccessSettings />, "mcp", "overview");

    expect(await screen.findByRole("region", { name: "Active now" })).toBeTruthy();
    expect(screen.getByText("01 · Activation")).toBeTruthy();
  });

  it("labels the configured Entra app as the MCP resource, not a client", async () => {
    mockFetch(Response.json(state()), Response.json(activity));
    renderInPanel(<McpAccessSettings />, "mcp", "overview");

    const label = await screen.findByText("MCP resource (API) app ID");
    expect(label.nextElementSibling?.textContent).toBe("mcp-app");
    expect(screen.queryByText(/MCP app \(client\) ID/)).toBeNull();
  });

  it("explains that the connect command needs a separate client registration", async () => {
    mockFetch(Response.json(state()), Response.json(activity));
    renderInPanel(<McpAccessSettings />, "mcp", "connect");

    const command = await screen.findByText(/claude mcp add/);
    expect(command.textContent).toContain("--client-id <client-id> --callback-port <port>");
    expect(command.textContent).not.toContain("33418");
    const help = screen.getByText(/not the MCP resource \(API\) app ID/);
    expect(help.textContent).toContain("http://localhost:<port>/callback");
    expect(help.textContent).toContain("nimbus.*");
  });

  it("fills the connect command with an approved client and flags the resource ID used as a client", async () => {
    const approved = (clientId: string) => ({ ...settings(), clients: { mode: "approved" as const, approved: [{ clientId, name: "Claude", mayChange: false }] } });
    mockFetch(Response.json(state({ saved: approved("5e39b51a-a6f6-4242-83b8-4f27bd96c2b0") })), Response.json(activity));
    renderInPanel(<McpAccessSettings />, "mcp", "connect");

    expect((await screen.findByText(/claude mcp add/)).textContent).toContain("--client-id 5e39b51a-a6f6-4242-83b8-4f27bd96c2b0");
    expect(screen.queryByText(/AADSTS500113/)).toBeNull();

    cleanup();
    mockFetch(Response.json(state({ saved: approved("MCP-APP") })), Response.json(activity));
    renderInPanel(<McpAccessSettings />, "mcp", "connect");

    expect((await screen.findByText(/AADSTS500113/)).textContent).toMatch(/is the MCP resource/);
  });

  it("counts unsaved changes and reviews them from the footer, replacing the tabs", async () => {
    mockFetch(Response.json(state()), Response.json(activity));
    const { onDirtyChange } = renderInPanel(<McpAccessSettings />, "mcp", "permissions");

    await userEvent.click(await screen.findByRole("switch", { name: "Mark reported" }));
    expect(onDirtyChange).toHaveBeenLastCalledWith(1);

    await userEvent.click(within(screen.getByRole("contentinfo")).getByRole("button", { name: "Review changes" }));
    expect(screen.getByRole("region", { name: "Review changes" })).toBeTruthy();
    expect(screen.queryByRole("tablist")).toBeNull();
    expect(screen.queryByRole("switch", { name: "Mark reported" })).toBeNull();
  });
});

describe("MCP access settings", () => {
  it("reviews a widening change, requires confirmation and saves with CSRF and the revision", async () => {
    const saved = { ...settings(), capabilities: { ...settings().capabilities, skip: true }, revision: "99999999-0000-0000-0000-000000000000" };
    const fetchMock = mockFetch(Response.json(state()), Response.json(activity), Response.json(state({ saved, effective: saved })));
    render(<McpAccessSettings />);

    await userEvent.click(await screen.findByRole("switch", { name: "Skip messages" }));
    expect(screen.getByText(/1 unsaved change/)).toBeTruthy();
    await userEvent.click(screen.getByRole("button", { name: "Review changes" }));

    const review = screen.getByRole("region", { name: "Review changes" });
    expect(within(review).getByText("Allow skipping messages")).toBeTruthy();
    expect(within(review).getByText("widens")).toBeTruthy();
    const save = screen.getByRole("button", { name: "Save settings" }) as HTMLButtonElement;
    expect(save.disabled).toBe(true);
    await userEvent.click(screen.getByRole("checkbox", { name: /agents may skip messages/ }));
    await userEvent.click(save);

    await screen.findByText(/Saved revision 99999999/);
    const [url, request] = fetchMock.mock.calls[2];
    expect(url).toBe("/api/admin/mcp/settings");
    expect(request.method).toBe("PUT");
    expect(request.headers["X-NimBus-CSRF"]).toBe("csrf-test");
    expect(JSON.parse(request.body)).toMatchObject({
      revision: "11111111-2222-3333-4444-555555555555", confirmWidening: true, settings: { capabilities: { skip: true } },
    });
  });

  it("saves a narrowing change without asking for confirmation", async () => {
    const fetchMock = mockFetch(Response.json(state()), Response.json(activity), Response.json(state()));
    render(<McpAccessSettings />);

    await userEvent.click(await screen.findByRole("switch", { name: "Mark reported" }));
    await userEvent.click(screen.getByRole("button", { name: "Review changes" }));
    expect(screen.getByText("narrows")).toBeTruthy();
    expect(screen.queryByRole("checkbox", { name: /I confirm/ })).toBeNull();
    await userEvent.click(screen.getByRole("button", { name: "Save settings" }));

    await screen.findByText(/Saved revision/);
    expect(JSON.parse(fetchMock.mock.calls[2][1].body).confirmWidening).toBe(false);
  });

  it("explains a stale revision", async () => {
    mockFetch(Response.json(state()), Response.json(activity),
      Response.json({ code: "RevisionConflict", errors: ["changed"] }, { status: 409 }));
    render(<McpAccessSettings />);

    await userEvent.click(await screen.findByRole("switch", { name: "Mark reported" }));
    await userEvent.click(screen.getByRole("button", { name: "Review changes" }));
    await userEvent.click(screen.getByRole("button", { name: "Save settings" }));

    expect((await screen.findByRole("alert")).textContent).toMatch(/Another administrator changed these settings/);
  });

  it("explains an expired security token", async () => {
    mockFetch(Response.json(state()), Response.json(activity), new Response(null, { status: 400 }));
    render(<McpAccessSettings />);

    await userEvent.click(await screen.findByRole("switch", { name: "Mark reported" }));
    await userEvent.click(screen.getByRole("button", { name: "Review changes" }));
    await userEvent.click(screen.getByRole("button", { name: "Save settings" }));

    expect((await screen.findByRole("alert")).textContent).toMatch(/security token expired/);
  });

  it("turns MCP access off from the confirmation dialog", async () => {
    const off = { ...settings(), enabled: false };
    const fetchMock = mockFetch(Response.json(state()), Response.json(activity), Response.json(state({ saved: off, effective: off })));
    render(<McpAccessSettings />);

    await userEvent.click(await screen.findByRole("button", { name: "Turn off now" }));
    await userEvent.click(screen.getByRole("button", { name: "Turn off MCP access" }));

    await screen.findByText(/MCP access is off. Agents get 503/);
    expect(fetchMock.mock.calls[2][0]).toBe("/api/admin/mcp/turn-off");
    expect(fetchMock.mock.calls[2][1].headers["X-NimBus-CSRF"]).toBe("csrf-test");
    expect(screen.queryByRole("button", { name: "Turn off now" })).toBeNull();
  });

  it("explains setup when the deployment does not serve MCP", async () => {
    mockFetch(Response.json(state({ deployment: deployment("disabled"), effective: null })), Response.json(activity));
    render(<McpAccessSettings />);

    expect(await screen.findByText(/MCP isn't set up on this deployment/)).toBeTruthy();
    expect((screen.getByRole("switch", { name: "Serve the MCP endpoint" }) as HTMLButtonElement).disabled).toBe(true);
    expect(screen.queryByRole("button", { name: "Turn off now" })).toBeNull();
  });

  it("disables people and client rules in local development", async () => {
    mockFetch(Response.json(state({ deployment: deployment("localDevelopment") })), Response.json(activity));
    render(<McpAccessSettings />);

    expect(await screen.findByText(/Local development mode/)).toBeTruthy();
    expect((screen.getByRole("combobox", { name: "People" }) as HTMLSelectElement).disabled).toBe(true);
    expect((screen.getByRole("combobox", { name: "Client applications" }) as HTMLSelectElement).disabled).toBe(true);
    expect(screen.getByText(/claude mcp add --transport http nimbus https:\/\/nimbus.example\/mcp/)).toBeTruthy();
  });

  it("offers to approve a refused client", async () => {
    mockFetch(Response.json(state({ saved: { ...settings(), clients: { mode: "approved", approved: [{ clientId: "c1", name: "Claude", mayChange: true }] } } })), Response.json(activity));
    render(<McpAccessSettings />);

    await userEvent.click(await screen.findByRole("button", { name: "Approve…" }));

    expect((screen.getByRole("textbox", { name: "Client ID" }) as HTMLInputElement).value).toBe("1b9e5c3a-02f4-4b8e-a6d1-7c2e9f107f04");
  });
});

describe("MCP access model", () => {
  it("marks widening and narrowing changes", () => {
    const before = settings();
    const after = { ...settings(), enabled: false, capabilities: { ...before.capabilities, skip: true }, limits: { requestsPerWindow: 10, mutationsPerWindow: null } };

    expect(describeChanges(before, after)).toEqual([
      { text: "Turn the MCP endpoint off", widens: false },
      { text: "Allow skipping messages", widens: true },
      { text: "Tool calls per window: deployment value → 10", widens: false },
    ]);
  });

  it("previews what a workload and a reader can use", () => {
    const s = { ...settings(), capabilities: { ...settings().capabilities, skip: true } };
    const workload = previewAccess(s, deployment(), "workload", true);
    const reader = previewAccess(s, deployment(), "reader", true);

    expect(workload[0].allowed).toBe(true);
    expect(workload.slice(1).every(r => !r.allowed)).toBe(true);
    expect(reader.find(r => r.name === "nimbus_resubmit_message")?.why).toBe("needs Contributor");
    expect(previewAccess({ ...s, allowWorkloads: false }, deployment(), "workload", true)[0].why).toBe("workloads off");
  });

  it("lets a workload record classifications only when the switch is on", () => {
    // Spec 044 §6.6: the Nimbus.Classify app role admits recording, nothing else.
    const s = { ...settings(), allowWorkloadClassification: true };
    const on = previewAccess(s, deployment(), "workload", true);
    const off = previewAccess({ ...s, allowWorkloadClassification: false }, deployment(), "workload", true);

    expect(on.find(r => r.name === "nimbus_record_classification")).toEqual(
      { name: "nimbus_record_classification", allowed: true, why: "Nimbus.Classify role + Contributor" });
    expect(on.filter(r => r.allowed).map(r => r.name)).toEqual(["13 read tools", "nimbus_record_classification"]);
    expect(off.find(r => r.name === "nimbus_record_classification")?.why).toBe("workload recording off");
    expect(describeChanges(settings(), s)).toEqual([{ text: "Let workloads record failure classifications", widens: true }]);
  });

  it("lets a workload resubmit, never skip, only when unattended resubmits are on", () => {
    // Spec 044 Phase 2: the Nimbus.Resubmit app role admits resubmit within a budget; skip stays with a person.
    const s = { ...settings(), allowWorkloadResubmit: true, capabilities: { ...settings().capabilities, skip: true } };
    const on = previewAccess(s, deployment(), "workload", true);
    const off = previewAccess({ ...s, allowWorkloadResubmit: false }, deployment(), "workload", true);

    expect(on.find(r => r.name === "nimbus_resubmit_message")?.allowed).toBe(true);
    expect(on.find(r => r.name === "nimbus_skip_message")).toEqual({ name: "nimbus_skip_message", allowed: false, why: "workloads never skip" });
    expect(off.find(r => r.name === "nimbus_resubmit_message")?.why).toBe("unattended resubmits off");
    expect(describeChanges({ ...s, allowWorkloadResubmit: false }, s)).toEqual([{ text: "Let workloads resubmit failed messages unattended", widens: true }]);
  });

  it("reviews raising the unattended limits as widening and lowering them as narrowing", () => {
    expect(describeChanges(settings(), { ...settings(), workloadResubmitsPerDay: 50 })).toEqual(
      [{ text: "Workload resubmits per day: 20 → 50", widens: true }]);
    expect(describeChanges(settings(), { ...settings(), workloadResubmitsPerMessage: 1 })).toEqual(
      [{ text: "Unsuccessful workload resubmits per message: 2 → 1", widens: false }]);
  });

  it("shows the unattended limits only while workloads may resubmit", async () => {
    const allowed = { ...settings(), allowWorkloadResubmit: true, workloadResubmitsPerDay: 7 };
    mockFetch(Response.json(state({ saved: allowed, effective: allowed })), Response.json(activity));
    render(<McpAccessSettings />);

    const perDay = await screen.findByRole("spinbutton", { name: "Resubmits per agent per day" });

    expect((perDay as HTMLInputElement).value).toBe("7");
    expect(screen.getByRole("spinbutton", { name: "Unsuccessful resubmits per message" })).toBeTruthy();
  });

  it("disables the workload recording switch while workload tokens are refused", async () => {
    const refused = { ...settings(), allowWorkloads: false };
    mockFetch(Response.json(state({ saved: refused, effective: refused })), Response.json(activity));
    render(<McpAccessSettings />);

    const toggle = await screen.findByRole("switch", { name: "Allow workloads to record classifications" });

    expect((toggle as HTMLButtonElement).disabled).toBe(true);
  });
});
