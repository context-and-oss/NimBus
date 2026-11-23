import { request, type APIRequestContext } from "@playwright/test";
import { ServiceUrls } from "./service-urls.js";

export interface Attempt { action: string; messageId: string; eventId: string; originatingMessageId: string; at: string }
export type Fault = "continue" | "fail" | "retry" | "transient" | "permanent" | "discard" | "pending" | "pending-twice" | "pending-throw" | "validation";

/** Opt-in controls target GUID sessions only. Use an ephemeral key for each run. */
export class E2eControl {
  private constructor(private readonly api: APIRequestContext) {}

  static async create(side: "crm" | "erp"): Promise<E2eControl> {
    const key = process.env.E2E__Key;
    if (!key || key.length < 32) throw new Error("Start the demo with E2E__Enabled=true and a random E2E__Key of at least 32 characters; use the same key for Playwright.");
    const api = await request.newContext({
      baseURL: side === "crm" ? ServiceUrls.crmApi : ServiceUrls.erpApi,
      ignoreHTTPSErrors: true,
      extraHTTPHeaders: { "X-NimBus-E2E-Key": key },
    });
    const ready = await api.get("/api/e2e/ready");
    if (!ready.ok()) { await api.dispose(); throw new Error(`E2E controls on ${side} are unavailable (${ready.status()}). Start the Development test profile.`); }
    return new E2eControl(api);
  }

  async script(session: string, eventType: string, actions: Fault[], stage = "handler"): Promise<void> {
    const res = await this.api.put(`/api/e2e/sessions/${session}`, { data: { eventType, stage, actions } });
    if (!res.ok()) throw new Error(`Configure E2E session: ${res.status()} ${await res.text()}`);
  }

  async attempts(session: string): Promise<Attempt[]> {
    const res = await this.api.get(`/api/e2e/sessions/${session}`);
    if (!res.ok()) throw new Error(`Read E2E evidence: ${res.status()}`);
    return await res.json() as Attempt[];
  }

  async remove(session: string): Promise<void> {
    const res = await this.api.delete(`/api/e2e/sessions/${session}`);
    if (!res.ok()) throw new Error(`Remove E2E session: ${res.status()}`);
  }

  async releaseHandoff(eventId: string): Promise<void> {
    const res = await this.api.post(`/api/e2e/handoff-jobs/${encodeURIComponent(eventId)}/release`);
    if (!res.ok()) throw new Error(`Release E2E handoff: ${res.status()} ${await res.text()}`);
  }

  async dispose(): Promise<void> { await this.api.dispose(); }

  async outbox(session: string): Promise<{ pending: number; total: number }> {
    const res = await this.api.get(`/api/e2e/outbox/${session}`);
    if (!res.ok()) throw new Error(`E2E outbox evidence: ${res.status()}`);
    return await res.json();
  }

  async rollback(): Promise<string> {
    const res = await this.api.post("/api/e2e/outbox/rollback");
    if (!res.ok()) throw new Error(`E2E rollback probe: ${res.status()} ${await res.text()}`);
    return (await res.json()).id;
  }

  async wire(session: string, kind: string): Promise<{ eventId: string; messageId: string; endpoint: string; eventType: string }> {
    const res = await this.api.post(`/api/e2e/wire/${session}/${kind}`);
    if (!res.ok()) throw new Error(`E2E wire probe: ${res.status()}`);
    return await res.json();
  }

  async sendDataPlatformCustomer(session: string): Promise<void> {
    const res = await this.api.post(`/api/e2e/dataplatform-inbox/${session}`);
    if (!res.ok()) throw new Error(`Send DataPlatform inbox delivery: ${res.status()}`);
  }

  async replay(session: string, attempt: Attempt, payload: unknown): Promise<void> {
    const res = await this.api.post(`/api/e2e/replay/${session}`, { data: {
      eventId: attempt.eventId, messageId: attempt.messageId, eventJson: JSON.stringify(payload),
    } });
    if (!res.ok()) throw new Error(`E2E duplicate delivery: ${res.status()} ${await res.text()}`);
  }

  async deadletters(session: string): Promise<Array<{ messageId: string; deadLetterReason: string }>> {
    const res = await this.api.get(`/api/e2e/deadletters/${session}`);
    if (!res.ok()) throw new Error(`E2E deadletter evidence: ${res.status()}`);
    return await res.json();
  }

  async settle(sessionId: string, eventTypeId: string, attempt: Attempt, outcome: "complete" | "fail"): Promise<void> {
    const res = await this.api.post(`/api/e2e/settle/${outcome}`, { data: {
      eventId: attempt.eventId, sessionId, messageId: attempt.messageId, eventTypeId,
      correlationId: attempt.messageId, originatingMessageId: attempt.originatingMessageId,
    } });
    if (!res.ok()) throw new Error(`E2E settlement: ${res.status()} ${await res.text()}`);
  }
}
