import { useEffect, useState } from 'react';
import { api } from '../../api';

// CloudEvents partner showcase panel: partner-portal publishes a PartnerLeadSubmitted lead
// every 45 seconds, which is too chatty for a demo left running. The switch lives in crm-api
// (in memory, like the outage toggle) and partner-portal reads it before each lead, so a flip
// takes effect within seconds and needs no restart.

export default function PartnerLeadsPanel() {
  const [enabled, setEnabled] = useState<boolean>(true);
  const [loaded, setLoaded] = useState<boolean>(false);

  useEffect(() => {
    let cancelled = false;
    (async () => {
      try {
        const m = await api.getPartnerLeads();
        if (!cancelled) {
          setEnabled(m.enabled);
          setLoaded(true);
        }
      } catch {
        /* crm-api unreachable — the switch stays disabled rather than showing a guess */
      }
    })();
    return () => {
      cancelled = true;
    };
  }, []);

  const toggle = async () => {
    try {
      const next = await api.setPartnerLeads(!enabled);
      setEnabled(next.enabled);
    } catch {
      /* ignore — the next click will reconcile */
    }
  };

  return (
    <section className="bg-white border border-slate-200 rounded-md p-4">
      <header className="flex items-center justify-between gap-4">
        <div>
          <h2 className="text-sm font-semibold text-slate-700">Partner leads · PartnerPortal</h2>
          <p className="text-xs text-slate-500">
            The simulated external partner publishes a lead as a raw CloudEvent every 45 seconds; each
            one arrives as a contact with a Partner badge. Turn the leads off to keep an idle demo quiet.
          </p>
        </div>
        <button
          type="button"
          onClick={toggle}
          disabled={!loaded}
          aria-pressed={enabled}
          className={`shrink-0 px-3 py-1.5 rounded-md text-xs font-medium disabled:opacity-50 ${
            enabled ? 'bg-violet-600 text-white' : 'bg-slate-200 text-slate-700'
          }`}
        >
          {`Partner leads: ${!loaded ? '…' : enabled ? 'ON' : 'OFF'}`}
        </button>
      </header>
    </section>
  );
}
