import { describe, expect, it } from "vitest";
import moment from "moment";
import * as api from "api-client";
import {
  describeCondition,
  draftFromRule,
  emptyDraft,
  evaluatorFreshness,
  failedEventsText,
  requestFromDraft,
  requestFromRule,
  ruleStatus,
  targetsSummary,
  validateDraft,
  type RuleDraft,
} from "./alert-rules-model";

const rule = (json: Record<string, unknown> = {}) =>
  api.AlertRuleModel.fromJS({
    id: "r1",
    endpointId: "ErpEndpoint",
    name: "ERP timeouts",
    enabled: true,
    condition: { kind: "failure_backlog", eventTypes: [], statuses: [], threshold: 5 },
    resolveAfterMinutes: 5,
    targets: [
      { id: "t1", type: "mail", address: "erp@example.com" },
      { id: "t2", type: "webhook", url: "https://hooks.example.com/nimbus" },
    ],
    state: { status: "Ok", pendingNotices: 0, transitions: [] },
    ...json,
  });

const delivery = { mailEnabled: true, webhookEnabled: true };

const valid = (changes: Partial<RuleDraft> = {}): RuleDraft => ({
  ...emptyDraft(),
  name: "ERP timeouts",
  targets: [{ key: "k1", type: "mail", value: "erp@example.com" }],
  ...changes,
});

describe("alert rule words", () => {
  it("describes the condition with its filters", () => {
    expect(describeCondition(new api.AlertRuleCondition({ threshold: 1 }))).toBe("≥ 1 failed event");
    expect(
      describeCondition(
        new api.AlertRuleCondition({
          threshold: 5,
          statuses: ["Failed", "DeadLettered"],
          eventTypes: ["OrderPlaced"],
          errorTextContains: "timeout",
        }),
      ),
    ).toBe('≥ 5 failed events (Failed, DeadLettered) of type OrderPlaced, error contains "timeout"');
    expect(describeCondition(new api.AlertRuleCondition({ threshold: 3, eventTypes: ["A", "B"] }))).toBe(
      "≥ 3 failed events of types A, B",
    );
  });

  it("caps the failed-event count at the threshold", () => {
    expect(failedEventsText(rule())).toBe("–");
    expect(failedEventsText(rule({ state: { status: "Ok", lastValue: 2 } }))).toBe("2");
    expect(failedEventsText(rule({ state: { status: "Firing", lastValue: 5 } }))).toBe("≥ 5");
    expect(failedEventsText(rule({ state: { status: "Ok", lastValue: 0 } }))).toBe("0");
  });

  it("says Disabled, Firing since a time, or Ok", () => {
    const now = moment("2026-11-20T12:00:00Z");
    const since = moment("2026-11-20T09:14:00Z");
    expect(ruleStatus(rule({ enabled: false }), now).label).toBe("Disabled");
    const firing = ruleStatus(rule({ state: { status: "Firing", sinceUtc: since.toISOString() } }), now);
    expect(firing.variant).toBe("error");
    expect(firing.label).toBe(`Firing since ${since.clone().local().format("HH:mm")}`);
    expect(ruleStatus(rule(), now)).toMatchObject({ label: "Ok", variant: "success" });
  });

  it("summarises targets by number and kind", () => {
    expect(targetsSummary([new api.AlertRuleTargetModel({ id: "t1", type: "mail" })])).toBe("1 target (mail)");
    expect(
      targetsSummary([
        new api.AlertRuleTargetModel({ id: "t1", type: "mail" }),
        new api.AlertRuleTargetModel({ id: "t2", type: "webhook" }),
        new api.AlertRuleTargetModel({ id: "t3", type: "mail" }),
      ]),
    ).toBe("3 targets (mail, webhook)");
  });

  it("reports when the evaluator last ran and warns when it has stopped", () => {
    const now = moment("2026-11-20T12:00:00Z");
    expect(evaluatorFreshness(undefined, now)).toEqual({ kind: "never", text: "Not evaluated yet" });
    expect(evaluatorFreshness(now.clone().subtract(40, "seconds"), now)).toEqual({
      kind: "fresh",
      text: "Last evaluated a few seconds ago",
    });
    const stale = evaluatorFreshness(now.clone().subtract(20, "minutes"), now);
    expect(stale.kind).toBe("stale");
    expect(stale.text).toBe(
      "The rule evaluator has not run for 20 minutes. Rules are evaluated only while the WebApp runs.",
    );
  });
});

describe("alert rule draft", () => {
  it("accepts a valid draft", () => {
    expect(validateDraft(valid(), delivery)).toEqual({});
  });

  it("mirrors the server's limits", () => {
    const errors = validateDraft(
      valid({
        name: " ",
        threshold: "101",
        resolveAfterMinutes: "0",
        errorTextContains: "x".repeat(201),
        targets: [],
      }),
      delivery,
    );
    expect(Object.keys(errors).sort()).toEqual(
      ["errorTextContains", "name", "resolveAfterMinutes", "targets", "threshold"].sort(),
    );
    expect(validateDraft(valid({ threshold: "2.5" }), delivery).threshold).toBeTruthy();
    expect(validateDraft(valid({ name: "x".repeat(101) }), delivery).name).toBeTruthy();
    const eleven = Array.from({ length: 11 }, (_, i) => ({ key: `k${i}`, type: "mail" as const, value: "a@example.com" }));
    expect(validateDraft(valid({ targets: eleven }), delivery).targets).toBe("A rule has at most 10 targets.");
  });

  it("checks each target and the deployment's delivery settings", () => {
    const targets = [
      { key: "m", type: "mail" as const, value: "not-an-address" },
      { key: "w", type: "webhook" as const, value: "http://hooks.example.com" },
    ];
    expect(validateDraft(valid({ targets }), delivery)).toEqual({
      "target:m": "Enter a mail address.",
      "target:w": "Enter an https URL.",
    });
    expect(
      Object.keys(validateDraft(valid({ targets }), { mailEnabled: false, webhookEnabled: false })),
    ).toEqual(["target:m", "target:w"]);
  });

  it("keeps target ids so the server keeps the targets and their secrets", () => {
    const request = requestFromRule(rule(), { enabled: false });
    expect(request.enabled).toBe(false);
    expect(request.name).toBe("ERP timeouts");
    expect(request.condition?.threshold).toBe(5);
    expect(request.resolveAfterMinutes).toBe(5);
    expect(request.targets?.map((t) => [t.id, t.type, t.address, t.url])).toEqual([
      ["t1", "mail", "erp@example.com", undefined],
      ["t2", "webhook", undefined, "https://hooks.example.com/nimbus"],
    ]);
  });

  it("omits the id of a new target and sends no blank error filter", () => {
    const draft = draftFromRule(rule());
    draft.targets.push({ key: "new", type: "webhook", value: " https://other.example.com/x " });
    const json = JSON.parse(JSON.stringify(requestFromDraft(draft)));
    expect(json.targets[2]).toEqual({ type: "webhook", url: "https://other.example.com/x" });
    expect(json.condition.errorTextContains).toBeUndefined();
    expect(json.condition.kind).toBe("failure_backlog");
  });
});
