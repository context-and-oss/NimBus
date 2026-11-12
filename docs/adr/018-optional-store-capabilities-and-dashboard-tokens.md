# ADR-018: Optional Store Capabilities for the Monitor, and Read-Only Dashboard Tokens

## Status
Proposed (2026-11). Plan:
[docs/plan/2026-11-10-monitor-dashboard-and-tokens.md](../plan/2026-11-10-monitor-dashboard-and-tokens.md).

## Context

The rebuilt Monitor wall needs three things the storage contracts did not offer: queue and
processing time per time bucket, a count of blocked sessions per endpoint, and a place to keep the
tokens a wall display uses instead of signing in.

Adding members to `IMetricsStore`, `IMessageTrackingStore` or `IEndpointMetadataStore` would break
every storage provider outside this repository, which `docs/versioning.md` reserves for a major
release. Default interface implementations avoid the break but hide the problem: a provider that
forgets to override one silently returns an empty or wrong answer, and AGENTS.md already warns that
an unforwarded default bypasses the real store.

A wall display also needs to authenticate. Sharing a person's account with a screen gives the
screen that person's permissions everywhere.

## Decision

1. **New capabilities are separate, optional interfaces.** `IMonitorMetricsStore` (latency per
   bucket, blocked-session count) and `IDashboardTokenStore` (the token record) live in
   `NimBus.MessageStore.Abstractions`, beside the existing `ISubscriptionNotificationStore` and
   `IHeartbeatHistoryStore`. The Cosmos DB, SQL Server and in-memory stores implement them and
   register them by casting the aggregate store. The WebApp resolves them optionally: without one,
   the affected numbers are reported as unavailable and the token admin API answers 501. Each has
   its own conformance suite.
2. **Blocked sessions are estimated from stored status.** Distinct session ids among unresolved
   events that are Failed, DeadLettered, or Pending with the hand-off sub-status. Service Bus
   session state is authoritative but would need one session lock per session per refresh. The
   estimate counts a first-attempt dead-letter, which does not block its session, and is labelled
   as an estimate in the UI.
3. **Dashboard tokens are a separate credential from agent API keys**, sharing only the secret
   format and hashing. A token authenticates through its own scheme, which only the Monitor feed's
   policy names, so it reaches nothing else. Its principal carries no identity that the role model
   can match, and the feed accepts it by the scheme it came through.
4. **The token travels in the URL fragment** (`/Monitor#token=…`) and from there in a request
   header. A fragment is never sent to a server, so the token stays out of server logs and
   `Referer`. The SPA shell for exactly `/Monitor` is therefore served without a session; it holds
   no data.

## Consequences

- A minor release, not a major one. Third-party providers keep compiling and run the Monitor with
  the blocked-session count and the latency chart unavailable until they implement
  `IMonitorMetricsStore`, and without dashboard tokens until they implement `IDashboardTokenStore`.
- Optional interfaces mean a capability check at each use. Two interfaces is acceptable; if more
  accumulate, they should be folded into the main contracts in the next major release.
- The blocked-session tile can over-count. An exact count would need a projection of session block
  state written by the subscriber, which is a larger change to the message-state model.
- A token in a bookmark is a long-lived credential. Mitigations: one token per wall, expiry by
  default, revocation within 30 seconds (2 minutes during a store outage), audit of every change,
  and a read-only scope.
