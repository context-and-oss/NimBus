import { FormEvent, useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { Customer, api } from '../api';
import { randomCompany } from '../fakeData';
import AuditLog from '../components/AuditLog';

export default function CustomerForm() {
  const { id } = useParams();
  const nav = useNavigate();
  const [form, setForm] = useState<Partial<Customer>>({ legalName: '', countryCode: 'DE' });
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (id) api.getCustomer(id).then(setForm);
  }, [id]);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setSaving(true);
    setError(null);
    try {
      if (id) await api.updateCustomer(id, form);
      else await api.createCustomer(form);
      nav('/customers');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally { setSaving(false); }
  }

  async function remove() {
    if (!id) return;
    if (!confirm(`Delete customer "${form.legalName}"? This will publish ErpCustomerDeleted; CRM will mark its matching account deleted.`)) return;
    setSaving(true);
    setError(null);
    try {
      await api.deleteCustomer(id);
      nav('/customers');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally { setSaving(false); }
  }

  return (
    <div className="max-w-xl space-y-4">
    <form onSubmit={submit} className="bg-white rounded-lg shadow-xs border border-slate-200 p-6 space-y-4">
      <div className="flex items-center justify-between">
        <h1 className="text-xl font-semibold">{id ? 'Edit customer' : 'New customer'}</h1>
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
      <Field label="Legal name" value={form.legalName ?? ''} onChange={v => setForm({ ...form, legalName: v })} required disabled={form.isDeleted} />
      <Field label="Country code (ISO-2)" value={form.countryCode ?? ''} onChange={v => setForm({ ...form, countryCode: v.toUpperCase().slice(0, 2) })} required disabled={form.isDeleted} />
      <Field label="Tax ID" value={form.taxId ?? ''} onChange={v => setForm({ ...form, taxId: v })} disabled={form.isDeleted} />
      {id && <Field label="Customer #" value={form.customerNumber ?? ''} onChange={() => {}} disabled />}
      <div className="flex gap-2 items-center">
        <button type="submit" disabled={saving || form.isDeleted} className="px-4 py-2 bg-emerald-600 text-white rounded-md disabled:opacity-60">{saving ? 'Saving…' : 'Save'}</button>
        <button type="button" onClick={() => nav('/customers')} className="px-4 py-2 text-slate-600">Cancel</button>
        {id && !form.isDeleted && (
          <button type="button" onClick={remove} disabled={saving} className="ml-auto px-4 py-2 bg-rose-600 text-white rounded-md disabled:opacity-60 hover:bg-rose-700">Delete</button>
        )}
        {id && form.isDeleted && (
          <span className="ml-auto inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-rose-50 text-rose-700 ring-1 ring-rose-200">Deleted</span>
        )}
      </div>
    </form>
    {id && <AuditLog entityType="Customer" entityId={id} />}
    </div>
  );
}

function Field({ label, value, onChange, required, disabled }: { label: string; value: string; onChange: (v: string) => void; required?: boolean; disabled?: boolean }) {
  return (
    <label className="block">
      <span className="block text-sm text-slate-600 mb-1">{label}{required && ' *'}</span>
      <input required={required} disabled={disabled} value={value} onChange={e => onChange(e.target.value)} className="w-full px-3 py-2 border border-slate-300 rounded-md disabled:bg-slate-100" />
    </label>
  );
}
