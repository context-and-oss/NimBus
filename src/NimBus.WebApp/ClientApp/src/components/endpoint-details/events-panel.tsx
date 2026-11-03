import * as React from "react";
import * as api from "api-client";
import moment from "moment";
import DataTable, {
  ITableHeadAction,
  ITableRow,
  ITableBodyAction,
  ITableHeadCell,
} from "components/data-table";
import ColumnChooser from "components/data-table/column-chooser";
import { useNavigate, useParams } from "react-router-dom";
import {
  formatMoment,
  formatResolutionStatus,
} from "functions/endpoint.functions";
import EventFiltering from "./filter/event-filtering";
import type { AdvancedFilters } from "./filter/advanced-filters";
import AdvancedFiltersPopover, {
  AdvancedFilterChips,
} from "./filter/advanced-filters-popover";
import FilterContext, { IFilterContext } from "./filter/filtering-context";
import TruncatedGuid from "components/common/truncated-guid";
import ErrorGroupedView from "./error-grouped-view";
import { useUrlFilters } from "hooks/use-url-filters";
import { Badge } from "components/ui/badge";
import { Tooltip } from "components/ui/tooltip";
import { Checkbox } from "components/ui/checkbox";
import { StatTile, StatRow } from "components/ui/stat-tile";
import { EmptyState } from "components/ui/empty-state";
import { cn } from "lib/utils";
import ReportPopover from "./report-popover";
import { notifyWithUndo, notifyError } from "functions/notifications.functions";
import { describeCommandError } from "functions/operator-command.functions";
import { failureBacklog } from "functions/failed-messages.functions";
import { reportedCellState } from "functions/reported.functions";
import { useTicketLinkTemplate } from "hooks/app-status";
import { useCurrentUser } from "hooks/use-current-user";

const isHandoffEvent = (event: api.Event): boolean =>
  event.resolutionStatus === api.ResolutionStatus.Pending &&
  (event.pendingSubStatus ?? "").toLowerCase() === "handoff";

// Map a ResolutionStatus onto the design system's badge variant set.
// Status-only — coral is reserved for action / focus (design rec §02).
function statusToBadgeVariant(
  status: string | api.ResolutionStatus | undefined,
):
  | "completed"
  | "failed"
  | "deferred"
  | "deadlettered"
  | "skipped"
  | "unsupported"
  | "pending"
  | "default" {
  switch (status) {
    case api.ResolutionStatus.Completed:
      return "completed";
    case api.ResolutionStatus.Failed:
      return "failed";
    case api.ResolutionStatus.Deferred:
      return "deferred";
    case api.ResolutionStatus.DeadLettered:
      return "deadlettered";
    case api.ResolutionStatus.Skipped:
      return "skipped";
    case api.ResolutionStatus.Unsupported:
      return "unsupported";
    case api.ResolutionStatus.Pending:
      return "pending";
    default:
      return "default";
  }
}

interface EventsPanelProps {
  endpointId: string;
}

interface SessionState {
  deferredCount: number;
}

// Values behind the Failed (Failed + DeadLettered + Unsupported), Deferred and
// Pending summary tiles.
interface TileCounts {
  failed: number;
  deferred: number;
  pending: number;
}

const ACTIONABLE_STATUSES = [
  api.ResolutionStatus.Failed,
  api.ResolutionStatus.DeadLettered,
  api.ResolutionStatus.Unsupported,
  api.ResolutionStatus.Deferred,
];

// Cap on the pages the hide-reported toggle may auto-append to fill a client
// page. Bounds memory and storage load on endpoints whose recent history is
// entirely reported; past the cap the operator pages on explicitly via Next.
const MAX_HIDE_REPORTED_REFILL_PAGES = 3;

// One-click triage set applied by the "Failed" button: the terminal failed
// statuses an operator acts on. Deliberately narrower than ACTIONABLE_STATUSES
// (no Deferred — those retry on their own).
export const FAILED_STATUS_SET = [
  api.ResolutionStatus.Failed,
  api.ResolutionStatus.DeadLettered,
  api.ResolutionStatus.Unsupported,
];

// URL sentinel meaning "user explicitly wants no status filter". The default
// is already "all statuses", but removing the last status chip still writes
// `?status=*` so the intent survives navigation explicitly rather than
// depending on param absence; buildEventFilterFromParams translates it back to
// no filter before hitting the API, and the chip combobox treats it as zero
// chips.
const STATUS_ALL_SENTINEL = "*";

function isAllStatusesSentinel(status: string[]): boolean {
  return status.length === 1 && status[0] === STATUS_ALL_SENTINEL;
}

// URL-driven filter shape. Basic fields come from the filter bar; the advanced
// fields (Updated/Added ranges + payload) come from the Advanced-filters
// popover and round-trip through the URL like everything else, so a bookmarked
// or Back-navigated URL restores them. The default `status` is empty — no
// status predicate — so the page opens showing the *latest* events of every
// status; operators most often want to see recent traffic first, and the
// one-click "Failed" button applies the triage set when needed. Declared as a
// closed `type` so it satisfies the index-signature constraint of
// `useUrlFilters<T>`.
type EndpointFilterParams = {
  status: string[];
  eventTypeId: string[];
  eventId: string;
  sessionId: string;
  updatedFrom: string;
  updatedTo: string;
  addedFrom: string;
  addedTo: string;
  payload: string;
  viewMode: string;
  maxResults: string;
};

export const DEFAULT_ENDPOINT_FILTER_PARAMS: EndpointFilterParams = {
  status: [],
  eventTypeId: [],
  eventId: "",
  sessionId: "",
  updatedFrom: "",
  updatedTo: "",
  addedFrom: "",
  addedTo: "",
  payload: "",
  viewMode: "list",
  maxResults: "100",
};

// Canonical column set for the events table, in render order. This single list
// drives both the table header (headCells) and the column chooser. The identity
// column (Event Id) is locked — always visible — so a row is never reduced to
// anonymous cells. Exported so the chooser invariants can be unit-tested
// without rendering.
export type EventColumn = ITableHeadCell & { locked?: boolean };

export const EVENT_COLUMNS: EventColumn[] = [
  {
    id: "eventId",
    label: "Event Id",
    numeric: false,
    width: "10%",
    locked: true,
  },
  {
    id: "status",
    label: "Status",
    numeric: false,
    // Wide enough for the status badge plus the "Awaiting external" /
    // "N blocked" chips that sit beside it.
    width: "15%",
    info: "A failed message shows how many deferred messages are blocked behind it in its session",
  },
  { id: "sessionId", label: "Session Id", numeric: false, width: "10%" },
  { id: "eventTypeId", label: "Event Type", numeric: false, width: "19%" },
  // A small count — fixed narrow width, just enough for the header label.
  { id: "resubmitCount", label: "Resubmits", numeric: true, width: 110 },
  {
    id: "reported",
    label: "Reported",
    numeric: false,
    width: 130,
    info: "Whether this event has been reported, with an optional ticket reference",
  },
  { id: "updated", label: "Updated", numeric: false, width: "12%" },
  { id: "added", label: "Added", numeric: false, width: "12%" },
];

// Visible columns after applying the operator's hidden-column choices, in
// canonical order. Locked columns are always visible. DataTable looks each body
// cell up by headCell id (row.data.get(id)), so filtering the headCells alone
// keeps header and body aligned.
export function getVisibleColumns(hidden: Set<string>): EventColumn[] {
  return EVENT_COLUMNS.filter((c) => c.locked || !hidden.has(c.id));
}

// Hidden-column choices persist across endpoints and sessions: it's a personal
// display preference, not a shareable filter (those live in the URL). Stored as
// a JSON array of column ids; locked ids are never written. Exported for tests.
export const HIDDEN_COLUMNS_STORAGE_KEY = "endpoint-events:hidden-columns";

export function loadHiddenColumns(): Set<string> {
  try {
    const raw = window.localStorage?.getItem(HIDDEN_COLUMNS_STORAGE_KEY);
    if (!raw) return new Set();
    const parsed = JSON.parse(raw) as unknown;
    return Array.isArray(parsed)
      ? new Set(parsed.filter((x): x is string => typeof x === "string"))
      : new Set();
  } catch {
    return new Set();
  }
}

export function saveHiddenColumns(hidden: Set<string>): void {
  try {
    window.localStorage?.setItem(
      HIDDEN_COLUMNS_STORAGE_KEY,
      JSON.stringify([...hidden]),
    );
  } catch {
    // Storage can be unavailable in hardened browsers or test runners.
  }
}

const parseLocalDateTime = (value: string): moment.Moment | undefined =>
  value ? moment(value, "YYYY-MM-DDTHH:mm") : undefined;

const formatLocalDateTime = (value: moment.Moment | undefined): string =>
  value ? value.format("YYYY-MM-DDTHH:mm") : "";

function buildEventFilterFromParams(
  params: EndpointFilterParams,
  endpointId: string,
): api.EventFilter {
  const filter = new api.EventFilter();
  filter.endpointId = endpointId;
  // Sentinel ["*"] means "no status filter" — leave resolutionStatus empty.
  filter.resolutionStatus = isAllStatusesSentinel(params.status)
    ? []
    : (params.status as api.ResolutionStatus[]);
  if (params.eventTypeId.length) filter.eventTypeId = [...params.eventTypeId];
  if (params.eventId) filter.eventId = params.eventId;
  if (params.sessionId) filter.sessionId = params.sessionId;
  // "Updated" → UpdatedAt (note the contract's misspelled `updateAtFrom`);
  // "Added" → EnqueuedTimeUtc — the same fields the old accordion sub-filters
  // wrote to the FilterContext draft.
  if (params.updatedFrom)
    filter.updateAtFrom = parseLocalDateTime(params.updatedFrom);
  if (params.updatedTo)
    filter.updatedAtTo = parseLocalDateTime(params.updatedTo);
  if (params.addedFrom)
    filter.enqueuedAtFrom = parseLocalDateTime(params.addedFrom);
  if (params.addedTo) filter.enqueuedAtTo = parseLocalDateTime(params.addedTo);
  const payload = params.payload.trim();
  if (payload) filter.payload = payload;
  return filter;
}

function paramsFromEventFilter(
  filter: api.EventFilter,
  current: EndpointFilterParams,
): EndpointFilterParams {
  // Search committed an empty status array → user means "all statuses". Write
  // the sentinel so the URL preserves intent across navigation.
  const rawStatus = (filter.resolutionStatus ?? []) as string[];
  return {
    status: rawStatus.length === 0 ? [STATUS_ALL_SENTINEL] : rawStatus,
    eventTypeId: (filter.eventTypeId ?? []) as string[],
    eventId: filter.eventId ?? "",
    sessionId: filter.sessionId ?? "",
    updatedFrom: formatLocalDateTime(filter.updateAtFrom),
    updatedTo: formatLocalDateTime(filter.updatedAtTo),
    addedFrom: formatLocalDateTime(filter.enqueuedAtFrom),
    addedTo: formatLocalDateTime(filter.enqueuedAtTo),
    payload: filter.payload ?? "",
    viewMode: current.viewMode,
    maxResults: current.maxResults,
  };
}

const EventsPanel = (props: EventsPanelProps) => {
  // Stable client instance — constructing per render churns an object every
  // keystroke/render. Never add it to a dependency array.
  const client = React.useMemo(() => new api.Client(api.CookieAuth()), []);
  const params = useParams();
  const navigate = useNavigate();
  const endpointId = props.endpointId || params.id!;

  // Filters live in the URL (shareable, Back/forward-safe) and are mirrored to
  // sessionStorage per endpoint, so navigating away and back through the
  // sidebar restores the operator's last-used filters. URL params always win
  // over the stored copy.
  const { applied, applyFilters, resetFilters } =
    useUrlFilters<EndpointFilterParams>(DEFAULT_ENDPOINT_FILTER_PARAMS, {
      persistKey: `endpoint-events-filter:${endpointId}`,
    });

  // Bumped on every explicit Search click. The fetch effect is keyed on the URL
  // filter (`applied`), which does not change when Search is clicked without
  // altering any filter — so without this nonce, Search would be a no-op when
  // nothing changed. Including it in the fetch deps makes Search always refresh.
  const [searchNonce, setSearchNonce] = React.useState(0);

  const [events, setEvents] = React.useState<api.Event[]>([]);
  const [sessions, setSessions] = React.useState<Record<string, SessionState>>(
    {},
  );
  const [continuationToken, setContinuationToken] = React.useState<
    string | undefined
  >();
  const [isLoading, setIsLoading] = React.useState<boolean>(true);
  // Endpoint-wide Failed/Deferred/Pending totals; undefined until read (or when
  // the read fails), in which case the tiles fall back to the loaded page.
  const [endpointTotals, setEndpointTotals] = React.useState<TileCounts>();
  // Same ordering guard as fetchTicket, for the totals request.
  const totalsTicket = React.useRef(0);
  const [searchError, setSearchError] = React.useState<string>();
  const [currentFilter, setCurrentFilter] = React.useState<
    api.EventFilter | undefined
  >();
  // Client-side toggle so watchers can hide events already reported.
  const [hideReported, setHideReported] = React.useState<boolean>(false);
  // "Reported" column state. ticketLinkTemplate is a configured deep-link
  // template ("…/{ticket}"); undefined → ticket chips render as plain text.
  // currentUser names the operator on optimistic marks so the who/when tooltip
  // is populated before the next refetch.
  const ticketLinkTemplate = useTicketLinkTemplate();
  const { user: currentUser } = useCurrentUser();
  const [reportTarget, setReportTarget] = React.useState<{
    event: api.Event;
    anchor: HTMLElement;
  } | null>(null);
  // Per-event chain of in-flight report writes. Mark→Undo fires two requests
  // back-to-back; without serialization their completion order is undefined and
  // storage could finish "reported" while the UI shows unreported.
  const reportWriteChains = React.useRef(new Map<string, Promise<void>>());
  // Per-event operation revision: a FAILED older write must not roll the UI
  // back over a newer operation's optimistic state (e.g. Mark fails slowly
  // after the user already did Undo → Mark again).
  const reportRevisions = React.useRef(new Map<string, number>());
  // Last state the SERVER is known to hold per event: seeded from the fetched
  // event on first touch, advanced only on write success. Failure rollback
  // restores this — never another operation's unconfirmed optimistic snapshot
  // (Mark ok → Undo fails → Mark-again fails must end reported, not unreported).
  type ReportState = {
    isReported?: boolean;
    reportedBy?: string;
    reportedAtUtc?: moment.Moment;
    ticketId?: string;
  };
  const confirmedReportStates = React.useRef(new Map<string, ReportState>());
  // Guards nextPage against overlapping auto-refill calls (see the
  // hide-reported effect below).
  const isPagingRef = React.useRef(false);
  // Bumped on every full fetch (mount, Search, Reset, filter/URL change). A late
  // out-of-order response — including its background session-status batch — only
  // commits while its ticket is still current, so a slow older fetch can't
  // overwrite a newer one. Pagination continues the current generation (see
  // nextPage), so it reads the ticket without bumping it.
  const fetchTicket = React.useRef(0);
  // Generation that the CURRENT continuationToken/currentFilter belong to.
  // nextPage must extend this generation, not whatever fetch happens to be the
  // newest — a page requested mid-refresh would otherwise run the OLD query
  // under the NEW ticket and get appended to the new result set.
  const tokenGeneration = React.useRef(0);
  // Columns the operator has chosen to hide via the column chooser (persisted;
  // locked columns are never included).
  const [hiddenCols, setHiddenCols] =
    React.useState<Set<string>>(loadHiddenColumns);

  const maxResults = Number(applied.maxResults) || 100;
  const viewMode = (applied.viewMode === "grouped" ? "grouped" : "list") as
    | "list"
    | "grouped";

  // EventFilter (draft) is derived from URL on mount and on URL change. Sub-filters
  // mutate this via the FilterContext as the user types; URL only updates on Search.
  const [eventFilter, setEventFilter] = React.useState<api.EventFilter>(() =>
    buildEventFilterFromParams(applied, endpointId),
  );

  // When the URL changes (Back/forward, Search, Reset, bookmark load) reset the
  // draft filter to match. Sub-filter components are also keyed on URL state so
  // their internal input state re-seeds from the URL-derived initialValue props.
  React.useEffect(() => {
    setEventFilter(buildEventFilterFromParams(applied, endpointId));
  }, [applied, endpointId]);

  const context: IFilterContext = {
    filterContext: eventFilter,
    setProjectContext: setEventFilter,
  };

  // NOTE: the defaults are no longer materialised into the URL on first mount.
  // A clean URL re-derives the same default filter deterministically, and the
  // hook's sessionStorage hydration (persistKey above) now owns the "restore
  // last-used filters on a clean URL" concern — an explicit default write here
  // would clobber it.

  // Re-fetch whenever the applied (URL) filter changes. Covers initial mount,
  // Search, Reset, browser Back/forward, and direct bookmark loads.
  React.useEffect(() => {
    if (!endpointId) return;
    setSessions({});
    fetchEvents(buildEventFilterFromParams(applied, endpointId));
    void fetchEndpointTotals();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [applied, endpointId, searchNonce]);

  // Endpoint-wide totals for the summary tiles. Refreshed with every result-set
  // load, so a Search, Reset or row action updates them too.
  const fetchEndpointTotals = async (): Promise<void> => {
    const ticket = ++totalsTicket.current;
    let totals: TileCounts | undefined;
    try {
      const count = await client.getEndpointStatusCountId(endpointId);
      if (count) {
        totals = {
          failed: failureBacklog([count]),
          deferred: count.deferredCount ?? 0,
          pending: count.pendingCount ?? 0,
        };
      }
    } catch {
      // Unreadable totals leave the tiles on the loaded page's counts.
    }
    if (ticket === totalsTicket.current) {
      setEndpointTotals(totals);
    }
  };

  const isActionableStatus = (status: string | undefined): boolean => {
    if (!status) return false;
    return ACTIONABLE_STATUSES.includes(status as api.ResolutionStatus);
  };

  // Only a failed message blocks its session, so only failed rows show how
  // many deferred messages are queued behind them.
  const isFailedStatus = (status: string | undefined): boolean => {
    if (!status) return false;
    return FAILED_STATUS_SET.includes(status as api.ResolutionStatus);
  };

  // Pending+Handoff entries are healthy in-flight messages but operators retain
  // manual override (FR-042) — they should expose the same Resubmit/Skip
  // actions as the standard actionable statuses.
  const isActionableEvent = (event: api.Event): boolean =>
    isActionableStatus(event.resolutionStatus) || isHandoffEvent(event);

  const getSessions = (
    deferredEvents: string[],
  ): Record<string, SessionState> => {
    const tempSessions: Record<string, SessionState> = {};

    deferredEvents.forEach((event) => {
      const eventSessionSplit = event.split("_");
      const sessionId = eventSessionSplit[1];
      if (tempSessions[sessionId] !== undefined) {
        tempSessions[sessionId].deferredCount += 1;
      } else {
        tempSessions[sessionId] = { deferredCount: 1 };
      }
    });

    return tempSessions;
  };

  const fetchSessionStatus = async (
    failedEvents: api.Event[],
    ticket: number,
  ): Promise<void> => {
    const uniqueSessionIds = [
      ...new Set(
        failedEvents.map((e) => e.sessionId).filter((id): id is string => !!id),
      ),
    ];

    if (uniqueSessionIds.length === 0) return;

    try {
      const sessionStatuses = await client.postEndpointSessionsBatch(
        endpointId,
        uniqueSessionIds,
      );

      const deferredEvents = sessionStatuses
        .map((status) => status.deferredEvents ?? [])
        .reduce((pre, cur) => pre.concat(cur), []);

      const tempSessions = getSessions(deferredEvents);
      if (ticket === fetchTicket.current) {
        setSessions((prev) => ({ ...prev, ...tempSessions }));
      }
    } catch {
      console.error("Failed to fetch session statuses in batch");
    }
  };

  const fetchEvents = async (filter: api.EventFilter): Promise<void> => {
    const ticket = ++fetchTicket.current;
    setIsLoading(true);
    setSearchError(undefined);
    // Invalidate the previous generation's pagination inputs immediately so a
    // Next click during the refresh can't issue a page for the old query.
    setContinuationToken(undefined);
    setCurrentFilter(undefined);
    try {
      const reqBody = new api.SearchRequest();
      reqBody.eventFilter = filter;
      reqBody.maxSearchItemsCount = maxResults;

      const response = await client.postApiEventEndpointIdGetByFilter(
        endpointId,
        reqBody,
      );
      const fetchedEvents = response.events ?? [];

      if (ticket === fetchTicket.current) {
        setEvents(fetchedEvents);
        setContinuationToken(response.continuationToken);
        setCurrentFilter(filter);
        tokenGeneration.current = ticket;
      }

      // Hydrate the per-session Deferred column in the background so the table
      // paints immediately. The session batch only fills that count, so
      // blocking the whole table (and its extra round trip) on it is
      // needless — fire it un-awaited and let the counts fill in when it
      // returns. Its commit is ticket-guarded too, so a stale batch can't
      // overwrite fresher counts. fetchSessionStatus owns its own error handling.
      const failedEvents = fetchedEvents.filter((e) =>
        isFailedStatus(e.resolutionStatus),
      );

      if (failedEvents.length > 0) {
        void fetchSessionStatus(failedEvents, ticket);
      }
    } catch (error) {
      if (ticket === fetchTicket.current) {
        console.error("Failed to fetch events:", error);
        setEvents([]);
        const status = (error as { status?: number } | null)?.status;
        setSearchError(
          status === 403
            ? "Access denied (HTTP 403). Payload searches involving sensitive or unclassified receiving event types require PiiReader. Endpoint read access is also required."
            : `Unable to load search results${typeof status === "number" ? ` (HTTP ${status})` : ""}. Try again; if it persists, contact your administrator.`,
        );
      }
    } finally {
      if (ticket === fetchTicket.current) {
        setIsLoading(false);
      }
    }
  };

  const nextPage = async (): Promise<void> => {
    if (
      isPagingRef.current ||
      !continuationToken ||
      continuationToken === "" ||
      continuationToken === "null" ||
      !currentFilter
    ) {
      return;
    }
    isPagingRef.current = true;

    // Pagination extends the generation the token/filter BELONG to (not the
    // newest fetch) — if a full re-fetch has started since that generation
    // committed, this append is already stale and every ticket guard below
    // rejects it.
    const ticket = tokenGeneration.current;

    try {
      const reqBody = new api.SearchRequest();
      reqBody.eventFilter = currentFilter;
      reqBody.continuationToken = continuationToken;
      reqBody.maxSearchItemsCount = maxResults;

      const response = await client.postApiEventEndpointIdGetByFilter(
        endpointId,
        reqBody,
      );
      const newEvents = response.events ?? [];

      if (ticket === fetchTicket.current) {
        setContinuationToken(response.continuationToken);
      }

      // Fetch session status for new failed events
      const failedEvents = newEvents.filter((e) =>
        isFailedStatus(e.resolutionStatus),
      );

      if (failedEvents.length > 0) {
        await fetchSessionStatus(failedEvents, ticket);
      }

      if (ticket === fetchTicket.current) {
        setEvents((prev) => [...prev, ...newEvents]);
      }
    } catch (error) {
      if (ticket === fetchTicket.current) {
        console.error("Failed to fetch next page:", error);
      }
    } finally {
      isPagingRef.current = false;
    }
  };

  const removeFromTable = (event: api.Event): void => {
    setEvents((prev) => prev.filter((e) => e.eventId !== event.eventId));
  };

  const skipSingleEvent = (event: api.Event): void => {
    removeFromTable(event);
    client
      .postSkipEventIds(event.eventId!, event.lastMessageId!)
      .catch((caught: unknown) => notifyError(describeCommandError(caught, "skip")));
  };

  const resubmitSingleEvent = (event: api.Event): void => {
    removeFromTable(event);
    client
      .postResubmitEventIds(event.eventId!, event.lastMessageId!)
      .catch((caught: unknown) =>
        notifyError(describeCommandError(caught, "resubmit")),
      );
  };

  const reprocessDeferredBySession = async (
    event: api.Event,
  ): Promise<void> => {
    const sessionId = event.sessionId;
    if (!sessionId) return;
    try {
      await client.postReprocessDeferred(endpointId, sessionId);
      setEvents((prev) =>
        prev.filter(
          (e) =>
            !(
              e.sessionId === sessionId &&
              e.resolutionStatus === api.ResolutionStatus.Deferred
            ),
        ),
      );
    } catch (err) {
      console.error(
        "Failed to reprocess deferred messages for session",
        sessionId,
        err,
      );
    }
  };

  // Optimistically set/clear an event's "reported" marker, persist it, and
  // revert the in-memory event if the request fails. Mutates the event in place
  // (matching the rest of this panel) then nudges `events` to re-render.
  const applyReportFlag = (
    event: api.Event,
    reported: boolean,
    ticketId: string | null,
  ): void => {
    const prev = {
      isReported: event.isReported,
      reportedBy: event.reportedBy,
      reportedAtUtc: event.reportedAtUtc,
      ticketId: event.ticketId,
    };
    // First touch: the pre-op state came from the server (search enrichment) —
    // that's the confirmed baseline until a write succeeds.
    if (!confirmedReportStates.current.has(event.eventId!)) {
      confirmedReportStates.current.set(event.eventId!, prev);
    }

    event.isReported = reported;
    event.reportedBy = reported
      ? (currentUser?.displayName ?? currentUser?.email ?? prev.reportedBy)
      : undefined;
    event.reportedAtUtc = reported ? moment() : undefined;
    event.ticketId = reported ? (ticketId ?? undefined) : undefined;
    setEvents((current) => [...current]);
    const applied = {
      isReported: event.isReported,
      reportedBy: event.reportedBy,
      reportedAtUtc: event.reportedAtUtc,
      ticketId: event.ticketId,
    };

    const body = new api.ReportEventRequest();
    body.reported = reported;
    body.ticketId = reported ? (ticketId ?? undefined) : undefined;
    // Serialize writes per event: chain this request after any in-flight one so
    // a rapid mark→Undo pair reaches the server in order.
    const eventKey = event.eventId!;
    const revision = (reportRevisions.current.get(eventKey) ?? 0) + 1;
    reportRevisions.current.set(eventKey, revision);
    const prior = reportWriteChains.current.get(eventKey) ?? Promise.resolve();
    const write = prior
      .then(() => client.postReportEvent(endpointId, eventKey, body))
      .then(() => {
        // Server accepted this write — its state is now what we applied.
        confirmedReportStates.current.set(eventKey, applied);
      })
      .catch((error) => {
        console.error("Failed to update reported flag:", error);
        // Only the LATEST operation may roll the UI back — a stale failure
        // must not overwrite a newer operation's optimistic state (the newer
        // write is queued behind this one and will settle the server state).
        // The rollback target is the last CONFIRMED state, never another
        // operation's unconfirmed optimistic snapshot.
        if (reportRevisions.current.get(eventKey) === revision) {
          const confirmed = confirmedReportStates.current.get(eventKey) ?? prev;
          event.isReported = confirmed.isReported;
          event.reportedBy = confirmed.reportedBy;
          event.reportedAtUtc = confirmed.reportedAtUtc;
          event.ticketId = confirmed.ticketId;
          setEvents((current) => [...current]);
        }
        notifyError("Could not update reported status");
      })
      .then(() => {
        if (reportWriteChains.current.get(eventKey) === write) {
          reportWriteChains.current.delete(eventKey);
        }
      });
    reportWriteChains.current.set(eventKey, write);
  };

  const markReported = (event: api.Event, ticketId: string | null): void => {
    applyReportFlag(event, true, ticketId);
    notifyWithUndo(
      ticketId ? `Reported under ${ticketId}.` : "Marked as reported.",
      () => applyReportFlag(event, false, null),
    );
  };

  const renderReportedCell = (item: api.Event): React.ReactNode => {
    const state = reportedCellState({
      isReported: item.isReported,
      ticketId: item.ticketId,
      reportedBy: item.reportedBy,
      reportedAtFormatted: item.reportedAtUtc
        ? formatMoment(item.reportedAtUtc)
        : undefined,
      ticketLinkTemplate,
    });

    if (state.kind === "ticket") {
      const chip = (
        <Badge variant="info" size="sm" withDot={false} className="font-mono">
          {state.ticketId}
        </Badge>
      );
      return (
        <Tooltip content={state.tooltip} position="top">
          {state.href ? (
            <a
              href={state.href}
              target="_blank"
              rel="noopener noreferrer"
              title={`Open ${state.ticketId}`}
              onClick={(e) => e.stopPropagation()}
            >
              {chip}
            </a>
          ) : (
            <span>{chip}</span>
          )}
        </Tooltip>
      );
    }

    if (state.kind === "done") {
      return (
        <Tooltip content={state.tooltip} position="top">
          <Badge variant="completed" size="sm" withDot={false}>
            ✓ Reported
          </Badge>
        </Tooltip>
      );
    }

    // Not yet reported: a bare flag keeps the column quiet; the label lives in
    // the accessible name and tooltip.
    return (
      <button
        type="button"
        aria-label="Report"
        title="Report this event"
        onClick={(e) => {
          e.stopPropagation();
          setReportTarget({ event: item, anchor: e.currentTarget });
        }}
        className="inline-flex h-7 w-7 items-center justify-center rounded-full border border-border-strong bg-card text-sm text-muted-foreground hover:border-primary hover:bg-primary/10 hover:text-primary-600"
      >
        ⚑
      </button>
    );
  };

  const getViableBodyActions = (event: api.Event): ITableBodyAction[] => {
    if (!isActionableEvent(event)) {
      return [];
    }

    const actions: ITableBodyAction[] = [];

    if (event.resolutionStatus === api.ResolutionStatus.Deferred) {
      actions.push({
        name: "Reprocess",
        description: "Retry the deferred messages in this session",
        onClick: () => {
          reprocessDeferredBySession(event);
          return false;
        },
      });
    } else {
      actions.push(
        {
          name: "Resubmit",
          description: "Send the message to the endpoint again",
          onClick: () => {
            resubmitSingleEvent(event);
            return false;
          },
        },
        {
          name: "Skip",
          description: "Mark the message as skipped and unblock its session",
          onClick: () => {
            skipSingleEvent(event);
            return false;
          },
        },
      );
    }

    return actions;
  };

  // Events actually shown — optionally hiding the ones already reported. The
  // KPI tiles still count over all loaded events.
  const visibleEvents = React.useMemo(
    () => (hideReported ? events.filter((e) => !e.isReported) : events),
    [events, hideReported],
  );

  // Hide-reported filters only the LOADED batch, which can thin the visible
  // rows below one client page while the server still has more. Top up with a
  // STRICTLY BOUNDED number of extra pages (an endpoint whose history is
  // entirely reported must not be paged into memory wholesale); beyond the
  // budget the user continues explicitly via the table's Next button, which
  // stays enabled while a server continuation token remains (hasMoreRows).
  const refillBudget = React.useRef(0);
  React.useEffect(() => {
    // Re-arm on toggle / new result-set generation.
    refillBudget.current = hideReported ? MAX_HIDE_REPORTED_REFILL_PAGES : 0;
  }, [hideReported, applied, searchNonce]);

  React.useEffect(() => {
    if (!hideReported || isLoading) return;
    if (refillBudget.current <= 0) return;
    if (!continuationToken || continuationToken === "null") return;
    if (visibleEvents.length >= 20) return; // one DataTable page (dataRowsPerPage)
    refillBudget.current -= 1;
    void nextPage();
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [hideReported, visibleEvents.length, continuationToken, isLoading]);

  const mapEvents = (): ITableRow[] => {
    return visibleEvents.map((item) => {
      // A failed message blocks its session; surface how many deferred
      // messages wait behind it as a chip on the status badge rather than a
      // column that is empty for every other row.
      const blockedCount = isFailedStatus(item.resolutionStatus)
        ? (sessions[item.sessionId!]?.deferredCount ?? 0)
        : 0;

      const row: ITableRow = {
        id: item.eventId!,
        route: `/Message/Index/${endpointId}/${item.eventId}/0`,
        bodyActions: getViableBodyActions(item),
        tone: item.isReported ? "reported" : undefined,
        data: new Map([
          [
            "eventId",
            {
              value: (
                <TruncatedGuid
                  guid={item.eventId}
                  onClick={(g) => handleFilterById("eventId", g)}
                />
              ),
              searchValue: item.eventId!,
            },
          ],
          [
            "status",
            {
              value: (
                <span className="inline-flex items-center gap-2">
                  <Badge
                    variant={statusToBadgeVariant(item.resolutionStatus)}
                    size="sm"
                  >
                    {formatResolutionStatus(
                      item.resolutionStatus,
                      item.reason,
                    ) ?? "—"}
                  </Badge>
                  {isHandoffEvent(item) && (
                    <Badge variant="info" size="sm">
                      Awaiting external
                    </Badge>
                  )}
                  {blockedCount > 0 && (
                    <Badge
                      variant="warning"
                      size="sm"
                      withDot={false}
                      title={`${blockedCount} deferred message${blockedCount === 1 ? "" : "s"} waiting on this failed message in session ${item.sessionId}`}
                    >
                      ⏸ {blockedCount} blocked
                    </Badge>
                  )}
                </span>
              ),
              searchValue:
                blockedCount > 0
                  ? `${item.resolutionStatus} ${blockedCount} blocked`
                  : (item.resolutionStatus ?? ""),
            },
          ],
          [
            "sessionId",
            {
              value: (
                <TruncatedGuid
                  guid={item?.sessionId}
                  onClick={(g) => handleFilterById("sessionId", g)}
                />
              ),
              searchValue: item?.sessionId || "",
            },
          ],
          [
            "eventTypeId",
            {
              value: item?.eventTypeId,
              searchValue: item?.eventTypeId || "",
            },
          ],
          [
            "resubmitCount",
            {
              value:
                (item.resubmitCount ?? 0) > 0 ? (
                  <Badge variant="info" size="sm">
                    {item.resubmitCount}
                  </Badge>
                ) : null,
              searchValue: String(item.resubmitCount ?? 0),
            },
          ],
          [
            "reported",
            {
              value: renderReportedCell(item),
              searchValue: item.isReported ? (item.ticketId ?? "reported") : "",
            },
          ],
          [
            "updated",
            {
              value: formatMoment(item?.updatedAt, true),
              searchValue: formatMoment(item?.updatedAt) || "",
            },
          ],
          [
            "added",
            {
              value: formatMoment(item?.enqueuedTimeUtc, true),
              searchValue: formatMoment(item?.enqueuedTimeUtc) || "",
            },
          ],
        ]),
      };
      return row;
    });
  };

  // Derive the table rows from the shown events + hydrated session counts.
  // Memoised so we only re-map when the underlying data changes — no redundant
  // state mirror + effect (which cost an extra render per fetch).
  const rows = React.useMemo(
    () => mapEvents(),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [visibleEvents, sessions, ticketLinkTemplate, currentUser],
  );

  const doActionSelectedRows = (
    selectedRows: ITableRow[],
    actionName: string,
  ): void => {
    selectedRows.forEach((row) => {
      const action = row.bodyActions?.find((a) => a.name === actionName);
      action?.onClick();
    });
  };

  const hasActionableSelected = (selectedRows: ITableRow[]): boolean => {
    return selectedRows.some(
      (row) => row.bodyActions && row.bodyActions.length > 0,
    );
  };

  // User clicked Search — write the current draft filter to the URL.
  // The useEffect on `applied` then refetches.
  const handleFilterClicked = (filter: api.EventFilter): void => {
    applyFilters(paramsFromEventFilter(filter, applied));
    // Force a refresh even when the filter is unchanged (same URL → `applied`
    // stays referentially stable, so the fetch effect would otherwise not re-run).
    setSearchNonce((n) => n + 1);
  };

  // Reset — clear all URL filter params back to defaults (all statuses).
  const handleReset = (): void => {
    resetFilters();
  };

  // One-click triage view: the actionable failed statuses.
  const handleFailedOnly = (): void => {
    applyFilters({ ...applied, status: [...FAILED_STATUS_SET] });
  };

  // Commit-on-change for the Status combobox: chip add/remove writes to the
  // URL immediately so browser Back from an event-detail page restores the
  // exact filter the user had selected, not the page's defaults. Removing the
  // last chip is treated as "All statuses" (sentinel) for the same reason.
  const handleStatusChange = (next: api.ResolutionStatus[]): void => {
    const nextStatus =
      next.length === 0 ? [STATUS_ALL_SENTINEL] : (next as string[]);
    applyFilters({ ...applied, status: nextStatus });
  };

  // Clicking an Event Id / Session Id cell narrows the list to that value.
  // Any prior status/type filter is kept so the click refines rather than
  // replaces the operator's view. The copy icon next to the value still copies.
  const handleFilterById = (
    key: "eventId" | "sessionId",
    value: string,
  ): void => {
    applyFilters({ ...applied, [key]: value });
  };

  const setMaxResults = (next: number): void => {
    applyFilters({ ...applied, maxResults: String(next) });
  };

  const setViewMode = (next: "list" | "grouped"): void => {
    applyFilters({ ...applied, viewMode: next });
  };

  // Commit the advanced filters from the popover (Updated/Added ranges +
  // Payload), leaving the basic filters untouched. Changing these applied
  // params triggers the fetch effect, so no extra refresh nonce is needed.
  const handleApplyAdvanced = (next: AdvancedFilters): void => {
    applyFilters({ ...applied, ...next });
  };

  const toggleColumn = (id: string): void => {
    setHiddenCols((prev) => {
      const next = new Set(prev);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      saveHiddenColumns(next);
      return next;
    });
  };

  const resetColumns = (): void => {
    const cleared = new Set<string>();
    saveHiddenColumns(cleared);
    setHiddenCols(cleared);
  };

  // Visible columns after applying the operator's hidden-column choices; this
  // drives the table header. DataTable resolves each body cell by headCell id,
  // so the rows themselves need no filtering.
  const headCells: ITableHeadCell[] = React.useMemo(
    () => getVisibleColumns(hiddenCols),
    [hiddenCols],
  );

  const headActions: ITableHeadAction[] = [
    {
      name: "Resubmit",
      onClick: (selectedRows: ITableRow[]) => {
        if (hasActionableSelected(selectedRows)) {
          const actionableRows = selectedRows.filter(
            (row) => row.bodyActions && row.bodyActions.length > 0,
          );
          doActionSelectedRows(actionableRows, "Resubmit");
        }
        return false;
      },
    },
    {
      name: "Skip",
      onClick: (selectedRows: ITableRow[]) => {
        if (hasActionableSelected(selectedRows)) {
          const actionableRows = selectedRows.filter(
            (row) => row.bodyActions && row.bodyActions.length > 0,
          );
          doActionSelectedRows(actionableRows, "Skip");
        }
        return false;
      },
    },
  ];

  // Hide the sentinel from the combobox — "all statuses" should render as an
  // empty chip area, not a chip literally labelled "*".
  const displayedStatuses = isAllStatusesSentinel(applied.status)
    ? []
    : (applied.status as api.ResolutionStatus[]);

  // Key the filter subtree on URL state so sub-filter inputs re-mount and re-seed
  // from `initialValue` props whenever the URL changes (e.g. browser Back/forward).
  const filterRemountKey = `${applied.status.join(",")}|${applied.eventTypeId.join(",")}|${applied.eventId}|${applied.sessionId}`;

  // Counts derived from the currently-loaded page. Only a fallback: the tiles
  // read as endpoint totals (Failed opens the endpoint's whole backlog), and a
  // page holds at most maxResults rows of every status.
  const pageCounts = React.useMemo((): TileCounts => {
    let failed = 0;
    let deferred = 0;
    let pending = 0;
    for (const e of events) {
      switch (e.resolutionStatus) {
        case api.ResolutionStatus.Failed:
        case api.ResolutionStatus.DeadLettered:
        case api.ResolutionStatus.Unsupported:
          failed += 1;
          break;
        case api.ResolutionStatus.Deferred:
          deferred += 1;
          break;
        case api.ResolutionStatus.Pending:
          pending += 1;
          break;
      }
    }
    return { failed, deferred, pending };
  }, [events]);
  const counts = endpointTotals ?? pageCounts;

  // Applied advanced filters, surfaced through the Advanced-filters popover
  // (next to the column chooser) and the chip row below the view controls.
  const advancedValue: AdvancedFilters = {
    updatedFrom: applied.updatedFrom,
    updatedTo: applied.updatedTo,
    addedFrom: applied.addedFrom,
    addedTo: applied.addedTo,
    payload: applied.payload,
  };

  const hasActiveFilters =
    (applied.status.length > 0 && !isAllStatusesSentinel(applied.status)) ||
    applied.eventTypeId.length > 0 ||
    applied.eventId.length > 0 ||
    applied.sessionId.length > 0 ||
    applied.updatedFrom.length > 0 ||
    applied.updatedTo.length > 0 ||
    applied.addedFrom.length > 0 ||
    applied.addedTo.length > 0 ||
    applied.payload.length > 0;

  const segBtn = (active: boolean) =>
    cn(
      "px-3 py-1.5 rounded-md text-xs font-semibold transition-colors",
      active
        ? "bg-primary text-white"
        : "text-muted-foreground hover:text-foreground",
    );

  return (
    <FilterContext.Provider value={context}>
      <div className="flex flex-col gap-4 w-full">
        {/* Promote summary metrics above the table — design rec §03.
            Status-coloured tiles answer "how is this endpoint doing?"
            before the operator scans rows. */}
        <StatRow columns={3}>
          <StatTile
            label="Failed"
            // Opens the cross-endpoint Failed page scoped to this endpoint:
            // its chart and error grouping cover the whole backlog, not one page.
            onClick={() =>
              navigate(`/Failed?endpointId=${encodeURIComponent(endpointId)}`)
            }
            value={isLoading ? "—" : counts.failed.toLocaleString()}
            tone={counts.failed > 0 ? "danger" : "muted"}
            delta={
              isLoading
                ? "…"
                : counts.failed === 0
                  ? "all clear"
                  : "Failed + DeadLettered + Unsupported"
            }
          />
          <StatTile
            label="Deferred"
            value={isLoading ? "—" : counts.deferred.toLocaleString()}
            tone={counts.deferred > 0 ? "warning" : "muted"}
            delta={
              isLoading
                ? "…"
                : counts.deferred === 0
                  ? "none"
                  : "awaiting retry"
            }
          />
          <StatTile
            label="Pending"
            value={isLoading ? "—" : counts.pending.toLocaleString()}
            tone={counts.pending > 0 ? "warning" : "muted"}
            delta={
              isLoading ? "…" : counts.pending === 0 ? "none" : "in-flight"
            }
          />
        </StatRow>

        <EventFiltering
          key={filterRemountKey}
          handleFilterClicked={handleFilterClicked}
          onReset={handleReset}
          onApplyFailed={handleFailedOnly}
          onStatusChange={handleStatusChange}
          initialStatuses={displayedStatuses}
          initialEventTypes={applied.eventTypeId}
          initialEventId={applied.eventId}
          initialSessionId={applied.sessionId}
          maxResults={maxResults}
          onMaxResultsChange={setMaxResults}
        />

        {/* View-mode segmented control — matches design `.seg` button group.
            Single role per hue: coral is "active selection", not a status. */}
        <div className="flex items-center gap-2 text-sm">
          <span className="text-muted-foreground font-semibold text-[13px]">
            View:
          </span>
          <div className="inline-flex items-center bg-card border border-border rounded-nb-md p-[3px] gap-[2px]">
            <button
              type="button"
              className={segBtn(viewMode === "list")}
              onClick={() => setViewMode("list")}
            >
              List
            </button>
            <button
              type="button"
              className={segBtn(viewMode === "grouped")}
              onClick={() => setViewMode("grouped")}
            >
              Grouped by Error
            </button>
          </div>
          <div className="ml-auto flex items-center gap-3">
            {viewMode === "list" && (
              <>
                <label className="inline-flex cursor-pointer select-none items-center gap-2 text-muted-foreground">
                  <Checkbox
                    checked={hideReported}
                    onChange={(e) => setHideReported(e.target.checked)}
                    aria-label="Hide reported events"
                  />
                  Hide reported
                </label>
                <ColumnChooser
                  columns={EVENT_COLUMNS}
                  hidden={hiddenCols}
                  onToggle={toggleColumn}
                  onReset={resetColumns}
                />
              </>
            )}
            <AdvancedFiltersPopover
              value={advancedValue}
              onApply={handleApplyAdvanced}
              align="right"
            />
          </div>
        </div>
        <AdvancedFilterChips
          value={advancedValue}
          onApply={handleApplyAdvanced}
        />

        {searchError ? (
          <div
            role="alert"
            className="rounded-nb-md border border-border bg-card p-6"
          >
            <p className="font-semibold">Search failed</p>
            <p className="text-sm text-muted-foreground">{searchError}</p>
          </div>
        ) : viewMode === "grouped" ? (
          <ErrorGroupedView
            events={events}
            onResubmitEvent={resubmitSingleEvent}
            onSkipEvent={skipSingleEvent}
            isActionableStatus={isActionableStatus}
          />
        ) : !isLoading && events.length === 0 ? (
          <EmptyState
            icon="◌"
            title={
              hasActiveFilters
                ? "No events match your filters"
                : "No events yet"
            }
            description={
              hasActiveFilters
                ? "Try removing a filter or expanding the time window."
                : "Messages will appear here as this endpoint receives traffic."
            }
            action={
              hasActiveFilters && (
                <button
                  type="button"
                  onClick={handleReset}
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
            headActions={headActions}
            rows={rows}
            withCheckboxes={true}
            rowActions="menu"
            noDataMessage="No events available"
            isLoading={isLoading}
            count={rows.length}
            hideDense={true}
            dataRowsPerPage={20}
            onPageChange={nextPage}
            hasMoreRows={!!continuationToken && continuationToken !== "null"}
            fixedWidth={"-webkit-fill-available"}
          />
        )}
      </div>
      {reportTarget && (
        <ReportPopover
          anchor={reportTarget.anchor}
          onClose={() => setReportTarget(null)}
          onSubmit={(ticketId) => {
            markReported(reportTarget.event, ticketId);
            setReportTarget(null);
          }}
        />
      )}
    </FilterContext.Provider>
  );
};

export default EventsPanel;
