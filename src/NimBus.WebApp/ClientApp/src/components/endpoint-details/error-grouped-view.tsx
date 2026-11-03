import * as React from "react";
import moment from "moment";
import TruncatedGuid from "components/common/truncated-guid";
import * as api from "api-client";
import { Badge } from "components/ui/badge";
import { Button } from "components/ui/button";
import { normalizeErrorPattern } from "functions/error-normalization";
import {
  formatMoment,
  formatResolutionStatus,
} from "functions/endpoint.functions";

interface ErrorGroupedViewProps {
  events: api.Event[];
  onResubmitEvent: (event: api.Event) => void;
  onSkipEvent: (event: api.Event) => void;
  isActionableStatus: (status: string | undefined) => boolean;
}

interface ErrorGroup {
  pattern: string;
  events: api.Event[];
  eventTypes: string[];
  latestOccurrence: string;
}

const ErrorGroupedView = ({
  events,
  onResubmitEvent,
  onSkipEvent,
  isActionableStatus,
}: ErrorGroupedViewProps) => {
  const [expanded, setExpanded] = React.useState<Set<number>>(new Set());

  const toggleExpand = (idx: number) => {
    setExpanded((prev) => {
      const next = new Set(prev);
      next.has(idx) ? next.delete(idx) : next.add(idx);
      return next;
    });
  };

  const groups = React.useMemo(() => {
    const map = new Map<string, api.Event[]>();
    for (const event of events) {
      if (
        !["failed", "deadlettered", "unsupported"].includes(
          (event.resolutionStatus ?? "").toLowerCase(),
        )
      )
        continue;
      const errorText =
        event.messageContent?.errorContent?.errorText ?? undefined;
      const pattern = normalizeErrorPattern(errorText);
      const existing = map.get(pattern);
      if (existing) {
        existing.push(event);
      } else {
        map.set(pattern, [event]);
      }
    }

    const result: ErrorGroup[] = [];
    for (const [pattern, groupEvents] of map) {
      const eventTypes = [
        ...new Set(
          groupEvents.map((e) => e.eventTypeId).filter((t): t is string => !!t),
        ),
      ];

      let latest = "";
      for (const e of groupEvents) {
        const ts = e.updatedAt
          ? typeof e.updatedAt.format === "function"
            ? e.updatedAt.toISOString()
            : ""
          : "";
        if (ts > latest) latest = ts;
      }

      result.push({
        pattern,
        events: groupEvents,
        eventTypes,
        latestOccurrence: latest,
      });
    }

    result.sort((a, b) => b.events.length - a.events.length);
    return result;
  }, [events]);

  const handleResubmitAll = (group: ErrorGroup, e: React.MouseEvent) => {
    e.stopPropagation();
    const actionable = group.events.filter((ev) =>
      isActionableStatus(ev.resolutionStatus),
    );
    actionable.forEach((ev) => onResubmitEvent(ev));
  };

  const handleSkipAll = (group: ErrorGroup, e: React.MouseEvent) => {
    e.stopPropagation();
    const actionable = group.events.filter((ev) =>
      isActionableStatus(ev.resolutionStatus),
    );
    actionable.forEach((ev) => onSkipEvent(ev));
  };

  if (groups.length === 0) {
    return (
      <p className="text-muted-foreground text-sm py-4">
        No failed events to group.
      </p>
    );
  }

  return (
    <div className="flex flex-col gap-1">
      <p className="text-xs text-muted-foreground mb-2">
        {groups.reduce((total, group) => total + group.events.length, 0)} events
        in {groups.length} error groups
      </p>
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b">
            <th className="text-left py-2 px-3">Error Pattern</th>
            <th className="text-right py-2 px-3 w-20">Count</th>
            <th className="text-left py-2 px-3">Event Types</th>
            <th className="text-left py-2 px-3 w-40">Latest</th>
            <th className="text-right py-2 px-3 w-48">Actions</th>
          </tr>
        </thead>
        <tbody>
          {groups.map((group, i) => {
            const isExpanded = expanded.has(i);
            const actionableCount = group.events.filter((ev) =>
              isActionableStatus(ev.resolutionStatus),
            ).length;

            return (
              <React.Fragment key={i}>
                <tr
                  className="border-b cursor-pointer hover:bg-accent"
                  onClick={() => toggleExpand(i)}
                >
                  <td
                    className="py-2 px-3 font-mono text-xs max-w-md truncate"
                    title={group.pattern}
                  >
                    <span className="inline-flex items-center gap-1">
                      <span className="text-muted-foreground text-[10px] w-3">
                        {isExpanded ? "\u25BC" : "\u25B6"}
                      </span>
                      {group.pattern}
                    </span>
                  </td>
                  <td className="text-right py-2 px-3">
                    <Badge variant="error">{group.events.length}</Badge>
                  </td>
                  <td className="py-2 px-3">
                    <div className="flex flex-wrap gap-1">
                      {group.eventTypes.map((et) => (
                        <Badge key={et} variant="info">
                          {et}
                        </Badge>
                      ))}
                    </div>
                  </td>
                  <td className="py-2 px-3 whitespace-nowrap text-xs">
                    {group.latestOccurrence
                      ? formatMoment(moment(group.latestOccurrence), true)
                      : "-"}
                  </td>
                  <td className="text-right py-2 px-3">
                    {actionableCount > 0 && (
                      <span className="inline-flex gap-1">
                        <Button
                          size="xs"
                          colorScheme="blue"
                          onClick={(e) => handleResubmitAll(group, e)}
                        >
                          Resubmit All
                        </Button>
                        <Button
                          size="xs"
                          variant="outline"
                          colorScheme="red"
                          onClick={(e) => handleSkipAll(group, e)}
                        >
                          Skip All
                        </Button>
                      </span>
                    )}
                  </td>
                </tr>
                {isExpanded && (
                  <tr>
                    <td colSpan={5} className="p-0">
                      <div className="max-h-96 overflow-y-auto bg-muted/30 border-b">
                        <table className="w-full text-xs">
                          <thead>
                            <tr className="border-b">
                              <th className="text-left py-1 px-3 pl-10">
                                Event Id
                              </th>
                              <th className="text-left py-1 px-3">Status</th>
                              <th className="text-left py-1 px-3">
                                Event Type
                              </th>
                              <th className="text-left py-1 px-3">Updated</th>
                              <th className="text-right py-1 px-3">Actions</th>
                            </tr>
                          </thead>
                          <tbody>
                            {group.events.map((ev) => (
                              <tr
                                key={ev.eventId}
                                className="border-b last:border-b-0 hover:bg-accent/50"
                              >
                                <td className="py-1 px-3 pl-10">
                                  <TruncatedGuid
                                    guid={ev.eventId}
                                    linkTo={`/Message/Index/${ev.endpointId}/${ev.eventId}/0`}
                                  />
                                </td>
                                <td className="py-1 px-3">
                                  {formatResolutionStatus(
                                    ev.resolutionStatus,
                                    ev.reason,
                                  )}
                                </td>
                                <td className="py-1 px-3">{ev.eventTypeId}</td>
                                <td className="py-1 px-3">
                                  {formatMoment(ev.updatedAt, true)}
                                </td>
                                <td className="text-right py-1 px-3">
                                  {isActionableStatus(ev.resolutionStatus) && (
                                    <span className="inline-flex gap-1">
                                      <Button
                                        size="xs"
                                        colorScheme="blue"
                                        onClick={(e) => {
                                          e.stopPropagation();
                                          onResubmitEvent(ev);
                                        }}
                                      >
                                        Resubmit
                                      </Button>
                                      <Button
                                        size="xs"
                                        variant="outline"
                                        colorScheme="red"
                                        onClick={(e) => {
                                          e.stopPropagation();
                                          onSkipEvent(ev);
                                        }}
                                      >
                                        Skip
                                      </Button>
                                    </span>
                                  )}
                                </td>
                              </tr>
                            ))}
                          </tbody>
                        </table>
                      </div>
                    </td>
                  </tr>
                )}
              </React.Fragment>
            );
          })}
        </tbody>
      </table>
    </div>
  );
};

export default ErrorGroupedView;
