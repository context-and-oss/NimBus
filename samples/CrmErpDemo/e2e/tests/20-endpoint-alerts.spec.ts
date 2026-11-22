import { createHmac, randomUUID, timingSafeEqual } from "node:crypto";
import { request } from "@playwright/test";
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
