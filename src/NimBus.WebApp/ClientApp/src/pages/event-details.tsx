import * as React from "react";
import * as api from "api-client";
import Page from "components/page";
import MessageListing from "components/event-details/message-listing";
import DeferredRecovery from "components/event-details/deferred-recovery";
import { useParams, useNavigate } from "react-router-dom";
import TabSelection from "components/tab-selection";
import Loading from "components/loading/loading";
import { Button } from "components/ui/button";
import BlockedListing from "components/event-details/blocked-listing";
import AuditListing from "components/event-details/audit-listing";
import FlowTimeline from "components/event-details/flow-timeline";
import { parseBlockedByEventId } from "functions/endpoint.functions";

const { useEffect, useState, useRef } = React;

export interface BlockedEvent {
  message: api.Message;
  status: string;
}

export interface PendingEvent {
  message: api.Message;
  status: string;
}

type EventDetailsProps = {
  backIndex?: number;
};

// Initial blocked-events page size. Server clamps `take` to [1, 200] so this fits comfortably.
const BLOCKED_PAGE_SIZE = 20;

// An accepted resubmit or skip archives the row until the Resolver records the
// next attempt, so the event 404s for a moment and then passes through Pending.
// The page follows it until it settles: quick checks first, then every 10
// seconds, about two minutes in all, after which it offers a manual refresh.
const COMMAND_FOLLOW_UP_DELAYS_MS = [
  300,
  700,
  1500,
  3000,
  5000,
  ...Array<number>(11).fill(10_000),
];

const isInFlight = (event: api.Event) =>
  event.resolutionStatus?.toLowerCase() === "pending" &&
  event.pendingSubStatus?.toLowerCase() !== "handoff";

// Resolve the BlockedEvent ID list into displayable rows (one detail fetch per ID).
// The server returns only the requested page, so the number of fetches is bounded by
// the page size, not the full set of blocked siblings on the session. The fetches are
// independent GETs, so they run concurrently; the server's page order is preserved and
// rows whose detail fetch resolved to nothing are dropped (same as the previous loop).
export const enrichBlockedItems = async (
  client: api.Client,
  items: api.BlockedEvent[],
): Promise<BlockedEvent[]> => {
  const messages = await Promise.all(
    items.map((event) =>
      client.getEventIds(event.eventId!, event.originatingId!),
    ),
  );
  return items
    .map((event, index) => ({
      message: messages[index],
      status: event.status!,
    }))
    .filter((entry) => Boolean(entry.message));
};

const EventDetails = (props: EventDetailsProps) => {
  const client = new api.Client(api.CookieAuth());
  const params = useParams();
  const navigate = useNavigate();

  const [cosmosEvent, setCosmosEvent] = useState<api.Event>();
  const [eventTypes, setEventTypes] = useState<api.EventType[]>([]);
  const [histories, setHistories] = useState<api.Message[]>([]);
  const [audits, setAudits] = useState<api.MessageAudit[]>([]);
  const [blockedEvents, setBlockedEvents] = useState<BlockedEvent[]>([]);
  const [blockedTotal, setBlockedTotal] = useState<number>(0);
  // Bumped whenever an action writes a new audit row. The Audit tab fetches
  // its own data, so remounting it on this key is what makes a just-added
  // comment appear without a page reload.
  const [auditReloadKey, setAuditReloadKey] = useState(0);
  // The attempt (by message id) an accepted resubmit or skip acted on. Kept
  // here, not in MessageListing, because switching tabs remounts the listing.
  const [commandAcceptedFor, setCommandAcceptedFor] = useState<string>();
  // Set when the follow-up gave up before the event settled.
  const [followUpExpired, setFollowUpExpired] = useState(false);
  const routeGeneration = useRef(0);
  const historyRequest = useRef(0);

  // Every history fetch goes through here, and only the newest may land: an
  // older one (the page's first, or an earlier follow-up) can answer late.
  const loadHistory = (current: () => boolean) => {
    const request = ++historyRequest.current;
    client
      .getEventDetailsHistoryId(params.id!, params.endpointId!)
      .then((res) => {
        if (current() && request === historyRequest.current) setHistories(res);
      })
      .catch(() => {});
  };

  useEffect(() => {
    const generation = ++routeGeneration.current;
    const current = () => generation === routeGeneration.current;
    // Reset before re-fetch so clicking a blocked row doesn't render the
    // previous event's data while the new fetch is in flight. The Loading
    // splash below keys off `cosmosEvent === undefined`.
    setCosmosEvent(undefined);
    setHistories([]);
    setAudits([]);
    setBlockedEvents([]);
    setBlockedTotal(0);
    setCommandAcceptedFor(undefined);
    setFollowUpExpired(false);

    const fetchData = async () => {
      // History, audits and event types are keyed off the route params, not the
      // resolved event, so start them immediately — they run concurrently with
      // the event fetch instead of waiting for it to resolve first.
      loadHistory(current);

      client.getMessageAuditsEventId(params.id!).then((res) => {
        if (current()) setAudits(res);
      });

      client.getEventTypes().then((res) => {
        if (current()) setEventTypes(res);
      });

      const tempCosmosEvent = await client.getEventId(
        params.id!,
        params.endpointId!,
      );

      if (!current()) return;
      setCosmosEvent(tempCosmosEvent);

      // Only the blocked-siblings fetch depends on the resolved event (its
      // endpointId/sessionId and resolutionStatus), so it stays after the await.
      if (
        tempCosmosEvent.resolutionStatus?.toLowerCase() === "failed" ||
        tempCosmosEvent.resolutionStatus?.toLowerCase() === "unsupported" ||
        tempCosmosEvent.resolutionStatus?.toLowerCase() === "deadlettered"
      )
        client
          .getEventBlockedId(
            tempCosmosEvent.endpointId!,
            tempCosmosEvent.sessionId!,
            0,
            BLOCKED_PAGE_SIZE,
          )
          .then(async (page) => {
            const enriched = await enrichBlockedItems(client, page.items ?? []);
            if (!current()) return;
            setBlockedEvents(enriched);
            setBlockedTotal(page.total ?? enriched.length);
          });
    };
    fetchData();
    return () => {
      routeGeneration.current++;
    };
    // Re-run when the route params change (e.g. clicking a row in the Blocked
    // tab navigates to /Message/Index/{endpointId}/{eventId}/0 while keeping
    // the same EventDetails component instance mounted — without these deps
    // the page would keep showing the previous event).
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [params.id, params.endpointId]);

  const fetchBlockedEvents = async (skip: number, take: number) => {
    if (!cosmosEvent?.endpointId || !cosmosEvent?.sessionId) return;
    const page = await client.getEventBlockedId(
      cosmosEvent.endpointId,
      cosmosEvent.sessionId,
      skip,
      take,
    );
    const enriched = await enrichBlockedItems(client, page.items ?? []);
    setBlockedEvents(enriched);
    setBlockedTotal(page.total ?? enriched.length);
  };

  const reloadEvent = async () => {
    const generation = routeGeneration.current;
    const updated = await client.getEventId(params.id!, params.endpointId!);
    if (generation === routeGeneration.current) setCosmosEvent(updated);
  };

  // Sends an operator command for the attempt on screen. Once the server accepts
  // it, the actions stay closed for that attempt and the page follows the event.
  // The route is captured before the POST so a late answer cannot act on the
  // event the operator moved to.
  const runCommand = async (actedOnMessageId: string, send: () => Promise<void>) => {
    const generation = routeGeneration.current;
    await send();
    if (generation !== routeGeneration.current) return;
    setCommandAcceptedFor(actedOnMessageId);
    setFollowUpExpired(false);
    followCommand(generation, actedOnMessageId);
  };

  // Runs after the command's promise has resolved, so a refresh that fails here
  // never reads as a failed command. Stops when the operator leaves the event.
  const followCommand = async (generation: number, actedOnMessageId: string) => {
    const current = () => generation === routeGeneration.current;
    for (const delay of COMMAND_FOLLOW_UP_DELAYS_MS) {
      await new Promise((resolve) => setTimeout(resolve, delay));
      if (!current()) return;
      let updated: api.Event;
      try {
        updated = await client.getEventId(params.id!, params.endpointId!);
      } catch {
        continue; // still archived
      }
      if (!current()) return;
      if (updated.lastMessageId === actedOnMessageId) continue;
      setCosmosEvent(updated);
      loadHistory(current);
      if (!isInFlight(updated)) {
        reloadAudits();
        return;
      }
    }
    if (current()) setFollowUpExpired(true);
  };

  // The manual refresh offered once the follow-up has given up. The notice stays
  // until the event shows a settled attempt other than the one acted on.
  const refreshEvent = async () => {
    const generation = routeGeneration.current;
    const current = () => generation === routeGeneration.current;
    let updated: api.Event;
    try {
      updated = await client.getEventId(params.id!, params.endpointId!);
    } catch {
      return; // still archived
    }
    if (!current()) return;
    setCosmosEvent(updated);
    loadHistory(current);
    reloadAudits();
    if (updated.lastMessageId !== commandAcceptedFor && !isInFlight(updated))
      setFollowUpExpired(false);
  };

  const skipEvent = (eventId: string, messageId: string) =>
    runCommand(messageId, () => client.postSkipEventIds(eventId, messageId));

  const resubmitEvent = (eventId: string, messageId: string) =>
    runCommand(messageId, () => client.postResubmitEventIds(eventId, messageId));

  const reprocessDeferred = async () => {
    if (!cosmosEvent?.endpointId || !cosmosEvent?.sessionId) return;
    await client.postReprocessDeferred(
      cosmosEvent.endpointId,
      cosmosEvent.sessionId,
    );
    await reloadEvent();
  };

  const reloadAudits = () => {
    const generation = routeGeneration.current;
    client.getMessageAuditsEventId(params.id!).then((res) => {
      if (generation === routeGeneration.current) setAudits(res);
    });
    setAuditReloadKey((k) => k + 1);
  };

  const deleteEvent = async () => {
    if (!cosmosEvent) return;
    await client.deleteEventInvalidId(
      cosmosEvent.endpointId!,
      cosmosEvent.eventId!,
      cosmosEvent.sessionId!,
    );
    navigate(`/Endpoints/Details/${params.endpointId!}`);
  };

  const resubmitEventWithChanges = (
    eventId: string,
    messageId: string,
    body: api.ResubmitWithChanges,
  ) =>
    runCommand(messageId, () =>
      client.postResubmitWithChangesEventIds(eventId, messageId, body),
    );

  // Settle a pending handoff on the operator's behalf. The Pending → Completed/
  // Failed transition itself is asynchronous (the owning subscriber processes
  // the published control message), so we reload audits — which the operator
  // note/reason lands in immediately — rather than the event document.
  const completeHandoff = async (
    endpointId: string,
    eventId: string,
    messageId: string,
    note?: string,
  ) => {
    await client.postHandoffComplete(
      endpointId,
      eventId,
      messageId,
      note ? new api.CompleteHandoffRequest({ note }) : undefined,
    );
    reloadAudits();
  };

  const failHandoff = async (
    endpointId: string,
    eventId: string,
    messageId: string,
    reason: string,
    errorType?: string,
  ) => {
    await client.postHandoffFail(
      endpointId,
      eventId,
      messageId,
      new api.FailHandoffRequest({ reason, errorType }),
    );
    reloadAudits();
  };

  const tabs = () => {
    const blockedCount = blockedTotal > 99 ? "99+" : blockedTotal;

    // Spec 006: derive the blocking event id from the most recent deferral
    // history entry. Older deferrals can name a different (already-resolved)
    // blocker, so we walk newest-first by enqueuedTimeUtc and pick the first
    // entry whose error text parses to a GUID. Falls through to undefined when
    // no history entry matches the canonical "is blocked by {GUID}" phrase.
    const blockedByEventId = (() => {
      if (!histories || histories.length === 0) return undefined;
      const ordered = [...histories].sort((a, b) => {
        const av = a.enqueuedTimeUtc?.valueOf?.() ?? 0;
        const bv = b.enqueuedTimeUtc?.valueOf?.() ?? 0;
        return bv - av;
      });
      for (const entry of ordered) {
        const parsed = parseBlockedByEventId(entry.errorContent?.errorText);
        if (parsed) return parsed;
      }
      return undefined;
    })();

    return [
      {
        name: `Message`,
        isEnabled: true,
        content: (
          <div className="w-full min-w-0">
            {cosmosEvent?.resolutionStatus?.toLowerCase() === "deferred" && (
              <DeferredRecovery
                key={`${params.endpointId}:${params.id}`}
                endpointId={params.endpointId!}
                eventId={params.id!}
                onResolved={async () => {
                  await reloadEvent();
                  reloadAudits();
                }}
              />
            )}
            {followUpExpired && (
              <div
                role="status"
                className="my-4 flex items-center justify-between gap-4 rounded-nb-lg border border-border bg-surface p-4 text-sm"
              >
                <span>
                  This event is still being processed. Refresh to see its latest
                  state.
                </span>
                <Button size="xs" variant="outline" onClick={refreshEvent}>
                  Refresh
                </Button>
              </div>
            )}
            <MessageListing
              resubmitEventWithChanges={resubmitEventWithChanges}
              resubmitEvent={resubmitEvent}
              skipEvent={skipEvent}
              deleteEvent={deleteEvent}
              reprocessDeferred={reprocessDeferred}
              completeHandoff={completeHandoff}
              failHandoff={failHandoff}
              onCommentAdded={reloadAudits}
              eventTypes={eventTypes}
              eventDetails={cosmosEvent}
              commandAcceptedFor={commandAcceptedFor}
              // Spec 005 (FR-016): the same `histories` array already fed to
              // `FlowTimeline` is forwarded here so MessageListing can derive a
              // lifecycle-aware Queue value for deferred / pending-handoff
              // events.
              messages={histories}
              blockedByEventId={blockedByEventId}
              key="Message"
            />
          </div>
        ),
      },
      {
        name: `Flow (${histories.length + audits.length})`,
        isEnabled: histories.length > 0,
        content: (
          <FlowTimeline messages={histories} audits={audits} key="Flow" />
        ),
      },
      {
        // Fetches its own rows from /api/audits/search rather than reusing the
        // page's `audits`: that endpoint carries the Data and Access Denied
        // columns the per-event MessageAudit shape does not, and supports
        // paging past the first 30.
        name: `Audit`,
        isEnabled: true,
        content: (
          <AuditListing
            endpointId={cosmosEvent?.endpointId ?? params.endpointId!}
            eventId={cosmosEvent?.eventId ?? params.id!}
            key={`Audit-${auditReloadKey}`}
          />
        ),
      },
      {
        name: `Blocked (${blockedCount})`,
        isEnabled: blockedTotal > 0,
        content: (
          <BlockedListing
            totalItems={blockedTotal}
            fetchBlockedEvents={fetchBlockedEvents}
            events={blockedEvents}
            endpointId={cosmosEvent?.endpointId ?? params.endpointId}
            key="Blocked"
          />
        ),
      },
    ];
  };

  return (
    <>
      {cosmosEvent === undefined ? (
        <div className="flex flex-1 justify-center items-center">
          <Loading />
        </div>
      ) : (
        <Page
          title="Event Details"
          backbutton={true}
          backUrl={`/Endpoints/Details/${params.endpointId!}`}
          backIndex={params.backindex!}
        >
          <TabSelection tabs={tabs()} />
        </Page>
      )}
    </>
  );
};

export default EventDetails;
