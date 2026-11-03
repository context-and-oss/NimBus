import * as React from "react";
import { Tooltip } from "components/ui/tooltip";
import * as api from "api-client";
import {
  EndpointStatus,
  getEndpointStatus,
} from "functions/endpoint.functions";
import DataTable, { ITableRow, ITableHeadCell } from "components/data-table";
import Page from "components/page";
import EndpointRowActions from "components/endpoint-list/endpoint-row-actions";
import EndpointStatusIcon from "components/endpoint-list/endpoint-status-icon";
import Cookies from "js-cookie";

type EndpointProps = {
  endpointIds?: string[];
  endpointStates?: api.EndpointStatus[];
};
type EndpointState = {
  endpointIds: string[];
  // Loaded endpoint statuses keyed by endpoint id. Acts as an accumulating
  // cache so checking/unchecking never refetches a status we already hold.
  statusById: Record<string, api.EndpointStatusCount>;
  loading: boolean;
  // The set of endpoint ids whose checkbox is ticked. Empty means "show all".
  checked: string[];
};

enum ITableData {
  name = "name",
  failed = "failed",
  deferred = "deferred",
  pending = "pending",
  lastUpdated = "lastUpdated",
  status = "status",
  subscriptionstatus = "subscription",
  actions = "actions",
}

export default class EndpointsList extends React.Component<
  EndpointProps,
  EndpointState
> {
  private client!: api.Client;
  private cookieName = "endpointFilters";

  constructor(props: EndpointProps) {
    super(props);

    const mappedProps = this.props.endpointStates?.map((x) =>
      this.mapMomentInEndpointStatus(x),
    );

    this.state = {
      endpointIds: this.props.endpointIds ?? [],
      statusById: this.indexById(mappedProps ?? []),
      loading: !mappedProps,
      checked: this.readPersistedFilter(),
    };

    this.handleCheck = this.handleCheck.bind(this);
  }

  // componentDidMount gets called once we're client-side
  async componentDidMount() {
    const persisted = this.readPersistedFilter();

    this.client = new api.Client(api.CookieAuth());
    // If we didn't rehydrate with endpoint states, call the api to get them.
    const endPointIds = this.props.endpointIds
      ? this.props.endpointIds
      : await this.client.getEndpointsAll();

    // Drop ids the catalog no longer knows about, otherwise a stale saved
    // filter keeps requesting ghost endpoints (and can hide every row).
    const known = new Set(endPointIds);
    const checked = persisted.filter((id) => known.has(id));
    if (checked.length !== persisted.length) {
      this.persistFilter(checked);
    }

    const filteredEndpointIds: Array<string> =
      checked.length > 0 ? checked : endPointIds;

    let endpoints: api.EndpointStatusCount[];
    try {
      endpoints =
        await this.client.postApiEndpointStatusCount(filteredEndpointIds);
    } catch {
      endpoints = filteredEndpointIds.map(
        (id) =>
          new api.EndpointStatusCount({
            endpointId: id,
            storageStatus: "unavailable",
          }),
      );
    }

    this.setState((prev) => ({
      endpointIds: endPointIds,
      checked,
      statusById: this.mergeStatuses(prev.statusById, endpoints),
      loading: false,
    }));
  }

  // The ticked endpoint ids survive navigation and reloads in a cookie.
  // An absent cookie means "show all".
  private readPersistedFilter(): string[] {
    const raw = Cookies.get(this.cookieName);
    if (!raw) {
      return [];
    }
    return raw.split(",").filter((id) => id.length > 0);
  }

  private persistFilter(checked: string[]): void {
    if (checked.length === 0) {
      Cookies.remove(this.cookieName);
      return;
    }
    Cookies.set(this.cookieName, checked.join(","), {
      expires: 365,
      sameSite: "lax",
    });
  }

  async handleCheck(endpointId: string, newState: boolean) {
    // Build the next checked set immutably — never push/pop `this.state`.
    const checked = newState
      ? this.state.checked.includes(endpointId)
        ? this.state.checked
        : [...this.state.checked, endpointId]
      : this.state.checked.filter((id) => id !== endpointId);

    // No ticked boxes means "show all"; otherwise show only the ticked ones.
    // Fetch only the statuses we don't already have cached — unchecking never
    // discards or refetches statuses that are still on screen.
    const visibleIds = checked.length > 0 ? checked : this.state.endpointIds;
    const statusById = await this.loadStatuses(
      visibleIds,
      this.state.statusById,
    );

    this.persistFilter(checked);
    this.setState({ checked, statusById });
  }

  stopLoading = (): void => {
    this.setState({ loading: false });
  };

  startLoading = (): void => {
    this.setState({ loading: true });
  };

  refreshEndpoint = async (endpointId: string): Promise<any> => {
    this.client = new api.Client(api.CookieAuth());

    const endpoint = await this.client.getEndpointStatusCountId(endpointId);

    this.setState((prev) => ({
      statusById: this.mergeStatuses(prev.statusById, [endpoint]),
      loading: false,
    }));
  };

  render() {
    return <Page title="Endpoints">{this.createTableNew()}</Page>;
  }

  mapMomentInEndpointStatus(endpointStatus: api.EndpointStatusCount) {
    // SSR supplies plain ISO JSON; API calls supply hydrated DTOs. Serialize DTO
    // dates to ISO before parsing so Moment never reparses its display string.
    return api.EndpointStatusCount.fromJS(endpointStatus.toJSON?.() ?? endpointStatus);
  }

  // Immutably merge freshly fetched statuses into an existing id->status map,
  // preserving the Moment fields already hydrated by the generated client.
  private mergeStatuses(
    existing: Record<string, api.EndpointStatusCount>,
    endpoints: api.EndpointStatusCount[],
  ): Record<string, api.EndpointStatusCount> {
    const next = { ...existing };
    for (const endpoint of endpoints) {
      const mapped = this.mapMomentInEndpointStatus(endpoint);
      if (mapped.endpointId) {
        next[mapped.endpointId] = mapped;
      }
    }
    return next;
  }

  private indexById(
    endpoints: api.EndpointStatusCount[],
  ): Record<string, api.EndpointStatusCount> {
    return this.mergeStatuses({}, endpoints);
  }

  // Ensure the given ids are present in the status map, fetching only the ones
  // we don't already hold. Returns the same map reference untouched when there
  // is nothing to fetch.
  private async loadStatuses(
    ids: string[],
    existing: Record<string, api.EndpointStatusCount>,
  ): Promise<Record<string, api.EndpointStatusCount>> {
    const missing = ids.filter((id) => !(id in existing));
    if (missing.length === 0) {
      return existing;
    }
    const endpoints = await this.client.postApiEndpointStatusCount(missing);
    return this.mergeStatuses(existing, endpoints);
  }

  createTableNew(): React.JSX.Element {
    // Derive the visible rows from the status cache: the checked endpoints, or
    // the whole list when nothing is checked. Rows are re-sorted by the table.
    const visibleIds =
      this.state.checked.length > 0
        ? this.state.checked
        : this.state.endpointIds;
    const tableData = visibleIds
      .map((id) => this.state.statusById[id])
      .filter(
        (status): status is api.EndpointStatusCount => status !== undefined,
      )
      .map((x) => this.mapEndpointStatusToTableRow(x));

    const headCells: ITableHeadCell[] = [
      { id: ITableData.name, label: "Name", numeric: false },
      { id: ITableData.failed, label: "Failed", numeric: true },
      { id: ITableData.deferred, label: "Deferred", numeric: true },
      { id: ITableData.pending, label: "Pending", numeric: true },
      { id: ITableData.lastUpdated, label: "Last updated", numeric: false },
      { id: ITableData.status, label: "Status", numeric: false },
      {
        id: ITableData.actions,
        label: "Actions",
        numeric: false,
        width: "15%",
      },
    ];

    return (
      <DataTable
        headCells={headCells}
        rows={tableData || []}
        noDataMessage="No endpoints available"
        isLoading={this.state.loading}
        orderBy={ITableData.name}
        styles={{ marginLeft: "-1rem", marginRight: "-1rem" }}
        dataRowsPerPage={20}
        count={tableData.length}
        endpointIds={this.state.endpointIds || []}
        checkedEndpointIds={this.state.checked}
        checked={this.handleCheck}
      />
    );
  }

  mapEndpointStatusToTableRow(
    endpointStatus: api.EndpointStatusCount,
  ): ITableRow {
    const endpointId = endpointStatus.endpointId || "";
    const failedEventsCount = endpointStatus.failedCount || 0;
    const deferredEventsCount = endpointStatus.deferredCount || 0;
    const pendingEventsCount = endpointStatus.pendingCount || 0;
    const lastUpdated = endpointStatus.eventTime?.fromNow() || "";
    const endpointStatusValue = getEndpointStatus(endpointStatus);

    return {
      id: endpointStatus.endpointId ?? "",
      route: `/Endpoints/Details/${endpointStatus.endpointId}`,
      data: new Map([
        [
          ITableData.name,
          {
            value: (
              <span className="text-delegate-blue-600 dark:text-delegate-blue-400 font-bold hover:underline">
                {endpointId}
              </span>
            ),
            searchValue: endpointId,
          },
        ],
        [
          ITableData.failed,
          {
            value: failedEventsCount,
            searchValue: failedEventsCount,
          },
        ],
        [
          ITableData.deferred,
          {
            value: deferredEventsCount,
            searchValue: deferredEventsCount,
          },
        ],
        [
          ITableData.pending,
          {
            value: pendingEventsCount,
            searchValue: pendingEventsCount,
          },
        ],
        [
          ITableData.lastUpdated,
          {
            value: lastUpdated,
            searchValue: lastUpdated,
          },
        ],
        [
          ITableData.status,
          {
            value: this.mapStatusToIcon(endpointStatusValue),
            searchValue: endpointStatusValue.toString(),
          },
        ],
        [
          ITableData.actions,
          {
            value: (
              <EndpointRowActions
                endpointId={endpointId}
                subscriptionStatus={endpointStatus.subscriptionStatus}
                failed={failedEventsCount}
                deferred={deferredEventsCount}
                pending={pendingEventsCount}
                storageAvailable={
                  endpointStatus.storageStatus !== "unavailable"
                }
                refreshEndpoint={this.refreshEndpoint}
                startLoading={this.startLoading}
                stopLoading={this.stopLoading}
              />
            ),
            searchValue: "Actions",
          },
        ],
      ]),
    };
  }

  mapStatusToIcon = (status: EndpointStatus): React.JSX.Element => (
    <Tooltip content={status} position="top">
      <EndpointStatusIcon status={status} />
    </Tooltip>
  );
}
