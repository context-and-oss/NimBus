import { useEffect, useRef, useState } from 'react';
import { api, HandoffJob } from '../../api';
import { useErpDemoState } from '../../demo-state';
import { PaneSection, SwitchButton } from '../shell/ui';

// Pending-handoff demo controls. The ERP adapter's CrmAccountCreated handler polls
// /api/admin/handoff-mode and, when Enabled, signals MarkPendingHandoff instead of writing the
// customer synchronously. Erp.Api then re-applies the write after `durationSeconds` and
// (probabilistically, per `failureRate`) either completes or fails the message.

const DEBOUNCE_MS = 300;

export default function HandoffModePanel() {
  const { refresh } = useErpDemoState();
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
      api.setHandoffMode({ enabled, durationSeconds, failureRate: failurePercent / 100 })
        .then(refresh)
        .catch(() => { /* ignore — UI will reflect next refresh */ });
    }, DEBOUNCE_MS);
    return () => {
      if (debounceRef.current) clearTimeout(debounceRef.current);
    };
  }, [enabled, durationSeconds, failurePercent, loaded, refresh]);

  return (
    <PaneSection
      title="Pending handoff"
      description={<>CrmAccountCreated returns <code className="font-mono">PendingHandoff</code> instead of writing immediately. Erp.Api settles the message after the delay.</>}
    >
      <SwitchButton
        on={enabled}
        disabled={!loaded}
        onClick={() => setEnabled((v) => !v)}
        label={`Pending-handoff mode: ${enabled ? 'ON' : 'OFF'}`}
        title="When ON, CrmAccountCreated returns PendingHandoff and Erp.Api settles the message after the configured delay."
      />
      <div className="flex flex-col gap-1">
        <div className="flex items-center justify-between text-xs text-ink-muted">
          <label htmlFor="handoff-duration">Duration</label>
          <span className="font-mono tabular-nums text-ink">{durationSeconds}s</span>
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
          className="w-full accent-accent"
        />
      </div>
      <div className="flex flex-col gap-1">
        <div className="flex items-center justify-between text-xs text-ink-muted">
          <label htmlFor="handoff-failure">Failure rate</label>
          <span className="font-mono tabular-nums text-ink">{failurePercent}% chance of failure</span>
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
          className="w-full accent-[#c50f1f]"
        />
      </div>
      <InFlightJobs />
    </PaneSection>
  );
}

function InFlightJobs() {
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
    void tick();
    const poll = setInterval(tick, 1000);
    const clock = setInterval(() => setNow(Date.now()), 250);
    return () => { cancelled = true; clearInterval(poll); clearInterval(clock); };
  }, []);

  return (
    <div className="flex flex-col gap-1.5 rounded-sm bg-canvas p-2.5">
      <h4 className="text-xs font-semibold">In-flight handoff jobs</h4>
      {jobs.length === 0 ? (
        <p className="text-xs text-ink-muted">No active handoff jobs</p>
      ) : (
        <ul className="flex flex-col gap-1 font-mono text-[11px]">
          {jobs.map((j) => {
            const remaining = Math.max(0, Math.ceil((new Date(j.dueAt).getTime() - now) / 1000));
            return (
              <li key={j.eventId} className="flex items-center gap-2">
                <span className="rounded-sm bg-white px-1.5 py-0.5">{j.externalJobId}</span>
                <span className="text-ink-muted">{j.eventId.slice(0, 8)}</span>
                <span className={`ml-auto ${remaining === 0 ? 'text-[#a4262c]' : 'text-accent'}`}>
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
