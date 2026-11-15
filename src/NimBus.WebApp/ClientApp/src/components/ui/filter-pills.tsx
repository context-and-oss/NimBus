import * as React from "react";
import * as api from "api-client";
import CustomRangeForm from "components/common/custom-range-form";
import { Button } from "components/ui/button";
import { Combobox, type ComboboxOption } from "components/ui/combobox";
import { Select } from "components/ui/select";
import { PERIOD_OPTIONS } from "functions/failed-messages.functions";
import {
  fromLocalInput,
  idQueryOf,
  isTimeFiltered,
  parseIdQuery,
  periodLabel,
  presetStart,
  timeRangeLabel,
  toLocalInput,
  type TimeRangeValue,
} from "functions/filter-pills.functions";
import { cn } from "lib/utils";

/** Classes of a filter pill; `active` tints a pill whose filter narrows the results. */
export const pillClass = (active: boolean) =>
  cn(
    "inline-flex h-8 items-center gap-1 whitespace-nowrap rounded-full px-3.5 text-[13px] text-foreground transition-colors",
    active
      ? "bg-primary-tint ring-1 ring-primary dark:bg-primary-900/40"
      : "bg-muted hover:bg-border",
  );

/** The pill's dropdown chevron. */
export const ChevronDown = () => (
  <svg
    width="12"
    height="12"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="2.4"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <path d="M6 9l6 6 6-6" />
  </svg>
);

/** The funnel-plus icon of the Add filter pill. */
export const AddFilterIcon = () => (
  <svg
    width="18"
    height="18"
    viewBox="0 0 24 24"
    fill="none"
    stroke="currentColor"
    strokeWidth="2"
    strokeLinecap="round"
    strokeLinejoin="round"
    aria-hidden="true"
  >
    <path d="M3 5h12l-4.5 6v6l-3 2v-8z" />
    <path d="M19 4v6M16 7h6" />
  </svg>
);

export interface PillPopoverProps {
  /** Accessible name of the pill's button. */
  label: string;
  /** Visible pill text. */
  children: React.ReactNode;
  /** Highlights a pill whose filter narrows the results. */
  active?: boolean;
  /** Renders the popover content; call `close` to dismiss it. */
  content: (close: () => void) => React.ReactNode;
  /** Adds a remove (x) button next to the pill. */
  onRemove?: () => void;
  className?: string;
}

/** A filter pill (Application Insights style) that opens a small popover below it. */
export function PillPopover({
  label,
  children,
  active = false,
  content,
  onRemove,
  className,
}: PillPopoverProps) {
  const [open, setOpen] = React.useState(false);
  const ref = React.useRef<HTMLSpanElement>(null);
  const trigger = React.useRef<HTMLButtonElement>(null);
  const close = React.useCallback(() => {
    setOpen(false);
    trigger.current?.focus();
  }, []);

  React.useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (!ref.current?.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === "Escape") close();
    };
    document.addEventListener("mousedown", onDown);
    document.addEventListener("keydown", onKey);
    return () => {
      document.removeEventListener("mousedown", onDown);
      document.removeEventListener("keydown", onKey);
    };
  }, [open, close]);

  return (
    <span ref={ref} className="relative inline-flex">
      <span className={cn(pillClass(active), onRemove && "pr-1", className)}>
        <button
          ref={trigger}
          type="button"
          aria-label={label}
          aria-haspopup="dialog"
          aria-expanded={open}
          onClick={() => setOpen((o) => !o)}
          className="inline-flex items-center gap-1"
        >
          {children}
        </button>
        {onRemove && (
          <button
            type="button"
            aria-label={`Remove ${label}`}
            onClick={onRemove}
            className="ml-1 inline-flex h-6 w-6 items-center justify-center rounded-full text-muted-foreground hover:bg-card hover:text-foreground"
          >
            <svg
              width="11"
              height="11"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2.6"
              strokeLinecap="round"
              aria-hidden="true"
            >
              <path d="M6 6l12 12M18 6L6 18" />
            </svg>
          </button>
        )}
      </span>
      {open && (
        <div
          role="dialog"
          aria-label={label}
          className="absolute left-0 top-full z-40 mt-1.5 min-w-[260px] rounded-nb-md border border-border-strong bg-card p-3 text-sm shadow-nb-lg animate-fade-in"
        >
          {content(close)}
        </div>
      )}
    </span>
  );
}

/** A multi-value pill's summary: "All selected", the one value, or "first +n". */
export const summary = (values: string[], none = "All selected") =>
  values.length === 0
    ? none
    : values.length === 1
      ? values[0]
      : `${values[0]} +${values.length - 1}`;

/** Popover content picking several values from a searchable list; each pick applies at once. */
export function MultiSelectContent({
  options,
  value,
  placeholder,
  onApply,
  close,
}: {
  options: ComboboxOption[];
  value: string[];
  placeholder: string;
  onApply: (next: string[]) => void;
  close: () => void;
}) {
  // Each pick applies at once (the options list covers the popover while it is open).
  return (
    <div className="flex w-[320px] flex-col gap-2">
      <Combobox
        options={options}
        value={value}
        onChange={onApply}
        placeholder={placeholder}
        multiple
      />
      {value.length > 0 && (
        <button
          type="button"
          onClick={() => {
            onApply([]);
            close();
          }}
          className="self-start text-[12.5px] font-semibold text-primary-700 hover:underline"
        >
          Clear
        </button>
      )}
    </div>
  );
}

const optionClass = (selected: boolean) =>
  cn(
    "rounded-nb-sm px-2.5 py-1.5 text-left hover:bg-muted",
    selected && "font-bold text-primary-700",
  );

/** Popover content picking one value from a short list; a pick applies and closes. */
export function SingleSelectContent({
  options,
  value,
  onApply,
  close,
  columns = 1,
}: {
  options: ComboboxOption[];
  value: string;
  onApply: (next: string) => void;
  close: () => void;
  columns?: 1 | 2;
}) {
  return (
    <div
      className={cn(
        "grid gap-x-2",
        columns === 2 ? "w-[420px] grid-cols-2" : "min-w-[200px] grid-cols-1",
      )}
    >
      {options.map((o) => (
        <button
          key={o.value}
          type="button"
          aria-pressed={value === o.value}
          onClick={() => {
            onApply(o.value);
            close();
          }}
          className={cn("truncate", optionClass(value === o.value))}
        >
          {o.label}
        </button>
      ))}
    </div>
  );
}

/** The endpoints and event types the pills offer, loaded once per mount. */
export function useFilterOptions() {
  const [endpoints, setEndpoints] = React.useState<ComboboxOption[]>([]);
  const [eventTypes, setEventTypes] = React.useState<ComboboxOption[]>([]);
  React.useEffect(() => {
    const client = new api.Client(api.CookieAuth());
    client
      .getEndpointsAll()
      .then((ids) => setEndpoints(ids.map((e) => ({ value: e, label: e }))))
      .catch(() => setEndpoints([]));
    client
      .getEventTypes()
      .then((types) =>
        setEventTypes(
          types
            .map((t) => t.name)
            .filter((name): name is string => Boolean(name))
            .map((name) => ({ value: name, label: name })),
        ),
      )
      .catch(() => setEventTypes([]));
  }, []);
  return { endpoints, eventTypes };
}

/** Popover content editing one endpoint filter ("" for any endpoint), applied together. */
export function EndpointSelectContent({
  label,
  options,
  value,
  onApply,
  close,
}: {
  label: string;
  options: ComboboxOption[];
  value: string;
  onApply: (next: string) => void;
  close: () => void;
}) {
  const [draft, setDraft] = React.useState(value);
  const id = React.useId();
  return (
    <div className="flex w-[280px] flex-col gap-3">
      <label htmlFor={id} className="text-xs font-medium text-muted-foreground">
        {label}
      </label>
      <Select
        id={id}
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        options={[{ value: "", label: "Any endpoint" }, ...options]}
      />
      <Button
        size="sm"
        colorScheme="primary"
        onClick={() => {
          onApply(draft);
          close();
        }}
      >
        Apply
      </Button>
    </div>
  );
}

/** The Add filter (+) popover's publisher and subscriber selects, applied together. */
export function PublisherSubscriberContent({
  options,
  from: initialFrom,
  to: initialTo,
  note,
  onApply,
  close,
}: {
  options: ComboboxOption[];
  from: string;
  to: string;
  /** A hint under the selects, e.g. where the ID filters went. */
  note?: string;
  onApply: (patch: { from: string; to: string }) => void;
  close: () => void;
}) {
  const [from, setFrom] = React.useState(initialFrom);
  const [to, setTo] = React.useState(initialTo);
  const any = [{ value: "", label: "Any endpoint" }, ...options];
  const fromId = React.useId();
  const toId = React.useId();
  return (
    <div className="flex w-[300px] flex-col gap-3">
      <div>
        <label
          htmlFor={fromId}
          className="mb-1 block text-xs font-medium text-muted-foreground"
        >
          From (publisher)
        </label>
        <Select
          id={fromId}
          value={from}
          onChange={(e) => setFrom(e.target.value)}
          options={any}
        />
      </div>
      <div>
        <label
          htmlFor={toId}
          className="mb-1 block text-xs font-medium text-muted-foreground"
        >
          To (subscriber)
        </label>
        <Select
          id={toId}
          value={to}
          onChange={(e) => setTo(e.target.value)}
          options={any}
        />
      </div>
      {note && <p className="m-0 text-[12px] text-muted-foreground">{note}</p>}
      <Button
        size="sm"
        colorScheme="primary"
        onClick={() => {
          onApply({ from, to });
          close();
        }}
      >
        Apply
      </Button>
    </div>
  );
}

/** The "Reset filters" link at the end of a pill row. */
export function ResetFiltersButton({ onClick }: { onClick: () => void }) {
  return (
    <button
      type="button"
      onClick={onClick}
      className="ml-1 text-[13px] font-semibold text-primary-700 hover:underline"
    >
      Reset filters
    </button>
  );
}

/** Search ranges have no server span limit; only the order of the ends matters. */
function searchRangeError(from: Date, to: Date): string | undefined {
  if (Number.isNaN(from.getTime()) || Number.isNaN(to.getTime()))
    return "Enter a start and an end time.";
  if (from >= to) return "The start must be before the end.";
  return undefined;
}

const ANY_TIME = "";
const CUSTOM = "custom";

function TimeRangeContent({
  value,
  onApply,
  close,
}: {
  value: TimeRangeValue;
  onApply: (next: TimeRangeValue) => void;
  close: () => void;
}) {
  const custom = !!(value.from || value.to);
  const current = custom ? CUSTOM : value.period;
  const [editing, setEditing] = React.useState(custom);
  const initial = React.useMemo(() => {
    const now = new Date();
    const from =
      fromLocalInput(value.from) ??
      presetStart(value.period, now) ??
      new Date(now.getTime() - 86_400_000);
    return { from, to: fromLocalInput(value.to) ?? now };
  }, [value]);
  const pick = (period: string) => {
    onApply({ period, from: "", to: "" });
    close();
  };
  const presets = [
    { value: ANY_TIME, label: "Any time" },
    ...PERIOD_OPTIONS.map((p) => ({
      value: p.value as string,
      label: periodLabel(p.label),
    })),
  ];

  return (
    <div className="flex w-[330px] flex-col gap-2">
      <div className="px-2.5 text-xs font-medium text-muted-foreground">
        Time range
      </div>
      <div className="grid grid-flow-col grid-cols-2 grid-rows-4 gap-x-2">
        {presets.map((p) => (
          <button
            key={p.value}
            type="button"
            aria-pressed={current === p.value}
            onClick={() => pick(p.value)}
            className={optionClass(current === p.value)}
          >
            {p.label}
          </button>
        ))}
        <button
          type="button"
          aria-pressed={current === CUSTOM}
          aria-expanded={editing}
          onClick={() => setEditing((on) => !on)}
          className={optionClass(current === CUSTOM || editing)}
        >
          Custom…
        </button>
      </div>
      {editing && (
        <CustomRangeForm
          label="Custom range"
          initial={initial}
          validate={searchRangeError}
          onApply={(range) => {
            onApply({
              period: "",
              from: toLocalInput(range.from),
              to: toLocalInput(range.to),
            });
            close();
          }}
          onCancel={close}
          className="border-t border-border px-1 pt-3"
        />
      )}
    </div>
  );
}

/**
 * A time filter pill: any time, a relative preset (the window ends now) or a custom range.
 * `label` names the timestamp it filters ("Enqueued", "Updated").
 */
export function TimePill({
  label,
  value,
  onApply,
  onRemove,
}: {
  label: string;
  value: TimeRangeValue;
  onApply: (next: TimeRangeValue) => void;
  /** Adds a remove (x) button, for a time filter added through (+). */
  onRemove?: () => void;
}) {
  return (
    <PillPopover
      label={`${label} time range`}
      active={isTimeFiltered(value)}
      onRemove={onRemove}
      content={(close) => (
        <TimeRangeContent value={value} onApply={onApply} close={close} />
      )}
    >
      {label}: <b className="font-bold">{timeRangeLabel(value)}</b>
      <ChevronDown />
    </PillPopover>
  );
}

export interface IdSearchBoxProps<F extends string> {
  /** Accessible name of the search input. */
  label: string;
  placeholder: string;
  /** Search prefixes and the field each sets (`{ event: "eventId" }`). */
  prefixes: Readonly<Record<string, F>>;
  /** The field an unprefixed value sets. */
  bare: F;
  /** The applied IDs. */
  value: Readonly<Record<F, string>>;
  /** Called with every field on Enter or Clear. */
  onSubmit: (fields: Record<F, string>) => void;
}

/** One search box for a page's ID filters: `prefix:value`, or a bare value for `bare`. */
export function IdSearchBox<F extends string>({
  label,
  placeholder,
  prefixes,
  bare,
  value,
  onSubmit,
}: IdSearchBoxProps<F>) {
  const applied = idQueryOf(value, prefixes);
  const [draft, setDraft] = React.useState(applied);
  React.useEffect(() => setDraft(applied), [applied]);
  const submit = (text: string) => onSubmit(parseIdQuery(text, prefixes, bare));

  return (
    <div className="flex h-10 items-center gap-2.5 rounded-nb-sm border border-border-strong bg-card px-3 focus-within:border-primary focus-within:ring-2 focus-within:ring-primary-200">
      <svg
        width="16"
        height="16"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        strokeWidth="1.8"
        strokeLinecap="round"
        aria-hidden="true"
        className="shrink-0 text-muted-foreground"
      >
        <circle cx="11" cy="11" r="7" />
        <path d="M20 20l-3.5-3.5" />
      </svg>
      <input
        type="search"
        aria-label={label}
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Enter") submit(draft);
        }}
        placeholder={placeholder}
        className="h-full min-w-0 flex-1 bg-transparent text-sm text-foreground outline-hidden placeholder:text-muted-foreground"
      />
      {applied && (
        <button
          type="button"
          aria-label="Clear search"
          onClick={() => submit("")}
          className="text-[12.5px] font-semibold text-muted-foreground hover:text-foreground"
        >
          Clear
        </button>
      )}
    </div>
  );
}
