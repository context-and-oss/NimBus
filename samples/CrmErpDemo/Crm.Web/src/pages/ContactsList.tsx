import { Link, useSearchParams } from 'react-router-dom';
import { Contact, api } from '../api';
import { PlusIcon, RefreshIcon } from '../components/shell/icons';
import { Chip, CommandBar, CommandButton, CommandLink, FilterBox, OriginChip, Pager, ViewHeader, formatDate, grid } from '../components/shell/ui';
import { PAGE_SIZE, usePagedList } from '../usePagedList';

export default function ContactsList() {
  const [params] = useSearchParams();
  const partnerOnly = params.get('view') === 'partner';
  // The server pages and filters the list, and each row carries its account's name, so this
  // page no longer loads every contact and every account.
  const list = usePagedList<Contact>(
    (page, q) => api.listContacts({ page, q, origin: partnerOnly ? 'Partner' : undefined }),
    partnerOnly ? 'partner' : 'all');
  const { rows, total, loading, q } = list;

  return (
    <>
      <CommandBar>
        <CommandLink to="/contacts/new" icon={<PlusIcon />} primary>New contact</CommandLink>
        <CommandButton icon={<RefreshIcon />} onClick={list.refresh}>Refresh</CommandButton>
      </CommandBar>
      <ViewHeader title={partnerOnly ? 'Partner leads' : 'Contacts'} count={loading ? undefined : total}>
        <FilterBox value={list.filter} onChange={list.setFilter} placeholder="Filter by keyword" />
      </ViewHeader>
      <div className={grid.wrap}>
        <table className={grid.table}>
          <thead>
            <tr>
              <th className={grid.th}>Full name</th>
              <th className={grid.th}>Email</th>
              <th className={grid.th}>Phone</th>
              <th className={grid.th}>Account</th>
              <th className={grid.th}>Origin</th>
              <th className={grid.th}>Status</th>
              <th className={grid.th}>Created on</th>
              <th className={grid.th}>Modified on</th>
            </tr>
          </thead>
          <tbody>
            {loading && <tr><td colSpan={8} className={grid.empty}>Loading…</td></tr>}
            {!loading && rows.length === 0 && (
              <tr><td colSpan={8} className={grid.empty}>{q ? 'No contacts match the filter.' : partnerOnly ? 'No partner leads yet. Turn on Partner leads in Demo controls.' : 'No contacts yet.'}</td></tr>
            )}
            {rows.map(r => (
              <tr key={r.id} className={`${grid.row} ${r.isDeleted ? 'text-ink-muted line-through' : ''}`}>
                <td className={grid.td}><Link to={`/contacts/${r.id}`} className={grid.link}>{r.firstName} {r.lastName}</Link></td>
                <td className={`${grid.td} text-ink-muted`}>{r.email ?? '—'}</td>
                <td className={`${grid.td} text-ink-muted`}>{r.phone ?? '—'}</td>
                <td className={grid.td}>
                  {r.accountId
                    ? <Link to={`/accounts/${r.accountId}`} className={grid.link}>{r.accountLegalName ?? r.accountId}</Link>
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
