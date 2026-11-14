import { useEffect, useRef, useState } from "react";
import { Link } from "react-router-dom";
import type moment from "moment";
import * as api from "api-client";
import { formatMoment } from "functions/endpoint.functions";
import { Badge } from "components/ui/badge";
import { Button } from "components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "components/ui/card";
import { Spinner } from "components/ui/spinner";
import { faultOriginLabel, provenanceLabel } from "lib/failure-classification";
import { ANALYSIS_STATE, causeLabel, guidanceLabel, markerLabel } from "./insights-labels";

/** The fixed window and endpoint the overview was built for (spec 043 §4.2). */
export interface InsightsScope {
  from: moment.Moment;
  to: moment.Moment;
  endpointId?: string;
}

export const PAGE_SIZE = 50;

interface OccurrenceTableProps {
  scope: InsightsScope;
  perspective: api.InsightsPerspective;
  groupKey: string;
  title: string;
  onClose: () => void;
}

/**
 * One group's failed attempts, newest first, a page at a time. Render it with a key that
 * changes with the scope and group, so paging restarts instead of mixing scopes.
 */
export function OccurrenceTable({ scope, perspective, groupKey, title, onClose }: OccurrenceTableProps) {
  const [tokens, setTokens] = useState<(string | undefined)[]>([undefined]);
  const [page, setPage] = useState<api.InsightOccurrencePage>();
  const [error, setError] = useState<string>();
  const [loading, setLoading] = useState(true);
  const ticket = useRef(0);
  const token = tokens[tokens.length - 1];
  const { from, to, endpointId } = scope;

  useEffect(() => {
    const mine = ++ticket.current;
    setLoading(true);
    new api.Client(api.CookieAuth())
      .getMetricsFailedInsightsOccurrences(from, to, perspective, groupKey, endpointId, PAGE_SIZE, token)
      .then((result) => {
        if (mine !== ticket.current) return;
        setPage(result);
        setError(undefined);
      })
      .catch(() => {
        if (mine === ticket.current) setError("The failures in this group couldn't be loaded. Try again.");
      })
      .finally(() => {
        if (mine === ticket.current) setLoading(false);
      });
  }, [from, to, endpointId, perspective, groupKey, token]);

  const rows = page?.occurrences ?? [];
  return (
    <Card>
      <CardHeader className="flex-row items-center justify-between gap-4 space-y-0">
        <CardTitle>Failures · {title}</CardTitle>
        <Button size="sm" variant="ghost" onClick={onClose}>
          Close
        </Button>
      </CardHeader>
      <CardContent className="space-y-3">
        {error && (
          <p role="alert" className="text-sm text-status-danger">
            {error}
          </p>
        )}
        {loading && !page ? (
          <div className="flex justify-center py-8">
            <Spinner />
          </div>
        ) : rows.length === 0 && !error ? (
          <p className="text-sm text-muted-foreground">No failure occurrences in this group.</p>
        ) : (
          <div className="overflow-x-auto">
            <table className="w-full text-sm">
              <thead>
                <tr className="border-b text-left">
                  <th className="py-2 px-3">Failed</th>
                  <th className="py-2 px-3">Endpoint</th>
                  <th className="py-2 px-3">Event type</th>
                  <th className="py-2 px-3">Error pattern</th>
                  <th className="py-2 px-3">Analysis</th>
                  <th className="py-2 px-3">Likely cause</th>
                  <th className="py-2 px-3">Guidance</th>
                  <th className="py-2 px-3">Recorded by</th>
                  <th className="py-2 px-3">Analyzed at</th>
                  <th className="py-2 px-3">Status now</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((row) => {
                  const state = row.analysisState ?? api.InsightAnalysisState.Unavailable;
                  const marker = markerLabel(row.operationMarker, state);
                  return (
                    <tr key={`${row.eventId}/${row.messageId}`} className="border-b align-top">
                      <td className="py-2 px-3 whitespace-nowrap">
                        <Link
                          className="text-primary hover:underline"
                          to={`/Message/Index/${row.endpointId}/${row.eventId}/0${row.messageId ? `?message=${encodeURIComponent(row.messageId)}` : ""}`}
                        >
                          {row.failedAt ? formatMoment(row.failedAt, true) : "-"}
                        </Link>
                      </td>
                      <td className="py-2 px-3">{row.endpointId}</td>
                      <td className="py-2 px-3">{row.eventTypeId}</td>
                      <td className="py-2 px-3 max-w-xs truncate font-mono text-xs" title={row.normalizedPattern ?? ""}>
                        {row.normalizedPattern}
                      </td>
                      <td className="py-2 px-3">
                        <Badge variant={ANALYSIS_STATE[state].variant}>{ANALYSIS_STATE[state].label}</Badge>
                        {marker && <div className="mt-1 text-xs text-muted-foreground">{marker}</div>}
                      </td>
                      <td className="py-2 px-3">
                        {causeLabel(row.category) ?? "-"}
                        {row.categoryConfidence != null && (
                          <span className="ml-1 text-xs text-muted-foreground">
                            {Math.round(row.categoryConfidence * 100)}%
                          </span>
                        )}
                      </td>
                      <td className="py-2 px-3">{guidanceLabel(row.guidance) ?? "-"}</td>
                      <td className="py-2 px-3">
                        {provenanceLabel(row.provider) ?? "-"}
                        {row.provider && (
                          <div className="mt-1 text-xs text-muted-foreground">Origin: {faultOriginLabel(row.faultOrigin)}</div>
                        )}
                      </td>
                      <td className="py-2 px-3 whitespace-nowrap">{row.classifiedAt ? formatMoment(row.classifiedAt, true) : "-"}</td>
                      <td className="py-2 px-3">{row.currentStatus ?? <span className="text-muted-foreground">Unavailable</span>}</td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
        <div className="flex items-center justify-between gap-2 text-sm text-muted-foreground">
          <span>
            Saved results describe the failure when it was analyzed (Analyzed at). Status now is the event's current state.
          </span>
          <span className="flex gap-2">
            <Button size="sm" variant="outline" disabled={tokens.length === 1 || loading} onClick={() => setTokens((t) => t.slice(0, -1))}>
              Previous page
            </Button>
            <Button
              size="sm"
              variant="outline"
              disabled={!page?.continuationToken || loading}
              onClick={() => setTokens((t) => [...t, page?.continuationToken ?? undefined])}
            >
              Next page
            </Button>
          </span>
        </div>
      </CardContent>
    </Card>
  );
}
