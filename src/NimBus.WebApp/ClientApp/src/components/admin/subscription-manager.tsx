import {
  Fragment,
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import * as api from "api-client";
import { Button } from "components/ui/button";
import { Badge } from "components/ui/badge";
import { Spinner } from "components/ui/spinner";
import { Checkbox } from "components/ui/checkbox";
import { Select } from "components/ui/select";
import { Tooltip } from "components/ui/tooltip";
import ConfirmDestructiveAction from "./confirm-destructive-action";
import ResolverDeadLetterDialog from "./resolver-dead-letter-dialog";
import { cn } from "lib/utils";

const AUTO_REFRESH_MS = 10_000;

/**
 * Purging drains message by message over a single HTTP request. Past this many
 * messages that is the wrong tool — recreate discards the backlog in one
 * management call — so the UI says so rather than letting an operator start a
 * drain that will time out mid-incident.
 */
const PURGE_ADVISORY_THRESHOLD = 5_000;

// Inline SVGs rather than an icon package: the app carries no icon dependency.
function Icon({ path, className }: { path: string; className?: string }) {
  return (
    <svg
      className={className}
      fill="none"
      viewBox="0 0 24 24"
      stroke="currentColor"
      aria-hidden="true"
    >
      <path
        strokeLinecap="round"
        strokeLinejoin="round"
        strokeWidth={2}
        d={path}
      />
    </svg>
  );
}

const CHEVRON_LEFT = "M15 19l-7-7 7-7";
const CHEVRON_UP = "M5 15l7-7 7 7";
const CHEVRON_DOWN = "M19 9l-7 7-7-7";
const REFRESH = "M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15";

type PendingAction =
  | { kind: "delete"; subscription: api.ServiceBusSubscriptionInfo }
  | { kind: "recreate"; subscription: api.ServiceBusSubscriptionInfo }
  | { kind: "purge"; subscription: api.ServiceBusSubscriptionInfo }
  | { kind: "purge-dead-letters"; subscription: api.ServiceBusSubscriptionInfo }
  | {
      kind: "detach-rule";
      subscription: api.ServiceBusSubscriptionInfo;
      ruleName: string;
    };

type RowFeedback = { tone: "ok" | "error"; message: string };
type TopicPlatformFilter = "all" | "platform" | "external";

const nf = new Intl.NumberFormat();

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  const units = ["KB", "MB", "GB", "TB"];
  let value = bytes / 1024;
  let unit = 0;
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024;
    unit++;
  }
  return `${value.toFixed(value < 10 ? 1 : 0)} ${units[unit]}`;
}

/** Non-zero counts get weight; zeros stay quiet so a backlog stands out. */
function Count({ value }: { value: number }) {
  if (!value) return <span className="text-muted-foreground/50">0</span>;
  return (
    <span className="font-semibold text-foreground">{nf.format(value)}</span>
  );
}

/**
 * Dead letters, both queues. Service Bus reports messages it could not
 * auto-forward in the *transfer* DLQ, not the regular one — showing only the
 * latter would read as "zero dead letters" in exactly the failed-forwarding
 * incident this page exists for.
 */
function DeadLetterCount({
  deadLetter,
  transferDeadLetter,
}: {
  deadLetter: number;
  transferDeadLetter: number;
}) {
  const total = deadLetter + transferDeadLetter;
  if (!total) return <span className="text-muted-foreground/50">0</span>;
  return (
    <span
      className="font-semibold text-status-danger"
      title={
        transferDeadLetter
          ? `${nf.format(deadLetter)} dead-lettered, ${nf.format(transferDeadLetter)} could not be auto-forwarded`
          : `${nf.format(deadLetter)} dead-lettered`
      }
    >
      {nf.format(total)}
      {transferDeadLetter > 0 && (
        <span className="font-normal text-muted-foreground">
          {" "}
          ({nf.format(transferDeadLetter)} fwd)
        </span>
      )}
    </span>
  );
}

function StatusBadge({ status }: { status: string }) {
  if (status === "Active")
    return (
      <Badge variant="success" size="sm">
        Active
      </Badge>
    );
  if (status === "ReceiveDisabled")
    return (
      <Badge variant="warning" size="sm">
        Paused
      </Badge>
    );
  if (status === "Disabled")
    return (
      <Badge variant="error" size="sm">
        Disabled
      </Badge>
    );
  if (status === "SendDisabled")
    return (
      <Badge variant="warning" size="sm">
        Send blocked
      </Badge>
    );
  return <Badge size="sm">{status}</Badge>;
}

export default function SubscriptionManager() {
  const client = useMemo(() => new api.Client(api.CookieAuth()), []);

  const [topics, setTopics] = useState<api.ServiceBusTopicOverview[]>([]);
  const [selectedTopic, setSelectedTopic] = useState<string | null>(null);
  const [subscriptions, setSubscriptions] = useState<
    api.ServiceBusSubscriptionInfo[]
  >([]);
  const [loadingTopics, setLoadingTopics] = useState(true);
  const [loadingSubs, setLoadingSubs] = useState(false);
  const [busyRow, setBusyRow] = useState<string | null>(null);
  const [feedback, setFeedback] = useState<Record<string, RowFeedback>>({});
  const [error, setError] = useState<string | null>(null);
  const [autoRefresh, setAutoRefresh] = useState(false);
  const [topicFilter, setTopicFilter] = useState<TopicPlatformFilter>("all");
  const [pending, setPending] = useState<PendingAction | null>(null);
  const [acknowledgeUnknown, setAcknowledgeUnknown] = useState(false);
  const [replaySubscription, setReplaySubscription] = useState<string | null>(null);
  const [pendingTopic, setPendingTopic] = useState<string | null>(null);

  const loadTopics = useCallback(async () => {
    try {
      setTopics(await client.getAdminServicebusTopics());
      setError(null);
    } catch (err: any) {
      setError(err?.message ?? "Failed to load Service Bus topics");
    } finally {
      setLoadingTopics(false);
    }
  }, [client]);

  // Every subscription fetch is stamped and only the newest one is allowed to
  // land. Without this, opening topic A then B before A responds — or two
  // auto-refreshes completing out of order — paints A's rows while
  // selectedTopic is B. Names like Resolver, Deferred and DeferredProcessor
  // appear on every topic, so the table would look right and the next Purge or
  // Delete would post those counts against B.
  const subsRequestId = useRef(0);

  const loadSubscriptions = useCallback(
    async (topicName: string) => {
      const requestId = ++subsRequestId.current;
      setLoadingSubs(true);
      try {
        const next = await client.getAdminServicebusSubscriptions(topicName);
        if (requestId !== subsRequestId.current) return;
        setSubscriptions(next);
        setError(null);
      } catch (err: any) {
        if (requestId !== subsRequestId.current) return;
        setError(err?.message ?? `Failed to load subscriptions on ${topicName}`);
      } finally {
        if (requestId === subsRequestId.current) setLoadingSubs(false);
      }
    },
    [client],
  );

  useEffect(() => {
    loadTopics();
  }, [loadTopics]);

  useEffect(() => {
    if (!autoRefresh) return;
    const id = setInterval(() => {
      loadTopics();
      if (selectedTopic) loadSubscriptions(selectedTopic);
    }, AUTO_REFRESH_MS);
    return () => clearInterval(id);
  }, [autoRefresh, selectedTopic, loadTopics, loadSubscriptions]);

  async function refresh() {
    await loadTopics();
    if (selectedTopic) await loadSubscriptions(selectedTopic);
  }

  async function deleteExternalTopic() {
    if (!pendingTopic) return;
    const topic = pendingTopic;
    setPendingTopic(null);
    setBusyRow(topic);
    try {
      const result = await client.deleteAdminServicebusTopic(topic);
      if (result.succeeded === false) throw new Error(result.message ?? "Delete topic failed.");
      closeTopic();
      await loadTopics();
    } catch (err: any) {
      setError(err?.message ?? "Delete topic failed.");
    } finally {
      setBusyRow(null);
    }
  }

  function openTopic(topicName: string) {
    setSelectedTopic(topicName);
    setSubscriptions([]);
    setFeedback({});
    loadSubscriptions(topicName);
  }

  function closeTopic() {
    // Invalidate anything in flight so a late response can't repopulate the
    // table under the next topic the operator opens.
    subsRequestId.current++;
    setSelectedTopic(null);
    setSubscriptions([]);
  }

  /**
   * Runs one per-subscription action, then reloads both tables so the counts an
   * operator is about to act on next are the counts after this action.
   */
  async function runAction(
    subscriptionName: string,
    label: string,
    action: () => Promise<{
      succeeded?: boolean;
      message?: string;
      errors?: string[];
    }>,
  ) {
    setBusyRow(subscriptionName);
    try {
      const result = await action();
      const errors = result.errors ?? [];
      const failed = result.succeeded === false || errors.length > 0;
      setFeedback((prev) => ({
        ...prev,
        [subscriptionName]: {
          tone: failed ? "error" : "ok",
          // Errors are never dropped in favour of the summary: a partly-failed
          // purge still reports "Purged N message(s)", and the warning it hides
          // can be "the subscription was left Active".
          message: errors.length
            ? [result.message, ...errors].filter(Boolean).join(" — ")
            : result.message || `${label} completed.`,
        },
      }));
      await refresh();
    } catch (err: any) {
      setFeedback((prev) => ({
        ...prev,
        [subscriptionName]: {
          tone: "error",
          message: err?.message ?? `${label} failed.`,
        },
      }));
    } finally {
      setBusyRow(null);
    }
  }

  function togglePause(sub: api.ServiceBusSubscriptionInfo) {
    const name = sub.name ?? "";
    const enable = sub.status !== "Active";
    runAction(name, enable ? "Resume" : "Pause", () =>
      client.postAdminServicebusSubscriptionStatus(selectedTopic!, name, {
        action: enable
          ? api.SubscriptionStatusRequestAction.Enable
          : api.SubscriptionStatusRequestAction.Disable,
      } as api.SubscriptionStatusRequest),
    );
  }

  function restoreRules(sub: api.ServiceBusSubscriptionInfo) {
    const name = sub.name ?? "";
    runAction(name, "Restore rules", () =>
      client.postAdminServicebusSubscriptionRestoreRules(selectedTopic!, name),
    );
  }

  async function confirmPending() {
    if (!pending || !selectedTopic) return;
    const name = pending.subscription.name ?? "";
    const target = pending;
    setPending(null);
    setAcknowledgeUnknown(false);

    switch (target.kind) {
      case "delete":
        await runAction(name, "Delete", () =>
          client.deleteAdminServicebusSubscription(selectedTopic, name),
        );
        break;
      case "recreate":
        await runAction(name, "Recreate", () =>
          client.postAdminServicebusSubscriptionRecreate(selectedTopic, name),
        );
        break;
      case "purge":
        await runAction(name, "Purge", async () => {
          const result = await client.postAdminServicebusSubscriptionPurge(
            selectedTopic,
            name,
          );
          return {
            succeeded: (result.failed ?? 0) === 0,
            message: `Purged ${nf.format(result.succeeded ?? 0)} message(s).`,
            errors: result.errors,
          };
        });
        break;
      case "purge-dead-letters":
        await runAction(name, "Purge dead letters", async () => {
          const result = await client.postAdminServicebusSubscriptionDeadlettersPurge(
            selectedTopic,
            name,
          );
          return {
            succeeded: (result.failed ?? 0) === 0,
            message: `Purged ${nf.format(result.succeeded ?? 0)} dead-lettered message(s).`,
            errors: result.errors,
          };
        });
        break;
      case "detach-rule":
        await runAction(name, "Detach rule", () =>
          client.deleteAdminServicebusSubscriptionRule(
            selectedTopic,
            name,
            target.ruleName,
          ),
        );
        break;
    }
  }

  const activeTopic = topics.find((t) => t.name === selectedTopic);
  const visibleTopics = useMemo(
    () =>
      topics.filter((topic) => {
        if (topicFilter === "all") return true;
        const isInPlatform = topic.isKnownToPlatform === true;
        return topicFilter === "platform" ? isInPlatform : !isInPlatform;
      }),
    [topics, topicFilter],
  );

  return (
    <div className="space-y-6 w-full">
      <div className="flex items-center justify-between gap-4 flex-wrap">
        <p className="text-[13px] text-muted-foreground m-0 flex-1 basis-3xl">
          Live message counts straight from Service Bus. Use this to find where a
          backlog actually sits and clear just that subscription. An
          auto-forwarding subscription (such as each endpoint&apos;s{" "}
          <span className="font-mono">Resolver</span> fan-out) cannot be drained
          while it forwards — &ldquo;Delete &amp; recreate&rdquo; discards its
          backlog in one call, or Pause first (which detaches forwarding) and then
          Purge if you want the subscription itself left alone.
        </p>
        <div className="flex items-center gap-3">
          {selectedTopic === null && (
            <Select
              value={topicFilter}
              onChange={(event) =>
                setTopicFilter(event.target.value as TopicPlatformFilter)
              }
              aria-label="Filter topics by platform membership"
              className="h-8 w-[170px] py-1 text-xs"
            >
              <option value="all">All topics</option>
              <option value="platform">In platform</option>
              <option value="external">Not in platform</option>
            </Select>
          )}
          <Checkbox
            checked={autoRefresh}
            onChange={(e) => setAutoRefresh(e.target.checked)}
            label="Auto-refresh"
            aria-label="Auto-refresh counts every 10 seconds"
          />
          <Button
            variant="outline"
            size="sm"
            onClick={refresh}
            leftIcon={<Icon path={REFRESH} className="w-4 h-4" />}
            isLoading={loadingTopics || loadingSubs}
          >
            Refresh
          </Button>
        </div>
      </div>

      {error && (
        <div className="bg-status-danger-50 border border-status-danger/30 dark:bg-red-950/30 dark:border-red-900/60 rounded-nb-md p-4 text-status-danger-ink dark:text-red-200">
          {error}
        </div>
      )}

      {selectedTopic === null ? (
        <TopicTable
          topics={visibleTopics}
          loading={loadingTopics}
          emptyMessage={
            topics.length === 0
              ? "No topics found in the namespace."
              : "No topics match this filter."
          }
          onOpen={openTopic}
        />
      ) : (
        <div className="space-y-4">
          <div className="flex items-center gap-3 flex-wrap">
            <Button
              variant="ghost"
              size="sm"
              onClick={closeTopic}
              leftIcon={<Icon path={CHEVRON_LEFT} className="w-4 h-4" />}
            >
              All topics
            </Button>
            <h3 className="text-lg font-semibold m-0 font-mono">
              {selectedTopic}
            </h3>
            {activeTopic?.isSystemTopic && (
              <Badge variant="info" size="sm">
                system topic
              </Badge>
            )}
            {activeTopic && <StatusBadge status={activeTopic.status ?? ""} />}
          </div>

          {activeTopic?.isKnownToPlatform === false && (
            // The API refuses mutations on a topic outside the platform
            // topology, so don't offer buttons that are guaranteed to 404.
            <div className="bg-status-warning-50 border border-status-warning/30 dark:bg-yellow-950/30 dark:border-yellow-900/60 rounded-nb-md p-3 text-sm text-status-warning-ink dark:text-yellow-200">
              <span className="font-mono">{selectedTopic}</span> is not part of
              the platform topology. You can delete this topic and all of its
              subscriptions and messages.
              <Button
                className="ml-3"
                colorScheme="red"
                size="sm"
                onClick={() => setPendingTopic(selectedTopic)}
                disabled={busyRow !== null}
              >
                Delete topic
              </Button>
            </div>
          )}

          {loadingSubs && subscriptions.length === 0 ? (
            <div className="flex justify-center py-10">
              <Spinner />
            </div>
          ) : (
            <SubscriptionTable
              subscriptions={subscriptions}
              readOnly={activeTopic?.isKnownToPlatform === false}
              busyRow={busyRow}
              feedback={feedback}
              topicName={selectedTopic}
              onTogglePause={togglePause}
              onRestoreRules={restoreRules}
              onInspectDeadLetters={(sub) => setReplaySubscription(sub.name ?? null)}
              onRequest={(action) => {
                setAcknowledgeUnknown(false);
                setPending(action);
              }}
            />
          )}
        </div>
      )}

      <ConfirmDestructiveAction
        isOpen={pending !== null && !needsAcknowledgement(pending)}
        onClose={() => setPending(null)}
        onConfirm={confirmPending}
        title={pendingTitle(pending)}
        description={pendingDescription(pending)}
        confirmText={pending?.subscription.name ?? ""}
        confirmLabel={pendingConfirmLabel(pending)}
        isLoading={busyRow !== null}
      />

      <ConfirmDestructiveAction
        isOpen={pendingTopic !== null}
        onClose={() => setPendingTopic(null)}
        onConfirm={deleteExternalTopic}
        title="Delete topic"
        description={`Deletes topic "${pendingTopic ?? ""}" with all subscriptions and messages. This cannot be undone.`}
        confirmText={pendingTopic ?? ""}
        confirmLabel="Delete topic permanently"
        isLoading={busyRow !== null}
      />

      {replaySubscription && (
        <ResolverDeadLetterDialog
          client={client}
          subscriptionName={replaySubscription}
          onClose={() => setReplaySubscription(null)}
          onReplayed={refresh}
        />
      )}

      {/* A subscription the platform can't describe has no safe rebuild path, so
          deleting it needs a deliberate second acknowledgement on top of the
          typed-name confirm. */}
      {pending !== null && needsAcknowledgement(pending) && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/50 p-4">
          <div className="bg-card border border-border rounded-nb-md max-w-lg w-full p-5 space-y-4">
            <h4 className="text-base font-bold m-0">
              Delete an unrecognised subscription
            </h4>
            <p className="text-sm text-muted-foreground m-0">
              <span className="font-mono">{pending.subscription.name}</span> is
              not part of the platform topology, so NimBus cannot recreate it.
              Once deleted it must be re-created by hand, and any consumer bound
              to it stops receiving.
            </p>
            <Checkbox
              checked={acknowledgeUnknown}
              onChange={(e) => setAcknowledgeUnknown(e.target.checked)}
              label="I understand this subscription cannot be restored automatically."
            />
            <div className="flex justify-end gap-2">
              <Button
                variant="ghost"
                colorScheme="gray"
                onClick={() => {
                  setPending(null);
                  setAcknowledgeUnknown(false);
                }}
              >
                Cancel
              </Button>
              <Button
                colorScheme="red"
                disabled={!acknowledgeUnknown}
                onClick={confirmPending}
                isLoading={busyRow !== null}
              >
                Delete subscription
              </Button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
}

function needsAcknowledgement(action: PendingAction | null): boolean {
  return action?.kind === "delete" && !action.subscription.canRecreate;
}

function pendingTitle(action: PendingAction | null): string {
  switch (action?.kind) {
    case "delete":
      return "Delete subscription";
    case "recreate":
      return "Delete & recreate subscription";
    case "purge":
      return "Purge subscription";
    case "purge-dead-letters":
      return "Purge dead letters";
    case "detach-rule":
      return "Detach rule";
    default:
      return "";
  }
}

// Deliberately distinct from the row buttons that open the dialog, so it is
// always unambiguous which control is the point of no return.
function pendingConfirmLabel(action: PendingAction | null): string {
  switch (action?.kind) {
    case "delete":
      return "Delete permanently";
    case "recreate":
      return "Delete & recreate now";
    case "purge":
      return "Purge messages";
    case "purge-dead-letters":
      return "Purge dead letters now";
    case "detach-rule":
      return "Detach rule now";
    default:
      return "Confirm";
  }
}

/** Messages a purge can reach: the drain settles active and deferred only. */
function drainableCount(sub: api.ServiceBusSubscriptionInfo): number {
  return (sub.activeMessageCount ?? 0) + (sub.transferMessageCount ?? 0);
}

/** Messages in both dead-letter queues: what Purge dead letters removes. */
function deadLetteredCount(sub: api.ServiceBusSubscriptionInfo): number {
  return (sub.deadLetterMessageCount ?? 0) + (sub.transferDeadLetterMessageCount ?? 0);
}

/**
 * Everything a delete takes with it, dead-letter queues included — a drain
 * leaves those behind, a delete does not. Understating this on the confirmation
 * would be the worst place to be imprecise.
 */
function discardedCount(sub: api.ServiceBusSubscriptionInfo): number {
  return drainableCount(sub) + deadLetteredCount(sub);
}

function pendingDescription(action: PendingAction | null): string {
  if (!action) return "";
  const sub = action.subscription;
  const name = sub.name ?? "";
  const discarded = nf.format(discardedCount(sub));
  const drainable = nf.format(drainableCount(sub));

  switch (action.kind) {
    case "delete":
      return `Deletes the subscription "${name}" and all ${discarded} message(s) it holds, dead-lettered ones included. Nothing is put back — new messages matching its rules are dropped until it is re-created.`;
    case "recreate":
      return `Deletes "${name}" with all ${discarded} message(s) it holds, dead-lettered ones included, and immediately re-provisions it from the platform topology (same rules, same forwarding, same session settings). Messages published during the few seconds it is missing are not captured.`;
    case "purge":
      return `Drains ${drainable} active and deferred message(s) out of "${name}" one batch at a time, leaving the subscription and its dead-letter queues in place.`;
    case "purge-dead-letters":
      return `Removes all ${nf.format(deadLetteredCount(sub))} dead-lettered message(s) from "${name}": its dead-letter queue and its transfer dead-letter queue. Service Bus never empties these on its own. The subscription and its active messages stay as they are, paused or not.`;
    case "detach-rule":
      return `Removes the rule "${action.ruleName}" from "${name}". No new messages enter through it; the ${drainable} already queued still drain. Restore it later with "Restore rules".`;
  }
}

// ───────────────────────── Topic overview ─────────────────────────

/**
 * Sort keys are column-level, not field-level: "Dead-letter" sorts on the same
 * combined figure the cell prints, so the order always matches what is on screen.
 */
const TOPIC_COLUMNS: {
  key: string;
  label: string;
  numeric: boolean;
  value: (topic: api.ServiceBusTopicOverview) => string | number;
}[] = [
  { key: "name", label: "Topic", numeric: false, value: (t) => t.name ?? "" },
  {
    key: "status",
    label: "Status",
    numeric: false,
    value: (t) => t.status ?? "",
  },
  {
    key: "subs",
    label: "Subs",
    numeric: true,
    value: (t) => t.subscriptionCount ?? 0,
  },
  {
    key: "active",
    label: "Active",
    numeric: true,
    value: (t) => t.activeMessageCount ?? 0,
  },
  {
    key: "deadLetter",
    label: "Dead-letter",
    numeric: true,
    value: (t) =>
      (t.deadLetterMessageCount ?? 0) + (t.transferDeadLetterMessageCount ?? 0),
  },
  {
    key: "inTransit",
    label: "In transit",
    numeric: true,
    value: (t) => t.transferMessageCount ?? 0,
  },
  {
    key: "scheduled",
    label: "Scheduled",
    numeric: true,
    value: (t) => t.scheduledMessageCount ?? 0,
  },
  {
    key: "size",
    label: "Size",
    numeric: true,
    value: (t) => t.sizeInBytes ?? 0,
  },
];

function TopicTable({
  topics,
  loading,
  emptyMessage,
  onOpen,
}: {
  topics: api.ServiceBusTopicOverview[];
  loading: boolean;
  emptyMessage: string;
  onOpen: (topicName: string) => void;
}) {
  // Alphabetical by default: the namespace has dozens of topics and an operator
  // arrives knowing the name they are looking for. Backlog-first orders are one
  // click away on the count columns.
  const [sortKey, setSortKey] = useState("name");
  const [sortDir, setSortDir] = useState<"asc" | "desc">("asc");

  const sorted = useMemo(() => {
    const column =
      TOPIC_COLUMNS.find((c) => c.key === sortKey) ?? TOPIC_COLUMNS[0];
    const factor = sortDir === "asc" ? 1 : -1;
    return [...topics].sort((a, b) => {
      const av = column.value(a);
      const bv = column.value(b);
      const cmp =
        typeof av === "number" && typeof bv === "number"
          ? av - bv
          : String(av).localeCompare(String(bv), undefined, {
              numeric: true,
              sensitivity: "base",
            });
      // Ties fall back to the name so the order is stable across refreshes —
      // rows must not shuffle under the pointer while auto-refresh runs.
      if (cmp !== 0) return cmp * factor;
      return (a.name ?? "").localeCompare(b.name ?? "", undefined, {
        sensitivity: "base",
      });
    });
  }, [topics, sortKey, sortDir]);

  function toggleSort(column: (typeof TOPIC_COLUMNS)[number]) {
    if (column.key === sortKey) {
      setSortDir((d) => (d === "asc" ? "desc" : "asc"));
      return;
    }
    setSortKey(column.key);
    // A count column is asked about biggest-first; a name column A–Z.
    setSortDir(column.numeric ? "desc" : "asc");
  }

  if (loading && topics.length === 0) {
    return (
      <div className="flex justify-center py-10">
        <Spinner />
      </div>
    );
  }

  return (
    <div className="border border-border rounded-nb-md overflow-x-auto">
      <table className="min-w-full text-sm">
        <thead className="bg-surface-2 text-muted-foreground">
          <tr>
            {TOPIC_COLUMNS.map((column) => {
              const active = column.key === sortKey;
              return (
                <th
                  key={column.key}
                  aria-sort={
                    active
                      ? sortDir === "asc"
                        ? "ascending"
                        : "descending"
                      : "none"
                  }
                  className={cn(
                    "font-semibold px-3 py-2 select-none",
                    column.numeric ? "text-right" : "text-left",
                  )}
                >
                  <button
                    type="button"
                    onClick={() => toggleSort(column)}
                    className={cn(
                      "inline-flex items-center gap-1 hover:text-primary focus:outline-hidden",
                      active && "text-foreground",
                    )}
                  >
                    {column.label}
                    {active ? (
                      sortDir === "desc" ? (
                        <Icon path={CHEVRON_DOWN} className="w-3 h-3" />
                      ) : (
                        <Icon path={CHEVRON_UP} className="w-3 h-3" />
                      )
                    ) : (
                      <span className="w-3 h-3" />
                    )}
                  </button>
                </th>
              );
            })}
          </tr>
        </thead>
        <tbody>
          {sorted.map((topic) => (
            <tr
              key={topic.name}
              className="border-t border-border hover:bg-surface-2 cursor-pointer"
              onClick={() => onOpen(topic.name ?? "")}
            >
              <td className="px-3 py-2">
                <button
                  type="button"
                  className="font-mono text-left hover:underline"
                  onClick={(e) => {
                    e.stopPropagation();
                    onOpen(topic.name ?? "");
                  }}
                >
                  {topic.name}
                </button>
                {topic.isSystemTopic && (
                  <Badge variant="info" size="sm" className="ml-2">
                    system
                  </Badge>
                )}
                {!topic.isKnownToPlatform && (
                  <Badge variant="warning" size="sm" className="ml-2">
                    not in platform
                  </Badge>
                )}
              </td>
              <td className="px-3 py-2">
                <StatusBadge status={topic.status ?? ""} />
              </td>
              <td className="px-3 py-2 text-right tabular-nums">
                {topic.subscriptionCount}
              </td>
              <td className="px-3 py-2 text-right tabular-nums">
                <Count value={topic.activeMessageCount ?? 0} />
              </td>
              <td className="px-3 py-2 text-right tabular-nums">
                <DeadLetterCount
                  deadLetter={topic.deadLetterMessageCount ?? 0}
                  transferDeadLetter={topic.transferDeadLetterMessageCount ?? 0}
                />
              </td>
              <td className="px-3 py-2 text-right tabular-nums">
                <Count value={topic.transferMessageCount ?? 0} />
              </td>
              <td className="px-3 py-2 text-right tabular-nums">
                <Count value={topic.scheduledMessageCount ?? 0} />
              </td>
              <td className="px-3 py-2 text-right tabular-nums text-muted-foreground">
                {formatBytes(topic.sizeInBytes ?? 0)}
              </td>
            </tr>
          ))}
          {topics.length === 0 && (
            <tr>
              <td
                colSpan={8}
                className="px-3 py-6 text-center text-muted-foreground"
              >
                {emptyMessage}
              </td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  );
}

// ───────────────────────── Subscription drill-down ─────────────────────────

function SubscriptionTable({
  subscriptions,
  topicName,
  readOnly,
  busyRow,
  feedback,
  onTogglePause,
  onRestoreRules,
  onInspectDeadLetters,
  onRequest,
}: {
  subscriptions: api.ServiceBusSubscriptionInfo[];
  topicName: string;
  readOnly: boolean;
  busyRow: string | null;
  feedback: Record<string, RowFeedback>;
  onTogglePause: (sub: api.ServiceBusSubscriptionInfo) => void;
  onRestoreRules: (sub: api.ServiceBusSubscriptionInfo) => void;
  onInspectDeadLetters: (sub: api.ServiceBusSubscriptionInfo) => void;
  onRequest: (action: PendingAction) => void;
}) {
  return (
    <div className="border border-border rounded-nb-md overflow-x-auto">
      <table className="min-w-full text-sm">
        <thead className="bg-surface-2 text-muted-foreground">
          <tr>
            <th className="text-left font-semibold px-3 py-2">Subscription</th>
            <th className="text-left font-semibold px-3 py-2">Rules</th>
            <th className="text-left font-semibold px-3 py-2">Forwards to</th>
            <th className="text-left font-semibold px-3 py-2">Status</th>
            <th className="text-right font-semibold px-3 py-2">Active</th>
            <th className="text-right font-semibold px-3 py-2">Dead-letter</th>
            <th className="text-right font-semibold px-3 py-2">In transit</th>
            <th className="text-left font-semibold px-3 py-2">Actions</th>
          </tr>
        </thead>
        <tbody>
          {subscriptions.map((sub) => {
            const name = sub.name ?? "";
            const busy = busyRow === name || readOnly;
            const forwarding = !!sub.forwardTo;
            const detached = !sub.forwardTo && !!sub.expectedForwardTo;
            const backlog =
              (sub.activeMessageCount ?? 0) + (sub.transferMessageCount ?? 0);
            const row = feedback[name];
            const missing = sub.missingRuleNames ?? [];
            const canInspectDeadLetters =
              topicName.toLowerCase() === "resolver" &&
              sub.requiresSession === true &&
              !sub.forwardTo &&
              (sub.deadLetterMessageCount ?? 0) > 0;

            return (
              <Fragment key={name}>
                <tr className="border-t border-border align-top">
                  <td className="px-3 py-2">
                    <div className="font-mono">{name}</div>
                    <div className="text-xs text-muted-foreground">
                      {sub.requiresSession ? "sessions on" : "sessions off"}
                      {!sub.canRecreate && " · not in platform topology"}
                    </div>
                  </td>
                  <td className="px-3 py-2">
                    <div className="flex flex-wrap gap-1">
                      {(sub.ruleNames ?? []).map((rule) =>
                        // Detach is only offered where Restore rules can put it
                        // back. A $Default that is a subscription's whole
                        // routing, or any rule on a hand-made subscription,
                        // would be a one-way removal — show it, don't arm it.
                        (sub.detachableRuleNames ?? []).includes(rule) ? (
                          <button
                            key={rule}
                            type="button"
                            disabled={busy}
                            onClick={() =>
                              onRequest({
                                kind: "detach-rule",
                                subscription: sub,
                                ruleName: rule,
                              })
                            }
                            title={`Detach rule "${rule}" so no new messages enter through it. Reversible with Restore rules.`}
                            className="font-mono text-xs px-1.5 py-0.5 rounded-sm bg-surface-2 hover:bg-status-danger-50 hover:text-status-danger-ink disabled:opacity-50"
                          >
                            {rule} ✕
                          </button>
                        ) : (
                          <span
                            key={rule}
                            title="Not part of the platform topology, so NimBus can't restore it — remove it from Topology → Catalog drift if it really is deprecated."
                            className="font-mono text-xs px-1.5 py-0.5 rounded-sm bg-surface-2 text-muted-foreground"
                          >
                            {rule}
                          </span>
                        ),
                      )}
                      {(sub.ruleNames ?? []).length === 0 && (
                        <span className="text-xs text-muted-foreground">
                          none — receives nothing
                        </span>
                      )}
                    </div>
                    {missing.length > 0 && (
                      <div className="text-xs text-status-warning-ink dark:text-yellow-300 mt-1">
                        missing: {missing.join(", ")}
                      </div>
                    )}
                  </td>
                  <td className="px-3 py-2 font-mono text-xs">
                    {forwarding ? (
                      `→ ${sub.forwardTo}`
                    ) : detached ? (
                      // Pausing a forwarding subscription detaches its
                      // destination; say so, so a pause nobody resumed is
                      // visible rather than looking like a terminal sub.
                      <span className="text-status-warning-ink dark:text-yellow-300">
                        → {sub.expectedForwardTo} (detached)
                      </span>
                    ) : (
                      "—"
                    )}
                  </td>
                  <td className="px-3 py-2">
                    <StatusBadge status={sub.status ?? ""} />
                  </td>
                  <td className="px-3 py-2 text-right tabular-nums">
                    <Count value={sub.activeMessageCount ?? 0} />
                  </td>
                  <td className="px-3 py-2 text-right tabular-nums">
                    <DeadLetterCount
                      deadLetter={sub.deadLetterMessageCount ?? 0}
                      transferDeadLetter={sub.transferDeadLetterMessageCount ?? 0}
                    />
                  </td>
                  <td className="px-3 py-2 text-right tabular-nums">
                    <Count value={sub.transferMessageCount ?? 0} />
                  </td>
                  <td className="px-3 py-2">
                    {/* min-w keeps the column from being squeezed to a single
                        button per line by a wide Rules cell next to it. */}
                    <div className="flex flex-wrap gap-1.5 items-center min-w-[200px]">
                      {canInspectDeadLetters && (
                        <Button
                          variant="outline"
                          size="xs"
                          disabled={busyRow !== null || readOnly}
                          onClick={() => onInspectDeadLetters(sub)}
                        >
                          Inspect dead letters
                        </Button>
                      )}
                      <Button
                        variant="outline"
                        size="xs"
                        disabled={busy}
                        onClick={() => onTogglePause(sub)}
                        title={
                          sub.status === "Active"
                            ? forwarding
                              ? `Stop delivery and detach forwarding to ${sub.forwardTo} — messages collect here instead of moving on. Reversible.`
                              : "Stop delivery — messages keep arriving but are not handed to consumers. Reversible."
                            : detached
                              ? `Resume delivery and restore forwarding to ${sub.expectedForwardTo}`
                              : "Resume delivery"
                        }
                      >
                        {sub.status === "Active" ? "Pause" : "Resume"}
                      </Button>

                      {missing.length > 0 && (
                        <Button
                          variant="outline"
                          size="xs"
                          disabled={busy}
                          onClick={() => onRestoreRules(sub)}
                          title="Re-attach the expected rules missing from this subscription"
                        >
                          Restore rules
                        </Button>
                      )}

                      {forwarding ? (
                        <Tooltip content="Service Bus rejects receive on an auto-forwarding subscription. Use Delete & recreate to empty it.">
                          <Button variant="outline" size="xs" disabled>
                            Purge
                          </Button>
                        </Tooltip>
                      ) : (
                        <Button
                          variant="outline"
                          size="xs"
                          disabled={busy || backlog === 0}
                          onClick={() =>
                            onRequest({ kind: "purge", subscription: sub })
                          }
                          title={
                            backlog > PURGE_ADVISORY_THRESHOLD
                              ? `${nf.format(backlog)} messages — draining that many one batch at a time is slow; Delete & recreate is faster`
                              : "Drain every message, leaving the subscription in place"
                          }
                        >
                          Purge
                        </Button>
                      )}

                      {deadLetteredCount(sub) > 0 && (
                        <Button
                          variant="outline"
                          size="xs"
                          disabled={busy}
                          onClick={() =>
                            onRequest({ kind: "purge-dead-letters", subscription: sub })
                          }
                          title="Remove every message from both dead-letter queues, leaving the subscription and its active messages alone"
                        >
                          Purge dead letters
                        </Button>
                      )}

                      {sub.canRecreate && (
                        <Button
                          variant="outline"
                          colorScheme="red"
                          size="xs"
                          disabled={busy}
                          onClick={() =>
                            onRequest({ kind: "recreate", subscription: sub })
                          }
                          title="Delete and immediately re-provision — the fastest way to discard a large backlog"
                        >
                          Delete &amp; recreate
                        </Button>
                      )}

                      <Button
                        variant="ghost"
                        colorScheme="red"
                        size="xs"
                        disabled={busy}
                        onClick={() =>
                          onRequest({ kind: "delete", subscription: sub })
                        }
                        title="Delete without putting it back"
                      >
                        Delete
                      </Button>
                    </div>
                  </td>
                </tr>
                {row && (
                  <tr className="bg-surface-2">
                    <td colSpan={8} className="px-3 py-1.5">
                      <span
                        className={cn(
                          "text-xs",
                          row.tone === "error"
                            ? "text-status-danger"
                            : "text-status-success",
                        )}
                      >
                        {row.message}
                      </span>
                    </td>
                  </tr>
                )}
              </Fragment>
            );
          })}
          {subscriptions.length === 0 && (
            <tr>
              <td
                colSpan={8}
                className="px-3 py-6 text-center text-muted-foreground"
              >
                No subscriptions on this topic.
              </td>
            </tr>
          )}
        </tbody>
      </table>
    </div>
  );
}
