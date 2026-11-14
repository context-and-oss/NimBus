# Session state on the Operations page

Date: 2026-11-03. Branch `feat/session-state-operations`.

## Problem

An endpoint keeps per-session bookkeeping in Service Bus session state (`SessionState`: the
blocking event, the deferred count, the deferral sequence, the replay position and a pending
drain request). Operators can't see it. The only WebApp actions that touch it are Session purge
and Reprocess deferred, and both do much more than rewrite the state. To answer "why is this
session still blocked?" or "is this session's state stale?", operators need the `nb` CLI or a
Service Bus explorer.

Session state also outlives a session's messages. Once a session has parked a message, its
deferral sequence and replay position stay in the state forever, so the namespace collects state
for sessions nothing uses any more.

## Decisions

1. **Two new operations.** *Session state* (Recovery group) looks up one session's state and
   changes it. *Clean up session state* (Cleanup group) finds idle sessions on an endpoint and
   resets their state. Session purge is unchanged.
2. **Lookup reads under a session lock and never writes.** The WebApp accepts the session on
   `<endpoint>/<endpoint>`, reads the state, peeks the session's active messages, then releases
   the lock. It also peeks the session on the `Deferred` subscription and reads the store's
   pending and deferred counts and the blocking event's status. A session the endpoint holds
   right now reports as *locked by a receiver*: nothing else can read its state, and that's
   expected.
3. **Writes are optimistic.** A lookup returns a `stateVersion`, the SHA-256 of the stored bytes.
   Each write sends it back. Under the lock, the server re-reads the state and refuses with
   409 if the version changed, returning the current state.
4. **Guided actions first, JSON second.**
   - *Clear block*: `ClearBlock()`.
   - *Mark parked messages replayed*: the new `SessionState.MarkAllReplayed()` (also used by
     `ClearAfterPurge()`). It keeps the block, and the server refuses it while the Deferred
     subscription or the store still holds parked messages for the session.
   - *Reset to empty*: writes empty state.
   - *Edit JSON* (Advanced): the body must deserialize into `SessionState` with unknown members
     rejected. It must also satisfy these invariants: counts and sequences ≥ 0; the replay position
     is below `NextDeferralSequence`; `DrainRequestedAtUtc` appears only with
     `DrainRequestedForSequence`; `BlockedByMessageId` appears only with `BlockedByEventId`.
   Every write requires an audit row (`LogRequiredAuditAsync`, new `ChangeSessionState` type,
   appended last) recording the action and the before and after states. If the audit can't be
   written, nothing is changed.
5. **Cleanup draws its candidates from the store.** `Azure.Messaging.ServiceBus` (and the
   emulator) can't list sessions. Candidates are the distinct session ids of the endpoint's
   stored events updated since a chosen date, capped per run (default 30 days and 200 sessions).
   Sessions the store never saw are out of scope.
6. **What counts as idle.** A session is idle when all of these hold:
   - its state is non-empty and readable;
   - it isn't blocked, has a zero `DeferredCount`, no unreplayed parked messages and no pending
     drain request. State written before the replay position existed (`ReplayedThroughSequence`
     null) counts every assigned sequence as unreplayed; with `DeferredCount` at zero and nothing
     parked anywhere, that is old bookkeeping, so it doesn't make the session in use;
   - it has no active messages on the endpoint subscription and none on the `Deferred`
     subscription;
   - the store has no pending or deferred events for it.
   *Stale block* is an opt-in second category: the blocking event completed, was skipped or
   is gone from the store (it is no longer Pending, Deferred, Failed, TooManyRequests,
   DeadLettered or Unsupported), and the other conditions hold apart from the block. Every
   other session is reported *in use*, with a reason. Locked sessions count as in use.
   Resetting idle state is safe: `MarkReplayed` ignores a sequence at or above
   `NextDeferralSequence`, so a stray replay after the reset can't move the position.
7. **Preview, then apply exactly what was previewed.** The preview returns every candidate with
   its classification and `stateVersion`. Apply takes the selected `(sessionId, stateVersion)`
   pairs. It re-checks each one under its lock and skips any that changed or are no longer idle.
   Before resetting anything it writes one required audit row listing the selected sessions; if
   that row can't be written, nothing is reset.

## API (api-spec.yaml, Admin tag, Site Owner only)

| Method | Route | Body → Result |
|---|---|---|
| GET | `/api/admin/endpoint/{endpointId}/session/{sessionId}/state` | → `SessionStateView` |
| POST | `/api/admin/endpoint/{endpointId}/session/{sessionId}/state` | `SessionStateChangeRequest` (`action`: clearBlock, markReplayed, reset, replace; `expectedVersion`; `stateJson` for replace) → `SessionStateView`; 400 invalid, 409 changed or locked (body: current view), 503 audit unavailable |
| POST | `/api/admin/endpoint/{endpointId}/session-cleanup/preview` | `SessionCleanupPreviewRequest` (`updatedSince`, `maxSessions`, `includeStaleBlocks`) → `SessionCleanupPreview` |
| POST | `/api/admin/endpoint/{endpointId}/session-cleanup` | `SessionCleanupRequest` (`sessions[]` of id + version, `includeStaleBlocks`) → `SessionCleanupResult` |

`SessionStateView` contains the session id, `locked`, `isEmpty` and `readable`, the state JSON
and `stateVersion`, and the parsed fields. It adds derived flags (`blocked`,
`hasUnreplayedMessages`) and the counts: active messages, Deferred subscription messages, store
pending and store deferred. It also carries the blocking event's status.

## Implementation

- `NimBus.Core` `SessionState`: `Validate()` for the invariants and a strict `Parse(string)`.
- `NimBus.WebApp/Services/SessionStateOperations.cs` holds a new service behind
  `ISessionStateOperations`, registered in DI. It covers lookup, change and cleanup preview and
  apply, over `ServiceBusClient` and `IMessageTrackingStore`. It uses the same session-receiver
  seam that `AdminSessionStateClearTests` fakes.
- `AdminImplementation`: four actions that check Site Owner and endpoint existence and write
  the audit. The ids are validated: non-empty, at most 128 characters (the Service Bus session
  id limit).
- `MessageAuditType.ChangeSessionState`, appended last and added to both api-spec audit enums
  (`AuditTypeContractTests`).
- ClientApp: `components/admin/session-state.tsx` (lookup, summary, guided actions, and a JSON
  editor behind a disclosure, with typed confirmation for reset and replace) and
  `components/admin/session-cleanup.tsx` (preview table with selection, then apply). Add both to
  `OPERATIONS` and `CARDS`.

## Tests (RED first)

- Core: `SessionState.Validate`/`Parse`, with unknown members, negative counts, replay position
  past the sequence, and a drain timestamp without a sequence.
- WebApp service, over a fake bus and the in-memory store:
  - lookup of empty, blocked and locked sessions;
  - a version mismatch returns a conflict;
  - each guided action writes the expected state;
  - replace rejects invalid JSON;
  - cleanup classifies idle, stale-block and in-use sessions (active messages, Deferred
    subscription messages, store pending, locked);
  - apply skips a session whose version changed.
- WebApp API: non-owners are forbidden and audited as denied, an unknown endpoint returns 404,
  and an audit failure blocks the write.
- ClientApp (Vitest): lookup renders the state, actions call the API with the version, a 409
  shows the refresh message, and cleanup previews and then applies only the selected sessions.

## Verification

`dotnet build src/NimBus.slnx -c Release`, `dotnet test src/NimBus.slnx -c Release --no-build`,
`npm --prefix src/NimBus.WebApp/ClientApp run test:ci` and `run build`. Capture a screenshot of
both cards for the PR (mock API preview).

## Out of scope

- Listing sessions through the AMQP `get-message-sessions` management call.
- A CLI counterpart.
- Session state on endpoints in other namespaces.
