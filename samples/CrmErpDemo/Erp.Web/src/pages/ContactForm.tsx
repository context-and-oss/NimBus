import { FormEvent, useEffect, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { Contact, Customer, api } from '../api';
import { randomPerson, randomPick } from '../fakeData';
import AuditLog from '../components/AuditLog';
import { FastTab } from '../components/shell/fasttab';
import { BackIcon, PlusIcon, SaveIcon, SparkleIcon, TrashIcon } from '../components/shell/icons';
import { CommandBar, CommandButton, CommandLink, CommandSeparator, ErrorBar, FieldRow, inputClass } from '../components/shell/ui';

export default function ContactForm() {
  const { id } = useParams();
  const nav = useNavigate();
  const [form, setForm] = useState<Partial<Contact>>({ firstName: '', lastName: '' });
  const [customers, setCustomers] = useState<Customer[]>([]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      const [recent, contact] = await Promise.all([
        api.listCustomers({ deleted: false }),
        id ? api.getContact(id) : Promise.resolve(null),
      ]);
      // The dropdown offers the 100 most recent customers. Keep the contact's own customer
      // in it, however old it is.
      let options = recent.items;
      const own = contact?.customerId;
      if (own && !options.some(c => c.id === own)) {
        try {
          options = [await api.getCustomer(own), ...options];
        } catch {
          /* the customer is gone; the contact keeps its id */
        }
      }
      if (cancelled) return;
      setCustomers(options);
      if (contact) setForm(contact);
    })().catch(() => { /* the form stays empty */ });
    return () => {
      cancelled = true;
    };
  }, [id]);

  async function submit(e: FormEvent) {
    e.preventDefault();
    setSaving(true);
    setError(null);
    try {
      if (id) await api.updateContact(id, form);
      else await api.createContact(form);
      nav('/contacts');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally { setSaving(false); }
  }

  async function remove() {
    if (!id) return;
    if (!confirm(`Delete contact "${form.firstName} ${form.lastName}"? This will publish ErpContactDeleted; CRM will mark its matching contact deleted.`)) return;
    setSaving(true);
    setError(null);
    try {
      await api.deleteContact(id);
      nav('/contacts');
    } catch (err) {
      setError(err instanceof Error ? err.message : String(err));
    } finally { setSaving(false); }
  }

  function fillFakeData() {
    const customer = randomPick(customers);
    setForm(prev => ({ ...prev, ...randomPerson(), customerId: customer?.id ?? null }));
  }

  const lock = form.isDeleted ?? false;
  const fullName = `${form.firstName ?? ''} ${form.lastName ?? ''}`.trim();

  return (
    <>
      <CommandBar>
        <CommandButton icon={<BackIcon />} onClick={() => nav('/contacts')} aria-label="Back to contacts" />
        <CommandButton type="submit" form="contact-form" icon={<SaveIcon />} primary disabled={saving || lock}>
          {saving ? 'Saving…' : 'Save'}
        </CommandButton>
        {id && <CommandLink to="/contacts/new" icon={<PlusIcon />}>New</CommandLink>}
        {id && !lock && <CommandButton icon={<TrashIcon />} danger onClick={() => void remove()} disabled={saving}>Delete</CommandButton>}
        {!id && (
          <>
            <CommandSeparator />
            <CommandButton icon={<SparkleIcon />} onClick={fillFakeData}>Generate fake data</CommandButton>
          </>
        )}
      </CommandBar>
      {error && <ErrorBar>Save failed: {error}</ErrorBar>}
      <div className="border-b border-line bg-white px-5 pb-3 pt-4">
        <div className="text-[11px] font-semibold uppercase tracking-wider text-ink-muted">Contact card</div>
        <h1 className="text-xl font-semibold">{fullName || (id ? '…' : 'New contact')}{lock && <span className="ml-2 text-[13px] font-normal text-[#a4262c]">Deleted</span>}</h1>
      </div>
      <form id="contact-form" onSubmit={submit}>
        <FastTab title="General">
          <div className="grid max-w-5xl gap-x-10 gap-y-3 lg:grid-cols-2">
            <FieldRow label="First name" required>
              <input required disabled={lock} value={form.firstName ?? ''} onChange={e => setForm({ ...form, firstName: e.target.value })} className={inputClass} />
            </FieldRow>
            <FieldRow label="Last name" required>
              <input required disabled={lock} value={form.lastName ?? ''} onChange={e => setForm({ ...form, lastName: e.target.value })} className={inputClass} />
            </FieldRow>
            <FieldRow label="Email">
              <input type="email" disabled={lock} value={form.email ?? ''} onChange={e => setForm({ ...form, email: e.target.value })} className={inputClass} />
            </FieldRow>
            <FieldRow label="Phone">
              <input disabled={lock} value={form.phone ?? ''} onChange={e => setForm({ ...form, phone: e.target.value })} className={inputClass} />
            </FieldRow>
            <FieldRow label="Customer">
              <select disabled={lock} value={form.customerId ?? ''} onChange={e => setForm({ ...form, customerId: e.target.value || null })} className={inputClass}>
                <option value="">—</option>
                {customers.map(c => <option key={c.id} value={c.id}>{c.legalName}</option>)}
              </select>
            </FieldRow>
          </div>
        </FastTab>
      </form>
      {id && (
        <FastTab title="Audit" summary="Change history" defaultOpen={false}>
          <div className="max-w-3xl"><AuditLog entityType="ErpContact" entityId={id} /></div>
        </FastTab>
      )}
    </>
  );
}
