import { useEffect, useRef, useState } from 'react';
import { api, HandoffJob } from '../../api';

// Pending-handoff demo controls. The ERP adapter's CrmAccountCreated handler
// polls /api/admin/handoff-mode and, when Enabled, signals MarkPendingHandoff
// instead of writing the customer synchronously. Erp.Api then re-applies the
// write after `durationSeconds` and (probabilistically, per `failureRate`)
// either completes or fails the message.

const DEBOUNCE_MS = 300;

export default function HandoffModePanel() {
  const [enabled, setEnabled] = useState<boolean>(false);
  const [durationSeconds, setDurationSeconds] = useState<number>(10);
  // failureRate is shown as 0..100 in the UI and divided by 100 on send.
  const [failurePercent, setFailurePercent] = useState<number>(0);
  const [loaded, setLoaded] = useState<boolean>(false);
  const debounceRef = useRef<ReturnType<typeof setTimeout> | null>(null);
  const skipNextSendRef = useRef<boolean>(true);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const m = await api.getHandoffMode();
        if (cancelled) return;
        setEnabled(m.enabled);
        setDurationSeconds(Math.max(1, Math.min(60, Math.round(m.durationSeconds))));
        setFailurePercent(Math.max(0, Math.min(100, Math.round(m.failureRate * 100))));
        setLoaded(true);
      } catch {
        setLoaded(true);
      }
    })();
    return () => { cancelled = true; };
  }, []);

  useEffect(() => {
    if (!loaded) return;
    // First render after the GET seeds initial values; don't echo it back.
    if (skipNextSendRef.current) {
      skipNextSendRef.current = false;
      return;
    }
    if (debounceRef.current) clearTimeout(debounceRef.current);
    debounceRef.current = setTimeout(() => {
      api.setHandoffMode({
        enabled,
        durationSeconds,
        failureRate: failurePercent / 100,
      }).catch(() => { /* ignore — UI will reflect next refresh */ });
    }, DEBOUNCE_MS);
    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current);
    };
  }, [enabled, durationSeconds, failurePercent, loaded]);

  return (
    <section className="bg-white border border-slate-200 rounded-md p-4 space-y-4">
      <header className="flex items-center justify-between">
        <div>
          <h2 className="text-sm font-semibold text-slate-700">Pending-handoff mode</h2>
          <p className="text-xs text-slate-500">
            When enabled, CrmAccountCreated returns <code>PendingHandoff</code> instead of writing immediately.
            Erp.Api settles the message after the configured delay.
          </p>
        </div>
        <button
          type="button"
          onClick={() => setEnabled((v) => !v)}
          disabled={!loaded}
          className={`px-3 py-1.5 rounded-md text-xs font-medium disabled:opacity-50 ${
            enabled
              ? 'bg-indigo-600 text-white hover:bg-indigo-700'
              : 'bg-slate-200 text-slate-700 hover:bg-slate-300'
          }`}
          title="When ON, CrmAccountCreated returns PendingHandoff and Erp.Api settles the message after the configured delay."
        >
          {`Pending-handoff mode: ${enabled ? 'ON' : 'OFF'}`}
        </button>
      </header>

      <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
        <div>
          <div className="flex items-center justify-between text-xs text-slate-600">
            <label htmlFor="handoff-duration">Duration</label>
            <span className="font-mono">{durationSeconds}s</span>
          </div>
          <input
            id="handoff-duration"
            type="range"
            min={1}
            max={60}
            step={1}
            value={durationSeconds}
            onChange={(e) => setDurationSeconds(Number(e.target.value))}
            disabled={!loaded}
            className="w-full accent-indigo-600"
          />
        </div>
        <div>
          <div className="flex items-center justify-between text-xs text-slate-600">
            <label htmlFor="handoff-failure">Failure rate</label>
            <span className="font-mono">{failurePercent}% chance of failure</span>
          </div>
          <input
            id="handoff-failure"
            type="range"
            min={0}
            max={100}
            step={1}
            value={failurePercent}
            onChange={(e) => setFailurePercent(Number(e.target.value))}
            disabled={!loaded}
            className="w-full accent-rose-600"
          />
        </div>
      </div>

      <InFlightJobsPanel />
    </section>
  );
}

function InFlightJobsPanel() {
  const [jobs, setJobs] = useState<HandoffJob[]>([]);
  const [now, setNow] = useState<number>(Date.now());

  useEffect(() => {
    let cancelled = false;
    const tick = async () => {
      try {
        const list = await api.getHandoffJobs();
        if (!cancelled) setJobs(list);
      } catch { /* ignore */ }
    };
    tick();
    const poll = setInterval(tick, 1000);
    const clock = setInterval(() => setNow(Date.now()), 250);
    return () => { cancelled = true; clearInterval(poll); clearInterval(clock); };
  }, []);

  return (
    <div className="border-t border-slate-200 pt-3">
      <h3 className="text-xs font-semibold text-slate-600 mb-2">In-flight handoff jobs</h3>
      {jobs.length === 0 ? (
        <p className="text-xs text-slate-400">No active handoff jobs</p>
      ) : (
        <ul className="space-y-1 text-xs font-mono">
          {jobs.map((j) => {
            const remainingMs = new Date(j.dueAt).getTime() - now;
            const remaining = Math.max(0, Math.ceil(remainingMs / 1000));
            return (
              <li key={j.eventId} className="flex items-center gap-3 text-slate-700">
                <span className="px-1.5 py-0.5 bg-slate-100 rounded-sm">{j.externalJobId}</span>
                <span className="text-slate-500">{j.eventId.slice(0, 8)}</span>
                <span className={remaining === 0 ? 'text-rose-600' : 'text-indigo-600'}>
                  {remaining === 0 ? 'settling…' : `${remaining}s remaining`}
                </span>
              </li>
            );
          })}
        </ul>
      )}
    </div>
  );
}
