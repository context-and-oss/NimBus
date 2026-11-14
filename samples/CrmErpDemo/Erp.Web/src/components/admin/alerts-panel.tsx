import { useEffect, useState } from 'react';
import { api, type Alert } from '../../api';
import { Panel } from '../shell/ui';

// Live view of NimBus notification alerts. The ERP adapter fires a webhook to the ERP API on
// every message Failure / DeadLetter / SessionBlock; the API keeps the last 50 in memory and
// this part polls them every 3s. Turn on Error mode or Service mode in Demo controls to make
// inbound ERP messages fail and watch alerts appear here.

const POLL_MS = 3000;

function severityStyle(severity: string): { dot: string; text: string } {
  switch (severity) {
    case 'Critical': return { dot: 'bg-[#c50f1f]', text: 'text-[#a4262c]' };
    case 'Error':    return { dot: 'bg-[#da3b01]', text: 'text-[#a33000]' };
    case 'Warning':  return { dot: 'bg-[#c19c00]', text: 'text-[#8a5300]' };
    default:         return { dot: 'bg-[#8a8886]', text: 'text-ink-muted' };
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

/** Polls the alert list. Shared by this part and the Home cue tile. */
export function useAlerts(): { alerts: Alert[]; clear: () => Promise<void> } {
  const [alerts, setAlerts] = useState<Alert[]>([]);
  useEffect(() => {
    let cancelled = false;
    const tick = async () => {
      try {
        const next = await api.getAlerts();
        if (!cancelled) setAlerts(next);
      } catch { /* try again next tick */ }
    };
    void tick();
    const t = setInterval(tick, POLL_MS);
    return () => { cancelled = true; clearInterval(t); };
  }, []);
  const clear = async () => {
    await api.clearAlerts();
    setAlerts([]);
  };
  return { alerts, clear };
}

export default function AlertsPanel({ alerts, clear, limit }: { alerts: Alert[]; clear: () => Promise<void>; limit?: number }) {
  const [clearing, setClearing] = useState(false);

  async function onClear() {
    setClearing(true);
    try { await clear(); } catch { /* UI reflects the next refresh */ } finally { setClearing(false); }
  }

  const shown = limit ? alerts.slice(0, limit) : alerts;

  return (
    <Panel
      title={<>Notification alerts{alerts.length > 0 && <span className="ml-1.5 font-normal text-ink-muted">({alerts.length})</span>}</>}
      actions={
        <button
          type="button"
          onClick={() => void onClear()}
          disabled={clearing || alerts.length === 0}
          className="rounded-sm px-2 py-1 text-[13px] text-accent hover:bg-nav disabled:text-ink-muted disabled:hover:bg-transparent"
        >
          Clear
        </button>
      }
    >
      {alerts.length === 0 ? (
        <p className="text-[13px] text-ink-muted">
          No alerts yet. NimBus sends one for every failed, dead-lettered or blocked message. Turn on Error mode or
          Service mode in Demo controls to produce some.
        </p>
      ) : (
        <ul className="flex flex-col divide-y divide-nav">
          {shown.map((a, i) => {
            const s = severityStyle(a.severity);
            return (
              <li key={`${a.messageId}-${a.receivedAt}-${i}`} className="grid grid-cols-[8px_minmax(0,1fr)_auto] gap-x-3 py-2 first:pt-0 last:pb-0">
                <span className={`mt-1.5 h-2 w-2 rounded-full ${s.dot}`} />
                <div className="min-w-0">
                  <div className="text-[13px]">
                    <span className={`font-semibold ${s.text}`}>{a.severity}</span>
                    <span className="font-semibold"> · {a.title}</span>
                  </div>
                  <p className="break-words text-xs text-ink-muted">{a.message}</p>
                  {a.errorDetails && <p className="mt-0.5 line-clamp-2 break-words font-mono text-[11px] text-[#a4262c]">{a.errorDetails}</p>}
                  {a.eventTypeId && (
                    <p className="mt-0.5 font-mono text-[10px] text-ink-muted">
                      {a.eventTypeId}{a.eventId ? ` · ${a.eventId}` : ''}
                    </p>
                  )}
                </div>
                <span className="whitespace-nowrap font-mono text-[11px] text-ink-muted">{formatTime(a.receivedAt)}</span>
              </li>
            );
          })}
        </ul>
      )}
      {limit && alerts.length > limit && <p className="mt-2 text-xs text-ink-muted">Showing the latest {limit} of {alerts.length}.</p>}
    </Panel>
  );
}
