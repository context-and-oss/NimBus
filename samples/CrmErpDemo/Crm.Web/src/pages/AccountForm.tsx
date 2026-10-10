import { FormEvent, useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { Account, CreditCheckResult, api } from '../api';
import { randomCompany } from '../fakeData';
import AuditLog from '../components/AuditLog';

export default function AccountForm() {
  const { id } = useParams();
  const nav = useNavigate();
  const [form, setForm] = useState<Partial<Account>>({ legalName: '', countryCode: 'DE' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);
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

  return (
    <div className="max-w-xl space-y-4">
    <form onSubmit={submit} className="bg-white rounded-lg shadow-xs border border-slate-200 p-6 space-y-4">
      <div className="flex items-center justify-between">
        <h1 className="text-xl font-semibold">{id ? 'Edit account' : 'New account'}</h1>
        {!id && (
          <button
            type="button"
            onClick={() => setForm(prev => ({ ...prev, ...randomCompany() }))}
            className="text-sm px-3 py-1 bg-slate-100 text-slate-700 rounded-md hover:bg-slate-200 border border-slate-200"
          >
            Generate fake data
          </button>
        )}
      </div>
      {error && <div className="rounded-md bg-red-50 border border-red-200 text-red-800 px-3 py-2 text-sm">Save failed: {error}</div>}
      <Field label="Legal name" value={form.legalName ?? ''} onChange={v => setForm({ ...form, legalName: v })} required />
      <Field label="Country code (ISO-2)" value={form.countryCode ?? ''} onChange={v => setForm({ ...form, countryCode: v.toUpperCase().slice(0, 2) })} required />
      <Field label="Tax ID" value={form.taxId ?? ''} onChange={v => setForm({ ...form, taxId: v })} />
      <div className="flex gap-2 items-center">
        <button type="submit" disabled={saving || form.isDeleted} className="px-4 py-2 bg-blue-600 text-white rounded-md disabled:opacity-60">{saving ? 'Saving…' : 'Save'}</button>
        <button type="button" onClick={() => nav('/accounts')} className="px-4 py-2 text-slate-600">Cancel</button>
        {id && !form.isDeleted && (
          <button type="button" onClick={remove} disabled={saving} className="ml-auto px-4 py-2 bg-rose-600 text-white rounded-md disabled:opacity-60 hover:bg-rose-700">Delete</button>
        )}
        {id && form.isDeleted && (
          <span className="ml-auto inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-rose-50 text-rose-700 ring-1 ring-rose-200">Deleted</span>
        )}
      </div>
    </form>
    {id && !form.isDeleted && (
      <div className="bg-white rounded-lg shadow-xs border border-slate-200 p-6 space-y-3">
        <h2 className="text-sm font-semibold text-slate-700">ERP integration</h2>
        <div className="flex flex-wrap gap-2 items-center">
          <button
            type="button"
            onClick={runCreditCheck}
            disabled={checking}
            className="px-3 py-2 text-sm bg-indigo-600 text-white rounded-md disabled:opacity-60 hover:bg-indigo-700"
          >
            {checking ? 'Checking…' : 'Run ERP credit check'}
          </button>
          <button
            type="button"
            onClick={placeHold}
            className="px-3 py-2 text-sm bg-amber-600 text-white rounded-md hover:bg-amber-700"
          >
            Place credit hold in ERP
          </button>
          {checkResult && (
            <span
              data-testid="credit-check-result"
              className={`inline-flex items-center px-2 py-1 rounded-full text-xs font-medium ring-1 ${
                checkResult.approved
                  ? 'bg-emerald-50 text-emerald-700 ring-emerald-200'
                  : checkResult.status === 'OnHold'
                    ? 'bg-amber-50 text-amber-700 ring-amber-200'
                    : 'bg-rose-50 text-rose-700 ring-rose-200'
              }`}
            >
              {checkResult.approved ? 'Approved' : checkResult.status === 'OnHold' ? 'On hold' : checkResult.status}
              {checkResult.customerNumber ? ` · ${checkResult.customerNumber}` : ''}
            </span>
          )}
          {holdRequested && (
            <span className="inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-amber-50 text-amber-700 ring-1 ring-amber-200">
              Hold command sent
            </span>
          )}
        </div>
        {checkError && (
          <div data-testid="credit-check-error" className="rounded-md bg-rose-50 border border-rose-200 text-rose-800 px-3 py-2 text-sm">
            Credit check failed: {checkError}
          </div>
        )}
        <p className="text-xs text-slate-500">
          Credit check = synchronous request/reply over the CrmEndpoint-reply subscription.
          Credit hold = fire-and-forget command consumed only by ErpEndpoint.
        </p>
      </div>
    )}
    {id && <AuditLog entityType="Account" entityId={id} />}
    </div>
  );
}

function Field({ label, value, onChange, required }: { label: string; value: string; onChange: (v: string) => void; required?: boolean }) {
  return (
    <label className="block">
      <span className="block text-sm text-slate-600 mb-1">{label}{required && ' *'}</span>
      <input required={required} value={value} onChange={e => onChange(e.target.value)} className="w-full px-3 py-2 border border-slate-300 rounded-md" />
    </label>
  );
}
