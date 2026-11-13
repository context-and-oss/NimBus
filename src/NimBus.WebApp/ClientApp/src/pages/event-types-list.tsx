import { useEffect, useMemo, useState } from "react";
import { Link } from "react-router-dom";
import * as api from "api-client";
import Page from "components/page";
import { Spinner } from "components/ui/spinner";
import { Badge } from "components/ui/badge";
import { NamespacePill } from "components/ui/namespace-pill";
import { EmptyState } from "components/ui/empty-state";
import DataTable, { ITableRow, ITableHeadCell } from "components/data-table";
import EventTypeSearchToolbar from "components/event-types/event-type-search-toolbar";
import { routingLabels } from "lib/routing-keys";
import { useUrlFilters } from "hooks/use-url-filters";
import { usePlatformPackage } from "hooks/app-status";

enum TableColumns {
  name = "name",
  namespace = "namespace",
  description = "description",
  producers = "producers",
  consumers = "consumers",
}

// URL-driven filter shape. The filters are stored in query params so that
// pressing Back (e.g. after drilling into an event type) restores the same
// filter state. Declared as a
// closed `type` so it satisfies the index-signature constraint of `useUrlFilters<T>`.
type EventTypesFilter = {
  searchTerm: string;
  selectedNamespace: string;
  selectedEndpoint: string;
};

const DEFAULT_EVENT_TYPES_FILTER: EventTypesFilter = {
  searchTerm: "",
  selectedNamespace: "",
  selectedEndpoint: "",
};

const EventTypesList: React.FC = () => {
  const { applied, setFiltersWithoutHistory } = useUrlFilters<EventTypesFilter>(
    DEFAULT_EVENT_TYPES_FILTER,
  );

  const searchTerm = applied.searchTerm;
  const selectedNamespace = applied.selectedNamespace;
  const selectedEndpoint = applied.selectedEndpoint;

  const [eventTypes, setEventTypes] = useState<api.EventType[]>([]);
  const [loading, setLoading] = useState(true);

  useEffect(() => {
    const fetchData = async () => {
      const client = new api.Client(api.CookieAuth());
      try {
        const types = await client.getEventTypes();
        setEventTypes(types.filter((et) => et.id));
      } catch (error) {
        console.error("Failed to fetch event types:", error);
      } finally {
        setLoading(false);
      }
    };
    fetchData();
  }, []);

  const UNCATEGORIZED = "Uncategorized";

  const namespaces = useMemo(() => {
    const nsSet = new Set<string>();
    let hasUncategorized = false;
    eventTypes.forEach((et) => {
      if (et.namespace) {
        nsSet.add(et.namespace);
      } else {
        hasUncategorized = true;
      }
    });
    const sorted = Array.from(nsSet).sort();
    if (hasUncategorized) {
      sorted.push(UNCATEGORIZED);
    }
    return sorted;
  }, [eventTypes]);

  const endpoints = useMemo(() => {
    const epSet = new Set<string>();
    eventTypes.forEach((et) => {
      et.producers?.forEach((p) => epSet.add(p));
      et.consumers?.forEach((c) => epSet.add(c));
    });
    return Array.from(epSet).sort();
  }, [eventTypes]);

  const filteredEventTypes = useMemo(() => {
    return eventTypes.filter((et) => {
      const matchesSearch =
        !searchTerm ||
        et.name?.toLowerCase().includes(searchTerm.toLowerCase()) ||
        et.namespace?.toLowerCase().includes(searchTerm.toLowerCase()) ||
        et.description?.toLowerCase().includes(searchTerm.toLowerCase());

      const matchesNamespace =
        !selectedNamespace ||
        et.namespace === selectedNamespace ||
        (selectedNamespace === UNCATEGORIZED && !et.namespace);

      const matchesEndpoint =
        !selectedEndpoint ||
        et.producers?.includes(selectedEndpoint) ||
        et.consumers?.includes(selectedEndpoint);

      return matchesSearch && matchesNamespace && matchesEndpoint;
    });
  }, [eventTypes, searchTerm, selectedNamespace, selectedEndpoint]);

  const tableRows = useMemo((): ITableRow[] => {
    return filteredEventTypes.map((et) => ({
      id: et.id,
      route: `/EventTypes/Details/${et.id}`,
      data: new Map([
        [
          TableColumns.name,
          {
            value: (
              <span className="text-status-info dark:text-blue-300 font-bold">
                {et.name}
              </span>
            ),
            searchValue: et.name || "",
          },
        ],
        [
          TableColumns.namespace,
          {
            value: (
              <NamespacePill size="sm">
                {et.namespace || UNCATEGORIZED}
              </NamespacePill>
            ),
            searchValue: et.namespace || UNCATEGORIZED,
          },
        ],
        [
          TableColumns.producers,
          {
            value: (
              <EndpointBadges endpoints={et.producers} variant="success" />
            ),
            searchValue: et.producers?.join(" ") || "",
          },
        ],
        [
          TableColumns.consumers,
          {
            value: (
              <EndpointBadges
                endpoints={et.consumers}
                variant="info"
                notes={routingLabels(et.consumerRouting)}
              />
            ),
            searchValue: et.consumers?.join(" ") || "",
          },
        ],
        [
          TableColumns.description,
          {
            value: (
              <span className="text-sm whitespace-normal line-clamp-2">
                {et.description || "No description"}
              </span>
            ),
            searchValue: et.description || "",
          },
        ],
      ]),
    }));
  }, [filteredEventTypes]);

  const headCells: ITableHeadCell[] = [
    { id: TableColumns.name, label: "Name", numeric: false },
    { id: TableColumns.namespace, label: "Namespace", numeric: false },
    { id: TableColumns.producers, label: "Producers", numeric: false },
    { id: TableColumns.consumers, label: "Consumers", numeric: false },
    { id: TableColumns.description, label: "Description", numeric: false },
  ];

  const eventTypeCount = eventTypes.length;
  const namespaceCount = namespaces.length;
  // The catalog package these contracts come from (e.g. "EET.Platform 1.0.1").
  const platformPackage = usePlatformPackage();

  if (loading) {
    return (
      <Page title="Event Types" subtitle="Contracts published across the bus">
        <div className="flex items-center justify-center w-full h-[200px]">
          <Spinner size="xl" color="primary" />
        </div>
      </Page>
    );
  }

  // Live-filter inputs (no Search button): every change replaces the current URL
  // entry rather than adding a new one, so history isn't polluted with one entry
  // per keystroke. Browser Back from a detail page still returns to the URL with
  // filters applied.
  const setSearchTerm = (next: string) =>
    setFiltersWithoutHistory({ ...applied, searchTerm: next });
  const setSelectedNamespace = (next: string) =>
    setFiltersWithoutHistory({ ...applied, selectedNamespace: next });
  const setSelectedEndpoint = (next: string) =>
    setFiltersWithoutHistory({ ...applied, selectedEndpoint: next });
  const hasActiveFilters =
    searchTerm.length > 0 ||
    selectedNamespace.length > 0 ||
    selectedEndpoint.length > 0;
  const counts = `${eventTypeCount} contract${eventTypeCount === 1 ? "" : "s"} across ${namespaceCount} namespace${namespaceCount === 1 ? "" : "s"}`;
  const subtitle = platformPackage ? `${counts} · ${platformPackage}` : counts;

  return (
    <Page title="Event Types" subtitle={subtitle}>
      <div className="w-full">
        <EventTypeSearchToolbar
          searchTerm={searchTerm}
          onSearchChange={setSearchTerm}
          selectedNamespace={selectedNamespace}
          onNamespaceChange={setSelectedNamespace}
          namespaces={namespaces}
          selectedEndpoint={selectedEndpoint}
          onEndpointChange={setSelectedEndpoint}
          endpoints={endpoints}
        />

        {filteredEventTypes.length === 0 ? (
          <EmptyState
            icon="◌"
            title={
              hasActiveFilters
                ? "No event types match your filters"
                : "No event types registered yet"
            }
            description={
              hasActiveFilters
                ? "Try a different search term or clear the namespace/endpoint filter."
                : "Event types will appear here once endpoints declare their published and consumed contracts."
            }
            action={
              hasActiveFilters && (
                <button
                  type="button"
                  onClick={() => {
                    setSearchTerm("");
                    setSelectedNamespace("");
                    setSelectedEndpoint("");
                  }}
                  className="text-primary-600 hover:text-primary text-[13px] font-semibold underline-offset-2 hover:underline"
                >
                  Clear all filters
                </button>
              )
            }
          />
        ) : (
          <DataTable
            headCells={headCells}
            rows={tableRows}
            noDataMessage="No event types available"
            isLoading={false}
            orderBy={TableColumns.name}
            dataRowsPerPage={20}
            count={tableRows.length}
            withToolbar={false}
          />
        )}
      </div>
    </Page>
  );
};

interface EndpointBadgesProps {
  endpoints?: string[];
  variant: "success" | "info";
  /** Spec 045: a routing label per endpoint name ("keys 01, 02", "unmatched"). */
  notes?: Record<string, string>;
}

const EndpointBadges = ({ endpoints, variant, notes }: EndpointBadgesProps) => {
  if (!endpoints?.length) {
    return <span className="text-muted-foreground">—</span>;
  }

  return (
    <span className="flex flex-wrap gap-1 whitespace-normal">
      {endpoints.map((endpoint) => (
        <Link
          key={endpoint}
          to={`/Endpoints/Details/${encodeURIComponent(endpoint)}`}
          className="no-underline"
          title={notes?.[endpoint] ? `${endpoint} (${notes[endpoint]})` : endpoint}
        >
          <Badge
            variant={variant}
            size="sm"
            withDot={false}
            className="rounded-nb-sm font-mono font-medium"
          >
            {endpoint}
            {notes?.[endpoint] && (
              <span className="ml-1 font-normal opacity-75">
                · {notes[endpoint]}
              </span>
            )}
          </Badge>
        </Link>
      ))}
    </span>
  );
};

export default EventTypesList;
