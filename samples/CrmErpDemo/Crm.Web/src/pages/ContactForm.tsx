import { FormEvent, useEffect, useState } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { Account, Contact, api } from '../api';
import { randomPerson, randomPick } from '../fakeData';
import AuditLog from '../components/AuditLog';
import { BackIcon, PlusIcon, SaveIcon, SparkleIcon, TrashIcon } from '../components/shell/icons';
import {
  Chip, CommandBar, CommandButton, CommandLink, CommandSeparator, ErrorBar, FieldRow, OriginChip, Panel,
  RecordHeader, inputClass,
} from '../components/shell/ui';

export default function ContactForm() {
  const { id } = useParams();
  const nav = useNavigate();
  const [form, setForm] = useState<Partial<Contact>>({ firstName: '', lastName: '' });
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      const [recent, contact] = await Promise.all([
        api.listAccounts({ deleted: false }),
        id ? api.getContact(id) : Promise.resolve(null),
      ]);
      // The dropdown offers the 100 most recent accounts. Keep the contact's own account in
      // it, however old it is.
      let options = recent.items;
      const own = contact?.accountId;
      if (own && !options.some(a => a.id === own)) {
        try {
          options = [await api.getAccount(own), ...options];
        } catch {
          /* the account is gone; the contact keeps its id */
        }
      }
      if (cancelled) return;
      setAccounts(options);
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
    if (!confirm(`Delete contact "${form.firstName} ${form.lastName}"? This will publish CrmContactDeleted; ERP will mark its matching contact deleted.`)) return;
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
    const account = randomPick(accounts);
    setForm(prev => ({ ...prev, ...randomPerson(), accountId: account?.id ?? null }));
  }

  const deleted = form.isDeleted ?? false;
  const fullName = `${form.firstName ?? ''} ${form.lastName ?? ''}`.trim();
  const account = accounts.find(a => a.id === form.accountId);

  return (
    <>
      <CommandBar>
        <CommandButton icon={<BackIcon />} onClick={() => nav('/contacts')} aria-label="Back to contacts" />
        <CommandButton type="submit" form="contact-form" icon={<SaveIcon />} primary disabled={saving || deleted}>
          {saving ? 'Saving…' : 'Save'}
        </CommandButton>
        {id && <CommandLink to="/contacts/new" icon={<PlusIcon />}>New</CommandLink>}
        {id && !deleted && <CommandButton icon={<TrashIcon />} danger onClick={() => void remove()} disabled={saving}>Delete</CommandButton>}
        {!id && (
          <>
            <CommandSeparator />
            <CommandButton icon={<SparkleIcon />} onClick={fillFakeData}>Generate fake data</CommandButton>
          </>
        )}
      </CommandBar>
      {error && <ErrorBar>Save failed: {error}</ErrorBar>}
      <RecordHeader
        title={fullName || (id ? '…' : 'New contact')}
        entity="Contact"
        avatarClass="bg-[#8764b8]"
        fields={id ? [
          { label: 'Account', value: account ? <Link to={`/accounts/${account.id}`} className="text-accent hover:underline">{account.legalName}</Link> : '—' },
          { label: 'Origin', value: <OriginChip origin={form.origin} /> },
          { label: 'Status', value: deleted ? <Chip tone="bad">Deleted</Chip> : <Chip tone="ok">Active</Chip> },
        ] : []}
      />
      <div className={`grid gap-4 p-5 ${id ? 'xl:grid-cols-[minmax(0,1.4fr)_minmax(0,1fr)]' : 'max-w-3xl'}`}>
        <Panel title="Contact information" className="min-w-0 self-start">
          <form id="contact-form" onSubmit={submit} className="flex flex-col gap-3">
            <FieldRow label="First name" required>
              <input required disabled={deleted} value={form.firstName ?? ''} onChange={e => setForm({ ...form, firstName: e.target.value })} className={inputClass} />
            </FieldRow>
            <FieldRow label="Last name" required>
              <input required disabled={deleted} value={form.lastName ?? ''} onChange={e => setForm({ ...form, lastName: e.target.value })} className={inputClass} />
            </FieldRow>
            <FieldRow label="Email">
              <input type="email" disabled={deleted} value={form.email ?? ''} onChange={e => setForm({ ...form, email: e.target.value })} className={inputClass} />
            </FieldRow>
            <FieldRow label="Phone">
              <input disabled={deleted} value={form.phone ?? ''} onChange={e => setForm({ ...form, phone: e.target.value })} className={inputClass} />
            </FieldRow>
            <FieldRow label="Account">
              <select disabled={deleted} value={form.accountId ?? ''} onChange={e => setForm({ ...form, accountId: e.target.value || null })} className={inputClass}>
                <option value="">—</option>
                {accounts.map(a => <option key={a.id} value={a.id}>{a.legalName}</option>)}
              </select>
            </FieldRow>
          </form>
        </Panel>
        {id && <div className="min-w-0"><AuditLog entityType="Contact" entityId={id} /></div>}
      </div>
    </>
  );
}
