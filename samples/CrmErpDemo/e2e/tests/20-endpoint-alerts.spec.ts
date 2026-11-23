import { createHmac, randomUUID, timingSafeEqual } from "node:crypto";
import { request, type APIRequestContext } from "@playwright/test";
import { test, expect } from "../helpers/lifecycle-fixture.js";
import { ServiceUrls } from "../helpers/service-urls.js";
import { waitFor } from "../helpers/wait-for.js";

/**
 * Endpoint alerts end to end: an Owner subscribes on the endpoint's Alerts tab, an event on the
 * endpoint fails, and the Resolver tells the subscriber by mail (Mailpit locally) and by a signed
 * webhook (the ERP API's recording receiver). Unit tests cover the pieces; this covers the chain:
 * WebApp subscribe API → storage → Resolver outcome → notifier → SMTP and HTTP delivery.
 */

interface ReceivedEndpointAlert {
  body: string;
  subscription: string | null;
  notice: string | null;
  delivery: string | null;
  timestamp: string | null;
  signature: string | null;
}

interface EndpointAlertBody {
  version: number;
  endpointId: string;
  eventId: string;
  sessionId: string;
  eventTypeId: string;
  status: string;
}

interface MailpitSearch {
  messages: Array<{ ID: string; Subject: string; To: Array<{ Address: string }> }>;
}

/** The signature a receiver must check: v1= + hex HMAC-SHA256(secret, "<timestamp>.<body>"). */
function signatureMatches(secret: string, received: ReceivedEndpointAlert): boolean {
  if (!received.timestamp || !received.signature?.startsWith("v1=")) return false;
  const expected = createHmac("sha256", secret).update(`${received.timestamp}.${received.body}`).digest("hex");
  const actual = received.signature.slice("v1=".length);
  return actual.length === expected.length && timingSafeEqual(Buffer.from(actual), Buffer.from(expected));
}

test("a failed event reaches an alert subscriber by mail and by a signed webhook", async ({ harness }) => {
  const flow = await harness.createFlow("crm-to-erp");
  const mailbox = `e2e-alerts-${randomUUID()}@example.com`;
  const erp = await request.newContext({ baseURL: ServiceUrls.erpApi, ignoreHTTPSErrors: true });
  const mailpit = await request.newContext({ baseURL: ServiceUrls.mailpit });
  // Frequency 0 notifies on every failure, so another test's failure on the same endpoint
  // can't use up this subscription's window; the assertions pick this test's event by id.
  const subscription = await harness.nimbus.subscribeToAlerts(flow.endpoint, {
    type: "mail;webhook",
    mail: mailbox,
    url: `${ServiceUrls.erpApi}/api/webhooks/endpoint-alerts`,
    eventTypes: [flow.eventType],
    frequency: 0,
  });
  try {
    expect(subscription.signingSecret, "A webhook subscription returns its signing secret once").toMatch(/^nbwh_/);

    await flow.script(["fail"]);
    await flow.update("endpoint-alert");
    const [failed] = await flow.status("Failed");

    const webhook = await waitFor(async () => {
      const response = await erp.get("/api/admin/endpoint-alerts");
      if (!response.ok()) throw new Error(`Read endpoint alerts: ${response.status()}`);
      const received = await response.json() as ReceivedEndpointAlert[];
      return received.find(r => r.subscription === subscription.id
        && (JSON.parse(r.body) as EndpointAlertBody).eventId === failed.eventId) ?? null;
    }, { timeoutMs: 60_000, description: "Signed endpoint-alert webhook for the failed event" });

    const body = JSON.parse(webhook.body) as EndpointAlertBody;
    expect(body).toMatchObject({
      version: 1,
      endpointId: flow.endpoint,
      eventId: failed.eventId,
      sessionId: failed.sessionId,
      eventTypeId: flow.eventType,
      status: "Failed",
    });
    expect(webhook.body, "The webhook carries ids only, never the payload or error text").not.toContain("E2E fail");
    expect(webhook.delivery).toMatch(/^[0-9a-f-]{36}$/);
    expect(signatureMatches(subscription.signingSecret!, webhook), "X-NimBus-Signature verifies with the secret").toBe(true);

    const mail = await waitFor(async () => {
      const response = await mailpit.get("/api/v1/search", { params: { query: `to:"${mailbox}"` } });
      if (!response.ok()) throw new Error(`Search Mailpit: ${response.status()}`);
      const found = await response.json() as MailpitSearch;
      for (const message of found.messages ?? []) {
        const text = await (await mailpit.get(`/api/v1/message/${message.ID}`)).json() as { Text: string };
        if (text.Text.includes(failed.eventId)) return { subject: message.Subject, text: text.Text };
      }
      return null;
    }, { timeoutMs: 60_000, description: "Alert mail in Mailpit naming the failed event" });

    expect(mail.subject).toContain(flow.endpoint);
    expect(mail.text).toContain(flow.eventType);
  } finally {
    await harness.nimbus.unsubscribeFromAlerts(flow.endpoint, subscription.id);
    await Promise.all([erp.dispose(), mailpit.dispose()]);
  }
});

interface RuleNoticeBody {
  version: number;
  kind: string;
  noticeId: string;
  ruleId: string;
  endpointId: string;
  conditionKind: string;
  status: "Firing" | "Resolved";
  reason: string | null;
  value: number | null;
  threshold: number;
}

/**
 * Alert rules end to end (Spec 053): a rule on the failure backlog opens when an event fails and
 * resolves once it is skipped, telling its webhook and mail targets once each time. The WebApp's
 * evaluator runs every minute, so this test takes a few minutes.
 */
test("an alert rule opens on a failed event and resolves when it is cleared", async ({ harness }) => {
  test.setTimeout(8 * 60_000);
  const flow = await harness.createFlow("crm-to-erp");
  const mailbox = `e2e-rule-${randomUUID()}@example.com`;
  const erp = await request.newContext({ baseURL: ServiceUrls.erpApi, ignoreHTTPSErrors: true });
  const mailpit = await request.newContext({ baseURL: ServiceUrls.mailpit });
  const saved = await harness.nimbus.createAlertRule(flow.endpoint, {
    name: `E2E backlog ${flow.session}`,
    enabled: true,
    // Failed only: earlier scenarios leave dead-lettered events of this type behind on purpose,
    // and a dead letter is never cleared, so a rule counting them would never resolve.
    condition: { threshold: 1, eventTypes: [flow.eventType], statuses: ["Failed"] },
    resolveAfterMinutes: 1,
    targets: [
      { type: "webhook", url: `${ServiceUrls.erpApi}/api/webhooks/endpoint-alerts` },
      { type: "mail", address: mailbox },
    ],
  });
  const ruleId = saved.rule.id;
  const webhookTarget = saved.rule.targets.find(t => t.type === "webhook")!;
  const secret = saved.secrets.find(s => s.targetId === webhookTarget.id)?.signingSecret;
  try {
    expect(secret, "A new webhook target returns its signing secret once").toMatch(/^nbwh_/);

    const notices = async (status: RuleNoticeBody["status"]) => {
      const response = await erp.get("/api/admin/endpoint-alerts");
      if (!response.ok()) throw new Error(`Read endpoint alerts: ${response.status()}`);
      return (await response.json() as ReceivedEndpointAlert[])
        .map(r => ({ received: r, body: JSON.parse(r.body) as RuleNoticeBody }))
        .filter(n => n.body.kind === "alert_rule" && n.body.ruleId === ruleId && n.body.status === status);
    };

    await flow.script(["fail"]);
    await flow.update("alert-rule");
    const [failed] = await flow.status("Failed");

    const opened = await waitFor(async () => (await notices("Firing"))[0] ?? null,
      { timeoutMs: 180_000, description: "Opened alert rule notice" });
    expect(opened.body).toMatchObject({ version: 2, endpointId: flow.endpoint, value: 1, threshold: 1, reason: null });
    expect(opened.received.notice).toBe(opened.body.noticeId);
    expect(signatureMatches(secret!, opened.received), "The rule notice verifies with the target's secret").toBe(true);

    await waitFor(async () => {
      const response = await mailpit.get("/api/v1/search", { params: { query: `to:"${mailbox}"` } });
      if (!response.ok()) throw new Error(`Search Mailpit: ${response.status()}`);
      const found = await response.json() as MailpitSearch;
      return (found.messages ?? []).find(m => m.Subject.startsWith("[NimBus] Firing:")) ?? null;
    }, { timeoutMs: 60_000, description: "Opened alert rule mail" });

    await harness.nimbus.skipMessage(failed.eventId, failed.lastMessageId);

    const resolved = await waitFor(async () => (await notices("Resolved"))[0] ?? null,
      { timeoutMs: 240_000, description: "Resolved alert rule notice" });
    expect(resolved.body.reason).toBe("cleared");
    expect(signatureMatches(secret!, resolved.received)).toBe(true);
    expect((await notices("Firing")).length, "One opened notice for the incident").toBe(1);
  } finally {
    await harness.nimbus.deleteAlertRule(flow.endpoint, ruleId);
    await Promise.all([erp.dispose(), mailpit.dispose()]);
  }
});

/** The rule notices the ERP API's receiver recorded for one rule. */
async function ruleNotices(erp: APIRequestContext, ruleId: string, status: RuleNoticeBody["status"]) {
  const response = await erp.get("/api/admin/endpoint-alerts");
  if (!response.ok()) throw new Error(`Read endpoint alerts: ${response.status()}`);
  return (await response.json() as ReceivedEndpointAlert[])
    .map(r => ({ received: r, body: JSON.parse(r.body) as RuleNoticeBody }))
    .filter(n => n.body.kind === "alert_rule" && n.body.ruleId === ruleId && n.body.status === status);
}

/**
 * Spec 053 Phase 2: a failure-occurrences rule counts failed attempts from the message history,
 * so an event that fails, is resubmitted and fails again counts twice. Other scenarios' failures
 * on the endpoint in the same five minutes count too; the rule only has to reach two.
 */
test("a failure-occurrences rule opens when attempts fail in its window", async ({ harness }) => {
  test.setTimeout(8 * 60_000);
  const flow = await harness.createFlow("crm-to-erp");
  const erp = await request.newContext({ baseURL: ServiceUrls.erpApi, ignoreHTTPSErrors: true });
  const saved = await harness.nimbus.createAlertRule(flow.endpoint, {
    name: `E2E occurrences ${flow.session}`,
    enabled: true,
    condition: { kind: "failure_occurrences", threshold: 2, windowMinutes: 5, eventTypes: [flow.eventType] },
    resolveAfterMinutes: 1,
    targets: [{ type: "webhook", url: `${ServiceUrls.erpApi}/api/webhooks/endpoint-alerts` }],
  });
  const ruleId = saved.rule.id;
  const secret = saved.secrets[0]?.signingSecret;
  try {
    await flow.script(["fail", "fail"]);
    await flow.update("occurrences");
    const [failed] = await flow.status("Failed");
    await harness.nimbus.resubmit(failed.eventId, failed.lastMessageId);
    await waitFor(async () => {
      const history = await harness.nimbus.history(flow.endpoint, failed!.eventId);
      // The API names message types in camelCase ("errorResponse").
      return history.filter(m => /^errorresponse$/i.test(m.messageType)).length >= 2 ? true : null;
    }, { timeoutMs: 120_000, description: "Two failed attempts in the event's history" });
    await flow.status("Failed");

    const opened = await waitFor(async () => (await ruleNotices(erp, ruleId, "Firing"))[0] ?? null,
      { timeoutMs: 180_000, description: "Opened failure-occurrences notice" });
    expect(opened.body).toMatchObject({ version: 2, conditionKind: "failure_occurrences", endpointId: flow.endpoint, value: 2, threshold: 2 });
    expect(signatureMatches(secret!, opened.received)).toBe(true);
  } finally {
    await harness.nimbus.deleteAlertRule(flow.endpoint, ruleId);
    // Skip the event as it is now: a resubmit changes its last message id.
    for (const event of await flow.events("Failed")) await harness.nimbus.skipMessage(event.eventId, event.lastMessageId);
    await erp.dispose();
  }
});

/**
 * Spec 053 Phase 2: a blocked-session rule on the deferred count opens while a failed event holds
 * two updates behind it, and resolves once skipping the failure releases them.
 */
test("a blocked-session rule opens on deferred events and resolves when they drain", async ({ harness }) => {
  test.setTimeout(8 * 60_000);
  const flow = await harness.createFlow("crm-to-erp");
  const erp = await request.newContext({ baseURL: ServiceUrls.erpApi, ignoreHTTPSErrors: true });
  const saved = await harness.nimbus.createAlertRule(flow.endpoint, {
    name: `E2E blocked ${flow.session}`,
    enabled: true,
    condition: { kind: "blocked_session", deferredThreshold: 2 },
    resolveAfterMinutes: 1,
    targets: [{ type: "webhook", url: `${ServiceUrls.erpApi}/api/webhooks/endpoint-alerts` }],
  });
  const ruleId = saved.rule.id;
  try {
    await flow.script(["fail"]);
    await flow.update("blocked-1");
    const [failed] = await flow.status("Failed");
    await flow.update("blocked-2");
    await flow.update("blocked-3");
    await flow.status("Deferred", 2);

    const opened = await waitFor(async () => (await ruleNotices(erp, ruleId, "Firing"))[0] ?? null,
      { timeoutMs: 180_000, description: "Opened blocked-session notice" });
    expect(opened.body).toMatchObject({ version: 2, conditionKind: "blocked_session", endpointId: flow.endpoint, reason: null });

    await harness.nimbus.skipMessage(failed.eventId, failed.lastMessageId);
    await flow.targetRevision("blocked-3");

    const resolved = await waitFor(async () => (await ruleNotices(erp, ruleId, "Resolved"))[0] ?? null,
      { timeoutMs: 240_000, description: "Resolved blocked-session notice" });
    expect(resolved.body.reason).toBe("cleared");
    expect((await ruleNotices(erp, ruleId, "Firing")).length, "One opened notice for the incident").toBe(1);
  } finally {
    await harness.nimbus.deleteAlertRule(flow.endpoint, ruleId);
    await erp.dispose();
  }
});
