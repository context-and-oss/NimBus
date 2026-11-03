import ResubmitEditor from "components/event-details/resubmit-editor";
import { getResubmitPayload } from "functions/resubmit.functions";
import * as React from "react";
import * as api from "api-client";
import { Link } from "react-router-dom";
import moment from "moment";
import { formatMoment } from "functions/endpoint.functions";
import IntelligenceCard from "components/event-details/intelligence-card";
import { Badge } from "components/ui/badge";
import { Button } from "components/ui/button";
import { TimingBar } from "components/ui/timing-bar";
import FlowGantt from "components/failed-messages/flow-gantt";
import type { FailureAction } from "components/failed-messages/failed-item-list";
import {
  STATUS_COLORS,
  errorTextOf,
} from "functions/failed-messages.functions";
import {
  buildFlowRows,
  formatSpan,
  type FlowRow,
} from "functions/flow-gantt.functions";
import { notifyError, notifySuccess } from "functions/notifications.functions";
import { copyToClipboard } from "lib/clipboard";

// Blocked siblings shown in the panel; Event Details pages through the rest.
const BLOCKED_PREVIEW = 5;

export interface FailureDetailPanelProps {
  endpointId: string;
  eventId: string;
  /** 1-based place of this failure in the loaded list, when it is in it. */
  position?: { index: number; total: number; more: boolean };
  onPrevious?: () => void;
  onNext?: () => void;
  onClose: () => void;
  onAct: (action: FailureAction, event: api.Event) => void;
  /** Shows this failure's endpoint in the Group by error view. */
  onShowSimilar: (event: api.Event) => void;
}

interface PanelData {
  event: api.Event;
  history: api.Message[];
  audits: api.MessageAudit[];
  blocked?: api.BlockedEventsPage;
}

const stamp = (m: moment.Moment | undefined, withMs = false) =>
  m ? formatMoment(m, !withMs) : "—";

const prettyJson = (raw: string | undefined) => {
  if (!raw) return undefined;
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
};

const isFailure = (status: string | undefined) =>
  ["failed", "deadlettered", "unsupported"].includes(
    (status ?? "").toLowerCase(),
  );

/** The entry the panel opens on: the latest error, else the latest entry. */
export function defaultFlowRow(rows: FlowRow[]): FlowRow | undefined {
  return [...rows].reverse().find((r) => r.kind === "error") ?? rows.at(-1);
}

/**
 * One failure in place, after Application Insights' end-to-end transaction details: its
 * message flow on a time axis, the selected entry's exception, properties, blocked siblings
 * and payload, with Resubmit and Skip.
 */
export default function FailureDetailPanel({
  endpointId,
  eventId,
  position,
  onPrevious,
  onNext,
  onClose,
  onAct,
  onShowSimilar,
}: FailureDetailPanelProps) {
  const routeGeneration = React.useRef(0);
  const [editorOpen, setEditorOpen] = React.useState(false);
  const [eventTypes, setEventTypes] = React.useState<api.EventType[]>([]);
  const [acceptedFor, setAcceptedFor] = React.useState<string>();
  const [data, setData] = React.useState<PanelData>();
  const [loadError, setLoadError] = React.useState<string>();
  const [selectedKey, setSelectedKey] = React.useState<string>();

  React.useEffect(() => {
    let cancelled = false;
    const generation = ++routeGeneration.current;
    setData(undefined);
    setEditorOpen(false);
    setEventTypes([]);
    setAcceptedFor(undefined);
    setLoadError(undefined);
    setSelectedKey(undefined);
    const client = new api.Client(api.CookieAuth());
    (async () => {
      try {
        const [event, history, audits] = await Promise.all([
          client.getEventId(eventId, endpointId),
          client.getEventDetailsHistoryId(eventId, endpointId).catch(() => []),
          client.getMessageAuditsEventId(eventId).catch(() => []),
        ]);
        const blocked =
          isFailure(event.resolutionStatus) && event.sessionId
            ? await client
                .getEventBlockedId(
                  event.endpointId ?? endpointId,
                  event.sessionId,
                  0,
                  BLOCKED_PREVIEW,
                )
                .catch(() => undefined)
            : undefined;
        if (!cancelled) setData({ event, history, audits, blocked });
      } catch (e) {
        if (!cancelled)
          setLoadError(
            e instanceof Error ? e.message : "The failure could not be loaded.",
          );
      }
    })();
    return () => {
      cancelled = true;
      routeGeneration.current = generation + 1;
    };
  }, [endpointId, eventId]);

  React.useEffect(() => {
    if (!editorOpen) return;
    let active = true;
    new api.Client(api.CookieAuth())
      .getEventTypes()
      .then((types) => {
        if (active) setEventTypes(types);
      })
      .catch(() => {});
    return () => {
      active = false;
    };
  }, [editorOpen]);

  const flow = React.useMemo(
    () => buildFlowRows(data?.history ?? [], data?.audits ?? []),
    [data],
  );
  const selected =
    flow.rows.find((r) => r.key === selectedKey) ?? defaultFlowRow(flow.rows);

  const event = data?.event;
  const status = event?.resolutionStatus ?? "";
  const fullPage = `/Message/Index/${endpointId}/${eventId}/0?from=failed`;
  const blockedTotal = data?.blocked?.total ?? 0;
  const rawPayload =
    getResubmitPayload(data?.history) ??
    event?.messageContent?.eventContent?.eventJson;
  const payload = prettyJson(rawPayload);
  const canAct =
    !!event?.lastMessageId &&
    isFailure(status) &&
    acceptedFor !== event.lastMessageId;
  const resubmitWithChanges = async (body: api.ResubmitWithChanges) => {
    if (!event?.lastMessageId) return;
    const generation = routeGeneration.current;
    await new api.Client(api.CookieAuth()).postResubmitWithChangesEventIds(
      eventId,
      event.lastMessageId,
      body,
    );
    if (generation !== routeGeneration.current) return;
    setAcceptedFor(event.lastMessageId);
    setEditorOpen(false);
  };

  const navButton =
    "inline-flex h-8 w-8 items-center justify-center rounded-nb-sm border border-border-strong text-foreground hover:bg-muted disabled:cursor-not-allowed disabled:opacity-40";

  return (
    <>
      <header className="flex items-start justify-between gap-4 border-b border-border px-6 pb-3.5 pt-4">
        <div className="flex min-w-0 flex-col gap-1">
          <div className="font-mono text-[11px] text-muted-foreground">
            Failed / Transaction details
          </div>
          <h2 className="m-0 flex items-center gap-2.5 text-[21px] font-extrabold">
            <span
              aria-hidden="true"
              className="inline-block h-2.5 w-2.5 shrink-0 rounded-full"
              style={{
                background: STATUS_COLORS[status] ?? STATUS_COLORS.Failed,
              }}
            />
            <span className="truncate">
              {event?.eventTypeId ?? "Loading…"}
              {status && ` — ${status.toUpperCase()}`}
            </span>
          </h2>
          <div className="text-[13px] text-muted-foreground">
            Event <span className="font-mono text-foreground">{eventId}</span>{" "}
            on <b className="text-foreground">{endpointId}</b>
          </div>
        </div>
        <div className="flex shrink-0 items-center gap-1.5">
          <button
            type="button"
            aria-label="Previous failure"
            disabled={!onPrevious}
            onClick={onPrevious}
            className={navButton}
          >
            <svg
              width="14"
              height="14"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2.2"
              strokeLinecap="round"
              strokeLinejoin="round"
              aria-hidden="true"
            >
              <path d="M18 15l-6-6-6 6" />
            </svg>
          </button>
          <button
            type="button"
            aria-label="Next failure"
            disabled={!onNext}
            onClick={onNext}
            className={navButton}
          >
            <svg
              width="14"
              height="14"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2.2"
              strokeLinecap="round"
              strokeLinejoin="round"
              aria-hidden="true"
            >
              <path d="M6 9l6 6 6-6" />
            </svg>
          </button>
          {position && (
            <span className="px-1 font-mono text-[12px] text-muted-foreground">
              {position.index} of {position.total}
              {position.more ? "+" : ""}
            </span>
          )}
          <Link
            to={fullPage}
            className="px-2 text-[13px] font-semibold text-primary-700 hover:underline"
          >
            Open full page
          </Link>
          <button
            type="button"
            aria-label="Close transaction details"
            onClick={onClose}
            className="inline-flex h-8 w-8 items-center justify-center rounded-nb-sm text-foreground hover:bg-muted"
          >
            <svg
              width="16"
              height="16"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2.2"
              strokeLinecap="round"
              aria-hidden="true"
            >
              <path d="M6 6l12 12M18 6L6 18" />
            </svg>
          </button>
        </div>
      </header>

      <div className="flex flex-wrap items-center gap-2 border-b border-border bg-background px-6 py-3">
        <Button
          size="sm"
          colorScheme="primary"
          disabled={!canAct}
          onClick={() => event && onAct("Resubmit", event)}
        >
          Resubmit
        </Button>
        <Button
          size="sm"
          variant="outline"
          colorScheme="gray"
          disabled={!canAct}
          onClick={() => setEditorOpen(true)}
        >
          Resubmit with changes…
        </Button>
        <Button
          size="sm"
          variant="outline"
          colorScheme="gray"
          disabled={!canAct}
          onClick={() => event && onAct("Skip", event)}
        >
          Skip
        </Button>
        <span className="flex-1" />
        {blockedTotal > 0 && (
          <Badge variant="warning" size="md" withDot={false}>
            {blockedTotal} blocked in session
          </Badge>
        )}
        {(event?.resubmitCount ?? 0) > 0 && (
          <Badge variant="info" size="md" withDot={false}>
            resubmitted {event?.resubmitCount}×
          </Badge>
        )}
        {event?.retryLimit !== undefined && event.retryLimit > 0 && (
          <Badge variant="default" size="md" withDot={false}>
            retries {event.retryCount ?? 0} / {event.retryLimit}
          </Badge>
        )}
      </div>

      {editorOpen && (
        <ResubmitEditor
          payload={rawPayload}
          eventTypeId={event?.eventTypeId}
          eventTypes={eventTypes}
          onClose={() => setEditorOpen(false)}
          onSubmit={resubmitWithChanges}
        />
      )}
      {acceptedFor && (
        <p role="status" className="px-6 py-3 text-sm">
          Resubmission accepted. Processing continues in the background. Refresh
          the list to see the latest attempt.
        </p>
      )}
      <div className="flex flex-1 flex-col gap-4 overflow-y-auto px-6 pb-6 pt-4">
        {loadError ? (
          <div role="alert" className="rounded-nb-md border border-border p-5">
            <p className="m-0 font-semibold">The failure could not be loaded</p>
            <p className="m-0 mt-1 text-sm text-muted-foreground">
              {loadError}
            </p>
          </div>
        ) : !data ? (
          <div aria-label="Loading failure" className="flex flex-col gap-3">
            <div className="h-48 animate-pulse rounded-nb-md bg-muted" />
            <div className="h-40 animate-pulse rounded-nb-md bg-muted" />
          </div>
        ) : (
          <>
            <FlowGantt
              rows={flow.rows}
              spanMs={flow.spanMs}
              hidden={flow.hidden}
              selectedKey={selected?.key}
              onSelect={(row) => setSelectedKey(row.key)}
              fullHistoryHref={fullPage}
            />

            <div className="grid grid-cols-1 gap-4 lg:grid-cols-[minmax(0,1fr)_360px]">
              <SelectedEntry row={selected} event={data.event} />
              <Properties event={data.event} />
            </div>

            <div className="grid grid-cols-1 gap-4 lg:grid-cols-[minmax(0,1fr)_360px]">
              <section
                aria-label="Similar failures"
                className="flex flex-col gap-2 rounded-nb-md border border-border bg-background p-3.5"
              >
                <h3 className="m-0 text-[14.5px] font-bold">
                  Similar failures
                </h3>
                {data.event.lastMessageId && (
                  <IntelligenceCard
                    endpointId={data.event.endpointId ?? endpointId}
                    eventId={eventId}
                    messageId={data.event.lastMessageId}
                    resolutionStatus={status}
                  />
                )}
                <p className="m-0 text-[13px] text-muted-foreground">
                  Failures with the same error pattern are grouped together on
                  this page.
                </p>
                <button
                  type="button"
                  onClick={() => onShowSimilar(data.event)}
                  className="self-start text-[13px] font-semibold text-primary-700 hover:underline"
                >
                  Show {endpointId}&apos;s failures grouped by error →
                </button>
              </section>
              <BlockedInSession
                page={data.blocked}
                endpointId={data.event.endpointId ?? endpointId}
                sessionId={data.event.sessionId}
              />
            </div>

            <section
              aria-label="Payload"
              className="rounded-nb-md border border-border bg-background"
            >
              <div className="flex items-center gap-2.5 border-b border-border px-3.5 py-2.5">
                <h3 className="m-0 flex-1 text-[14.5px] font-bold">Payload</h3>
                {payload && (
                  <Button
                    size="xs"
                    variant="outline"
                    colorScheme="gray"
                    onClick={() =>
                      copyToClipboard(payload).then(
                        () => notifySuccess("Payload copied."),
                        () => notifyError("Clipboard access is not available."),
                      )
                    }
                  >
                    Copy
                  </Button>
                )}
              </div>
              {payload ? (
                <pre className="m-0 max-h-[320px] overflow-auto px-3.5 py-3 font-mono text-[12px] leading-relaxed text-foreground">
                  {payload}
                </pre>
              ) : (
                <p className="m-0 px-3.5 py-4 text-sm text-muted-foreground">
                  No payload stored for this event.
                </p>
              )}
            </section>
          </>
        )}
      </div>
    </>
  );
}

function SelectedEntry({
  row,
  event,
}: {
  row: FlowRow | undefined;
  event: api.Event;
}) {
  // Without history, show the error the event row carries.
  const errorText = row ? row.errorText : errorTextOf(event);
  const stackTrace = row
    ? row.stackTrace
    : event.messageContent?.errorContent?.exceptionStackTrace;
  const errorType = row
    ? row.message?.errorContent?.errorType
    : event.messageContent?.errorContent?.errorType;
  const hasTiming =
    event.queueTimeMs !== undefined || event.processingTimeMs !== undefined;

  return (
    <section
      aria-label="Selected entry"
      className="flex min-w-0 flex-col rounded-nb-md border border-border bg-background"
    >
      <div className="flex items-center justify-between gap-2 border-b border-border px-3.5 py-2.5">
        <h3 className="m-0 truncate text-[14.5px] font-bold">
          {row
            ? `${row.label} · ${moment(row.at).format("HH:mm:ss.SSS")}`
            : "Last error"}
        </h3>
        {row && (
          <span className="shrink-0 text-[11.5px] text-muted-foreground">
            selected in flow
          </span>
        )}
      </div>
      <div className="flex flex-col gap-3 px-3.5 py-3">
        {errorText ? (
          <div className="flex gap-2.5 rounded-nb-sm bg-status-danger-50 px-3 py-2.5 text-[13px] leading-snug text-status-danger-ink dark:bg-red-950/40 dark:text-red-200">
            <svg
              width="18"
              height="18"
              viewBox="0 0 24 24"
              fill="none"
              stroke="currentColor"
              strokeWidth="2"
              strokeLinecap="round"
              strokeLinejoin="round"
              aria-hidden="true"
              className="mt-px shrink-0"
            >
              <path d="M12 3l10 18H2z" />
              <path d="M12 10v5M12 18v.5" />
            </svg>
            <div className="min-w-0 wrap-break-word">
              {errorType && <b className="block">{errorType}</b>}
              {errorText}
            </div>
          </div>
        ) : row?.audit ? (
          <p className="m-0 text-[13px] text-foreground">
            {row.audit.comment || `${row.label} ${row.route}`}
          </p>
        ) : (
          <p className="m-0 text-[13px] text-muted-foreground">
            No error on this entry.
          </p>
        )}
        {stackTrace && (
          <div className="flex flex-col gap-1.5">
            <div className="text-[12px] font-bold text-muted-foreground">
              Call stack
            </div>
            <pre className="m-0 max-h-[260px] overflow-auto whitespace-pre-wrap wrap-break-word rounded-nb-sm bg-ink px-3 py-2.5 font-mono text-[11.5px] leading-relaxed text-[#EDE8DC]">
              {stackTrace}
            </pre>
          </div>
        )}
        {hasTiming && (
          <TimingBar
            segments={[
              {
                label: "Queue",
                display: formatSpan(event.queueTimeMs ?? 0),
                weight: event.queueTimeMs ?? 0,
                colorClass: "bg-ink-3",
              },
              {
                label: "Processing",
                display: formatSpan(event.processingTimeMs ?? 0),
                weight: event.processingTimeMs ?? 0,
                colorClass: "bg-status-info",
              },
            ]}
            trailing="last attempt"
          />
        )}
      </div>
    </section>
  );
}

function Properties({ event }: { event: api.Event }) {
  const mono = "font-mono text-[11.5px] break-all";
  const rows: [string, React.ReactNode, string?][] = [
    ["Event ID", event.eventId, mono],
    ["Last message ID", event.lastMessageId, mono],
    ["Session ID", event.sessionId, mono],
    ["Correlation ID", event.correlationId, mono],
    ["Event type", event.eventTypeId],
    ["From", event.from || event.originatingFrom],
    ["To", event.to],
    ["Endpoint role", event.endpointRole],
    ["Status", event.resolutionStatus],
    [
      "Retries",
      event.retryLimit
        ? `${event.retryCount ?? 0} of ${event.retryLimit} used`
        : String(event.retryCount ?? 0),
    ],
    ["Resubmits", String(event.resubmitCount ?? 0)],
    ["First enqueued", stamp(event.enqueuedTimeUtc)],
    ["Last updated", stamp(event.updatedAt)],
    [
      "Reported",
      event.isReported
        ? [event.ticketId, event.reportedBy && `by ${event.reportedBy}`]
            .filter(Boolean)
            .join(" ") || "Reported"
        : "Not reported",
    ],
  ];
  return (
    <section
      aria-label="Properties"
      className="rounded-nb-md border border-border bg-background"
    >
      <h3 className="m-0 border-b border-border px-3.5 py-2.5 text-[14.5px] font-bold">
        Properties
      </h3>
      <dl className="m-0 px-3.5 py-1">
        {rows.map(([name, value, cls]) => (
          <div
            key={name}
            className="flex gap-3 border-b border-border/60 py-1.5 text-[12.5px] last:border-b-0"
          >
            <dt className="w-[118px] shrink-0 text-muted-foreground">{name}</dt>
            <dd
              className={`m-0 min-w-0 font-semibold text-foreground ${cls ?? ""}`}
            >
              {value || "—"}
            </dd>
          </div>
        ))}
      </dl>
    </section>
  );
}

function BlockedInSession({
  page,
  endpointId,
  sessionId,
}: {
  page: api.BlockedEventsPage | undefined;
  endpointId: string;
  sessionId: string | undefined;
}) {
  const items = page?.items ?? [];
  const total = page?.total ?? 0;
  return (
    <section
      aria-label="Blocked in session"
      className="rounded-nb-md border border-border bg-background"
    >
      <div className="flex items-center justify-between gap-2 border-b border-border px-3.5 py-2.5">
        <h3 className="m-0 shrink-0 text-[14.5px] font-bold">
          Blocked in session
        </h3>
        {sessionId && (
          <span className="truncate font-mono text-[11.5px] text-muted-foreground">
            {sessionId}
          </span>
        )}
      </div>
      {items.length === 0 ? (
        <p className="m-0 px-3.5 py-3 text-[13px] text-muted-foreground">
          Nothing is waiting on this failure.
        </p>
      ) : (
        <ul className="m-0 list-none px-3.5 py-1">
          {items.map((b) => (
            <li
              key={b.eventId}
              className="flex items-center gap-2.5 border-b border-border/60 py-1.5 text-[12.5px] last:border-b-0"
            >
              <span
                aria-hidden="true"
                className="inline-block h-2 w-2 rounded-full bg-nimbus-purple"
              />
              <Link
                to={`/Message/Index/${endpointId}/${b.eventId}/0`}
                className="font-mono text-[12px] text-primary-700 hover:underline"
              >
                {b.eventId?.slice(0, 8)}…
              </Link>
              <span className="flex-1 text-muted-foreground">{b.status}</span>
            </li>
          ))}
          <li className="py-1.5 text-[11.5px] text-muted-foreground">
            {total > items.length ? `${total - items.length} more. ` : ""}
            Deferred until this failure is resubmitted or skipped.
          </li>
        </ul>
      )}
    </section>
  );
}
