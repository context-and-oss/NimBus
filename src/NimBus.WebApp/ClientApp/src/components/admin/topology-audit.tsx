import { useState, useEffect } from "react";
import * as api from "api-client";
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

export default function TopologyAudit() {
  const [endpoints, setEndpoints] = useState<EndpointOption[]>([]);
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

  async function handleAudit() {
    if (selectedEndpoint.length === 0) return;
    setLoading(true);
    setError(null);
    setAudit(null);
    setCleanupResult(null);
    try {
      const client = new api.Client(api.CookieAuth());
      const result = await client.getAdminTopology(selectedEndpoint[0]);
      setAudit(result);
      updateSummaryRow(selectedEndpoint[0], result);
    } catch (err: any) {
      setError(err.message ?? "Failed to audit topology");
    } finally {
      setLoading(false);
    }
  }

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

  return (
    <div className="space-y-6 w-full">
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

      {error && (
        <div className="bg-red-50 border border-red-200 rounded-md p-4 text-red-800">
          {error}
        </div>
      )}

      {allAudits && (
        <div className="space-y-2">
          <p className="m-0 text-sm text-muted-foreground">
            {driftingCount === 0
              ? `All ${allAudits.length - failedCount} audited endpoints match the catalog`
              : `${driftingCount} of ${allAudits.length} endpoints drift from the catalog`}
            {failedCount > 0 ? `; ${failedCount} could not be audited.` : "."}
          </p>
          <div className="border rounded-md overflow-hidden">
            <table
              className="w-full text-sm"
              aria-label="Catalog drift by endpoint"
            >
              <thead className="bg-muted text-left">
                <tr>
                  <th className="px-4 py-2 font-medium">Endpoint</th>
                  <th className="px-4 py-2 font-medium">Drift</th>
                  <th className="px-4 py-2">
                    <span className="sr-only">Actions</span>
                  </th>
                </tr>
              </thead>
              <tbody>
                {allAudits.map((row) => (
                  <tr
                    key={row.endpoint}
                    className={`border-t ${
                      audit && row.endpoint === selectedEndpoint[0]
                        ? "bg-muted/50"
                        : ""
                    }`}
                  >
                    <td className="px-4 py-2 font-mono">{row.endpoint}</td>
                    <td className="px-4 py-2">
                      {row.error !== undefined ? (
                        <span className="text-red-700">{row.error}</span>
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
                    <td className="px-4 py-2 text-right">
                      {row.result && (
                        <Button
                          variant="ghost"
                          size="sm"
                          onClick={() => viewEndpoint(row)}
                        >
                          View
                        </Button>
                      )}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </div>
      )}

      {audit && (
        <div className="space-y-4">
          <div className="flex justify-between items-center">
            <h3 className="text-lg font-semibold">
              Topic: {audit.topicName}
              {audit.hasDeprecated && (
                <Badge variant="warning" size="sm" className="ml-2">
                  Has deprecated items
                </Badge>
              )}
            </h3>
            {audit.hasDeprecated && (
              <Button
                colorScheme="red"
                onClick={() => setShowConfirm(true)}
                disabled={cleaning}
                isLoading={cleaning}
              >
                Remove Deprecated
              </Button>
            )}
          </div>

          <div className="border rounded-md overflow-hidden">
            {(audit.subscriptions ?? []).map((sub) => (
              <div key={sub.name} className="border-b last:border-b-0">
                <div
                  className={`flex items-center gap-2 px-4 py-2 font-medium text-sm ${
                    sub.isDeprecated ? "bg-red-50 text-red-700" : "bg-muted"
                  }`}
                >
                  <span className="font-mono">{sub.name}</span>
                  {sub.isDeprecated && (
                    <Badge variant="error" size="sm">
                      deprecated
                    </Badge>
                  )}
                </div>
                <div className="pl-8">
                  {(sub.rules ?? []).map((rule) => (
                    <div
                      key={`${sub.name}-${rule.name}`}
                      className={`flex items-center gap-2 px-4 py-1.5 text-sm border-t ${
                        rule.isDeprecated ? "bg-red-50 text-red-600" : ""
                      }`}
                    >
                      <span className="text-muted-foreground">├─</span>
                      <span className="font-mono">{rule.name}</span>
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
        </div>
      )}

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
