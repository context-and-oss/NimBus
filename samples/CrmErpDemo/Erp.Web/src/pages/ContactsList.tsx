import { Link } from 'react-router-dom';
import { Contact, api } from '../api';
import { PlusIcon, RefreshIcon } from '../components/shell/icons';
import { Chip, CommandBar, CommandButton, CommandLink, FilterBox, OriginChip, Pager, ViewHeader, formatDate, grid } from '../components/shell/ui';
import { PAGE_SIZE, usePagedList } from '../usePagedList';

export default function ContactsList() {
  // The server pages and filters the list, and each row carries its customer's name, so this
  // page no longer loads every contact and every customer.
  const list = usePagedList<Contact>((page, q) => api.listContacts({ page, q }));
  const { rows, total, loading, q } = list;

  return (
    <>
      <CommandBar>
        <CommandLink to="/contacts/new" icon={<PlusIcon />} primary>New contact</CommandLink>
        <CommandButton icon={<RefreshIcon />} onClick={list.refresh}>Refresh</CommandButton>
      </CommandBar>
      <ViewHeader title="Contacts" count={loading ? undefined : total}>
        <FilterBox value={list.filter} onChange={list.setFilter} placeholder="Search contacts" />
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
            {!loading && rows.length === 0 && <tr><td colSpan={8} className={grid.empty}>{q ? 'No contacts match the search.' : 'No contacts yet.'}</td></tr>}
            {rows.map(r => (
              <tr key={r.id} className={`${grid.row} ${r.isDeleted ? 'text-ink-muted line-through' : ''}`}>
                <td className={grid.td}><Link to={`/contacts/${r.id}`} className={grid.link}>{r.firstName} {r.lastName}</Link></td>
                <td className={`${grid.td} text-ink-muted`}>{r.email ?? '—'}</td>
                <td className={`${grid.td} text-ink-muted`}>{r.phone ?? '—'}</td>
                <td className={grid.td}>
                  {r.customerId
                    ? <Link to={`/customers/${r.customerId}`} className={grid.link}>{r.customerLegalName ?? r.customerId}</Link>
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
        <Pager page={list.page} pageSize={PAGE_SIZE} total={total} onPage={list.setPage} />
      </div>
    </>
  );
}
