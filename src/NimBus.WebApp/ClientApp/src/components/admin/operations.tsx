import { useCallback, useEffect, useMemo, useState, type ReactNode } from "react";
import { Navigate, useNavigate, useParams, useSearchParams } from "react-router-dom";
import * as api from "api-client";
import { Badge, type BadgeVariant } from "components/ui/badge";
import {
  Inspector,
  Workspace,
  WorkspaceChip,
  WorkspaceSection,
  WorkspaceTabs,
  type ChipTone,
} from "components/workspace/workspace";
import {
  RowSelect,
  selectableRowClass,
  WORKSPACE_HEAD_ROW,
  WORKSPACE_TH,
  WorkspaceTableFrame,
} from "components/workspace/selectable-row";
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

const GROUP_TONE: Record<OperationGroupId, ChipTone> = {
  recovery: "ok",
  cleanup: "warn",
  movement: "info",
  danger: "danger",
};

const GROUP_BADGE: Record<OperationGroupId, BadgeVariant> = {
  recovery: "success",
  cleanup: "warning",
  movement: "info",
  danger: "error",
};

const TABS = [
  { id: "endpoints", label: "Endpoints" },
  { id: "operations", label: "Operations", count: OPERATIONS.length },
] as const;

const operationUrl = (operationId: string, endpointId?: string) =>
  `/Operations/${operationId}${endpointId ? `?endpoint=${encodeURIComponent(endpointId)}` : ""}`;

/**
 * The Operations page (Spec 038 §6) as a workspace with two tabs. Endpoints,
 * at /Operations, is the kill switch and each endpoint's backlog. Operations,
 * at /Operations/:operation?endpoint=:id, lists the bulk operations by blast
 * radius; the selected one runs from the inspector and keeps its own form,
 * preview and typed confirmation.
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
  const [group, setGroup] = useState<OperationGroupId | "all">("all");
  // The operation whose drawer was closed; the list stays on its URL.
  const [closed, setClosed] = useState<string>();

  const current = operation ? OPERATIONS.find((o) => o.id === operation.toLowerCase()) : undefined;
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

  // Re-read the counts whenever the tab or operation changes, so the header
  // and table reflect what the last run did. Fail soft: the figures show "—".
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
    (operationId: string, endpointId?: string) => {
      setClosed(undefined);
      navigate(operationUrl(operationId, endpointId));
    },
    [navigate],
  );

  if (operation && !current) return <Navigate to="/Operations" replace />;

  const paused = statuses
    ? Object.values(statuses).filter((s) => s.receive === "disabled" || s.send === "disabled").length
    : undefined;
  const currentGroup = current && OPERATION_GROUPS.find((g) => g.id === current.group)!;
  const drawerKey = current ? `${current.id}:${endpoint ?? ""}` : undefined;
  const drawerOpen = drawerKey !== undefined && drawerKey !== closed;
  const listed = OPERATIONS.filter((o) => group === "all" || o.group === group);

  return (
    <Workspace
      title="Operations"
      subtitle="Act on endpoints and stored messages. Some actions are irreversible."
      chipsLabel={current ? "Group" : "Show"}
      chips={
        current ? (
          <>
            <WorkspaceChip selected={group === "all"} onSelect={() => setGroup("all")} count={OPERATIONS.length}>
              All
            </WorkspaceChip>
            {OPERATION_GROUPS.map((g) => (
              <WorkspaceChip
                key={g.id}
                tone={GROUP_TONE[g.id]}
                title={g.caption}
                selected={group === g.id}
                onSelect={() => setGroup(g.id)}
                count={OPERATIONS.filter((o) => o.group === g.id).length}
              >
                {g.label}
              </WorkspaceChip>
            ))}
          </>
        ) : (
          <>
            <WorkspaceChip selected={!attentionOnly} onSelect={() => setAttentionOnly(false)}>
              All endpoints
            </WorkspaceChip>
            <WorkspaceChip tone="warn" selected={attentionOnly} onSelect={() => setAttentionOnly(true)}>
              Needs attention
            </WorkspaceChip>
          </>
        )
      }
      stats={
        <OperationsStatus
          counts={counts}
          paused={paused}
          onOpen={(id) => open(id)}
          onShowPaused={() => {
            setAttentionOnly(true);
            navigate("/Operations");
          }}
        />
      }
      tabs={
        <WorkspaceTabs
          label="Operations views"
          tabs={TABS}
          active={current ? "operations" : "endpoints"}
          onChange={(id) => navigate(id === "endpoints" ? "/Operations" : operationUrl(OPERATIONS[0].id, endpoint))}
        />
      }
      inspectorSlot={!current}
      inspector={
        current && currentGroup && drawerOpen ? (
          <Inspector
            eyebrow="Operation"
            title={current.label}
            badge={<Badge variant={GROUP_BADGE[current.group]}>{currentGroup.label}</Badge>}
            description={
              <>
                {current.description} <span className="text-muted-foreground">{currentGroup.caption}.</span>
              </>
            }
            onClose={() => setClosed(drawerKey)}
          >
            {/* Keyed so a new operation or pre-filled endpoint starts a fresh form.
                A container, so each form lays out for the inspector's width. */}
            <div key={`${current.id}:${endpoint ?? ""}`} className="@container">
              {CARDS[current.id]({ endpoints, initialEndpoint: current.takesEndpoint ? endpoint : undefined })}
            </div>
          </Inspector>
        ) : undefined
      }
      wideInspector={!!current}
    >
      {/* Mounted on both tabs, so the Paused figure stays loaded. */}
      <EndpointControlsCard
        endpoints={endpoints}
        counts={counts}
        attentionOnly={attentionOnly}
        onOperate={open}
        onStatusChange={setStatuses}
        initialSelected={endpoint}
        hidden={!!current}
      />

      {current && (
        <WorkspaceSection
          title="Operations"
          count={listed.length}
          intro="Grouped by blast radius, safest first. Select one to preview and run it."
          foot="Every run is audit-logged."
        >
          <WorkspaceTableFrame>
            <table className="w-full min-w-[34rem] text-[13px]" aria-label="Operations">
              <thead>
                <tr className={WORKSPACE_HEAD_ROW}>
                  <th scope="col" className={WORKSPACE_TH}>Operation</th>
                  <th scope="col" className={WORKSPACE_TH}>Group</th>
                  <th scope="col" className={WORKSPACE_TH}>What it does</th>
                </tr>
              </thead>
              <tbody>
                {listed.map((o) => {
                  const g = OPERATION_GROUPS.find((x) => x.id === o.group)!;
                  const selected = o.id === current.id && drawerOpen;
                  return (
                    <tr key={o.id} className={selectableRowClass(selected)}>
                      <td className="whitespace-nowrap px-4">
                        <RowSelect selected={selected} onSelect={() => open(o.id, endpoint)}>
                          {o.label}
                        </RowSelect>
                      </td>
                      <td className="whitespace-nowrap px-4">
                        <Badge variant={GROUP_BADGE[o.group]} withDot={false}>
                          {g.label}
                        </Badge>
                      </td>
                      <td className="px-4 py-2 text-muted-foreground">{o.description}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </WorkspaceTableFrame>
        </WorkspaceSection>
      )}
    </Workspace>
  );
}
