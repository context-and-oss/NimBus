import { useEffect, useState } from 'react';
import { Link, NavLink, Route, Routes } from 'react-router-dom';
import CustomersList from './pages/CustomersList';
import CustomerForm from './pages/CustomerForm';
import ContactsList from './pages/ContactsList';
import ContactForm from './pages/ContactForm';
import HandoffModePanel from './components/admin/handoff-mode-panel';
import ProcessingDelayPanel from './components/admin/processing-delay-panel';
import AlertsPanel from './components/admin/alerts-panel';
import DataResetPanel from './components/admin/data-reset-panel';
import SignedInBadge from './components/SignedInBadge';
import { api, type FailureReason } from './api';

const tabClass = ({ isActive }: { isActive: boolean }) =>
  `px-4 py-2 rounded-md text-sm font-medium ${
    isActive ? 'bg-emerald-600 text-white' : 'text-slate-600 hover:bg-slate-200'
  }`;

export default function App() {
  return (
    <div className="min-h-full">
      <header className="bg-white border-b border-slate-200">
        <div className="max-w-5xl mx-auto px-6 py-4 flex items-center gap-6">
          <Link to="/" className="text-xl font-semibold text-emerald-700">ERP Demo</Link>
          <nav className="flex gap-2">
            <NavLink to="/customers" className={tabClass}>Customers</NavLink>
            <NavLink to="/contacts" className={tabClass}>Contacts</NavLink>
          </nav>
          <div className="ml-auto flex items-center gap-4">
            <ServiceModeToggle />
            <ErrorModeToggle />
            <span className="text-xs text-slate-400">NimBus · CRM + ERP demo</span>
            <SignedInBadge />
          </div>
        </div>
      </header>
      <ServiceModeBanner />
      <ErrorModeBanner />
      <main className="max-w-5xl mx-auto px-6 py-8 space-y-6">
        <HandoffModePanel />
        <ProcessingDelayPanel />
        <AlertsPanel />
        <DataResetPanel />
        <Routes>
          <Route path="/" element={<CustomersList />} />
          <Route path="/customers" element={<CustomersList />} />
          <Route path="/customers/new" element={<CustomerForm />} />
          <Route path="/customers/:id" element={<CustomerForm />} />
          <Route path="/contacts" element={<ContactsList />} />
          <Route path="/contacts/new" element={<ContactForm />} />
          <Route path="/contacts/:id" element={<ContactForm />} />
        </Routes>
      </main>
    </div>
  );
}

function useServiceMode() {
  const [enabled, setEnabled] = useState<boolean | null>(null);
  const [busy, setBusy] = useState(false);

  async function refresh() {
    try { setEnabled((await api.getServiceMode()).enabled); } catch { /* ignore */ }
  }

  async function toggle() {
    if (enabled === null || busy) return;
    setBusy(true);
    try { setEnabled((await api.setServiceMode(!enabled)).enabled); }
    finally { setBusy(false); }
  }

  useEffect(() => {
    refresh();
    const t = setInterval(refresh, 3000);
    return () => clearInterval(t);
  }, []);

  return { enabled, busy, toggle };
}

function ServiceModeToggle() {
  const { enabled, busy, toggle } = useServiceMode();
  const label = enabled === null ? 'Service mode' : enabled ? 'Service mode: ON' : 'Service mode: OFF';
  const cls = enabled
    ? 'bg-rose-600 text-white hover:bg-rose-700'
    : 'bg-slate-200 text-slate-700 hover:bg-slate-300';
  return (
    <button
      type="button"
      onClick={toggle}
      disabled={enabled === null || busy}
      className={`px-3 py-1.5 rounded-md text-xs font-medium disabled:opacity-50 ${cls}`}
      title="When ON, the ERP adapter rejects every inbound message — useful for showing the dead-letter / resubmit flow."
    >
      {label}
    </button>
  );
}

function ServiceModeBanner() {
  const [enabled, setEnabled] = useState(false);
  useEffect(() => {
    const tick = async () => {
      try { setEnabled((await api.getServiceMode()).enabled); } catch { /* ignore */ }
    };
    tick();
    const t = setInterval(tick, 3000);
    return () => clearInterval(t);
  }, []);
  if (!enabled) return null;
  return (
    <div className="bg-rose-600 text-white text-sm">
      <div className="max-w-5xl mx-auto px-6 py-2">
        ERP is in <strong>service mode</strong> — the ERP adapter rejects every inbound message until the toggle is turned off.
      </div>
    </div>
  );
}

/**
 * Error mode: every ERP adapter handler throws before doing any work. The selected
 * failure reason decides *what* it throws, so the NimBus WebApp's Integration
 * Intelligence card gets a realistic, distinct failure to classify per reason.
 */
function useErrorMode() {
  const [enabled, setEnabled] = useState<boolean | null>(null);
  const [reason, setReasonState] = useState<string>('');
  const [reasons, setReasons] = useState<FailureReason[]>([]);
  const [busy, setBusy] = useState(false);

  async function refresh() {
    try {
      const mode = await api.getErrorMode();
      setEnabled(mode.enabled);
      setReasonState(mode.reason);
    } catch { /* ignore */ }
  }

  async function apply(next: { enabled: boolean; reason?: string }) {
    if (busy) return;
    setBusy(true);
    try {
      const mode = await api.setErrorMode(next.enabled, next.reason);
      setEnabled(mode.enabled);
      setReasonState(mode.reason);
    } finally { setBusy(false); }
  }

  const toggle = () => { if (enabled !== null) void apply({ enabled: !enabled }); };
  const setReason = (id: string) => { if (enabled !== null) void apply({ enabled, reason: id }); };

  useEffect(() => {
    refresh();
    api.getErrorModeReasons().then(setReasons).catch(() => { /* ignore */ });
    const t = setInterval(refresh, 3000);
    return () => clearInterval(t);
  }, []);

  return { enabled, reason, reasons, busy, toggle, setReason };
}

function ErrorModeToggle() {
  const { enabled, reason, reasons, busy, toggle, setReason } = useErrorMode();
  const label = enabled === null ? 'Error mode' : enabled ? 'Error mode: ON' : 'Error mode: OFF';
  const cls = enabled
    ? 'bg-amber-600 text-white hover:bg-amber-700'
    : 'bg-slate-200 text-slate-700 hover:bg-slate-300';
  const selected = reasons.find((r) => r.id === reason);
  return (
    <div className="flex items-center gap-1.5">
      <button
        type="button"
        onClick={toggle}
        disabled={enabled === null || busy}
        className={`px-3 py-1.5 rounded-md text-xs font-medium disabled:opacity-50 ${cls}`}
        title="When ON, every ERP adapter message handler throws the selected failure."
      >
        {label}
      </button>
      <select
        aria-label="Failure reason"
        value={reason}
        onChange={(e) => setReason(e.target.value)}
        disabled={enabled === null || busy || reasons.length === 0}
        className="px-2 py-1.5 rounded-md text-xs border border-slate-300 bg-white text-slate-700 disabled:opacity-50 max-w-[16rem]"
        title={selected ? `${selected.description} NimBus outcome: ${selected.disposition}.` : 'Which failure the ERP handlers simulate while error mode is ON.'}
      >
        {reasons.length === 0 && <option value="">Loading reasons…</option>}
        {reasons.map((r) => (
          <option key={r.id} value={r.id}>{r.title}</option>
        ))}
      </select>
    </div>
  );
}

function ErrorModeBanner() {
  const [mode, setMode] = useState<{ enabled: boolean; reason: string } | null>(null);
  const [reasons, setReasons] = useState<FailureReason[]>([]);
  useEffect(() => {
    const tick = async () => {
      try {
        const m = await api.getErrorMode();
        setMode({ enabled: m.enabled, reason: m.reason });
      } catch { /* ignore */ }
    };
    tick();
    api.getErrorModeReasons().then(setReasons).catch(() => { /* ignore */ });
    const t = setInterval(tick, 3000);
    return () => clearInterval(t);
  }, []);
  if (!mode?.enabled) return null;
  const selected = reasons.find((r) => r.id === mode.reason);
  return (
    <div className="bg-amber-600 text-white text-sm">
      <div className="max-w-5xl mx-auto px-6 py-2 space-y-0.5">
        <div>
          ERP is in <strong>error mode</strong>
          {selected ? <> — simulating <strong>{selected.title}</strong></> : null}
          {' '}— message handlers throw on every inbound message until the toggle is turned off.
        </div>
        {selected ? (
          <div className="text-amber-100 text-xs">
            {selected.description} NimBus outcome: <strong>{selected.disposition}</strong>. Open the message in
            the NimBus WebApp and click <em>Analyze failure</em> to see Integration Intelligence classify it
            (expected: <code>{selected.expectedCategory}</code>).
          </div>
        ) : null}
      </div>
    </div>
  );
}
