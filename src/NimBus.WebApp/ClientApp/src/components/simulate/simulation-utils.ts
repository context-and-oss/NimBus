import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import * as api from "api-client";
import { problemOf } from "api-client/problem";

/** Speed multipliers the server accepts (plan Decision 11). */
export const SPEEDS = [0.5, 1, 2, 5, 10, 20] as const;

export const LIMITS = {
  autoStopMin: 1,
  autoStopMax: 240,
  rateMin: 1,
  rateMax: 100,
  failAttemptsMin: 1,
  failAttemptsMax: 3,
  latencyMin: 0,
  latencyMax: 10_000,
  exceptionMessageMax: 512,
  sessionPatternMax: 64,
  revertMin: 1,
  revertMax: 240,
} as const;

export const SESSION_PATTERN = /^[A-Za-z0-9\-_.:*?]*$/;

/**
 * Case-insensitive enum comparison. The contract enums are camelCase on the
 * wire, but other admin enums have shipped as CLR names; tolerate either.
 */
export const is = (value: string | undefined, expected: string): boolean =>
  (value ?? "").toLowerCase() === expected.toLowerCase();

export const clamp = (value: number, min: number, max: number): number =>
  Number.isFinite(value) ? Math.min(max, Math.max(min, Math.round(value))) : min;

/** Human text for a failed call: the server's violation list when there is one. */
export function problemMessage(error: unknown): string {
  if (error && typeof error === "object") {
    const errors = problemOf(error)?.errors;
    if (Array.isArray(errors) && errors.length > 0) return errors.join(" ");
    const status = (error as { status?: unknown }).status;
    if (status === 401 || status === 403)
      return "Only site Owners can use the simulator, and only in an allowed environment.";
    if (typeof status === "number") return `Request failed (HTTP ${status}).`;
  }
  if (error instanceof Error && error.message) return error.message;
  return "Request failed.";
}

export const blockedMessage = (status: api.SimulationStatus): string => {
  const env = status.environment?.trim();
  if (is(status.blockedReason, api.SimulationBlockReason.Production))
    return `"${env}" is a production environment. Simulation can never run in production.`;
  if (is(status.blockedReason, api.SimulationBlockReason.NotAllowed))
    return `Environment "${env}" is not in NimBus:Simulation:AllowedEnvironments, so simulation is blocked.`;
  return "The Environment setting is missing or blank, so simulation is blocked (the gate fails closed).";
};

export const ownedEndpointIds = (status: api.SimulationStatus): Set<string> =>
  new Set(
    (status.endpoints ?? [])
      .filter((e) => e.owned && (e.consumes?.length ?? 0) > 0)
      .map((e) => e.endpointId ?? ""),
  );

export interface PlainFailure {
  mode: api.SimulationFailureMode;
  rate: number;
  failAttempts: number;
  latencyMinMs: number;
  latencyMaxMs: number;
  exceptionMessage?: string;
  eventTypeIds: string[];
  sessionPattern?: string;
  revertAfterMinutes?: number;
}

export interface PlainSubscriber {
  endpointId: string;
  failure: PlainFailure;
}

export interface PlainPublisher {
  endpointId: string;
  eventTypes: { eventTypeId: string; enabled: boolean; ratePerMinute: number }[];
}

export const defaultFailure = (
  mode: api.SimulationFailureMode = api.SimulationFailureMode.Healthy,
): PlainFailure => ({
  mode,
  rate: 30,
  failAttempts: 2,
  latencyMinMs: 800,
  latencyMaxMs: 2500,
  eventTypeIds: [],
});

const toPlainFailure = (failure?: api.SimulationFailure): PlainFailure => ({
  ...defaultFailure(),
  mode: (failure?.mode ?? api.SimulationFailureMode.Healthy) as api.SimulationFailureMode,
  rate: failure?.rate ?? 30,
  failAttempts: failure?.failAttempts ?? 2,
  latencyMinMs: failure?.latencyMinMs ?? 800,
  latencyMaxMs: failure?.latencyMaxMs ?? 2500,
  exceptionMessage: failure?.exceptionMessage || undefined,
  eventTypeIds: [...(failure?.eventTypeIds ?? [])],
  sessionPattern: failure?.sessionPattern || undefined,
  revertAfterMinutes: failure?.revertAfterMinutes ?? undefined,
});

export const plainPublishers = (status: api.SimulationStatus): PlainPublisher[] =>
  (status.config?.publishers ?? []).map((p) => ({
    endpointId: p.endpointId ?? "",
    eventTypes: (p.eventTypes ?? []).map((e) => ({
      eventTypeId: e.eventTypeId ?? "",
      enabled: e.enabled ?? false,
      ratePerMinute: e.ratePerMinute ?? 1,
    })),
  }));

/** Configured subscribers, restricted to endpoints the simulator owns. */
export const plainSubscribers = (status: api.SimulationStatus): PlainSubscriber[] => {
  const owned = ownedEndpointIds(status);
  return (status.config?.subscribers ?? [])
    .filter((s) => owned.has(s.endpointId ?? ""))
    .map((s) => ({ endpointId: s.endpointId ?? "", failure: toPlainFailure(s.failure) }));
};

export const failureFor = (status: api.SimulationStatus, endpointId: string): PlainFailure =>
  plainSubscribers(status).find((s) => s.endpointId === endpointId)?.failure ?? defaultFailure();

/**
 * The whole config the PUT replaces, built from the current status with the
 * given parts swapped in. Subscribers are always limited to owned endpoints.
 */
export function buildConfig(
  status: api.SimulationStatus,
  patch: { speed?: number; publishers?: PlainPublisher[]; subscribers?: PlainSubscriber[] } = {},
): api.SimulationConfig {
  const owned = ownedEndpointIds(status);
  return api.SimulationConfig.fromJS({
    speed: patch.speed ?? status.config?.speed ?? 1,
    publishers: patch.publishers ?? plainPublishers(status),
    subscribers: (patch.subscribers ?? plainSubscribers(status)).filter((s) => owned.has(s.endpointId)),
  });
}

/** Replaces (or adds) one subscriber's failure, keeping every other entry. */
export const withSubscriber = (
  status: api.SimulationStatus,
  endpointId: string,
  failure: PlainFailure,
): PlainSubscriber[] => [
  ...plainSubscribers(status).filter((s) => s.endpointId !== endpointId),
  { endpointId, failure },
];

export interface Preset {
  id: string;
  label: string;
  description: string;
  failure: (consumes: string[]) => PlainFailure;
}

export const PRESETS: Preset[] = [
  {
    id: "healthy",
    label: "All healthy",
    description: "Every owned handler completes.",
    failure: () => defaultFailure(),
  },
  {
    id: "flaky",
    label: "Flaky (Random 30%)",
    description: "30 % of deliveries throw a transient error.",
    failure: () => ({ ...defaultFailure(api.SimulationFailureMode.Random), rate: 30 }),
  },
  {
    id: "transient",
    label: "Transient retries",
    description: "Two failed attempts, then success.",
    failure: () => ({ ...defaultFailure(api.SimulationFailureMode.Transient), failAttempts: 2 }),
  },
  {
    id: "slow",
    label: "Slow",
    description: "Handlers take 0.8–2.5 s.",
    failure: () => defaultFailure(api.SimulationFailureMode.Slow),
  },
  {
    id: "poison",
    label: "Poison one type",
    description: "The first consumed event type is dead-lettered.",
    failure: (consumes) => ({
      ...defaultFailure(api.SimulationFailureMode.Poison),
      eventTypeIds: consumes.slice(0, 1),
    }),
  },
  {
    id: "no-handler",
    label: "Missing handler",
    description: "Deliveries are answered Unsupported.",
    failure: () => defaultFailure(api.SimulationFailureMode.NoHandler),
  },
];

/** Subscribers after applying a preset to every owned endpoint (and only those). */
export const presetSubscribers = (status: api.SimulationStatus, preset: Preset): PlainSubscriber[] =>
  (status.endpoints ?? [])
    .filter((e) => e.owned && (e.consumes?.length ?? 0) > 0)
    .map((e) => ({ endpointId: e.endpointId ?? "", failure: preset.failure(e.consumes ?? []) }));

export const MODE_LABEL: Record<string, string> = {
  healthy: "Healthy",
  random: "Random failures",
  transient: "Transient",
  slow: "Slow",
  poison: "Poison",
  nohandler: "No handler",
};

export const modeLabel = (mode?: string): string => MODE_LABEL[(mode ?? "healthy").toLowerCase()] ?? mode ?? "Healthy";

/**
 * Loads GET /api/admin/simulation and, when `pollMs` is set, re-reads it on
 * that cadence while the tab is visible.
 */
export function useSimulationStatus(options: { pollMs?: number; enabled?: boolean } = {}) {
  const { pollMs, enabled = true } = options;
  const client = useMemo(() => new api.Client(api.CookieAuth()), []);
  const [status, setStatus] = useState<api.SimulationStatus>();
  const [error, setError] = useState<string>();
  const [loaded, setLoaded] = useState(false);
  const alive = useRef(true);

  const refresh = useCallback(async () => {
    try {
      const next = await client.getAdminSimulation();
      if (alive.current) {
        setStatus(next);
        setError(undefined);
      }
    } catch (cause) {
      if (alive.current) setError(problemMessage(cause));
    } finally {
      if (alive.current) setLoaded(true);
    }
  }, [client]);

  useEffect(() => {
    alive.current = true;
    if (!enabled) return () => {
      alive.current = false;
    };
    void refresh();
    const id = pollMs
      ? window.setInterval(() => {
          if (document.visibilityState === "visible") void refresh();
        }, pollMs)
      : undefined;
    return () => {
      alive.current = false;
      if (id !== undefined) window.clearInterval(id);
    };
  }, [refresh, pollMs, enabled]);

  const accept = useCallback((next: api.SimulationStatus) => {
    if (alive.current) setStatus(next);
  }, []);

  return { client, status, accept, error, setError, loaded, refresh };
}
