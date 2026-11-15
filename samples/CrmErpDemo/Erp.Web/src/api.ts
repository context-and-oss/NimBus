import { apiFetch } from './auth';
import type { Page } from './usePagedList';

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
  /** The customer's name. Only on rows from the list. */
  customerLegalName?: string | null;
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

/** Paging and filters every list takes. The server defaults to page 1 here and 100 rows. */
export interface ListQuery {
  page?: number;
  pageSize?: number;
  /** Keyword, matched by the server. */
  q?: string;
  /** false leaves deleted rows out; true returns only those. */
  deleted?: boolean;
}

/** One page of a list. The server returns the rows and puts the match count in X-Total-Count. */
async function listPage<T>(path: string, query: object): Promise<Page<T>> {
  const params = new URLSearchParams({ page: '1' });
  for (const [key, value] of Object.entries(query)) {
    if (value !== undefined && value !== null && value !== '') params.set(key, String(value));
  }
  const res = await apiFetch(`${path}?${params}`);
  const items = await json<T[]>(res);
  const total = res.headers.get('X-Total-Count');
  return { items, total: total === null ? items.length : Number(total) };
}

/** The customer counts on the Home page. Deleted customers are left out. */
export interface CustomerSummary {
  active: number;
  fromCrmSince: number;
  onCreditHold: number;
}

export const api = {
  listCustomers: (query: ListQuery & { origin?: string; creditHold?: boolean } = {}) =>
    listPage<Customer>('/api/customers', query),
  /** `since` is the start of the day the "from CRM today" count is for, as an ISO instant. */
  getCustomerSummary: (since: string) =>
    apiFetch(`/api/customers/summary?since=${encodeURIComponent(since)}`).then(json<CustomerSummary>),
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

  listContacts: (query: ListQuery & { customerId?: string } = {}) =>
    listPage<Contact>('/api/contacts', query),
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
