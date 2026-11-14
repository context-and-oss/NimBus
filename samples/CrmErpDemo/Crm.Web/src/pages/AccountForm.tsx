import { FormEvent, useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { Account, Contact, CreditCheckResult, api } from '../api';
import { randomCompany } from '../fakeData';
import AuditLog from '../components/AuditLog';
import RegionalHubHint from '../components/RegionalHubHint';
import { BackIcon, PlusIcon, SaveIcon, SparkleIcon, TrashIcon } from '../components/shell/icons';
import {
  Chip, CommandBar, CommandButton, CommandLink, CommandSeparator, ErrorBar, FieldRow, OriginChip, Panel,
  RecordHeader, RecordTabs, formatDate, grid, inputClass,
} from '../components/shell/ui';

type Tab = 'summary' | 'contacts';

export default function AccountForm() {
  const { id } = useParams();
  const nav = useNavigate();
  const [form, setForm] = useState<Partial<Account>>({ legalName: '', countryCode: 'DE' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [tab, setTab] = useState<Tab>('summary');
  const [checking, setChecking] = useState(false);
  const [checkResult, setCheckResult] = useState<CreditCheckResult | null>(null);
  const [checkError, setCheckError] = useState<string | null>(null);
  const [holdRequested, setHoldRequested] = useState(false);

  async function runCreditCheck() {
    if (!id) return;
    setChecking(true);
    setCheckResult(null);
    setCheckError(null);
    const started = Date.now();
    try {
      setCheckResult(await api.creditCheck(id));
    } catch (err) {
      const elapsed = ((Date.now() - started) / 1000).toFixed(1);
      setCheckError(`${err instanceof Error ? err.message : String(err)} (after ${elapsed}s)`);
    } finally { setChecking(false); }
  }

  async function placeHold() {
    if (!id) return;
    if (!confirm('Send PlaceCustomerOnCreditHold to ERP? This is a command: imperative, one consumer.')) return;
    setHoldRequested(false);
    setCheckError(null);
    try {
      await api.placeCreditHold(id, 'Requested from CRM UI');
      setHoldRequested(true);
    } catch (err) {
      setCheckError(err instanceof Error ? err.message : String(err));
    }
  }

  useEffect(() => {
    if (id) api.getAccount(id).then(setForm);
  }, [id]);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setSaving(true);
    setError(null);
    try {
      if (id) await api.updateAccount(id, form);
      else await api.createAccount(form);
      nav('/accounts');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally { setSaving(false); }
  }

  async function remove() {
    if (!id) return;
    if (!confirm(`Delete account "${form.legalName}"? This will publish CrmAccountDeleted; ERP will mark its matching customer deleted.`)) return;
    setSaving(true);
    setError(null);
    try {
      await api.deleteAccount(id);
      nav('/accounts');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally { setSaving(false); }
  }

  const deleted = form.isDeleted ?? false;

  return (
    <>
      <CommandBar>
        <CommandButton icon={<BackIcon />} onClick={() => nav('/accounts')} aria-label="Back to accounts" />
        <CommandButton type="submit" form="account-form" icon={<SaveIcon />} primary disabled={saving || deleted}>
          {saving ? 'Saving…' : 'Save'}
        </CommandButton>
        {id && <CommandLink to="/accounts/new" icon={<PlusIcon />}>New</CommandLink>}
        {id && !deleted && <CommandButton icon={<TrashIcon />} danger onClick={() => void remove()} disabled={saving}>Delete</CommandButton>}
        {!id && (
          <>
            <CommandSeparator />
            <CommandButton icon={<SparkleIcon />} onClick={() => setForm(prev => ({ ...prev, ...randomCompany() }))}>
              Generate fake data
            </CommandButton>
          </>
        )}
      </CommandBar>
      {error && <ErrorBar>Save failed: {error}</ErrorBar>}
      <RecordHeader
        title={form.legalName || (id ? '…' : 'New account')}
        entity="Account"
        fields={id ? [
          { label: 'Country', value: form.countryCode || '—' },
          { label: 'ERP customer', value: form.erpCustomerNumber ? <Chip tone="ok" dot>{form.erpCustomerNumber}</Chip> : deleted ? '—' : <Chip tone="warn" dot>Pending</Chip> },
          { label: 'Origin', value: <OriginChip origin={form.origin} /> },
          { label: 'Status', value: deleted ? <Chip tone="bad">Deleted</Chip> : <Chip tone="ok">Active</Chip> },
        ] : []}
      >
        {id && <RecordTabs<Tab> value={tab} onChange={setTab} tabs={[{ id: 'summary', label: 'Summary' }, { id: 'contacts', label: 'Contacts' }]} />}
      </RecordHeader>

      {tab === 'contacts' && id ? (
        <AccountContacts accountId={id} />
      ) : (
        <div className={`grid gap-4 p-5 ${id ? 'xl:grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)]' : 'max-w-3xl'}`}>
          <div className="flex min-w-0 flex-col gap-4">
            <Panel title="Account information">
              <form id="account-form" onSubmit={submit} className="flex flex-col gap-3">
                <FieldRow label="Legal name" required>
                  <input required disabled={deleted} value={form.legalName ?? ''} onChange={e => setForm({ ...form, legalName: e.target.value })} className={inputClass} />
                </FieldRow>
                <FieldRow label="Country code (ISO-2)" required>
                  <div>
                    <input required disabled={deleted} value={form.countryCode ?? ''} onChange={e => setForm({ ...form, countryCode: e.target.value.toUpperCase().slice(0, 2) })} className={inputClass} />
                    {!deleted && <RegionalHubHint countryCode={form.countryCode} />}
                  </div>
                </FieldRow>
                <FieldRow label="Tax ID">
                  <input disabled={deleted} value={form.taxId ?? ''} onChange={e => setForm({ ...form, taxId: e.target.value })} className={inputClass} />
                </FieldRow>
              </form>
            </Panel>
            {id && !deleted && (
              <Panel title="ERP integration">
                <div className="flex flex-col gap-3">
                  <div className="flex flex-wrap items-center gap-2">
                    <button
                      type="button"
                      onClick={() => void runCreditCheck()}
                      disabled={checking}
                      className="rounded-sm bg-accent px-3 py-1.5 text-[13px] font-semibold text-white hover:bg-accent-hover disabled:opacity-60"
                    >
                      {checking ? 'Checking…' : 'Run ERP credit check'}
                    </button>
                    <button
                      type="button"
                      onClick={() => void placeHold()}
                      className="rounded-sm border border-[#8a8886] bg-white px-3 py-1.5 text-[13px] font-semibold hover:bg-nav"
                    >
                      Place credit hold in ERP
                    </button>
                    {checkResult && (
                      <span data-testid="credit-check-result">
                        <Chip tone={checkResult.approved ? 'ok' : checkResult.status === 'OnHold' ? 'warn' : 'bad'} dot>
                          {checkResult.approved ? 'Approved' : checkResult.status === 'OnHold' ? 'On hold' : checkResult.status}
                          {checkResult.customerNumber ? ` · ${checkResult.customerNumber}` : ''}
                        </Chip>
                      </span>
                    )}
                    {holdRequested && <Chip tone="warn" dot>Hold command sent</Chip>}
                  </div>
                  {checkError && (
                    <div data-testid="credit-check-error" className="rounded-sm border border-[#f1bbbc] bg-[#fde7e9] px-3 py-2 text-[13px] text-[#a4262c]">
                      Credit check failed: {checkError}
                    </div>
                  )}
                  <p className="text-xs text-ink-muted">
                    Credit check is a synchronous request/reply over the CrmEndpoint-reply subscription.
                    Credit hold is a fire-and-forget command consumed only by ErpEndpoint.
                  </p>
                </div>
              </Panel>
            )}
          </div>
          {id && <div className="min-w-0"><AuditLog entityType="Account" entityId={id} /></div>}
        </div>
      )}
    </>
  );
}

function AccountContacts({ accountId }: { accountId: string }) {
  const [rows, setRows] = useState<Contact[] | null>(null);
  useEffect(() => {
    api.listContacts().then(all => setRows(all.filter(c => c.accountId === accountId))).catch(() => setRows([]));
  }, [accountId]);

  return (
    <div className="pt-5">
      <div className={grid.wrap}>
        <table className={grid.table}>
          <thead>
            <tr>
              <th className={grid.th}>Full name</th>
              <th className={grid.th}>Email</th>
              <th className={grid.th}>Phone</th>
              <th className={grid.th}>Origin</th>
              <th className={grid.th}>Created on</th>
            </tr>
          </thead>
          <tbody>
            {rows === null && <tr><td colSpan={5} className={grid.empty}>Loading…</td></tr>}
            {rows?.length === 0 && <tr><td colSpan={5} className={grid.empty}>No contacts for this account.</td></tr>}
            {rows?.map(c => (
              <tr key={c.id} className={`${grid.row} ${c.isDeleted ? 'text-ink-muted line-through' : ''}`}>
                <td className={grid.td}><Link to={`/contacts/${c.id}`} className={grid.link}>{c.firstName} {c.lastName}</Link></td>
                <td className={`${grid.td} text-ink-muted`}>{c.email ?? '—'}</td>
                <td className={`${grid.td} text-ink-muted`}>{c.phone ?? '—'}</td>
                <td className={grid.td}><OriginChip origin={c.origin} /></td>
                <td className={`${grid.td} text-ink-muted`}>{formatDate(c.createdAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
