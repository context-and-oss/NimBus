import * as React from "react";
import moment from "moment";
import { messageTypeKey } from "functions/message-type.functions";
import * as api from "api-client";
import { formatMoment } from "functions/endpoint.functions";

interface FlowTimelineProps {
  messages: api.Message[];
  audits: api.MessageAudit[];
}

interface TimelineEntry {
  timestamp: Date;
  type: "message" | "audit";
  messageType?: string;
  auditType?: string;
  from?: string;
  to?: string;
  errorText?: string;
  exceptionStackTrace?: string;
  eventContent?: string;
  auditorName?: string;
  comment?: string;
  messageId?: string;
}

// Keyed by the contract values from api-spec.yaml; getMessageColor normalises the
// casing so history written when the server still sent CLR names keeps its colour.
const MESSAGE_COLORS: Record<string, { bg: string; border: string; dot: string; label: string }> = {
  eventRequest:            { bg: "bg-blue-50 dark:bg-blue-950/30",       border: "border-blue-300 dark:border-blue-900/60",       dot: "bg-blue-500",    label: "Event Request" },
  resolutionResponse:      { bg: "bg-green-50 dark:bg-green-950/30",     border: "border-green-300 dark:border-green-900/60",     dot: "bg-green-500",   label: "Completed" },
  errorResponse:           { bg: "bg-red-50 dark:bg-red-950/30",         border: "border-red-300 dark:border-red-900/60",         dot: "bg-red-500",     label: "Error" },
  retryRequest:            { bg: "bg-amber-50 dark:bg-amber-950/30",     border: "border-amber-300 dark:border-amber-900/60",     dot: "bg-amber-500",   label: "Retry" },
  deferralResponse:        { bg: "bg-purple-50 dark:bg-purple-950/30",   border: "border-purple-300 dark:border-purple-900/60",   dot: "bg-purple-500",  label: "Deferred" },
  resubmissionRequest:     { bg: "bg-emerald-50 dark:bg-emerald-950/30", border: "border-emerald-300 dark:border-emerald-900/60", dot: "bg-emerald-500", label: "Resubmission" },
  skipResponse:            { bg: "bg-gray-50 dark:bg-zinc-900/40",       border: "border-gray-300 dark:border-zinc-700",          dot: "bg-gray-400",    label: "Skipped" },
  skipRequest:             { bg: "bg-gray-50 dark:bg-zinc-900/40",       border: "border-gray-300 dark:border-zinc-700",          dot: "bg-gray-400",    label: "Skip Request" },
  continuationRequest:     { bg: "bg-indigo-50 dark:bg-indigo-950/30",   border: "border-indigo-300 dark:border-indigo-900/60",   dot: "bg-indigo-500",  label: "Continuation" },
  unsupportedRequest:      { bg: "bg-orange-50 dark:bg-orange-950/30",   border: "border-orange-300 dark:border-orange-900/60",   dot: "bg-orange-500",  label: "Unsupported" },
  pendingHandoffResponse:  { bg: "bg-sky-50 dark:bg-sky-950/30",         border: "border-sky-300 dark:border-sky-900/60",         dot: "bg-sky-500",     label: "Awaiting External" },
  handoffCompletedRequest: { bg: "bg-teal-50 dark:bg-teal-950/30",       border: "border-teal-300 dark:border-teal-900/60",       dot: "bg-teal-500",    label: "Handoff Completed" },
  handoffFailedRequest:    { bg: "bg-orange-50 dark:bg-orange-950/30",   border: "border-orange-300 dark:border-orange-900/60",   dot: "bg-orange-500",  label: "Handoff Failed" },
};

const AUDIT_COLOR = { bg: "bg-sky-50 dark:bg-sky-950/30", border: "border-sky-200 dark:border-sky-900/60", dot: "bg-sky-400" };

function formatJson(raw: string | undefined): string {
  if (!raw) return "";
  try {
    return JSON.stringify(JSON.parse(raw), null, 2);
  } catch {
    return raw;
  }
}

function getMessageColor(messageType: string | undefined) {
  return MESSAGE_COLORS[messageTypeKey(messageType ?? "")] ?? { bg: "bg-gray-50 dark:bg-zinc-900/40", border: "border-gray-200 dark:border-zinc-700", dot: "bg-gray-400", label: messageType ?? "Unknown" };
}

export default function FlowTimeline({ messages, audits }: FlowTimelineProps) {
  const entries = React.useMemo(() => {
    const items: TimelineEntry[] = [];

    for (const msg of messages) {
      const ts = msg.enqueuedTimeUtc;
      if (!ts) continue;
      items.push({
        timestamp: typeof ts.toDate === "function" ? ts.toDate() : new Date(ts as any),
        type: "message",
        messageType: msg.messageType,
        // Show the endpoint that actually sent this message — for a
        // ResolutionResponse that's the handling subscriber, not the
        // original publisher. originatingFrom is only the fallback.
        from: msg.from || msg.originatingFrom,
        to: msg.to,
        errorText: msg.errorContent?.errorText,
        exceptionStackTrace: msg.errorContent?.exceptionStackTrace,
        eventContent: msg.eventContent,
        messageId: msg.messageId,
      });
    }

    for (const audit of audits) {
      const ts = audit.auditTimestamp;
      if (!ts) continue;
      items.push({
        timestamp: typeof ts.toDate === "function" ? ts.toDate() : new Date(ts as any),
        type: "audit",
        auditType: audit.auditType,
        auditorName: audit.auditorName,
        comment: audit.comment,
      });
    }

    items.sort((a, b) => a.timestamp.getTime() - b.timestamp.getTime());
    return items;
  }, [messages, audits]);

  if (entries.length === 0) {
    return (
      <p className="text-muted-foreground text-sm py-8 text-center">
        No message history available for this event.
      </p>
    );
  }

  return (
    <div className="relative pl-8 py-4 w-full">
      <p className="mb-3 text-xs text-muted-foreground">Times shown in local time ({Intl.DateTimeFormat().resolvedOptions().timeZone}).</p>
      {/* Vertical line */}
      <div className="absolute left-3.5 top-0 bottom-0 w-0.5 bg-border" />

      {entries.map((entry, i) => {
        if (entry.type === "audit") {
          return (
            <div key={`audit-${i}`} className="relative mb-4">
              <div className={`absolute left-[-20px] top-2 w-3 h-3 rounded-full border-2 border-background ${AUDIT_COLOR.dot}`} />
              <div className={`ml-4 p-3 rounded-md border ${AUDIT_COLOR.bg} ${AUDIT_COLOR.border}`}>
                <div className="flex items-center gap-2 text-xs text-muted-foreground">
                  <span className="font-mono">{formatMoment(moment(entry.timestamp))}</span>
                  <span className="font-semibold text-sky-700">
                    {entry.auditType === "comment" ? "Comment" : entry.auditType}
                  </span>
                  {entry.auditorName && <span>by {entry.auditorName}</span>}
                </div>
                {entry.comment && (
                  <p className="text-sm mt-1 text-sky-900">{entry.comment}</p>
                )}
              </div>
            </div>
          );
        }

        const color = getMessageColor(entry.messageType);

        return (
          <div key={`msg-${i}`} className="relative mb-4">
            <div className={`absolute left-[-20px] top-2 w-3 h-3 rounded-full border-2 border-background ${color.dot}`} />
            <div className={`ml-4 p-3 rounded-md border ${color.bg} ${color.border}`}>
              <div className="flex items-center justify-between gap-2">
                <div className="flex items-center gap-2">
                  <span className={`text-xs font-bold px-2 py-0.5 rounded-sm ${color.dot} text-white`}>
                    {color.label}
                  </span>
                  {entry.from && entry.to && (
                    <span className="text-xs text-muted-foreground">
                      {entry.from} <span className="font-bold">→</span> {entry.to}
                    </span>
                  )}
                </div>
                <span className="text-xs font-mono text-muted-foreground whitespace-nowrap">
                  {formatMoment(moment(entry.timestamp))}
                </span>
              </div>
              {entry.errorText && (
                <p className="text-xs mt-2 text-red-700 bg-red-100 dark:text-red-200 dark:bg-red-950/40 rounded-sm px-2 py-1 font-mono break-all">
                  {entry.errorText}
                </p>
              )}
              {entry.exceptionStackTrace && (
                <details className="mt-2">
                  <summary className="text-[10px] text-red-600 dark:text-red-400 cursor-pointer hover:underline">Exception details</summary>
                  <pre className="text-[10px] mt-1 text-red-800 bg-red-50 dark:text-red-200 dark:bg-red-950/30 rounded-sm px-2 py-1 overflow-x-auto max-h-40 whitespace-pre-wrap break-all">
                    {entry.exceptionStackTrace}
                  </pre>
                </details>
              )}
              {entry.eventContent && (
                <details className="mt-2">
                  <summary className="text-[10px] text-muted-foreground cursor-pointer hover:underline">Payload</summary>
                  <pre className="text-[10px] mt-1 bg-muted rounded-sm px-2 py-1 overflow-x-auto max-h-48 whitespace-pre-wrap">
                    {formatJson(entry.eventContent)}
                  </pre>
                </details>
              )}
              {entry.messageId && (
                <p className="text-[10px] mt-1 text-muted-foreground font-mono">
                  MessageId: {entry.messageId}
                </p>
              )}
            </div>
          </div>
        );
      })}
    </div>
  );
}
