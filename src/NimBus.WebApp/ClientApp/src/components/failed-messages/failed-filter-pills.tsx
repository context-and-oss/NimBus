import * as React from "react";
import * as api from "api-client";
import moment from "moment";
import { Button } from "components/ui/button";
import { Checkbox } from "components/ui/checkbox";
import { type ComboboxOption } from "components/ui/combobox";
import CustomRangeForm from "components/common/custom-range-form";
import { Select } from "components/ui/select";
import {
  AddFilterIcon,
  ChevronDown,
  EndpointSelectContent,
  MultiSelectContent,
  PillPopover,
  PublisherSubscriberContent,
  ResetFiltersButton,
  summary,
  useFilterOptions,
} from "components/ui/filter-pills";
import { periodLabel } from "functions/filter-pills.functions";
import {
  FAILED_STATUSES,
  PERIOD_OPTIONS,
  STATUS_COLORS,
  customRange,
  parseSearchQuery,
  periodOption,
  rangeParams,
  rangeWindow,
  searchQueryOf,
  toggleStatus,
  windowParams,
  type FailedFilterValues,
} from "functions/failed-messages.functions";
import { cn } from "lib/utils";

export { periodLabel } from "functions/filter-pills.functions";
export { PillPopover } from "components/ui/filter-pills";

const CUSTOM = "custom";

/** The time pill's label for the applied range: the preset, or the custom start–end. */
export function rangeLabel(
  value: Pick<FailedFilterValues, "period" | "rangeStart" | "rangeEnd">,
): string {
  const custom = customRange(value);
  if (!custom) return periodLabel(periodOption(value.period).label);
  const fmt = (d: Date) => moment(d).format("DD/MM/YYYY HH:mm");
  return `${fmt(custom.from)} – ${fmt(custom.to)}`;
}

/**
 * The time range picker, after Application Insights': a preset applies on click; Custom opens
 * local start and end fields, applied together and starting from the range on screen.
 */
function TimeRangeContent({
  value,
  onApply,
  close,
}: {
  value: FailedFilterValues;
  onApply: (patch: Partial<FailedFilterValues>) => void;
  close: () => void;
}) {
  const applied = customRange(value);
  const shown = applied ?? rangeWindow(value, new Date());
  const current = applied ? CUSTOM : periodOption(value.period).value;
  const [editing, setEditing] = React.useState(!!applied);
  const pick = (period: string) => {
    onApply({ period, ...rangeParams(undefined), ...windowParams(undefined) });
    close();
  };
  const option = (selected: boolean) =>
    cn(
      "rounded-nb-sm px-2.5 py-1.5 text-left hover:bg-muted",
      selected && "font-bold text-primary-700",
    );

  return (
    <div className="flex w-[330px] flex-col gap-2">
      <div className="px-2.5 text-xs font-medium text-muted-foreground">
        Time range
      </div>
      <div className="grid grid-flow-col grid-cols-2 grid-rows-4 gap-x-2">
        {PERIOD_OPTIONS.map((p) => (
          <button
            key={p.value}
            type="button"
            aria-pressed={current === p.value}
            onClick={() => pick(p.value)}
            className={option(current === p.value)}
          >
            {periodLabel(p.label)}
          </button>
        ))}
        <button
          type="button"
          aria-pressed={current === CUSTOM}
          aria-expanded={editing}
          onClick={() => setEditing((on) => !on)}
          className={option(current === CUSTOM || editing)}
        >
          Custom…
        </button>
      </div>
      {editing && (
        <CustomRangeForm
          label="Custom range"
          initial={shown}
          onApply={(range) => {
            onApply({ ...rangeParams(range), ...windowParams(undefined) });
            close();
          }}
          onCancel={close}
          className="border-t border-border px-1 pt-3"
        />
      )}
    </div>
  );
}

export interface FailedFilterPillsProps {
  /** The applied filter (URL-driven). */
  value: FailedFilterValues;
  /** Per-status counts shown in the status pill. */
  statusCounts?: Partial<Record<string, number>>;
  /** Applies a change to the filter. */
  onApply: (patch: Partial<FailedFilterValues>) => void;
  onReset: () => void;
}

/**
 * The Failed page's filter row, after Application Insights' transaction search: time, view
 * and field filters as pills, with the less-used publisher/subscriber filters behind (+).
 */
export default function FailedFilterPills({
  value,
  statusCounts,
  onApply,
  onReset,
}: FailedFilterPillsProps) {
  const { endpoints, eventTypes } = useFilterOptions();
  const custom = !!customRange(value);
  const statusOn = (status: string) =>
    value.status.length === 0 || value.status.includes(status);
  const filtered =
    custom ||
    value.endpointId.length > 0 ||
    value.eventTypeId.length > 0 ||
    value.status.length > 0 ||
    !!value.from ||
    !!value.to ||
    !!searchQueryOf(value);

  return (
    <div
      className="flex flex-wrap items-center gap-2"
      role="group"
      aria-label="Filters"
    >
      <PillPopover
        label="Time range"
        active={custom}
        content={(close) => (
          <TimeRangeContent value={value} onApply={onApply} close={close} />
        )}
      >
        Local time: <b className="font-bold">{rangeLabel(value)}</b>
        <ChevronDown />
      </PillPopover>

      <PillPopover
        label="View as"
        content={(close) => (
          <div className="flex min-w-[180px] flex-col">
            {[
              ["items", "Individual items"],
              ["table", "Table"],
            ].map(([key, text]) => (
              <button
                key={key}
                type="button"
                aria-pressed={value.display === key}
                onClick={() => {
                  onApply({ display: key });
                  close();
                }}
                className={cn(
                  "rounded-nb-sm px-2.5 py-1.5 text-left hover:bg-muted",
                  value.display === key && "font-bold text-primary-700",
                )}
              >
                {text}
              </button>
            ))}
          </div>
        )}
      >
        View as:{" "}
        <b className="font-bold">
          {value.display === "table" ? "Table" : "Individual items"}
        </b>
        <ChevronDown />
      </PillPopover>

      <PillPopover
        label="Status"
        active={value.status.length > 0}
        content={() => (
          <div className="flex min-w-[220px] flex-col gap-1">
            {FAILED_STATUSES.map((status) => (
              <label
                key={status}
                className="flex cursor-pointer items-center gap-2.5 rounded-nb-sm px-2 py-1.5 hover:bg-muted"
              >
                <Checkbox
                  checked={statusOn(status)}
                  onChange={() =>
                    onApply({ status: toggleStatus(value.status, status) })
                  }
                />
                <span
                  className="inline-block h-2 w-2 rounded-full"
                  style={{ background: STATUS_COLORS[status] }}
                />
                <span className="flex-1">{status}</span>
                {statusCounts?.[status] !== undefined && (
                  <span className="font-mono text-[11px] text-muted-foreground">
                    {statusCounts[status]}
                  </span>
                )}
              </label>
            ))}
          </div>
        )}
      >
        Status = <b className="font-bold">{summary(value.status)}</b>
        <ChevronDown />
      </PillPopover>

      <PillPopover
        label="Endpoint"
        active={value.endpointId.length > 0}
        content={(close) => (
          <MultiSelectContent
            options={endpoints}
            value={value.endpointId}
            placeholder="All endpoints"
            onApply={(endpointId) => onApply({ endpointId })}
            close={close}
          />
        )}
      >
        Endpoint = <b className="font-bold">{summary(value.endpointId)}</b>
        <ChevronDown />
      </PillPopover>

      <PillPopover
        label="Event type"
        active={value.eventTypeId.length > 0}
        content={(close) => (
          <MultiSelectContent
            options={eventTypes}
            value={value.eventTypeId}
            placeholder="All event types"
            onApply={(eventTypeId) => onApply({ eventTypeId })}
            close={close}
          />
        )}
      >
        Event type = <b className="font-bold">{summary(value.eventTypeId)}</b>
        <ChevronDown />
      </PillPopover>

      {(["from", "to"] as const)
        .filter((field) => !!value[field])
        .map((field) => {
          const text =
            field === "from" ? "From (publisher)" : "To (subscriber)";
          return (
            <PillPopover
              key={field}
              label={text}
              active
              onRemove={() => onApply({ [field]: "" })}
              content={(close) => (
                <EndpointSelectContent
                  label={text}
                  options={endpoints}
                  value={value[field]}
                  onApply={(next) => onApply({ [field]: next })}
                  close={close}
                />
              )}
            >
              {field === "from" ? "From" : "To"} ={" "}
              <b className="font-bold">{value[field]}</b>
            </PillPopover>
          );
        })}

      <PillPopover
        label="Add filter"
        className="px-3 text-primary-700"
        content={(close) => (
          <PublisherSubscriberContent
            options={endpoints}
            from={value.from}
            to={value.to}
            note="Event, message and session IDs and error text go in the search box."
            onApply={onApply}
            close={close}
          />
        )}
      >
        <AddFilterIcon />
      </PillPopover>

      {filtered && <ResetFiltersButton onClick={onReset} />}
    </div>
  );
}

export interface FailedSearchBoxProps {
  value: FailedFilterValues;
  onApply: (patch: Partial<FailedFilterValues>) => void;
}

/**
 * One search box for the ID and error-text filters. `event:`, `message:` and `session:` set
 * those fields, a bare GUID finds an event ID, and anything else searches the error text.
 */
export function FailedSearchBox({ value, onApply }: FailedSearchBoxProps) {
  const applied = searchQueryOf(value);
  const [draft, setDraft] = React.useState(applied);
  React.useEffect(() => setDraft(applied), [applied]);
  const submit = (text: string) => onApply(parseSearchQuery(text));

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
        aria-label="Search failures"
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        onKeyDown={(e) => {
          if (e.key === "Enter") submit(draft);
        }}
        placeholder="Search error text, e.g. 503 or timeout, or event:… message:… session:… (a bare GUID finds an event)"
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
