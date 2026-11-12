import { useState, useEffect, useCallback, type ReactNode } from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import { Badge } from "components/ui/badge";
import { Input } from "components/ui/input";
import { Toggle } from "components/ui/toggle";
import { DropdownItem, DropdownMenu } from "components/ui/dropdown-menu";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  CardDescription,
} from "components/ui/card";
import {
  Modal,
  ModalHeader,
  ModalBody,
  ModalFooter,
} from "components/ui/modal";
import { OPERATIONS } from "models/manage-pages";
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

// The row's "⋯" menu: every operation that starts from an endpoint.
const ROW_OPERATIONS = OPERATIONS.filter((o) => o.takesEndpoint);

export interface EndpointControlsCardProps {
  endpoints: EndpointOption[];
  /** Per-endpoint backlog from getEndpointStatusCountAll; a missing row shows "—". */
  counts?: api.EndpointStatusCount[];
  /** Show only endpoints with a pause, a failure or a dead letter. */
  attentionOnly?: boolean;
  onAttentionChange?: (attentionOnly: boolean) => void;
  /** Opens a bulk operation pre-filled with the endpoint. Row actions appear only when set. */
  onOperate?: (operationId: string, endpointId: string) => void;
  /** Reports every endpoint's loaded receive and send status. */
  onStatusChange?: (statuses: Record<string, EndpointChannelStatus>) => void;
}

/**
 * The Operations page's Endpoints table (Spec 038 §6.2): each endpoint's kill
 * switch beside its backlog, with row actions that open a bulk operation.
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
  onAttentionChange,
  onOperate,
  onStatusChange,
}: EndpointControlsCardProps) {
  const [rows, setRows] = useState<Record<string, EndpointChannelStatus>>({});
  const [busy, setBusy] = useState<Record<string, boolean>>({});
  const [confirm, setConfirm] = useState<PendingConfirm | null>(null);
  const [query, setQuery] = useState("");

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

  const channelCell = (ep: EndpointOption, channel: Channel, status: Status) => (
    <td className="py-2 pr-4">
      <div className="flex items-center gap-2">
        <Toggle
          checked={status === "active"}
          showStateLabel={false}
          disabled={(status !== "active" && status !== "disabled") || (busy[ep.value] ?? false)}
          onChange={() => onToggle(ep.value, channel, status)}
          aria-label={`${channel === "receive" ? "Receive" : "Send"} ${ep.label}`}
        />
        <StatusNote status={status} />
      </div>
    </td>
  );

  const number = (value: number | undefined, tone?: string) =>
    value === undefined ? (
      <span className="text-muted-foreground">—</span>
    ) : (
      <span className={cn(value > 0 && tone)}>{value.toLocaleString()}</span>
    );

  const confirmIsSend = confirm?.channel === "send";

  return (
    <Card>
      <CardHeader className="flex flex-row flex-wrap items-start justify-between gap-3">
        <div>
          <CardTitle>Endpoints</CardTitle>
          <CardDescription>
            Switch each endpoint's <strong>receive</strong> (processing) and{" "}
            <strong>send</strong> (publishing) independently. Disabling send
            quarantines the endpoint's topic — while off, events forwarded in from
            other endpoints dead-letter at the source.
          </CardDescription>
        </div>
        <div className="flex items-center gap-2">
          <Input
            type="search"
            aria-label="Filter endpoints"
            placeholder="Filter endpoints"
            className="h-9 w-48"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
          />
          <button
            type="button"
            aria-pressed={attentionOnly}
            onClick={() => onAttentionChange?.(!attentionOnly)}
            className={cn(
              "whitespace-nowrap rounded-full border px-3 py-1 text-[12.5px] transition-colors",
              attentionOnly
                ? "border-foreground bg-foreground text-background"
                : "border-border bg-card text-muted-foreground hover:text-foreground",
            )}
          >
            Needs attention
          </button>
        </div>
      </CardHeader>
      <CardContent>
        <div className="overflow-x-auto">
          <table className="w-full text-sm">
            <thead>
              <tr className="text-left text-xs font-medium text-muted-foreground border-b border-border">
                <th className="py-2 pl-3 pr-4">Endpoint</th>
                <th className="py-2 pr-4">Receive</th>
                <th className="py-2 pr-4">Send</th>
                <th className="py-2 pr-4 text-right">Failed</th>
                <th className="py-2 pr-4 text-right">Dead-letter</th>
                <th className="py-2 pr-4 text-right">Pending</th>
                {onOperate && (
                  <th className="py-2">
                    <span className="sr-only">Actions</span>
                  </th>
                )}
              </tr>
            </thead>
            <tbody>
              {visible.map((ep) => {
                const row = rows[ep.value] ?? { receive: "loading", send: "loading" };
                const b = backlog(ep.value);
                return (
                  <tr
                    key={ep.value}
                    className={cn(
                      "border-b border-border/50",
                      needsAttention(ep.value) && "[&>td:first-child]:shadow-[inset_3px_0_0_var(--color-status-warning)]",
                    )}
                  >
                    <td className="py-2 pl-3 pr-4 font-mono text-[13px] font-medium">{ep.label}</td>
                    {channelCell(ep, "receive", row.receive)}
                    {channelCell(ep, "send", row.send)}
                    <td className="py-2 pr-4 text-right tabular-nums">
                      {number(b?.failed, "font-semibold text-status-danger")}
                    </td>
                    <td className="py-2 pr-4 text-right tabular-nums">
                      {number(b?.deadLettered, "font-semibold text-status-warning-ink")}
                    </td>
                    <td className="py-2 pr-4 text-right tabular-nums">{number(b?.pending)}</td>
                    {onOperate && (
                      <td className="py-2 text-right">
                        <div className="flex items-center justify-end gap-1.5">
                          {!!b?.failed && (
                            <Button
                              size="xs"
                              variant="outline"
                              colorScheme="gray"
                              aria-label={`Resubmit ${ep.label}…`}
                              onClick={() => onOperate("resubmit", ep.value)}
                            >
                              Resubmit…
                            </Button>
                          )}
                          <DropdownMenu
                            trigger={<span aria-hidden="true">⋯</span>}
                            triggerLabel={`More operations for ${ep.label}`}
                          >
                            {ROW_OPERATIONS.map((o) => (
                              <DropdownItem
                                key={o.id}
                                destructive={o.group === "danger"}
                                onSelect={() => onOperate(o.id, ep.value)}
                              >
                                {o.label}
                              </DropdownItem>
                            ))}
                          </DropdownMenu>
                        </div>
                      </td>
                    )}
                  </tr>
                );
              })}
              {visible.length === 0 && (
                <tr>
                  <td
                    colSpan={onOperate ? 7 : 6}
                    className="py-4 text-center text-muted-foreground"
                  >
                    {endpoints.length === 0 ? "No endpoints." : "No endpoints match."}
                  </td>
                </tr>
              )}
            </tbody>
          </table>
        </div>

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
      </CardContent>
    </Card>
  );
}
