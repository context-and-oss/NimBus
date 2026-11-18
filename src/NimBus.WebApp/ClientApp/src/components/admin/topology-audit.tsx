import { useCallback, useEffect, useRef, useState } from "react";
import * as api from "api-client";
import { Inspector, InspectorBlock, InspectorPortal } from "components/workspace/workspace";
import {
  RowSelect,
  selectableRowClass,
  WORKSPACE_HEAD_ROW,
  WORKSPACE_TH,
  WorkspaceTableFrame,
} from "components/workspace/selectable-row";
import { Button } from "components/ui/button";
import { Badge } from "components/ui/badge";
import { Combobox } from "components/ui/combobox";
import ConfirmDestructiveAction from "./confirm-destructive-action";
import OperationProgress from "./operation-progress";

interface EndpointOption {
  value: string;
  label: string;
}

/** One endpoint's row in the "Audit all endpoints" summary. */
interface EndpointAudit {
  endpoint: string;
  result?: api.TopologyAuditResult;
  error?: string;
}

/** Each audit lists a topic's subscriptions and rules; keep the admin API calm. */
const AUDIT_ALL_CONCURRENCY = 4;

/**
 * Deprecated items an endpoint's cleanup would delete. Rules of a deprecated
 * subscription go with it, so they aren't counted twice.
 */
function deprecatedCount(audit: api.TopologyAuditResult): number {
  return (audit.subscriptions ?? []).reduce(
    (n, sub) =>
      sub.isDeprecated
        ? n + 1
        : n + (sub.rules ?? []).filter((r) => r.isDeprecated).length,
    0,
  );
}

/** Drifting endpoints first, then failed audits, then endpoints in sync. */
function attentionRank(row: EndpointAudit): number {
  if (row.result?.hasDeprecated) return 0;
  if (row.error !== undefined) return 1;
  return 2;
}

export interface TopologyAuditProps {
  /**
   * The audited endpoint, when the page chooses it: a name audits that
   * endpoint at once, null offers the all-endpoints audit. Left undefined, the
   * component shows its own endpoint picker.
   */
  endpoint?: string | null;
  /** Asked to open an endpoint from the all-endpoints summary when `endpoint` is controlled. */
  onEndpointChange?: (endpoint: string) => void;
  /** Receives how many endpoints drift after an all-endpoints audit; undefined before one. */
  onDriftCount?: (drifting: number | undefined) => void;
}

export default function TopologyAudit({ endpoint, onEndpointChange, onDriftCount }: TopologyAuditProps = {}) {
  const controlled = endpoint !== undefined;
  // A ref, so an inline callback doesn't re-run the report below.
  const driftCount = useRef(onDriftCount);
  driftCount.current = onDriftCount;
  // Only the newest single audit may land, so a quick switch between
  // endpoints can't show (or clean up) the one before.
  const auditRequest = useRef(0);
  const [endpoints, setEndpoints] = useState<EndpointOption[]>([]);
  const [endpointsLoaded, setEndpointsLoaded] = useState(false);
  const [selectedEndpoint, setSelectedEndpoint] = useState<string[]>([]);
  const [audit, setAudit] = useState<api.TopologyAuditResult | null>(null);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showConfirm, setShowConfirm] = useState(false);
  const [cleaning, setCleaning] = useState(false);
  const [cleanupResult, setCleanupResult] =
    useState<api.TopologyCleanupResult | null>(null);
  const [allAudits, setAllAudits] = useState<EndpointAudit[] | null>(null);
  const [auditingAll, setAuditingAll] = useState(false);

  useEffect(() => {
    loadEndpoints();
  }, []);

  async function loadEndpoints() {
    try {
      const client = new api.Client(api.CookieAuth());
      const config = await client.getAdminPlatformConfig();
      const eps = (config.endpoints ?? []).map((ep) => ({
        value: ep.name ?? ep.id ?? "",
        label: ep.name ?? ep.id ?? "",
      }));
      setEndpoints(eps);
    } catch {
      // Endpoints will be empty
    } finally {
      setEndpointsLoaded(true);
    }
  }

  /** Keeps the all-endpoints summary in step with a fresh single audit. */
  function updateSummaryRow(endpoint: string, result: api.TopologyAuditResult) {
    setAllAudits(
      (rows) =>
        rows?.map((row) =>
          row.endpoint === endpoint ? { endpoint, result } : row,
        ) ?? null,
    );
  }

  const auditEndpoint = useCallback(async (name: string) => {
    const request = ++auditRequest.current;
    setLoading(true);
    setError(null);
    setAudit(null);
    setCleanupResult(null);
    try {
      const client = new api.Client(api.CookieAuth());
      const result = await client.getAdminTopology(name);
      if (request !== auditRequest.current) return;
      setAudit(result);
      setAllAudits(
        (rows) =>
          rows?.map((row) => (row.endpoint === name ? { endpoint: name, result } : row)) ?? null,
      );
    } catch (err: any) {
      if (request !== auditRequest.current) return;
      setError(err.message ?? "Failed to audit topology");
    } finally {
      if (request === auditRequest.current) setLoading(false);
    }
  }, []);

  function handleAudit() {
    if (selectedEndpoint.length === 0) return;
    void auditEndpoint(selectedEndpoint[0]);
  }

  // Controlled: audit the page's endpoint as soon as it is chosen.
  useEffect(() => {
    if (!controlled) return;
    if (endpoint) {
      setSelectedEndpoint([endpoint]);
      void auditEndpoint(endpoint);
    } else {
      ++auditRequest.current;
      setSelectedEndpoint([]);
      setAudit(null);
      setCleanupResult(null);
      setError(null);
      setLoading(false);
    }
  }, [controlled, endpoint, auditEndpoint]);

  async function handleAuditAll() {
    if (endpoints.length === 0) return;
    setAuditingAll(true);
    setError(null);
    setAudit(null);
    setCleanupResult(null);
    setAllAudits(null);

    const names = endpoints.map((ep) => ep.value);
    const rows: EndpointAudit[] = new Array(names.length);
    let next = 0;
    async function worker() {
      const client = new api.Client(api.CookieAuth());
      while (next < names.length) {
        const i = next++;
        try {
          const result = await client.getAdminTopology(names[i]);
          rows[i] = { endpoint: names[i], result };
        } catch (err: any) {
          rows[i] = {
            endpoint: names[i],
            error: err?.message ?? "Audit failed",
          };
        }
      }
    }
    try {
      await Promise.all(
        Array.from(
          { length: Math.min(AUDIT_ALL_CONCURRENCY, names.length) },
          worker,
        ),
      );
      // sort is stable, so catalog order holds within each group.
      setAllAudits(
        [...rows].sort((a, b) => attentionRank(a) - attentionRank(b)),
      );
    } finally {
      setAuditingAll(false);
    }
  }

  function viewEndpoint(row: EndpointAudit) {
    if (!row.result) return;
    if (controlled) {
      onEndpointChange?.(row.endpoint);
      return;
    }
    setSelectedEndpoint([row.endpoint]);
    setAudit(row.result);
    setCleanupResult(null);
    setError(null);
  }

  async function handleRemoveDeprecated() {
    if (selectedEndpoint.length === 0) return;
    setCleaning(true);
    setShowConfirm(false);
    try {
      const client = new api.Client(api.CookieAuth());
      const result = await client.postAdminTopologyRemoveDeprecated(
        selectedEndpoint[0],
      );
      setCleanupResult(result);
      // Refresh audit without clearing cleanupResult
      setLoading(true);
      try {
        const auditClient = new api.Client(api.CookieAuth());
        const auditResult = await auditClient.getAdminTopology(
          selectedEndpoint[0],
        );
        setAudit(auditResult);
        updateSummaryRow(selectedEndpoint[0], auditResult);
      } finally {
        setLoading(false);
      }
    } catch (err: any) {
      setError(err.message ?? "Failed to remove deprecated items");
    } finally {
      setCleaning(false);
    }
  }

  const driftingCount =
    allAudits?.filter((r) => r.result?.hasDeprecated).length ?? 0;
  const failedCount =
    allAudits?.filter((r) => r.error !== undefined).length ?? 0;

  useEffect(() => {
    driftCount.current?.(allAudits ? driftingCount : undefined);
  }, [allAudits, driftingCount]);

  // The table's rows: the all-endpoints summary, or the one audited endpoint.
  const rows: EndpointAudit[] | null =
    allAudits ?? (audit && selectedEndpoint[0] ? [{ endpoint: selectedEndpoint[0], result: audit }] : null);
  const viewed = audit ? selectedEndpoint[0] : undefined;

  // A topic outside the catalog (a system topic, or one NimBus didn't create)
  // has nothing to be compared with.
  const notInCatalog =
    controlled && !!endpoint && endpointsLoaded && !endpoints.some((ep) => ep.value === endpoint);

  return (
    <div className="space-y-6 w-full">
      {!controlled && (
        <div className="flex flex-wrap gap-4 items-end">
          <div className="flex-1 max-w-md">
            <Combobox
              options={endpoints}
              value={selectedEndpoint}
              onChange={setSelectedEndpoint}
              placeholder="Select endpoint..."
              label="Endpoint"
              multiple={false}
            />
          </div>
          <Button
            onClick={handleAudit}
            disabled={selectedEndpoint.length === 0 || loading || auditingAll}
            isLoading={loading}
          >
            Audit Topology
          </Button>
          <Button
            variant="outline"
            onClick={handleAuditAll}
            disabled={endpoints.length === 0 || loading || auditingAll}
            isLoading={auditingAll}
          >
            Audit all endpoints
          </Button>
        </div>
      )}
      {controlled && !endpoint && (
        <Button
          variant="outline"
          onClick={handleAuditAll}
          disabled={endpoints.length === 0 || auditingAll}
          isLoading={auditingAll}
        >
          Audit all endpoints
        </Button>
      )}
      {controlled && endpoint && !notInCatalog && (
        <Button
          variant="outline"
          size="sm"
          onClick={() => void auditEndpoint(endpoint)}
          disabled={loading || cleaning}
          isLoading={loading}
        >
          Audit again
        </Button>
      )}
      {notInCatalog && (
        <p className="m-0 rounded-nb-md border border-border bg-card p-4 text-sm text-muted-foreground">
          <span className="font-mono">{endpoint}</span> isn&apos;t an endpoint in the catalog, so
          there is nothing to compare it with.
        </p>
      )}

      {error && !notInCatalog && (
        <div className="bg-red-50 border border-red-200 rounded-md p-4 text-red-800">
          {error}
        </div>
      )}

      {rows && !notInCatalog && (
        <div className="space-y-2">
          {allAudits && (
            <p className="m-0 text-sm text-muted-foreground">
              {driftingCount === 0
                ? `All ${allAudits.length - failedCount} audited endpoints match the catalog`
                : `${driftingCount} of ${allAudits.length} endpoints drift from the catalog`}
              {failedCount > 0 ? `; ${failedCount} could not be audited.` : "."}
            </p>
          )}
          <WorkspaceTableFrame>
            <table className="w-full text-sm" aria-label="Catalog drift by endpoint">
              <thead>
                <tr className={WORKSPACE_HEAD_ROW}>
                  <th scope="col" className={WORKSPACE_TH}>Endpoint</th>
                  <th scope="col" className={WORKSPACE_TH}>Drift</th>
                </tr>
              </thead>
              <tbody>
                {rows.map((row) => (
                  <tr key={row.endpoint} className={selectableRowClass(row.endpoint === viewed)}>
                    <td className="px-4">
                      {row.result ? (
                        <RowSelect mono selected={row.endpoint === viewed} onSelect={() => viewEndpoint(row)}>
                          {row.endpoint}
                        </RowSelect>
                      ) : (
                        <span className="block py-2.5 font-mono text-[13px] font-semibold">{row.endpoint}</span>
                      )}
                    </td>
                    <td className="px-4 py-2">
                      {row.error !== undefined ? (
                        <span className="text-status-danger">{row.error}</span>
                      ) : row.result?.hasDeprecated ? (
                        <Badge variant="warning" size="sm">
                          {deprecatedCount(row.result)} deprecated
                        </Badge>
                      ) : (
                        <Badge variant="success" size="sm">
                          In sync
                        </Badge>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </WorkspaceTableFrame>
        </div>
      )}

      <InspectorPortal>
        {audit && !notInCatalog ? (
          <Inspector
            eyebrow="Topic"
            title={<span className="font-mono text-[16px]">{audit.topicName}</span>}
            state={
              audit.hasDeprecated ? (
                <Badge variant="warning" size="sm">
                  Has deprecated items
                </Badge>
              ) : (
                <Badge variant="success" size="sm">
                  In sync
                </Badge>
              )
            }
            description={
              audit.hasDeprecated
                ? "Deprecated items exist in the namespace but aren't declared in the catalog. Removing them deletes them from Service Bus; messages they would have received are no longer delivered."
                : "The live subscriptions and rules match the declared catalog."
            }
          >
            <InspectorBlock label="Subscriptions and rules">
              <div className="overflow-hidden rounded-nb-md border border-border bg-background">
                {(audit.subscriptions ?? []).map((sub) => (
                  <div key={sub.name} className="border-b border-border last:border-b-0">
                    <div
                      className={`flex items-center gap-2 px-3 py-1.5 text-[13px] font-medium ${
                        sub.isDeprecated ? "bg-status-danger-50 text-status-danger-ink" : "bg-muted"
                      }`}
                    >
                      <span className="font-mono [overflow-wrap:anywhere]">{sub.name}</span>
                      {sub.isDeprecated && (
                        <Badge variant="error" size="sm">
                          deprecated
                        </Badge>
                      )}
                    </div>
                    <div className="pl-4">
                      {(sub.rules ?? []).map((rule) => (
                        <div
                          key={`${sub.name}-${rule.name}`}
                          className={`flex items-center gap-2 border-t border-border px-3 py-1 text-[12.5px] ${
                            rule.isDeprecated ? "bg-status-danger-50 text-status-danger-ink" : ""
                          }`}
                        >
                          <span className="text-muted-foreground">├─</span>
                          <span className="font-mono [overflow-wrap:anywhere]">{rule.name}</span>
                          {rule.isDeprecated && (
                            <Badge variant="error" size="sm">
                              deprecated
                            </Badge>
                          )}
                        </div>
                      ))}
                    </div>
                  </div>
                ))}
              </div>
            </InspectorBlock>

            {audit.hasDeprecated && (
              <Button
                colorScheme="red"
                size="sm"
                onClick={() => setShowConfirm(true)}
                disabled={cleaning}
                isLoading={cleaning}
              >
                Remove Deprecated
              </Button>
            )}

            {cleanupResult && (
              <OperationProgress
                processed={
                  (cleanupResult.deletedSubscriptions?.length ?? 0) +
                  (cleanupResult.deletedRules?.length ?? 0) +
                  (cleanupResult.errors?.length ?? 0)
                }
                succeeded={
                  (cleanupResult.deletedSubscriptions?.length ?? 0) +
                  (cleanupResult.deletedRules?.length ?? 0)
                }
                failed={cleanupResult.errors?.length ?? 0}
                errors={cleanupResult.errors}
                isComplete={true}
              />
            )}
          </Inspector>
        ) : (
          <Inspector
            empty={
              loading
                ? "Auditing…"
                : "Audit the endpoints, then select one to see its subscriptions and rules."
            }
          />
        )}
      </InspectorPortal>

      <ConfirmDestructiveAction
        isOpen={showConfirm}
        onClose={() => setShowConfirm(false)}
        onConfirm={handleRemoveDeprecated}
        title="Remove Deprecated Topology"
        description={`This will delete all deprecated subscriptions and rules from the Service Bus topic "${selectedEndpoint[0] ?? ""}".`}
        confirmText={selectedEndpoint[0] ?? ""}
        isLoading={cleaning}
      />
    </div>
  );
}
