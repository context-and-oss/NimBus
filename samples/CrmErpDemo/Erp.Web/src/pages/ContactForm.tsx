import { FormEvent, useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { Contact, Customer, api } from '../api';
import { randomPerson, randomPick } from '../fakeData';
import AuditLog from '../components/AuditLog';

export default function ContactForm() {
  const { id } = useParams();
  const nav = useNavigate();
  const [form, setForm] = useState<Partial<Contact>>({ firstName: '', lastName: '' });
  const [customers, setCustomers] = useState<Customer[]>([]);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    api.listCustomers().then(setCustomers);
    if (id) api.getContact(id).then(setForm);
  }, [id]);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setSaving(true);
    try {
      if (id) await api.updateContact(id, form);
      else await api.createContact(form);
      nav('/contacts');
    } finally { setSaving(false); }
  }

  async function remove() {
    if (!id) return;
    if (!confirm(`Delete contact "${form.firstName} ${form.lastName}"? This will publish ErpContactDeleted; CRM will mark its matching contact deleted.`)) return;
    setSaving(true);
    try {
      await api.deleteContact(id);
      nav('/contacts');
    } finally { setSaving(false); }
  }

  function fillFakeData() {
    const customer = randomPick(customers);
    setForm(prev => ({ ...prev, ...randomPerson(), customerId: customer?.id ?? null }));
  }

  const lock = form.isDeleted ?? false;

  return (
    <div className="max-w-xl space-y-4">
    <form onSubmit={submit} className="bg-white rounded-lg shadow-xs border border-slate-200 p-6 space-y-4">
      <div className="flex items-center justify-between">
        <h1 className="text-xl font-semibold">{id ? 'Edit contact' : 'New contact'}</h1>
        {!id && (
          <button
            type="button"
            onClick={fillFakeData}
            className="text-sm px-3 py-1 bg-slate-100 text-slate-700 rounded-md hover:bg-slate-200 border border-slate-200"
          >
            Generate fake data
          </button>
        )}
      </div>
      <div className="grid grid-cols-2 gap-3">
        <label className="block">
          <span className="block text-sm text-slate-600 mb-1">First name *</span>
          <input required disabled={lock} value={form.firstName ?? ''} onChange={e => setForm({ ...form, firstName: e.target.value })} className="w-full px-3 py-2 border border-slate-300 rounded-md disabled:bg-slate-100" />
        </label>
        <label className="block">
          <span className="block text-sm text-slate-600 mb-1">Last name *</span>
          <input required disabled={lock} value={form.lastName ?? ''} onChange={e => setForm({ ...form, lastName: e.target.value })} className="w-full px-3 py-2 border border-slate-300 rounded-md disabled:bg-slate-100" />
        </label>
      </div>
      <label className="block">
        <span className="block text-sm text-slate-600 mb-1">Email</span>
        <input type="email" disabled={lock} value={form.email ?? ''} onChange={e => setForm({ ...form, email: e.target.value })} className="w-full px-3 py-2 border border-slate-300 rounded-md disabled:bg-slate-100" />
      </label>
      <label className="block">
        <span className="block text-sm text-slate-600 mb-1">Phone</span>
        <input disabled={lock} value={form.phone ?? ''} onChange={e => setForm({ ...form, phone: e.target.value })} className="w-full px-3 py-2 border border-slate-300 rounded-md disabled:bg-slate-100" />
      </label>
      <label className="block">
        <span className="block text-sm text-slate-600 mb-1">Customer</span>
        <select disabled={lock} value={form.customerId ?? ''} onChange={e => setForm({ ...form, customerId: e.target.value || null })} className="w-full px-3 py-2 border border-slate-300 rounded-md bg-white disabled:bg-slate-100">
          <option value="">—</option>
          {customers.map(c => <option key={c.id} value={c.id}>{c.legalName}</option>)}
        </select>
      </label>
      <div className="flex gap-2 items-center">
        <button type="submit" disabled={saving || lock} className="px-4 py-2 bg-emerald-600 text-white rounded-md disabled:opacity-60">{saving ? 'Saving…' : 'Save'}</button>
        <button type="button" onClick={() => nav('/contacts')} className="px-4 py-2 text-slate-600">Cancel</button>
        {id && !lock && (
          <button type="button" onClick={remove} disabled={saving} className="ml-auto px-4 py-2 bg-rose-600 text-white rounded-md disabled:opacity-60 hover:bg-rose-700">Delete</button>
        )}
        {id && lock && (
          <span className="ml-auto inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-rose-50 text-rose-700 ring-1 ring-rose-200">Deleted</span>
        )}
      </div>
    </form>
    {id && <AuditLog entityType="ErpContact" entityId={id} />}
    </div>
  );
}
