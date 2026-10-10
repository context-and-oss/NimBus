import { useEffect, useState } from 'react';
import { AuditEntry, api } from '../api';

interface Props {
  entityType: 'Account' | 'Contact';
  entityId: string | undefined;
}

interface Group {
  key: string;
  timestamp: string;
  origin?: string | null;
  rows: AuditEntry[];
  action: string;
}

// Reads /api/audit/{entityType}/{entityId} and renders the change history as
// a vertical timeline. Updated rows are grouped by their (timestamp, action)
// because a single SaveChanges may produce many field rows that share the
// same instant — we want them under one "06/05/26 12:34:56 — Updated" header
// rather than one bullet per field.
export default function AuditLog({ entityType, entityId }: Props) {
  const [entries, setEntries] = useState<AuditEntry[] | null>(null);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    if (!entityId) return;
    let cancelled = false;
    setError(null);
    api
      .getAuditLog(entityType, entityId)
      .then(rs => { if (!cancelled) setEntries(rs); })
      .catch(err => { if (!cancelled) setError(err instanceof Error ? err.message : String(err)); });
    return () => { cancelled = true; };
  }, [entityType, entityId]);

  if (!entityId) return null;

  const groups = group(entries ?? []);

  return (
    <section className="bg-white rounded-lg shadow-xs border border-slate-200 p-6 space-y-3">
      <h2 className="text-base font-semibold text-slate-700">Audit log</h2>
      {error && <div className="text-sm text-red-700">Failed to load audit history: {error}</div>}
      {entries === null && !error && <div className="text-sm text-slate-400">Loading…</div>}
      {entries !== null && groups.length === 0 && (
        <div className="text-sm text-slate-400">No history yet.</div>
      )}
      {groups.length > 0 && (
        <ol className="relative pl-6 space-y-4 before:absolute before:left-2 before:top-1 before:bottom-1 before:w-px before:bg-slate-200">
          {groups.map(g => (
            <li key={g.key} className="relative">
              <span className={`absolute left-[-22px] top-1.5 w-3 h-3 rounded-full border-2 border-white ${dotClass(g.action)}`} />
              <div className="flex flex-wrap items-center gap-2 text-xs">
                <span className={`px-1.5 py-0.5 rounded-sm font-semibold uppercase tracking-wide ${badgeClass(g.action)}`}>{g.action}</span>
                <span className="font-mono text-slate-500">{formatTimestamp(g.timestamp)}</span>
                {g.origin && (
                  <span className="px-1.5 py-0.5 rounded-sm bg-slate-100 text-slate-600 font-mono">via {g.origin}</span>
                )}
              </div>
              {g.action === 'Updated' && (
                <ul className="mt-1 text-sm text-slate-700 space-y-0.5">
                  {g.rows.filter(r => r.fieldName).map(r => (
                    <li key={r.id}>
                      <span className="font-medium text-slate-600">{r.fieldName}</span>:{' '}
                      <span className="font-mono text-slate-400 line-through">{display(r.oldValue)}</span>
                      {' → '}
                      <span className="font-mono text-slate-800">{display(r.newValue)}</span>
                    </li>
                  ))}
                </ul>
              )}
              {g.action === 'Created' && g.rows[0]?.newValue && (
                <details className="mt-1 text-sm">
                  <summary className="text-xs text-slate-500 cursor-pointer hover:underline">Initial values</summary>
                  <pre className="mt-1 px-2 py-1 bg-slate-50 border border-slate-100 rounded-sm text-[11px] font-mono overflow-x-auto whitespace-pre-wrap">{prettyJson(g.rows[0].newValue)}</pre>
                </details>
              )}
            </li>
          ))}
        </ol>
      )}
    </section>
  );
}

function group(entries: AuditEntry[]): Group[] {
  // Server returns newest-first; keep that order.
  const out: Group[] = [];
  for (const e of entries) {
    const last = out[out.length - 1];
    if (last && last.timestamp === e.timestamp && last.action === e.action) {
      last.rows.push(e);
    } else {
      out.push({ key: e.id, timestamp: e.timestamp, origin: e.origin, action: e.action, rows: [e] });
    }
  }
  return out;
}

function display(v: string | null | undefined): string {
  if (v === null || v === undefined || v === '') return '∅';
  return v;
}

function formatTimestamp(iso: string): string {
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return iso;
  return d.toLocaleString();
}

function prettyJson(raw: string): string {
  try { return JSON.stringify(JSON.parse(raw), null, 2); } catch { return raw; }
}

function badgeClass(action: string): string {
  switch (action) {
    case 'Created': return 'bg-emerald-100 text-emerald-800';
    case 'Updated': return 'bg-indigo-100 text-indigo-800';
    case 'Deleted': return 'bg-rose-100 text-rose-800';
    default:        return 'bg-slate-100 text-slate-700';
  }
}

function dotClass(action: string): string {
  switch (action) {
    case 'Created': return 'bg-emerald-500';
    case 'Updated': return 'bg-indigo-500';
    case 'Deleted': return 'bg-rose-500';
    default:        return 'bg-slate-400';
  }
}
