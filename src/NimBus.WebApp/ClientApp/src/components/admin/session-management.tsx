import { useState, useEffect } from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "components/ui/card";
import { Badge } from "components/ui/badge";
import { Combobox } from "components/ui/combobox";
import { Input } from "components/ui/input";
import { Spinner } from "components/ui/spinner";
import ConfirmDestructiveAction from "./confirm-destructive-action";

interface EndpointOption {
  value: string;
  label: string;
}

export default function SessionManagement() {
  const [endpoints, setEndpoints] = useState<EndpointOption[]>([]);

  useEffect(() => {
    loadEndpoints();
  }, []);

  async function loadEndpoints() {
    try {
      const client = new api.Client(api.CookieAuth());
      const config = await client.getAdminPlatformConfig();
      const eps = (config.endpoints ?? []).map((ep) => ({
        value: ep.id ?? "",
        label: ep.name ?? ep.id ?? "",
      }));
      setEndpoints(eps);
    } catch {
      // fallback
    }
  }

  return <SessionPurgeCard endpoints={endpoints} />;
}

export function SessionPurgeCard({ endpoints, initialEndpoint }: { endpoints: EndpointOption[]; initialEndpoint?: string }) {
  const [selectedEndpoint, setSelectedEndpoint] = useState<string[]>(initialEndpoint ? [initialEndpoint] : []);
  const [sessionId, setSessionId] = useState("");
  const [preview, setPreview] = useState<api.SessionPurgePreview | null>(null);
  const [loadingPreview, setLoadingPreview] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [showConfirm, setShowConfirm] = useState(false);
  const [purging, setPurging] = useState(false);
  const [result, setResult] = useState<api.SessionPurgeResult | null>(null);

  async function handlePreview() {
    if (selectedEndpoint.length === 0 || !sessionId.trim()) return;
    setLoadingPreview(true);
    setError(null);
    setPreview(null);
    setResult(null);
    try {
      const client = new api.Client(api.CookieAuth());
      const p = await client.getAdminSessionPreview(
        selectedEndpoint[0],
        sessionId.trim(),
      );
      setPreview(p);
    } catch (err: any) {
      setError(err.message ?? "Failed to preview session");
    } finally {
      setLoadingPreview(false);
    }
  }

  async function handlePurge() {
    if (selectedEndpoint.length === 0 || !sessionId.trim()) return;
    setShowConfirm(false);
    setPurging(true);
    setError(null);
    try {
      const client = new api.Client(api.CookieAuth());
      const r = await client.postAdminSessionPurge(
        selectedEndpoint[0],
        sessionId.trim(),
      );
      setResult(r);
      setPreview(null);
    } catch (err: any) {
      setError(err.message ?? "Failed to purge session");
    } finally {
      setPurging(false);
    }
  }

  const confirmText = `${selectedEndpoint[0] ?? ""}/${sessionId.trim()}`;

  return (
    <div className="space-y-6 w-full">
      <Card>
        <CardHeader>
          <CardTitle>Session Purge</CardTitle>
        </CardHeader>
        <CardContent>
          <div className="space-y-4">
            <div className="grid grid-cols-1 @md:grid-cols-2 gap-4">
              <Combobox
                options={endpoints}
                value={selectedEndpoint}
                onChange={(v) => {
                  setSelectedEndpoint(v);
                  setPreview(null);
                  setResult(null);
                  setError(null);
                }}
                placeholder="Select endpoint..."
                label="Endpoint"
                multiple={false}
              />
              <div>
                <label className="block text-sm font-medium text-foreground mb-1">
                  Session ID
                </label>
                <Input
                  value={sessionId}
                  onChange={(e) => {
                    setSessionId(e.target.value);
                    setPreview(null);
                    setResult(null);
                    setError(null);
                  }}
                  placeholder="Enter session ID..."
                />
              </div>
            </div>

            <Button
              onClick={handlePreview}
              disabled={
                selectedEndpoint.length === 0 ||
                !sessionId.trim() ||
                loadingPreview
              }
              isLoading={loadingPreview}
              variant="outline"
            >
              Preview Session
            </Button>

            {error && (
              <div className="bg-red-50 border border-red-200 dark:bg-red-950/30 dark:border-red-900/60 rounded-md p-3 text-red-800 dark:text-red-200 text-sm">
                {error}
              </div>
            )}

            {preview && (
              <div className="bg-blue-50 border border-blue-200 dark:bg-blue-950/30 dark:border-blue-900/60 rounded-md p-4 space-y-3">
                <h4 className="font-medium text-sm">
                  Session: {preview.sessionId}
                </h4>
                <div className="grid grid-cols-2 @lg:grid-cols-4 gap-4 text-sm">
                  <div>
                    <span className="text-muted-foreground">Pending:</span>{" "}
                    <Badge variant="pending" size="sm">
                      {preview.pendingCount}
                    </Badge>
                  </div>
                  <div>
                    <span className="text-muted-foreground">Deferred:</span>{" "}
                    <Badge variant="deferred" size="sm">
                      {preview.deferredCount}
                    </Badge>
                  </div>
                  <div>
                    <span className="text-muted-foreground">Deferred sub:</span>{" "}
                    <Badge variant="deferred" size="sm">
                      {preview.deferredSubscriptionCount}
                    </Badge>
                  </div>
                  <div>
                    <span className="text-muted-foreground">
                      Storage events:
                    </span>{" "}
                    <Badge variant="info" size="sm">
                      {preview.cosmosEventCount}
                    </Badge>
                  </div>
                </div>
                <Button
                  colorScheme="red"
                  onClick={() => setShowConfirm(true)}
                  disabled={purging}
                  isLoading={purging}
                  size="sm"
                >
                  Purge Session
                </Button>
              </div>
            )}

            {result && (
              <div className="bg-green-50 border border-green-200 dark:bg-green-950/30 dark:border-green-900/60 rounded-md p-4 space-y-2 text-sm">
                <h4 className="font-medium">
                  Purge Complete: {result.sessionId}
                </h4>
                <div className="space-y-1">
                  <div className="flex items-center gap-2">
                    <span
                      className={
                        result.activeMessagesRemoved! > 0
                          ? "text-green-700 dark:text-green-400"
                          : "text-muted-foreground"
                      }
                    >
                      Active messages removed: {result.activeMessagesRemoved}
                    </span>
                  </div>
                  <div className="flex items-center gap-2">
                    <span
                      className={
                        result.deferredMessagesRemoved! > 0
                          ? "text-green-700 dark:text-green-400"
                          : "text-muted-foreground"
                      }
                    >
                      Deferred messages removed:{" "}
                      {result.deferredMessagesRemoved}
                    </span>
                  </div>
                  <div className="flex items-center gap-2">
                    <span
                      className={
                        result.deferredSubscriptionMessagesRemoved! > 0
                          ? "text-green-700 dark:text-green-400"
                          : "text-muted-foreground"
                      }
                    >
                      Deferred sub messages removed:{" "}
                      {result.deferredSubscriptionMessagesRemoved}
                    </span>
                  </div>
                  <div className="flex items-center gap-2">
                    <span
                      className={
                        result.cosmosEventsRemoved! > 0
                          ? "text-green-700 dark:text-green-400"
                          : "text-muted-foreground"
                      }
                    >
                      Storage events removed: {result.cosmosEventsRemoved}
                    </span>
                  </div>
                  <div className="flex items-center gap-2">
                    <span
                      className={
                        result.sessionStateCleared
                          ? "text-green-700 dark:text-green-400"
                          : "text-red-600 dark:text-red-400"
                      }
                    >
                      Session state cleared:{" "}
                      {result.sessionStateCleared ? "Yes" : "No"}
                    </span>
                  </div>
                </div>
                {(result.errors?.length ?? 0) > 0 && (
                  <div className="mt-2">
                    <p className="text-xs font-medium text-red-700 dark:text-red-300">Errors:</p>
                    <ul className="text-xs text-red-600 dark:text-red-400 space-y-0.5">
                      {result.errors!.map((err, i) => (
                        <li key={i} className="font-mono">
                          {err}
                        </li>
                      ))}
                    </ul>
                  </div>
                )}
              </div>
            )}
          </div>
        </CardContent>
      </Card>

      <ConfirmDestructiveAction
        isOpen={showConfirm}
        onClose={() => setShowConfirm(false)}
        onConfirm={handlePurge}
        title="Purge Session"
        confirmLabel="Purge session"
        description={`This will remove all active messages, deferred messages (including Deferred subscription), stored events, and clear the session state for "${confirmText}".`}
        confirmText={confirmText}
        isLoading={purging}
      />
    </div>
  );
}
