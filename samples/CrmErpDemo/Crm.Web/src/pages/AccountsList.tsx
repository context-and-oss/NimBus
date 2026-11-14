import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Account, api } from '../api';
import { PlusIcon, RefreshIcon } from '../components/shell/icons';
import { Chip, CommandBar, CommandButton, CommandLink, FilterBox, OriginChip, ViewHeader, formatDate, grid } from '../components/shell/ui';

export default function AccountsList() {
  const [rows, setRows] = useState<Account[]>([]);
  const [loading, setLoading] = useState(true);
  const [filter, setFilter] = useState('');

  // initial=true on the first mount shows the loading placeholder; background
  // polls re-fetch silently so the table doesn't flash "Loading…" every 3s.
  async function load(initial = false) {
    if (initial) setLoading(true);
    try { setRows(await api.listAccounts()); } finally { if (initial) setLoading(false); }
  }
  useEffect(() => {
    void load(true);
    const t = setInterval(() => void load(false), 3000);
    return () => clearInterval(t);
  }, []);

  const q = filter.trim().toLowerCase();
  const visible = q
    ? rows.filter(r => [r.legalName, r.countryCode, r.taxId, r.erpCustomerNumber].some(v => v?.toLowerCase().includes(q)))
    : rows;

  return (
    <>
      <CommandBar>
        <CommandLink to="/accounts/new" icon={<PlusIcon />} primary>New account</CommandLink>
        <CommandButton icon={<RefreshIcon />} onClick={() => void load(false)}>Refresh</CommandButton>
      </CommandBar>
      <ViewHeader title="Accounts" count={loading ? undefined : visible.length}>
        <FilterBox value={filter} onChange={setFilter} placeholder="Filter by keyword" />
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
            {!loading && visible.length === 0 && (
              <tr><td colSpan={8} className={grid.empty}>{q ? 'No accounts match the filter.' : 'No accounts yet. Create one, or turn on Simulate in Demo controls.'}</td></tr>
            )}
            {visible.map(r => (
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
