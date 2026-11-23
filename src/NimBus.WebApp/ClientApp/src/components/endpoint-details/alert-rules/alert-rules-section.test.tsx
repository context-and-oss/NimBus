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
  postEndpointAlertRulePreview: vi.fn(),
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

const ALL_KINDS = ["failure_backlog", "failure_occurrences", "blocked_session", "silence"];

const KIND_RULES = [
  ruleJson({
    id: "k1",
    name: "Failure burst",
    condition: { kind: "failure_occurrences", threshold: 2, windowMinutes: 15, eventTypes: [], statuses: [] },
    state: { status: "Firing", sinceUtc: SINCE, lastValue: 2, pendingNotices: 0, transitions: [] },
  }),
  ruleJson({
    id: "k2",
    name: "Stuck session",
    condition: { kind: "blocked_session", minutes: 30, deferredThreshold: 50, threshold: 1, eventTypes: [], statuses: [] },
    state: { status: "Firing", sinceUtc: SINCE, lastValue: 31, pendingNotices: 0, transitions: [] },
  }),
  ruleJson({
    id: "k3",
    name: "Quiet endpoint",
    enabled: false,
    condition: { kind: "silence", minutes: 60, threshold: 1, eventTypes: [], statuses: [] },
    state: { status: "Ok", lastValue: 0, pendingNotices: 0, transitions: [] },
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
        '5 or more failed events (Failed, DeadLettered) of type OrderPlaced with an error containing "timeout"',
      ),
    ).toBeTruthy();
    expect(within(firing).getByText("Firing since 00:01")).toBeTruthy();
    expect(within(firing).getByText("≥ 5")).toBeTruthy();
    expect(within(firing).getByText("erp@example.com")).toBeTruthy();
    expect(within(firing).getByText("hooks.example.com")).toBeTruthy();
    expect(within(firing).queryByText(/nimbus\/abc/)).toBeNull();

    const ok = await rowOf("Any failure");
    expect(within(ok).getByText("Ok")).toBeTruthy();
    expect(within(ok).getByText("3 or more failed events")).toBeTruthy();
    expect(within(ok).getByText("1")).toBeTruthy();

    const paused = await rowOf("Paused rule");
    expect(within(paused).getByText("Disabled")).toBeTruthy();
    expect(within(paused).getByText("–")).toBeTruthy();

    expect(screen.getByRole("button", { name: "New rule" })).toBeTruthy();
    expect(screen.getAllByRole("button", { name: /^Actions for / })).toHaveLength(3);
    expect(apiMocks.getEndpointAlertRules).toHaveBeenCalledWith("ErpEndpoint");
  });

  it("describes and reads each condition kind in its own words", async () => {
    apiMocks.getEndpointAlertRules.mockResolvedValue(list({ rules: KIND_RULES, availableKinds: ALL_KINDS }));
    renderSection();

    const burst = await rowOf("Failure burst");
    expect(within(burst).getByText("2 or more failures in 15 min")).toBeTruthy();
    expect(within(burst).getByText("≥ 2")).toBeTruthy();
    expect(within(burst).getByText("Resolves after 5 min below the threshold")).toBeTruthy();

    const stuck = await rowOf("Stuck session");
    expect(within(stuck).getByText("a session blocked for 30 min, or 50 or more deferred events")).toBeTruthy();
    expect(within(stuck).getByText("31 min").getAttribute("title")).toBe("Blocked for (minutes)");
    expect(within(stuck).getByText("Resolves after 5 min without the condition")).toBeTruthy();

    const quiet = await rowOf("Quiet endpoint");
    expect(within(quiet).getByText("no completed message for 1 h")).toBeTruthy();
    expect(within(quiet).getByText("0 min")).toBeTruthy();

    expect(screen.getByRole("columnheader", { name: "Last value" })).toBeTruthy();
    expect(screen.queryByText(/failed at once/)).toBeNull();
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
        categories: [],
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

  it("offers only the deployment's condition kinds, and only the backlog when it names none", async () => {
    const user = userEvent.setup();
    renderSection();
    await user.click(await screen.findByRole("button", { name: "New rule" }));
    let kind = within(screen.getByRole("dialog", { name: "New rule" })).getByLabelText("Condition") as HTMLSelectElement;
    expect([...kind.options].map((o) => o.value)).toEqual(["failure_backlog"]);
    cleanup();

    apiMocks.getEndpointAlertRules.mockResolvedValue(list({ availableKinds: ["failure_backlog", "silence"] }));
    renderSection();
    await user.click(await screen.findByRole("button", { name: "New rule" }));
    kind = within(screen.getByRole("dialog", { name: "New rule" })).getByLabelText("Condition") as HTMLSelectElement;
    expect([...kind.options].map((o) => o.value)).toEqual(["failure_backlog", "silence"]);
    expect(kind.value).toBe("failure_backlog");
  });

  it("creates a failures-in-a-window rule with only its own fields", async () => {
    const user = userEvent.setup();
    apiMocks.getEndpointAlertRules.mockResolvedValue(list({ availableKinds: ALL_KINDS }));
    apiMocks.postEndpointAlertRule.mockResolvedValue(api.AlertRuleSaved.fromJS({ rule: KIND_RULES[0], secrets: [] }));
    renderSection();
    await user.click(await screen.findByRole("button", { name: "New rule" }));
    const dialog = screen.getByRole("dialog", { name: "New rule" });

    await user.selectOptions(within(dialog).getByLabelText("Condition"), "failure_occurrences");
    expect(within(dialog).getByText(/failed attempts on ErpEndpoint in the window/)).toBeTruthy();
    expect(within(dialog).queryByLabelText("Failed")).toBeNull();
    expect((within(dialog).getByLabelText("Window (minutes)") as HTMLInputElement).value).toBe("15");

    await user.type(within(dialog).getByLabelText("Name"), "Failure burst");
    await user.clear(within(dialog).getByLabelText("Threshold"));
    await user.type(within(dialog).getByLabelText("Threshold"), "2");
    await user.clear(within(dialog).getByLabelText("Window (minutes)"));
    await user.type(within(dialog).getByLabelText("Window (minutes)"), "1441");
    await user.click(within(dialog).getByRole("button", { name: "Add mail" }));
    await user.type(within(dialog).getByLabelText("Mail address 1"), "erp@example.com");
    await user.click(within(dialog).getByRole("button", { name: "Create rule" }));
    expect(within(dialog).getByText("Enter a whole number of minutes from 5 to 1440.")).toBeTruthy();
    expect(apiMocks.postEndpointAlertRule).not.toHaveBeenCalled();

    await user.clear(within(dialog).getByLabelText("Window (minutes)"));
    await user.type(within(dialog).getByLabelText("Window (minutes)"), "30");
    await user.click(within(dialog).getByRole("button", { name: "Create rule" }));

    await waitFor(() => expect(apiMocks.postEndpointAlertRule).toHaveBeenCalledTimes(1));
    const [, body] = apiMocks.postEndpointAlertRule.mock.calls[0];
    expect(JSON.parse(JSON.stringify(body)).condition).toEqual({
      kind: "failure_occurrences",
      eventTypes: [],
      categories: [],
      threshold: 2,
      windowMinutes: 30,
    });
  });

  it("needs blocked minutes, deferred events or both for a blocked-session rule", async () => {
    const user = userEvent.setup();
    apiMocks.getEndpointAlertRules.mockResolvedValue(list({ availableKinds: ALL_KINDS }));
    apiMocks.postEndpointAlertRule.mockResolvedValue(api.AlertRuleSaved.fromJS({ rule: KIND_RULES[1], secrets: [] }));
    renderSection();
    await user.click(await screen.findByRole("button", { name: "New rule" }));
    const dialog = screen.getByRole("dialog", { name: "New rule" });

    await user.selectOptions(within(dialog).getByLabelText("Condition"), "blocked_session");
    expect(within(dialog).queryByLabelText("Threshold")).toBeNull();
    expect(within(dialog).queryByLabelText("Error text contains")).toBeNull();
    expect((within(dialog).getByLabelText("Blocked for (minutes)") as HTMLInputElement).value).toBe("30");
    expect((within(dialog).getByLabelText("Or deferred events at least") as HTMLInputElement).value).toBe("");

    await user.type(within(dialog).getByLabelText("Name"), "Stuck session");
    await user.clear(within(dialog).getByLabelText("Blocked for (minutes)"));
    await user.click(within(dialog).getByRole("button", { name: "Add mail" }));
    await user.type(within(dialog).getByLabelText("Mail address 1"), "erp@example.com");
    await user.click(within(dialog).getByRole("button", { name: "Create rule" }));
    expect(within(dialog).getByText("Enter the blocked minutes, the deferred events or both.")).toBeTruthy();
    expect(apiMocks.postEndpointAlertRule).not.toHaveBeenCalled();

    await user.type(within(dialog).getByLabelText("Or deferred events at least"), "50");
    await user.click(within(dialog).getByRole("button", { name: "Create rule" }));
    await waitFor(() => expect(apiMocks.postEndpointAlertRule).toHaveBeenCalledTimes(1));
    expect(JSON.parse(JSON.stringify(apiMocks.postEndpointAlertRule.mock.calls[0][1])).condition).toEqual({
      kind: "blocked_session",
      deferredThreshold: 50,
      categories: [],
    });
  });

  it("creates a silence rule with its default minutes", async () => {
    const user = userEvent.setup();
    apiMocks.getEndpointAlertRules.mockResolvedValue(list({ availableKinds: ALL_KINDS }));
    apiMocks.postEndpointAlertRule.mockResolvedValue(api.AlertRuleSaved.fromJS({ rule: KIND_RULES[2], secrets: [] }));
    renderSection();
    await user.click(await screen.findByRole("button", { name: "New rule" }));
    const dialog = screen.getByRole("dialog", { name: "New rule" });

    await user.selectOptions(within(dialog).getByLabelText("Condition"), "silence");
    expect(within(dialog).getByText(/no message on ErpEndpoint has completed/)).toBeTruthy();
    await user.type(within(dialog).getByLabelText("Name"), "Quiet endpoint");
    await user.click(within(dialog).getByRole("button", { name: "Add mail" }));
    await user.type(within(dialog).getByLabelText("Mail address 1"), "erp@example.com");
    await user.click(within(dialog).getByRole("button", { name: "Create rule" }));

    await waitFor(() => expect(apiMocks.postEndpointAlertRule).toHaveBeenCalledTimes(1));
    expect(JSON.parse(JSON.stringify(apiMocks.postEndpointAlertRule.mock.calls[0][1])).condition).toEqual({
      kind: "silence",
      minutes: 60,
      categories: [],
    });
  });

  it("opens an existing rule with its kind and values", async () => {
    const user = userEvent.setup();
    apiMocks.getEndpointAlertRules.mockResolvedValue(list({ rules: KIND_RULES, availableKinds: ALL_KINDS }));
    renderSection();
    await openMenu(user, "Stuck session", "Edit…");
    const dialog = screen.getByRole("dialog", { name: "Edit rule Stuck session" });
    expect((within(dialog).getByLabelText("Condition") as HTMLSelectElement).value).toBe("blocked_session");
    expect((within(dialog).getByLabelText("Blocked for (minutes)") as HTMLInputElement).value).toBe("30");
    expect((within(dialog).getByLabelText("Or deferred events at least") as HTMLInputElement).value).toBe("50");
  });

  it("keeps an existing rule's kind selectable when the deployment no longer offers it", async () => {
    const user = userEvent.setup();
    apiMocks.getEndpointAlertRules.mockResolvedValue(list({ rules: KIND_RULES }));
    renderSection();
    await openMenu(user, "Failure burst", "Edit…");
    const kind = within(screen.getByRole("dialog", { name: "Edit rule Failure burst" })).getByLabelText(
      "Condition",
    ) as HTMLSelectElement;
    expect(kind.value).toBe("failure_occurrences");
    expect([...kind.options].map((o) => o.value)).toEqual(["failure_backlog", "failure_occurrences"]);
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

  it("keeps a rule's condition kind and its fields when disabling or enabling it", async () => {
    const user = userEvent.setup();
    apiMocks.getEndpointAlertRules.mockResolvedValue(list({ rules: KIND_RULES, availableKinds: ALL_KINDS }));
    apiMocks.putEndpointAlertRule.mockResolvedValue(api.AlertRuleSaved.fromJS({ rule: KIND_RULES[1], secrets: [] }));
    renderSection();

    await openMenu(user, "Stuck session", "Disable");
    await waitFor(() => expect(apiMocks.putEndpointAlertRule).toHaveBeenCalledTimes(1));
    let body = JSON.parse(JSON.stringify(apiMocks.putEndpointAlertRule.mock.calls[0][2]));
    expect(body.enabled).toBe(false);
    expect(body.condition).toEqual({ kind: "blocked_session", minutes: 30, deferredThreshold: 50, categories: [] });

    await openMenu(user, "Failure burst", "Disable");
    await waitFor(() => expect(apiMocks.putEndpointAlertRule).toHaveBeenCalledTimes(2));
    body = JSON.parse(JSON.stringify(apiMocks.putEndpointAlertRule.mock.calls[1][2]));
    expect(body.condition).toEqual({
      kind: "failure_occurrences",
      eventTypes: [],
      categories: [],
      threshold: 2,
      windowMinutes: 15,
    });

    await openMenu(user, "Quiet endpoint", "Enable");
    await waitFor(() => expect(apiMocks.putEndpointAlertRule).toHaveBeenCalledTimes(3));
    body = JSON.parse(JSON.stringify(apiMocks.putEndpointAlertRule.mock.calls[2][2]));
    expect(body.enabled).toBe(true);
    expect(body.condition).toEqual({ kind: "silence", minutes: 60, categories: [] });
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

const CATEGORIES = [
  "transient_dependency",
  "authentication_configuration",
  "contract_schema",
  "business_rule",
  "missing_reference_data",
  "application_defect",
  "messaging_platform",
];

const classified = (overrides: Record<string, unknown> = {}) =>
  list({ classificationAvailable: true, categories: CATEGORIES, availableKinds: ALL_KINDS, ...overrides });

const openNewRule = async (user: ReturnType<typeof userEvent.setup>) => {
  await user.click(await screen.findByRole("button", { name: "New rule" }));
  return screen.getByRole("dialog", { name: "New rule" });
};

const HELP = "Counts only failures an agent has classified into one of these. A retry is a new, unclassified attempt.";

describe("AlertRulesSection categories", () => {
  it("describes a rule's categories in its condition", async () => {
    apiMocks.getEndpointAlertRules.mockResolvedValue(
      classified({
        rules: [
          ruleJson({
            condition: {
              kind: "failure_occurrences",
              threshold: 3,
              windowMinutes: 15,
              eventTypes: [],
              statuses: [],
              categories: ["transient_dependency", "contract_schema"],
            },
          }),
        ],
      }),
    );
    renderSection();
    const row = await rowOf("ERP timeouts");
    expect(
      within(row).getByText("3 or more failures in 15 min classified as transient dependency or contract schema"),
    ).toBeTruthy();
  });

  it("offers the categories for backlog and occurrences rules and sends the picked ones", async () => {
    const user = userEvent.setup();
    apiMocks.getEndpointAlertRules.mockResolvedValue(classified());
    apiMocks.postEndpointAlertRule.mockResolvedValue(api.AlertRuleSaved.fromJS({ rule: ruleJson(), secrets: [] }));
    renderSection();
    const dialog = await openNewRule(user);

    const group = within(dialog).getByRole("group", { name: "Categories" });
    expect(within(group).getByText(HELP)).toBeTruthy();
    expect(within(group).getAllByRole("checkbox")).toHaveLength(7);
    await user.click(within(group).getByLabelText("Contract schema"));
    await user.click(within(group).getByLabelText("Transient dependency"));

    await user.selectOptions(within(dialog).getByLabelText("Condition"), "silence");
    expect(within(dialog).queryByRole("group", { name: "Categories" })).toBeNull();
    await user.selectOptions(within(dialog).getByLabelText("Condition"), "failure_occurrences");
    expect(within(dialog).getByRole("group", { name: "Categories" })).toBeTruthy();

    await user.type(within(dialog).getByLabelText("Name"), "Transient bursts");
    await user.click(within(dialog).getByRole("button", { name: "Add mail" }));
    await user.type(within(dialog).getByLabelText("Mail address 1"), "erp@example.com");
    await user.click(within(dialog).getByRole("button", { name: "Create rule" }));

    await waitFor(() => expect(apiMocks.postEndpointAlertRule).toHaveBeenCalledTimes(1));
    const body = JSON.parse(JSON.stringify(apiMocks.postEndpointAlertRule.mock.calls[0][1]));
    // In the order the server lists them, not the order they were ticked.
    expect(body.condition.categories).toEqual(["transient_dependency", "contract_schema"]);
  });

  it("sends no categories for a silence rule even when some were ticked", async () => {
    const user = userEvent.setup();
    apiMocks.getEndpointAlertRules.mockResolvedValue(classified());
    apiMocks.postEndpointAlertRule.mockResolvedValue(api.AlertRuleSaved.fromJS({ rule: ruleJson(), secrets: [] }));
    renderSection();
    const dialog = await openNewRule(user);
    await user.click(within(dialog).getByLabelText("Business rule"));
    await user.selectOptions(within(dialog).getByLabelText("Condition"), "silence");
    await user.type(within(dialog).getByLabelText("Name"), "Quiet");
    await user.click(within(dialog).getByRole("button", { name: "Add mail" }));
    await user.type(within(dialog).getByLabelText("Mail address 1"), "erp@example.com");
    await user.click(within(dialog).getByRole("button", { name: "Create rule" }));

    await waitFor(() => expect(apiMocks.postEndpointAlertRule).toHaveBeenCalledTimes(1));
    expect(JSON.parse(JSON.stringify(apiMocks.postEndpointAlertRule.mock.calls[0][1])).condition.categories).toEqual([]);
  });

  it("hides the categories when classifications can't be read", async () => {
    const user = userEvent.setup();
    apiMocks.getEndpointAlertRules.mockResolvedValue(classified({ classificationAvailable: false, categories: [] }));
    renderSection();
    const dialog = await openNewRule(user);
    expect(within(dialog).queryByRole("group", { name: "Categories" })).toBeNull();
    expect(within(dialog).queryByText(HELP)).toBeNull();
  });

  it("opens a rule with its categories ticked", async () => {
    const user = userEvent.setup();
    apiMocks.getEndpointAlertRules.mockResolvedValue(
      classified({ rules: [ruleJson({ condition: { kind: "failure_backlog", threshold: 5, categories: ["business_rule"] } })] }),
    );
    renderSection();
    await openMenu(user, "ERP timeouts", "Edit…");
    const dialog = screen.getByRole("dialog", { name: "Edit rule ERP timeouts" });
    expect((within(dialog).getByLabelText("Business rule") as HTMLInputElement).checked).toBe(true);
    expect((within(dialog).getByLabelText("Contract schema") as HTMLInputElement).checked).toBe(false);
  });

  it("keeps a rule's categories when disabling it", async () => {
    const user = userEvent.setup();
    const categories = ["transient_dependency", "contract_schema"];
    apiMocks.getEndpointAlertRules.mockResolvedValue(
      classified({ rules: [ruleJson({ condition: { kind: "failure_backlog", threshold: 5, categories } })] }),
    );
    apiMocks.putEndpointAlertRule.mockResolvedValue(api.AlertRuleSaved.fromJS({ rule: ruleJson(), secrets: [] }));
    renderSection();
    await openMenu(user, "ERP timeouts", "Disable");
    await waitFor(() => expect(apiMocks.putEndpointAlertRule).toHaveBeenCalledTimes(1));
    const body = JSON.parse(JSON.stringify(apiMocks.putEndpointAlertRule.mock.calls[0][2]));
    expect(body.enabled).toBe(false);
    expect(body.condition.categories).toEqual(categories);
  });
});

const at = (hours: number, minutes: number) =>
  moment().startOf("day").add(hours, "hours").add(minutes, "minutes").toISOString();

const previewJson = (overrides: Record<string, unknown> = {}) =>
  api.AlertRulePreview.fromJS({
    valueNow: 3,
    holdsNow: true,
    replay: {
      fromUtc: at(-12, 0),
      toUtc: at(12, 0),
      series: [
        { atUtc: at(9, 0), value: 1 },
        { atUtc: at(9, 15), value: 4 },
        { atUtc: at(9, 30), value: 0 },
      ],
      periods: [{ fromUtc: at(9, 14), toUtc: at(9, 31) }, { fromUtc: at(11, 2) }],
    },
    ...overrides,
  });

const preview = (dialog: HTMLElement) => within(dialog).getByRole("region", { name: "Preview" });

describe("AlertRulesSection preview", () => {
  it("previews only when asked, with the draft's condition and resolve time", async () => {
    const user = userEvent.setup();
    apiMocks.getEndpointAlertRules.mockResolvedValue(classified());
    apiMocks.postEndpointAlertRulePreview.mockResolvedValue(previewJson());
    renderSection();
    const dialog = await openNewRule(user);
    await user.selectOptions(within(dialog).getByLabelText("Condition"), "failure_occurrences");
    await user.clear(within(dialog).getByLabelText("Threshold"));
    await user.type(within(dialog).getByLabelText("Threshold"), "3");
    await user.clear(within(dialog).getByLabelText("Resolve after (minutes)"));
    await user.type(within(dialog).getByLabelText("Resolve after (minutes)"), "10");
    expect(apiMocks.postEndpointAlertRulePreview).not.toHaveBeenCalled();

    // No name and no targets: neither blocks a preview.
    await user.click(within(preview(dialog)).getByRole("button", { name: "Preview" }));

    await waitFor(() => expect(apiMocks.postEndpointAlertRulePreview).toHaveBeenCalledTimes(1));
    const [endpointId, body] = apiMocks.postEndpointAlertRulePreview.mock.calls[0];
    expect(endpointId).toBe("ErpEndpoint");
    expect(JSON.parse(JSON.stringify(body))).toEqual({
      condition: { kind: "failure_occurrences", eventTypes: [], categories: [], threshold: 3, windowMinutes: 15 },
      resolveAfterMinutes: 10,
    });
    expect(within(dialog).queryByText(/Enter a name/)).toBeNull();
    expect(apiMocks.postEndpointAlertRule).not.toHaveBeenCalled();
  });

  it("doesn't preview a condition that isn't valid", async () => {
    const user = userEvent.setup();
    renderSection();
    const dialog = await openNewRule(user);
    await user.clear(within(dialog).getByLabelText("Threshold"));
    await user.type(within(dialog).getByLabelText("Threshold"), "0");
    await user.click(within(preview(dialog)).getByRole("button", { name: "Preview" }));
    expect(within(preview(dialog)).getByText("Correct the condition to preview it.")).toBeTruthy();
    expect(apiMocks.postEndpointAlertRulePreview).not.toHaveBeenCalled();
  });

  it("says whether the rule would fire now and when it would have fired", async () => {
    const user = userEvent.setup();
    apiMocks.getEndpointAlertRules.mockResolvedValue(classified());
    apiMocks.postEndpointAlertRulePreview.mockResolvedValue(previewJson());
    renderSection();
    const dialog = await openNewRule(user);
    await user.selectOptions(within(dialog).getByLabelText("Condition"), "failure_occurrences");
    await user.clear(within(dialog).getByLabelText("Threshold"));
    await user.type(within(dialog).getByLabelText("Threshold"), "3");
    await user.click(within(preview(dialog)).getByRole("button", { name: "Preview" }));

    const panel = preview(dialog);
    const now = await within(panel).findByText("Would fire now");
    expect(now.closest("p")?.textContent).toBe("Would fire nowFailures in the window: ≥ 3");
    expect(within(panel).getByRole("img", { name: /over the last 24 hours/ })).toBeTruthy();
    expect(panel.querySelector("[data-reference]")).not.toBeNull();
    const periods = within(panel).getByRole("list", { name: "Would have fired" });
    expect(within(periods).getAllByRole("listitem").map((li) => li.textContent)).toEqual([
      "09:14–09:31",
      "since 11:02",
    ]);
    expect(within(panel).getByRole("button", { name: "Refresh" })).toBeTruthy();
  });

  it("says when the rule would not have fired", async () => {
    const user = userEvent.setup();
    apiMocks.getEndpointAlertRules.mockResolvedValue(classified());
    apiMocks.postEndpointAlertRulePreview.mockResolvedValue(
      previewJson({
        valueNow: 0,
        holdsNow: false,
        replay: { fromUtc: at(-12, 0), toUtc: at(12, 0), series: [{ atUtc: at(9, 0), value: 0 }], periods: [] },
      }),
    );
    renderSection();
    const dialog = await openNewRule(user);
    await user.selectOptions(within(dialog).getByLabelText("Condition"), "silence");
    await user.click(within(preview(dialog)).getByRole("button", { name: "Preview" }));

    const panel = preview(dialog);
    const now = await within(panel).findByText("Would not fire now");
    expect(now.closest("p")?.textContent).toBe("Would not fire nowSilent for: 0 min");
    expect(within(panel).getByText("Would not have fired in the last 24 hours.")).toBeTruthy();
  });

  it("explains why there is no replay or no value now", async () => {
    const user = userEvent.setup();
    apiMocks.getEndpointAlertRules.mockResolvedValue(classified());
    apiMocks.postEndpointAlertRulePreview.mockResolvedValueOnce(
      previewJson({ replay: undefined, replayUnsupportedReason: "no_history" }),
    );
    renderSection();
    const dialog = await openNewRule(user);
    await user.click(within(preview(dialog)).getByRole("button", { name: "Preview" }));
    expect(
      await within(preview(dialog)).findByText(
        "Backlog and blocked-session rules can't be replayed: deleted events leave no history.",
      ),
    ).toBeTruthy();
    expect(within(preview(dialog)).queryByRole("img")).toBeNull();

    apiMocks.postEndpointAlertRulePreview.mockResolvedValueOnce(
      previewJson({
        valueNow: null,
        holdsNow: null,
        nowUnavailableReason: "classification_unavailable",
        replay: undefined,
        replayUnsupportedReason: "categories",
      }),
    );
    await user.click(within(dialog).getByLabelText("Business rule"));
    await user.click(within(preview(dialog)).getByRole("button", { name: "Refresh" }));
    expect(
      await within(preview(dialog)).findByText(
        "Rules with categories can't be replayed: classifications arrive after the failure.",
      ),
    ).toBeTruthy();
    expect(
      within(preview(dialog)).getByText("The failure classifications can't be read, so there is no value now."),
    ).toBeTruthy();
    expect(within(preview(dialog)).queryByText(/Would (not )?fire now/)).toBeNull();
  });

  it("asks to wait when previews are rate limited", async () => {
    const user = userEvent.setup();
    apiMocks.postEndpointAlertRulePreview.mockRejectedValue(
      new api.SwaggerException("An unexpected server error occurred.", 429, "", {}, null),
    );
    renderSection();
    const dialog = await openNewRule(user);
    await user.click(within(preview(dialog)).getByRole("button", { name: "Preview" }));
    expect(await within(preview(dialog)).findByText("Too many previews; try again in a minute.")).toBeTruthy();
  });

  it("shows the server's problem when a preview fails", async () => {
    const user = userEvent.setup();
    apiMocks.postEndpointAlertRulePreview.mockRejectedValue(
      new api.SwaggerException(
        "Bad Request",
        400,
        JSON.stringify({ errors: ["condition.threshold must be 1 to 100."] }),
        {},
        null,
      ),
    );
    renderSection();
    const dialog = await openNewRule(user);
    await user.click(within(preview(dialog)).getByRole("button", { name: "Preview" }));
    expect(await within(preview(dialog)).findByText("condition.threshold must be 1 to 100.")).toBeTruthy();
  });

  it("marks a preview stale once the condition changes, and refreshes it on request", async () => {
    const user = userEvent.setup();
    apiMocks.postEndpointAlertRulePreview.mockResolvedValue(
      previewJson({ replay: undefined, replayUnsupportedReason: "no_history" }),
    );
    renderSection();
    const dialog = await openNewRule(user);
    await user.click(within(preview(dialog)).getByRole("button", { name: "Preview" }));
    await within(preview(dialog)).findByText("Would fire now");
    const stale = "The condition changed since this preview. Refresh to see the new one.";
    expect(within(preview(dialog)).queryByText(stale)).toBeNull();

    // A name or a target is not part of the preview.
    await user.type(within(dialog).getByLabelText("Name"), "Renamed");
    expect(within(preview(dialog)).queryByText(stale)).toBeNull();

    await user.clear(within(dialog).getByLabelText("Threshold"));
    await user.type(within(dialog).getByLabelText("Threshold"), "8");
    expect(within(preview(dialog)).getByText(stale)).toBeTruthy();
    expect(apiMocks.postEndpointAlertRulePreview).toHaveBeenCalledTimes(1);

    await user.click(within(preview(dialog)).getByRole("button", { name: "Refresh" }));
    await waitFor(() => expect(apiMocks.postEndpointAlertRulePreview).toHaveBeenCalledTimes(2));
    expect(
      JSON.parse(JSON.stringify(apiMocks.postEndpointAlertRulePreview.mock.calls[1][1])).condition.threshold,
    ).toBe(8);
    await waitFor(() => expect(within(preview(dialog)).queryByText(stale)).toBeNull());
  });
});
