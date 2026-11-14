import { useEffect, useState, type ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { Contact, Customer, api } from '../api';
import { PlusIcon, RefreshIcon } from '../components/shell/icons';
import { Chip, CommandBar, CommandButton, CommandLink, FilterBox, OriginChip, ViewHeader, formatDate, grid } from '../components/shell/ui';

type View = 'all' | 'from-crm' | 'credit-hold';

const VIEWS: { id: View; label: string; title: string }[] = [
  { id: 'all', label: 'All', title: 'Customers' },
  { id: 'from-crm', label: 'From CRM', title: 'Customers from CRM' },
  { id: 'credit-hold', label: 'Credit holds', title: 'Customers on credit hold' },
];

export default function CustomersList() {
  const [params] = useSearchParams();
  const view = (VIEWS.find(v => v.id === params.get('view'))?.id ?? 'all') as View;
  const [rows, setRows] = useState<Customer[]>([]);
  const [contacts, setContacts] = useState<Contact[]>([]);
  const [loading, setLoading] = useState(true);
  const [filter, setFilter] = useState('');
  const [selectedId, setSelectedId] = useState<string | null>(null);

  // initial=true on the first mount shows the loading placeholder; background
  // polls re-fetch silently so the table doesn't flash "Loading…" every 3s.
  async function load(initial = false) {
    if (initial) setLoading(true);
    try { setRows(await api.listCustomers()); } finally { if (initial) setLoading(false); }
  }
  useEffect(() => {
    void load(true);
    api.listContacts().then(setContacts).catch(() => { /* FactBox shows no count */ });
    const t = setInterval(() => void load(false), 3000);
    return () => clearInterval(t);
  }, []);

  const q = filter.trim().toLowerCase();
  const visible = rows
    .filter(r => view !== 'from-crm' || r.origin === 'Crm')
    .filter(r => view !== 'credit-hold' || (r.creditHold && !r.isDeleted))
    .filter(r => !q || [r.customerNumber, r.legalName, r.countryCode, r.taxId].some(v => v?.toLowerCase().includes(q)));
  const selected = visible.find(r => r.id === selectedId) ?? visible[0];

  return (
    <>
      <CommandBar>
        <CommandLink to="/customers/new" icon={<PlusIcon />} primary>New customer</CommandLink>
        <CommandButton icon={<RefreshIcon />} onClick={() => void load(false)}>Refresh</CommandButton>
      </CommandBar>
      <div className="flex min-h-0 flex-1">
        <div className="min-w-0 flex-1">
          <ViewHeader title={VIEWS.find(v => v.id === view)!.title} count={loading ? undefined : visible.length}>
            <div role="group" aria-label="Views" className="flex rounded border border-line bg-white p-0.5">
              {VIEWS.map(v => (
                <Link
                  key={v.id}
                  to={v.id === 'all' ? '/customers' : `/customers?view=${v.id}`}
                  aria-current={view === v.id ? 'page' : undefined}
                  className={`rounded-sm px-2.5 py-1 text-[13px] ${view === v.id ? 'bg-accent-soft font-semibold text-accent' : 'hover:bg-nav'}`}
                >
                  {v.label}
                </Link>
              ))}
            </div>
            <FilterBox value={filter} onChange={setFilter} placeholder="Search customers" />
          </ViewHeader>
          <div className={grid.wrap}>
            <table className={grid.table}>
              <thead>
                <tr>
                  <th className={grid.th}>No.</th>
                  <th className={grid.th}>Name</th>
                  <th className={grid.th}>Country</th>
                  <th className={grid.th}>Origin</th>
                  <th className={grid.th}>CRM account</th>
                  <th className={grid.th}>Status</th>
                  <th className={grid.th}>Created</th>
                  <th className={grid.th}>Updated</th>
                </tr>
              </thead>
              <tbody>
                {loading && <tr><td colSpan={8} className={grid.empty}>Loading…</td></tr>}
                {!loading && visible.length === 0 && <tr><td colSpan={8} className={grid.empty}>No customers in this view.</td></tr>}
                {visible.map(r => (
                  <tr
                    key={r.id}
                    onClick={() => setSelectedId(r.id)}
                    aria-selected={selected?.id === r.id}
                    className={`cursor-default ${selected?.id === r.id ? 'bg-accent-soft' : grid.row} ${r.isDeleted ? 'text-ink-muted line-through' : ''}`}
                  >
                    <td className={`${grid.td} font-mono text-xs`}>{r.customerNumber}</td>
                    <td className={grid.td}><Link to={`/customers/${r.id}`} className={grid.link}>{r.legalName}</Link></td>
                    <td className={grid.td}>{r.countryCode}</td>
                    <td className={grid.td}><OriginChip origin={r.origin} /></td>
                    <td className={`${grid.td} font-mono text-xs text-ink-muted`} title={r.crmAccountId ?? undefined}>{r.crmAccountId ? `${r.crmAccountId.slice(0, 8)}…` : '—'}</td>
                    <td className={grid.td}><CustomerStatus customer={r} /></td>
                    <td className={`${grid.td} text-ink-muted`}>{formatDate(r.createdAt)}</td>
                    <td className={`${grid.td} text-ink-muted`}>{formatDate(r.updatedAt)}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
        {selected && <FactBox customer={selected} contactCount={contacts.filter(c => c.customerId === selected.id && !c.isDeleted).length} />}
      </div>
    </>
  );
}

export function CustomerStatus({ customer }: { customer: Partial<Customer> }) {
  if (customer.isDeleted) return <Chip tone="bad">Deleted</Chip>;
  if (customer.creditHold) return <Chip tone="warn" data-testid="credit-hold-badge">Credit hold</Chip>;
  return <Chip tone="ok">Active</Chip>;
}

function FactBox({ customer, contactCount }: { customer: Customer; contactCount: number }) {
  return (
    <aside aria-label="Customer details" className="hidden w-60 shrink-0 flex-col gap-4 border-l border-line bg-white p-4 xl:flex">
      <FactGroup title="Customer details">
        <Fact label="No." value={<span className="font-mono text-xs">{customer.customerNumber}</span>} />
        <Fact label="Name" value={customer.legalName} />
        <Fact label="Contacts" value={contactCount} />
        <Fact label="Status" value={<CustomerStatus customer={customer} />} />
        <Fact label="Updated" value={formatDate(customer.updatedAt ?? customer.createdAt)} />
      </FactGroup>
      <FactGroup title="CRM link">
        <Fact label="Origin" value={<OriginChip origin={customer.origin} />} />
        <Fact label="Account" value={customer.crmAccountId ? <span className="font-mono text-xs" title={customer.crmAccountId}>{customer.crmAccountId.slice(0, 13)}…</span> : '—'} />
        <Fact label="Sync" value={customer.crmAccountId ? <Chip tone="ok" dot>Linked</Chip> : <Chip tone="neutral">Not linked</Chip>} />
      </FactGroup>
    </aside>
  );
}

function FactGroup({ title, children }: { title: string; children: ReactNode }) {
  return (
    <section className="flex flex-col gap-1.5">
      <h2 className="text-[13px] font-semibold">{title}</h2>
      <dl className="flex flex-col gap-1.5">{children}</dl>
    </section>
  );
}

function Fact({ label, value }: { label: string; value: ReactNode }) {
  return (
    <div className="flex items-baseline justify-between gap-3 text-[13px]">
      <dt className="shrink-0 text-ink-muted">{label}</dt>
      <dd className="min-w-0 truncate text-right">{value}</dd>
    </div>
  );
}
