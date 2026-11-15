import { useState, type ReactNode } from 'react';
import { ChevronDownIcon, ChevronRightIcon } from './icons';

/** A collapsible card section with a one-line summary shown on the right of its heading. */
export function FastTab({ title, summary, defaultOpen = true, children }: { title: string; summary?: ReactNode; defaultOpen?: boolean; children: ReactNode }) {
  const [open, setOpen] = useState(defaultOpen);
  return (
    <section className="border-b border-line bg-white">
      <button
        type="button"
        onClick={() => setOpen(o => !o)}
        aria-expanded={open}
        className="flex w-full items-center gap-2 px-5 py-3 text-left hover:bg-canvas"
      >
        {open ? <ChevronDownIcon /> : <ChevronRightIcon />}
        <span className="text-[15px] font-semibold">{title}</span>
        {summary && <span className="ml-auto truncate text-[13px] text-ink-muted">{summary}</span>}
      </button>
      {open && <div className="px-5 pb-4 pl-11">{children}</div>}
    </section>
  );
}
