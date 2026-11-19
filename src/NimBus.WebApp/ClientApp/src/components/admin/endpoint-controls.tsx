import { useState, useEffect, useCallback, type ReactNode } from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import { Badge } from "components/ui/badge";
import { Input } from "components/ui/input";
import { Toggle } from "components/ui/toggle";
import {
  Inspector,
  InspectorBlock,
  InspectorList,
  InspectorPortal,
  InspectorTiles,
  WorkspaceSection,
} from "components/workspace/workspace";
import {
  RowActionsCell,
  RowActionsHeader,
  RowSelect,
  type RowAction,
  selectableRowClass,
  WORKSPACE_HEAD_ROW,
  WORKSPACE_TH,
  WorkspaceTableFrame,
} from "components/workspace/selectable-row";
import {
  Modal,
  ModalHeader,
  ModalBody,
  ModalFooter,
} from "components/ui/modal";
import { OPERATION_GROUPS, OPERATIONS } from "models/manage-pages";
import { cn } from "lib/utils";

interface EndpointOption {
  value: string;
  label: string;
}

// "active" | "disabled" | "not-found" | "unknown" (probe failed) | "loading"
type Status = string;

/** One endpoint's receive (subscription) and send (topic) status. */
export interface EndpointChannelStatus {
  receive: Status;
  send: Status;
}

type Channel = "receive" | "send";
type Action = "enable" | "disable";

interface PendingConfirm {
  endpointId: string;
  channel: Channel;
}

/** Text beside a switch; a switch alone already shows on and off. */
function StatusNote({ status }: { status: Status }): ReactNode {
  switch (status) {
    case "active":
      return <span className="text-xs text-muted-foreground">On</span>;
    case "disabled":
      return <span className="text-xs font-semibold text-status-warning-ink">Paused</span>;
    case "not-found":
      return <Badge variant="warning">Missing</Badge>;
    case "loading":
      return <span className="text-xs text-muted-foreground">…</span>;
    default:
      return <Badge variant="secondary">Unknown</Badge>;
  }
}

// The inspector's list: every operation that starts from an endpoint.
const ROW_OPERATIONS = OPERATIONS.filter((o) => o.takesEndpoint);

const GROUP_CAPTION = Object.fromEntries(OPERATION_GROUPS.map((g) => [g.id, `${g.label} · ${g.caption}`]));

export interface EndpointControlsCardProps {
  endpoints: EndpointOption[];
  /** Per-endpoint backlog from getEndpointStatusCountAll; a missing row shows "—". */
  counts?: api.EndpointStatusCount[];
  /** Show only endpoints with a pause, a failure or a dead letter. */
  attentionOnly?: boolean;
  /** Opens a bulk operation pre-filled with the endpoint. The inspector lists them only when set. */
  onOperate?: (operationId: string, endpointId: string) => void;
  /** The endpoint to start with in the inspector. */
  initialSelected?: string;
  /** Keeps loading and reporting statuses but draws nothing. */
  hidden?: boolean;
  /** Reports every endpoint's loaded receive and send status. */
  onStatusChange?: (statuses: Record<string, EndpointChannelStatus>) => void;
}

/**
 * The Operations page's Endpoints table (Spec 038 §6.2): each endpoint's kill
 * switch beside its backlog. The selected endpoint shows in the inspector with
 * the operations that start from it.
 * Two independent Service Bus entity-status switches per endpoint:
 *  - Receive: the endpoint's subscription (Active ↔ ReceiveDisabled) — stops processing.
 *  - Send: the endpoint's topic (Active ↔ SendDisabled) — stops publishing.
 * Because an endpoint's topic is also where its consumed events are auto-forwarded in,
 * disabling send quarantines the topic (inbound forwards dead-letter at the source);
 * the disable-send confirm spells this out.
 */
export function EndpointControlsCard({
  endpoints,
  counts,
  attentionOnly = false,
  onOperate,
  onStatusChange,
  initialSelected,
  hidden = false,
}: EndpointControlsCardProps) {
  const [rows, setRows] = useState<Record<string, EndpointChannelStatus>>({});
  const [busy, setBusy] = useState<Record<string, boolean>>({});
  const [confirm, setConfirm] = useState<PendingConfirm | null>(null);
  const [query, setQuery] = useState("");
  const [selectedId, setSelectedId] = useState(initialSelected);

  const refreshEndpoint = useCallback(async (endpointId: string) => {
    const client = new api.Client(api.CookieAuth());
    try {
      const [receive, send] = await Promise.all([
        client.getEndpointSubscriptionstatus(endpointId).catch(() => "unknown"),
        client.getEndpointSendstatus(endpointId).catch(() => "unknown"),
      ]);
      setRows((prev) => ({ ...prev, [endpointId]: { receive, send } }));
    } catch {
      setRows((prev) => ({
        ...prev,
        [endpointId]: { receive: "unknown", send: "unknown" },
      }));
    }
  }, []);

  useEffect(() => {
    for (const ep of endpoints) {
      setRows((prev) =>
        prev[ep.value]
          ? prev
          : { ...prev, [ep.value]: { receive: "loading", send: "loading" } },
      );
      void refreshEndpoint(ep.value);
    }
  }, [endpoints, refreshEndpoint]);

  // Report only once every endpoint has finished loading, so a count of paused
  // endpoints is never computed from half-loaded rows.
  useEffect(() => {
    if (!onStatusChange || endpoints.length === 0) return;
    const loaded = endpoints.every((ep) => {
      const r = rows[ep.value];
      return r && r.receive !== "loading" && r.send !== "loading";
    });
    if (loaded) onStatusChange(Object.fromEntries(endpoints.map((ep) => [ep.value, rows[ep.value]])));
  }, [rows, endpoints, onStatusChange]);

  async function apply(endpointId: string, channel: Channel, action: Action) {
    setBusy((prev) => ({ ...prev, [endpointId]: true }));
    const client = new api.Client(api.CookieAuth());
    try {
      if (channel === "receive") {
        await client.postEndpointSubscriptionstatus(endpointId, action);
      } else {
        await client.postEndpointSendstatus(endpointId, action);
      }
      await refreshEndpoint(endpointId);
    } catch {
      // Leave the row as-is; a re-probe on next render will reconcile.
    } finally {
      setBusy((prev) => ({ ...prev, [endpointId]: false }));
    }
  }

  // Enabling is safe → apply immediately. Disabling is impactful → confirm first.
  function onToggle(endpointId: string, channel: Channel, current: Status) {
    if (current === "active") {
      setConfirm({ endpointId, channel });
    } else if (current === "disabled") {
      void apply(endpointId, channel, "enable");
    }
  }

  function confirmDisable() {
    if (!confirm) return;
    const { endpointId, channel } = confirm;
    setConfirm(null);
    void apply(endpointId, channel, "disable");
  }

  const countsById = new Map((counts ?? []).map((c) => [c.endpointId ?? "", c]));
  // failedCount already includes dead-lettered and unsupported.
  const backlog = (id: string) => {
    const c = countsById.get(id);
    return c
      ? {
          failed: c.failedCount ?? 0,
          deadLettered: c.deadletterCount ?? 0,
          pending: c.pendingCount ?? 0,
        }
      : undefined;
  };
  const needsAttention = (id: string) => {
    const r = rows[id];
    const b = backlog(id);
    return r?.receive === "disabled" || r?.send === "disabled" || !!b?.failed || !!b?.deadLettered;
  };

  const visible = endpoints.filter(
    (ep) =>
      (!attentionOnly || needsAttention(ep.value)) &&
      (!query.trim() || ep.label.toLowerCase().includes(query.trim().toLowerCase())),
  );

  const channelToggle = (ep: EndpointOption, channel: Channel, status: Status, label: string) => (
    <div className="flex items-center gap-2">
      <Toggle
        checked={status === "active"}
        showStateLabel={false}
        disabled={(status !== "active" && status !== "disabled") || (busy[ep.value] ?? false)}
        onChange={() => onToggle(ep.value, channel, status)}
        aria-label={label}
      />
      <StatusNote status={status} />
    </div>
  );

  const number = (value: number | undefined, tone?: string) =>
    value === undefined ? (
      <span className="text-muted-foreground">—</span>
    ) : (
      <span className={cn(value > 0 && tone)}>{value.toLocaleString()}</span>
    );

  const confirmIsSend = confirm?.channel === "send";
  const isPaused = (r?: EndpointChannelStatus) => r?.receive === "disabled" || r?.send === "disabled";

  // The page keeps this mounted while another tab shows, so the statuses it
  // reports stay loaded; it then draws nothing.
  if (hidden) return null;

  const selected = endpoints.find((ep) => ep.value === selectedId);
  const selectedRow = selected ? (rows[selected.value] ?? { receive: "loading", send: "loading" }) : undefined;
  const selectedBacklog = selected ? backlog(selected.value) : undefined;
  const tile = (value: number | undefined) => (value === undefined ? "—" : value.toLocaleString());
  // The operations that start from an endpoint, for its ⋯ menu.
  const rowActions = (ep: EndpointOption): RowAction[] =>
    onOperate
      ? ROW_OPERATIONS.map((o) => ({
          label: `${o.label}…`,
          destructive: o.group === "danger",
          onSelect: () => onOperate(o.id, ep.value),
        }))
      : [];

  return (
    <>
      <WorkspaceSection
        title="Endpoints"
        count={visible.length}
        intro={
          <>
            Switch each endpoint&apos;s <strong>receive</strong> (processing) and <strong>send</strong> (publishing)
            independently. Disabling send quarantines the endpoint&apos;s topic — while off, events forwarded in from
            other endpoints dead-letter at the source. Select an endpoint to run an operation on it.
          </>
        }
        action={
          <Input
            type="search"
            aria-label="Filter endpoints"
            placeholder="Filter endpoints"
            className="h-9 w-48"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
        }
      >
        <WorkspaceTableFrame>
          <table className="w-full min-w-[40rem] text-sm" aria-label="Endpoints">
            <thead>
              <tr className={WORKSPACE_HEAD_ROW}>
                <th scope="col" className={WORKSPACE_TH}>Endpoint</th>
                <th scope="col" className={cn(WORKSPACE_TH, "text-right")}>Failed</th>
                <th scope="col" className={cn(WORKSPACE_TH, "text-right")}>Dead-letter</th>
                <th scope="col" className={cn(WORKSPACE_TH, "text-right")}>Pending</th>
                <th scope="col" className={WORKSPACE_TH}>Receive</th>
                <th scope="col" className={WORKSPACE_TH}>Send</th>
                {onOperate && <RowActionsHeader />}
              </tr>
            </thead>
            <tbody>
              {visible.map((ep) => {
                const row = rows[ep.value] ?? { receive: "loading", send: "loading" };
                const b = backlog(ep.value);
                const isSelected = ep.value === selectedId;
                return (
                  <tr key={ep.value} className={selectableRowClass(isSelected)}>
                    <td className="px-4">
                      <RowSelect
                        mono
                        selected={isSelected}
                        onSelect={() => setSelectedId(ep.value)}
                        after={
                          isPaused(row) && (
                            <span className="text-[11px] font-semibold text-status-warning-ink">Paused</span>
                          )
                        }
                      >
                        {ep.label}
                      </RowSelect>
                    </td>
                    <td className="px-4 py-2 text-right tabular-nums">
                      {number(b?.failed, "font-semibold text-status-danger")}
                    </td>
                    <td className="px-4 py-2 text-right tabular-nums">
                      {number(b?.deadLettered, "font-semibold text-status-warning-ink")}
                    </td>
                    <td className="px-4 py-2 text-right tabular-nums">{number(b?.pending)}</td>
                    <td className="px-4 py-2">{channelToggle(ep, "receive", row.receive, `Receive ${ep.label}`)}</td>
                    <td className="px-4 py-2">{channelToggle(ep, "send", row.send, `Send ${ep.label}`)}</td>
                    {onOperate && <RowActionsCell label={`Operations on ${ep.label}`} actions={rowActions(ep)} />}
                  </tr>
                );
              })}
              {visible.length === 0 && (
                <tr>
                  <td colSpan={onOperate ? 7 : 6} className="py-4 text-center text-muted-foreground">
                    {endpoints.length === 0
                      ? "No endpoints."
                      : attentionOnly && !query.trim()
                        ? "Nothing needs attention."
                        : "No endpoints match."}
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </WorkspaceTableFrame>
      </WorkspaceSection>

      <InspectorPortal>
        {selected && selectedRow ? (
          <Inspector
            eyebrow="Endpoint"
            title={<span className="font-mono text-[16px]">{selected.label}</span>}
            state={
              isPaused(selectedRow) ? (
                <Badge variant="warning">Paused</Badge>
              ) : selectedRow.receive === "active" && selectedRow.send === "active" ? (
                <Badge variant="success">Running</Badge>
              ) : undefined
            }
            description={
              isPaused(selectedRow)
                ? "A paused endpoint keeps its messages in Service Bus until it is switched back on."
                : undefined
            }
            onClose={() => setSelectedId(undefined)}
          >
            <InspectorTiles
              tiles={[
                { label: "Failed", value: tile(selectedBacklog?.failed), tone: selectedBacklog?.failed ? "danger" : "default" },
                {
                  label: "Dead-letter",
                  value: tile(selectedBacklog?.deadLettered),
                  tone: selectedBacklog?.deadLettered ? "warn" : "default",
                },
                { label: "Pending", value: tile(selectedBacklog?.pending) },
              ]}
            />
            <InspectorBlock label="Kill switch">
              <div className="divide-y divide-border rounded-nb-md border border-border bg-background">
                <div className="flex items-center justify-between gap-3 px-3 py-2">
                  <span className="text-[13.5px] font-semibold">Receive</span>
                  {channelToggle(selected, "receive", selectedRow.receive, `${selected.label} receives`)}
                </div>
                <div className="flex items-center justify-between gap-3 px-3 py-2">
                  <span className="text-[13.5px] font-semibold">Send</span>
                  {channelToggle(selected, "send", selectedRow.send, `${selected.label} sends`)}
                </div>
              </div>
            </InspectorBlock>
            {onOperate && (
              <InspectorList
                label="Operations on this endpoint"
                items={ROW_OPERATIONS.map((o) => ({
                  key: o.id,
                  label: o.label,
                  meta: GROUP_CAPTION[o.group],
                  danger: o.group === "danger",
                  actionLabel: `Open ${o.label} for ${selected.label}`,
                  onAction: () => onOperate(o.id, selected.value),
                }))}
              />
            )}
          </Inspector>
        ) : (
          <Inspector empty="Select an endpoint to see its switches and the operations that start from it." />
        )}
      </InspectorPortal>

      <Modal
        isOpen={confirm !== null}
        onClose={() => setConfirm(null)}
        label={`Disable ${confirmIsSend ? "send" : "receive"} for ${confirm?.endpointId}?`}
      >
        <ModalHeader onClose={() => setConfirm(null)}>
          Disable {confirmIsSend ? "send" : "receive"} for {confirm?.endpointId}?
        </ModalHeader>
        <ModalBody>
          {confirmIsSend ? (
            <p className="text-sm text-muted-foreground m-0">
              This sets the <strong>{confirm?.endpointId}</strong> topic to
              <span className="font-mono"> SendDisabled</span>: the endpoint can
              no longer publish. Note the topic is also this endpoint's inbox —
              while send is disabled, events auto-forwarded in from other
              endpoints will dead-letter at the source. Re-enabling restores
              normal flow.
            </p>
          ) : (
            <p className="text-sm text-muted-foreground m-0">
              This sets the <strong>{confirm?.endpointId}</strong> subscription to
              <span className="font-mono"> ReceiveDisabled</span>: the endpoint
              stops processing messages. Messages accumulate on the subscription
              until it is re-enabled.
            </p>
          )}
        </ModalBody>
        <ModalFooter>
          <Button
            variant="ghost"
            colorScheme="gray"
            onClick={() => setConfirm(null)}
          >
            Cancel
          </Button>
          <Button variant="solid" colorScheme="red" onClick={confirmDisable}>
            Disable {confirmIsSend ? "send" : "receive"}
          </Button>
        </ModalFooter>
      </Modal>
    </>
  );
}
