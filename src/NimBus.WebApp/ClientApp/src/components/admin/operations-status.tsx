import type * as api from "api-client";
import { StatRow, StatTile } from "components/ui/stat-tile";
import { failureBacklog } from "functions/failed-messages.functions";

// The Operations page's status strip (Spec 038 §6.1). Each tile is also a shortcut.
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
    <StatRow className="max-lg:grid-cols-2">
      <StatTile
        label="Failed"
        value={value(failureBacklog)}
        tone={failed > 0 ? "danger" : "muted"}
        delta={failed > 0 ? "Resubmit →" : "None"}
        onClick={() => onOpen("resubmit")}
      />
      <StatTile
        label="Dead-lettered"
        value={value((c) => sum(c, (x) => x.deadletterCount ?? 0))}
        tone={deadLettered > 0 ? "warning" : "muted"}
        delta={deadLettered > 0 ? "Delete or resubmit →" : "None"}
        onClick={() => onOpen("dlq")}
      />
      <StatTile
        label="Endpoints paused"
        value={paused === undefined ? "—" : paused.toLocaleString()}
        tone={paused ? "warning" : "muted"}
        delta={paused ? "Show them" : "All receiving and sending"}
        onClick={onShowPaused}
      />
      <StatTile
        label="Pending"
        value={value((c) => sum(c, (x) => x.pendingCount ?? 0))}
        tone="muted"
        delta="Reconcile if stuck →"
        onClick={() => onOpen("stale")}
      />
    </StatRow>
  );
}
