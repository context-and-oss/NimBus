import { useEffect, useState, type ReactNode } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { Customer, api } from '../api';
import { PlusIcon, RefreshIcon } from '../components/shell/icons';
import { Chip, CommandBar, CommandButton, CommandLink, FilterBox, OriginChip, Pager, ViewHeader, formatDate, grid } from '../components/shell/ui';
import { PAGE_SIZE, usePagedList } from '../usePagedList';

type View = 'all' | 'from-crm' | 'credit-hold';

const VIEWS: { id: View; label: string; title: string }[] = [
  { id: 'all', label: 'All', title: 'Customers' },
  { id: 'from-crm', label: 'From CRM', title: 'Customers from CRM' },
  { id: 'credit-hold', label: 'Credit holds', title: 'Customers on credit hold' },
];

// What each view asks the server for.
const VIEW_QUERY: Record<View, { origin?: string; creditHold?: boolean; deleted?: boolean }> = {
  all: {},
  'from-crm': { origin: 'Crm' },
  'credit-hold': { creditHold: true, deleted: false },
};

export default function CustomersList() {
  const [params] = useSearchParams();
  const view = (VIEWS.find(v => v.id === params.get('view'))?.id ?? 'all') as View;
  const [selectedId, setSelectedId] = useState<string | null>(null);

  // The server pages the list and applies the view and the search. The page in view is
  // polled every 3 s, so a customer arriving from CRM shows up without a reload.
  const list = usePagedList<Customer>((page, q) => api.listCustomers({ page, q, ...VIEW_QUERY[view] }), view, 3000);
  const { rows, total, loading } = list;
  const selected = rows.find(r => r.id === selectedId) ?? rows[0];

  // The details box counts the selected customer's contacts: one small query for that
  // customer, where the page used to load every contact to count them.
  const selectedKey = selected?.id;
  const [contactCount, setContactCount] = useState<{ id: string; count: number } | null>(null);
  useEffect(() => {
    if (!selectedKey) return;
    let cancelled = false;
    api.listContacts({ customerId: selectedKey, deleted: false, pageSize: 1 })
      .then(found => { if (!cancelled) setContactCount({ id: selectedKey, count: found.total }); })
      .catch(() => { /* the box shows no count */ });
    return () => {
      cancelled = true;
    };
  }, [selectedKey]);

  return (
    <>
      <CommandBar>
        <CommandLink to="/customers/new" icon={<PlusIcon />} primary>New customer</CommandLink>
        <CommandButton icon={<RefreshIcon />} onClick={list.refresh}>Refresh</CommandButton>
      </CommandBar>
      <div className="flex min-h-0 flex-1">
        <div className="min-w-0 flex-1">
          <ViewHeader title={VIEWS.find(v => v.id === view)!.title} count={loading ? undefined : total}>
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
            <FilterBox value={list.filter} onChange={list.setFilter} placeholder="Search customers" />
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
                {!loading && rows.length === 0 && <tr><td colSpan={8} className={grid.empty}>No customers in this view.</td></tr>}
                {rows.map(r => (
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
            <Pager page={list.page} pageSize={PAGE_SIZE} total={total} onPage={list.setPage} />
          </div>
        </div>
        {selected && <FactBox customer={selected} contactCount={contactCount?.id === selected.id ? contactCount.count : undefined} />}
      </div>
    </>
  );
}

export function CustomerStatus({ customer }: { customer: Partial<Customer> }) {
  if (customer.isDeleted) return <Chip tone="bad">Deleted</Chip>;
  if (customer.creditHold) return <Chip tone="warn" data-testid="credit-hold-badge">Credit hold</Chip>;
  return <Chip tone="ok">Active</Chip>;
}

function FactBox({ customer, contactCount }: { customer: Customer; contactCount?: number }) {
  return (
    <aside aria-label="Customer details" className="hidden w-60 shrink-0 flex-col gap-4 border-l border-line bg-white p-4 xl:flex">
      <FactGroup title="Customer details">
        <Fact label="No." value={<span className="font-mono text-xs">{customer.customerNumber}</span>} />
        <Fact label="Name" value={customer.legalName} />
        <Fact label="Contacts" value={contactCount ?? '…'} />
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
