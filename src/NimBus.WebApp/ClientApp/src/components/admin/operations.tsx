import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import { Link, Navigate, useNavigate, useParams, useSearchParams } from "react-router-dom";
import * as api from "api-client";
import { Badge, type BadgeVariant } from "components/ui/badge";
import { cn } from "lib/utils";
import {
  BulkResubmitCard,
  DeleteDeadLetteredCard,
  DeleteEventCard,
} from "./bulk-operations";
import {
  SubscriptionPurgeCard,
  DeleteByStatusCard,
  SkipMessagesCard,
  DeleteMessagesByToCard,
  CopyEndpointCard,
  DeleteAllEventsCard,
} from "./advanced-operations";
import { SessionPurgeCard } from "./session-management";
import { SessionStateCard } from "./session-state";
import { SessionCleanupCard } from "./session-cleanup";
import { StalePendingReconcileCard } from "./stale-pending-reconcile";
import { EndpointControlsCard, type EndpointChannelStatus } from "./endpoint-controls";
import OperationsStatus from "./operations-status";
import { OPERATION_GROUPS, OPERATIONS, type OperationGroupId } from "models/manage-pages";

interface EndpointOption {
  value: string;
  label: string;
}

interface CardProps {
  endpoints: EndpointOption[];
  initialEndpoint?: string;
}

/** Each operation's existing card, keyed by its URL segment. */
const CARDS: Record<string, (props: CardProps) => ReactNode> = {
  resubmit: (p) => <BulkResubmitCard {...p} />,
  skip: (p) => <SkipMessagesCard {...p} />,
  session: (p) => <SessionPurgeCard {...p} />,
  "session-state": (p) => <SessionStateCard {...p} />,
  "session-cleanup": (p) => <SessionCleanupCard {...p} />,
  stale: (p) => <StalePendingReconcileCard {...p} />,
  dlq: (p) => <DeleteDeadLetteredCard {...p} />,
  status: (p) => <DeleteByStatusCard {...p} />,
  to: () => <DeleteMessagesByToCard />,
  single: (p) => <DeleteEventCard {...p} />,
  purge: (p) => <SubscriptionPurgeCard {...p} />,
  copy: (p) => <CopyEndpointCard {...p} />,
  all: (p) => <DeleteAllEventsCard {...p} />,
};

const GROUP_MARKER: Record<OperationGroupId, string> = {
  recovery: "bg-status-success",
  cleanup: "bg-status-warning",
  movement: "bg-status-info",
  danger: "bg-status-danger",
};

const GROUP_BADGE: Record<OperationGroupId, BadgeVariant> = {
  recovery: "success",
  cleanup: "warning",
  movement: "info",
  danger: "error",
};

const operationUrl = (operationId: string, endpointId?: string) =>
  `/Operations/${operationId}${endpointId ? `?endpoint=${encodeURIComponent(endpointId)}` : ""}`;

/**
 * The Operations page body (Spec 038 §6): a status strip, the Endpoints table
 * (kill switch, counts and row actions), and one bulk operation at a time,
 * picked from a list grouped by blast radius. Each operation has a URL,
 * /Operations/:operation?endpoint=:id, and keeps its own form, preview and
 * typed confirmation.
 */
export default function Operations() {
  const { operation } = useParams();
  const [searchParams] = useSearchParams();
  const navigate = useNavigate();
  const client = useMemo(() => new api.Client(api.CookieAuth()), []);
  const [endpoints, setEndpoints] = useState<EndpointOption[]>([]);
  const [counts, setCounts] = useState<api.EndpointStatusCount[]>();
  const [statuses, setStatuses] = useState<Record<string, EndpointChannelStatus>>();
  const [attentionOnly, setAttentionOnly] = useState(false);

  const current = OPERATIONS.find((o) => o.id === (operation ?? OPERATIONS[0].id).toLowerCase());
  const endpoint = searchParams.get("endpoint") ?? undefined;

  useEffect(() => {
    client
      .getAdminPlatformConfig()
      .then((config) =>
        setEndpoints(
          (config.endpoints ?? []).map((ep) => ({ value: ep.id ?? "", label: ep.name ?? ep.id ?? "" })),
        ),
      )
      .catch(() => undefined);
  }, [client]);

  // Re-read the counts whenever the operation changes, so the strip and table
  // reflect what the last run did. Fail soft: the strip shows "—".
  const currentId = current?.id;
  useEffect(() => {
    let cancelled = false;
    client
      .getEndpointStatusCountAll()
      .then((next) => {
        if (!cancelled && Array.isArray(next)) setCounts(next);
      })
      .catch(() => undefined);
    return () => {
      cancelled = true;
    };
  }, [client, currentId]);

  const open = useCallback(
    (operationId: string, endpointId?: string) => navigate(operationUrl(operationId, endpointId)),
    [navigate],
  );

  if (!current) return <Navigate to="/Operations" replace />;

  const paused = statuses
    ? Object.values(statuses).filter((s) => s.receive === "disabled" || s.send === "disabled").length
    : undefined;
  const group = OPERATION_GROUPS.find((g) => g.id === current.group)!;

  return (
    <div className="w-full space-y-6">
      <OperationsStatus
        counts={counts}
        paused={paused}
        onOpen={(id) => open(id)}
        onShowPaused={() => setAttentionOnly(true)}
      />

      <EndpointControlsCard
        endpoints={endpoints}
        counts={counts}
        attentionOnly={attentionOnly}
        onAttentionChange={setAttentionOnly}
        onOperate={open}
        onStatusChange={setStatuses}
      />

      <div className="grid items-start gap-4 lg:grid-cols-[17rem_minmax(0,1fr)]">
        <nav
          aria-label="Operations"
          className="hidden overflow-hidden rounded-nb-md border border-border bg-card lg:sticky lg:top-20 lg:block"
        >
          {OPERATION_GROUPS.map((g) => (
            <div key={g.id} className="border-t border-border pb-1.5 first:border-t-0">
              <div className="flex items-center gap-2 px-3.5 pb-1 pt-3">
                <span aria-hidden="true" className={cn("h-2 w-2 shrink-0 rounded-sm", GROUP_MARKER[g.id])} />
                <h2 className="m-0 font-mono text-[10.5px] font-medium uppercase tracking-[0.14em] text-muted-foreground">
                  {g.label}
                </h2>
                <span className="ml-auto text-[11px] text-muted-foreground">{g.caption}</span>
              </div>
              <ul className="m-0 list-none p-0">
                {OPERATIONS.filter((o) => o.group === g.id).map((o) => {
                  const selected = o.id === current.id;
                  return (
                    <li key={o.id}>
                      <Link
                        to={operationUrl(o.id)}
                        aria-current={selected ? "page" : undefined}
                        className={cn(
                          "block py-1.5 pl-[30px] pr-3.5 text-[13.5px] no-underline hover:no-underline",
                          selected
                            ? "bg-primary/10 font-semibold text-primary shadow-[inset_3px_0_0_var(--color-primary)]"
                            : g.id === "danger"
                              ? "text-status-danger hover:bg-muted"
                              : "text-foreground hover:bg-muted",
                        )}
                      >
                        {o.label}
                      </Link>
                    </li>
                  );
                })}
              </ul>
            </div>
          ))}
        </nav>

        <div className="min-w-0 space-y-3">
          <select
            aria-label="Operation"
            className="w-full rounded-nb-md border border-border-strong bg-card px-3 py-2 text-sm lg:hidden"
            value={current.id}
            onChange={(e) => open(e.target.value)}
          >
            {OPERATION_GROUPS.map((g) => (
              <optgroup key={g.id} label={g.label}>
                {OPERATIONS.filter((o) => o.group === g.id).map((o) => (
                  <option key={o.id} value={o.id}>
                    {o.label}
                  </option>
                ))}
              </optgroup>
            ))}
          </select>

          <section aria-label={current.label} className="space-y-3">
            <div className="flex flex-wrap items-center gap-2.5">
              <Badge variant={GROUP_BADGE[current.group]}>
                {group.label} · {group.caption}
              </Badge>
              <p className="m-0 text-sm text-muted-foreground">{current.description}</p>
            </div>
            {/* Keyed so a new operation or pre-filled endpoint starts a fresh form. */}
            <div key={`${current.id}:${endpoint ?? ""}`}>
              {CARDS[current.id]({ endpoints, initialEndpoint: current.takesEndpoint ? endpoint : undefined })}
            </div>
          </section>
        </div>
      </div>
    </div>
  );
}
