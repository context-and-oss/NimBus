# Spec 041 — On-prem NimBus: RabbitMQ transport with SQL Server storage

Status: investigation, revision 4. Not implemented; no code, broker or benchmark exists yet.
Revision 3 is parity-first. Revision 2 tried to improve on Service Bus inside the transport (a
synthetic blocker and in-partition replay) and to fan out in the sender. Codex's second review
showed both break Core paths or bypassed publishers. Revision 3 ports the Service Bus design as it
is instead. Revision 4 answers the third review (sound after fixes). Reviews: [`docs/plan/2026-10-21-spec-041-review.md`](../../plan/2026-10-21-spec-041-review.md);
§11 maps every finding.
Baseline: master `3f6e9cff`. Supersedes the transport design in the September study
(`docs/spec/on-prem-nimbus-feasibility.html`, added in `b29ff5bb`, removed in `71f2220a`; plan at
[`docs/plan/2026-09-14-on-prem-feasibility.md`](../../plan/2026-09-14-on-prem-feasibility.md)).
Scope: a second transport for customers who can't or won't use Azure, with SQL Server as the only
store. Azure Service Bus stays the default and keeps every feature it has today.

## 1. Why

Some customers want NimBus without Azure: on their own servers, with no cloud connectivity. The
SQL Server message store, inbox and outbox exist, and the WebApp selects the store from
configuration (`WebApp/Startup.Platform.cs:148-173`). The transport is the gap: Azure Service Bus
is the only one.

[ADR-001](../../adr/001-session-based-ordering.md), `CONTRIBUTING.md` ("Don't abstract the
transport") and `AGENTS.md` rule out a transport abstraction. Those rules were written without a
concrete use case. This one is concrete, so a new ADR (§10, phase 0) should allow exactly one
second transport for on-prem installations. It does not open the door to a general transport
framework.

## 2. What the code shows

### 2.1 Core has most of the seam

`NimBus.Core` does not reference `Azure.Messaging.ServiceBus`. A transport implements four Core
contracts:

| Contract | Service Bus implementation | What it covers |
|---|---|---|
| `IMessageContext` (`Core/Messages/IMessageContext.cs`) | `ServiceBus/MessageContext.cs` | settlement, session block/unblock, deferral counters, scheduled redelivery |
| `ISender` | `ServiceBus/Sender.cs` | send, schedule, cancel a scheduled send (`long` token) |
| `IDeferredMessageProcessor` | `ServiceBus/DeferredMessageProcessor.cs` | replay a session's parked messages |
| `IReplyDispatcher` | `ServiceBus/ReplyDispatcher.cs` | request/reply |

`NimBus.Testing` implements the same contracts in memory (`InMemoryMessageBus`,
`InMemoryMessageContext`). The Core outbox dispatcher publishes through `ISender`
(`Core/Outbox/OutboxDispatcher.cs`), so it follows whichever transport is registered.

The **public SDK surface is not transport-neutral**:

- `ISubscriberClient` inherits the Service Bus–specific `IServiceBusAdapter`
  (`SDK/ISubscriberClient.cs:8`).
- `PublisherClient.Request` sends, then receives the reply with a `ServiceBusSessionReceiver`
  (`SDK/PublisherClient.cs:265-279`).
- `OutboxDispatcherSender` inherits the Service Bus `Sender` (`SDK/Hosting/OutboxDispatcherSender.cs:10`).
- `NimBusReceiverHostedService` is built on `ServiceBusSessionProcessor`
  (`SDK/Hosting/NimBusReceiverHostedService.cs:198-215`).

### 2.2 Failure isolation is NimBus's, not the broker's

`StrictMessageHandler.HandleEventRequest` (`Core/Messages/StrictMessageHandler.cs:71-162`), on
its normal path:

- **Handler fails** (`catch (EventContextHandlerException)`, line 153): send an error response to
  the Resolver, mark the session blocked, **complete the message**, then `CheckForRetry`
  (line 647). That schedules a retry only when a retry policy applies; otherwise the failure waits
  for an operator.
- **Session already blocked** (line 108 → `catch (SessionBlockedException)`, line 146): send a
  deferral response, then park (`DeferMessageToSubscription`, line 506): take the next deferral
  number, send a copy addressed `To = "Deferred"` through `ISender`, increment the deferred count,
  and **complete the original**. The parked copy (`ResponseService.SendToDeferredSubscription`,
  line 313) is a new `Message` with no `MessageId`; it carries the source id as `ParentMessageId`.
- **Retry or resubmission succeeds** (lines 184-207, 258-293): unblock the session, then send a
  `ProcessDeferredRequest` if the deferred count is above zero (`ContinueWithAnyDeferredMessages`,
  line 637).

So on the normal path the queue never holds a failed message. If sending a response, writing
session state or completing fails, the delivery stays unsettled and is redelivered. The central
finding: **the broker only has to deliver each session in order to one consumer at a time.**
Isolating a failed session from the others already happens above the broker.

### 2.3 Transient failures

`MessageHandler` catches `TransientException` and calls `Abandon`
(`Core/Messages/MessageHandler.cs:70-82`). On Service Bus `Abandon` **does nothing**
(`ServiceBus/MessageContext.cs:250-257`). The message stays unsettled and returns when the session
lock is released or expires. Other sessions are unaffected.

### 2.4 Service Bus features the transport relies on

| Feature | Where | Notes |
|---|---|---|
| Session receivers: ordered, exclusive per session, `MaxConcurrentSessions` in parallel | SDK receiver host, Resolver host | The ordering primitive |
| Session lock: expiry, renewal, `SessionLockLost` on settlement and state writes | `MessageContext.cs:281` | What fences a consumer that lost the session |
| Session state | `ServiceBus/SessionState.cs`, `ServiceBusSession.cs:95-145` | 4 live fields: `BlockedByEventId`, `BlockedByMessageId`, `DeferredCount`, `NextDeferralSequence` |
| `Deferred` subscription (sessions on) | `TopologyDescriptor.cs:177-192` | Parked copies, filtered `To = 'Deferred' AND OriginalSessionId IS NOT NULL` |
| `DeferredProcessor` subscription (sessions off) | `TopologyDescriptor.cs:195-206`, `SDK/Hosting/DeferredMessageProcessorHostedService.cs:80` | Receives `ProcessDeferredRequest` (`ResponseService.cs:377`), consumed by its own hosted service, not by `StrictMessageHandler` (line 431) |
| Replay | `ServiceBus/DeferredMessageProcessor.cs:63-130, 190` | Lock the session on `Deferred` (`AcceptSessionAsync`), sort by deferral number, republish to the main topic with a new `MessageId`, complete; stop when a replay is parked again |
| Scheduled send and cancel | `ServiceBus/Sender.cs:30-33` | Token is the Service Bus sequence number |
| Dead-letter queue | `ServiceBusSession.cs:80-88`, WebApp `ResolverDeadLetterClient` | |
| Rule actions and auto-forward | `TopologyDescriptor.cs:94-115` | Forwarders: filter `EventTypeId = X AND From IS NULL`, action `SET From = <producer>; SET EventId = newid(); SET To = <consumer>`, forward to the consumer's topic. Retry and continuation rules re-stamp `From` and `To` |

### 2.5 Who publishes

Fan-out happens in the broker today, so anything that writes to a producer's topic is fanned out:

- NimBus publishers (`PublisherClient`, the outbox dispatcher, the Resolver, ManagerClient, WebApp
  simulation and heartbeat). `PublisherClient` sets `MessageId` to a content hash
  (`<eventType>-<hash>`, `PublisherClient.cs:431-436`), so two identical publishes share it, and
  leaves `EventId` for the forwarder.
- **Raw publishers** with no NimBus code, such as PartnerPortal, which sends a CloudEvent with a
  random `MessageId` and a session id using only the Service Bus SDK
  (`samples/CrmErpDemo/PartnerPortal/Program.cs:115`).
- **Raw consumers** read the original publication before any rule runs, such as
  `CloudEventsInterop.NonNimBusConsumer` on a plain subscription of the producer topic.

### 2.6 Coupling outside the transport project

| Area | Coupling |
|---|---|
| `NimBus.SDK` | References `NimBus.ServiceBus`; public types in §2.1; handoff client |
| `NimBus.Manager` | `ManagerClient` takes a `ServiceBusClient` and publishes directly (`ManagerClient.cs:66`), although resubmit and skip are ordinary messages |
| `NimBus.Resolver` / `.Host` | Functions entry point uses a Service Bus session trigger; the worker host constructs `NimBusReceiverHostedService` from `ServiceBusClient` (`Resolver.Host/Program.cs:44`) |
| `NimBus.WebApp` startup | `AddServiceBusClients` runs unconditionally (`Startup.cs:31`); the heartbeat sender is `ServiceBusHeartbeatMessageSender` (`Startup.Web.cs:139`); `EndpointImplementation` requires `IServiceBusManagement` (line 38); simulation hosts use the Service Bus SDK |
| `NimBus.WebApp` admin | Purge, subscription admin, topology, dead-letter client, deferred-message inspector: about 2,800 lines in `Services/AdminService*.cs`, `SubscriptionAdminService.cs`, `ResolverDeadLetterClient.cs`, `DeferredMessageInspector.cs` |
| `NimBus.WebApp` storage | Ten files reference `Microsoft.Azure.Cosmos`, mostly `catch (CosmosException) when NotFound` around store calls (`EndpointImplementation.cs:117`, `EventImplementation.OperatorActions.cs:270`, `HandoffSettlementService.cs:84`). Whether each behaves correctly with SQL Server needs checking: a SQL-only question, not a transport one |
| `NimBus.Management.ServiceBus`, `NimBus.CommandLine` | Endpoint management and `nb topology apply` over the administration client |
| Other Azure dependencies | `ServiceDefaults`: Azure Monitor exporter. WebApp: `Microsoft.Identity.Web` (Entra). Resolver: Functions worker. Replaceable with OTLP, local Identity (`NimBus.Extensions.Identity`) and the worker host |

### 2.7 Existing issues, both transports

Codex's reviews confirmed two existing Service Bus behaviours. The RabbitMQ transport keeps them
(parity) and they are fixed separately, in Core, for both transports:

1. **Overtaking after unblock.** The session guard (`StrictMessageHandler.cs:99-109`) checks only
   `BlockedByEventId`. After an unblock, a message that arrives before the replay is handled ahead
   of older parked messages, because replays are republished to the back of the topic. No test
   covers the window. `DeferredCount` can't be used as a guard as it stands: it is incremented when
   parking and never decremented or reset in production code.
2. **A duplicate replay can rewrite the outcome.** A replay redelivered after its handler and inbox
   record committed hits the inbox pre-check (`StrictMessageHandler.cs:90`) and sends a skip
   response, which the SQL store applies over the earlier completed state
   (`SqlServerMessageTrackingStore.Writes.cs:169`).
3. **A redelivered skip can strand parked messages.** `HandleSkipRequest`
   (`StrictMessageHandler.cs:326-345`) unblocks the session and then sends the drain trigger. If it
   stops between the two, the redelivered skip fails `VerifySessionIsBlockedByThis`, sends only a
   skip response and completes. No trigger is sent, and the session is no longer blocked, so nothing
   sends one later: the parked messages stay parked while new ones are handled. Handoff completion
   has a repair branch for the same window; skip does not.

These are fixed in Core before an on-prem release (§10), since an on-prem installation inherits
them.

## 3. Decision

Port the Service Bus design to RabbitMQ and SQL Server, one feature at a time, without changing
what Core sees:

| Service Bus | On-prem replacement |
|---|---|
| Session-ordered subscription | Fixed partition queues, single active consumer each (§4.2-4.3) |
| Session lock | SQL partition lease with expiry and renewal (§4.4) |
| Session state | SQL `SessionState` table, same four fields, same calls (§4.5) |
| `Deferred` subscription | SQL `ParkedMessage` table (§4.6) |
| `DeferredProcessor` subscription + hosted service | A trigger queue + the same hosted service, draining from SQL (§4.6) |
| Rule actions + auto-forward | A forwarder that consumes each producer exchange and republishes stamped copies (§4.1) |
| Scheduled messages | SQL `ScheduledMessage` table + dispatcher (§4.7) |
| Abandon (no-op, wait for the lock) | Backoff, then `basic.reject` (§4.8) |

Core's handler logic and contracts are unchanged. The RabbitMQ adapters must give each contract
the same semantics the Service Bus adapters give it: settlement, session state, scheduling with the
original `MessageId`, and `ProcessDeferredMessagesAsync(sessionId, topicName)` addressed by
endpoint. Hosting changes (§4.6, §4.10). Improvements beyond parity, like §2.7, are separate changes
that apply to both transports. The September study's alternative (a SQL "session runtime" that acknowledges on intake
and owns every pending message) stays the fallback if the phase 2 tests fail.

## 4. Design

### 4.1 Topics, subscriptions and the forwarder

On Service Bus a NimBus sender publishes to a **topic**, and the topic's **subscriptions** decide
where copies go (`TopologyDescriptor.ForEndpointTopic`). On RabbitMQ the topic becomes an exchange
plus a **forwarder**, a hosted service that evaluates the same subscription matrix and publishes
the copies. Everything published to a topic goes through its forwarder, as everything on Service
Bus goes through a topic's rules. That includes Resolver responses, retries, continuations,
replays, scheduled dispatches and raw publishes.

Per endpoint topic `T`:

- **Exchange `nimbus.T`** (direct). NimBus senders publish with routing key `p<i>` (§4.2); raw
  publishers use `raw`.
  - **Intake queues `nimbus.T.in.p<i>`**, bound with key `p<i>`.
  - **Raw lane `nimbus.T.in.raw`**, bound with key `raw`.
  - **Alternate exchange `nimbus.T.unrouted`** (fanout), bound to the raw lane, so a raw publish
    with any other key lands there too. RabbitMQ counts a message routed through the alternate
    exchange as routable, so `mandatory` doesn't return it.
- **Partition queues `nimbus.T.p<i>`**: what T's handlers consume.
- **Trigger queue `nimbus.T.deferred-processor`**: §4.6.
- **Dead-letter queue `nimbus.T.dlq`**.

The Resolver topic has an exchange, intake and partition queues of its own, and a forwarder with
one rule: everything goes to its partitions (the retained `$Default` rule).

All quorum queues except the trigger queue use `x-single-active-consumer: true`. All quorum queues
use `dead-letter-strategy: at-least-once` and `overflow: reject-publish`; the defaults dead-letter at
most once. **Raw consumers** (§2.5) bind their own queue to `nimbus.T` with every key (`p0` …
`p<N-1>`, `raw`) and to `nimbus.T.unrouted`. A queue bound with several keys gets each message once.

**The forwarder** runs in the endpoint host. It consumes T's intake queues and raw lane in order
(single active consumer, prefetch 1). For each message it evaluates every subscription below; each
subscription's rules are mutually exclusive (they test different `To` values or event types), so a
subscription yields at most one copy, as on Service Bus.

| Service Bus subscription on topic T | Filter | Copy | Destination |
|---|---|---|---|
| Endpoint subscription `T` | `To = T` | unchanged | `nimbus.route`, key `T.<partition>` → T's partitions |
| | `To = Continuation` | `To = T`, `From = Continuation` | same |
| | `To = Retry` | `To = T`, `From = Retry` | same |
| Reply subscription `T-reply` | `To = T-reply` | not used on RabbitMQ: replies go straight to a reply queue (§4.9) | — |
| Resolver fan-out | `To = Resolver` | `From = T` | `nimbus.Resolver` exchange |
| | `To = T` | unchanged | `nimbus.Resolver` exchange |
| `Deferred` | `To = Deferred AND OriginalSessionId IS NOT NULL` | not used: the sender writes the `ParkedMessage` row directly (§4.6). A parked copy without `OriginalSessionId` matches no subscription and is dropped, as on Service Bus | — |
| `DeferredProcessor` | `To = DeferredProcessor` | unchanged | `nimbus.T.deferred-processor` |
| Consumer forwarder `C` (compiled), one per consumer | `EventTypeId ∈ C's types AND From IS NULL` | `From = T`, `To = C`, per-copy `EventId` | `nimbus.C` exchange, key `p<partition>` |
| Dynamic forwarder `dynamic-C` | same, for dynamic types | same | same |

A message no subscription matches is acknowledged and counted, as Service Bus discards it.

**Publishing a copy.** Every forwarder publish uses publisher confirms and `mandatory`. The forwarder
acknowledges the intake message only when every copy is confirmed and none was returned. A return
(an unroutable copy) or a failed confirm leaves the intake message unacknowledged; it is retried
with `basic.reject` and dead-lettered at the delivery limit (§4.8). A retry republishes copies that
were already confirmed, so a destination can receive the same copy twice. Every copy keeps the
original `MessageId` (Service Bus auto-forwarding keeps it too), and its `EventId` is stable
(below), so a duplicate is recognisable by both: the inbox deduplicates on `MessageId`, and phase 2
verifies that the Resolver treats a repeated `EventId` as the same event.

**Per-copy `EventId`.** Service Bus uses `newid()`; there, auto-forwarding is transactional. Here,
a redelivered intake message must produce the same copies. So the `EventId` is a name-based UUID
(v5) of:

- the **publish identity**: a random publish-id header the RabbitMQ sender stamps on every NimBus
  publish, or, for a raw publish, the CloudEvents `source` plus `id` (unique as a pair by the
  CloudEvents spec), falling back to `MessageId`;
- the topic `T`;
- the subscription name (`C` or `dynamic-C`), so a compiled and a dynamic forwarder to the same
  consumer still give two copies different ids, as two Service Bus rule actions do.

Two identical NimBus publishes have different publish ids, so they stay two events, as on Service
Bus. A raw message with no `MessageId` and no CloudEvents `id` is dead-lettered with a reason; the
on-prem guide tells raw publishers to set one.

The forwarder costs a hop: an event crosses the broker twice from producer to consumer (P's
forwarder → C's topic → C's forwarder → C's partitions), as it crosses two topics on Service Bus.

### 4.2 Partitioning

`partition = stableHash(SessionId) % N(topic)`. The NimBus sender computes it for the topic it
publishes to (routing key `p<i>`); the forwarder computes it for each destination it routes to
(key `T.<i>` for partitions, `p<i>` for another topic's exchange).

**Order is per session per lane.** A session's messages stay in order when they enter a topic
through the same intake lane, which every NimBus publish for that session does (one hash, one
`p<i>`). Raw publishes enter through the raw lane, which another forwarder thread drains, so a raw
publish and a NimBus publish for the same session have no defined order between them. On Service
Bus they meet in one topic in arrival order. Raw publishers that need that ordering set the routing
key to `p<i>` with the documented hash instead of `raw`.

- The hash is fixed and versioned, for example xxHash64 of the UTF-8 session id. Never
  `string.GetHashCode`.
- **Raw publishers can't compute a partition.** The NimBus sender publishes with routing key
  `p<i>`; raw publishers use `raw`, and any other key falls through to the raw lane through the
  alternate exchange (§4.1). The raw lane is a single-active-consumer queue the
  forwarder drains in order. Raw publishers to one endpoint keep their order among themselves but
  share one serial lane. The forwarder reads a raw message's session id from a `nimbus-session-id`
  header, falling back to the CloudEvents `partitionkey` extension; on Service Bus the raw publisher
  sets the broker's `SessionId` property instead.
- **A partition is never stored.** Parked, scheduled and outbox rows keep the destination and
  session id; the partition is computed at publish time.
- **Changing N** for an endpoint: stop everything that publishes to it (including its producers'
  forwarders and the scheduled and outbox dispatchers), let its partition and intake queues drain
  empty, update N, the routing table version and raw consumers' bindings, restart. Senders and forwarders refuse to publish
  under a stale version.
- **Not** the consistent-hash exchange. Its ring is rebuilt at boot, so after a restart "a given
  routing key now may route to a different queue", which breaks per-session order.

**Missing session id.** The SDK assigns a GUID to events without a session key
(`Abstractions/Events/Event.cs:23`), but `MessageHelper.cs:141` passes any `IMessage.SessionId`
through. Phase 1 records what Service Bus does with an empty session id on a session-enabled
subscription; the RabbitMQ sender and forwarder do the same.

### 4.3 Receiving

The receiver host consumes every partition queue of its endpoint. Single active consumer makes one
consumer active per partition, with automatic failover. Within a partition, messages are handled
one at a time (prefetch 1). The broker ack is `Complete`. Endpoint parallelism is the number of
partitions, instead of `MaxConcurrentSessions`.

**Behavioural difference:** a slow handler delays the other sessions in its partition. It never
blocks them, because failures park (§2.2). Choosing N trades parallelism against queue count.

### 4.4 Partition lease

The counterpart of the session lock. `PartitionLease(Queue, Owner, Epoch, ExpiresUtc)`:

- A consumer must hold the lease before it handles a delivery. On its first delivery after
  subscribing, it takes the lease if it is free or expired (`Epoch + 1`); if another owner's lease is
  still valid, it holds the delivery unacknowledged and retries until the lease expires.
- The holder renews it on a timer (for example a 60-second lease renewed every 20 seconds).
- **If renewal fails or the lease is found expired,** the consumer cancels the running handler's
  cancellation token, stops publishing for it, and closes its channel without acknowledging. The
  broker requeues the unacknowledged delivery for the successor. Its SQL writes already fail on the
  predicate below. Like Service Bus after `SessionLockLost`, anything the handler already published
  stays published.
- **Timeouts.** A successor waiting for a lease holds one delivery unacknowledged. The lease period
  (plus the SQL timeout) must stay well below the RabbitMQ consumer timeout (default 30 minutes).
  If the timeout fires anyway, for example during a long SQL outage, the broker cancels the consumer
  and requeues the delivery; that increments only the acquired count, not the delivery count, so it
  doesn't push the message toward the dead-letter queue.
- **Every transport SQL write** (session state, parking, drain, scheduling) carries
  `WHERE Owner = @me AND Epoch = @epoch AND ExpiresUtc > SYSUTCDATETIME()` against the lease row,
  in the same transaction. A stale write fails with `TransientException`, as `SessionLockLost`
  does on Service Bus.

A consumer that lost its broker connection keeps a valid lease until it expires, but its successor
doesn't start until then, so the two never overlap: the same guarantee a Service Bus session lock
gives. As on Service Bus, this doesn't fence handler side effects: a Resolver response already
published, inbox or outbox rows written by handler code, calls to external systems. The inbox and
destination idempotency remain the answer. The cost is a takeover delay of up to one lease period
after a crash.

### 4.5 Session state

`SessionState(Endpoint, SessionId, BlockedByEventId, BlockedByMessageId, DeferredCount,
NextDeferralSequence)`: the same four fields, read and written through the same
`IMessageContext` calls as on Service Bus, under the lease (§4.4).

### 4.6 Parking and replay

Same flow as Service Bus, with SQL instead of the `Deferred` subscription:

- **Park.** `ISender.Send` with `To = "Deferred"` inserts a `ParkedMessage(Id identity, Endpoint,
  SessionId, DeferralSequence, ParentMessageId, Body, Headers, ParkedAtUtc)` row. The parked copy
  has no `MessageId` (§2.2), so the row is unique on `(Endpoint, SessionId, ParentMessageId)`: a
  redelivered original that parks again doesn't create a second row. Order is `DeferralSequence`,
  as on Service Bus. Core's other two calls (sequence, count) update `SessionState` as before.
- **Trigger.** `ProcessDeferredRequest` (`To = "DeferredProcessor"`) goes through E's forwarder to
  `nimbus.E.deferred-processor`, a plain queue. `DeferredMessageProcessorHostedService` builds a
  `ServiceBusProcessor` today (line 72), so it needs a RabbitMQ variant that consumes this queue and
  calls `IDeferredMessageProcessor.ProcessDeferredMessagesAsync(sessionId, topicName)` with the
  endpoint as `topicName`, as now.
- **Drain.** The RabbitMQ `IDeferredMessageProcessor`, standing in for `AcceptSessionAsync`:
  1. Opens a dedicated SQL connection and takes a session-owned application lock
     (`sp_getapplock`) on `(Endpoint, SessionId)`, held for the whole drain, so two triggers can't
     drain one session at once. If the lock is taken, the trigger is a no-op, as
     `SessionCannotBeLocked` is today.
  2. Repeats: read the next batch of rows with `DeferralSequence` above the last one handled, in
     order. For each row, if its `ParentMessageId` is the `MessageId` of a replay from this drain,
     the replay was parked again: stop, leaving it and everything after it
     (`IsReplayParkedAgain`). Otherwise publish it to topic E (`nimbus.E`, key `p<i>`) with a new
     `MessageId`, as `CreateRepublishedMessage` does, wait for the confirm, check it wasn't returned,
     and delete the row.
  3. Finishes when a read returns no rows, then releases the lock.

  The endpoint can park new rows while a drain runs; reading in batches by sequence finds them, as
  receiving from `Deferred` does today. The replay goes through E's forwarder, so it reaches E's
  partitions and the Resolver fan-out exactly as a Service Bus replay published to E's topic does.

A crash between confirm and delete republishes that row on the next drain: the same window Service
Bus has between send and complete. §2.7 applies unchanged.

### 4.7 Scheduling

There is no native scheduled delivery. `ScheduledMessage(Id identity, Destination, SessionId,
Body, Headers, DueUtc, State)` holds the message. A dispatcher claims due rows with
`UPDATE … SET State = 'Dispatching' WHERE State = 'Pending'`, publishes each (confirm +
`mandatory`), and deletes it after the confirm. The row id is the `long` token
`ISender.ScheduleMessage` returns; `CancelScheduledMessage` deletes the row only while it is
`Pending`, so a cancel and a dispatch can't both win. A row stuck in `Dispatching` after a crash is
retried after a timeout, which can publish it twice: at-least-once, like every other send. A
returned (unroutable) message keeps its row and is flagged for an operator.

`ScheduleRedelivery` and retry-policy delays use the same table. `ScheduleRedelivery` keeps the
original `MessageId`, as the Service Bus implementation does (`MessageContext.cs:594`, Spec 030
§5.7), and the original is completed only after the row has committed.

**Not** the delayed-message-exchange plugin: its delayed messages have a single replica per node,
and it isn't meant for large numbers of delayed messages.

### 4.8 Transient retries

The RabbitMQ `Abandon` waits a short backoff in-process, standing in for the wait for the Service
Bus session lock (§2.3), then sends `basic.reject` with requeue. Not `basic.nack`: from RabbitMQ
4.3, reject and a crashed channel increment the delivery count, nack does not, so a nack-based retry
could loop forever. With reject, the quorum queue's delivery limit (default 20) dead-letters the
message. Returned messages are redelivered "in the exact order they were returned".

**Difference from Service Bus:** the backoff and retries hold up the rest of the partition; Service
Bus holds only the session. The bound is roughly the delivery limit times the backoff. The POC
measures whether that is acceptable.

### 4.9 Request/reply

On RabbitMQ, `PublisherClient.Request` declares an exclusive, auto-delete reply queue for the
client instance and **starts consuming it before sending** the request, with `ReplyTo = <queue>`
and a correlation id. Today it sends first and then opens the reply session (§2.1); on RabbitMQ a
fast reply to an undeclared queue would be unroutable. Replies are matched by correlation id; they
don't need session order. `IReplyDispatcher` publishes to the `ReplyTo` queue through the default
exchange with `mandatory`.

### 4.10 Management, hosting and tools

- **Choosing the transport.** `NimBus.SDK` stops referencing `NimBus.ServiceBus` directly. Each
  transport registers itself (`AddNimBusServiceBusTransport()` / `AddNimBusRabbitMqTransport()`).
  The receiver host, publisher, subscriber, reply receiver, handoff client and outbox dispatcher
  depend on transport-neutral types. Members typed on Service Bus (§2.1) keep working through
  `[Obsolete]` bridges until the next major (`docs/versioning.md`).
- **ManagerClient** sends through `ISender`.
- **WebApp startup** registers transport services by configuration, as it chooses the store today.
  Each feature in §2.6 gets an on-prem answer from the phase 1 inventory. The first RabbitMQ release
  keeps the transport-neutral features (message store, resubmit, skip, handoff settlement, store
  metrics) and hides the Service Bus–specific ones. A later release rebuilds them on the RabbitMQ
  management HTTP API and the parking table.
- **Resolver** runs in `NimBus.Resolver.Host` with the transport's receiver.
- **CLI.** `nb topology apply --transport rabbitmq` declares exchanges, queues and bindings and
  writes the routing table. RabbitMQ rejects re-declaring a queue with different arguments, so
  changed arguments need an explicit migration.
- **Observability.** W3C trace context in message headers, carried through the forwarder; OTLP
  instead of Azure Monitor.
- **Identity.** Local Identity (`NimBus.Extensions.Identity`) instead of Entra.
- **Client.** `RabbitMQ.Client` 7.x over AMQP 0-9-1. The Service Bus SDK can't use RabbitMQ's AMQP
  1.0 support: it depends on Service Bus–specific management links and session filters.

## 5. Parity with Service Bus

| Behaviour | Service Bus | RabbitMQ + SQL |
|---|---|---|
| Per-session order | Session lock, in topic arrival order | Partition, single active consumer, partition lease; per intake lane, so a raw and a NimBus publish for one session are not ordered against each other (§4.2) |
| Failed session isolated from others | Yes (park) | Yes (park) |
| Concurrency unit | Session | Partition |
| Slow handler or transient retry delays others | No | Same partition only, bounded |
| Transient retry bounded | Delivery limit | Delivery limit, using `basic.reject` |
| Two consumers on one session | Prevented by the lock; takeover after expiry | Prevented by the lease; takeover after expiry |
| Writes after lock loss rejected | Session state and settlement (`SessionLockLost`) | Transport SQL writes after lease expiry or epoch change; broker acks fail once the channel is closed (§4.4). Neither fences publishes already made |
| Handler side effects after lock loss | Not fenced | Not fenced |
| Fan-out for raw publishers, raw consumers | Yes | Yes, through the forwarder |
| Forwarding | Transactional auto-forward of each subscription copy | Not transactional; a redelivered intake message republishes identical copies (same `MessageId`, same `EventId`) (§4.1) |
| Parking, replay, trigger | 3 writes; republish with new `MessageId` | Same; duplicate park suppressed |
| The three issues in §2.7 | Present | Present; fixed in Core for both before an on-prem release |
| Scheduled send token | Sequence number | SQL identity |
| Delivery guarantee | At least once | At least once |
| Dead-letter browsing in WebApp | Yes | Later release |

## 6. Deployment profile

- RabbitMQ 4.3 or later (pinned in phase 2), three nodes, quorum queues replicated across them. A
  majority must be up, so one node can be lost.
- SQL Server for tracking, inbox, outbox, routing, leases, session state, parking and scheduling.
  Its HA and backup policy now covers in-flight work: parked and scheduled messages exist only in
  SQL.
- Endpoints (each with its forwarder), Resolver and WebApp as .NET worker/ASP.NET Core services or
  containers. Aspire composes local development with a `RabbitMQ` resource next to SQL Server.
- No Azure dependency at runtime, once the phase 1 inventory is worked through.

## 7. Risks and open questions

1. **Head-of-line latency** inside a partition from slow handlers and transient retries (§4.3,
   §4.8). Needs throughput and latency targets from the customers.
2. **Forwarder hop.** Every message crosses the broker at least twice (topic exchange, forwarder, partition), and an event four times from producer to consumer. Phase 2 measures the cost.
3. **Raw publishers share one serial lane** per endpoint (§4.2). Fine for PartnerPortal-style
   inbound traffic; a high-volume raw publisher would need the partition header.
4. **Takeover delay** of up to one lease period after a consumer crash (§4.4).
5. **Partition count** is fixed between maintenance windows (§4.2).
6. **Requeue order after a dropped connection.** The RabbitMQ docs describe returned-message order
   for nack, reject and timeouts, not explicitly for a dropped connection. Phase 2 tests it.
7. **Migration from Azure.** First release: new installations or an offline cutover after draining
   sessions and the `Deferred` subscriptions. No live bridge.
8. **WebApp parity** decides when the transport is supportable, more than the message flow.

## 8. Out of scope

- A generic transport framework or other brokers. Apache ActiveMQ Artemis (message groups) was
  considered in the September study and is not pursued.
- Running the NimBus Service Bus emulator in production. It keeps messages, locks and session state
  in memory, binds to loopback only and doesn't validate SAS signatures.
- PostgreSQL or another store. SQL Server only.
- Improving on Service Bus behaviour inside the transport. §2.7's fixes are separate Core changes.

## 9. Acceptance tests for phase 2

**Shared behaviour suite** in `NimBus.Testing`, run against the in-memory transport, the Service
Bus emulator and RabbitMQ, so a behavioural difference shows up as a failing test:

- **Interleaved order.** Sessions A and B share a partition (or, on Service Bus, run concurrently).
  A fails and parks; B completes; retry or skip of A replays A's parked messages in deferral order.
- **Park during drain.** A replay fails again mid-drain; the drain stops and leaves the rest parked.
- **Every subscription route.** For each row of the §4.1 table: endpoint, continuation, retry,
  Resolver fan-out (both rules), DeferredProcessor, compiled and dynamic forwarders (including both
  to one consumer), and a message no route matches. Assert `To`, `From`, `EventId` distinctness
  and the destination.
- **Transient retry.** A handler that always throws `TransientException` is dead-lettered at the
  delivery limit.
- **Scheduling.** Schedule, cancel, race a cancel against a due dispatch, and `ScheduleRedelivery`
  keeping the original `MessageId`.
- **Request/reply** across two instances, including a reply that arrives immediately.
- **The three §2.7 issues**, as regression tests for their Core fixes: overtaking after unblock,
  outcome rewrite on a duplicate replay, and a skip redelivered after its unblock.

**RabbitMQ-only tests**, because the mechanisms have no counterpart elsewhere:

- **Lease.** Pause the active consumer mid-handler and cut its connection: the successor handles
  nothing until the lease expires, and the old consumer's later SQL writes fail. Repeat with the
  pause before the successor's first delivery. Make renewal fail mid-handler: the handler is
  cancelled and its delivery requeued. Hold a waiting successor past a short consumer timeout: the
  delivery is requeued without counting toward the delivery limit.
- **Crash points.** Kill the process before and after each SQL write and before and after each ack,
  in handling, parking, draining, forwarding and scheduled dispatch. Nothing is lost; a redelivered
  park doesn't add a row; a redelivered intake message republishes identical copies.
- **Partial forwarder confirm.** One copy confirmed, the next returned: the intake message is not
  acknowledged, and the retry produces the same `MessageId` and `EventId` for the first copy.
- **Concurrent drains.** Two triggers for one session drain it once.
- **Raw ingress.** A raw publish with key `raw`, with an arbitrary key (alternate exchange), and
  without `message-id` or CloudEvents `id` (dead-lettered). A raw consumer sees all three originals.
  A raw and a NimBus publish for one session: document the observed order, matching §5.
- **Broker failover.** Kill a quorum queue leader and the active consumer; order holds.
- **Capacity.** Throughput and p95 latency against agreed targets for a chosen N.

## 10. Delivery

| Phase | Work | Exit |
|---|---|---|
| 0 | ADR amending ADR-001, `CONTRIBUTING.md` and `AGENTS.md` for one on-prem transport; GitHub issue per the contribution process; customer throughput and latency targets | ADR accepted, targets agreed |
| 1 | Inventory every Service Bus- or Cosmos-typed service the SDK, Resolver host and WebApp resolve at startup, with an on-prem answer each (§2.6). Transport-neutral SDK surface with `[Obsolete]` bridges. `ManagerClient` over `ISender`. WebApp transport selection. The shared suite of §9, green on in-memory and the Service Bus emulator. Core fixes for §2.7 with their regression tests | Release build and existing tests green; WebApp and Resolver start with no Service Bus configuration (features hidden) |
| 2 | `NimBus.Transport.RabbitMq`: sender, forwarder, partitioned receiver, lease, session state, parking and drain, scheduler, reply queues, topology provisioning, the RabbitMQ variant of the deferred-processor host | §9 passes on RabbitMQ |
| 3 | CrmErpDemo on RabbitMQ and SQL; Aspire resource; OTLP; local Identity | CrmErpDemo end-to-end tests green on RabbitMQ |
| 4 | WebApp admin on the RabbitMQ management API; install and upgrade runbook; HA and backup tests | Supportable release |

No effort estimate yet. Estimate after phase 0, from the phase 1 inventory and a phase 2 work
breakdown.

## 11. Review record

Codex reviewed every revision on 2026-10-21: revision 1 (rethink), revision 2 (rethink), revision 3
(sound after fixes). The reviews are in
[`docs/plan/2026-10-21-spec-041-review.md`](../../plan/2026-10-21-spec-041-review.md). Every finding
was checked against the code and accepted.

| Finding | Where it is answered |
|---|---|
| R1 Code 1: separate replay trigger and hosted service | Kept as on Service Bus, with a RabbitMQ hosting variant (§4.6) |
| R1 Code 2, R2 ordering: overtaking gap, `DeferredCount` never decremented | Existing issue (§2.7), fixed in Core before release |
| R1 Code 3: `Abandon` is a no-op | §2.3, §4.8 |
| R1 Code 4: conditional completion and retry | §2.2 |
| R1 Design 1, R2 routing, R3 1-2: rule actions, bypassing publishers, the full subscription matrix | Topic forwarder with the complete route table (§4.1); publishers listed (§2.5) |
| R1 Design 2, R2 synthetic blocker and replay settlement | Removed; Core paths unchanged (§3, §4.6) |
| R2 skip strands parked rows | Existing issue (§2.7, item 3), fixed in Core before release |
| R1 Design 3: `basic.nack` unbounded from 4.3 | `basic.reject` (§4.8) |
| R1 Design 4, R2 parked identity, R2 inbox outcome rewrite | Rows keyed on `ParentMessageId` (§4.6); outcome rewrite is §2.7 item 2 |
| R1 Design 5, R2 epoch window, R3 7: lease loss and timeouts | Expiring lease checked in each write; renewal-failure path; timeout margin (§4.4) |
| R1 Design 6: routing, per-destination N, pending sources | Per-destination partitions computed at publish time; drain-and-restart for N (§4.2) |
| R1 Design 7: missing session id | Match Service Bus, recorded in phase 1 (§4.2) |
| R2 `EventId` seed, R3 4: CloudEvents `source`, two forwarders to one consumer | Seed = publish identity + topic + subscription (§4.1) |
| R3 3: two intake lanes can reorder a session | Order defined per lane; raw publishers can use `p<i>` (§4.2, §5) |
| R3 5: unroutable copies, partial confirms | `mandatory` + returns; ack only after all copies; identical copies on retry (§4.1) |
| R3 6: alternate exchange invisible to raw consumers | Fanout alternate exchange; raw consumers bind to it (§4.1) |
| R3 8: drain lock lifetime, observing re-parks | Session-owned app lock for the whole drain; batch reads by sequence (§4.6) |
| R3 9: "Core is unchanged" too broad | Contract semantics stated; hosting changes named (§3, §4.6, §4.7) |
| R2 reply queue declared after send | Declare and consume before sending (§4.9) |
| R1/R2 Completeness: SDK surface, startup inventory, estimate | §2.1, §2.6, §4.10, §10 |
| R2, R3: §5 and §9 overstate | §5 rows scoped; §9 split into shared and RabbitMQ-only tests |

## 12. Sources

Checked 2026-10-21.

- RabbitMQ, [Quorum queues](https://www.rabbitmq.com/docs/quorum-queues): single active consumer
  support; returned-message order; delivery limit default; from 4.3, `basic.reject` and channel
  crashes increment the delivery count and `basic.nack` does not; at-least-once dead-lettering
  requirements.
- RabbitMQ, [Consumers](https://www.rabbitmq.com/docs/consumers): single active consumer failover;
  active-consumer notifications exist for AMQP 1.0, not 0-9-1.
- RabbitMQ, [Alternate exchanges](https://www.rabbitmq.com/docs/ae): unroutable messages are
  republished to the exchange's alternate exchange; any exchange except the default one.
- RabbitMQ, [Exchanges](https://www.rabbitmq.com/docs/exchanges) and
  [AMQP concepts](https://www.rabbitmq.com/tutorials/amqp-concepts): bindings route without
  transforming.
- RabbitMQ, [Confirms](https://www.rabbitmq.com/docs/confirms): publisher confirms and consumer acks
  are independent; unroutable messages need `mandatory` to be returned.
- RabbitMQ,
  [consistent hash exchange](https://github.com/rabbitmq/rabbitmq-server/tree/main/deps/rabbitmq_consistent_hash_exchange):
  ring rebuilt at boot; routing can change after a restart.
- Tencent Cloud TDMQ,
  [delayed message limitations](https://intl.cloud.tencent.com/document/product/1112/60154), and the
  plugin's [README mirror](https://github.com/Sergi0Limit/rabbitmq-delayed-message-exchange):
  single replica per node, not for large volumes. Secondary sources; the upstream repository's
  current status was not confirmed.
- `src/NimBus.ServiceBusEmulator/README.md`: emulator storage and limits.
