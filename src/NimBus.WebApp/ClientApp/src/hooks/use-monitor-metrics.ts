import { useEffect, useRef, useState } from "react";
import type * as api from "api-client";
import { createMonitorClient } from "lib/monitor-access";

/** The last-hour metrics refresh with the server's 30 s cache. */
export const METRICS_REFRESH_MS = 30_000;
/** The history (3 days, 24 h of timings) refreshes with the server's 5 min cache. */
export const HISTORY_REFRESH_MS = 5 * 60_000;

export interface UseMonitorMetricsResult {
  metrics?: api.MonitorFeedMetrics;
  history?: api.MonitorFeedHistory;
}

/**
 * Polls the Monitor's metrics (every 30 s) and history (every 5 min) feeds. A failed poll keeps
 * the last good data; the status poll owns the connection-lost banner. Pauses while the tab is
 * hidden and catches up when it is shown again, like the status poll.
 */
export function useMonitorMetrics(options: { token?: string } = {}): UseMonitorMetricsResult {
  const { token } = options;
  const [metrics, setMetrics] = useState<api.MonitorFeedMetrics | undefined>();
  const [history, setHistory] = useState<api.MonitorFeedHistory | undefined>();
  const clientRef = useRef<api.Client | null>(null);

  useEffect(() => {
    clientRef.current = createMonitorClient(token);
    let cancelled = false;

    const loadMetrics = async () => {
      try {
        const next = await clientRef.current!.getMonitorFeedMetrics();
        if (!cancelled) setMetrics(next);
      } catch {
        // Keep the last good data.
      }
    };
    const loadHistory = async () => {
      try {
        const next = await clientRef.current!.getMonitorFeedHistory();
        if (!cancelled) setHistory(next);
      } catch {
        // Keep the last good data.
      }
    };

    void loadMetrics();
    void loadHistory();
    const metricsTimer = window.setInterval(() => {
      if (!document.hidden) void loadMetrics();
    }, METRICS_REFRESH_MS);
    const historyTimer = window.setInterval(() => {
      if (!document.hidden) void loadHistory();
    }, HISTORY_REFRESH_MS);
    const onVisibilityChange = () => {
      if (document.hidden) return;
      void loadMetrics();
      void loadHistory();
    };
    document.addEventListener("visibilitychange", onVisibilityChange);

    return () => {
      cancelled = true;
      window.clearInterval(metricsTimer);
      window.clearInterval(historyTimer);
      document.removeEventListener("visibilitychange", onVisibilityChange);
    };
  }, [token]);

  return { metrics, history };
}
