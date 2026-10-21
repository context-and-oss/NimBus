# Session order after unblock (Spec 041 §2.7, bug 1)

## Problem

`StrictMessageHandler.HandleEventRequest` defers an event only while `BlockedByEventId` is set.
When the block clears (resubmit, skip, retry, handoff completion), the drain republishes the parked
messages to the back of the topic. A new event that reaches the endpoint before those replays sees
an open session and runs ahead of older parked events, which breaks session order.
`DeferredCount` can't detect this: production code increments it when parking and never decrements
or resets it.

## Design

**Replay marker.** `DeferredMessageProcessor` keeps `DeferralSequence` on the replay instead of
stripping it. A delivery from the main subscription that carries `DeferralSequence` is a replay.
Nothing else reads that property on a main-subscription delivery: the Resolver doesn't map it,
and no subscription filter matches on it. `OriginalSessionId`, which the `Deferred` filter
matches on, is still stripped.

A drain from the previous version strips `DeferralSequence`, and during a rolling deploy its
replays reach new handlers. Since v4.2.0 every parked copy carries `InboxMessageId`, and that drain
copies it onto the replay; no new `EventRequest` carries it. A delivery with `InboxMessageId` but no
`DeferralSequence` is therefore a legacy replay. It goes through the guard, so it isn't parked again
in a loop, but it can't advance the replay position. The drain still replays in order.

**Replay position, not a count.** The session state gains `ReplayedThroughSequence`, the highest
deferral sequence whose replay has reached the endpoint. Replays arrive in deferral order, so a
parked message still waits exactly when `ReplayedThroughSequence < NextDeferralSequence - 1`.
Recording it is `max()`, so a redelivered replay changes nothing. A counter would drift on every
redelivery between its decrement and the settlement. A sequence the session never assigned (a
replay of a message parked before the WebApp cleared the state) is ignored.

- State written before this change has no position. The first message parked afterwards starts it
  just below its own sequence, so that message is covered; replays of older messages then advance
  it. Sessions that never park again pay nothing.
- A gap (a sequence taken whose park send failed) looks like a waiting message. The next new event
  parks and requests a drain, its replay moves the position past the gap, and the guard opens.

**Guard.** After the block check, a non-replay event parks when `HasUnreplayedDeferredMessages`
is true. It sends a `DeferralResponse`, parks in the `Deferred` subscription and completes. It is
not a block: no handler runs, `HandlerOutcome` is `NotDispatched`, and no session-blocked
lifecycle signal is raised. Replays go through the guard in order.

**Drain ownership.** The drain that the unblock requested can finish receiving before a new event
parks behind its replays. Requesting a drain for every such event would queue one empty drain
(a 5-second receive) per event on the serial trigger subscription. So:

1. The first event that parks behind replays sends a `ProcessDeferredRequest` and records the
   request: its own sequence (`DrainRequestedForSequence`) and the time (`DrainRequestedAtUtc`).
   Later events see the pending request and don't send another, unless it is older than two
   minutes (see "Requests whose replay never arrives").
2. When the replay of that event arrives, the request is cleared. If messages parked after it are
   still waiting and the session isn't blocked, another drain is requested first. A blocked
   session leaves the drain to its unblock, as before.

Each trigger is sent before the state that suppresses the next one is written. A crash therefore
repeats a trigger and never loses one, and an extra drain with nothing to replay does nothing.

**Requests whose replay never arrives.** The request is cleared only when its event's replay
reaches the endpoint. Several failures prevent that:

- the replay is handled by an instance still running the previous version during a rolling deploy;
- the trigger is dead-lettered after repeated drain failures;
- a concurrent drain can't lock the `Deferred` session and does nothing (`SessionCannotBeLocked`);
- the parked copy is purged or removed by hand;
- the replay itself is dead-lettered.

Without a fallback, the guard would then stay on with a request that never clears: every new
event would park and none would request a drain, so the session would stop silently. An expiry
prevents that. Once a request is older than `DrainRequestTimeout` (two minutes), the next event
parked behind replays requests a drain again and records a new request. Two minutes is well beyond
a normal drain and replay. A request that expires early, during a long drain, costs one more drain
that finds the session locked or empty. A stored request without a timestamp reads as expired.

**Operator actions.** The WebApp's "reprocess deferred" and session purge used to overwrite the
session state with `null`. That dropped the replay position, so after a reprocess a new event could
overtake the parked messages again. It also reset `NextDeferralSequence`, so a replay already in
flight could be mistaken for a message parked afterwards. Both actions now rewrite the state
through `AdminService.ClearSessionState`:

- `ClearForReprocess` clears the block, the legacy deferred sequence numbers and the drain
  request. It keeps the sequence and the replay position, so new events park until the reprocess
  replays arrive.
- `ClearAfterPurge` does the same, zeroes `DeferredCount`, and counts every assigned sequence as
  replayed, because the parked copies are gone. Replays still in flight carry sequences below the
  kept `NextDeferralSequence`, so they change nothing.

State that can't be read is cleared outright, as before. `SessionState.IsEmpty` now also requires
no sequence, position or request, so a state that only keeps the sequence is still stored.

## Interface

`IMessageContext` gains four members with default implementations that turn the guard off, so
existing third-party contexts still compile and behave as before:
`MarkDeferredMessageReplayed`, `HasUnreplayedDeferredMessages`, `GetDeferredDrainRequest` and
`SetDeferredDrainRequest`. The last two use the new `DeferredDrainRequest(DeferralSequence,
RequestedAtUtc)` record. The Service Bus context and `InMemoryMessageContext` implement them over
the session state.

## Costs and limits

- One extra session-state read per `EventRequest` (the guard), and up to three state operations
  per replay.
- **Pre-existing race, slightly wider (not fixed here).** When two instances run the drain trigger
  at once, the second can't lock the `Deferred` session and treats that as "nothing to drain"
  (`SessionCannotBeLocked`). A message parked between the first drain's final empty receive and its
  lock release then waits for the next drain. If that message requested the drain, the expiry above
  recovers it only when another event arrives. Unblock triggers already have this race. The fix is
  for the drain to retry on lock contention instead of doing nothing, but it depends on what Service
  Bus returns when accepting a session that has no messages. If that also raises
  `SessionCannotBeLocked`, a retry would loop on every empty drain, so it needs checking against a
  real namespace first. It is left as a separate change.
- After a lost request, recovery needs a new event: the next one parked after the expiry requests
  the drain. If no event arrives, the messages already parked stay parked, just as messages
  stranded by a lost unblock trigger do today. "Reprocess deferred" in the WebApp clears the
  session state and requests a drain.

## Tests

- `SessionOrderAfterUnblockTests` (end to end, real `StrictMessageHandler` and the Service Bus
  `MessageContext` over a fake session):
  - a new event arriving between a skip and the replay parks and then runs after the parked
    event;
  - an event parked after the requested drain is drained when the requesting replay arrives;
  - a request whose replay never arrives is requested again by the first event parked after it
    expires;
  - a replay from a previous-version drain (no `DeferralSequence`) goes through instead of parking
    again;
  - a new event arriving after a reprocess, before its replays, parks behind them; after a purge it
    runs straight away;
  - a replay that blocks the session again keeps the later parked messages and a new event in
    order.
  - The first two fail on `master`. The expiry and legacy-replay tests fail when their check is
    removed.
- `MessageContextTests`: legacy-state start, guard opening, no movement backwards, cleared-state
  sequences ignored, the drain request round trip, a request stored without a timestamp reading
  as expired, and the Newtonsoft-write/System.Text.Json-read round trip of the new fields.
- `MessageContextTests` also cover `ClearForReprocess`, `ClearAfterPurge` and the `SessionState`
  serialization helpers.
- `AdminSessionStateClearTests` (WebApp): reprocess keeps the replay position, and unreadable state
  is cleared outright.
- `DeferredMessageProcessorTests`: the replay keeps `DeferralSequence`.
- The three end-to-end drain simulations now keep `DeferralSequence`, as the real drain does.
