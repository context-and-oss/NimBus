import type * as api from "api-client";
import { WorkspaceStat } from "components/workspace/workspace";
import { failureBacklog } from "functions/failed-messages.functions";

// The Operations page's header figures (Spec 038 §6.1). Each is also a shortcut.
//
// Status counts overlap: failedCount already includes dead-lettered and unsupported
// messages (Mapper.EndpointStatusCountFromEndpointStateCount), so Failed matches the
// sidebar's Failed badge and Dead-lettered is a subset of it.

const sum = (counts: api.EndpointStatusCount[], pick: (c: api.EndpointStatusCount) => number) =>
  counts.reduce((total, c) => total + pick(c), 0);

export interface OperationsStatusProps {
  /** Undefined while loading. */
  counts?: api.EndpointStatusCount[];
  /** Endpoints with receive or send disabled; undefined until the table has loaded them. */
  paused?: number;
  /** Opens an operation by its id. */
  onOpen: (operationId: string) => void;
  /** Filters the Endpoints table to those needing attention. */
  onShowPaused: () => void;
}

export default function OperationsStatus({ counts, paused, onOpen, onShowPaused }: OperationsStatusProps) {
  const value = (pick: (c: api.EndpointStatusCount[]) => number) => (counts ? pick(counts).toLocaleString() : "—");
  const failed = counts ? failureBacklog(counts) : 0;
  const deadLettered = counts ? sum(counts, (c) => c.deadletterCount ?? 0) : 0;

  return (
    <>
      <WorkspaceStat
        label="Failed"
        value={value(failureBacklog)}
        tone={failed > 0 ? "danger" : "default"}
        title="Open Bulk resubmit failed"
        onClick={() => onOpen("resubmit")}
      />
      <WorkspaceStat
        label="Dead-lettered"
        value={value((c) => sum(c, (x) => x.deadletterCount ?? 0))}
        tone={deadLettered > 0 ? "warn" : "default"}
        title="Open Delete dead-lettered"
        onClick={() => onOpen("dlq")}
      />
      <WorkspaceStat
        label="Paused"
        value={paused === undefined ? "—" : paused.toLocaleString()}
        tone={paused ? "warn" : "default"}
        title="Show the endpoints that need attention"
        onClick={onShowPaused}
      />
      <WorkspaceStat
        label="Pending"
        value={value((c) => sum(c, (x) => x.pendingCount ?? 0))}
        title="Open Reconcile stale pending"
        onClick={() => onOpen("stale")}
      />
    </>
  );
}
