import { useCallback, useEffect, useRef, useState } from "react";
import * as api from "api-client";
import {
  createMonitorClient,
  feedAccessError,
  type FeedAccessError,
} from "lib/monitor-access";

const REFRESH_MS = 5_000;
// How many samples to keep per endpoint — drives the sparkline and the
// "rate / min" calculation. 12 samples at 5 s each = 60 s of history.
const HISTORY_LEN = 12;
// Fleet-wide totals kept for the telemetry chart: 120 samples at 5 s = the
// last 10 minutes. In-memory only, so the chart refills after a reload.
export const TELEMETRY_LEN = 120;
// New-failure pulse window. A card that transitions from no-failures to
// failing gets a pulsing ring for this long before settling into static red.
export const NEW_FAILURE_MS = 60_000;
// Acks auto-expire after this long (the server enforces it; this only shapes
// the optimistic record). The operator can also un-ack manually.
export const ACK_TTL_MS = 4 * 60 * 60 * 1000;
// If we haven't successfully refreshed for this long, dim the wall and
// surface a "connection lost" banner — a frozen page that looks healthy is
// the worst possible failure mode for a monitoring wall.
export const STALE_AFTER_MS = 30_000;

// Acks used to live in each browser's localStorage under this key. They are
// shared server-side now; the stale per-device copy is removed on load.
const LEGACY_ACK_STORAGE_KEY = "nb.monitor.acks.v1";

export type EndpointSample = {
  t: number;
  failed: number;
  pending: number;
  deferred: number;
};

/** Fleet-wide totals at one poll, for the telemetry chart. */
export type FleetSample = {
  t: number;
  /** Sum of failed messages (dead-lettered included) across all endpoints. */
  failed: number;
  /** Sum of pending + deferred messages across all endpoints. */
  backlog: number;
};

export type AckRecord = {
  /** Operator-supplied reason (free text). Empty string allowed. */
  reason: string;
  /** Wall-clock ms when the ack happened. */
  ackedAt: number;
  /** Failed count at the moment of ack — the server clears the ack on recovery. */
  failedAtAck: number;
  /** Wall-clock ms when the server lets the ack lapse. */
  expiresAt: number;
  /** Who acked it, when the server knows. */
  acknowledgedBy?: string;
  /** Server token, new on every ack. Undefined while an optimistic ack is in flight. */
  id?: string;
};

export type MonitorEndpoint = {
  id: string;
  /** Latest snapshot from the API. */
  status: api.EndpointStatusCount;
  /** Recent history (oldest → newest), capped at HISTORY_LEN. */
  samples: EndpointSample[];
  /**
   * Wall-clock ms when the endpoint's longest-standing open failure was
   * recorded (server `oldestFailureAt`). Falls back to when this page saw
   * it start failing only if the server sends no timestamp.
   */
  firstFailureAt?: number;
  /**
   * Set only when this page saw the endpoint go from no failures to failing.
   * Failures already present on the first poll leave it undefined, so a
   * reload doesn't make every failing card look new.
   */
  failureObservedAt?: number;
  /** Truthy only inside the NEW_FAILURE_MS window after an observed new failure. */
  isFreshFailure: boolean;
  /** Ack record, if the operator has silenced this endpoint. */
  ack?: AckRecord;
  /** Failures per minute over the available sample window, or undefined. */
  ratePerMin?: number;
  /**
   * Estimated sessions blocked by a failed, dead-lettered or pending hand-off message; undefined
   * when the server cannot tell.
   */
  blockedSessionCount?: number;
  /** Heartbeat liveness, when the endpoint takes part in heartbeats. */
  heartbeat?: api.MonitorFeedHeartbeat;
};

/** What the status feed says about the deployment and the caller. */
export type MonitorFeedInfo = {
  environment?: string;
  platformName?: string;
  /** True when the page reads the feed with a dashboard token (read-only). */
  tokenAccess: boolean;
  /** True when the server could not read the store and returned an earlier result. */
  serverStale: boolean;
  /** When the server read the data it returned. */
  generatedAt?: number;
};

export type TickerEvent = {
  id: string;
  t: number;
  endpoint: string;
  kind: "failure" | "recovery" | "ack" | "unack";
  detail?: string;
};

const TICKER_MAX = 12;

function removeLegacyAcks(): void {
  try {
    window.localStorage?.removeItem(LEGACY_ACK_STORAGE_KEY);
  } catch {
    // localStorage can be unavailable (private mode, blocked storage); ignore.
  }
}

function ackFromApi(ack: api.MonitorAcknowledgement): AckRecord {
  return {
    reason: ack.reason ?? "",
    ackedAt: ack.acknowledgedAt?.valueOf() ?? Date.now(),
    failedAtAck: ack.failedCountAtAcknowledgement ?? 0,
    expiresAt: ack.expiresAt?.valueOf() ?? Date.now() + ACK_TTL_MS,
    acknowledgedBy: ack.acknowledgedBy ?? undefined,
    id: ack.acknowledgementId,
  };
}

function errorMessage(err: unknown): string {
  return err instanceof Error ? err.message : "Request failed";
}

export interface UseMonitorDataResult {
  endpoints: MonitorEndpoint[];
  lastRefreshAt: number | undefined;
  loading: boolean;
  error: string | undefined;
  /** Set when the feed refused the caller: no valid session or token, or no Reader role. */
  accessError?: FeedAccessError;
  /** Deployment and caller details from the last successful poll. */
  feed?: MonitorFeedInfo;
  /** Last failed ACK / un-ACK request, cleared by the next successful one. */
  actionError?: string;
  isStale: boolean;
  ticker: TickerEvent[];
  /** Fleet totals per poll, oldest → newest, capped at TELEMETRY_LEN. */
  telemetry: FleetSample[];
  ack: (endpointId: string, reason?: string) => Promise<void>;
  unack: (endpointId: string) => Promise<void>;
  refresh: () => Promise<void>;
}

/**
 * Polls the Monitor status feed (`/api/monitor/feed/status`: counts, acknowledgements, blocked
 * sessions and heartbeat for every endpoint) on a fixed cadence, with the session or with the
 * dashboard token, and decorates each endpoint with the derived data the Monitor page needs:
 *  - rolling sample history (drives sparklines + rate / min)
 *  - the oldest open failure's timestamp from the server (drives "Failing
 *    for…" and the header T+ clock), plus the page-observed transition that
 *    drives the new-failure pulse
 *  - operator acks, stored server-side so an ACK on a laptop also silences
 *    the wall PC; changes made on other devices show up in the ticker
 *  - a small in-memory ticker fed by sample-diff transitions
 *
 * Designed to keep working on a fixed schedule even if individual fetches
 * fail (we just keep the last good snapshot and flip `isStale` once the gap
 * crosses STALE_AFTER_MS).
 *
 * Polling pauses while the tab is hidden (no point burning API + store
 * round-trips for a wall nobody sees) and refreshes immediately on resume.
 * Staleness is measured from the later of last-refresh and resume, so a
 * long-hidden tab doesn't flash the "connection lost" banner while its
 * catch-up fetch is in flight — real failures still surface after
 * STALE_AFTER_MS of visible, failing polls.
 */
export function useMonitorData(
  options: { token?: string } = {},
): UseMonitorDataResult {
  const { token } = options;
  const [endpoints, setEndpoints] = useState<MonitorEndpoint[]>([]);
  const [lastRefreshAt, setLastRefreshAt] = useState<number | undefined>();
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | undefined>();
  const [accessError, setAccessError] = useState<FeedAccessError | undefined>();
  const [feed, setFeed] = useState<MonitorFeedInfo | undefined>();
  const [now, setNow] = useState(Date.now());
  const [ticker, setTicker] = useState<TickerEvent[]>([]);
  const [telemetry, setTelemetry] = useState<FleetSample[]>([]);
  const [actionError, setActionError] = useState<string | undefined>();
  // Wall-clock ms when the tab last became visible again; staleness is
  // measured from max(lastRefreshAt, resumedAt) so resume doesn't false-flag.
  const [resumedAt, setResumedAt] = useState(0);

  // Per-endpoint derived state we maintain across refreshes. Stored in refs
  // because they change on every poll tick but only need to bake into the
  // returned MonitorEndpoint[] — re-rendering on every sample-history mutation
  // would be wasteful.
  const samplesRef = useRef<Record<string, EndpointSample[]>>({});
  const firstFailureRef = useRef<Record<string, number>>({});
  const observedFailureRef = useRef<Record<string, number>>({});
  // Last known server acks, overlaid with this page's own in-flight changes.
  const acksRef = useRef<Record<string, AckRecord>>({});
  // False until the first ack list arrives: acks that already exist on load
  // are not "news" and stay out of the ticker.
  const acksLoadedRef = useRef(false);
  // Local ack/un-ack bookkeeping so a poll whose GET raced a PUT/DELETE can't
  // overwrite (and ticker-report) the change this page just made.
  const mutationSeqRef = useRef(0);
  const lastMutationSeqRef = useRef<Record<string, number>>({});
  const inFlightRef = useRef<Set<string>>(new Set());
  const clientRef = useRef<api.Client | null>(null);

  const getClient = useCallback((): api.Client => {
    if (!clientRef.current) {
      clientRef.current = createMonitorClient(token);
    }
    return clientRef.current;
  }, [token]);

  useEffect(() => {
    removeLegacyAcks();
  }, []);

  const pushTicker = useCallback((event: Omit<TickerEvent, "id">) => {
    setTicker((prev) => {
      const next: TickerEvent = {
        ...event,
        id: `${event.t}-${event.endpoint}-${event.kind}`,
      };
      return [next, ...prev].slice(0, TICKER_MAX);
    });
  }, []);

  /**
   * Replaces the known acks with the server's list, except for endpoints this
   * page is mid-way through changing (`keepLocal`). Changes made elsewhere —
   * another device's ack or un-ack, or the server clearing an ack on expiry
   * or recovery — are reported in the ticker.
   */
  const mergeServerAcks = useCallback(
    (
      serverAcks: api.MonitorAcknowledgement[],
      t: number,
      failedById: Map<string, number>,
      keepLocal: (id: string) => boolean,
    ) => {
      const previous = acksRef.current;
      const next: Record<string, AckRecord> = {};
      for (const serverAck of serverAcks) {
        const id = serverAck.endpointId ?? "";
        if (id && !keepLocal(id)) next[id] = ackFromApi(serverAck);
      }
      for (const [id, local] of Object.entries(previous)) {
        if (keepLocal(id)) next[id] = local;
      }

      if (acksLoadedRef.current) {
        for (const [id, before] of Object.entries(previous)) {
          if (next[id] || keepLocal(id)) continue;
          pushTicker({
            t,
            endpoint: id,
            kind: "unack",
            detail:
              before.failedAtAck > 0 && failedById.get(id) === 0
                ? "auto-cleared · recovered"
                : t >= before.expiresAt
                  ? "auto-cleared · 4h expiry"
                  : undefined,
          });
        }
        for (const [id, after] of Object.entries(next)) {
          if (keepLocal(id) || previous[id]?.id === after.id) continue;
          pushTicker({
            t,
            endpoint: id,
            kind: "ack",
            detail: describeAck(after),
          });
        }
      }

      acksRef.current = next;
      acksLoadedRef.current = true;
    },
    [pushTicker],
  );

  const fetchOnce = useCallback(async (): Promise<void> => {
    const client = getClient();

    const seqAtStart = mutationSeqRef.current;
    const inFlightAtStart = new Set(inFlightRef.current);
    const status = await client.getMonitorFeedStatus();
    const feedEndpoints = (status.endpoints ?? []).filter((e) => e.status);
    const snapshot = feedEndpoints.map((e) => e.status!);
    const serverAcks = feedEndpoints
      .map((e) => e.acknowledgement)
      .filter((a): a is api.MonitorAcknowledgement => a !== undefined && a !== null);
    const extrasById = new Map(
      feedEndpoints.map((e) => [e.status!.endpointId ?? "", e]),
    );
    setFeed({
      environment: status.environment ?? undefined,
      platformName: status.platformName ?? undefined,
      tokenAccess: status.tokenAccess ?? false,
      serverStale: status.stale ?? false,
      generatedAt: status.generatedAtUtc?.valueOf(),
    });
    setAccessError(undefined);
    const t = Date.now();
    const failedById = new Map(
      snapshot.map((s) => [s.endpointId ?? "", s.failedCount ?? 0]),
    );
    mergeServerAcks(serverAcks, t, failedById, (id) =>
      inFlightAtStart.has(id) ||
      inFlightRef.current.has(id) ||
      (lastMutationSeqRef.current[id] ?? 0) > seqAtStart,
    );

    const decorated: MonitorEndpoint[] = snapshot.map((status) => {
      const id = status.endpointId ?? "";
      const failed = status.failedCount ?? 0;
      const pending = status.pendingCount ?? 0;
      const deferred = status.deferredCount ?? 0;
      const sample: EndpointSample = { t, failed, pending, deferred };

      // Append + truncate the sample history for this endpoint.
      const prevSamples = samplesRef.current[id] ?? [];
      const samples = [...prevSamples, sample].slice(-HISTORY_LEN);
      samplesRef.current[id] = samples;

      // Detect failure transitions against the prior sample (if any).
      const prior = prevSamples.length
        ? prevSamples[prevSamples.length - 1]
        : undefined;
      if (prior) {
        if (prior.failed === 0 && failed > 0) {
          firstFailureRef.current[id] = t;
          observedFailureRef.current[id] = t;
          pushTicker({
            t,
            endpoint: id,
            kind: "failure",
            detail: describeFailure(failed, status.deadletterCount ?? 0),
          });
        } else if (prior.failed > 0 && failed === 0) {
          delete firstFailureRef.current[id];
          delete observedFailureRef.current[id];
          pushTicker({ t, endpoint: id, kind: "recovery" });
        }
      } else if (failed > 0 && firstFailureRef.current[id] === undefined) {
        // Already failing on the first poll: the page can't know when it
        // started, so this is only the fallback for a server that sends no
        // oldestFailureAt (e.g. a storage-unavailable stub).
        firstFailureRef.current[id] = t;
      }

      const ack: AckRecord | undefined = acksRef.current[id];

      // The server's oldest open failure survives reloads and is shared by
      // every client; the page-observed transition is only a fallback.
      const serverSince = status.oldestFailureAt?.valueOf();
      const firstFailureAt =
        failed > 0 && serverSince !== undefined
          ? serverSince
          : firstFailureRef.current[id];
      const failureObservedAt = observedFailureRef.current[id];
      const isFreshFailure = isFresh(failureObservedAt, t);

      // Rate per minute — use the oldest sample we still have to spread the
      // delta over the longest window available, then normalize to /min.
      let ratePerMin: number | undefined;
      if (samples.length >= 2) {
        const oldest = samples[0];
        const dt = sample.t - oldest.t;
        if (dt > 0) {
          ratePerMin = ((failed - oldest.failed) / dt) * 60_000;
        }
      }

      const extras = extrasById.get(id);
      return {
        id,
        status,
        samples,
        firstFailureAt,
        failureObservedAt,
        isFreshFailure,
        ack,
        ratePerMin,
        blockedSessionCount: extras?.blockedSessionCount ?? undefined,
        heartbeat: extras?.heartbeat ?? undefined,
      };
    });

    const fleet: FleetSample = { t, failed: 0, backlog: 0 };
    for (const e of decorated) {
      fleet.failed += e.status.failedCount ?? 0;
      fleet.backlog +=
        (e.status.pendingCount ?? 0) + (e.status.deferredCount ?? 0);
    }
    setTelemetry((prev) => [...prev, fleet].slice(-TELEMETRY_LEN));

    setEndpoints(decorated);
    setLastRefreshAt(t);
    setError(undefined);
    setLoading(false);
  }, [getClient, mergeServerAcks, pushTicker]);

  // Initial load + polling loop. We intentionally swallow per-tick errors and
  // surface them on the page instead of throwing, so a transient API blip
  // doesn't tear down the auto-refresh interval.
  useEffect(() => {
    let cancelled = false;
    const run = async () => {
      try {
        await fetchOnce();
      } catch (err) {
        if (!cancelled) {
          setError(err instanceof Error ? err.message : "Failed to refresh");
          setAccessError(feedAccessError(err));
          setLoading(false);
        }
      }
    };
    void run();
    const handle = window.setInterval(() => {
      // Hidden tab: skip the tick — the wall isn't visible and browsers
      // throttle timers anyway; the visibilitychange handler below catches up.
      if (document.hidden) return;
      void run();
    }, REFRESH_MS);
    const onVisibilityChange = () => {
      if (document.hidden) return;
      // Refresh immediately on resume and mark the resume time so the
      // staleness banner doesn't fire while the catch-up fetch runs.
      setResumedAt(Date.now());
      void run();
    };
    document.addEventListener("visibilitychange", onVisibilityChange);
    return () => {
      cancelled = true;
      window.clearInterval(handle);
      document.removeEventListener("visibilitychange", onVisibilityChange);
    };
  }, [fetchOnce]);

  // Drive "is this stale?" + the new-failure pulse window off a 1 Hz tick.
  // We re-derive isFreshFailure each render rather than scheduling per-card
  // timeouts because the cardinality is small and the math is trivial.
  useEffect(() => {
    const handle = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(handle);
  }, []);

  const isStale =
    lastRefreshAt !== undefined &&
    now - Math.max(lastRefreshAt, resumedAt) > STALE_AFTER_MS;

  const decoratedEndpoints: MonitorEndpoint[] = endpoints.map((e) => ({
    ...e,
    isFreshFailure: isFresh(e.failureObservedAt, now),
  }));

  /**
   * Applies a local ack change optimistically, sends it, then settles on the
   * server's answer — or restores the previous state and reports the error.
   * Only the latest change per endpoint may settle it.
   */
  const mutateAck = useCallback(
    async (
      endpointId: string,
      optimistic: AckRecord | undefined,
      send: (client: api.Client) => Promise<AckRecord | undefined>,
    ) => {
      const seq = ++mutationSeqRef.current;
      lastMutationSeqRef.current[endpointId] = seq;
      inFlightRef.current.add(endpointId);
      const previous = acksRef.current[endpointId];

      const apply = (record: AckRecord | undefined) => {
        if (record) acksRef.current[endpointId] = record;
        else delete acksRef.current[endpointId];
        setEndpoints((prev) =>
          prev.map((e) => (e.id === endpointId ? { ...e, ack: record } : e)),
        );
      };

      apply(optimistic);
      try {
        const settled = await send(getClient());
        if (lastMutationSeqRef.current[endpointId] === seq) apply(settled);
        setActionError(undefined);
      } catch (err) {
        if (lastMutationSeqRef.current[endpointId] === seq) apply(previous);
        setActionError(`${endpointId}: ${errorMessage(err)}`);
      } finally {
        if (lastMutationSeqRef.current[endpointId] === seq) {
          inFlightRef.current.delete(endpointId);
        }
      }
    },
    [getClient],
  );

  const ack = useCallback(
    async (endpointId: string, reason: string = "") => {
      const target = endpoints.find((e) => e.id === endpointId);
      const now = Date.now();
      pushTicker({
        t: now,
        endpoint: endpointId,
        kind: "ack",
        detail: reason || undefined,
      });
      await mutateAck(
        endpointId,
        {
          reason,
          ackedAt: now,
          failedAtAck: target?.status.failedCount ?? 0,
          expiresAt: now + ACK_TTL_MS,
        },
        async (client) =>
          ackFromApi(
            await client.putMonitorAcknowledgement(
              endpointId,
              new api.MonitorAcknowledgementRequest({ reason }),
            ),
          ),
      );
    },
    [endpoints, mutateAck, pushTicker],
  );

  const unack = useCallback(
    async (endpointId: string) => {
      pushTicker({
        t: Date.now(),
        endpoint: endpointId,
        kind: "unack",
      });
      await mutateAck(endpointId, undefined, async (client) => {
        await client.deleteMonitorAcknowledgement(endpointId);
        return undefined;
      });
    },
    [mutateAck, pushTicker],
  );

  const refresh = useCallback(async () => {
    setLoading(true);
    try {
      await fetchOnce();
    } catch (err) {
      setError(err instanceof Error ? err.message : "Failed to refresh");
      setAccessError(feedAccessError(err));
      setLoading(false);
    }
  }, [fetchOnce]);

  return {
    endpoints: decoratedEndpoints,
    lastRefreshAt,
    loading,
    error,
    accessError,
    feed,
    actionError,
    isStale,
    ticker,
    telemetry,
    ack,
    unack,
    refresh,
  };
}

function isFresh(failureObservedAt: number | undefined, now: number): boolean {
  return (
    failureObservedAt !== undefined && now - failureObservedAt < NEW_FAILURE_MS
  );
}

/** Ticker detail for an ack placed on another device. */
function describeAck(ack: AckRecord): string | undefined {
  const parts = [ack.reason, ack.acknowledgedBy && `by ${ack.acknowledgedBy}`];
  return parts.filter(Boolean).join(" · ") || undefined;
}

/** Ticker detail for a new failure; dead-letters are part of the failed count. */
function describeFailure(failed: number, deadlettered: number): string {
  const messages = `${failed.toLocaleString()} message${failed === 1 ? "" : "s"}`;
  return deadlettered > 0
    ? `${messages} · ${deadlettered.toLocaleString()} dead-lettered`
    : messages;
}
