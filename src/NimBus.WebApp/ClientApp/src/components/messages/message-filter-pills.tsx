import { useEffect, useMemo, useState } from "react";
import { Input } from "components/ui/input";
import { Button } from "components/ui/button";
import { Select } from "components/ui/select";
import { Combobox, type ComboboxOption } from "components/ui/combobox";
import {
  Client,
  CookieAuth,
  MessageSearchFilter,
  MessageSearchFilterMessageType,
} from "api-client";

/**
 * Plain string-keyed shape of the message filter, suitable for URL-param round-tripping.
 * The page owns this; the bar is a controlled component over it.
 *
 * Declared as a closed `type` (not `interface`) so it satisfies the index-signature
 * constraint of `useUrlFilters<T>`.
 */
export type MessageFilterValues = {
  endpointId: string;
  eventId: string;
  messageId: string;
  sessionId: string;
  eventTypeId: string;
  from: string;
  to: string;
  messageType: string;
  enqueuedFrom: string;
  enqueuedTo: string;
};

export const EMPTY_MESSAGE_FILTER: MessageFilterValues = {
  endpointId: "",
  eventId: "",
  messageId: "",
  sessionId: "",
  eventTypeId: "",
  from: "",
  to: "",
  messageType: "",
  enqueuedFrom: "",
  enqueuedTo: "",
};

/**
 * Build a MessageSearchFilter (the API contract type) from the flat filter values.
 */
export function toMessageSearchFilter(
  v: MessageFilterValues,
): MessageSearchFilter {
  const filter = new MessageSearchFilter();
  if (v.endpointId) filter.endpointId = v.endpointId;
  if (v.eventId) filter.eventId = v.eventId;
  if (v.messageId) filter.messageId = v.messageId;
  if (v.sessionId) filter.sessionId = v.sessionId;
  if (v.eventTypeId) filter.eventTypeId = [v.eventTypeId];
  if (v.from) filter.senderEndpoint = v.from;
  if (v.to) filter.receiverEndpoint = v.to;
  if (v.messageType)
    filter.messageType = v.messageType as MessageSearchFilterMessageType;
  if (v.enqueuedFrom) filter.enqueuedAtFrom = new Date(v.enqueuedFrom) as any;
  if (v.enqueuedTo) filter.enqueuedAtTo = new Date(v.enqueuedTo) as any;
  return filter;
}

interface MessageFilterBarProps {
  /** Currently applied filter (URL-driven). The bar's draft state is reset to this whenever it changes. */
  value: MessageFilterValues;
  /** Called when the user clicks Search. Receives the typed-up draft. */
  onSearch: (filter: MessageFilterValues) => void;
  /** Called when the user clicks Reset. */
  onReset: () => void;
  isLoading: boolean;
}

const messageTypeOptions = Object.entries(MessageSearchFilterMessageType).map(
  ([key, value]) => ({ label: key, value }),
);

export default function MessageFilterBar({
  value,
  onSearch,
  onReset,
  isLoading,
}: MessageFilterBarProps) {
  const [draft, setDraft] = useState<MessageFilterValues>(value);
  const [endpoints, setEndpoints] = useState<string[]>([]);
  const [eventTypeOptions, setEventTypeOptions] = useState<ComboboxOption[]>(
    [],
  );

  // When the applied filter changes (new search OR browser Back/forward), reset the draft.
  useEffect(() => {
    setDraft(value);
  }, [value]);

  useEffect(() => {
    const client = new Client(CookieAuth());
    client
      .getEndpointsAll()
      .then(setEndpoints)
      .catch(() => setEndpoints([]));
    client
      .getEventTypes()
      .then((types) =>
        setEventTypeOptions(
          types
            .map((t) => t.name)
            .filter((name): name is string => Boolean(name))
            .map((name) => ({ value: name, label: name })),
        ),
      )
      .catch(() => setEventTypeOptions([]));
  }, []);

  const endpointOptions = useMemo(
    () => [
      { value: "", label: "All endpoints" },
      ...endpoints.map((endpoint) => ({ value: endpoint, label: endpoint })),
    ],
    [endpoints],
  );

  const update = <K extends keyof MessageFilterValues>(
    key: K,
    next: MessageFilterValues[K],
  ) => setDraft((d) => ({ ...d, [key]: next }));

  const handleSearch = () => onSearch(draft);
  const handleReset = () => {
    setDraft(EMPTY_MESSAGE_FILTER);
    onReset();
  };
  const handleKeyDown = (e: React.KeyboardEvent) => {
    if (e.key === "Enter") handleSearch();
  };

  return (
    <div className="bg-muted border border-border rounded-lg p-4 mb-4">
      <div className="grid grid-cols-2 md:grid-cols-3 lg:grid-cols-4 gap-3">
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Endpoint
          </label>
          <Select
            value={draft.endpointId}
            onChange={(e) => update("endpointId", e.target.value)}
            options={endpointOptions}
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Event ID
          </label>
          <Input
            value={draft.eventId}
            onChange={(e) => update("eventId", e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Filter by event ID..."
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Message ID
          </label>
          <Input
            value={draft.messageId}
            onChange={(e) => update("messageId", e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Filter by message ID..."
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Session ID
          </label>
          <Input
            value={draft.sessionId}
            onChange={(e) => update("sessionId", e.target.value)}
            onKeyDown={handleKeyDown}
            placeholder="Filter by session ID..."
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Event Type
          </label>
          <Combobox
            options={eventTypeOptions}
            value={draft.eventTypeId ? [draft.eventTypeId] : []}
            onChange={(val) => update("eventTypeId", val[0] ?? "")}
            placeholder="Filter by event type..."
            multiple={false}
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            From (Publisher)
          </label>
          <Select
            value={draft.from}
            onChange={(e) => update("from", e.target.value)}
            options={endpointOptions}
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            To (Subscriber)
          </label>
          <Select
            value={draft.to}
            onChange={(e) => update("to", e.target.value)}
            options={endpointOptions}
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Message Type
          </label>
          <select
            value={draft.messageType}
            onChange={(e) => update("messageType", e.target.value)}
            className="flex h-10 w-full rounded-md border border-input bg-background px-3 py-2 text-sm focus:outline-hidden focus:ring-2 focus:ring-offset-0 focus:border-primary focus:ring-primary-200"
          >
            <option value="">All types</option>
            {messageTypeOptions.map((opt) => (
              <option key={opt.value} value={opt.value}>
                {opt.label}
              </option>
            ))}
          </select>
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Enqueued From
          </label>
          <Input
            type="datetime-local"
            value={draft.enqueuedFrom}
            onChange={(e) => update("enqueuedFrom", e.target.value)}
            onKeyDown={handleKeyDown}
          />
        </div>
        <div>
          <label className="block text-xs font-medium text-muted-foreground mb-1">
            Enqueued To
          </label>
          <Input
            type="datetime-local"
            value={draft.enqueuedTo}
            onChange={(e) => update("enqueuedTo", e.target.value)}
            onKeyDown={handleKeyDown}
          />
        </div>
      </div>
      <div className="flex gap-2 mt-3">
        <Button
          onClick={handleSearch}
          disabled={isLoading}
          size="sm"
          colorScheme="primary"
        >
          Search
        </Button>
        <Button
          onClick={handleReset}
          disabled={isLoading}
          size="sm"
          variant="outline"
          colorScheme="gray"
        >
          Reset
        </Button>
      </div>
    </div>
  );
}
