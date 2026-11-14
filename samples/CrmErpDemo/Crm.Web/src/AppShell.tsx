import type { ReactNode } from 'react';
import { Link, useLocation } from 'react-router-dom';
import CircuitBreakerPanel from './components/admin/circuit-breaker-panel';
import DataResetPanel from './components/admin/data-reset-panel';
import PartnerLeadsPanel from './components/admin/partner-leads-panel';
import SimulatorPanel from './components/admin/simulator-panel';
import SignedInBadge from './components/SignedInBadge';
import { useDemoPane } from './components/shell/demo-pane';
import { BuildingIcon, CloseIcon, GearIcon, HandshakeIcon, PersonIcon, Waffle } from './components/shell/icons';
import { BarPill } from './components/shell/ui';
import { useCrmDemoState } from './demo-state';
import { useSimulator } from './simulator';

// Sales-app shell: dark app bar, site map on the left, page in the middle and the Demo
// controls pane on the right (open by default).

export default function AppShell({ children }: { children: ReactNode }) {
  const pane = useDemoPane();
  return (
    <div className="flex h-full flex-col">
      <AppBar />
      <OutageBanner />
      <div className="relative flex min-h-0 flex-1">
        <SiteMap />
        <main className="flex min-w-0 flex-1 flex-col overflow-auto">{children}</main>
        {pane.open && <DemoPane onClose={() => pane.setOpen(false)} />}
      </div>
    </div>
  );
}

function AppBar() {
  const pane = useDemoPane();
  const sim = useSimulator();
  const { outage, circuit } = useCrmDemoState();
  return (
    <header className="flex h-12 shrink-0 items-center gap-4 bg-bar px-4 text-white">
      <Waffle />
      <Link to="/" className="flex items-baseline gap-2 whitespace-nowrap">
        <span className="text-[15px] font-semibold">CRM Demo</span>
        <span className="hidden text-[13px] text-white/60 sm:inline">Sales</span>
      </Link>
      <div className="ml-auto flex min-w-0 items-center gap-2">
        {sim.running && <BarPill tone="busy">Simulating · {sim.ratePerMinute}/min</BarPill>}
        {outage && <BarPill tone="bad">CRM API outage</BarPill>}
        {circuit?.state === 'Open' && <BarPill tone="bad" title={circuit.reason}>Circuit open</BarPill>}
        {circuit?.state === 'HalfOpen' && <BarPill tone="warn" title={circuit.reason}>Circuit half-open</BarPill>}
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

function OutageBanner() {
  const { outage } = useCrmDemoState();
  if (!outage) return null;
  return (
    <div className="shrink-0 bg-[#fde7e9] px-4 py-1.5 text-[13px] text-[#6e0811]">
      <strong>CRM API outage is on.</strong> Every CRM adapter call fails with 503 until you turn it off in
      Demo controls. Watch the CrmEndpoint circuit open in NimBus.
    </div>
  );
}

function SiteMap() {
  const { pathname, search } = useLocation();
  const partnerView = pathname.startsWith('/contacts') && new URLSearchParams(search).get('view') === 'partner';
  const items = [
    { group: 'Customers', to: '/accounts', label: 'Accounts', icon: <BuildingIcon />, active: pathname === '/' || pathname.startsWith('/accounts') },
    { group: 'Customers', to: '/contacts', label: 'Contacts', icon: <PersonIcon />, active: pathname.startsWith('/contacts') && !partnerView },
    { group: 'Integration', to: '/contacts?view=partner', label: 'Partner leads', icon: <HandshakeIcon />, active: partnerView },
  ];
  const groups = [...new Set(items.map((i) => i.group))];
  return (
    <nav aria-label="Site map" className="hidden w-52 shrink-0 flex-col border-r border-line bg-nav py-2 md:flex">
      {groups.map((g) => (
        <div key={g} className="flex flex-col">
          <span className="px-4 pb-1 pt-3 text-xs font-semibold text-ink-muted">{g}</span>
          {items.filter((i) => i.group === g).map((i) => (
            <Link
              key={i.to}
              to={i.to}
              aria-current={i.active ? 'page' : undefined}
              className={`flex items-center gap-2.5 border-l-[3px] px-4 py-1.5 text-[14px] ${
                i.active ? 'border-accent bg-accent-soft font-semibold' : 'border-transparent hover:bg-[#edebe9]'
              }`}
            >
              <span className={i.active ? 'text-accent' : 'text-ink-muted'}>{i.icon}</span>
              {i.label}
            </Link>
          ))}
        </div>
      ))}
      <div className="mt-auto border-t border-line px-4 pt-2 text-xs text-ink-muted">Sales</div>
    </nav>
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
        <SimulatorPanel />
        <CircuitBreakerPanel />
        <PartnerLeadsPanel />
        <DataResetPanel />
      </div>
    </aside>
  );
}
