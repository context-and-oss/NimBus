import { useEffect, useState } from 'react';
import { api, type Alert } from '../../api';

// Live view of NimBus notification alerts. The ERP adapter fires a webhook to the ERP API on
// every message Failure / DeadLetter / SessionBlock; the API keeps the last 50 in memory and
// this panel polls them every 3s. Flip "Error mode" or "Service mode" (header toggles) to make
// inbound ERP messages fail and watch alerts appear here.

const POLL_MS = 3000;

function severityStyle(severity: string): { dot: string; badge: string } {
  switch (severity) {
    case 'Critical':
      return { dot: 'bg-rose-500', badge: 'bg-rose-100 text-rose-700' };
    case 'Error':
      return { dot: 'bg-amber-500', badge: 'bg-amber-100 text-amber-700' };
    case 'Warning':
      return { dot: 'bg-yellow-400', badge: 'bg-yellow-100 text-yellow-700' };
    default:
      return { dot: 'bg-slate-400', badge: 'bg-slate-100 text-slate-600' };
  }
}

function formatTime(iso: string): string {
  const t = new Date(iso);
  if (Number.isNaN(t.getTime())) return '';
  const secondsAgo = Math.max(0, Math.round((Date.now() - t.getTime()) / 1000));
  if (secondsAgo < 60) return `${secondsAgo}s ago`;
  if (secondsAgo < 3600) return `${Math.round(secondsAgo / 60)}m ago`;
  return t.toLocaleTimeString();
}

export default function AlertsPanel() {
  const [alerts, setAlerts] = useState<Alert[]>([]);
  const [clearing, setClearing] = useState(false);

  useEffect(() => {
    let cancelled = false;
    const tick = async () => {
      try {
        const next = await api.getAlerts();
        if (!cancelled) setAlerts(next);
      } catch {
        /* ignore — try again next tick */
      }
    };
    tick();
    const t = setInterval(tick, POLL_MS);
    return () => {
      cancelled = true;
      clearInterval(t);
    };
  }, []);

  async function clear() {
    setClearing(true);
    try {
      await api.clearAlerts();
      setAlerts([]);
    } catch {
      /* ignore — UI will reflect next refresh */
    } finally {
      setClearing(false);
    }
  }

  return (
    <section className="bg-white border border-slate-200 rounded-md p-4 space-y-4">
      <header className="flex items-center justify-between">
        <div>
          <h2 className="text-sm font-semibold text-slate-700">
            Notification alerts
            {alerts.length > 0 && (
              <span className="ml-2 text-xs font-normal text-slate-400">({alerts.length})</span>
            )}
          </h2>
          <p className="text-xs text-slate-500">
            NimBus fires a webhook on every message failure, dead-letter, or blocked session — the
            ERP adapter sends them here. Flip <strong>Error mode</strong> or{' '}
            <strong>Service mode</strong> above to generate some.
          </p>
        </div>
        <button
          type="button"
          onClick={clear}
          disabled={clearing || alerts.length === 0}
          className="px-3 py-1.5 rounded-md text-xs font-medium bg-slate-200 text-slate-700 hover:bg-slate-300 disabled:opacity-50"
        >
          Clear
        </button>
      </header>

      {alerts.length === 0 ? (
        <p className="text-xs text-slate-400 italic">No alerts yet.</p>
      ) : (
        <ul className="space-y-2">
          {alerts.map((a, i) => {
            const s = severityStyle(a.severity);
            return (
              <li
                key={`${a.messageId}-${a.receivedAt}-${i}`}
                className="flex gap-3 border border-slate-100 rounded-md p-3"
              >
                <span className={`mt-1 h-2 w-2 shrink-0 rounded-full ${s.dot}`} />
                <div className="min-w-0 flex-1">
                  <div className="flex items-center gap-2">
                    <span className={`px-1.5 py-0.5 rounded-sm text-[10px] font-semibold uppercase ${s.badge}`}>
                      {a.severity}
                    </span>
                    <span className="text-sm font-medium text-slate-700 truncate">{a.title}</span>
                    <span className="ml-auto text-[10px] text-slate-400 font-mono shrink-0">
                      {formatTime(a.receivedAt)}
                    </span>
                  </div>
                  <p className="text-xs text-slate-600 mt-0.5 break-words">{a.message}</p>
                  {a.errorDetails && (
                    <p className="text-[11px] text-rose-600 font-mono mt-1 break-words line-clamp-3">
                      {a.errorDetails}
                    </p>
                  )}
                  {a.eventTypeId && (
                    <p className="text-[10px] text-slate-400 font-mono mt-1">
                      {a.eventTypeId}
                      {a.eventId ? ` · ${a.eventId}` : ''}
                    </p>
                  )}
                </div>
              </li>
            );
          })}
        </ul>
      )}
    </section>
  );
}
