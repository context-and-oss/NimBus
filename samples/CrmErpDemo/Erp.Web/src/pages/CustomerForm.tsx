import { FormEvent, useEffect, useState, type ReactNode } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { Contact, Customer, api } from '../api';
import { randomCompany } from '../fakeData';
import AuditLog from '../components/AuditLog';
import { FastTab } from '../components/shell/fasttab';
import { BackIcon, PlusIcon, SaveIcon, SparkleIcon, TrashIcon } from '../components/shell/icons';
import { CommandBar, CommandButton, CommandLink, CommandSeparator, ErrorBar, FieldRow, OriginChip, formatDate, inputClass } from '../components/shell/ui';
import { CustomerStatus } from './CustomersList';

// Customer card: General, CRM link, Credit, Contacts and Audit FastTabs.

export default function CustomerForm() {
  const { id } = useParams();
  const nav = useNavigate();
  const [form, setForm] = useState<Partial<Customer>>({ legalName: '', countryCode: 'DE' });
  const [contacts, setContacts] = useState<Contact[]>([]);
  const [contactTotal, setContactTotal] = useState(0);
  const [saving, setSaving] = useState(false);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!id) return;
    api.getCustomer(id).then(setForm);
    // The server returns this customer's contacts: the first 100, and how many there are.
    api.listContacts({ customerId: id })
      .then(found => { setContacts(found.items); setContactTotal(found.total); })
      .catch(() => { /* tab shows none */ });
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

  const deleted = form.isDeleted ?? false;

  return (
    <>
      <CommandBar>
        <CommandButton icon={<BackIcon />} onClick={() => nav('/customers')} aria-label="Back to customers" />
        <CommandButton type="submit" form="customer-form" icon={<SaveIcon />} primary disabled={saving || deleted}>
          {saving ? 'Saving…' : 'Save'}
        </CommandButton>
        {id && <CommandLink to="/customers/new" icon={<PlusIcon />}>New</CommandLink>}
        {id && !deleted && <CommandButton icon={<TrashIcon />} danger onClick={() => void remove()} disabled={saving}>Delete</CommandButton>}
        {!id && (
          <>
            <CommandSeparator />
            <CommandButton icon={<SparkleIcon />} onClick={() => setForm(prev => ({ ...prev, ...randomCompany() }))}>Generate fake data</CommandButton>
          </>
        )}
      </CommandBar>
      {error && <ErrorBar>Save failed: {error}</ErrorBar>}
      <div className="border-b border-line bg-white px-5 pb-3 pt-4">
        <div className="text-[11px] font-semibold uppercase tracking-wider text-ink-muted">Customer card</div>
        <h1 className="text-xl font-semibold">
          {id ? <>{form.customerNumber && <span className="font-mono text-[17px]">{form.customerNumber} · </span>}{form.legalName || '…'}</> : 'New customer'}
        </h1>
      </div>

      <form id="customer-form" onSubmit={submit}>
        <FastTab title="General" summary={id ? <>{form.legalName} · {form.countryCode}</> : undefined}>
          <div className="grid max-w-5xl gap-x-10 gap-y-3 lg:grid-cols-2">
            <FieldRow label="Legal name" required>
              <input required disabled={deleted} value={form.legalName ?? ''} onChange={e => setForm({ ...form, legalName: e.target.value })} className={inputClass} />
            </FieldRow>
            <FieldRow label="Country code (ISO-2)" required>
              <input required disabled={deleted} value={form.countryCode ?? ''} onChange={e => setForm({ ...form, countryCode: e.target.value.toUpperCase().slice(0, 2) })} className={inputClass} />
            </FieldRow>
            <FieldRow label="Tax ID">
              <input disabled={deleted} value={form.taxId ?? ''} onChange={e => setForm({ ...form, taxId: e.target.value })} className={inputClass} />
            </FieldRow>
            {id && (
              <FieldRow label="Customer #">
                <input disabled value={form.customerNumber ?? ''} readOnly className={`${inputClass} font-mono`} />
              </FieldRow>
            )}
          </div>
        </FastTab>
      </form>

      {id && (
        <>
          <FastTab title="CRM link" summary={form.crmAccountId ? 'Linked to a CRM account' : 'Not linked'}>
            <dl className="grid max-w-5xl gap-x-10 gap-y-2 text-[13px] lg:grid-cols-2">
              <Read label="Origin" value={<OriginChip origin={form.origin} />} />
              <Read label="CRM account ID" value={<span className="break-all font-mono text-xs">{form.crmAccountId ?? '—'}</span>} />
              <Read label="Created" value={formatDate(form.createdAt)} />
              <Read label="Last updated" value={formatDate(form.updatedAt)} />
            </dl>
          </FastTab>
          <FastTab title="Credit" summary={<CustomerStatus customer={form} />}>
            <p className="max-w-2xl text-[13px] text-ink-muted">
              {form.creditHold
                ? 'This customer is on credit hold. The hold arrived from CRM as a PlaceCustomerOnCreditHold command; CRM credit checks now answer "On hold".'
                : 'No credit hold. CRM can place one with the PlaceCustomerOnCreditHold command from the account form.'}
            </p>
          </FastTab>
          <FastTab title="Contacts" summary={`${contactTotal} ${contactTotal === 1 ? 'contact' : 'contacts'}`} defaultOpen={false}>
            {contacts.length === 0 ? (
              <p className="text-[13px] text-ink-muted">No contacts for this customer.</p>
            ) : (
              <ul className="flex flex-col gap-1 text-[13px]">
                {contacts.map(c => (
                  <li key={c.id} className={c.isDeleted ? 'text-ink-muted line-through' : ''}>
                    <Link to={`/contacts/${c.id}`} className="text-accent hover:underline">{c.firstName} {c.lastName}</Link>
                    {c.email && <span className="text-ink-muted"> · {c.email}</span>}
                  </li>
                ))}
                {contactTotal > contacts.length && (
                  <li className="text-ink-muted">Showing the newest {contacts.length} of {contactTotal}. The Contacts page has the rest.</li>
                )}
              </ul>
            )}
          </FastTab>
          <FastTab title="Audit" summary="Change history" defaultOpen={false}>
            <div className="max-w-3xl"><AuditLog entityType="Customer" entityId={id} /></div>
          </FastTab>
        </>
      )}
    </>
  );
}

function Read({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div className="grid grid-cols-[180px_minmax(0,1fr)] items-baseline gap-3">
      <dt className="text-ink-muted">{label}</dt>
      <dd className="min-w-0">{value}</dd>
    </div>
  );
}
