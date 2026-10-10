import { FormEvent, useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { Account, Contact, api } from '../api';
import { randomPerson, randomPick } from '../fakeData';
import AuditLog from '../components/AuditLog';

export default function ContactForm() {
  const { id } = useParams();
  const nav = useNavigate();
  const [form, setForm] = useState<Partial<Contact>>({ firstName: '', lastName: '' });
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [saving, setSaving] = useState(false);

  useEffect(() => {
    api.listAccounts().then(setAccounts);
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
    if (!confirm(`Delete contact "${form.firstName} ${form.lastName}"? This will publish CrmContactDeleted; ERP will mark its matching contact deleted.`)) return;
    setSaving(true);
    try {
      await api.deleteContact(id);
      nav('/contacts');
    } finally { setSaving(false); }
  }

  function fillFakeData() {
    const account = randomPick(accounts);
    setForm(prev => ({ ...prev, ...randomPerson(), accountId: account?.id ?? null }));
  }

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
          <input required value={form.firstName ?? ''} onChange={e => setForm({ ...form, firstName: e.target.value })} className="w-full px-3 py-2 border border-slate-300 rounded-md" />
        </label>
        <label className="block">
          <span className="block text-sm text-slate-600 mb-1">Last name *</span>
          <input required value={form.lastName ?? ''} onChange={e => setForm({ ...form, lastName: e.target.value })} className="w-full px-3 py-2 border border-slate-300 rounded-md" />
        </label>
      </div>
      <label className="block">
        <span className="block text-sm text-slate-600 mb-1">Email</span>
        <input type="email" value={form.email ?? ''} onChange={e => setForm({ ...form, email: e.target.value })} className="w-full px-3 py-2 border border-slate-300 rounded-md" />
      </label>
      <label className="block">
        <span className="block text-sm text-slate-600 mb-1">Phone</span>
        <input value={form.phone ?? ''} onChange={e => setForm({ ...form, phone: e.target.value })} className="w-full px-3 py-2 border border-slate-300 rounded-md" />
      </label>
      <label className="block">
        <span className="block text-sm text-slate-600 mb-1">Account</span>
        <select value={form.accountId ?? ''} onChange={e => setForm({ ...form, accountId: e.target.value || null })} className="w-full px-3 py-2 border border-slate-300 rounded-md bg-white">
          <option value="">—</option>
          {accounts.map(a => <option key={a.id} value={a.id}>{a.legalName}</option>)}
        </select>
      </label>
      <div className="flex gap-2 items-center">
        <button type="submit" disabled={saving || form.isDeleted} className="px-4 py-2 bg-blue-600 text-white rounded-md disabled:opacity-60">{saving ? 'Saving…' : 'Save'}</button>
        <button type="button" onClick={() => nav('/contacts')} className="px-4 py-2 text-slate-600">Cancel</button>
        {id && !form.isDeleted && (
          <button type="button" onClick={remove} disabled={saving} className="ml-auto px-4 py-2 bg-rose-600 text-white rounded-md disabled:opacity-60 hover:bg-rose-700">Delete</button>
        )}
        {id && form.isDeleted && (
          <span className="ml-auto inline-flex items-center px-2 py-1 rounded-full text-xs font-medium bg-rose-50 text-rose-700 ring-1 ring-rose-200">Deleted</span>
        )}
      </div>
    </form>
    {id && <AuditLog entityType="Contact" entityId={id} />}
    </div>
  );
}
