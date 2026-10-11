import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Contact, api } from '../api';

export default function ContactsList() {
  const [rows, setRows] = useState<Contact[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    api.listContacts().then(setRows).finally(() => setLoading(false));
  }, []);

  return (
    <section>
      <header className="flex items-center mb-4">
        <h1 className="text-2xl font-semibold">Contacts</h1>
        <Link to="/contacts/new" className="ml-auto px-3 py-2 bg-emerald-600 text-white rounded-md text-sm">New contact</Link>
      </header>
      <div className="bg-white rounded-lg shadow-xs border border-slate-200 overflow-hidden">
        <table className="min-w-full text-sm">
          <thead className="bg-slate-100 text-slate-600 text-left">
            <tr>
              <th className="px-4 py-2">Name</th>
              <th className="px-4 py-2">Email</th>
              <th className="px-4 py-2">Phone</th>
              <th className="px-4 py-2">Customer</th>
              <th className="px-4 py-2">Origin</th>
              <th className="px-4 py-2">Status</th>
              <th className="px-4 py-2">Created</th>
              <th className="px-4 py-2">Updated</th>
            </tr>
          </thead>
          <tbody>
            {loading && <tr><td colSpan={8} className="px-4 py-6 text-center text-slate-400">Loading…</td></tr>}
            {!loading && rows.length === 0 && <tr><td colSpan={8} className="px-4 py-6 text-center text-slate-400">No contacts yet.</td></tr>}
            {rows.map(r => (
              <tr key={r.id} className={`border-t border-slate-100 ${r.isDeleted ? 'opacity-60 line-through' : ''}`}>
                <td className="px-4 py-2"><Link to={`/contacts/${r.id}`} className="text-emerald-700 hover:underline">{r.firstName} {r.lastName}</Link></td>
                <td className="px-4 py-2 text-slate-500">{r.email ?? '—'}</td>
                <td className="px-4 py-2 text-slate-500">{r.phone ?? '—'}</td>
                <td className="px-4 py-2 text-slate-500">{r.customerId ?? '—'}</td>
                <td className="px-4 py-2"><OriginBadge origin={r.origin} /></td>
                <td className="px-4 py-2"><StatusBadge isDeleted={r.isDeleted} /></td>
                <td className="px-4 py-2 text-xs text-slate-500 whitespace-nowrap">{formatDate(r.createdAt)}</td>
                <td className="px-4 py-2 text-xs text-slate-500 whitespace-nowrap">{formatDate(r.updatedAt)}</td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </section>
  );
}

function OriginBadge({ origin }: { origin?: string }) {
  if (!origin) return <span className="text-slate-400">—</span>;
  const styles = origin === 'Crm'
    ? 'bg-blue-50 text-blue-700 ring-1 ring-blue-200'
    : origin === 'Erp'
      ? 'bg-emerald-50 text-emerald-700 ring-1 ring-emerald-200'
      : 'bg-slate-100 text-slate-600 ring-1 ring-slate-200';
  return <span className={`inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium ${styles}`}>{origin}</span>;
}

function StatusBadge({ isDeleted }: { isDeleted?: boolean }) {
  return isDeleted
    ? <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-rose-50 text-rose-700 ring-1 ring-rose-200">Deleted</span>
    : <span className="inline-flex items-center px-2 py-0.5 rounded-full text-xs font-medium bg-emerald-50 text-emerald-700 ring-1 ring-emerald-200">Active</span>;
}

function formatDate(value?: string | null) {
  return value ? new Date(value).toLocaleString() : '—';
}
