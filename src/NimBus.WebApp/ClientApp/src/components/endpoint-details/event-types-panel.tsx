import * as React from "react";
const { useEffect, useMemo, useState } = React;
import { Link } from "react-router-dom";
import * as api from "api-client";
import { errorMessage } from "api-client/problem";
import { cn } from "lib/utils";

interface EventTypesPanelProps {
  endpointId: string;
}

interface EvTypeItem {
  eventType: api.EventType;
  partners: string[];
}

const MagIcon = () => (
  <svg width="13" height="13" viewBox="0 0 16 16" fill="none">
    <circle cx="7" cy="7" r="4.5" stroke="currentColor" strokeWidth="1.5" />
    <path d="M10.5 10.5L13 13" stroke="currentColor" strokeWidth="1.5" strokeLinecap="round" />
  </svg>
);

const filterItems = (items: EvTypeItem[], filter: string): EvTypeItem[] => {
  const q = filter.trim().toLowerCase();
  if (!q) return items;
  return items.filter((i) => {
    const name = i.eventType.name?.toLowerCase() ?? "";
    const ns = i.eventType.namespace?.toLowerCase() ?? "";
    return name.includes(q) || ns.includes(q);
  });
};

const buildItems = (
  data: api.Anonymous,
  groupings: api.EventTypeGrouping[] | undefined,
  pickPartners: (d: api.EventTypeDetails) => string[] | undefined,
  selfEndpoint: string,
): EvTypeItem[] => {
  const detailsById = new Map<string, api.EventTypeDetails>();
  (data.eventTypeDetails ?? []).forEach((d) => {
    if (d.eventType?.id) detailsById.set(d.eventType.id, d);
  });
  const items: EvTypeItem[] = [];
  (groupings ?? []).forEach((g) => {
    (g.events ?? []).forEach((et) => {
      const details = et.id ? detailsById.get(et.id) : undefined;
      const partners = (details ? pickPartners(details) ?? [] : []).filter(
        (p) => p && p !== selfEndpoint,
      );
      items.push({ eventType: et, partners });
    });
  });
  return items;
};

const EventTypesPanel: React.FC<EventTypesPanelProps> = ({ endpointId }) => {
  const [data, setData] = useState<api.Anonymous | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);
  const [producesFilter, setProducesFilter] = useState("");
  const [consumesFilter, setConsumesFilter] = useState("");

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(null);
    const client = new api.Client(api.CookieAuth());
    client
      .getEventtypesByEndpointId(endpointId)
      .then((res) => {
        if (cancelled) return;
        setData(res);
      })
      .catch((e) => {
        if (cancelled) return;
        setError(errorMessage(e));
      })
      .finally(() => {
        if (cancelled) return;
        setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [endpointId]);

  const producesItems = useMemo(
    () =>
      data
        ? buildItems(data, data.produces, (d) => d.consumers, endpointId)
        : [],
    [data, endpointId],
  );
  const consumesItems = useMemo(
    () =>
      data
        ? buildItems(data, data.consumes, (d) => d.producers, endpointId)
        : [],
    [data, endpointId],
  );

  const producesFiltered = useMemo(
    () => filterItems(producesItems, producesFilter),
    [producesItems, producesFilter],
  );
  const consumesFiltered = useMemo(
    () => filterItems(consumesItems, consumesFilter),
    [consumesItems, consumesFilter],
  );

  if (loading) {
    return (
      <div className="p-6 text-sm text-muted-foreground">
        Loading event types…
      </div>
    );
  }
  if (error) {
    return (
      <div className="p-6 text-sm text-status-danger">
        Failed to load event types: {error}
      </div>
    );
  }
  if (!data || (producesItems.length === 0 && consumesItems.length === 0)) {
    return (
      <div className="p-6 rounded-nb-md border border-border bg-surface text-sm text-muted-foreground">
        This endpoint neither publishes nor subscribes to any event types.
      </div>
    );
  }

  return (
    <div className="grid grid-cols-1 lg:grid-cols-2 gap-3.5 w-full">
      <PubSubPanel
        variant="produces"
        count={producesItems.length}
        filter={producesFilter}
        onFilterChange={setProducesFilter}
        items={producesFiltered}
        totalItems={producesItems.length}
        partnerPrefix=""
      />
      <PubSubPanel
        variant="consumes"
        count={consumesItems.length}
        filter={consumesFilter}
        onFilterChange={setConsumesFilter}
        items={consumesFiltered}
        totalItems={consumesItems.length}
        partnerPrefix="from "
      />
    </div>
  );
};

// ---------- side panel ----------

interface PubSubPanelProps {
  variant: "produces" | "consumes";
  count: number;
  totalItems: number;
  filter: string;
  onFilterChange: (v: string) => void;
  items: EvTypeItem[];
  partnerPrefix: string;
}

const PubSubPanel: React.FC<PubSubPanelProps> = ({
  variant,
  count,
  totalItems,
  filter,
  onFilterChange,
  items,
  partnerPrefix,
}) => {
  const isProduces = variant === "produces";
  return (
    <div
      className={cn(
        "bg-surface border border-border rounded-nb-md border-l-4 p-4 md:px-5 md:py-4 flex flex-col gap-2.5",
        isProduces ? "border-l-status-success" : "border-l-status-info",
      )}
    >
      <div className="flex items-center gap-2.5">
        <h3 className="m-0 text-[15px] font-bold tracking-tight inline-flex items-center gap-2">
          <span
            className={cn(
              "font-bold",
              isProduces ? "text-status-success" : "text-status-info",
            )}
          >
            {isProduces ? "↗" : "↘"}
          </span>
          {isProduces ? "Produces" : "Consumes"}
          <span
            className={cn(
              "inline-flex items-center justify-center text-[11px] font-bold px-2 py-px rounded-full font-mono",
              isProduces
                ? "bg-status-success-50 text-status-success-ink"
                : "bg-status-info-50 text-status-info-ink",
            )}
          >
            {count}
          </span>
        </h3>
      </div>

      <div className="relative">
        <span className="absolute left-2.5 top-1/2 -translate-y-1/2 text-ink-3">
          <MagIcon />
        </span>
        <input
          value={filter}
          onChange={(e) => onFilterChange(e.target.value)}
          placeholder={
            isProduces
              ? "Filter event types this endpoint publishes…"
              : "Filter event types this endpoint subscribes to…"
          }
          className="w-full font-sans text-xs text-ink bg-canvas border border-border rounded-md py-1.5 pl-7 pr-3 placeholder:text-ink-3 focus:outline-hidden focus:border-primary"
        />
      </div>

      {items.length === 0 ? (
        <div className="py-6 text-center text-xs text-ink-3 italic">
          {totalItems === 0
            ? isProduces
              ? "This endpoint does not publish any event types."
              : "This endpoint does not subscribe to any event types."
            : "No event types match the filter."}
        </div>
      ) : (
        <div className="flex flex-col gap-1.5 mt-0.5">
          {items.map((item) => (
            <EvTypeRow
              key={item.eventType.id ?? item.eventType.name ?? ""}
              variant={variant}
              item={item}
              partnerPrefix={partnerPrefix}
            />
          ))}
        </div>
      )}
    </div>
  );
};

// ---------- single event-type row ----------

interface EvTypeRowProps {
  variant: "produces" | "consumes";
  item: EvTypeItem;
  partnerPrefix: string;
}

const EvTypeRow: React.FC<EvTypeRowProps> = ({ variant, item, partnerPrefix }) => {
  const isProduces = variant === "produces";
  const eventTypeId = item.eventType.id ?? item.eventType.name ?? "";

  return (
    <div
      className={cn(
        "grid gap-2.5 items-center bg-canvas border border-border rounded-md py-2.5 px-3 transition-[border-color,box-shadow] duration-100",
        "grid-cols-[12px_1fr_auto] hover:border-primary hover:shadow-nb-sm",
      )}
    >
      <span
        className={cn(
          "w-2 h-2 rounded-full",
          isProduces ? "bg-status-success" : "bg-status-info",
        )}
      />
      <Link
        to={`/EventTypes/Details/${encodeURIComponent(eventTypeId)}`}
        className="flex flex-col gap-0.5 min-w-0 overflow-hidden text-ink no-underline"
        title={item.eventType.id ?? item.eventType.name ?? ""}
      >
        <span className="font-bold text-[13px] truncate">
          {item.eventType.name}
        </span>
        {item.eventType.namespace && (
          <span className="font-mono text-[10px] text-ink-3 font-medium tracking-wide truncate">
            {item.eventType.namespace}
          </span>
        )}
      </Link>
      <div
        className={cn(
          "flex flex-col gap-0.5 items-end font-mono text-[10.5px] max-w-[200px] leading-tight",
          isProduces ? "text-status-info" : "text-status-success",
        )}
      >
        {item.partners.length === 0 ? (
          <span className="text-ink-3 italic font-sans">
            {isProduces ? "no consumers" : "no producers"}
          </span>
        ) : (
          item.partners.map((p) => (
            <span key={p} className="inline-flex items-center gap-1">
              {partnerPrefix && (
                <span className="text-ink-3 font-sans">{partnerPrefix}</span>
              )}
              <Link
                to={`/Endpoints/Details/${encodeURIComponent(p)}`}
                className="font-semibold hover:underline text-inherit no-underline"
              >
                {p}
              </Link>
            </span>
          ))
        )}
      </div>
    </div>
  );
};

export default EventTypesPanel;
