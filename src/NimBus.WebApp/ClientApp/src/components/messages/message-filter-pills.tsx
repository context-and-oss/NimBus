import moment from "moment";
import {
  MessageSearchFilter,
  MessageSearchFilterMessageType,
} from "api-client";
import {
  AddFilterIcon,
  ChevronDown,
  EndpointSelectContent,
  IdSearchBox,
  MultiSelectContent,
  PillPopover,
  PublisherSubscriberContent,
  ResetFiltersButton,
  SingleSelectContent,
  TimePill,
  summary,
  useFilterOptions,
} from "components/ui/filter-pills";
import {
  fromLocalInput,
  isTimeFiltered,
  presetStart,
} from "functions/filter-pills.functions";

/**
 * Plain string-keyed shape of the message filter, suitable for URL-param round-tripping.
 * The page owns this; the pills are controlled components over it.
 *
 * Declared as a closed `type` (not `interface`) so it satisfies the index-signature
 * constraint of `useUrlFilters<T>`.
 */
export type MessageFilterValues = {
  endpointId: string;
  eventId: string;
  messageId: string;
  sessionId: string;
  eventTypeId: string[];
  from: string;
  to: string;
  messageType: string;
  /** Relative enqueued-time preset ("" for any time); a custom range below wins over it. */
  enqueuedPeriod: string;
  /** Custom range start, a datetime-local value in local time. */
  enqueuedFrom: string;
  /** Custom range end, a datetime-local value in local time. */
  enqueuedTo: string;
};

export const EMPTY_MESSAGE_FILTER: MessageFilterValues = {
  endpointId: "",
  eventId: "",
  messageId: "",
  sessionId: "",
  eventTypeId: [],
  from: "",
  to: "",
  messageType: "",
  enqueuedPeriod: "",
  enqueuedFrom: "",
  enqueuedTo: "",
};

/**
 * Build a MessageSearchFilter (the API contract type) from the flat filter values. A preset
 * window starts its span before `now` and is open-ended.
 */
export function toMessageSearchFilter(
  v: MessageFilterValues,
  now: Date = new Date(),
): MessageSearchFilter {
  const filter = new MessageSearchFilter();
  if (v.endpointId) filter.endpointId = v.endpointId;
  if (v.eventId) filter.eventId = v.eventId;
  if (v.messageId) filter.messageId = v.messageId;
  if (v.sessionId) filter.sessionId = v.sessionId;
  if (v.eventTypeId.length) filter.eventTypeId = [...v.eventTypeId];
  if (v.from) filter.senderEndpoint = v.from;
  if (v.to) filter.receiverEndpoint = v.to;
  if (v.messageType)
    filter.messageType = v.messageType as MessageSearchFilterMessageType;
  const custom = !!(v.enqueuedFrom || v.enqueuedTo);
  const start = custom
    ? fromLocalInput(v.enqueuedFrom)
    : presetStart(v.enqueuedPeriod, now);
  const end = custom ? fromLocalInput(v.enqueuedTo) : undefined;
  if (start) filter.enqueuedAtFrom = moment(start);
  if (end) filter.enqueuedAtTo = moment(end);
  return filter;
}

const MESSAGE_TYPE_OPTIONS = [
  { value: "", label: "Any type" },
  ...Object.entries(MessageSearchFilterMessageType).map(([key, value]) => ({
    label: key,
    value: value as string,
  })),
];

const messageTypeLabel = (value: string) =>
  MESSAGE_TYPE_OPTIONS.find((o) => o.value === value)?.label ?? value;

const ROUTE_FIELDS = [
  { key: "from", label: "From (publisher)", short: "From" },
  { key: "to", label: "To (subscriber)", short: "To" },
] as const;

export interface MessageFilterPillsProps {
  /** The applied filter (URL-driven). */
  value: MessageFilterValues;
  /** Applies a change to the filter. */
  onApply: (patch: Partial<MessageFilterValues>) => void;
  onReset: () => void;
}

/**
 * The Messages page's filter row, after the Failed page's: an enqueued-time pill, field pills
 * that apply on change, publisher/subscriber behind (+), and Reset filters.
 */
export default function MessageFilterPills({
  value,
  onApply,
  onReset,
}: MessageFilterPillsProps) {
  const { endpoints, eventTypes } = useFilterOptions();
  const time = {
    period: value.enqueuedPeriod,
    from: value.enqueuedFrom,
    to: value.enqueuedTo,
  };
  const filtered =
    isTimeFiltered(time) ||
    !!value.messageType ||
    !!value.endpointId ||
    value.eventTypeId.length > 0 ||
    !!value.from ||
    !!value.to ||
    !!value.eventId ||
    !!value.messageId ||
    !!value.sessionId;

  return (
    <div
      className="flex flex-wrap items-center gap-2"
      role="group"
      aria-label="Filters"
    >
      <TimePill
        label="Enqueued"
        value={time}
        onApply={(next) =>
          onApply({
            enqueuedPeriod: next.period,
            enqueuedFrom: next.from,
            enqueuedTo: next.to,
          })
        }
      />

      <PillPopover
        label="Message type"
        active={!!value.messageType}
        content={(close) => (
          <SingleSelectContent
            options={MESSAGE_TYPE_OPTIONS}
            value={value.messageType}
            onApply={(messageType) => onApply({ messageType })}
            close={close}
            columns={2}
          />
        )}
      >
        Message type ={" "}
        <b className="font-bold">
          {value.messageType ? messageTypeLabel(value.messageType) : "Any"}
        </b>
        <ChevronDown />
      </PillPopover>

      <PillPopover
        label="Endpoint"
        active={!!value.endpointId}
        content={(close) => (
          <SingleSelectContent
            options={[{ value: "", label: "Any endpoint" }, ...endpoints]}
            value={value.endpointId}
            onApply={(endpointId) => onApply({ endpointId })}
            close={close}
          />
        )}
      >
        Endpoint = <b className="font-bold">{value.endpointId || "Any"}</b>
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

      {ROUTE_FIELDS.filter((f) => !!value[f.key]).map((f) => (
        <PillPopover
          key={f.key}
          label={f.label}
          active
          onRemove={() => onApply({ [f.key]: "" })}
          content={(close) => (
            <EndpointSelectContent
              label={f.label}
              options={endpoints}
              value={value[f.key]}
              onApply={(next) => onApply({ [f.key]: next })}
              close={close}
            />
          )}
        >
          {f.short} = <b className="font-bold">{value[f.key]}</b>
        </PillPopover>
      ))}

      <PillPopover
        label="Add filter"
        className="px-3 text-primary-700"
        content={(close) => (
          <PublisherSubscriberContent
            options={endpoints}
            from={value.from}
            to={value.to}
            note="Event, message and session IDs go in the search box."
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

const MESSAGE_ID_PREFIXES = {
  event: "eventId",
  message: "messageId",
  session: "sessionId",
} as const;

/** The Messages page's ID search: `event:`, `message:`, `session:`, or a bare event ID. */
export function MessageSearchBox({
  value,
  onApply,
}: {
  value: MessageFilterValues;
  onApply: (
    patch: Pick<MessageFilterValues, "eventId" | "messageId" | "sessionId">,
  ) => void;
}) {
  return (
    <IdSearchBox
      label="Search messages"
      placeholder="Search by ID: event:… message:… session:… (a bare value finds an event)"
      prefixes={MESSAGE_ID_PREFIXES}
      bare="eventId"
      value={{
        eventId: value.eventId,
        messageId: value.messageId,
        sessionId: value.sessionId,
      }}
      onSubmit={onApply}
    />
  );
}
