# Spec 041 — On-prem NimBus: RabbitMQ transport with SQL Server storage

Status: investigation, not implemented. No code, broker or benchmark exists yet.
Baseline: master `3f6e9cff`. Supersedes the transport design in the September study
(`docs/spec/on-prem-nimbus-feasibility.html`, added in `b29ff5bb`, removed in `71f2220a`; its
plan is still at [`docs/plan/2026-09-14-on-prem-feasibility.md`](../../plan/2026-09-14-on-prem-feasibility.md)).
Scope: a second transport for customers who can't or won't use Azure, with SQL Server as the only
store. Azure Service Bus stays the default and keeps every feature it has today.

## 1. Why

Some customers want NimBus without Azure: on their own servers, with no cloud connectivity. The
storage side is already covered. The SQL Server message store, inbox and outbox exist, and the
WebApp selects the store from configuration (`Startup.Platform.cs:148-173`). The transport is the
gap: Azure Service Bus is the only one.

[ADR-001](../../adr/001-session-based-ordering.md), `CONTRIBUTING.md` ("Don't abstract the
transport") and `AGENTS.md` rule out a transport abstraction. Those rules were written without a
concrete use case. This one is concrete, so a new ADR (§9, phase 0) should allow exactly one
second transport for on-prem installations. It does not open the door to a general transport
framework.

## 2. What the code shows

### 2.1 Core already has the seam

`NimBus.Core` does not reference `Azure.Messaging.ServiceBus`. A transport implements four Core
contracts:

| Contract | Service Bus implementation | What it covers |
|---|---|---|
| `IMessageContext` (`Core/Messages/IMessageContext.cs`) | `ServiceBus/MessageContext.cs` (674 lines) | settlement, session block/unblock, deferral counters, scheduled redelivery |
| `ISender` | `ServiceBus/Sender.cs` | send, schedule, cancel a scheduled send (`long` token) |
| `IDeferredMessageProcessor` | `ServiceBus/DeferredMessageProcessor.cs` | replay a session's parked messages |
| `IReplyDispatcher` | `ServiceBus/ReplyDispatcher.cs` | request/reply |

`NimBus.Testing` implements the same contracts in memory (`InMemoryMessageBus`,
`InMemoryMessageContext`, about 300 lines), so the seam is already used by a second
implementation.

### 2.2 Failure isolation is NimBus's, not the broker's

`StrictMessageHandler.HandleEventRequest` (`Core/Messages/StrictMessageHandler.cs:71-162`):

- **Handler fails** (`catch (EventContextHandlerException)`, line 153): send an error response to
  the Resolver, mark the session blocked in session state, **complete the message**, and schedule a
  retry through the retry policy (`CheckForRetry`).
- **Session already blocked** (line 108 → `catch (SessionBlockedException)`, line 146): send a
  deferral response, then park the message: take the next deferral number, send a copy addressed
  `To = "Deferred"` through `ISender`, increment the deferred count, and **complete the original**
  (`DeferMessageToSubscription`, line 506).
- **Retry or resubmission succeeds** (lines 184-207, 258-293): unblock the session, then send a
  `ProcessDeferredRequest` if the deferred count is above zero (`ContinueWithAnyDeferredMessages`,
  line 637).

The main queue never holds a failed message. A blocked session costs one session-state read per
arriving message and one park. This is the central finding: **the broker only has to deliver each
session in order to one consumer at a time.** Isolating a failed session from the others is
already done above the broker.

The exception is a transient failure. `MessageHandler` catches `TransientException` and calls
`Abandon` (`Core/Messages/MessageHandler.cs:70-82`). Service Bus then redelivers the same message
under the same session lock, up to the delivery limit.

### 2.3 Service Bus features the transport relies on

| Feature | Where | Notes |
|---|---|---|
| Session receivers: ordered, exclusive per session, `MaxConcurrentSessions` in parallel | `SDK/Hosting/NimBusReceiverHostedService.cs:198-215`, Resolver host | The ordering primitive |
| Session state | `ServiceBus/SessionState.cs`, `ServiceBusSession.cs:95-145` | 4 live fields: `BlockedByEventId`, `BlockedByMessageId`, `DeferredCount`, `NextDeferralSequence` |
| `Deferred` subscription, drained by session | `ServiceBus/DeferredMessageProcessor.cs:63` | `AcceptSessionAsync(topic, "Deferred", sessionId)`, sort by deferral number, republish to the main topic, complete |
| Scheduled send and cancel | `ServiceBus/Sender.cs:30-33` | Token is the Service Bus sequence number |
| Dead-letter queue | `ServiceBus/ServiceBusSession.cs:80-88`, WebApp `ResolverDeadLetterClient` | |
| Subscriptions with SQL filters and rule actions, auto-forward | `ServiceBus/Provisioning/TopologyDescriptor.cs` | Filters are `user.To = '<endpoint>'`, plus `EventTypeId` forwarders that rewrite `To` |

### 2.4 Coupling outside the transport project

| Area | Coupling | Size |
|---|---|---|
| `NimBus.SDK` | References `NimBus.ServiceBus`. Receiver host, publisher, subscriber, handoff client and outbox dispatcher use the Service Bus SDK | 8 files |
| `NimBus.Manager` | `ManagerClient` takes a `ServiceBusClient`, although resubmit and skip are ordinary messages (`ResubmissionRequest`, `SkipRequest`) | 1 file |
| `NimBus.Resolver` / `.Host` | Functions entry point uses a Service Bus session trigger. The worker host uses the SDK receiver | 4 files |
| `NimBus.WebApp` | Registers Service Bus clients. Purge, subscription admin, topology, dead-letter client, deferred-message inspector, heartbeat sender and simulation use the Service Bus SDK | 13 files, ≈2,800 lines in `Services/AdminService*.cs`, `SubscriptionAdminService.cs`, `ResolverDeadLetterClient.cs`, `DeferredMessageInspector.cs` |
| `NimBus.Management.ServiceBus` | Endpoint management over the administration client | 494 lines |
| `NimBus.CommandLine` | `nb topology apply` and friends | 4 files |
| Other Azure dependencies | `ServiceDefaults`: Azure Monitor exporter. WebApp: `Microsoft.Identity.Web` (Entra). Resolver: Functions worker | Already optional or replaceable: OTLP export, local Identity (`NimBus.Extensions.Identity`), worker host |

### 2.5 Existing ordering gap (both transports)

The session guard (`StrictMessageHandler.cs:99-109`) checks only `BlockedByEventId`. After an
unblock, the session accepts new messages straight away, while its parked messages are still
waiting for the `ProcessDeferredRequest` replay. A message that arrives in that window is handled
before older parked messages. Nothing reads `DeferredCount` or `HasDeferredMessages` in the guard.
Replays are republished to the main topic, so they also queue behind anything already there.

This is today's Service Bus behaviour, not something RabbitMQ introduces. The on-prem transport
should not make it worse, and it is cheap to close in the SQL design (§4.4): park any non-replay
message while the session's deferred count is above zero. Closing it on Service Bus is a separate
fix and needs its own regression test.

## 3. Decision

Build a RabbitMQ transport that matches what Service Bus gives NimBus today, and no more:

- **RabbitMQ** delivers each session in order to one consumer at a time, using fixed partition
  queues with a single active consumer.
- **SQL Server** holds what Service Bus holds besides messages: session state, parked messages and
  scheduled sends.
- **Core is unchanged.** The new transport implements the four contracts in §2.1.

The September study recommended a SQL "session runtime": acknowledge on intake, SQL owns every
pending message, and workers claim a fenced session head. It rejected partitioned queues because
"one failed entity blocks its partition if strict completion order is retained". §2.2 shows NimBus
does not retain the failed message, so that objection doesn't apply. The runtime design also aims
for guarantees Service Bus does not provide (fencing of remote side effects after lock loss). It
remains the fallback if the phase 2 tests in §9 fail.

## 4. Design

### 4.1 Topology

Per endpoint:

- One **headers exchange** `nimbus.<endpoint>`, the counterpart of the endpoint's topic.
- **N partition queues** `nimbus.<endpoint>.p<i>`: quorum queues declared with
  `x-single-active-consumer: true`, a dead-letter exchange, `dead-letter-strategy: at-least-once`
  and `overflow: reject-publish`. Quorum at-least-once dead-lettering requires `reject-publish`;
  the defaults are at-most-once.
- A **dead-letter queue** `nimbus.<endpoint>.dlq`.
- Bindings replace subscriptions. `To = <endpoint>` targets the endpoint. `EventTypeId` bindings
  between exchanges replace the forwarders. RabbitMQ cannot rewrite headers the way the forwarder
  rule actions do, so the receiver takes `To` from the queue it consumed. A topology descriptor for
  RabbitMQ is generated from the same endpoint and event catalog as `TopologyDescriptor`. Any filter
  it cannot express is rejected when provisioning, never silently dropped.

`Deferred` and `DeferredProcessor` are not broker entities on RabbitMQ (§4.4, §4.5).

### 4.2 Partitioning

`partition = stableHash(SessionId) % N`, computed by the sender. It becomes the routing key or a
header that the partition bindings match.

- The hash is a fixed, versioned algorithm (for example xxHash64 of the UTF-8 session id), never
  `string.GetHashCode`.
- N is recorded per endpoint in the topology. Changing it means stopping publishers, draining the
  partitions, and restarting with the new N.
- **Not** the consistent-hash exchange. Its ring is in memory and rebuilt at boot, so after a restart
  "a given routing key now may route to a different queue", which breaks per-session order.
- Messages without a session id (NimBus assigns a random session when the key is null) spread
  evenly across partitions.

### 4.3 Receiving

The receiver host consumes every partition queue of its endpoint. Single active consumer makes one
instance active per partition, with automatic failover. Within a partition, messages are handled
one at a time (prefetch 1 to start; the POC measures whether a larger prefetch with in-order acks
is safe). The broker acknowledgement is `Complete`. Endpoint parallelism is the number of
partitions the instance is active on, instead of `MaxConcurrentSessions`.

**Behavioural difference:** a slow handler delays the other sessions in its partition. It never
blocks them, because failures park (§2.2). Choosing N trades parallelism against queue count.

### 4.4 Session state and parking in SQL

Two tables in the endpoint's SQL database, in their own schema, next to the existing NimBus
tables:

- `SessionState(Endpoint, SessionId, BlockedByEventId, BlockedByMessageId, DeferredCount,
  NextDeferralSequence, RowVersion)`. Every update is a compare-and-swap on `RowVersion`.
- `ParkedMessage(Endpoint, SessionId, DeferralSequence, MessageId, Body, Headers, ParkedAtUtc)`,
  with the primary key `(Endpoint, SessionId, DeferralSequence)`.

The transport's `ISender` recognises `To = "Deferred"` and writes a `ParkedMessage` row instead of
publishing. Taking the deferral number, inserting the row and incrementing the count happen in one
SQL transaction. On Service Bus these are three separate writes.

The guard gap in §2.5 closes here: while `DeferredCount > 0`, park any message that is not a
replay. This is a transport-side check, so Core and Service Bus are unchanged.

### 4.5 Replaying parked messages

`IDeferredMessageProcessor` for RabbitMQ reads `ParkedMessage` rows for the session, ordered by
`DeferralSequence`, and republishes each one to the session's partition. It deletes the row after
the publish is confirmed. It keeps the existing "parked again" stop condition
(`DeferredMessageProcessor.cs`, `IsReplayParkedAgain`). `ProcessDeferredRequest` stays the
trigger: it is a message to the endpoint, handled by the same hosted service.

### 4.6 Scheduling

There is no native scheduled delivery. A `ScheduledMessage` table holds the message and its due
time, and a poller publishes due rows. The row's `bigint` identity is the `long` token that
`ISender.ScheduleMessage` returns, so `CancelScheduledMessage` is a delete. The SQL outbox
dispatcher is the model to follow.

**Not** the delayed-message-exchange plugin. Its delayed messages have a single replica per node,
and it is not meant for large numbers of delayed messages.

`ScheduleRedelivery` (throttling) and retry-policy delays use the same table.

### 4.7 Transient retries

`Abandon` becomes `basic.nack` with requeue. Quorum queues return messages "in the exact order they
were returned", and the delivery limit (default 20 since RabbitMQ 4.0; from 4.3 it counts failed
deliveries only) dead-letters a poison message. Unlike Service Bus, retrying one session holds up
the rest of its partition until the retry succeeds or the limit is reached. If that is
unacceptable, `Abandon` can instead schedule a redelivery (§4.6) and complete. That keeps the
session's order only if the session is blocked meanwhile; the POC decides.

### 4.8 Failover and lock loss

Service Bus fails settlement and session-state writes after a lost session lock (the
`SessionLockLost` → `TransientException` mapping in `MessageContext.cs:281`). With single active
consumer, a consumer whose connection dropped may still be running a handler while its successor
starts. To match Service Bus:

- Broker acks from the old channel already fail once the channel is closed.
- SQL writes need the same: each consumer takes a lease epoch per partition when it becomes active
  and includes it in every `SessionState` and `ParkedMessage` write. A write with an old epoch fails
  with `TransientException`.

This protects NimBus's own state, as Service Bus does. Like Service Bus, it does not undo a remote
side effect the old handler already made. The inbox and destination idempotency remain the answer
there.

### 4.9 Management, hosting and tools

- **Choosing the transport.** `NimBus.SDK` stops referencing `NimBus.ServiceBus` directly. Each
  transport registers itself (`AddNimBusServiceBusTransport()` / `AddNimBusRabbitMqTransport()`),
  and the receiver host, publisher and subscriber depend only on Core contracts. The public SDK API
  keeps working through `[Obsolete]` bridges that call the Service Bus registration
  (`docs/versioning.md`).
- **ManagerClient** sends through `ISender`.
- **WebApp.** Features split into transport-neutral ones (message store, resubmit, skip, metrics
  from the store) and transport-specific ones (purge, subscription admin, topology, dead-letter
  browsing, deferred inspection, queue metrics). The first RabbitMQ release hides the specific ones
  behind a capability flag. A later release reimplements them on the RabbitMQ management HTTP API
  and the SQL parking table.
- **Resolver.** Runs in `NimBus.Resolver.Host` (worker), not the Functions entry point.
- **CLI.** `nb topology apply --transport rabbitmq` declares exchanges, queues and bindings.
  RabbitMQ rejects re-declaring a queue with different arguments, so changed arguments need an
  explicit migration, not an in-place update.
- **Observability.** W3C trace context in message headers, OTLP export instead of Azure Monitor.
- **Identity.** Local Identity (`NimBus.Extensions.Identity`) instead of Entra.
- **Client.** `RabbitMQ.Client` 7.x over AMQP 0-9-1. The Service Bus SDK cannot talk to RabbitMQ's
  AMQP 1.0 support: it depends on Service Bus–specific management links and session filters.

## 5. Parity with Service Bus

| Behaviour | Service Bus | RabbitMQ + SQL |
|---|---|---|
| Per-session order | Session lock | Partition with single active consumer |
| Failed session isolated from others | Yes (park) | Yes (park) |
| Concurrency unit | Session | Partition |
| Slow handler delays others | No | Same partition only |
| Transient retry delays others | No | Same partition only (§4.7) |
| State writes after lock loss rejected | Yes | Yes, with lease epoch (§4.8) |
| Parking is atomic | No (3 writes) | Yes (1 SQL transaction) |
| Overtaking parked messages after unblock (§2.5) | Possible | Closed by the transport guard |
| Scheduled send token | Sequence number | SQL identity |
| Dead-letter browsing in WebApp | Yes | Later release |

## 6. Deployment profile

- RabbitMQ 4.x, three nodes, quorum queues replicated across them. A majority must be up, so one
  node can be lost.
- SQL Server for tracking, inbox, outbox, session state, parking and scheduling. Its HA and backup
  policy now covers in-flight work: parked and scheduled messages exist only in SQL.
- Endpoints, Resolver and WebApp as .NET worker/ASP.NET Core services or containers. Aspire composes
  local development, adding a `RabbitMQ` resource next to the existing SQL Server one.
- No Azure dependency at runtime.

## 7. Risks and open questions

1. **Partition count is fixed** (§4.2). How many partitions per endpoint, and does it need to vary
   by endpoint?
2. **Head-of-line latency** inside a partition (§4.3, §4.7). Needs a throughput target from the
   customers.
3. **Cross-store atomicity.** A broker ack and a SQL commit are not one transaction. Every write in
   §4.4-4.6 must be idempotent so a redelivery after a crash between them repeats safely: parking
   keyed by `MessageId`, replays deleting the row only after a publisher confirm.
4. **Order of the requeue after failover.** The RabbitMQ docs describe returned-message order for
   nack, reject and timeout. They don't spell it out for a dropped connection. Phase 2 must test it
   against the pinned server version.
5. **Migration from Azure.** First release: new installations only, or an offline cutover after
   draining sessions and the `Deferred` subscriptions. No live bridge.
6. **WebApp parity** decides when the RabbitMQ transport is supportable, not the message flow.

## 8. Out of scope

- A generic transport framework or other brokers. Apache ActiveMQ Artemis (message groups) was
  considered in the September study and is not pursued.
- Running the NimBus Service Bus emulator in production. It keeps messages, locks and session state
  in memory, binds to loopback only and doesn't validate SAS signatures.
- PostgreSQL or another store. SQL Server only.
- Changing Service Bus behaviour, except fixing §2.5 as its own change.

## 9. Delivery

| Phase | Work | Exit |
|---|---|---|
| 0 | ADR amending ADR-001, `CONTRIBUTING.md` and `AGENTS.md` for one on-prem transport | ADR accepted |
| 1 | Seams with no behaviour change on Azure: SDK transport registration, `ManagerClient` over `ISender`, WebApp capability flags. A transport conformance suite in `NimBus.Testing`, run against in-memory and the Service Bus emulator | Release build and existing tests green; suite passes on both |
| 2 | POC: `NimBus.Transport.RabbitMq` and the SQL session, parking and scheduling store; CrmErpDemo on RabbitMQ and SQL | The tests below pass |
| 3 | Resolver host, `nb topology apply`, WebApp capability-gated, OTLP, local Identity, Aspire resource | CrmErpDemo end-to-end tests green on RabbitMQ |
| 4 | WebApp admin on the RabbitMQ management API; install and upgrade runbook; HA and backup tests | Supportable release |

Phase 2 must pass:

- **Interleaved order.** Sessions A and B share a partition. A fails and parks; B completes; retry or
  skip of A replays its parked messages in order, including ones arriving during the replay.
- **Crash points.** Kill the consumer before and after the SQL commit and before and after the ack.
  Nothing is lost, and a redelivery doesn't park or replay twice.
- **Failover overlap.** Pause the active consumer past its connection loss, let the successor take
  over, resume the old one: its SQL writes fail.
- **Broker failover.** Kill a quorum queue leader and the active consumer; order holds.
- **Scheduling.** Schedule, cancel, and race a cancel against a due dispatch.
- **Capacity.** Throughput and p95 latency against agreed targets, for a chosen N.

Rough size for one developer: 2–3 weeks to the phase 2 exit, 2–3 months to phase 4. Most of that
is phases 3-4, not the message flow.

## 10. Sources

Checked 2026-10-21.

- RabbitMQ, [Quorum queues](https://www.rabbitmq.com/docs/quorum-queues): single active consumer
  support, returned-message order, delivery limit default and its 4.3 change, at-least-once
  dead-lettering requirements.
- RabbitMQ, [Consumers](https://www.rabbitmq.com/docs/consumers): single active consumer
  failover; not available to AMQP 0-9-1 clients on streams.
- RabbitMQ,
  [consistent hash exchange](https://github.com/rabbitmq/rabbitmq-server/tree/main/deps/rabbitmq_consistent_hash_exchange):
  ring rebuilt at boot; routing can change after a restart.
- Tencent Cloud TDMQ,
  [delayed message limitations](https://intl.cloud.tencent.com/document/product/1112/60154), and the
  plugin's [README mirror](https://github.com/Sergi0Limit/rabbitmq-delayed-message-exchange):
  single replica per node, not for large volumes. Secondary sources; the upstream repository's
  current status was not confirmed.
- `src/NimBus.ServiceBusEmulator/README.md`: emulator storage and limits.
