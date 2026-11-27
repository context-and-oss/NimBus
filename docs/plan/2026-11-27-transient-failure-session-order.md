# Transient failure keeps session order

## Problem

`MessageHandler` calls `IMessageContext.Abandon` when a delivery fails with
`TransientException`: handler code, a lost lock, a transient `Complete` or session-state
call, or an `InboxStoreException` after the handler succeeded. `MessageContext.Abandon` was
a deliberate no-op. The idea was that the message stays locked and Service Bus redelivers it
when the lock expires, after about 30 seconds.

Session messages have no per-message lock. The session processor keeps the session lock and
renews it. With `MaxConcurrentCallsPerSession = 1` it receives again as soon as the callback
returns, and the unsettled message stays locked to that receiver. The next receive therefore
returns N+1, then N+2. N comes back only after the processor releases the session, which
happens when the session goes idle or the renewal cap is reached. A prefetched N+1 overtakes
N even after an explicit abandon. A new emulator test drives `NimBusReceiverHostedService`
with E1 and E2 in one session and fails E1 once with `TransientException`. It observes
E1, E2, E1. With an inbox the handler runs out of order, and `StrictMessageHandler` has no
stale-write guard like the Resolver's (Spec 030) to make up for it.

## Fix

1. `IServiceBusSession` gains `AbandonAsync(IServiceBusMessage, CancellationToken)`. It is a
   default interface method that does nothing, so existing implementations still compile and
   keep the old behaviour.
2. On a transient failure, `ServiceBusSession.AbandonAsync` returns the message to the head of
   its session. On the session processor (`ProcessSessionMessageEventArgs`) and the Functions
   isolated worker (`ServiceBusMessageActions` plus `ServiceBusSessionMessageActions`) it:
   1. waits a short backoff while it still holds the session (see below);
   2. abandons the message, which spends one delivery of the `MaxDeliveryCount` budget;
   3. releases the session (`ReleaseSession`) even if the abandon failed, so the receiver
      closes after the callback and discards anything it prefetched.
   When the session is accepted again, the failed message is at its head. On a caller-owned
   `ServiceBusSessionReceiver` (`SubscriberClient`), NimBus does not own the session and
   cannot release it. There the message is abandoned, so the caller's next receive returns
   it unless something was prefetched. The obsolete session-actions-only overload cannot
   settle, so it keeps the no-op.
3. `MessageContext.Abandon` delegates to the session. A `SessionLockLost` or
   `MessageLockLost` there is swallowed: the broker already returns the message to the
   session, so there is nothing left to retry.

## Retry pause

Under the old model, the pause before a retry was however long the session stayed held. That
was about 30 s after a quiet session drained, and it was paid for by reordering the session.
The new pause is explicit. NimBus waits `min(2^(DeliveryCount-1) s, 30 s)` inside the
callback before it abandons: 1 s, 2 s, 4 s, 8 s, 16 s, then 30 s. Across the default
ten-delivery budget that is about 2.5 minutes before the broker dead-letters the message.
The wait observes the processor's cancellation token, so shutdown and an opening circuit cut
it short and the message is abandoned straight away.

Tradeoff: during the wait the failing session holds one of the receiver's
`MaxConcurrentSessions` slots, for at most 30 s per attempt. That is the same session that
could not make progress anyway. Releasing straight away without a pause would use up the
whole delivery budget in seconds. Downstream outages still belong on the scheduled-retry
path (typed exceptions and retry rules), as the error-handling guide already says.

## Tests

- Emulator (`NimBus.ServiceBusEmulator.Tests`): E1 and E2 in one session through
  `NimBusReceiverHostedService`, E1 throws `TransientException` once. Expect E1, E1, E2.
  This fails on master with E1, E2, E1.
- Unit (`NimBus.ServiceBus.Tests`): on the processor path, a transient abandon waits,
  abandons and then releases the session. The release still happens when the abandon reports
  a lost lock. A cancelled token skips the wait. On the Functions path, the message is
  abandoned and the session released. On a caller-owned receiver, the message is abandoned
  without a release.

## Docs

Update `docs/message-flows.md` §11 and `docs/error-handling.md` to describe abandon, release
and backoff in place of the lock-expiry model.
