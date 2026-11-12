import * as React from "react";
import * as api from "api-client";
import { Link } from "react-router-dom";
import { formatMoment } from "functions/endpoint.functions";
import TruncatedGuid from "components/common/truncated-guid";
import { Badge } from "components/ui/badge";
import { Button } from "components/ui/button";
import { Checkbox } from "components/ui/checkbox";
import {
  STATUS_COLORS,
  errorTextOf,
} from "functions/failed-messages.functions";
import { cn } from "lib/utils";

/** The id a failure is selected and opened by: one event per endpoint. */
export const failureIdOf = (e: api.Event) => `${e.endpointId}/${e.eventId}`;

/** The Event Details route of a failure. */
export const failureRouteOf = (e: api.Event) =>
  `/Message/Index/${e.endpointId}/${e.eventId}/0?from=failed`;

export type FailureAction = "Resubmit" | "Skip";

export interface FailedItemListProps {
  events: api.Event[];
  /** Deferred-message counts keyed by `${endpointId}/${sessionId}`. */
  blocked: Record<string, number>;
  selected: Set<string>;
  onSelectedChange: (next: Set<string>) => void;
  onAct: (action: FailureAction, targets: api.Event[]) => void;
  /** Opens a failure; a plain click on its title calls this instead of navigating. */
  onOpen: (e: api.Event) => void;
  /** The failure shown in the details panel, highlighted in the list. */
  openId?: string;
  /** Narrows the page to one event or session (a click on the ID). */
  onNarrow: (field: "eventId" | "sessionId", value: string) => void;
  hasMore: boolean;
  isLoading: boolean;
  onLoadMore: () => void;
}

/**
 * The failures as Application Insights-style individual items: a status bar, "time - STATUS",
 * the error in monospace and the identifiers underneath, with per-item and bulk actions.
 */
export default function FailedItemList({
  events,
  blocked,
  selected,
  onSelectedChange,
  onAct,
  onOpen,
  openId,
  onNarrow,
  hasMore,
  isLoading,
  onLoadMore,
}: FailedItemListProps) {
  const ids = events.map(failureIdOf);
  const chosen = events.filter((e) => selected.has(failureIdOf(e)));
  const allChosen = events.length > 0 && chosen.length === events.length;

  const toggle = (id: string) => {
    const next = new Set(selected);
    if (next.has(id)) next.delete(id);
    else next.add(id);
    onSelectedChange(next);
  };

  return (
    <div className="flex flex-col gap-2">
      <div
        role="toolbar"
        aria-label="Bulk actions"
        className="flex flex-wrap items-center gap-3 rounded-nb-sm bg-muted px-3 py-2 text-[13px]"
      >
        <label className="flex cursor-pointer items-center gap-2 font-semibold">
          <Checkbox
            checked={allChosen}
            indeterminate={chosen.length > 0 && !allChosen}
            disabled={events.length === 0}
            onChange={() =>
              onSelectedChange(allChosen ? new Set() : new Set(ids))
            }
            aria-label="Select all loaded failures"
          />
          Select all {events.length.toLocaleString()}
          {hasMore ? "+" : ""}
        </label>
        {chosen.length > 0 && (
          <span className="text-muted-foreground">
            {chosen.length} selected
          </span>
        )}
        <span className="flex-1" />
        <Button
          size="sm"
          colorScheme="primary"
          disabled={chosen.length === 0}
          onClick={() => onAct("Resubmit", chosen)}
        >
          Resubmit{chosen.length ? ` ${chosen.length}` : ""}
        </Button>
        <Button
          size="sm"
          variant="outline"
          colorScheme="gray"
          disabled={chosen.length === 0}
          onClick={() => onAct("Skip", chosen)}
        >
          Skip{chosen.length ? ` ${chosen.length}` : ""}
        </Button>
      </div>

      <ul aria-label="Failures" className="m-0 flex list-none flex-col p-0">
        {events.map((e) => {
          const id = failureIdOf(e);
          const status = e.resolutionStatus ?? "Failed";
          const error = errorTextOf(e);
          const blockedCount = blocked[`${e.endpointId}/${e.sessionId}`] ?? 0;
          const isOpen = id === openId;
          return (
            <li
              key={id}
              className={cn(
                "flex gap-3 border-b border-border px-3 py-3",
                selected.has(id) && "bg-primary-tint/60 dark:bg-primary-900/20",
                isOpen &&
                  "bg-primary-50 ring-2 ring-inset ring-primary dark:bg-primary-900/30",
                e.isReported && !isOpen && "opacity-70",
              )}
            >
              <Checkbox
                checked={selected.has(id)}
                onChange={() => toggle(id)}
                aria-label={`Select event ${e.eventId}`}
                className="mt-1"
              />
              <span
                aria-hidden="true"
                className="w-[5px] shrink-0 self-stretch rounded-[1px]"
                style={{
                  background: STATUS_COLORS[status] ?? STATUS_COLORS.Failed,
                }}
              />
              <div className="flex min-w-0 flex-1 flex-col gap-1">
                <div className="flex flex-wrap items-center gap-2">
                  <Link
                    to={failureRouteOf(e)}
                    onClick={(ev) => {
                      // Keep new-tab and new-window clicks as plain links.
                      if (
                        ev.metaKey ||
                        ev.ctrlKey ||
                        ev.shiftKey ||
                        ev.button !== 0
                      )
                        return;
                      ev.preventDefault();
                      onOpen(e);
                    }}
                    className="text-[15px] font-bold text-foreground hover:underline"
                  >
                    {e.updatedAt
                      ? formatMoment(e.updatedAt, true)
                      : "—"}{" "}
                    - {status.toUpperCase()}
                  </Link>
                  {blockedCount > 0 && (
                    <Badge
                      variant="warning"
                      size="sm"
                      withDot={false}
                      title={`${blockedCount} deferred message${blockedCount === 1 ? "" : "s"} waiting on this failure in session ${e.sessionId}`}
                    >
                      {blockedCount} blocked in session
                    </Badge>
                  )}
                  {(e.resubmitCount ?? 0) > 0 && (
                    <Badge variant="info" size="sm" withDot={false}>
                      resubmitted {e.resubmitCount}×
                    </Badge>
                  )}
                  {e.isReported && (
                    <Badge
                      variant="info"
                      size="sm"
                      withDot={false}
                      title={
                        e.reportedBy ? `Reported by ${e.reportedBy}` : undefined
                      }
                    >
                      {e.ticketId || "Reported"}
                    </Badge>
                  )}
                </div>
                <div
                  className="truncate font-mono text-[13px] text-foreground"
                  title={error}
                >
                  {error ?? "—"}
                </div>
                <div className="flex flex-wrap items-center gap-x-5 gap-y-1 text-[12.5px] text-muted-foreground">
                  <span>
                    Event type:{" "}
                    <b className="font-semibold text-foreground">
                      {e.eventTypeId ?? "—"}
                    </b>
                  </span>
                  <span>
                    Endpoint:{" "}
                    <Link
                      to={`/Endpoints/Details/${e.endpointId}`}
                      className="font-semibold text-foreground hover:underline"
                    >
                      {e.endpointId}
                    </Link>
                  </span>
                  <span className="inline-flex items-center gap-1">
                    Session:
                    <TruncatedGuid
                      guid={e.sessionId}
                      displayLength={24}
                      onClick={(g) => onNarrow("sessionId", g)}
                    />
                  </span>
                  <span className="inline-flex items-center gap-1">
                    Event ID:
                    <TruncatedGuid
                      guid={e.eventId}
                      linkTo={failureRouteOf(e)}
                      onClick={() => onOpen(e)}
                    />
                  </span>
                </div>
              </div>
              <div className="flex shrink-0 items-start gap-1.5">
                <Button
                  size="xs"
                  variant="outline"
                  colorScheme="gray"
                  onClick={() => onAct("Resubmit", [e])}
                  title="Send the message to the endpoint again"
                >
                  Resubmit
                </Button>
                <Button
                  size="xs"
                  variant="outline"
                  colorScheme="gray"
                  onClick={() => onAct("Skip", [e])}
                  title="Mark the message as skipped and unblock its session"
                >
                  Skip
                </Button>
              </div>
            </li>
          );
        })}
        {isLoading &&
          events.length === 0 &&
          [0, 1, 2, 3].map((i) => (
            <li
              key={`skeleton-${i}`}
              aria-hidden="true"
              className="flex gap-3 border-b border-border px-3 py-4"
            >
              <span className="h-12 w-[5px] rounded-[1px] bg-muted" />
              <span className="flex flex-1 flex-col gap-2">
                <span className="h-4 w-64 animate-pulse rounded-sm bg-muted" />
                <span className="h-3 w-3/4 animate-pulse rounded-sm bg-muted" />
              </span>
            </li>
          ))}
      </ul>

      <div className="flex items-center justify-between gap-3 text-[13px] text-muted-foreground">
        <span>
          Showing {events.length.toLocaleString()}
          {hasMore ? "" : " (all)"}
        </span>
        {hasMore && (
          <Button
            size="sm"
            variant="outline"
            colorScheme="gray"
            disabled={isLoading}
            onClick={onLoadMore}
          >
            {isLoading ? "Loading…" : "Load more"}
          </Button>
        )}
      </div>
    </div>
  );
}
