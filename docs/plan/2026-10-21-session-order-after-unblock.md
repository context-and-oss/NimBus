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

1. The first event that parks behind replays sends a `ProcessDeferredRequest` and records its own
   sequence in `DrainRequestedForSequence`. Later events see the request and don't send another.
2. When the replay of that event arrives, the request is cleared. If messages parked after it are
   still waiting and the session isn't blocked, another drain is requested first. A blocked
   session leaves the drain to its unblock, as before.

Each trigger is sent before the state that suppresses the next one is written. A crash therefore
repeats a trigger and never loses one, and an extra drain with nothing to replay does nothing.

## Interface

`IMessageContext` gains four members with default implementations that turn the guard off, so
existing third-party contexts still compile and behave as before:
`MarkDeferredMessageReplayed`, `HasUnreplayedDeferredMessages`, `GetDrainRequestedForSequence` and
`SetDrainRequestedForSequence`. The Service Bus context and `InMemoryMessageContext` implement
them over the session state.

## Costs and limits

- One extra session-state read per `EventRequest` (the guard), and up to three state operations
  per replay.
- **Pre-existing race, slightly wider.** When two instances run the drain trigger at once, the
  second can't lock the `Deferred` session and treats that as "nothing to drain"
  (`SessionCannotBeLocked`). A message parked between the first drain's final empty receive and
  its lock release then waits for the next drain. This already applies to unblock triggers; the
  new trigger is exposed to it the same way.
- If the message a drain was requested for disappears without being replayed (purged, or its
  trigger dead-lettered), new events keep parking behind it. Operators recover the same way as
  from any stranded parked message: "reprocess deferred" in the WebApp clears the session state
  and requests a drain.

## Tests

- `SessionOrderAfterUnblockTests` (end to end, real `StrictMessageHandler` and the Service Bus
  `MessageContext` over a fake session):
  - a new event arriving between a skip and the replay parks and then runs after the parked
    event;
  - an event parked after the requested drain is drained when the requesting replay arrives.
  - Both fail on `master`.
- `MessageContextTests`: legacy-state start, guard opening, no movement backwards, cleared-state
  sequences ignored, and the Newtonsoft-write/System.Text.Json-read round trip of the new fields.
- `DeferredMessageProcessorTests`: the replay keeps `DeferralSequence`.
- The three end-to-end drain simulations now keep `DeferralSequence`, as the real drain does.
