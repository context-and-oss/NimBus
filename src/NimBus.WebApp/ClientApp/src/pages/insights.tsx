import { formatMoment } from "functions/endpoint.functions";
import React, { useState, useEffect, useCallback, useMemo, useRef } from "react";
import { useSearchParams } from "react-router-dom";
import moment from "moment";
import * as api from "api-client";
import CustomRangePopover from "components/metrics/custom-range-popover";
import Page from "components/page";
import { Card, CardHeader, CardTitle, CardContent } from "components/ui/card";
import { Spinner } from "components/ui/spinner";
import { Badge } from "components/ui/badge";
import { Button } from "components/ui/button";
import { Select } from "components/ui/select";
import { CoverageBar } from "components/insights/coverage-bar";
import { GroupPatterns } from "components/insights/group-patterns";
import { OccurrenceTable, type InsightsScope } from "components/insights/occurrence-table";
import { enrichmentMessage, GROUP_BY_OPTIONS, type InsightsGroupBy, PERSPECTIVE } from "components/insights/insights-labels";
import {
  BarChart,
  Bar,
  XAxis,
  YAxis,
  Tooltip,
  ResponsiveContainer,
  CartesianGrid,
} from "recharts";
import type { TimeRange } from "functions/time-range.functions";

const PERIODS: { label: string; value: api.Period; hours: number }[] = [
  { label: "1h", value: api.Period._1h, hours: 1 },
  { label: "12h", value: api.Period._12h, hours: 12 },
  { label: "1d", value: api.Period._1d, hours: 24 },
  { label: "3d", value: api.Period._3d, hours: 72 },
  { label: "7d", value: api.Period._7d, hours: 168 },
  { label: "30d", value: api.Period._30d, hours: 720 },
];

const NO_FAILURES = "No failure occurrences in this period.";
/** The cause and guidance group key of occurrences without a completed result. */
const NOT_ANALYZED = "notAnalyzed";

const segmentClass = (active: boolean) =>
  `px-4 py-2 text-sm font-semibold rounded-md border transition-colors disabled:cursor-not-allowed disabled:opacity-50 ${
    active
      ? "bg-primary text-white border-primary"
      : "bg-card text-foreground border-border hover:bg-accent"
  }`;

const parsePeriod = (value: string | null) =>
  PERIODS.find((p) => p.value === value)?.value ?? api.Period._1d;

const parseBy = (value: string | null): InsightsGroupBy =>
  value === "cause" || value === "guidance" ? value : "patterns";

function parseRange(from: string | null, to: string | null): TimeRange | undefined {
  if (!from || !to) return undefined;
  const range = { from: new Date(from), to: new Date(to) };
  return Number.isNaN(range.from.getTime()) || Number.isNaN(range.to.getTime()) ? undefined : range;
}

/**
 * Failed Message Insights (spec 043): the same failures grouped by error pattern, likely cause
 * or recovery guidance, with coverage and a drill-down. The scope lives in the URL, so returning
 * from message details or sharing a link restores it. Nothing here starts an analysis.
 */
export default function Insights() {
  const [searchParams, setSearchParams] = useSearchParams();
  const period = parsePeriod(searchParams.get("period"));
  const range = parseRange(searchParams.get("from"), searchParams.get("to"));
  const endpointId = searchParams.get("endpoint") || undefined;
  const requestedBy = parseBy(searchParams.get("by"));
  const group = searchParams.get("group") || undefined;
  const panel = searchParams.get("panel") === "patterns" ? "patterns" : "failures";

  const [data, setData] = useState<api.FailedInsightsOverview | null>(null);
  const [loading, setLoading] = useState(true);
  const [loadError, setLoadError] = useState<string>();
  const [endpoints, setEndpoints] = useState<string[]>([]);
  const [refreshCount, setRefreshCount] = useState(0);
  const [expanded, setExpanded] = useState<Set<string>>(new Set());
  // The first load and the Refresh button read saved classifications afresh, so a result made on
  // the details page shows when coming back; filter changes may use the short server cache.
  const freshRead = useRef(true);
  const ticket = useRef(0);

  const update = useCallback(
    (changes: Record<string, string | undefined>) =>
      setSearchParams((previous) => {
        const next = new URLSearchParams(previous);
        for (const [key, value] of Object.entries(changes)) {
          if (value) next.set(key, value);
          else next.delete(key);
        }
        return next;
      }),
    [setSearchParams],
  );

  useEffect(() => {
    new api.Client(api.CookieAuth())
      .getEndpointStatusCountAll()
      .then((counts) =>
        setEndpoints([...new Set((counts ?? []).map((c) => c.endpointId).filter((id): id is string => !!id))].sort()),
      )
      .catch(() => setEndpoints([]));
  }, []);

  const rangeFrom = range?.from.toISOString();
  const rangeTo = range?.to.toISOString();
  useEffect(() => {
    const mine = ++ticket.current;
    const refresh = freshRead.current;
    freshRead.current = false;
    setLoading(true);
    new api.Client(api.CookieAuth())
      .getMetricsFailedInsights(
        period,
        rangeFrom ? moment(rangeFrom) : undefined,
        rangeTo ? moment(rangeTo) : undefined,
        endpointId,
        refresh ? true : undefined,
      )
      .then((result) => {
        if (mine !== ticket.current) return;
        setData(result);
        setLoadError(undefined);
      })
      .catch(() => {
        if (mine === ticket.current) setLoadError("Failure insights couldn't be loaded. Try again.");
      })
      .finally(() => {
        if (mine === ticket.current) setLoading(false);
      });
  }, [period, rangeFrom, rangeTo, endpointId, refreshCount]);

  const refresh = () => {
    freshRead.current = true;
    setRefreshCount((count) => count + 1);
  };

  const enriched = data?.enrichment === api.InsightsEnrichmentStatus.Available;
  // Without saved classifications a cause or guidance link (shared, or after a failed retry)
  // shows error patterns and the banner. The URL keeps the request, so a later refresh restores it.
  const by: InsightsGroupBy = data && !enriched && requestedBy !== "patterns" ? "patterns" : requestedBy;
  const openPanel = group && by === requestedBy ? group : undefined;
  const scope: InsightsScope | undefined = data?.from && data?.to ? { from: data.from, to: data.to, endpointId } : undefined;
  const insightGroups = by === "cause" ? data?.causeGroups : by === "guidance" ? data?.guidanceGroups : undefined;
  const total = data?.totalOccurrences ?? data?.totalFailed ?? 0;

  const chartData = useMemo(() => {
    const bars =
      by === "patterns"
        ? (data?.groups ?? []).map((g) => ({ name: g.errorCategory ?? "", count: g.count ?? 0 }))
        : (insightGroups ?? []).map((g) => ({ name: g.label ?? g.groupKey ?? "", count: g.count ?? 0 }));
    return bars
      .slice(0, 15)
      .map((b) => ({ category: b.name.length > 40 ? b.name.substring(0, 40) + "..." : b.name, count: b.count }));
  }, [by, data, insightGroups]);

  const groupTitle = useMemo(() => {
    if (!group || !data) return undefined;
    if (by !== "patterns") return insightGroups?.find((g) => g.groupKey === group)?.label ?? group;
    for (const g of data.groups ?? []) {
      if (g.groupKey === group) return g.errorCategory;
      const sub = (g.subGroups ?? []).find((s) => s.groupKey === group);
      if (sub) return sub.normalizedPattern;
    }
    return group;
  }, [by, data, group, insightGroups]);

  const toggleExpand = (key: string) =>
    setExpanded((prev) => {
      const next = new Set(prev);
      if (next.has(key)) next.delete(key);
      else next.add(key);
      return next;
    });

  const openGroup = (groupKey: string, view: "failures" | "patterns" = "failures") =>
    update({ by: by === "patterns" ? undefined : by, group: groupKey, panel: view === "patterns" ? "patterns" : undefined });

  const presetHours = PERIODS.find((p) => p.value === period)?.hours ?? 24;
  const endpointOptions = [
    { value: "", label: "All endpoints" },
    ...[...new Set([...endpoints, ...(endpointId ? [endpointId] : [])])].map((id) => ({ value: id, label: id })),
  ];
  const banner = data && !enriched ? enrichmentMessage(data.enrichment, data.enrichmentLimit) : undefined;

  return (
    <Page title="Failed Message Insights">
      <div className="flex flex-col w-full gap-6 pb-8">
        <div className="flex flex-wrap items-center gap-2">
          {PERIODS.map((p) => (
            <button
              key={p.value}
              type="button"
              aria-pressed={!range && period === p.value}
              onClick={() => update({ period: p.value, from: undefined, to: undefined, group: undefined, panel: undefined })}
              className={segmentClass(!range && period === p.value)}
            >
              {p.label}
            </button>
          ))}
          <CustomRangePopover
            range={range}
            shown={{
              from: new Date(Date.now() - presetHours * 3_600_000),
              to: new Date(),
            }}
            onApply={(r) => update({ from: r.from.toISOString(), to: r.to.toISOString(), group: undefined, panel: undefined })}
            className={segmentClass(!!range)}
          />
          <div className="ml-2 w-56">
            <Select
              aria-label="Endpoint"
              value={endpointId ?? ""}
              options={endpointOptions}
              onChange={(e) => update({ endpoint: e.target.value || undefined, group: undefined, panel: undefined })}
            />
          </div>
        </div>

        <div className="flex flex-wrap items-center gap-2" role="group" aria-label="Group by">
          <span className="text-sm text-muted-foreground mr-1">Group by</span>
          {GROUP_BY_OPTIONS.map((option) => {
            const needsEnrichment = option.value !== "patterns";
            return (
              <button
                key={option.value}
                type="button"
                aria-pressed={by === option.value}
                disabled={needsEnrichment && !!data && !enriched}
                title={needsEnrichment && data && !enriched ? "Saved classifications aren't available for this selection" : undefined}
                onClick={() => update({ by: option.value === "patterns" ? undefined : option.value, group: undefined, panel: undefined })}
                className={segmentClass(by === option.value)}
              >
                {option.label}
              </button>
            );
          })}
        </div>

        {data && (
          <div className="flex flex-wrap items-center gap-x-6 gap-y-2 text-sm">
            <span className="font-semibold">{`${total.toLocaleString()} failure occurrences`}</span>
            {data.coverage && <CoverageBar coverage={data.coverage} />}
            <span className="text-muted-foreground">
              Failures read {data.occurrencesReadAt ? formatMoment(data.occurrencesReadAt, true) : "-"}
              {data.classificationsReadAt && <> · Classifications read {formatMoment(data.classificationsReadAt, true)}</>}
              {" · "}new failures can take up to 30 s to appear
            </span>
            <Button size="sm" variant="outline" onClick={refresh} disabled={loading}>
              Refresh
            </Button>
          </div>
        )}

        {enriched && data?.analysisAvailable === false && (
          <p role="status" className="text-sm text-muted-foreground">
            New analysis is unavailable because no provider is configured. Saved results are shown; not analyzed failures stay
            that way until a provider is configured.
          </p>
        )}
        {banner && (
          <p role="status" className="rounded-md border border-status-warning/40 bg-status-warning/10 px-4 py-3 text-sm">
            {banner}
          </p>
        )}
        {loadError && (
          <p role="alert" className="text-sm text-status-danger">
            {loadError}
          </p>
        )}

        {!data && loading ? (
          <div className="flex justify-center items-center py-20">
            <Spinner size="lg" />
          </div>
        ) : data ? (
          <>
            <Card>
              <CardHeader>
                <CardTitle>{GROUP_BY_OPTIONS.find((o) => o.value === by)?.label}</CardTitle>
              </CardHeader>
              <CardContent>
                {chartData.length === 0 ? (
                  <p className="text-muted-foreground text-sm">{NO_FAILURES}</p>
                ) : (
                  <ResponsiveContainer width="100%" height={Math.max(200, chartData.length * 40)}>
                    <BarChart data={chartData} layout="vertical" margin={{ left: 200, right: 20, top: 5, bottom: 5 }}>
                      <CartesianGrid strokeDasharray="3 3" horizontal={false} />
                      <XAxis type="number" />
                      <YAxis type="category" dataKey="category" width={190} tick={{ fontSize: 11 }} />
                      <Tooltip />
                      <Bar dataKey="count" fill="#ef4444" name="Failure occurrences" />
                    </BarChart>
                  </ResponsiveContainer>
                )}
              </CardContent>
            </Card>

            <Card>
              <CardHeader>
                <CardTitle>{by === "patterns" ? "Error pattern details" : "Group details"}</CardTitle>
              </CardHeader>
              <CardContent>
                {by === "patterns" ? (
                  <ErrorPatternTable groups={data.groups ?? []} expanded={expanded} onToggle={toggleExpand} onOpen={openGroup} showCoverage={enriched} />
                ) : (
                  <InsightGroupTable groups={insightGroups ?? []} onOpen={openGroup} />
                )}
              </CardContent>
            </Card>

            {openPanel && scope && (
              panel === "patterns" && by !== "patterns" ? (
                <GroupPatterns
                  key={`patterns|${by}|${openPanel}|${scope.from.toISOString()}|${scope.to.toISOString()}|${endpointId ?? ""}`}
                  scope={scope}
                  perspective={PERSPECTIVE[by]}
                  groupKey={openPanel}
                  title={groupTitle ?? openPanel}
                  onClose={() => update({ group: undefined, panel: undefined })}
                />
              ) : (
                <OccurrenceTable
                  key={`failures|${by}|${openPanel}|${scope.from.toISOString()}|${scope.to.toISOString()}|${endpointId ?? ""}`}
                  scope={scope}
                  perspective={PERSPECTIVE[by]}
                  groupKey={openPanel}
                  title={groupTitle ?? openPanel}
                  onClose={() => update({ group: undefined, panel: undefined })}
                />
              )
            )}
          </>
        ) : null}
      </div>
    </Page>
  );
}

function Badges({ values, variant }: { values?: string[]; variant: "default" | "info" }) {
  return (
    <div className="flex flex-wrap gap-1">
      {(values ?? []).map((value) => (
        <Badge key={value} variant={variant}>
          {value}
        </Badge>
      ))}
    </div>
  );
}

function ErrorPatternTable({
  groups,
  expanded,
  onToggle,
  onOpen,
  showCoverage,
}: {
  groups: api.ErrorPatternGroup[];
  expanded: Set<string>;
  onToggle: (key: string) => void;
  onOpen: (groupKey: string) => void;
  showCoverage: boolean;
}) {
  if (groups.length === 0) return <p className="text-muted-foreground text-sm">{NO_FAILURES}</p>;
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b">
            <th className="text-left py-2 px-3">Error pattern</th>
            <th className="text-right py-2 px-3">Count</th>
            {showCoverage && <th className="text-left py-2 px-3">Analyzed</th>}
            <th className="text-left py-2 px-3">Endpoints</th>
            <th className="text-left py-2 px-3">Event Types</th>
            <th className="text-left py-2 px-3">Latest Occurrence</th>
            <th className="text-left py-2 px-3">Example Error</th>
            <th className="py-2 px-3">
              <span className="sr-only">Actions</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {groups.map((g, i) => {
            const key = g.groupKey ?? String(i);
            const subs = g.subGroups ?? [];
            const hasSubGroups = subs.length > 1;
            const isExpanded = expanded.has(key);
            return (
              <React.Fragment key={key}>
                <tr
                  className={`border-b ${hasSubGroups ? "cursor-pointer hover:bg-accent" : ""}`}
                  onClick={hasSubGroups ? () => onToggle(key) : undefined}
                >
                  <td className="py-2 px-3 font-mono text-xs max-w-xs truncate" title={g.errorCategory ?? ""}>
                    <span className="inline-flex items-center gap-1">
                      {hasSubGroups && (
                        <span className="text-muted-foreground text-[10px] w-3">{isExpanded ? "▼" : "▶"}</span>
                      )}
                      {g.errorCategory}
                      {hasSubGroups && <span className="text-muted-foreground text-[10px] ml-1">({subs.length} patterns)</span>}
                    </span>
                  </td>
                  <td className="text-right py-2 px-3">
                    <Badge variant="error">{g.count}</Badge>
                  </td>
                  {showCoverage && <td className="py-2 px-3">{g.coverage && <CoverageBar coverage={g.coverage} compact />}</td>}
                  <td className="py-2 px-3"><Badges values={g.endpoints} variant="default" /></td>
                  <td className="py-2 px-3"><Badges values={g.eventTypes} variant="info" /></td>
                  <td className="py-2 px-3 whitespace-nowrap">{g.latestOccurrence ? formatMoment(g.latestOccurrence, true) : "-"}</td>
                  <td className="py-2 px-3 max-w-sm truncate text-xs text-muted-foreground" title={g.exampleErrorText ?? ""}>
                    {g.exampleErrorText}
                  </td>
                  <td className="py-2 px-3">
                    {g.groupKey && (
                      <Button size="xs" variant="outline" className="whitespace-nowrap" onClick={(e) => { e.stopPropagation(); onOpen(g.groupKey!); }}>
                        View failures
                      </Button>
                    )}
                  </td>
                </tr>
                {isExpanded &&
                  subs.map((sg, j) => (
                    <tr key={sg.groupKey ?? `${key}-${j}`} className="border-b bg-muted/50">
                      <td className="py-2 px-3 pl-8 font-mono text-xs max-w-xs truncate text-muted-foreground" title={sg.normalizedPattern ?? ""}>
                        {sg.normalizedPattern}
                      </td>
                      <td className="text-right py-2 px-3">
                        <Badge variant="error">{sg.count}</Badge>
                      </td>
                      {showCoverage && <td className="py-2 px-3">{sg.coverage && <CoverageBar coverage={sg.coverage} compact />}</td>}
                      <td className="py-2 px-3"><Badges values={sg.endpoints} variant="default" /></td>
                      <td className="py-2 px-3"><Badges values={sg.eventTypes} variant="info" /></td>
                      <td className="py-2 px-3 whitespace-nowrap">{sg.latestOccurrence ? formatMoment(sg.latestOccurrence, true) : "-"}</td>
                      <td className="py-2 px-3 max-w-sm truncate text-xs text-muted-foreground" title={sg.exampleErrorText ?? ""}>
                        {sg.exampleErrorText}
                      </td>
                      <td className="py-2 px-3">
                        {sg.groupKey && (
                          <Button size="xs" variant="outline" className="whitespace-nowrap" onClick={() => onOpen(sg.groupKey!)}>
                            View failures
                          </Button>
                        )}
                      </td>
                    </tr>
                  ))}
              </React.Fragment>
            );
          })}
        </tbody>
      </table>
    </div>
  );
}

function InsightGroupTable({
  groups,
  onOpen,
}: {
  groups: api.InsightGroup[];
  onOpen: (groupKey: string, view?: "failures" | "patterns") => void;
}) {
  if (groups.length === 0) return <p className="text-muted-foreground text-sm">{NO_FAILURES}</p>;
  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b">
            <th className="text-left py-2 px-3">Group</th>
            <th className="text-right py-2 px-3">Count</th>
            <th className="text-left py-2 px-3">Analyzed</th>
            <th className="text-left py-2 px-3">Endpoints</th>
            <th className="text-left py-2 px-3">Event Types</th>
            <th className="text-left py-2 px-3">Latest Occurrence</th>
            <th className="text-left py-2 px-3">Top error patterns</th>
            <th className="py-2 px-3">
              <span className="sr-only">Actions</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {groups.map((g) => (
            <tr key={g.groupKey} className="border-b align-top">
              <td className="py-2 px-3 font-medium">
                {g.label}
                {g.groupKey === NOT_ANALYZED && (
                  // Spec 043 §5: an event that has since completed or been skipped can't be analyzed.
                  <div className="text-xs font-normal text-muted-foreground">Includes failures that can no longer be analyzed</div>
                )}
              </td>
              <td className="text-right py-2 px-3">
                <Badge variant="error">{g.count}</Badge>
              </td>
              <td className="py-2 px-3">{g.coverage && <CoverageBar coverage={g.coverage} compact />}</td>
              <td className="py-2 px-3"><Badges values={g.endpoints} variant="default" /></td>
              <td className="py-2 px-3"><Badges values={g.eventTypes} variant="info" /></td>
              <td className="py-2 px-3 whitespace-nowrap">{g.latestOccurrence ? formatMoment(g.latestOccurrence, true) : "-"}</td>
              <td className="py-2 px-3">
                <ul className="space-y-1">
                  {(g.topPatterns ?? []).map((p) => (
                    <li key={p.groupKey} className="flex gap-2 text-xs">
                      <span className="font-mono truncate max-w-[15rem]" title={p.normalizedPattern ?? ""}>{p.normalizedPattern}</span>
                      <span className="text-muted-foreground">{p.count}</span>
                    </li>
                  ))}
                </ul>
                {(g.morePatterns ?? 0) > 0 && g.groupKey && (
                  <Button size="xs" variant="link" onClick={() => onOpen(g.groupKey!, "patterns")}>
                    and {g.morePatterns} more
                  </Button>
                )}
              </td>
              <td className="py-2 px-3">
                {g.groupKey && (
                  <Button size="xs" variant="outline" className="whitespace-nowrap" onClick={() => onOpen(g.groupKey!)}>
                    View failures
                  </Button>
                )}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
