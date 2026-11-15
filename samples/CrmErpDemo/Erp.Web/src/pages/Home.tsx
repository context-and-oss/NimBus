import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { Customer, CustomerSummary, HandoffJob, api } from '../api';
import AlertsPanel, { useAlerts } from '../components/admin/alerts-panel';
import { PlusIcon } from '../components/shell/icons';
import { CommandBar, CommandLink, OriginChip, Panel, formatDate, grid } from '../components/shell/ui';

// Role center: cue tiles that count what a presenter points at, recent customers and the
// NimBus notification alerts.

const POLL_MS = 3000;

/** Midnight today in the browser's time zone, which is the day the "today" tile means. */
function startOfToday() {
  const start = new Date();
  start.setHours(0, 0, 0, 0);
  return start.toISOString();
}

export default function Home() {
  const [summary, setSummary] = useState<CustomerSummary | null>(null);
  const [recent, setRecent] = useState<Customer[]>([]);
  const [jobs, setJobs] = useState<HandoffJob[]>([]);
  const { alerts, clear } = useAlerts();

  useEffect(() => {
    let cancelled = false;
    const tick = async () => {
      // The server counts the customers and returns the six newest, where this page used to
      // load every customer every three seconds to do both itself.
      const [s, r, j] = await Promise.allSettled([
        api.getCustomerSummary(startOfToday()),
        api.listCustomers({ deleted: false, pageSize: 6 }),
        api.getHandoffJobs(),
      ]);
      if (cancelled) return;
      if (s.status === 'fulfilled') setSummary(s.value);
      if (r.status === 'fulfilled') setRecent(r.value.items);
      if (j.status === 'fulfilled') setJobs(j.value);
    };
    void tick();
    const t = setInterval(tick, POLL_MS);
    return () => { cancelled = true; clearInterval(t); };
  }, []);

  const fromCrmToday = summary?.fromCrmSince ?? 0;
  const onHold = summary?.onCreditHold ?? 0;
  const loading = summary === null;

  return (
    <>
      <CommandBar>
        <CommandLink to="/customers/new" icon={<PlusIcon />} primary>New customer</CommandLink>
        <CommandLink to="/contacts/new" icon={<PlusIcon />}>New contact</CommandLink>
      </CommandBar>
      <div className="flex flex-col gap-6 p-5">
        <div>
          <h1 className="text-2xl font-light">{greeting()}</h1>
          <p className="text-[13px] text-ink-muted">
            {loading ? 'Loading…' : headline(fromCrmToday, alerts.length)}
          </p>
        </div>

        <section aria-label="Integration activities" className="flex flex-col gap-2">
          <h2 className="text-xs font-semibold uppercase tracking-wider text-ink-muted">Integration activities</h2>
          <div className="flex flex-wrap gap-3">
            <Cue to="/customers" label="Customers" value={loading ? '…' : summary.active} />
            <Cue to="/customers?view=from-crm" label="From CRM today" value={loading ? '…' : fromCrmToday} />
            <Cue to="/customers?view=credit-hold" label="On credit hold" value={loading ? '…' : onHold} tone={onHold > 0 ? 'warn' : 'accent'} />
            <Cue label="Pending handoffs" value={jobs.length} tone={jobs.length > 0 ? 'warn' : 'idle'} hint="In-flight jobs; see Demo controls" />
            <Cue to="/alerts" label="Notification alerts" value={alerts.length} tone={alerts.length > 0 ? 'bad' : 'idle'} />
          </div>
        </section>

        <div className="grid gap-5 xl:grid-cols-[minmax(0,1.3fr)_minmax(0,1fr)]">
          <Panel title="Recent customers" actions={<Link to="/customers" className="text-[13px] text-accent hover:underline">See all</Link>}>
            <div className="-m-4 overflow-x-auto">
              <table className={grid.table}>
                <thead>
                  <tr>
                    <th className={grid.th}>No.</th>
                    <th className={grid.th}>Name</th>
                    <th className={grid.th}>Country</th>
                    <th className={grid.th}>Origin</th>
                    <th className={grid.th}>Created</th>
                  </tr>
                </thead>
                <tbody>
                  {!loading && recent.length === 0 && <tr><td colSpan={5} className={grid.empty}>No customers yet.</td></tr>}
                  {recent.map(c => (
                    <tr key={c.id} className={grid.row}>
                      <td className={`${grid.td} font-mono text-xs`}>{c.customerNumber}</td>
                      <td className={grid.td}><Link to={`/customers/${c.id}`} className={grid.link}>{c.legalName}</Link></td>
                      <td className={grid.td}>{c.countryCode}</td>
                      <td className={grid.td}><OriginChip origin={c.origin} /></td>
                      <td className={`${grid.td} text-ink-muted`}>{formatDate(c.createdAt)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          </Panel>
          <AlertsPanel alerts={alerts} clear={clear} limit={5} />
        </div>
      </div>
    </>
  );
}

const cueTones = {
  accent: 'bg-accent text-white hover:bg-accent-hover',
  warn: 'bg-[#c19c00] text-white hover:bg-[#a98900]',
  bad: 'bg-[#a4262c] text-white hover:bg-[#8e1f25]',
  idle: 'bg-[#e1dfdd] text-ink hover:bg-[#d2d0ce]',
};

function Cue({ to, label, value, tone = 'accent', hint }: { to?: string; label: string; value: number | string; tone?: keyof typeof cueTones; hint?: string }) {
  const body = (
    <>
      <span className="text-[12px] leading-tight">{label}</span>
      <span className="text-[32px] font-light leading-none tabular-nums">{value}</span>
    </>
  );
  const cls = `flex h-[100px] w-[136px] flex-col justify-between rounded-sm p-2.5 ${cueTones[tone]}`;
  return to
    ? <Link to={to} className={cls} title={hint}>{body}</Link>
    : <div className={cls} title={hint}>{body}</div>;
}

function greeting(): string {
  const h = new Date().getHours();
  return h < 12 ? 'Good morning' : h < 18 ? 'Good afternoon' : 'Good evening';
}

function headline(fromCrm: number, alertCount: number): string {
  const created = fromCrm === 0
    ? 'No customers have arrived from CRM today.'
    : `${fromCrm} ${fromCrm === 1 ? 'customer has' : 'customers have'} arrived from CRM today.`;
  const failures = alertCount === 0 ? '' : ` NimBus reported ${alertCount} ${alertCount === 1 ? 'alert' : 'alerts'}.`;
  return created + failures;
}
