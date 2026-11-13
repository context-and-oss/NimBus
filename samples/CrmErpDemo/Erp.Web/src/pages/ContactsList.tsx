import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Contact, Customer, api } from '../api';
import { PlusIcon, RefreshIcon } from '../components/shell/icons';
import { Chip, CommandBar, CommandButton, CommandLink, FilterBox, OriginChip, ViewHeader, formatDate, grid } from '../components/shell/ui';

export default function ContactsList() {
  const [rows, setRows] = useState<Contact[]>([]);
  const [customers, setCustomers] = useState<Customer[]>([]);
  const [loading, setLoading] = useState(true);
  const [filter, setFilter] = useState('');

  async function load() {
    const [contacts, custs] = await Promise.allSettled([api.listContacts(), api.listCustomers()]);
    if (contacts.status === 'fulfilled') setRows(contacts.value);
    if (custs.status === 'fulfilled') setCustomers(custs.value);
  }

  useEffect(() => {
    load().finally(() => setLoading(false));
  }, []);

  const customerName = new Map(customers.map(c => [c.id, c.legalName]));
  const q = filter.trim().toLowerCase();
  const visible = q ? rows.filter(r => [r.firstName, r.lastName, r.email, r.phone].some(v => v?.toLowerCase().includes(q))) : rows;

  return (
    <>
      <CommandBar>
        <CommandLink to="/contacts/new" icon={<PlusIcon />} primary>New contact</CommandLink>
        <CommandButton icon={<RefreshIcon />} onClick={() => void load()}>Refresh</CommandButton>
      </CommandBar>
      <ViewHeader title="Contacts" count={loading ? undefined : visible.length}>
        <FilterBox value={filter} onChange={setFilter} placeholder="Search contacts" />
      </ViewHeader>
      <div className={grid.wrap}>
        <table className={grid.table}>
          <thead>
            <tr>
              <th className={grid.th}>Name</th>
              <th className={grid.th}>Email</th>
              <th className={grid.th}>Phone</th>
              <th className={grid.th}>Customer</th>
              <th className={grid.th}>Origin</th>
              <th className={grid.th}>Status</th>
              <th className={grid.th}>Created</th>
              <th className={grid.th}>Updated</th>
            </tr>
          </thead>
          <tbody>
            {loading && <tr><td colSpan={8} className={grid.empty}>Loading…</td></tr>}
            {!loading && visible.length === 0 && <tr><td colSpan={8} className={grid.empty}>No contacts yet.</td></tr>}
            {visible.map(r => (
              <tr key={r.id} className={`${grid.row} ${r.isDeleted ? 'text-ink-muted line-through' : ''}`}>
                <td className={grid.td}><Link to={`/contacts/${r.id}`} className={grid.link}>{r.firstName} {r.lastName}</Link></td>
                <td className={`${grid.td} text-ink-muted`}>{r.email ?? '—'}</td>
                <td className={`${grid.td} text-ink-muted`}>{r.phone ?? '—'}</td>
                <td className={grid.td}>
                  {r.customerId
                    ? <Link to={`/customers/${r.customerId}`} className={grid.link}>{customerName.get(r.customerId) ?? r.customerId}</Link>
                    : <span className="text-ink-muted">—</span>}
                </td>
                <td className={grid.td}><OriginChip origin={r.origin} /></td>
                <td className={grid.td}>{r.isDeleted ? <Chip tone="bad">Deleted</Chip> : <Chip tone="ok">Active</Chip>}</td>
                <td className={`${grid.td} text-ink-muted`}>{formatDate(r.createdAt)}</td>
                <td className={`${grid.td} text-ink-muted`}>{formatDate(r.updatedAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </>
  );
}
