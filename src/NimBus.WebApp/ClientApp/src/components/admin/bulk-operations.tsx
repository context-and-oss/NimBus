import { useState, useEffect } from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
  CardTitle,
  CardDescription,
} from "components/ui/card";
import { Combobox } from "components/ui/combobox";
import { Input } from "components/ui/input";
import { Spinner } from "components/ui/spinner";
import ConfirmDestructiveAction from "./confirm-destructive-action";
import OperationProgress from "./operation-progress";

interface EndpointOption {
  value: string;
  label: string;
}

export default function BulkOperations() {
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

  return (
    <div className="space-y-6 w-full">
      <BulkResubmitCard endpoints={endpoints} />
      <DeleteDeadLetteredCard endpoints={endpoints} />
      <DeleteEventCard endpoints={endpoints} />
    </div>
  );
}

export function BulkResubmitCard({ endpoints, initialEndpoint }: { endpoints: EndpointOption[]; initialEndpoint?: string }) {
  const [selected, setSelected] = useState<string[]>(initialEndpoint ? [initialEndpoint] : []);
  const [preview, setPreview] = useState<api.BulkResubmitPreview | null>(null);
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<api.BulkOperationResult | null>(null);
  const [executing, setExecuting] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);

  async function handlePreview() {
    if (selected.length === 0) return;
    setLoading(true);
    setPreview(null);
    setResult(null);
    try {
      const client = new api.Client(api.CookieAuth());
      const p = await client.getAdminFailedPreview(selected[0]);
      setPreview(p);
    } catch {
      // error handling
    } finally {
      setLoading(false);
    }
  }

  async function handleResubmit() {
    if (selected.length === 0) return;
    setShowConfirm(false);
    setExecuting(true);
    try {
      const client = new api.Client(api.CookieAuth());
      const r = await client.postAdminBulkResubmit(selected[0]);
      setResult(r);
      setPreview(null);
    } catch {
      // error
    } finally {
      setExecuting(false);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Bulk Resubmit Failed Messages</CardTitle>
        <CardDescription>
          Resubmit all failed messages older than 10 minutes for an endpoint
        </CardDescription>
      </CardHeader>
      <CardContent>
        <div className="space-y-4">
          <div className="flex gap-4 items-end">
            <div className="flex-1 max-w-md">
              <Combobox
                options={endpoints}
                value={selected}
                onChange={(v) => {
                  setSelected(v);
                  setPreview(null);
                  setResult(null);
                }}
                placeholder="Select endpoint..."
                label="Endpoint"
                multiple={false}
              />
            </div>
            <Button
              onClick={handlePreview}
              disabled={selected.length === 0 || loading}
              isLoading={loading}
              variant="outline"
            >
              Preview
            </Button>
          </div>

          {preview && (
            <div className="bg-blue-50 border border-blue-200 dark:bg-blue-950/30 dark:border-blue-900/60 rounded-md p-4 space-y-2">
              <p className="text-sm">
                Total failed:{" "}
                <span className="font-bold">{preview.totalFailedCount}</span>
              </p>
              <p className="text-sm">
                Eligible for resubmit ({">"}
                {preview.ageThresholdMinutes} min old):{" "}
                <span className="font-bold">{preview.eligibleCount}</span>
              </p>
              {(preview.eligibleCount ?? 0) > 0 && (
                <Button
                  colorScheme="green"
                  onClick={() => setShowConfirm(true)}
                  disabled={executing}
                  isLoading={executing}
                  size="sm"
                >
                  Resubmit {preview.eligibleCount} messages
                </Button>
              )}
            </div>
          )}

          {result && (
            <OperationProgress
              processed={result.processed ?? 0}
              succeeded={result.succeeded ?? 0}
              failed={result.failed ?? 0}
              errors={result.errors}
              isComplete={true}
            />
          )}

          <ConfirmDestructiveAction
            isOpen={showConfirm}
            onClose={() => setShowConfirm(false)}
            onConfirm={handleResubmit}
            title="Bulk Resubmit Failed Messages"
            description={`This will resubmit ${preview?.eligibleCount ?? 0} failed messages for endpoint "${selected[0] ?? ""}".`}
            confirmText={selected[0] ?? ""}
            confirmLabel="Resubmit"
            tone="primary"
            isLoading={executing}
          />
        </div>
      </CardContent>
    </Card>
  );
}

export function DeleteDeadLetteredCard({
  endpoints,
  initialEndpoint,
}: {
  endpoints: EndpointOption[];
  /** Pre-selects the endpoint, e.g. from an Endpoints table row action. */
  initialEndpoint?: string;
}) {
  const [selected, setSelected] = useState<string[]>(initialEndpoint ? [initialEndpoint] : []);
  const [count, setCount] = useState<number | null>(null);
  // The subscription's dead-letter queue: every message, and those of the stored events (the ones
  // the delete removes). Null when Service Bus couldn't be read.
  const [queueCount, setQueueCount] = useState<number | null>(null);
  const [matchingCount, setMatchingCount] = useState<number | null>(null);
  const [loading, setLoading] = useState(false);
  const [result, setResult] = useState<api.BulkOperationResult | null>(null);
  const [executing, setExecuting] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);

  // Queue messages with no stored event: the delete leaves them for Purge dead letters.
  const unmatched = queueCount !== null && matchingCount !== null ? queueCount - matchingCount : 0;
  const matchingText = matchingCount === null ? "messages" : `${matchingCount} messages`;

  async function handlePreview() {
    if (selected.length === 0) return;
    setLoading(true);
    setCount(null);
    setResult(null);
    try {
      const client = new api.Client(api.CookieAuth());
      const p = await client.getAdminDeadletteredPreview(selected[0]);
      setQueueCount(p.queueCount ?? null);
      setMatchingCount(p.matchingQueueCount ?? null);
      setCount(p.count ?? 0);
    } catch {
      // error
    } finally {
      setLoading(false);
    }
  }

  async function handleDelete() {
    if (selected.length === 0) return;
    setShowConfirm(false);
    setExecuting(true);
    try {
      const client = new api.Client(api.CookieAuth());
      const r = await client.postAdminDeleteDeadlettered(selected[0]);
      setResult(r);
      setCount(null);
    } catch {
      // error
    } finally {
      setExecuting(false);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Delete Dead-Lettered Messages</CardTitle>
        <CardDescription>
          Delete an endpoint's dead-lettered events and remove their messages from its subscription's dead-letter queue
        </CardDescription>
      </CardHeader>
      <CardContent>
        <div className="space-y-4">
          <div className="flex gap-4 items-end">
            <div className="flex-1 max-w-md">
              <Combobox
                options={endpoints}
                value={selected}
                onChange={(v) => {
                  setSelected(v);
                  setCount(null);
                  setResult(null);
                }}
                placeholder="Select endpoint..."
                label="Endpoint"
                multiple={false}
              />
            </div>
            <Button
              onClick={handlePreview}
              disabled={selected.length === 0 || loading}
              isLoading={loading}
              variant="outline"
            >
              Preview
            </Button>
          </div>

          {count !== null && (
            <div className="bg-yellow-50 border border-yellow-200 dark:bg-yellow-950/30 dark:border-yellow-900/60 rounded-md p-4 space-y-2">
              <p className="text-sm">
                Stored dead-lettered events:{" "}
                <span className="font-bold">{count}</span>
              </p>
              <p className="text-sm">
                Their messages in the dead-letter queue:{" "}
                <span className="font-bold">{matchingCount ?? "unknown"}</span>
              </p>
              {unmatched > 0 && (
                <p className="text-sm text-muted-foreground">
                  {unmatched} more messages in the dead-letter queue have no stored event, so this
                  leaves them. Purge them in Admin → Subscriptions with Purge dead letters.
                </p>
              )}
              {count > 0 && (
                <Button
                  colorScheme="red"
                  onClick={() => setShowConfirm(true)}
                  disabled={executing}
                  isLoading={executing}
                  size="sm"
                >
                  Delete dead-lettered messages
                </Button>
              )}
            </div>
          )}

          {result && (
            <OperationProgress
              processed={result.processed ?? 0}
              succeeded={result.succeeded ?? 0}
              failed={result.failed ?? 0}
              errors={result.errors}
              isComplete={true}
            />
          )}

          <ConfirmDestructiveAction
            isOpen={showConfirm}
            onClose={() => setShowConfirm(false)}
            onConfirm={handleDelete}
            title="Delete Dead-Lettered Messages"
            confirmLabel="Delete dead-lettered"
            description={`This will permanently delete ${count ?? 0} stored dead-lettered events and their ${matchingText} in the dead-letter queue of endpoint "${selected[0] ?? ""}".`}
            confirmText={selected[0] ?? ""}
            isLoading={executing}
          />
        </div>
      </CardContent>
    </Card>
  );
}

export function DeleteEventCard({ endpoints, initialEndpoint }: { endpoints: EndpointOption[]; initialEndpoint?: string }) {
  const [selected, setSelected] = useState<string[]>(initialEndpoint ? [initialEndpoint] : []);
  const [eventId, setEventId] = useState("");
  const [deleting, setDeleting] = useState(false);
  const [showConfirm, setShowConfirm] = useState(false);
  const [deleteResult, setDeleteResult] = useState<
    "success" | "not-found" | "blocks-session" | "failed" | null
  >(null);

  async function handleDelete() {
    if (selected.length === 0 || !eventId.trim()) return;
    setDeleting(true);
    setDeleteResult(null);
    try {
      const client = new api.Client(api.CookieAuth());
      await client.deleteAdminEvent(selected[0], eventId.trim());
      setDeleteResult("success");
      setEventId("");
    } catch (err: any) {
      if (err.status === 404) {
        setDeleteResult("not-found");
      } else if (err.status === 409) {
        // #288: it blocks its session, so the API keeps it.
        setDeleteResult("blocks-session");
      } else {
        setDeleteResult("failed");
      }
    } finally {
      setDeleting(false);
    }
  }

  return (
    <Card>
      <CardHeader>
        <CardTitle>Delete Single Event</CardTitle>
        <CardDescription>
          Delete a specific event by ID
        </CardDescription>
      </CardHeader>
      <CardContent>
        <div className="space-y-4">
          <div className="flex gap-4 items-end">
            <div className="flex-1 max-w-md">
              <Combobox
                options={endpoints}
                value={selected}
                onChange={(v) => {
                  setSelected(v);
                  setDeleteResult(null);
                }}
                placeholder="Select endpoint..."
                label="Endpoint"
                multiple={false}
              />
            </div>
          </div>
          <div className="flex gap-4 items-end">
            <div className="flex-1 max-w-md">
              <label className="block text-sm font-medium text-foreground mb-1">
                Event ID
              </label>
              <Input
                value={eventId}
                onChange={(e) => {
                  setEventId(e.target.value);
                  setDeleteResult(null);
                }}
                placeholder="Enter event ID..."
              />
            </div>
            <Button
              colorScheme="red"
              onClick={() => setShowConfirm(true)}
              disabled={selected.length === 0 || !eventId.trim() || deleting}
              isLoading={deleting}
            >
              Delete Event
            </Button>
          </div>

          {deleteResult === "success" && (
            <div className="bg-green-50 border border-green-200 dark:bg-green-950/30 dark:border-green-900/60 rounded-md p-3 text-green-800 dark:text-green-200 text-sm">
              Event deleted successfully.
            </div>
          )}
          {deleteResult === "not-found" && (
            <div className="bg-red-50 border border-red-200 dark:bg-red-950/30 dark:border-red-900/60 rounded-md p-3 text-red-800 dark:text-red-200 text-sm">
              Event not found.
            </div>
          )}
          {deleteResult === "blocks-session" && (
            <div className="bg-yellow-50 border border-yellow-200 dark:bg-yellow-950/30 dark:border-yellow-900/60 rounded-md p-3 text-sm">
              This event blocks its session, so it was not deleted: deleting it would leave the session
              blocked. Skip it first (on the message, or with Operations → Skip messages), then delete it.
            </div>
          )}
          {deleteResult === "failed" && (
            <div className="bg-red-50 border border-red-200 dark:bg-red-950/30 dark:border-red-900/60 rounded-md p-3 text-red-800 dark:text-red-200 text-sm">
              The event could not be deleted. Try again.
            </div>
          )}

          {/* Typed like every other delete (Spec 038 §6.4); it used to delete on one click. */}
          <ConfirmDestructiveAction
            isOpen={showConfirm}
            onClose={() => setShowConfirm(false)}
            onConfirm={() => {
              setShowConfirm(false);
              void handleDelete();
            }}
            title="Delete Single Event"
            description={`This will permanently delete event "${eventId.trim()}" from endpoint "${selected[0] ?? ""}".`}
            confirmText={eventId.trim()}
            confirmLabel="Delete event"
            caseSensitive
            isLoading={deleting}
          />
        </div>
      </CardContent>
    </Card>
  );
}
