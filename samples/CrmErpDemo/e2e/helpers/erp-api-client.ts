import type { APIRequestContext } from "@playwright/test";
import { request } from "@playwright/test";
import { ServiceUrls } from "./service-urls.js";
import type { CrmContact } from "./crm-api-client.js";
import { E2eControl } from "./e2e-control.js";
import { waitFor } from "./wait-for.js";
import { NimBusApiClient } from "./nimbus-api-client.js";

export interface ErpCustomer {
  id: string;
  customerNumber: string;
  legalName: string;
  taxId?: string | null;
  countryCode: string;
  crmAccountId?: string | null;
  origin: string;
  isDeleted: boolean;
  creditHold?: boolean;
  createdAt: string;
  updatedAt?: string | null;
}

export interface CreateErpCustomerRequest {
  legalName: string;
  taxId?: string | null;
  countryCode: string;
  customerNumber?: string | null;
}

export interface ErpContact extends Omit<CrmContact, "accountId"> { customerId?: string | null }
export interface ErpContactRequest { customerId?: string | null; firstName: string; lastName: string; email?: string | null; phone?: string | null }

export interface ServiceModeState {
  enabled: boolean;
  changedAt: string;
}

export interface HandoffModeRequest {
  enabled: boolean;
  durationSeconds: number;
  failureRate: number;
}

export interface HandoffModeState {
  enabled: boolean;
  durationSeconds: number;
  failureRate: number;
  changedAt: string;
}

export interface HandoffJob {
  eventId: string;
  sessionId: string;
  messageId: string;
  originatingMessageId?: string | null;
  eventTypeId: string;
  correlationId?: string | null;
  externalJobId: string;
  dueAt: string;
  payloadJson: string;
  registeredAt: string;
}

export class ErpApiClient {
  private constructor(private readonly api: APIRequestContext) {}

  static async create(): Promise<ErpApiClient> {
    const api = await request.newContext({
      baseURL: ServiceUrls.erpApi,
      ignoreHTTPSErrors: true,
    });
    return new ErpApiClient(api);
  }

  async dispose(): Promise<void> {
    await this.api.dispose();
  }

  // ─── Customers ────────────────────────────────────────────────

  async createCustomer(req: CreateErpCustomerRequest): Promise<ErpCustomer> {
    const res = await this.api.post("/api/customers", { data: req });
    if (!res.ok()) throw new Error(`ERP POST /api/customers → ${res.status()} ${await res.text()}`);
    return (await res.json()) as ErpCustomer;
  }

  async getCustomer(id: string): Promise<ErpCustomer | null> {
    const res = await this.api.get(`/api/customers/${id}`);
    if (res.status() === 404) return null;
    if (!res.ok()) throw new Error(`ERP GET /api/customers/${id} → ${res.status()}`);
    return (await res.json()) as ErpCustomer;
  }

  async listCustomers(): Promise<ErpCustomer[]> {
    const res = await this.api.get("/api/customers");
    if (!res.ok()) throw new Error(`ERP GET /api/customers → ${res.status()}`);
    return (await res.json()) as ErpCustomer[];
  }

  async findCustomerByCrmAccountId(crmAccountId: string): Promise<ErpCustomer | null> {
    const all = await this.listCustomers();
    return all.find((c) => c.crmAccountId === crmAccountId && !c.isDeleted) ?? null;
  }

  async updateCustomer(id: string, req: { legalName: string; taxId?: string | null; countryCode: string }): Promise<ErpCustomer> {
    const res = await this.api.put(`/api/customers/${id}`, { data: req });
    if (!res.ok()) throw new Error(`ERP PUT /api/customers/${id} → ${res.status()}`);
    return (await res.json()) as ErpCustomer;
  }

  async deleteCustomer(id: string): Promise<void> {
    const res = await this.api.delete(`/api/customers/${id}`);
    if (!res.ok()) throw new Error(`ERP DELETE customer → ${res.status()}`);
  }

  async createContact(data: ErpContactRequest): Promise<ErpContact> {
    const res = await this.api.post("/api/contacts", { data });
    if (!res.ok()) throw new Error(`ERP POST contact → ${res.status()} ${await res.text()}`);
    return await res.json() as ErpContact;
  }

  async getContact(id: string): Promise<ErpContact | null> {
    const res = await this.api.get(`/api/contacts/${id}`);
    if (res.status() === 404) return null;
    if (!res.ok()) throw new Error(`ERP GET contact → ${res.status()}`);
    return await res.json() as ErpContact;
  }

  async updateContact(id: string, data: ErpContactRequest): Promise<ErpContact> {
    const res = await this.api.put(`/api/contacts/${id}`, { data });
    if (!res.ok()) throw new Error(`ERP PUT contact → ${res.status()} ${await res.text()}`);
    return await res.json() as ErpContact;
  }

  async deleteContact(id: string): Promise<void> {
    const res = await this.api.delete(`/api/contacts/${id}`);
    if (!res.ok()) throw new Error(`ERP DELETE contact → ${res.status()}`);
  }

  async getAuditLog(entityType: "Customer" | "Contact", id: string): Promise<unknown[]> {
    const res = await this.api.get(`/api/audit/${entityType}/${id}`);
    if (!res.ok()) throw new Error(`ERP GET audit → ${res.status()}`);
    return await res.json() as unknown[];
  }

  // ─── Failure-mode toggles ─────────────────────────────────────

  async getServiceMode(): Promise<ServiceModeState> {
    const res = await this.api.get("/api/admin/service-mode");
    if (!res.ok()) throw new Error(`ERP GET service-mode → ${res.status()}`);
    return (await res.json()) as ServiceModeState;
  }

  async setServiceMode(enabled: boolean): Promise<ServiceModeState> {
    const res = await this.api.put("/api/admin/service-mode", { data: { enabled } });
    if (!res.ok()) throw new Error(`ERP PUT service-mode → ${res.status()}`);
    return (await res.json()) as ServiceModeState;
  }

  async getErrorMode(): Promise<ServiceModeState> {
    const res = await this.api.get("/api/admin/error-mode");
    if (!res.ok()) throw new Error(`ERP GET error-mode → ${res.status()}`);
    return (await res.json()) as ServiceModeState;
  }

  /**
   * @param reason Optional failure-reason id from `GET /api/admin/error-mode/reasons`
   * (e.g. `transient_dependency`, or `random`); omitted keeps the currently selected reason.
   * @param failureRate Optional share of messages that fail, 0..1; omitted keeps the current rate.
   */
  async setErrorMode(enabled: boolean, reason?: string, failureRate?: number): Promise<ServiceModeState> {
    const data: { enabled: boolean; reason?: string; failureRate?: number } = { enabled };
    if (reason !== undefined) data.reason = reason;
    if (failureRate !== undefined) data.failureRate = failureRate;
    const res = await this.api.put("/api/admin/error-mode", { data });
    if (!res.ok()) throw new Error(`ERP PUT error-mode → ${res.status()}`);
    return (await res.json()) as ServiceModeState;
  }

  /**
   * Restore both failure modes to off; safe to call from afterEach hooks. Also puts the failure
   * rate back to every message, so error-mode tests never inherit a lower rate.
   */
  async resetFailureModes(): Promise<void> {
    await this.setErrorMode(false, undefined, 1);
    await this.setServiceMode(false);
  }

  // ─── Handoff mode (PendingHandoff showcase) ───────────────────

  async getHandoffMode(): Promise<HandoffModeState> {
    const res = await this.api.get("/api/admin/handoff-mode");
    if (!res.ok()) throw new Error(`ERP GET handoff-mode → ${res.status()}`);
    return (await res.json()) as HandoffModeState;
  }

  async setHandoffMode(req: HandoffModeRequest): Promise<HandoffModeState> {
    const res = await this.api.put("/api/admin/handoff-mode", { data: req });
    if (!res.ok()) throw new Error(`ERP PUT handoff-mode → ${res.status()} ${await res.text()}`);
    return (await res.json()) as HandoffModeState;
  }

  /** Disable handoff mode with sensible defaults; safe to call from afterEach hooks. */
  async resetHandoffMode(): Promise<void> {
    await this.setHandoffMode({ enabled: false, durationSeconds: 10, failureRate: 0 });
  }

  async getHandoffJobs(): Promise<HandoffJob[]> {
    const res = await this.api.get("/api/internal/handoff-jobs");
    if (!res.ok()) throw new Error(`ERP GET handoff-jobs → ${res.status()}`);
    return (await res.json()) as HandoffJob[];
  }

  async releaseHandoffForSession(session: string): Promise<void> {
    const control = await E2eControl.create("erp");
    const nimbus = await NimBusApiClient.create();
    try {
      const pending = await waitFor(async () => (await nimbus.searchEvents("ErpEndpoint", {
        sessionId: session, eventTypeId: ["CrmAccountCreated"], resolutionStatus: ["Pending"],
      })).find(event => event.pendingSubStatus === "Handoff") ?? null,
      { timeoutMs: 60_000, description: "Real ERP job is Pending+Handoff before release" });
      await control.releaseHandoff(pending.eventId);
    } finally { await control.dispose(); await nimbus.dispose(); }
  }
}
