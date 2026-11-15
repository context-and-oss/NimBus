import * as React from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import { Checkbox } from "components/ui/checkbox";
import type { ComboboxOption } from "components/ui/combobox";
import { Input } from "components/ui/input";
import { Select } from "components/ui/select";
import {
  AddFilterIcon,
  ChevronDown,
  IdSearchBox,
  MultiSelectContent,
  PillPopover,
  ResetFiltersButton,
  SingleSelectContent,
  TimePill,
  summary,
} from "components/ui/filter-pills";
import {
  PERIOD_OPTIONS,
  STATUS_COLORS,
} from "functions/failed-messages.functions";
import { isTimeFiltered, periodLabel } from "functions/filter-pills.functions";
import { getEventTypesByEndpoint } from "hooks/event-types";
import { cn } from "lib/utils";

/**
 * One-click triage set applied by the Status pill's "Unresolved" link: the terminal failed
 * statuses an operator acts on. No Deferred — those retry on their own.
 */
export const FAILED_STATUS_SET = [
  api.ResolutionStatus.Failed,
  api.ResolutionStatus.DeadLettered,
  api.ResolutionStatus.Unsupported,
];

/** The filter values the pills read and write (the panel keeps them in the URL). */
export type EndpointPillValues = {
  /** Selected statuses; empty means every status. */
  status: string[];
  eventTypeId: string[];
  eventId: string;
  sessionId: string;
  /** Relative updated-time preset ("" for any time); a custom range wins over it. */
  updatedPeriod: string;
  updatedFrom: string;
  updatedTo: string;
  /** Relative added (enqueued) preset ("" for any time); a custom range wins over it. */
  addedPeriod: string;
  addedFrom: string;
  addedTo: string;
  payload: string;
};

export const EMPTY_ENDPOINT_PILLS: EndpointPillValues = {
  status: [],
  eventTypeId: [],
  eventId: "",
  sessionId: "",
  updatedPeriod: "",
  updatedFrom: "",
  updatedTo: "",
  addedPeriod: "",
  addedFrom: "",
  addedTo: "",
  payload: "",
};

const STATUS_ORDER = [
  ...FAILED_STATUS_SET,
  api.ResolutionStatus.Deferred,
  api.ResolutionStatus.Pending,
  api.ResolutionStatus.Completed,
  api.ResolutionStatus.Skipped,
  api.ResolutionStatus.Resolved,
];

const STATUS_DOT: Record<string, string> = {
  ...STATUS_COLORS,
  Deferred: "#C98A1B",
  Pending: "#3A6FB0",
  Completed: "#2E8F5E",
  Resolved: "#2E8F5E",
  Skipped: "#8C8677",
};

/**
 * Per-status counts from the endpoint's totals. The API's failedCount already includes
 * DeadLettered and Unsupported, so Failed alone is the remainder.
 */
export function statusCountsOf(
  count: api.EndpointStatusCount | undefined,
): Partial<Record<string, number>> {
  if (!count) return {};
  const deadLettered = count.deadletterCount ?? 0;
  const unsupported = count.unsupportedCount ?? 0;
  return {
    Failed: Math.max(0, (count.failedCount ?? 0) - deadLettered - unsupported),
    DeadLettered: deadLettered,
    Unsupported: unsupported,
    Deferred: count.deferredCount ?? 0,
    Pending: count.pendingCount ?? 0,
  };
}

function useEndpointEventTypes(endpointId: string): ComboboxOption[] {
  const [names, setNames] = React.useState<string[]>([]);
  React.useEffect(() => {
    let live = true;
    getEventTypesByEndpoint(endpointId)
      .then((result) => {
        const flatten = (groups?: api.EventTypeGrouping[]) =>
          (groups ?? [])
            .flatMap((g) => g.events ?? [])
            .map((e) => e.name)
            .filter((name): name is string => Boolean(name));
        if (live)
          setNames([
            ...new Set([
              ...flatten(result.consumes),
              ...flatten(result.produces),
            ]),
          ]);
      })
      .catch(() => live && setNames([]));
    return () => {
      live = false;
    };
  }, [endpointId]);
  return React.useMemo(
    () => names.map((name) => ({ value: name, label: name })),
    [names],
  );
}

function StatusContent({
  value,
  counts,
  onChange,
  close,
}: {
  value: string[];
  counts?: Partial<Record<string, number>>;
  onChange: (next: string[]) => void;
  close: () => void;
}) {
  const toggle = (status: string) =>
    onChange(
      value.includes(status)
        ? value.filter((s) => s !== status)
        : STATUS_ORDER.filter((s) => s === status || value.includes(s)),
    );
  const link = "text-[12.5px] font-semibold text-primary-700 hover:underline";
  return (
    <div className="flex min-w-[240px] flex-col gap-1">
      <div className="flex items-center justify-between px-2 pb-1 text-xs font-medium text-muted-foreground">
        <span>Status</span>
        <span className="flex gap-3">
          <button
            type="button"
            className={link}
            onClick={() => {
              onChange([...FAILED_STATUS_SET]);
              close();
            }}
          >
            Unresolved
          </button>
          <button
            type="button"
            className={link}
            onClick={() => {
              onChange([]);
              close();
            }}
          >
            All
          </button>
        </span>
      </div>
      {STATUS_ORDER.map((status, i) => (
        <React.Fragment key={status}>
          {i === FAILED_STATUS_SET.length && (
            <div className="mx-2 my-1 h-px bg-border" aria-hidden="true" />
          )}
          <label className="flex cursor-pointer items-center gap-2.5 rounded-nb-sm px-2 py-1.5 hover:bg-muted">
            <Checkbox
              checked={value.includes(status)}
              onChange={() => toggle(status)}
            />
            <span
              className="inline-block h-2 w-2 rounded-full"
              style={{ background: STATUS_DOT[status] }}
              aria-hidden="true"
            />
            <span className="flex-1">{status}</span>
            {counts?.[status] !== undefined && (
              <span className="font-mono text-[11px] text-muted-foreground">
                {counts[status]!.toLocaleString()}
              </span>
            )}
          </label>
        </React.Fragment>
      ))}
    </div>
  );
}

function PayloadContent({
  value,
  onApply,
  close,
}: {
  value: string;
  onApply: (payload: string) => void;
  close: () => void;
}) {
  const [draft, setDraft] = React.useState(value);
  const id = React.useId();
  return (
    <form
      className="flex w-[280px] flex-col gap-3"
      onSubmit={(e) => {
        e.preventDefault();
        onApply(draft.trim());
        close();
      }}
    >
      <label htmlFor={id} className="text-xs font-medium text-muted-foreground">
        Payload contains
      </label>
      <Input
        id={id}
        value={draft}
        onChange={(e) => setDraft(e.target.value)}
        placeholder="e.g. orderId:4471"
      />
      <Button type="submit" size="sm" colorScheme="primary">
        Apply
      </Button>
    </form>
  );
}

const ADDED_OPTIONS = [
  { value: "", label: "Any time" },
  ...PERIOD_OPTIONS.map((p) => ({
    value: p.value as string,
    label: periodLabel(p.label),
  })),
];

function AddFilterContent({
  value,
  hideReported,
  onApply,
  onHideReportedChange,
  close,
}: {
  value: EndpointPillValues;
  hideReported: boolean;
  onApply: (patch: Partial<EndpointPillValues>) => void;
  onHideReportedChange: (hide: boolean) => void;
  close: () => void;
}) {
  const customAdded = !!(value.addedFrom || value.addedTo);
  const [payload, setPayload] = React.useState(value.payload);
  const [added, setAdded] = React.useState(
    customAdded ? "" : value.addedPeriod,
  );
  const [hide, setHide] = React.useState(hideReported);
  const payloadId = React.useId();
  const addedId = React.useId();
  return (
    <form
      className="flex w-[300px] flex-col gap-3"
      onSubmit={(e) => {
        e.preventDefault();
        const patch: Partial<EndpointPillValues> = { payload: payload.trim() };
        // A custom added range is edited on its own pill; keep it unless a preset is picked.
        if (!customAdded || added)
          Object.assign(patch, {
            addedPeriod: added,
            addedFrom: "",
            addedTo: "",
          });
        onApply(patch);
        if (hide !== hideReported) onHideReportedChange(hide);
        close();
      }}
    >
      <div>
        <label
          htmlFor={addedId}
          className="mb-1 block text-xs font-medium text-muted-foreground"
        >
          Added
        </label>
        <Select
          id={addedId}
          value={added}
          onChange={(e) => setAdded(e.target.value)}
          options={
            customAdded
              ? [
                  { value: "", label: "Custom range (keep)" },
                  ...ADDED_OPTIONS.slice(1),
                ]
              : ADDED_OPTIONS
          }
        />
      </div>
      <div>
        <label
          htmlFor={payloadId}
          className="mb-1 block text-xs font-medium text-muted-foreground"
        >
          Payload contains
        </label>
        <Input
          id={payloadId}
          value={payload}
          onChange={(e) => setPayload(e.target.value)}
          placeholder="e.g. orderId:4471"
        />
      </div>
      <label className="flex cursor-pointer items-center gap-2.5 text-sm">
        <Checkbox
          checked={hide}
          onChange={(e) => setHide(e.target.checked)}
          aria-label="Hide reported events"
        />
        Hide reported events
      </label>
      <p className="m-0 text-[12px] text-muted-foreground">
        Event and session IDs go in the search box.
      </p>
      <Button type="submit" size="sm" colorScheme="primary">
        Apply
      </Button>
    </form>
  );
}

export interface EndpointFilterPillsProps {
  endpointId: string;
  /** The applied filter (URL-driven). */
  value: EndpointPillValues;
  /** Endpoint-wide count per status, where known. */
  statusCounts?: Partial<Record<string, number>>;
  hideReported: boolean;
  onHideReportedChange: (hide: boolean) => void;
  /** Applies a change to the filter (status goes through `onStatusChange`). */
  onApply: (patch: Partial<EndpointPillValues>) => void;
  /** Applies a status selection; empty means every status. */
  onStatusChange: (next: string[]) => void;
  onReset: () => void;
}

/**
 * The endpoint Messages tab's filter row, after the Failed page's: an updated-time pill,
 * status and event-type pills, and the rarer filters (added time, payload, hide reported)
 * behind (+), each shown as a removable pill once set.
 */
export default function EndpointFilterPills({
  endpointId,
  value,
  statusCounts,
  hideReported,
  onHideReportedChange,
  onApply,
  onStatusChange,
  onReset,
}: EndpointFilterPillsProps) {
  const eventTypes = useEndpointEventTypes(endpointId);
  const updated = {
    period: value.updatedPeriod,
    from: value.updatedFrom,
    to: value.updatedTo,
  };
  const added = {
    period: value.addedPeriod,
    from: value.addedFrom,
    to: value.addedTo,
  };
  const addedSet = isTimeFiltered(added);
  const payload = value.payload.trim();
  const filtered =
    isTimeFiltered(updated) ||
    addedSet ||
    value.status.length > 0 ||
    value.eventTypeId.length > 0 ||
    !!value.eventId ||
    !!value.sessionId ||
    !!payload ||
    hideReported;

  return (
    <div
      className="flex flex-wrap items-center gap-2"
      role="group"
      aria-label="Filters"
    >
      <TimePill
        label="Updated"
        value={updated}
        onApply={(next) =>
          onApply({
            updatedPeriod: next.period,
            updatedFrom: next.from,
            updatedTo: next.to,
          })
        }
      />

      <PillPopover
        label="Status"
        active={value.status.length > 0}
        content={(close) => (
          <StatusContent
            value={value.status}
            counts={statusCounts}
            onChange={onStatusChange}
            close={close}
          />
        )}
      >
        Status = <b className="font-bold">{summary(value.status)}</b>
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

      {addedSet && (
        <TimePill
          label="Added"
          value={added}
          onApply={(next) =>
            onApply({
              addedPeriod: next.period,
              addedFrom: next.from,
              addedTo: next.to,
            })
          }
          onRemove={() =>
            onApply({ addedPeriod: "", addedFrom: "", addedTo: "" })
          }
        />
      )}

      {payload && (
        <PillPopover
          label="Payload contains"
          active
          onRemove={() => onApply({ payload: "" })}
          content={(close) => (
            <PayloadContent
              value={payload}
              onApply={(next) => onApply({ payload: next })}
              close={close}
            />
          )}
        >
          Payload contains{" "}
          <b className="max-w-[240px] truncate font-bold">{payload}</b>
        </PillPopover>
      )}

      {hideReported && (
        <PillPopover
          label="Reported"
          active
          onRemove={() => onHideReportedChange(false)}
          content={(close) => (
            <SingleSelectContent
              options={[
                { value: "show", label: "Show reported" },
                { value: "hide", label: "Hide reported" },
              ]}
              value="hide"
              onApply={(next) => onHideReportedChange(next === "hide")}
              close={close}
            />
          )}
        >
          Reported = <b className="font-bold">Hidden</b>
        </PillPopover>
      )}

      <PillPopover
        label="Add filter"
        className={cn("px-3 text-primary-700")}
        content={(close) => (
          <AddFilterContent
            value={value}
            hideReported={hideReported}
            onApply={onApply}
            onHideReportedChange={onHideReportedChange}
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

const EVENT_ID_PREFIXES = { event: "eventId", session: "sessionId" } as const;

/** The endpoint Messages tab's ID search: `event:`, `session:`, or a bare event ID. */
export function EndpointSearchBox({
  value,
  onApply,
}: {
  value: Pick<EndpointPillValues, "eventId" | "sessionId">;
  onApply: (patch: Pick<EndpointPillValues, "eventId" | "sessionId">) => void;
}) {
  return (
    <IdSearchBox
      label="Search events"
      placeholder="Search by ID: event:… session:… (a bare value finds an event)"
      prefixes={EVENT_ID_PREFIXES}
      bare="eventId"
      value={{ eventId: value.eventId, sessionId: value.sessionId }}
      onSubmit={onApply}
    />
  );
}
