import { describe, expect, it } from "vitest";
import moment from "moment";
import * as api from "api-client";
import {
  availableKindsOf,
  describeCondition,
  draftFromRule,
  emptyDraft,
  evaluatorFreshness,
  lastValueText,
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
  it("describes a backlog condition with its filters, like the notices", () => {
    expect(describeCondition(new api.AlertRuleCondition({ threshold: 1 }))).toBe("1 or more failed event");
    expect(
      describeCondition(
        new api.AlertRuleCondition({
          kind: "failure_backlog",
          threshold: 5,
          statuses: ["Failed", "DeadLettered"],
          eventTypes: ["OrderPlaced"],
          errorTextContains: "timeout",
        }),
      ),
    ).toBe('5 or more failed events (Failed, DeadLettered) of type OrderPlaced with an error containing "timeout"');
    expect(describeCondition(new api.AlertRuleCondition({ threshold: 3, eventTypes: ["A", "B"] }))).toBe(
      "3 or more failed events of type A or B",
    );
  });

  it("describes the other condition kinds in the notices' words", () => {
    expect(
      describeCondition(
        new api.AlertRuleCondition({
          kind: "failure_occurrences",
          threshold: 3,
          windowMinutes: 15,
          statuses: ["Failed"],
          eventTypes: ["OrderPlaced"],
        }),
      ),
    ).toBe("3 or more failures in 15 min of type OrderPlaced");
    expect(
      describeCondition(new api.AlertRuleCondition({ kind: "failure_occurrences", threshold: 1, windowMinutes: 90 })),
    ).toBe("1 or more failure in 1 h 30 min");
    expect(
      describeCondition(new api.AlertRuleCondition({ kind: "blocked_session", minutes: 30, deferredThreshold: 50 })),
    ).toBe("a session blocked for 30 min, or 50 or more deferred events");
    expect(describeCondition(new api.AlertRuleCondition({ kind: "blocked_session", deferredThreshold: 1 }))).toBe(
      "1 or more deferred event",
    );
    expect(describeCondition(new api.AlertRuleCondition({ kind: "silence", minutes: 60 }))).toBe(
      "no completed message for 1 h",
    );
  });

  it("caps the failed-event count at the threshold", () => {
    expect(lastValueText(rule())).toBe("–");
    expect(lastValueText(rule({ state: { status: "Ok", lastValue: 2 } }))).toBe("2");
    expect(lastValueText(rule({ state: { status: "Firing", lastValue: 5 } }))).toBe("≥ 5");
    expect(lastValueText(rule({ state: { status: "Ok", lastValue: 0 } }))).toBe("0");
  });

  it("formats the last value per condition kind", () => {
    const occurrences = { kind: "failure_occurrences", threshold: 2, windowMinutes: 15 };
    expect(lastValueText(rule({ condition: occurrences, state: { status: "Firing", lastValue: 2 } }))).toBe("≥ 2");
    expect(lastValueText(rule({ condition: occurrences, state: { status: "Ok", lastValue: 1 } }))).toBe("1");
    const blocked = { kind: "blocked_session", minutes: 30, deferredThreshold: 50 };
    expect(lastValueText(rule({ condition: blocked, state: { status: "Firing", lastValue: 31 } }))).toBe("31 min");
    expect(lastValueText(rule({ condition: blocked, state: { status: "Firing", lastValue: 95 } }))).toBe("1 h 35 min");
    expect(lastValueText(rule({ condition: blocked, state: { status: "Ok" } }))).toBe("–");
    // A deferred-only rule reads no blocked time, so it has no minutes to show.
    const deferredOnly = { kind: "blocked_session", deferredThreshold: 50 };
    expect(lastValueText(rule({ condition: deferredOnly, state: { status: "Firing", lastValue: 0 } }))).toBe("–");
    const silence = { kind: "silence", minutes: 45 };
    expect(lastValueText(rule({ condition: silence, state: { status: "Firing", lastValue: 45 } }))).toBe("≥ 45 min");
    expect(lastValueText(rule({ condition: silence, state: { status: "Ok", lastValue: 0 } }))).toBe("0 min");
  });

  it("offers the deployment's condition kinds, or the backlog when it names none", () => {
    expect(availableKindsOf(undefined)).toEqual(["failure_backlog"]);
    expect(availableKindsOf([])).toEqual(["failure_backlog"]);
    expect(availableKindsOf(["failure_backlog", "silence", "unknown_kind"])).toEqual(["failure_backlog", "silence"]);
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

describe("alert rule condition kinds", () => {
  const conditionOf = (draft: RuleDraft) => JSON.parse(JSON.stringify(requestFromDraft(draft))).condition;

  it("starts a new rule as a backlog with each kind's defaults", () => {
    const draft = emptyDraft();
    expect(draft.kind).toBe("failure_backlog");
    expect(draft.windowMinutes).toBe("15");
    expect(draft.blockedMinutes).toBe("30");
    expect(draft.deferredThreshold).toBe("");
    expect(draft.silenceMinutes).toBe("60");
  });

  it("sends only the fields of the chosen kind", () => {
    const filters = { statuses: ["Failed"], eventTypes: ["OrderPlaced"], errorTextContains: "timeout" };
    expect(conditionOf(valid({ ...filters, kind: "failure_occurrences", threshold: "3", windowMinutes: "20" }))).toEqual({
      kind: "failure_occurrences",
      eventTypes: ["OrderPlaced"],
      errorTextContains: "timeout",
      threshold: 3,
      windowMinutes: 20,
    });
    expect(conditionOf(valid({ ...filters, kind: "blocked_session", blockedMinutes: "45", deferredThreshold: "" }))).toEqual({
      kind: "blocked_session",
      minutes: 45,
    });
    expect(conditionOf(valid({ kind: "blocked_session", blockedMinutes: " ", deferredThreshold: "50" }))).toEqual({
      kind: "blocked_session",
      deferredThreshold: 50,
    });
    expect(conditionOf(valid({ ...filters, kind: "silence", silenceMinutes: "90" }))).toEqual({
      kind: "silence",
      minutes: 90,
    });
  });

  it("mirrors the server's limits for each kind", () => {
    const errorsOf = (changes: Partial<RuleDraft>) => validateDraft(valid(changes), delivery);
    expect(errorsOf({ kind: "failure_occurrences", windowMinutes: "4" })).toEqual({
      windowMinutes: "Enter a whole number of minutes from 5 to 1440.",
    });
    expect(errorsOf({ kind: "failure_occurrences", windowMinutes: "", threshold: "0" })).toEqual({
      threshold: "Enter a whole number from 1 to 100.",
      windowMinutes: "Enter a whole number of minutes from 5 to 1440.",
    });
    // A kind ignores the fields of the other kinds.
    expect(errorsOf({ kind: "failure_backlog", windowMinutes: "", silenceMinutes: "" })).toEqual({});
    expect(errorsOf({ kind: "silence", threshold: "", silenceMinutes: "60" })).toEqual({});
    expect(errorsOf({ kind: "silence", silenceMinutes: "4" })).toEqual({
      silenceMinutes: "Enter a whole number of minutes from 5 to 1440.",
    });
    expect(errorsOf({ kind: "blocked_session", blockedMinutes: "", deferredThreshold: "" })).toEqual({
      blocked: "Enter the blocked minutes, the deferred events or both.",
    });
    expect(errorsOf({ kind: "blocked_session", blockedMinutes: "1441", deferredThreshold: "1001" })).toEqual({
      blockedMinutes: "Enter a whole number of minutes from 1 to 1440.",
      deferredThreshold: "Enter a whole number from 1 to 1000.",
    });
    expect(errorsOf({ kind: "blocked_session", blockedMinutes: "", deferredThreshold: "1" })).toEqual({});
  });

  it("opens an existing rule with its kind and values", () => {
    const occurrences = draftFromRule(
      rule({ condition: { kind: "failure_occurrences", threshold: 2, windowMinutes: 30, eventTypes: ["A"] } }),
    );
    expect(occurrences).toMatchObject({ kind: "failure_occurrences", threshold: "2", windowMinutes: "30", eventTypes: ["A"] });
    const blocked = draftFromRule(rule({ condition: { kind: "blocked_session", deferredThreshold: 50 } }));
    expect(blocked).toMatchObject({ kind: "blocked_session", blockedMinutes: "", deferredThreshold: "50" });
    const silence = draftFromRule(rule({ condition: { kind: "silence", minutes: 120 } }));
    expect(silence).toMatchObject({ kind: "silence", silenceMinutes: "120", blockedMinutes: "30" });
    expect(draftFromRule(rule({ condition: { threshold: 5 } })).kind).toBe("failure_backlog");
  });

  it("keeps each kind's condition when a rule is disabled or enabled", () => {
    const roundTrip = (condition: Record<string, unknown>, enabled: boolean) =>
      JSON.parse(JSON.stringify(requestFromRule(rule({ condition }), { enabled })));

    const occurrences = roundTrip(
      { kind: "failure_occurrences", threshold: 2, windowMinutes: 30, eventTypes: ["A"], errorTextContains: "x" },
      false,
    );
    expect(occurrences.enabled).toBe(false);
    expect(occurrences.condition).toEqual({
      kind: "failure_occurrences",
      eventTypes: ["A"],
      errorTextContains: "x",
      threshold: 2,
      windowMinutes: 30,
    });
    expect(roundTrip({ kind: "blocked_session", minutes: 30, deferredThreshold: 50 }, false).condition).toEqual({
      kind: "blocked_session",
      minutes: 30,
      deferredThreshold: 50,
    });
    expect(roundTrip({ kind: "blocked_session", deferredThreshold: 50 }, true).condition).toEqual({
      kind: "blocked_session",
      deferredThreshold: 50,
    });
    expect(roundTrip({ kind: "silence", minutes: 120 }, true).condition).toEqual({ kind: "silence", minutes: 120 });
  });
});
