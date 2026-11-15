import { Link } from 'react-router-dom';
import { Account, api } from '../api';
import { PlusIcon, RefreshIcon } from '../components/shell/icons';
import { Chip, CommandBar, CommandButton, CommandLink, FilterBox, OriginChip, Pager, ViewHeader, formatDate, grid } from '../components/shell/ui';
import { PAGE_SIZE, usePagedList } from '../usePagedList';

export default function AccountsList() {
  // The server pages and filters the list. The page in view is polled every 3 s, so a new
  // account or its ERP link shows up without a reload, and the table never flashes "Loading…".
  const list = usePagedList<Account>((page, q) => api.listAccounts({ page, q }), '', 3000);
  const { rows, total, loading, q } = list;

  return (
    <>
      <CommandBar>
        <CommandLink to="/accounts/new" icon={<PlusIcon />} primary>New account</CommandLink>
        <CommandButton icon={<RefreshIcon />} onClick={list.refresh}>Refresh</CommandButton>
      </CommandBar>
      <ViewHeader title="Accounts" count={loading ? undefined : total}>
        <FilterBox value={list.filter} onChange={list.setFilter} placeholder="Filter by keyword" />
      </ViewHeader>
      <div className={grid.wrap}>
        <table className={grid.table}>
          <thead>
            <tr>
              <th className={grid.th}>Account name</th>
              <th className={grid.th}>Country</th>
              <th className={grid.th}>Tax ID</th>
              <th className={grid.th}>Origin</th>
              <th className={grid.th}>ERP customer</th>
              <th className={grid.th}>Status</th>
              <th className={grid.th}>Created on</th>
              <th className={grid.th}>Modified on</th>
            </tr>
          </thead>
          <tbody>
            {loading && <tr><td colSpan={8} className={grid.empty}>Loading…</td></tr>}
            {!loading && rows.length === 0 && (
              <tr><td colSpan={8} className={grid.empty}>{q ? 'No accounts match the filter.' : 'No accounts yet. Create one, or turn on Simulate in Demo controls.'}</td></tr>
            )}
            {rows.map(r => (
              <tr key={r.id} className={`${grid.row} ${r.isDeleted ? 'text-ink-muted line-through' : ''}`}>
                <td className={grid.td}><Link to={`/accounts/${r.id}`} className={grid.link}>{r.legalName}</Link></td>
                <td className={grid.td}>{r.countryCode}</td>
                <td className={`${grid.td} text-ink-muted`}>{r.taxId ?? '—'}</td>
                <td className={grid.td}><OriginChip origin={r.origin} /></td>
                <td className={grid.td}><ErpSync account={r} /></td>
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

/** "✓ C-…" once ERP has linked a customer; the demo scripts wait for the ✓. */
function ErpSync({ account }: { account: Account }) {
  if (account.erpCustomerNumber) return <Chip tone="ok">✓ {account.erpCustomerNumber}</Chip>;
  if (account.isDeleted) return <span className="text-ink-muted">—</span>;
  return <Chip tone="warn" dot pulse>pending…</Chip>;
}
