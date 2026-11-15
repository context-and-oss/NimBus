import { useEffect, useState } from 'react';
import { AuditEntry, api } from '../api';
import { Panel } from './shell/ui';

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

// Reads /api/audit/{entityType}/{entityId} and renders the change history as the record's
// timeline. Updated rows are grouped by (timestamp, action) because a single SaveChanges may
// produce many field rows that share the same instant; they belong under one entry.
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
    <Panel title="Timeline">
      {error && <div className="text-[13px] text-[#a4262c]">Couldn't load the history: {error}</div>}
      {entries === null && !error && <div className="text-[13px] text-ink-muted">Loading…</div>}
      {entries !== null && groups.length === 0 && <div className="text-[13px] text-ink-muted">No history yet.</div>}
      {groups.length > 0 && (
        <ol className="flex flex-col gap-4">
          {groups.map(g => (
            <li key={g.key} className="grid grid-cols-[28px_minmax(0,1fr)] gap-3">
              <span aria-hidden="true" className={`grid h-7 w-7 place-items-center rounded-full text-[12px] font-bold text-white ${dotClass(g.action)}`}>
                {glyph(g.action)}
              </span>
              <div className="min-w-0">
                <div className="text-[13px]">
                  <span className="font-semibold">{title(g.action)}</span>
                  {g.origin && <span className="text-ink-muted"> · via {g.origin}</span>}
                </div>
                <div className="text-xs text-ink-muted">{formatTimestamp(g.timestamp)}</div>
                {g.action === 'Updated' && (
                  <ul className="mt-1 flex flex-col gap-0.5 text-[13px]">
                    {g.rows.filter(r => r.fieldName).map(r => (
                      <li key={r.id} className="break-words">
                        <span className="text-ink-muted">{r.fieldName}</span>{' '}
                        <span className="font-mono text-xs text-ink-muted line-through">{display(r.oldValue)}</span>
                        {' → '}
                        <span className="font-mono text-xs">{display(r.newValue)}</span>
                      </li>
                    ))}
                  </ul>
                )}
                {g.action === 'Created' && g.rows[0]?.newValue && (
                  <details className="mt-1 text-[13px]">
                    <summary className="cursor-pointer text-xs text-accent hover:underline">Initial values</summary>
                    <pre className="mt-1 overflow-x-auto whitespace-pre-wrap rounded-sm border border-line bg-canvas px-2 py-1 font-mono text-[11px]">{prettyJson(g.rows[0].newValue)}</pre>
                  </details>
                )}
              </div>
            </li>
          ))}
        </ol>
      )}
    </Panel>
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

function title(action: string): string {
  switch (action) {
    case 'Created': return 'Record created';
    case 'Updated': return 'Fields changed';
    case 'Deleted': return 'Record deleted';
    default:        return action;
  }
}

function glyph(action: string): string {
  switch (action) {
    case 'Created': return '+';
    case 'Updated': return '✎';
    case 'Deleted': return '×';
    default:        return '•';
  }
}

function dotClass(action: string): string {
  switch (action) {
    case 'Created': return 'bg-[#107c10]';
    case 'Updated': return 'bg-accent';
    case 'Deleted': return 'bg-[#c50f1f]';
    default:        return 'bg-[#8a8886]';
  }
}
