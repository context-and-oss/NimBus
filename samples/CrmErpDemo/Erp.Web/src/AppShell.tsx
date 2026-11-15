import type { ReactNode } from 'react';
import { Link, useLocation } from 'react-router-dom';
import DataResetPanel from './components/admin/data-reset-panel';
import { ErrorModeSection, ServiceModeSection } from './components/admin/failure-modes-panel';
import HandoffModePanel from './components/admin/handoff-mode-panel';
import ProcessingDelayPanel from './components/admin/processing-delay-panel';
import SignedInBadge from './components/SignedInBadge';
import { useDemoPane } from './components/shell/demo-pane';
import { CloseIcon, GearIcon, Waffle } from './components/shell/icons';
import { BarPill } from './components/shell/ui';
import { formatDelay, useErpDemoState } from './demo-state';

// Role-center shell: dark app bar, a navigation row under it, the page, and the Demo controls
// pane on the right (open by default).

export default function AppShell({ children }: { children: ReactNode }) {
  const pane = useDemoPane();
  return (
    <div className="flex h-full flex-col">
      <AppBar />
      <NavRow />
      <ServiceModeBanner />
      <ErrorModeBanner />
      <div className="relative flex min-h-0 flex-1">
        <main className="flex min-w-0 flex-1 flex-col overflow-auto">{children}</main>
        {pane.open && <DemoPane onClose={() => pane.setOpen(false)} />}
      </div>
    </div>
  );
}

function AppBar() {
  const pane = useDemoPane();
  const { serviceMode, errorMode, reasons, handoff, delay } = useErpDemoState();
  const reason = reasons.find((r) => r.id === errorMode?.reason);
  return (
    <header className="flex h-12 shrink-0 items-center gap-4 bg-bar px-4 text-white">
      <Waffle />
      <Link to="/" className="whitespace-nowrap text-[15px] font-semibold">ERP Demo</Link>
      <div className="ml-auto flex min-w-0 items-center gap-2">
        {serviceMode?.enabled && <BarPill tone="bad">Service mode</BarPill>}
        {errorMode?.enabled && <BarPill tone="warn" title={reason?.description}>Error mode{reason ? ` · ${reason.title}` : ''}</BarPill>}
        {handoff?.enabled && <BarPill tone="busy">Pending handoff · {handoff.durationSeconds}s</BarPill>}
        {delay?.enabled && <BarPill tone="busy">Delay · {formatDelay(delay.delayMs)}</BarPill>}
        <span className="hidden whitespace-nowrap px-2 text-xs text-white/50 xl:inline">NimBus · CRM + ERP demo</span>
        <button
          type="button"
          onClick={pane.toggle}
          aria-pressed={pane.open}
          aria-label="Demo controls"
          title="Demo controls"
          className={`grid h-8 w-8 shrink-0 place-items-center rounded hover:bg-bar-hover ${pane.open ? 'bg-white/20' : ''}`}
        >
          <GearIcon className="h-[18px] w-[18px]" />
        </button>
        <SignedInBadge />
      </div>
    </header>
  );
}

function NavRow() {
  const { pathname, search } = useLocation();
  const view = new URLSearchParams(search).get('view');
  const items = [
    { to: '/', label: 'Home', active: pathname === '/' },
    { to: '/customers', label: 'Customers', active: pathname.startsWith('/customers') && view !== 'credit-hold' },
    { to: '/contacts', label: 'Contacts', active: pathname.startsWith('/contacts') },
    { to: '/customers?view=credit-hold', label: 'Credit holds', active: pathname.startsWith('/customers') && view === 'credit-hold' },
    { to: '/alerts', label: 'Alerts', active: pathname.startsWith('/alerts') },
  ];
  return (
    <nav aria-label="Main" className="flex h-10 shrink-0 items-center gap-6 overflow-x-auto border-b border-line bg-white px-5">
      <span className="whitespace-nowrap text-[14px] font-semibold">Demo Company</span>
      {items.map((i) => (
        <Link
          key={i.to}
          to={i.to}
          aria-current={i.active ? 'page' : undefined}
          className={`-mb-px whitespace-nowrap border-b-2 py-2 text-[14px] ${
            i.active ? 'border-accent font-semibold text-accent' : 'border-transparent hover:text-accent'
          }`}
        >
          {i.label}
        </Link>
      ))}
    </nav>
  );
}

function ServiceModeBanner() {
  const { serviceMode } = useErpDemoState();
  if (!serviceMode?.enabled) return null;
  return (
    <div className="shrink-0 bg-[#fde7e9] px-5 py-1.5 text-[13px] text-[#6e0811]">
      ERP is in <strong>service mode</strong>. The ERP adapter rejects every inbound message until you turn it off.
    </div>
  );
}

function ErrorModeBanner() {
  const { errorMode, reasons } = useErpDemoState();
  if (!errorMode?.enabled) return null;
  const selected = reasons.find((r) => r.id === errorMode.reason);
  return (
    <div className="flex shrink-0 flex-col gap-0.5 bg-[#fff4ce] px-5 py-1.5 text-[13px] text-[#5c3a00]">
      <div>
        ERP is in <strong>error mode</strong>
        {selected ? <>, simulating <strong>{selected.title}</strong></> : null}. Message handlers throw on every inbound
        message until you turn it off.
      </div>
      {selected ? (
        <div className="text-xs">
          {selected.description} NimBus outcome: <strong>{selected.disposition}</strong>. Open the message in the NimBus
          WebApp and click <em>Analyze failure</em> to see Integration Intelligence classify it (expected:{' '}
          <code className="font-mono">{selected.expectedCategory}</code>).
        </div>
      ) : null}
    </div>
  );
}

function DemoPane({ onClose }: { onClose: () => void }) {
  return (
    <aside
      aria-label="Demo controls"
      className="z-20 flex w-80 shrink-0 flex-col border-l border-line bg-white max-lg:absolute max-lg:inset-y-0 max-lg:right-0 max-lg:shadow-xl"
    >
      <header className="flex items-center justify-between border-b border-line px-4 py-3">
        <h2 className="text-[15px] font-semibold">Demo controls</h2>
        <button type="button" onClick={onClose} aria-label="Close demo controls" className="grid h-7 w-7 place-items-center rounded hover:bg-nav">
          <CloseIcon />
        </button>
      </header>
      <div className="min-h-0 flex-1 overflow-y-auto">
        <ServiceModeSection />
        <ErrorModeSection />
        <HandoffModePanel />
        <ProcessingDelayPanel />
        <DataResetPanel />
      </div>
    </aside>
  );
}
