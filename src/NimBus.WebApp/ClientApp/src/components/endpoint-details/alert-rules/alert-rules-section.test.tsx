import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { cleanup, render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import moment from "moment";
import * as api from "api-client";
import { ToastProvider } from "components/ui";
import AlertRulesSection from "./alert-rules-section";

const apiMocks = vi.hoisted(() => ({
  getEndpointAlertRules: vi.fn(),
  postEndpointAlertRule: vi.fn(),
  putEndpointAlertRule: vi.fn(),
  deleteEndpointAlertRule: vi.fn(),
}));

vi.mock("api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("api-client")>();
  return {
    ...actual,
    CookieAuth: vi.fn(() => ({})),
    Client: vi.fn(function () {
      return apiMocks;
    }),
  };
});

vi.mock("hooks/event-types", () => ({
  getEventTypesByEndpoint: vi.fn(() => Promise.resolve({ consumes: [], produces: [] })),
}));

const clipboard = vi.hoisted(() => ({ copyToClipboard: vi.fn(() => Promise.resolve()) }));
vi.mock("lib/clipboard", () => clipboard);

// A minute past midnight today, local time: the badge shows only the time for today.
const SINCE = moment().startOf("day").add(1, "minute").toISOString();

const ruleJson = (overrides: Record<string, unknown> = {}) => ({
  id: "r1",
  endpointId: "ErpEndpoint",
  name: "ERP timeouts",
  enabled: true,
  condition: {
    kind: "failure_backlog",
    eventTypes: ["OrderPlaced"],
    statuses: ["Failed", "DeadLettered"],
    errorTextContains: "timeout",
    threshold: 5,
  },
  resolveAfterMinutes: 5,
  targets: [
    { id: "t1", type: "mail", address: "erp@example.com" },
    { id: "t2", type: "webhook", url: "https://hooks.example.com/nimbus/abc" },
  ],
  state: { status: "Firing", sinceUtc: SINCE, lastValue: 5, pendingNotices: 0, transitions: [] },
  createdBy: "owner@example.com",
  ...overrides,
});

const OWNER_RULES = [
  ruleJson(),
  ruleJson({
    id: "r2",
    name: "Any failure",
    condition: { kind: "failure_backlog", eventTypes: [], statuses: [], threshold: 3 },
    targets: [{ id: "t1", type: "mail", address: "ops@example.com" }],
    state: { status: "Ok", lastValue: 1, pendingNotices: 0, transitions: [] },
  }),
  ruleJson({
    id: "r3",
    name: "Paused rule",
    enabled: false,
    targets: [{ id: "t1", type: "mail", address: "ops@example.com" }],
    state: { status: "Ok", pendingNotices: 0, transitions: [] },
  }),
];

const list = (overrides: Record<string, unknown> = {}) =>
  api.AlertRuleList.fromJS({
    rules: OWNER_RULES,
    evaluatorLastCompletedAtUtc: moment().subtract(30, "seconds").toISOString(),
    mailEnabled: true,
    webhookEnabled: true,
    canManage: true,
    ...overrides,
  });

const readerList = () =>
  list({
    canManage: false,
    rules: [
      ruleJson({
        targets: [
          { id: "t1", type: "mail", address: null },
          { id: "t2", type: "webhook", url: null },
        ],
      }),
    ],
  });

const renderSection = () =>
  render(
    <ToastProvider>
      <AlertRulesSection endpointId="ErpEndpoint" />
    </ToastProvider>,
  );

const table = () => screen.findByRole("table", { name: "Alert rules" });
const rowOf = async (name: string) => {
  const cell = await within(await table()).findByText(name);
  return cell.closest("tr") as HTMLElement;
};
const openMenu = async (user: ReturnType<typeof userEvent.setup>, name: string, item: string) => {
  await user.click(within(await rowOf(name)).getByRole("button", { name: `Actions for ${name}` }));
  await user.click(within(screen.getByRole("menu")).getByRole("menuitem", { name: item }));
};

beforeEach(() => {
  for (const mock of Object.values(apiMocks)) mock.mockReset();
  clipboard.copyToClipboard.mockClear();
  apiMocks.getEndpointAlertRules.mockResolvedValue(list());
});

afterEach(cleanup);

describe("AlertRulesSection table", () => {
  it("shows each rule's condition, status, capped count and targets to an Owner", async () => {
    renderSection();

    const firing = await rowOf("ERP timeouts");
    expect(
      within(firing).getByText(
        '≥ 5 failed events (Failed, DeadLettered) of type OrderPlaced, error contains "timeout"',
      ),
    ).toBeTruthy();
    expect(within(firing).getByText("Firing since 00:01")).toBeTruthy();
    expect(within(firing).getByText("≥ 5")).toBeTruthy();
    expect(within(firing).getByText("erp@example.com")).toBeTruthy();
    expect(within(firing).getByText("hooks.example.com")).toBeTruthy();
    expect(within(firing).queryByText(/nimbus\/abc/)).toBeNull();

    const ok = await rowOf("Any failure");
    expect(within(ok).getByText("Ok")).toBeTruthy();
    expect(within(ok).getByText("≥ 3 failed events")).toBeTruthy();
    expect(within(ok).getByText("1")).toBeTruthy();

    const paused = await rowOf("Paused rule");
    expect(within(paused).getByText("Disabled")).toBeTruthy();
    expect(within(paused).getByText("–")).toBeTruthy();

    expect(screen.getByRole("button", { name: "New rule" })).toBeTruthy();
    expect(screen.getAllByRole("button", { name: /^Actions for / })).toHaveLength(3);
    expect(apiMocks.getEndpointAlertRules).toHaveBeenCalledWith("ErpEndpoint");
  });

  it("offers Edit, Disable and Delete last in an Owner's row menu", async () => {
    const user = userEvent.setup();
    renderSection();
    await user.click(within(await rowOf("ERP timeouts")).getByRole("button", { name: "Actions for ERP timeouts" }));
    expect(within(screen.getByRole("menu")).getAllByRole("menuitem").map((i) => i.textContent)).toEqual([
      "Edit…",
      "Disable",
      "Delete…",
    ]);
  });

  it("gives a Reader no menu and no New rule, and only the number and kind of targets", async () => {
    apiMocks.getEndpointAlertRules.mockResolvedValue(readerList());
    renderSection();

    const row = await rowOf("ERP timeouts");
    expect(within(row).getByText("2 targets (mail, webhook)")).toBeTruthy();
    expect(screen.queryByRole("button", { name: "New rule" })).toBeNull();
    expect(screen.queryByRole("button", { name: /^Actions for / })).toBeNull();
    expect(within(screen.getByRole("table", { name: "Alert rules" })).queryByText("Actions")).toBeNull();
  });

  it("shows an empty state, with a hint for Owners only", async () => {
    apiMocks.getEndpointAlertRules.mockResolvedValue(list({ rules: [] }));
    renderSection();
    expect(await screen.findByText("No alert rules")).toBeTruthy();
    expect(screen.getByText(/Create a rule/)).toBeTruthy();
    cleanup();

    apiMocks.getEndpointAlertRules.mockResolvedValue(list({ rules: [], canManage: false }));
    renderSection();
    expect(await screen.findByText("No alert rules")).toBeTruthy();
    expect(screen.queryByText(/Create a rule/)).toBeNull();
  });

  it("says when the rules were last evaluated", async () => {
    renderSection();
    expect(await screen.findByText(/^Last evaluated .* ago$/)).toBeTruthy();
    cleanup();

    apiMocks.getEndpointAlertRules.mockResolvedValue(list({ evaluatorLastCompletedAtUtc: undefined }));
    renderSection();
    expect(await screen.findByText("Not evaluated yet")).toBeTruthy();
  });

  it("warns when the evaluator has not run for more than five minutes", async () => {
    apiMocks.getEndpointAlertRules.mockResolvedValue(
      list({ evaluatorLastCompletedAtUtc: moment().subtract(2, "hours").toISOString() }),
    );
    renderSection();
    const warning = await screen.findByText(/The rule evaluator has not run for 2 hours/);
    expect(warning.textContent).toContain("Rules are evaluated only while the WebApp runs.");
    expect(warning.getAttribute("role")).toBe("status");
  });
});

describe("AlertRulesSection form", () => {
  it("checks the form before sending it", async () => {
    const user = userEvent.setup();
    renderSection();
    await user.click(await screen.findByRole("button", { name: "New rule" }));
    const dialog = screen.getByRole("dialog", { name: "New rule" });

    await user.clear(within(dialog).getByLabelText("Threshold"));
    await user.type(within(dialog).getByLabelText("Threshold"), "0");
    await user.click(within(dialog).getByRole("button", { name: "Create rule" }));

    expect(within(dialog).getByText("Enter a name of 1 to 100 characters.")).toBeTruthy();
    expect(within(dialog).getByText("Enter a whole number from 1 to 100.")).toBeTruthy();
    expect(within(dialog).getByText("Add at least one target.")).toBeTruthy();
    expect(apiMocks.postEndpointAlertRule).not.toHaveBeenCalled();

    await user.click(within(dialog).getByRole("button", { name: "Add webhook" }));
    await user.type(within(dialog).getByLabelText("Webhook URL 1"), "http://hooks.example.com/x");
    expect(within(dialog).getByText("Enter an https URL.")).toBeTruthy();
  });

  it("creates a rule and shows a new webhook's secret once, with Copy", async () => {
    const user = userEvent.setup();
    apiMocks.postEndpointAlertRule.mockResolvedValue(
      api.AlertRuleSaved.fromJS({
        rule: ruleJson({
          id: "r9",
          name: "Stuck orders",
          targets: [
            { id: "t1", type: "mail", address: "erp@example.com" },
            { id: "t2", type: "webhook", url: "https://hooks.example.com/in" },
          ],
        }),
        secrets: [{ targetId: "t2", signingSecret: "nbwh_secret-value" }],
      }),
    );
    renderSection();
    await user.click(await screen.findByRole("button", { name: "New rule" }));
    const dialog = screen.getByRole("dialog", { name: "New rule" });

    await user.type(within(dialog).getByLabelText("Name"), "Stuck orders");
    await user.clear(within(dialog).getByLabelText("Threshold"));
    await user.type(within(dialog).getByLabelText("Threshold"), "10");
    await user.click(within(dialog).getByLabelText("Failed"));
    await user.click(within(dialog).getByLabelText("Unsupported"));
    await user.type(within(dialog).getByLabelText("Error text contains"), "timeout");
    await user.clear(within(dialog).getByLabelText("Resolve after (minutes)"));
    await user.type(within(dialog).getByLabelText("Resolve after (minutes)"), "15");
    await user.click(within(dialog).getByRole("button", { name: "Add mail" }));
    await user.type(within(dialog).getByLabelText("Mail address 1"), "erp@example.com");
    await user.click(within(dialog).getByRole("button", { name: "Add webhook" }));
    await user.type(within(dialog).getByLabelText("Webhook URL 2"), "https://hooks.example.com/in");
    await user.click(within(dialog).getByRole("button", { name: "Create rule" }));

    const secrets = await screen.findByRole("dialog", { name: "Webhook signing secrets" });
    expect(within(secrets).getByText("nbwh_secret-value")).toBeTruthy();
    expect(within(secrets).getByText(/won't be shown again/)).toBeTruthy();

    expect(apiMocks.postEndpointAlertRule).toHaveBeenCalledTimes(1);
    const [endpointId, body] = apiMocks.postEndpointAlertRule.mock.calls[0];
    expect(endpointId).toBe("ErpEndpoint");
    expect(JSON.parse(JSON.stringify(body))).toEqual({
      name: "Stuck orders",
      enabled: true,
      condition: {
        kind: "failure_backlog",
        eventTypes: [],
        statuses: ["Failed", "Unsupported"],
        errorTextContains: "timeout",
        threshold: 10,
      },
      resolveAfterMinutes: 15,
      targets: [
        { type: "mail", address: "erp@example.com" },
        { type: "webhook", url: "https://hooks.example.com/in" },
      ],
    });

    await user.click(within(secrets).getByRole("button", { name: "Copy secret for https://hooks.example.com/in" }));
    expect(clipboard.copyToClipboard).toHaveBeenCalledWith("nbwh_secret-value");
    expect(await screen.findByText("Secret copied.")).toBeTruthy();

    await user.click(within(secrets).getByRole("button", { name: "Done" }));
    expect(screen.queryByRole("dialog")).toBeNull();
    expect(screen.queryByText("nbwh_secret-value")).toBeNull();
    expect(apiMocks.getEndpointAlertRules).toHaveBeenCalledTimes(2);
  });

  it("closes without a secret panel when no webhook target was added", async () => {
    const user = userEvent.setup();
    apiMocks.postEndpointAlertRule.mockResolvedValue(
      api.AlertRuleSaved.fromJS({ rule: ruleJson({ id: "r9" }), secrets: [] }),
    );
    renderSection();
    await user.click(await screen.findByRole("button", { name: "New rule" }));
    const dialog = screen.getByRole("dialog", { name: "New rule" });
    await user.type(within(dialog).getByLabelText("Name"), "Mail only");
    await user.click(within(dialog).getByRole("button", { name: "Add mail" }));
    await user.type(within(dialog).getByLabelText("Mail address 1"), "erp@example.com");
    await user.click(within(dialog).getByRole("button", { name: "Create rule" }));

    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
    expect(await screen.findByText('Created "Mail only".')).toBeTruthy();
  });

  it("shows the server's problem in the form", async () => {
    const user = userEvent.setup();
    const problem = JSON.stringify({ code: "Invalid", errors: ["Alert mail is not configured on this WebApp (NimBus:Alerts:Mail)."] });
    apiMocks.postEndpointAlertRule.mockRejectedValue(new api.SwaggerException("Bad Request", 400, problem, {}, null));
    renderSection();
    await user.click(await screen.findByRole("button", { name: "New rule" }));
    const dialog = screen.getByRole("dialog", { name: "New rule" });
    await user.type(within(dialog).getByLabelText("Name"), "Rule");
    await user.click(within(dialog).getByRole("button", { name: "Add mail" }));
    await user.type(within(dialog).getByLabelText("Mail address 1"), "erp@example.com");
    await user.click(within(dialog).getByRole("button", { name: "Create rule" }));

    expect((await within(dialog).findByRole("alert")).textContent).toBe(
      "Alert mail is not configured on this WebApp (NimBus:Alerts:Mail).",
    );
    expect(screen.getByRole("dialog", { name: "New rule" })).toBeTruthy();
  });

  it("disables adding a target kind the deployment cannot deliver", async () => {
    const user = userEvent.setup();
    apiMocks.getEndpointAlertRules.mockResolvedValue(list({ mailEnabled: false }));
    renderSection();
    await user.click(await screen.findByRole("button", { name: "New rule" }));
    const dialog = screen.getByRole("dialog", { name: "New rule" });
    expect((within(dialog).getByRole("button", { name: "Add mail" }) as HTMLButtonElement).disabled).toBe(true);
    expect((within(dialog).getByRole("button", { name: "Add webhook" }) as HTMLButtonElement).disabled).toBe(false);
    expect(within(dialog).getByText(/Alert mail is not configured on this WebApp/)).toBeTruthy();
  });

  it("edits a rule and keeps its targets' ids", async () => {
    const user = userEvent.setup();
    apiMocks.putEndpointAlertRule.mockResolvedValue(api.AlertRuleSaved.fromJS({ rule: ruleJson(), secrets: [] }));
    renderSection();
    await openMenu(user, "ERP timeouts", "Edit…");
    const dialog = screen.getByRole("dialog", { name: "Edit rule ERP timeouts" });
    expect((within(dialog).getByLabelText("Mail address 1") as HTMLInputElement).value).toBe("erp@example.com");

    await user.clear(within(dialog).getByLabelText("Name"));
    await user.type(within(dialog).getByLabelText("Name"), "ERP timeouts (renamed)");
    await user.click(within(dialog).getByRole("button", { name: "Save rule" }));

    await waitFor(() => expect(apiMocks.putEndpointAlertRule).toHaveBeenCalledTimes(1));
    const [endpointId, ruleId, body] = apiMocks.putEndpointAlertRule.mock.calls[0];
    expect([endpointId, ruleId]).toEqual(["ErpEndpoint", "r1"]);
    expect(body.name).toBe("ERP timeouts (renamed)");
    expect(body.condition.eventTypes).toEqual(["OrderPlaced"]);
    expect(body.targets.map((t: api.AlertRuleTargetModel) => t.id)).toEqual(["t1", "t2"]);
    await waitFor(() => expect(screen.queryByRole("dialog")).toBeNull());
  });
});

describe("AlertRulesSection row actions", () => {
  it("disables and enables a rule by saving it with enabled flipped", async () => {
    const user = userEvent.setup();
    apiMocks.putEndpointAlertRule.mockResolvedValue(api.AlertRuleSaved.fromJS({ rule: ruleJson(), secrets: [] }));
    renderSection();

    await openMenu(user, "ERP timeouts", "Disable");
    await waitFor(() => expect(apiMocks.putEndpointAlertRule).toHaveBeenCalledTimes(1));
    let [, ruleId, body] = apiMocks.putEndpointAlertRule.mock.calls[0];
    expect(ruleId).toBe("r1");
    expect(body.enabled).toBe(false);
    expect(body.targets.map((t: api.AlertRuleTargetModel) => t.id)).toEqual(["t1", "t2"]);
    expect(body.condition.threshold).toBe(5);

    await openMenu(user, "Paused rule", "Enable");
    await waitFor(() => expect(apiMocks.putEndpointAlertRule).toHaveBeenCalledTimes(2));
    [, ruleId, body] = apiMocks.putEndpointAlertRule.mock.calls[1];
    expect(ruleId).toBe("r3");
    expect(body.enabled).toBe(true);
    await waitFor(() => expect(apiMocks.getEndpointAlertRules).toHaveBeenCalledTimes(3));
  });

  it("deletes a rule only after confirming, then refreshes", async () => {
    const user = userEvent.setup();
    apiMocks.deleteEndpointAlertRule.mockResolvedValue(undefined);
    renderSection();

    await openMenu(user, "ERP timeouts", "Delete…");
    const confirm = screen.getByRole("dialog", { name: "Delete alert rule" });
    expect(within(confirm).getByText(/resolved notice/)).toBeTruthy();
    expect(apiMocks.deleteEndpointAlertRule).not.toHaveBeenCalled();
    await user.click(within(confirm).getByRole("button", { name: "Cancel" }));
    expect(apiMocks.deleteEndpointAlertRule).not.toHaveBeenCalled();

    apiMocks.getEndpointAlertRules.mockResolvedValue(list({ rules: OWNER_RULES.slice(1) }));
    await openMenu(user, "ERP timeouts", "Delete…");
    await user.click(
      within(screen.getByRole("dialog", { name: "Delete alert rule" })).getByRole("button", { name: "Delete rule" }),
    );

    await waitFor(() => expect(apiMocks.deleteEndpointAlertRule).toHaveBeenCalledWith("ErpEndpoint", "r1"));
    await waitFor(() => expect(screen.queryByText("ERP timeouts")).toBeNull());
    expect(apiMocks.getEndpointAlertRules).toHaveBeenCalledTimes(2);
  });

  it("reports a failed change in a toast", async () => {
    const user = userEvent.setup();
    apiMocks.putEndpointAlertRule.mockRejectedValue(
      new api.SwaggerException("Conflict", 409, JSON.stringify({ errors: ["The rule kept changing."] }), {}, null),
    );
    renderSection();
    await openMenu(user, "ERP timeouts", "Disable");
    expect(await screen.findByText('Could not disable "ERP timeouts".')).toBeTruthy();
    expect(screen.getByText("The rule kept changing.")).toBeTruthy();
  });
});
