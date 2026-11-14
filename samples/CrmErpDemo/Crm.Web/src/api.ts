import { apiFetch } from './auth';
import type { Page } from './usePagedList';

export interface Account {
  id: string;
  legalName: string;
  taxId?: string | null;
  countryCode: string;
  erpCustomerId?: string | null;
  erpCustomerNumber?: string | null;
  createdAt: string;
  updatedAt?: string | null;
  origin?: string;
  isDeleted?: boolean;
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
  accountId?: string | null;
  firstName: string;
  lastName: string;
  email?: string | null;
  phone?: string | null;
  createdAt: string;
  updatedAt?: string | null;
  origin?: string;
  isDeleted?: boolean;
  /** The account's name. Only on rows from the list. */
  accountLegalName?: string | null;
}

export interface CreditCheckResult {
  accountId: string;
  approved: boolean;
  status: 'Active' | 'OnHold' | 'NotFound' | 'Deleted' | string;
  customerNumber?: string | null;
  checkedAt: string;
}

export interface ModeState {
  enabled: boolean;
  changedAt: string;
}

export interface CircuitStateSnapshot {
  endpoint: string;
  state: 'Closed' | 'Open' | 'HalfOpen' | string;
  reason: string;
  changedAt: string;
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

export const api = {
  listAccounts: (query: ListQuery = {}) => listPage<Account>('/api/accounts', query),
  getAccount: (id: string) => apiFetch(`/api/accounts/${id}`).then(json<Account>),
  createAccount: (a: Partial<Account>) =>
    apiFetch('/api/accounts', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(a),
    }).then(json<Account>),
  updateAccount: (id: string, a: Partial<Account>) =>
    apiFetch(`/api/accounts/${id}`, {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(a),
    }).then(json<Account>),

  deleteAccount: (id: string) =>
    apiFetch(`/api/accounts/${id}`, { method: 'DELETE' }).then(json<Account>),

  listContacts: (query: ListQuery & { origin?: string; accountId?: string } = {}) =>
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

  getAuditLog: (entityType: 'Account' | 'Contact', entityId: string) =>
    apiFetch(`/api/audit/${entityType}/${entityId}`).then(json<AuditEntry[]>),

  // Request/reply showcase: synchronous ERP credit check (blocks up to ~10s).
  creditCheck: (accountId: string) =>
    apiFetch(`/api/accounts/${accountId}/credit-check`, { method: 'POST' }).then(json<CreditCheckResult>),

  // Command showcase: fire-and-forget PlaceCustomerOnCreditHold to ERP.
  placeCreditHold: (accountId: string, reason?: string) =>
    apiFetch(`/api/accounts/${accountId}/credit-hold`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ reason: reason ?? null }),
    }).then((res) => {
      if (!res.ok) throw new Error(`${res.status} ${res.statusText}`);
    }),

  // Circuit-breaker showcase: the CRM API outage toggle + the live circuit
  // state the adapter pushes into crm-api.
  getErrorMode: () => apiFetch('/api/admin/error-mode').then(json<ModeState>),
  setErrorMode: (enabled: boolean) =>
    apiFetch('/api/admin/error-mode', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled }),
    }).then(json<ModeState>),
  getCircuitState: () => apiFetch('/api/admin/circuit-state').then(json<CircuitStateSnapshot>),

  // CloudEvents partner showcase: turns partner-portal's recurring leads on and off.
  getPartnerLeads: () => apiFetch('/api/admin/partner-leads').then(json<ModeState>),
  setPartnerLeads: (enabled: boolean) =>
    apiFetch('/api/admin/partner-leads', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ enabled }),
    }).then(json<ModeState>),

  // Routing-keys showcase: which regional hub takes which countries.
  getRegionalHubs: () => apiFetch('/api/regional-hubs').then(json<RegionalHub[]>),

  // Demo reset: wipes all CRM business data (no delete events published).
  deleteAllData: () =>
    apiFetch('/api/admin/data', { method: 'DELETE' }).then(json<DataResetResult>),
};

export interface RegionalHub {
  endpointId: string;
  name: string;
  countries: string[];
  isUnrouted: boolean;
}

export interface DataResetResult {
  accounts: number;
  contacts: number;
  audits: number;
  nimbusRows: number;
}
