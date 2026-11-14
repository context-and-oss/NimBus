import { useEffect, useMemo, useRef, useState } from "react";
// Type-only import: the api-client module (345KB, pulls in moment) must not
// land in the eager entry chunk — the runtime module is loaded on demand
// inside the effects below via dynamic import.
import type * as api from "api-client";
import { useStorageProvider } from "hooks/app-status";
import { manageResults } from "./manage-results";

// 8-4-4-4-12 hex layout OR 32 contiguous hex chars (Service Bus session keys).
const GUID_DASHED = /^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$/;
const HEX_32 = /^[0-9a-fA-F]{32}$/;
const REMOTE_DEBOUNCE_MS = 250;
const MAX_LOCAL_RESULTS = 8;
const MAX_REMOTE_RESULTS = 25;

export interface PaletteResult {
  /** Stable key for React reconciliation. */
  key: string;
  kind: "page" | "operation" | "setting" | "endpoint" | "eventType" | "event" | "session";
  title: string;
  /** Mono secondary line; usually a namespace or "Session …" descriptor. */
  subtitle?: string;
  /** Route to navigate to on selection. */
  route: string;
}

export interface PaletteSearchState {
  /** Whether catalog (endpoints + event types) is still loading. */
  catalogLoading: boolean;
  /** Whether a GUID lookup is in flight. */
  remoteSearching: boolean;
  /** Soft error from the GUID lookup; UI shows a hint, doesn't crash. */
  remoteError?: string;
  results: PaletteResult[];
}

function looksLikeId(input: string): boolean {
  return GUID_DASHED.test(input) || HEX_32.test(input);
}

/**
 * Aggregates command-palette results from two sources:
 *
 * 1. **Catalog (local)** — endpoint names + event types fetched once when the
 *    palette opens and substring-filtered against the input. For site Owners
 *    (`canManage`), the Manage pages, operations and settings come first.
 * 2. **GUID lookup (remote)** — when the input looks like an ID we hit the
 *    `/api/v1/messages/search` endpoint twice in parallel (by eventId, by sessionId)
 *    and surface the hits as `event` / `session` rows.
 *
 * Catalog requests run once per page load (the catalog rarely changes during
 * an operator's session). Remote lookups debounce 250 ms so paste-then-type
 * doesn't fire 5 round-trips.
 */
export function usePaletteSearch(
  query: string,
  enabled: boolean,
  canManage = false,
): PaletteSearchState {
  // Cached app status, already loaded by the footer; decides the Storage view.
  const storageProvider = useStorageProvider();
  const [endpoints, setEndpoints] = useState<string[] | undefined>(undefined);
  const [eventTypes, setEventTypes] = useState<api.EventType[] | undefined>(
    undefined,
  );
  const [catalogLoading, setCatalogLoading] = useState(false);

  const [remoteEvents, setRemoteEvents] = useState<api.Message[]>([]);
  const [remoteSessions, setRemoteSessions] = useState<api.Message[]>([]);
  const [remoteSearching, setRemoteSearching] = useState(false);
  const [remoteError, setRemoteError] = useState<string | undefined>(undefined);
  // Increments every time we kick off a new remote search; only the latest
  // ticket's response is applied so out-of-order returns can't clobber state.
  const remoteTicket = useRef(0);

  // 1. Catalog fetch — once, lazily when the palette first opens.
  useEffect(() => {
    if (!enabled) return;
    if (endpoints !== undefined && eventTypes !== undefined) return;
    let cancelled = false;
    setCatalogLoading(true);
    (async () => {
      const apiMod = await import("api-client");
      const client = new apiMod.Client(apiMod.CookieAuth());
      return Promise.all([
        client.getEndpointsAll().catch((): string[] => []),
        client.getEventTypes().catch((): api.EventType[] => []),
      ]);
    })()
      .then(([eps, types]) => {
        if (cancelled) return;
        setEndpoints(eps);
        setEventTypes(types);
      })
      .finally(() => {
        if (!cancelled) setCatalogLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [enabled, endpoints, eventTypes]);

  // 2. Remote GUID lookup — debounced.
  useEffect(() => {
    if (!enabled) {
      setRemoteEvents([]);
      setRemoteSessions([]);
      setRemoteError(undefined);
      return;
    }
    const trimmed = query.trim();
    if (!trimmed || !looksLikeId(trimmed)) {
      setRemoteEvents([]);
      setRemoteSessions([]);
      setRemoteError(undefined);
      return;
    }

    const ticket = ++remoteTicket.current;
    const timer = window.setTimeout(async () => {
      const apiMod = await import("api-client");
      const client = new apiMod.Client(apiMod.CookieAuth());
      setRemoteSearching(true);
      setRemoteError(undefined);
      try {
        const byEvent = new apiMod.MessageSearchRequest();
        byEvent.filter = apiMod.MessageSearchFilter.fromJS({ eventId: trimmed });
        byEvent.maxItemCount = MAX_REMOTE_RESULTS;

        const bySession = new apiMod.MessageSearchRequest();
        bySession.filter = apiMod.MessageSearchFilter.fromJS({
          sessionId: trimmed,
        });
        bySession.maxItemCount = MAX_REMOTE_RESULTS;

        const [evRes, seRes] = await Promise.all([
          client.postMessagesSearch(byEvent).catch(() => undefined),
          client.postMessagesSearch(bySession).catch(() => undefined),
        ]);

        if (ticket !== remoteTicket.current) return;
        setRemoteEvents(evRes?.messages ?? []);
        setRemoteSessions(seRes?.messages ?? []);
      } catch (err) {
        if (ticket !== remoteTicket.current) return;
        setRemoteError(
          err instanceof Error ? err.message : "Lookup failed",
        );
        setRemoteEvents([]);
        setRemoteSessions([]);
      } finally {
        if (ticket === remoteTicket.current) {
          setRemoteSearching(false);
        }
      }
    }, REMOTE_DEBOUNCE_MS);

    return () => window.clearTimeout(timer);
  }, [enabled, query]);

  // 3. Compose results
  return useMemo<PaletteSearchState>(() => {
    const trimmed = query.trim();
    const lower = trimmed.toLowerCase();
    // Results are pushed in section order: the palette highlights by index into
    // this array but draws it grouped, so the two orders must agree.
    const results: PaletteResult[] = canManage ? manageResults(trimmed, storageProvider) : [];

    // Local matches — only when there's actual input (empty input shows hint).
    if (trimmed) {
      const epMatches = (endpoints ?? [])
        .filter((name) => name.toLowerCase().includes(lower))
        .slice(0, MAX_LOCAL_RESULTS);
      for (const name of epMatches) {
        results.push({
          key: `endpoint::${name}`,
          kind: "endpoint",
          title: name,
          subtitle: "Endpoint",
          route: `/Endpoints/Details/${encodeURIComponent(name)}`,
        });
      }

      const typeMatches = (eventTypes ?? [])
        .filter((et) => {
          const name = (et.name || et.id || "").toLowerCase();
          const ns = (et.namespace || "").toLowerCase();
          return name.includes(lower) || ns.includes(lower);
        })
        .slice(0, MAX_LOCAL_RESULTS);
      for (const et of typeMatches) {
        results.push({
          key: `eventType::${et.id}`,
          kind: "eventType",
          title: et.name || et.id,
          subtitle: et.namespace || "Event type",
          route: `/EventTypes/Details/${encodeURIComponent(et.id)}`,
        });
      }
    }

    // Remote — only meaningful when query is GUID-shaped.
    if (trimmed && looksLikeId(trimmed)) {
      for (const m of remoteEvents) {
        if (!m.eventId || !m.endpointId) continue;
        const ep = m.endpointId;
        const evId = m.eventId;
        const short = evId.length > 12 ? `${evId.slice(0, 8)}…` : evId;
        results.push({
          key: `event::${ep}::${evId}`,
          kind: "event",
          title: `Event ${short}`,
          subtitle: `${m.eventTypeId ?? "(unknown type)"} · ${ep}`,
          route: `/Message/Index/${encodeURIComponent(ep)}/${encodeURIComponent(evId)}/0`,
        });
      }

      // Sessions can match many messages — collapse to one row per unique session.
      const sessionSeen = new Set<string>();
      for (const m of remoteSessions) {
        if (!m.sessionId) continue;
        if (sessionSeen.has(m.sessionId)) continue;
        sessionSeen.add(m.sessionId);
        const short =
          m.sessionId.length > 12
            ? `${m.sessionId.slice(0, 8)}…`
            : m.sessionId;
        const ep = m.endpointId ?? m.to ?? "—";
        results.push({
          key: `session::${m.sessionId}`,
          kind: "session",
          title: `Session ${short}`,
          subtitle: `Endpoint ${ep}`,
          route: `/Messages?sessionId=${encodeURIComponent(m.sessionId)}`,
        });
      }
    }

    return {
      catalogLoading,
      remoteSearching,
      remoteError,
      results,
    };
  }, [
    query,
    canManage,
    storageProvider,
    endpoints,
    eventTypes,
    remoteEvents,
    remoteSessions,
    remoteSearching,
    remoteError,
    catalogLoading,
  ]);
}
