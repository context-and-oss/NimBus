import { useEffect, useState } from 'react';
import { Link, useSearchParams } from 'react-router-dom';
import { Account, Contact, api } from '../api';
import { PlusIcon, RefreshIcon } from '../components/shell/icons';
import { Chip, CommandBar, CommandButton, CommandLink, FilterBox, OriginChip, ViewHeader, formatDate, grid } from '../components/shell/ui';

export default function ContactsList() {
  const [params] = useSearchParams();
  const partnerOnly = params.get('view') === 'partner';
  const [rows, setRows] = useState<Contact[]>([]);
  const [accounts, setAccounts] = useState<Account[]>([]);
  const [loading, setLoading] = useState(true);
  const [filter, setFilter] = useState('');

  async function load() {
    const [contacts, accts] = await Promise.allSettled([api.listContacts(), api.listAccounts()]);
    if (contacts.status === 'fulfilled') setRows(contacts.value);
    if (accts.status === 'fulfilled') setAccounts(accts.value);
  }

  useEffect(() => {
    load().finally(() => setLoading(false));
  }, []);

  const accountName = new Map(accounts.map(a => [a.id, a.legalName]));
  const q = filter.trim().toLowerCase();
  const visible = rows
    .filter(r => !partnerOnly || r.origin === 'Partner')
    .filter(r => !q || [r.firstName, r.lastName, r.email, r.phone].some(v => v?.toLowerCase().includes(q)));

  return (
    <>
      <CommandBar>
        <CommandLink to="/contacts/new" icon={<PlusIcon />} primary>New contact</CommandLink>
        <CommandButton icon={<RefreshIcon />} onClick={() => void load()}>Refresh</CommandButton>
      </CommandBar>
      <ViewHeader title={partnerOnly ? 'Partner leads' : 'Contacts'} count={loading ? undefined : visible.length}>
        <FilterBox value={filter} onChange={setFilter} placeholder="Filter by keyword" />
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
            {!loading && visible.length === 0 && (
              <tr><td colSpan={8} className={grid.empty}>{partnerOnly ? 'No partner leads yet. Turn on Partner leads in Demo controls.' : 'No contacts yet.'}</td></tr>
            )}
            {visible.map(r => (
              <tr key={r.id} className={`${grid.row} ${r.isDeleted ? 'text-ink-muted line-through' : ''}`}>
                <td className={grid.td}><Link to={`/contacts/${r.id}`} className={grid.link}>{r.firstName} {r.lastName}</Link></td>
                <td className={`${grid.td} text-ink-muted`}>{r.email ?? '—'}</td>
                <td className={`${grid.td} text-ink-muted`}>{r.phone ?? '—'}</td>
                <td className={grid.td}>
                  {r.accountId
                    ? <Link to={`/accounts/${r.accountId}`} className={grid.link}>{accountName.get(r.accountId) ?? r.accountId}</Link>
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
