import { apiFetch } from './auth';

export interface Customer {
  id: string;
  customerNumber: string;
  legalName: string;
  taxId?: string | null;
  countryCode: string;
  crmAccountId?: string | null;
  createdAt: string;
  updatedAt?: string | null;
  origin?: string;
  isDeleted?: boolean;
  creditHold?: boolean;
}

export interface AuditEntry {
  id: string;
  entityType: string;
  entityId: string;
  action: 'Created' | 'Updated' | 'Deleted' | string;
  fieldName?: string | null;
  oldValue?: string | null;
  newValue?: string | null;
  timestamp: string;
  origin?: string | null;
}

export interface Contact {
  id: string;
  customerId?: string | null;
  firstName: string;
  lastName: string;
  email?: string | null;
  phone?: string | null;
  createdAt: string;
  updatedAt?: string | null;
  origin?: string;
  isDeleted?: boolean;
}

export interface ModeState {
  enabled: boolean;
  changedAt: string;
}

export interface ErrorModeState extends ModeState {
  /** Id of the selected failure reason; see `getErrorModeReasons`. */
  reason: string;
}

/** One selectable failure the ERP adapter simulates while error mode is on. */
export interface FailureReason {
  id: string;
  title: string;
  description: string;
  /** The Integration Intelligence category this failure is designed to be classified as. */
  expectedCategory: string;
  /** How NimBus treats it: `Failed` (retryable, resubmit) or `DeadLettered`. */
  disposition: 'Failed' | 'DeadLettered' | string;
}

export interface HandoffMode {
  enabled: boolean;
  durationSeconds: number;
  failureRate: number;
  changedAt: string;
}

export interface ProcessingDelay {
  enabled: boolean;
  delayMs: number;
  changedAt: string;
}

export interface Alert {
  severity: 'Information' | 'Warning' | 'Error' | 'Critical' | string;
  title: string;
  message: string;
  eventId: string;
  eventTypeId: string;
  messageId: string;
  correlationId: string;
  errorDetails: string;
  receivedAt: string;
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

export interface DataResetResult {
  customers: number;
  contacts: number;
  audits: number;
  outboxRows: number;
}

async function json<T>(res: Response): Promise<T> {
  if (!res.ok) {
    const body = await res.text().catch(() => '');
    throw new Error(`${res.status} ${res.statusText}${body ? ` — ${body.slice(0, 500)}` : ''}`);
  }
  return res.json() as Promise<T>;
}

export const api = {
  listCustomers: () => apiFetch('/api/customers').then(json<Customer[]>),
  getCustomer: (id: string) => apiFetch(`/api/customers/${id}`).then(json<Customer>),
  createCustomer: (c: Partial<Customer>) =>
    apiFetch('/api/customers', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(c),
    }).then(json<Customer>),
  updateCustomer: (id: string, c: Partial<Customer>) =>
    apiFetch(`/api/customers/${id}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(c),
    }).then(json<Customer>),
  deleteCustomer: (id: string) =>
    apiFetch(`/api/customers/${id}`, { method: 'DELETE' }).then(json<Customer>),

  getServiceMode: () => apiFetch('/api/admin/service-mode').then(json<ModeState>),
  setServiceMode: (enabled: boolean) =>
    apiFetch('/api/admin/service-mode', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled }),
    }).then(json<ModeState>),

  getErrorMode: () => apiFetch('/api/admin/error-mode').then(json<ErrorModeState>),
  setErrorMode: (enabled: boolean, reason?: string) =>
    apiFetch('/api/admin/error-mode', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(reason === undefined ? { enabled } : { enabled, reason }),
    }).then(json<ErrorModeState>),
  getErrorModeReasons: () => apiFetch('/api/admin/error-mode/reasons').then(json<FailureReason[]>),

  getHandoffMode: () => apiFetch('/api/admin/handoff-mode').then(json<HandoffMode>),
  setHandoffMode: (m: { enabled: boolean; durationSeconds: number; failureRate: number }) =>
    apiFetch('/api/admin/handoff-mode', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(m),
    }).then(json<HandoffMode>),

  getHandoffJobs: () => apiFetch('/api/internal/handoff-jobs').then(json<HandoffJob[]>),

  getProcessingDelay: () => apiFetch('/api/admin/processing-delay').then(json<ProcessingDelay>),
  setProcessingDelay: (m: { enabled: boolean; delayMs: number }) =>
    apiFetch('/api/admin/processing-delay', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(m),
    }).then(json<ProcessingDelay>),

  getAlerts: () => apiFetch('/api/admin/alerts').then(json<Alert[]>),
  clearAlerts: () => apiFetch('/api/admin/alerts', { method: 'DELETE' }).then((res) => {
    if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
  }),

  // Demo reset: wipes all ERP business data + pending outbox rows (no delete
  // events published).
  deleteAllData: () =>
    apiFetch('/api/admin/data', { method: 'DELETE' }).then(json<DataResetResult>),

  listContacts: () => apiFetch('/api/contacts').then(json<Contact[]>),
  getContact: (id: string) => apiFetch(`/api/contacts/${id}`).then(json<Contact>),
  createContact: (c: Partial<Contact>) =>
    apiFetch('/api/contacts', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(c),
    }).then(json<Contact>),
  updateContact: (id: string, c: Partial<Contact>) =>
    apiFetch(`/api/contacts/${id}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(c),
    }).then(json<Contact>),
  deleteContact: (id: string) =>
    apiFetch(`/api/contacts/${id}`, { method: 'DELETE' }).then(json<Contact>),

  getAuditLog: (entityType: 'Customer' | 'ErpContact', entityId: string) =>
    apiFetch(`/api/audit/${entityType}/${entityId}`).then(json<AuditEntry[]>),
};
