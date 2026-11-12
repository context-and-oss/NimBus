import { useEffect, useRef, useState } from "react";
import * as api from "api-client";
import { Badge } from "components/ui/badge";
import { Button } from "components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "components/ui/card";
import { Spinner } from "components/ui/spinner";
import { type InsightsScope, PAGE_SIZE } from "./occurrence-table";

interface GroupPatternsProps {
  scope: InsightsScope;
  perspective: api.InsightsPerspective;
  groupKey: string;
  title: string;
  onClose: () => void;
}

/** Every error pattern of one likely-cause or recovery-guidance group, most frequent first. */
export function GroupPatterns({ scope, perspective, groupKey, title, onClose }: GroupPatternsProps) {
  const [tokens, setTokens] = useState<(string | undefined)[]>([undefined]);
  const [page, setPage] = useState<api.InsightGroupPatternPage>();
  const [error, setError] = useState<string>();
  const [loading, setLoading] = useState(true);
  const ticket = useRef(0);
  const token = tokens[tokens.length - 1];
  const { from, to, endpointId } = scope;

  useEffect(() => {
    const mine = ++ticket.current;
    setLoading(true);
    new api.Client(api.CookieAuth())
      .getMetricsFailedInsightsPatterns(from, to, perspective, groupKey, endpointId, PAGE_SIZE, token)
      .then((result) => {
        if (mine !== ticket.current) return;
        setPage(result);
        setError(undefined);
      })
      .catch(() => {
        if (mine === ticket.current) setError("The error patterns in this group couldn't be loaded. Try again.");
      })
      .finally(() => {
        if (mine === ticket.current) setLoading(false);
      });
  }, [from, to, endpointId, perspective, groupKey, token]);

  return (
    <Card>
      <CardHeader className="flex-row items-center justify-between gap-4 space-y-0">
        <CardTitle>Error patterns · {title}</CardTitle>
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
        ) : (
          <ul className="divide-y text-sm">
            {(page?.patterns ?? []).map((pattern) => (
              <li key={pattern.groupKey} className="flex items-start justify-between gap-4 py-2">
                <span className="font-mono text-xs break-all" title={pattern.exampleErrorText ?? ""}>
                  {pattern.normalizedPattern}
                </span>
                <Badge variant="error">{pattern.count}</Badge>
              </li>
            ))}
          </ul>
        )}
        <div className="flex justify-end gap-2">
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
        </div>
      </CardContent>
    </Card>
  );
}
