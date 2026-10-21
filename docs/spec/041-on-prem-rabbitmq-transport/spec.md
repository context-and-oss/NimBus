# Spec 041 — On-prem NimBus: RabbitMQ transport with SQL Server storage

Status: investigation, revision 2. Not implemented; no code, broker or benchmark exists yet.
Revision 2 answers the Codex review of revision 1
([`docs/plan/2026-10-21-spec-041-review.md`](../../plan/2026-10-21-spec-041-review.md)); §11 maps
each finding to its change.
Baseline: master `3f6e9cff`. Supersedes the transport design in the September study
(`docs/spec/on-prem-nimbus-feasibility.html`, added in `b29ff5bb`, removed in `71f2220a`; its
plan is still at [`docs/plan/2026-09-14-on-prem-feasibility.md`](../../plan/2026-09-14-on-prem-feasibility.md)).
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
`InMemoryMessageContext`).

The **public SDK surface is not transport-neutral**, so Core alone is not the whole seam:

- `ISubscriberClient` inherits the Service Bus–specific `IServiceBusAdapter`
  (`SDK/ISubscriberClient.cs:8`).
- `PublisherClient.Request` receives the reply with a `ServiceBusSessionReceiver`
  (`SDK/PublisherClient.cs:279`).
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
  and **complete the original**. Those are three separate calls.
- **Retry or resubmission succeeds** (lines 184-207, 258-293): unblock the session, then send a
  `ProcessDeferredRequest` if the deferred count is above zero (`ContinueWithAnyDeferredMessages`,
  line 637).

So on the normal path the queue never holds a failed message: it is completed and recorded.
Exceptions: if sending the response, writing session state or completing fails, the delivery stays
unsettled and is redelivered. This is the central finding: **the broker only has to deliver each
session in order to one consumer at a time.** Isolating a failed session from the others already
happens above the broker.

### 2.3 Transient failures

`MessageHandler` catches `TransientException` and calls `Abandon`
(`Core/Messages/MessageHandler.cs:70-82`). On Service Bus `Abandon` **does nothing**
(`ServiceBus/MessageContext.cs:250-257`). The message stays unsettled and returns when the session
lock is released or expires, so the retry pause is however long the session stays held. Other
sessions are unaffected.

### 2.4 Service Bus features the transport relies on

| Feature | Where | Notes |
|---|---|---|
| Session receivers: ordered, exclusive per session, `MaxConcurrentSessions` in parallel | SDK receiver host, Resolver host | The ordering primitive |
| Session state | `ServiceBus/SessionState.cs`, `ServiceBusSession.cs:95-145` | 4 live fields: `BlockedByEventId`, `BlockedByMessageId`, `DeferredCount`, `NextDeferralSequence` |
| `Deferred` subscription (sessions on) | `TopologyDescriptor.cs:177-192` | Parked copies, filtered `To = 'Deferred' AND OriginalSessionId IS NOT NULL` |
| `DeferredProcessor` subscription (sessions off) | `TopologyDescriptor.cs:195-206`, `SDK/Hosting/DeferredMessageProcessorHostedService.cs:80` | Receives `ProcessDeferredRequest` triggers (`ResponseService.cs:377`), consumed by its own hosted service, not by `StrictMessageHandler` (line 431) |
| Replay | `ServiceBus/DeferredMessageProcessor.cs:63-130, 190` | `AcceptSessionAsync(topic, "Deferred", sessionId)`, sort by deferral number, republish to the main topic **with a new `MessageId`**, complete; stop when a replay is parked again |
| Scheduled send and cancel | `ServiceBus/Sender.cs:30-33` | Token is the Service Bus sequence number |
| Dead-letter queue | `ServiceBusSession.cs:80-88`, WebApp `ResolverDeadLetterClient` | |
| Rule actions | `TopologyDescriptor.cs:94-115` | Forwarders: filter `EventTypeId = X AND From IS NULL`, action `SET From = <producer>; SET EventId = newid(); SET To = <consumer>`. Retry and continuation rules re-stamp `From` and `To` |

### 2.5 Coupling outside the transport project

| Area | Coupling |
|---|---|
| `NimBus.SDK` | References `NimBus.ServiceBus`; public types in §2.1; handoff client |
| `NimBus.Manager` | `ManagerClient` takes a `ServiceBusClient`, although resubmit and skip are ordinary messages (`ResubmissionRequest`, `SkipRequest`) |
| `NimBus.Resolver` / `.Host` | Functions entry point uses a Service Bus session trigger; the worker host constructs `NimBusReceiverHostedService` from `ServiceBusClient` (`Resolver.Host/Program.cs:44`) |
| `NimBus.WebApp` startup | `AddServiceBusClients` runs unconditionally (`Startup.cs:31`); the heartbeat sender is `ServiceBusHeartbeatMessageSender` (`Startup.Web.cs:139`); `EndpointImplementation` requires `IServiceBusManagement` (line 38); simulation hosts use the Service Bus SDK |
| `NimBus.WebApp` admin | Purge, subscription admin, topology, dead-letter client, deferred-message inspector: about 2,800 lines in `Services/AdminService*.cs`, `SubscriptionAdminService.cs`, `ResolverDeadLetterClient.cs`, `DeferredMessageInspector.cs` |
| `NimBus.WebApp` storage | Ten files reference `Microsoft.Azure.Cosmos`, mostly `catch (CosmosException) when NotFound` around store calls (`EndpointImplementation.cs:117`, `EventImplementation.OperatorActions.cs:270`, `HandoffSettlementService.cs:84`). Whether each behaves correctly when the store is SQL Server needs checking; it is a SQL-only question, not a transport one |
| `NimBus.Management.ServiceBus`, `NimBus.CommandLine` | Endpoint management and `nb topology apply` over the administration client |
| Other Azure dependencies | `ServiceDefaults`: Azure Monitor exporter. WebApp: `Microsoft.Identity.Web` (Entra). Resolver: Functions worker. Replaceable with OTLP export, local Identity (`NimBus.Extensions.Identity`) and the worker host |

### 2.6 Existing ordering gap (both transports)

The session guard (`StrictMessageHandler.cs:99-109`) checks only `BlockedByEventId`.
`MessageHandler` adds no replay guard. After an unblock, the session accepts new messages straight
away, while the `ProcessDeferredRequest` trigger is still handled separately (§2.4) and replays are
republished to the back of the main topic. A message that arrives in that window is handled before
older parked messages. Codex confirmed this, and found no test that covers the window.

`DeferredCount` cannot serve as a guard as it stands: production code increments it when parking,
but nothing calls `DecrementDeferredCount` or `ResetDeferredCount`.

This is Service Bus behaviour today. The RabbitMQ design closes it (§4.5). Closing it on Service Bus
is a separate fix with its own regression test.

## 3. Decision

Build a RabbitMQ transport that matches what Service Bus gives NimBus, closing §2.6 on the way:

- **RabbitMQ** delivers each session in order to one consumer at a time, using fixed partition
  queues with a single active consumer.
- **SQL Server** holds what Service Bus holds besides messages: routing, session state, parked
  messages and scheduled sends.
- **The NimBus sender does what Service Bus rule actions do**: fan-out and re-stamping (§4.1).
- **Core's handler logic is unchanged.** The transport implements the four Core contracts. The SDK
  gets a transport-neutral surface with `[Obsolete]` bridges for the Service Bus–typed members.

The September study's alternative (a SQL "session runtime" that acknowledges on intake and owns
every pending message) rejected partitioned queues because "one failed entity blocks its partition
if strict completion order is retained". §2.2 shows NimBus doesn't retain the failed message, so
that objection doesn't apply. The runtime design stays the fallback if the phase 2 tests fail.

## 4. Design

### 4.1 Routing: the sender fans out

RabbitMQ bindings route a message unchanged; they can't set `From`, a per-copy `EventId` or `To`.
So the NimBus sender does it before publishing:

- `nb topology apply --transport rabbitmq` writes a **routing table** to SQL from the same endpoint
  and event catalog that `TopologyDescriptor` uses today: which endpoints consume which event types
  from which producers, the control-message rules (retry, continuation, resolver, reply), and each
  endpoint's partition count.
- The sender loads the table (cached, refreshed periodically) and, for a published event, publishes
  **one copy per consuming endpoint**, stamped exactly as the forwarder action would:
  `From = <producer>`, `To = <consumer>`, a per-copy `EventId`. Control messages get the `From`/`To`
  stamps of their rule.
- **Per-copy `EventId` is deterministic**: a name-based UUID (v5) of the original `EventId` and the
  consumer endpoint. Service Bus generates a random one (`newid()`). A deterministic id means a
  publish retried after a crash produces the same copy, which inbox deduplication can recognise.
  The Resolver and WebApp only need it to be unique per copy; phase 1 confirms nothing depends on it
  being random.
- A routing-table entry that can't be expressed is rejected when provisioning, never dropped.

Broker topology is then flat: one **direct exchange** `nimbus`, and per endpoint **N partition
queues** `nimbus.<endpoint>.p<i>`, bound with routing key `<endpoint>.<i>`. Each partition queue is a
quorum queue with `x-single-active-consumer: true`, a dead-letter exchange,
`dead-letter-strategy: at-least-once` and `overflow: reject-publish` (the defaults dead-letter at
most once). Each endpoint also has a dead-letter queue `nimbus.<endpoint>.dlq`.

Every publish uses publisher confirms and the `mandatory` flag. An unroutable message comes back as
a return, and the send fails instead of being silently confirmed.

### 4.2 Partitioning

`partition = stableHash(SessionId) % N(destination)`, computed by the sender **per destination**,
at publish time.

- The hash is fixed and versioned, for example xxHash64 of the UTF-8 session id. Never
  `string.GetHashCode`.
- **A partition is never stored.** Parked rows, scheduled rows, outbox rows and retries store the
  destination endpoint and session id; the partition is computed when they are published. So a
  change of N affects only messages already in the broker.
- **Changing N** for an endpoint: stop its publishers, let its partition queues drain empty, update
  N in the routing table, restart. The routing table carries a version, and senders refuse to
  publish with a stale version. Stopping the endpoint's own consumers isn't needed.
- **Not** the consistent-hash exchange. Its ring is rebuilt at boot, so after a restart "a given
  routing key now may route to a different queue", which breaks per-session order.

**Messages without a session id.** The SDK assigns a GUID to events without a session key
(`Abstractions/Events/Event.cs:23`), but `MessageHelper.cs:141` passes any `IMessage.SessionId`
through as given, and the deferred dispatcher handles a missing one
(`SDK/Hosting/DeferredMessageDispatcher.cs:74`). The RabbitMQ sender assigns a GUID when the session
id is empty and writes it into the message, so retries, replies and parking see the same value.
Phase 1 records what Service Bus does with an empty session id on a session-enabled subscription,
and the transport matches it if it is a rejection.

### 4.3 Receiving

The receiver host consumes every partition queue of its endpoint. Single active consumer makes one
consumer active per partition, with automatic failover. Within a partition, messages are handled
one at a time (prefetch 1). The broker ack is `Complete`. Endpoint parallelism is the number of
partitions, instead of `MaxConcurrentSessions`.

**Behavioural difference:** a slow handler delays the other sessions in its partition. It never
blocks them, because failures park (§2.2). Choosing N trades parallelism against queue count.

### 4.4 SQL tables

In the endpoint's SQL database, in a `nimbus_transport` schema:

- `Routing(...)`, `RoutingVersion`: §4.1-4.2.
- `SessionState(Endpoint, SessionId, BlockedByEventId, BlockedByMessageId, ParkedBehindEventId,
  RowVersion)`.
- `ParkedMessage(Id bigint identity, Endpoint, SessionId, MessageId, Body, Headers, ParkedAtUtc)`,
  unique on `(Endpoint, SessionId, MessageId)`. Order is `Id`.
- `ScheduledMessage(Id bigint identity, Destination, SessionId, MessageId, Body, Headers, DueUtc,
  State)`.
- `PartitionLease(Endpoint, Partition, Epoch, Owner, AcquiredUtc)`: §4.8.

### 4.5 Parking and replay

**Parking is one SQL write.** Core still makes three calls (§2.2); the RabbitMQ `IMessageContext`
maps them to one:

| Core call | RabbitMQ transport |
|---|---|
| `GetNextDeferralSequenceAndIncrement` | Returns a provisional number; not authoritative |
| `ISender.Send(To = "Deferred")` | `INSERT ParkedMessage`; the identity `Id` is the real order. A duplicate `MessageId` (a redelivery after a crash between insert and ack) is a no-op |
| `IncrementDeferredCount` | No-op |
| `GetDeferredCount` / `HasDeferredMessages` | `COUNT(*)` / `EXISTS` over `ParkedMessage` for the session |
| `Decrement…` / `Reset…` | No-op; the count is derived |

Since the count is derived from the rows, it can't drift (§2.6).

**The session is open only when it is not blocked and has no parked rows.** Core must still see a
message that arrives while rows remain, because its blocked path sends the deferral response the
Resolver records. So `SessionState` keeps the blocking event id in a `ParkedBehindEventId` column
until the backlog is empty. While rows remain, `GetBlockedByEventId` returns it for every non-replay
context. Core then throws `SessionBlockedException`, sends the deferral response, and parks the
message behind the backlog. Replay contexts see the real `BlockedByEventId`. This closes §2.6.
Phase 2 must check the Core paths that compare against the blocker
(`IsSessionBlockedByThis`, `IsThisEvent`) for a late retry or resubmission of the old blocker that
arrives during a drain.

**Replay runs inside the partition consumer.** The `ProcessDeferredRequest` trigger carries the
session id, so the sender routes it to that session's partition, like any other message for the
session. The partition consumer recognises it and, instead of republishing:

1. Reads up to K parked rows for the session in `Id` order.
2. Runs each row through the normal handler pipeline as a replay context with its **original
   `MessageId`**, so the inbox recognises a replay that already succeeded.
3. On success (`Complete`), deletes the row, in the same SQL transaction as any session-state
   change.
4. If the replay fails, the session is blocked again. Core then parks the next rows, and for a
   replay context "park" leaves the row where it is. The drain stops, as `IsReplayParkedAgain` does
   today. The rows wait for the next unblock.
5. After K rows, if rows remain, it publishes a new trigger to the back of the partition and acks
   the current one. Other sessions in the partition get a turn. New arrivals for this session keep
   parking behind the backlog, because rows exist.
6. When no rows remain, it clears `ParkedBehindEventId` in the same transaction as the last delete,
   and the session is open.

A crash anywhere in the drain leaves the rows that weren't deleted. The trigger is redelivered, or a
later unblock sends a new one. A row whose handler finished but wasn't deleted is replayed with the
same `MessageId`. With the inbox enabled it is recognised as a duplicate; without the inbox it is
handled again, which is the same at-least-once delivery as Service Bus.

There is no `Deferred` or `DeferredProcessor` entity on RabbitMQ, and no separate
`DeferredMessageProcessorHostedService`. `IDeferredMessageProcessor` remains for the Service Bus
transport.

### 4.6 Scheduling

There is no native scheduled delivery. `ScheduledMessage` holds the message and its due time. A
dispatcher polls for due rows, publishes each one (confirms + `mandatory`) with the message's own
`MessageId`, and deletes it after the confirm. The row's `bigint` id is the `long` token that
`ISender.ScheduleMessage` returns. `CancelScheduledMessage` deletes the row if it hasn't been
claimed; claim and cancel are a single `UPDATE … WHERE State = 'Pending'`, so they can't both win.

A crash between confirm and delete republishes the row with the same `MessageId`: at-least-once, as
in §4.5. A returned (unroutable) message keeps its row and is flagged for an operator.

`ScheduleRedelivery` (throttling) and retry-policy delays use the same table.

**Not** the delayed-message-exchange plugin. Its delayed messages have a single replica per node,
and it isn't meant for large numbers of delayed messages.

### 4.7 Transient retries

Service Bus doesn't redeliver immediately (§2.3); the message returns after the session lock is
released. The RabbitMQ `Abandon`:

1. Waits a short backoff in-process (configurable, default a few seconds).
2. Then sends `basic.reject` with requeue.

`basic.reject` is used, not `basic.nack`. From RabbitMQ 4.3, `basic.reject` and a crashed channel
increment the delivery count, but `basic.nack` does not. A nack-based retry could therefore loop
forever; with reject, the quorum queue's delivery limit (default 20) stops it and dead-letters the
message. Returned messages are redelivered "in the exact order they were returned", so session order
holds.

**Difference from Service Bus:** the backoff and retries hold up the rest of the partition, while
Service Bus holds only the session. The bound is roughly the delivery limit times the backoff. The
POC measures whether that is acceptable or whether a transient failure should instead park the
session and schedule a retry (§4.6).

### 4.8 Failover and lock loss

Service Bus fails settlement and session-state writes after a lost session lock (`SessionLockLost`
→ `TransientException`, `MessageContext.cs:281`). With single active consumer, a consumer that
lost its connection may still be running a handler when its successor starts.

- **Broker acks** from the old channel fail once the channel is closed.
- **Transport SQL writes are fenced.** AMQP 0-9-1 doesn't tell a consumer when it becomes active,
  so a consumer takes the lease on its first delivery after subscribing:
  `UPDATE PartitionLease SET Epoch = Epoch + 1, Owner = @me`. Every `SessionState`, `ParkedMessage`
  and `ScheduledMessage` write from that consumer then checks that the epoch is still its own. A
  stale write fails with `TransientException`.

**What this does not fence:** a Resolver response the old handler already published, inbox or outbox
rows written by handler code, and side effects in external systems. Service Bus doesn't fence those
either. The inbox and destination idempotency remain the answer.

### 4.9 Request/reply

`PublisherClient.Request` waits for one reply on a Service Bus reply session. On RabbitMQ each
client instance declares an exclusive, auto-delete reply queue, sends `ReplyTo = <queue>` with a
correlation id, and matches replies by correlation id. Replies don't need session order.
`IReplyDispatcher` publishes straight to the `ReplyTo` queue through the default exchange.

### 4.10 Management, hosting and tools

- **Choosing the transport.** `NimBus.SDK` stops referencing `NimBus.ServiceBus` directly. Each
  transport registers itself (`AddNimBusServiceBusTransport()` / `AddNimBusRabbitMqTransport()`).
  The receiver host, publisher, subscriber, reply receiver, handoff client and outbox dispatcher
  depend on transport-neutral types. Members typed on Service Bus (§2.1) keep working through
  `[Obsolete]` bridges until the next major (`docs/versioning.md`).
- **ManagerClient** sends through `ISender`.
- **WebApp startup** registers transport services by configuration, the same way it chooses the
  store today. Each feature in §2.5 gets an on-prem answer (§10, phase 1 inventory). The
  first RabbitMQ release keeps the transport-neutral features (message store, resubmit, skip,
  handoff settlement, store metrics) and hides the Service Bus–specific ones. A later release
  rebuilds them on the RabbitMQ management HTTP API and the parking table.
- **Resolver** runs in `NimBus.Resolver.Host` with the transport's receiver.
- **CLI.** `nb topology apply --transport rabbitmq` declares the exchange, queues and bindings and
  writes the routing table. RabbitMQ rejects re-declaring a queue with different arguments, so
  changed arguments need an explicit migration.
- **Observability.** W3C trace context in message headers; OTLP export instead of Azure Monitor.
- **Identity.** Local Identity (`NimBus.Extensions.Identity`) instead of Entra.
- **Client.** `RabbitMQ.Client` 7.x over AMQP 0-9-1. The Service Bus SDK can't talk to RabbitMQ's
  AMQP 1.0 support: it depends on Service Bus–specific management links and session filters.

## 5. Parity with Service Bus

| Behaviour | Service Bus | RabbitMQ + SQL |
|---|---|---|
| Per-session order | Session lock | Partition with single active consumer |
| Failed session isolated from others | Yes (park) | Yes (park) |
| Concurrency unit | Session | Partition |
| Slow handler delays others | No | Same partition only |
| Transient retry delays others | No | Same partition only, bounded (§4.7) |
| Transient retry bounded | Delivery limit | Delivery limit, using `basic.reject` |
| Transport state writes after lock loss rejected | Session state, settlement | Session state, parking, scheduling, settlement (§4.8) |
| Handler side effects after lock loss | Not fenced | Not fenced |
| Parking | 3 separate writes | 1 SQL insert, count derived |
| New message overtakes parked ones after unblock (§2.6) | Possible | No |
| Replay identity | New `MessageId` | Original `MessageId` |
| Fan-out `EventId` | Random per copy | Deterministic per copy |
| Scheduled send token | Sequence number | SQL identity |
| Delivery guarantee | At least once | At least once |
| Dead-letter browsing in WebApp | Yes | Later release |

## 6. Deployment profile

- RabbitMQ 4.3 or later (pinned in phase 2), three nodes, quorum queues replicated across them. A
  majority must be up, so one node can be lost.
- SQL Server for tracking, inbox, outbox, routing, session state, parking and scheduling. Its HA and
  backup policy now covers in-flight work: parked and scheduled messages exist only in SQL.
- Endpoints, Resolver and WebApp as .NET worker/ASP.NET Core services or containers. Aspire composes
  local development with a `RabbitMQ` resource next to the existing SQL Server one.
- No Azure dependency at runtime, once §2.5 is worked through.

## 7. Risks and open questions

1. **Head-of-line latency** inside a partition, from slow handlers and transient retries (§4.3,
   §4.7). Needs a throughput and latency target from the customers.
2. **Partition count** is fixed between maintenance windows (§4.2).
3. **The sender needs the routing table.** A stale cache misroutes until refreshed; the version check
   in §4.2 limits this to N changes, but a new subscription takes effect only after the refresh.
4. **Inbox off means at-least-once handlers.** That's no worse than Service Bus, but the on-prem
   profile should recommend the inbox.
5. **Requeue order after a dropped connection.** The RabbitMQ docs describe returned-message order
   for nack, reject and timeouts, not explicitly for a dropped connection. Phase 2 tests it on the
   pinned version.
6. **Migration from Azure.** First release: new installations only, or an offline cutover after
   draining sessions and the `Deferred` subscriptions. No live bridge.
7. **WebApp parity** decides when the RabbitMQ transport is supportable, more than the message flow.

## 8. Out of scope

- A generic transport framework or other brokers. Apache ActiveMQ Artemis (message groups) was
  considered in the September study and is not pursued.
- Running the NimBus Service Bus emulator in production. It keeps messages, locks and session state
  in memory, binds to loopback only and doesn't validate SAS signatures.
- PostgreSQL or another store. SQL Server only.
- Changing Service Bus behaviour, except fixing §2.6 as its own change.

## 9. Acceptance tests for phase 2

- **Interleaved order.** Sessions A and B share a partition. A fails and parks; B completes; retry
  or skip of A replays its parked messages in order, while new A messages arrive during the replay
  and land behind them.
- **Replay batching.** A backlog larger than K drains across several triggers, with B traffic in
  between, without reordering A.
- **Crash points.** Kill the consumer before and after each SQL write and before and after the ack,
  in normal handling, parking, replay and scheduled dispatch. Nothing is lost, nothing is parked
  twice, and every duplicate carries the original `MessageId`.
- **Failover overlap.** Pause the active consumer past its connection loss, let the successor take
  over, resume the old one: its transport SQL writes fail.
- **Broker failover.** Kill a quorum queue leader and the active consumer; order holds.
- **Transient retry.** A handler that always throws `TransientException` is dead-lettered at the
  delivery limit; other sessions in the partition resume.
- **Routing.** Fan-out to several consumers with per-copy `EventId`s; retry and continuation
  stamping; an unroutable publish fails.
- **Scheduling.** Schedule, cancel, and race a cancel against a due dispatch.
- **Request/reply** across two instances.
- **Capacity.** Throughput and p95 latency against agreed targets for a chosen N.

## 10. Delivery

| Phase | Work | Exit |
|---|---|---|
| 0 | ADR amending ADR-001, `CONTRIBUTING.md` and `AGENTS.md` for one on-prem transport; GitHub issue per the contribution process; customer throughput and latency targets | ADR accepted, targets agreed |
| 1 | Inventory every Service Bus- or Cosmos-typed service the SDK, Resolver host and WebApp resolve at startup, with an on-prem answer each (§2.5). Transport-neutral SDK surface with `[Obsolete]` bridges. `ManagerClient` over `ISender`. WebApp transport selection. Regression test and fix for §2.6 on Service Bus, if accepted separately | Release build and existing tests green; the WebApp and Resolver start with no Service Bus configuration (features hidden) |
| 2 | `NimBus.Transport.RabbitMq`: sender with fan-out, partitioned receiver, parking and in-partition replay, scheduler, lease fencing, reply queues, topology provisioning | §9 passes |
| 3 | CrmErpDemo on RabbitMQ and SQL; Aspire resource; OTLP; local Identity | CrmErpDemo end-to-end tests green on RabbitMQ |
| 4 | WebApp admin on the RabbitMQ management API; install and upgrade runbook; HA and backup tests | Supportable release |

No effort estimate yet. Revision 1's guess came before the replay protocol, routing and SDK seam
were designed. Estimate after phase 0, from the phase 1 inventory and a phase 2 work breakdown.

## 11. Review record

Codex reviewed revision 1 on 2026-10-21 (verdict: rethink; full text in
[`docs/plan/2026-10-21-spec-041-review.md`](../../plan/2026-10-21-spec-041-review.md)). All 14
findings were checked against the code and accepted:

| Finding | Change |
|---|---|
| Code 1: replay trigger has its own subscription and hosted service | §2.4; replay moved into the partition consumer (§4.5) |
| Code 2: §2.5 gap real; `DeferredCount` never decremented | §2.6; count derived from rows, open-session rule (§4.5) |
| Code 3: `Abandon` is a no-op on Service Bus | §2.3, §4.7 |
| Code 4: failure completion and retry are conditional | §2.2 qualified |
| Design 1: forwarding re-stamps `From`, `EventId`, `To` | Sender fan-out from a routing table (§4.1) |
| Design 2: no transaction boundary or completion protocol for the guard | §4.5 mapping table and drain steps |
| Design 3: `basic.nack` doesn't count toward the delivery limit from 4.3 | `basic.reject` after a backoff (§4.7) |
| Design 4: confirm-then-delete duplicates; replays get new ids | Original `MessageId` on replay and schedule; `mandatory` returns (§4.1, §4.5, §4.6) |
| Design 5: epoch fences only part of the work | Acquisition defined; claim narrowed (§4.8, §5) |
| Design 6: headers exchange ignores routing key; per-destination N; pending sources | Direct exchange; per-destination partition at publish time; partitions never stored (§4.1, §4.2) |
| Design 7: session-less ingress rule | §4.2 |
| Completeness 1: SDK public surface | §2.1, §4.9, §4.10 |
| Completeness 2: WebApp and Resolver startup inventory | §2.5; phase 1 inventory and exit (§10) |
| Completeness 3: estimate before design | Estimate withdrawn (§10) |

## 12. Sources

Checked 2026-10-21.

- RabbitMQ, [Quorum queues](https://www.rabbitmq.com/docs/quorum-queues): single active consumer
  support; returned-message order; delivery limit default; from 4.3, `basic.reject` and channel
  crashes increment the delivery count and `basic.nack` does not; at-least-once dead-lettering
  requirements.
- RabbitMQ, [Consumers](https://www.rabbitmq.com/docs/consumers): single active consumer failover.
- RabbitMQ, [Exchanges](https://www.rabbitmq.com/docs/exchanges) and
  [AMQP concepts](https://www.rabbitmq.com/tutorials/amqp-concepts): bindings route without
  transforming; headers exchanges ignore the routing key.
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
