import type { ButtonHTMLAttributes, ReactNode } from 'react';
import { Link } from 'react-router-dom';

// Shared building blocks for the app shell: command bar, status chips, switches, record
// header, form rows and grid styles. Colours come from the @theme tokens in index.css.

// ---------- Command bar ----------

export function CommandBar({ children }: { children: ReactNode }) {
  return (
    <div className="sticky top-0 z-10 flex h-11 shrink-0 items-center gap-0.5 overflow-x-auto border-b border-line bg-white px-3">
      {children}
    </div>
  );
}

const commandClass =
  'inline-flex h-8 shrink-0 items-center gap-1.5 rounded px-2.5 text-[13px] whitespace-nowrap hover:bg-nav disabled:opacity-40 disabled:hover:bg-transparent focus-visible:outline-2 focus-visible:outline-accent';

type CommandButtonProps = ButtonHTMLAttributes<HTMLButtonElement> & {
  icon?: ReactNode;
  primary?: boolean;
  danger?: boolean;
};

export function CommandButton({ icon, primary, danger, className, children, type = 'button', ...rest }: CommandButtonProps) {
  const tone = primary ? 'text-accent font-semibold' : danger ? 'text-red-700' : 'text-ink';
  return (
    <button type={type} className={`${commandClass} ${tone} ${className ?? ''}`} {...rest}>
      {icon}
      {children}
    </button>
  );
}

export function CommandLink({ to, icon, primary, children }: { to: string; icon?: ReactNode; primary?: boolean; children: ReactNode }) {
  return (
    <Link to={to} className={`${commandClass} ${primary ? 'text-accent font-semibold' : 'text-ink'}`}>
      {icon}
      {children}
    </Link>
  );
}

export function CommandSeparator() {
  return <span aria-hidden="true" className="mx-1 h-5 w-px shrink-0 bg-line" />;
}

// ---------- Chips ----------

export type Tone = 'ok' | 'warn' | 'bad' | 'info' | 'neutral' | 'partner';

const chipTones: Record<Tone, { chip: string; dot: string }> = {
  ok: { chip: 'bg-[#dff6dd] text-[#0e700e]', dot: 'bg-[#107c10]' },
  warn: { chip: 'bg-[#fff4ce] text-[#8a5300]', dot: 'bg-[#c19c00]' },
  bad: { chip: 'bg-[#fde7e9] text-[#a4262c]', dot: 'bg-[#c50f1f]' },
  info: { chip: 'bg-accent-soft text-accent', dot: 'bg-accent' },
  neutral: { chip: 'bg-nav text-ink-muted', dot: 'bg-[#8a8886]' },
  partner: { chip: 'bg-[#f1e8fa] text-[#6b2fa0]', dot: 'bg-[#8a3fc7]' },
};

export function Chip({ tone, dot, pulse, children, ...rest }: { tone: Tone; dot?: boolean; pulse?: boolean; children: ReactNode; 'data-testid'?: string }) {
  const t = chipTones[tone];
  return (
    <span data-testid={rest['data-testid']} className={`inline-flex items-center gap-1.5 whitespace-nowrap rounded-full px-2 py-0.5 text-xs font-semibold ${t.chip}`}>
      {dot && <span className={`h-1.5 w-1.5 rounded-full ${t.dot} ${pulse ? 'animate-pulse' : ''}`} />}
      {children}
    </span>
  );
}

export function OriginChip({ origin }: { origin?: string | null }) {
  if (!origin) return <span className="text-ink-muted">—</span>;
  const tone: Tone = origin === 'Partner' ? 'partner' : origin === 'Crm' || origin === 'Erp' ? 'info' : 'neutral';
  return <Chip tone={tone}>{origin === 'Crm' ? 'CRM' : origin === 'Erp' ? 'ERP' : origin}</Chip>;
}

/** Status pill for the dark app bar. A span, never a button: the demo scripts find the mode
 *  toggles by accessible name, and a second match would make their locators ambiguous. */
export function BarPill({ tone, children, title }: { tone: 'bad' | 'warn' | 'busy'; children: ReactNode; title?: string }) {
  const cls = tone === 'bad' ? 'bg-[#c50f1f] text-white' : tone === 'warn' ? 'bg-[#bc4b09] text-white' : 'bg-white/15 text-white';
  const dot = tone === 'busy' ? 'bg-[#ffb900] animate-pulse' : 'bg-white';
  return (
    <span title={title} className={`inline-flex items-center gap-1.5 whitespace-nowrap rounded-full px-2.5 py-1 text-xs font-semibold ${cls}`}>
      <span className={`h-1.5 w-1.5 rounded-full ${dot}`} />
      {children}
    </span>
  );
}

// ---------- Switch ----------

/** A switch whose accessible name is its visible label, e.g. "Error mode: OFF". */
export function SwitchButton({ on, label, onClick, disabled, title, danger }: {
  on: boolean;
  label: string;
  onClick: () => void;
  disabled?: boolean;
  title?: string;
  danger?: boolean;
}) {
  const track = on ? (danger ? 'bg-[#c50f1f] border-[#c50f1f]' : 'bg-accent border-accent') : 'bg-white border-[#8a8886]';
  return (
    <button
      type="button"
      onClick={onClick}
      disabled={disabled}
      aria-pressed={on}
      title={title}
      className="inline-flex items-center gap-2.5 rounded text-[13px] font-semibold text-ink disabled:opacity-50 focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-accent"
    >
      <span aria-hidden="true" className={`relative inline-block h-5 w-9 shrink-0 rounded-full border transition-colors ${track}`}>
        <span className={`absolute top-[3px] h-3 w-3 rounded-full transition-all ${on ? 'left-[19px] bg-white' : 'left-[3px] bg-[#616161]'}`} />
      </span>
      <span>{label}</span>
    </button>
  );
}

// ---------- Demo pane sections ----------

export function PaneSection({ title, description, children }: { title?: ReactNode; description?: ReactNode; children?: ReactNode }) {
  return (
    <section className="flex flex-col gap-2.5 border-b border-nav px-4 py-4 last:border-b-0">
      {title && <h3 className="text-[11px] font-semibold uppercase tracking-wider text-ink-muted">{title}</h3>}
      {children}
      {description && <p className="text-xs leading-relaxed text-ink-muted">{description}</p>}
    </section>
  );
}

// ---------- Page parts ----------

export function ViewHeader({ title, count, children }: { title: string; count?: number; children?: ReactNode }) {
  return (
    <div className="flex flex-wrap items-center gap-3 px-5 pb-3 pt-4">
      <h1 className="text-xl font-semibold">{title}</h1>
      {count !== undefined && <span className="text-[13px] text-ink-muted">{count} {count === 1 ? 'record' : 'records'}</span>}
      <div className="ml-auto flex items-center gap-2">{children}</div>
    </div>
  );
}

export function FilterBox({ value, onChange, placeholder }: { value: string; onChange: (v: string) => void; placeholder: string }) {
  return (
    <input
      type="search"
      aria-label={placeholder}
      value={value}
      onChange={(e) => onChange(e.target.value)}
      placeholder={placeholder}
      className="h-8 w-64 rounded border border-line bg-white px-2.5 text-[13px] placeholder:text-ink-muted focus:border-accent focus:outline-none"
    />
  );
}

export function Panel({ title, actions, children, className }: { title?: ReactNode; actions?: ReactNode; children: ReactNode; className?: string }) {
  return (
    <section className={`rounded border border-line bg-white ${className ?? ''}`}>
      {title && (
        <header className="flex items-center gap-2 border-b border-nav px-4 py-2.5">
          <h2 className="text-[14px] font-semibold">{title}</h2>
          <div className="ml-auto flex items-center gap-2">{actions}</div>
        </header>
      )}
      <div className="p-4">{children}</div>
    </section>
  );
}

export function initials(name: string | undefined | null): string {
  const parts = (name ?? '').trim().split(/\s+/).filter(Boolean);
  if (parts.length === 0) return '?';
  return (parts[0][0] + (parts[1]?.[0] ?? '')).toUpperCase();
}

export function RecordHeader({ title, entity, avatarClass, fields, children }: {
  title: string;
  entity: string;
  avatarClass?: string;
  fields?: { label: string; value: ReactNode }[];
  children?: ReactNode;
}) {
  return (
    <div className="border-b border-line bg-white px-5 pt-4">
      <div className="flex flex-wrap items-center gap-4 pb-3">
        <span aria-hidden="true" className={`grid h-11 w-11 shrink-0 place-items-center rounded-full text-[15px] font-semibold text-white ${avatarClass ?? 'bg-accent'}`}>
          {initials(title)}
        </span>
        <div className="min-w-0">
          <h1 className="truncate text-xl font-semibold">{title}</h1>
          <div className="text-xs text-ink-muted">{entity}</div>
        </div>
        {fields && fields.length > 0 && (
          <dl className="ml-auto flex flex-wrap gap-x-8 gap-y-2">
            {fields.map((f) => (
              <div key={f.label} className="flex flex-col-reverse">
                <dt className="text-[11px] text-ink-muted">{f.label}</dt>
                <dd className="text-[13px] font-semibold">{f.value}</dd>
              </div>
            ))}
          </dl>
        )}
      </div>
      {children}
    </div>
  );
}

export function RecordTabs<T extends string>({ tabs, value, onChange }: { tabs: { id: T; label: string }[]; value: T; onChange: (id: T) => void }) {
  return (
    <div role="tablist" className="flex gap-6">
      {tabs.map((t) => (
        <button
          key={t.id}
          type="button"
          role="tab"
          aria-selected={value === t.id}
          onClick={() => onChange(t.id)}
          className={`-mb-px border-b-2 py-2 text-[13px] ${value === t.id ? 'border-accent font-semibold text-ink' : 'border-transparent text-ink-muted hover:text-ink'}`}
        >
          {t.label}
        </button>
      ))}
    </div>
  );
}

/** Label-left form row. The <label> wraps the input so `label:has-text("…") input` finds it. */
export function FieldRow({ label, required, children }: { label: string; required?: boolean; children: ReactNode }) {
  return (
    <label className="grid grid-cols-1 items-center gap-1 sm:grid-cols-[180px_minmax(0,1fr)] sm:gap-3">
      <span className="text-[13px] text-ink-muted">
        {label}
        {required && <span className="text-[#c50f1f]"> *</span>}
      </span>
      {children}
    </label>
  );
}

export const inputClass =
  'h-8 w-full rounded-sm border border-line bg-white px-2 text-[14px] hover:border-[#8a8886] focus:border-accent focus:outline-none disabled:bg-nav disabled:text-ink-muted';

export function ErrorBar({ children }: { children: ReactNode }) {
  return <div role="alert" className="border-b border-[#f1bbbc] bg-[#fde7e9] px-5 py-2 text-[13px] text-[#a4262c]">{children}</div>;
}

// ---------- Grid ----------

export const grid = {
  wrap: 'mx-5 mb-5 overflow-x-auto rounded border border-line bg-white',
  table: 'min-w-full text-[13px] tabular-nums',
  th: 'whitespace-nowrap border-b border-line px-3 py-2 text-left font-semibold',
  td: 'whitespace-nowrap border-b border-nav px-3 py-2',
  row: 'hover:bg-nav/60',
  link: 'text-accent hover:underline',
  empty: 'px-3 py-8 text-center text-ink-muted',
};

export function formatDate(value?: string | null) {
  return value ? new Date(value).toLocaleString() : '—';
}
