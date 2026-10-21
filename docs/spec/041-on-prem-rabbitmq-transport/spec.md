# Spec 041 — On-prem NimBus: RabbitMQ transport with SQL Server storage

Status: **draft, revision 7** (2026-10-21). Nothing is implemented. Decision record:
[ADR-018](../../adr/018-on-prem-rabbitmq-transport.md) (proposed), which amends ADR-001 when
accepted. This draft turns an investigation into a specification. Codex reviewed the investigation
four times and this draft once ([2026-10-21-spec-041-review.md](../../plan/2026-10-21-spec-041-review.md));
§17 lists what changed.
Baseline: master `2aaf3ce6`.
Scope: a new `NimBus.Transport.RabbitMq` project and package; a transport seam in `NimBus.Core` and
`NimBus.SDK`; transport selection in `NimBus.Resolver.Host` and `NimBus.WebApp`; a router service;
`nb topology apply --transport rabbitmq`; three Core bug fixes; the CI split; Aspire and docs.
Unchanged: Core's handler logic (`StrictMessageHandler`, `ResponseService`, the pipeline), the four
Core transport contracts, the storage contracts and providers, and everything Service Bus
installations do today. `api-spec.yaml` gains two additive fields in phase 3 and admin endpoints in
phase 4 (§6.16).
Why: some customers can't or won't run on Azure. Storage is already pluggable (ADR-010), so the
transport is the only gap.

## 1. Summary

NimBus gets one additional transport, RabbitMQ, for on-premises installations, always with SQL
Server for storage and transport state. The design ports the Service Bus design one feature at a
time instead of improving on it:

| Service Bus | On-premises replacement |
|---|---|
| Topic + subscription rules + auto-forward | Topic exchange + a **router** that evaluates the same rules and publishes stamped copies (§6.5) |
| Session-ordered subscription | Fixed partition queues per endpoint, single active consumer each (§6.6, §6.7) |
| Session lock | SQL partition lease with expiry and renewal (§6.8) |
| Session state | SQL `SessionState` table, same four fields, same calls (§6.9) |
| `Deferred` subscription | SQL `ParkedMessage` table (§6.10) |
| `DeferredProcessor` subscription + hosted service | Trigger queue + a RabbitMQ variant of the same hosted service (§6.10) |
| Scheduled messages | SQL `ScheduledMessage` table + dispatcher in the router (§6.11) |
| `Abandon` (wait for the session lock) | Backoff, then `basic.reject` (§6.12) |

The investigation's key finding makes this tractable: failure isolation lives in Core, not in the
broker (§2.2). The broker only has to deliver each session in order to one consumer at a time.

Before any on-prem release, three existing Core bugs that affect Service Bus installations today
get fixed (§2.7). The CI workflow splits into lanes so the RabbitMQ suite doesn't lengthen every
pull request, and the WebApp frontend tests, which CI doesn't run today, get a lane (§8).

## 2. Current state

### 2.1 Core has most of the seam

`NimBus.Core` does not reference `Azure.Messaging.ServiceBus`. A transport implements four Core
contracts:

| Contract | Service Bus implementation | What it covers |
|---|---|---|
| `IMessageContext` (`Core/Messages/IMessageContext.cs`) | `ServiceBus/MessageContext.cs` | settlement, session block/unblock, deferral counters, scheduled redelivery |
| `ISender` | `ServiceBus/Sender.cs` | send, schedule, cancel a scheduled send (`long` token) |
| `IDeferredMessageProcessor` | `ServiceBus/DeferredMessageProcessor.cs` | replay a session's parked messages |
| `IReplyDispatcher` | `ServiceBus/ReplyDispatcher.cs` | request/reply |

`NimBus.Testing` implements three of them in memory (`InMemoryMessageBus` for `ISender`,
`InMemoryMessageContext`, `InMemoryReplyDispatcher`); it has no in-memory
`IDeferredMessageProcessor`, which phase 1h adds for the shared suite (§9). The Core outbox
dispatcher publishes through `ISender` (`Core/Outbox/OutboxDispatcher.cs`), so it follows whichever
transport is registered.

The **SDK and hosts are not transport-neutral.** They build Service Bus objects directly:

- `SDK/Extensions/ServiceCollectionExtensions.cs`: the publisher's sender and reply client
  (lines 101-118), the reply dispatcher (245), the deferred message processor (273), the
  subscriber's response sender and `ServiceBusAdapter` (277-343), the receiver hosted service
  (393-398), the outbox dispatcher's sender (468-478), and the handoff clients (581-619).
- `ISubscriberClient` inherits the Service Bus–specific `IServiceBusAdapter`
  (`SDK/ISubscriberClient.cs:8`); `SubscriberClient.CreateAsync` builds a sender and adapter (43-64).
- `PublisherClient.Request` sends, then receives the reply with a `ServiceBusSessionReceiver`
  (`SDK/PublisherClient.cs:265-279`).
- `OutboxDispatcherSender` inherits the Service Bus `Sender` (`SDK/Hosting/OutboxDispatcherSender.cs:10`).
- `NimBusReceiverHostedService` and `DeferredMessageProcessorHostedService` are built on
  `ServiceBusSessionProcessor` and `ServiceBusProcessor`.
- `HandoffClientFactory` takes a `ServiceBusClient` (`SDK/HandoffClientFactory.cs:51`).
- The Resolver's `ServiceExtensions` and `ResolverBuilderExtensions` create the `ServiceBusClient`
  and adapter; `Resolver.Host/Program.cs:44` constructs the receiver from them.

### 2.2 Failure isolation is NimBus's, not the broker's

`StrictMessageHandler.HandleEventRequest` (`Core/Messages/StrictMessageHandler.cs:71-162`), on
its normal path:

- **Handler fails** (`catch (EventContextHandlerException)`, line 153): send an error response to
  the Resolver, mark the session blocked, **complete the message**, then `CheckForRetry`
  (line 647), which schedules a retry when a retry policy applies.
- **Session already blocked** (line 108 → `catch (SessionBlockedException)`, line 146): send a
  deferral response, then park (`DeferMessageToSubscription`, line 506): take the next deferral
  number, send a copy addressed `To = "Deferred"` through `ISender`, increment the deferred count,
  and **complete the original**. The parked copy (`ResponseService.SendToDeferredSubscription`,
  line 313) is a new `Message` with no `MessageId`; it carries the source id as `ParentMessageId`.
- **Retry or resubmission succeeds** (lines 184-207, 258-293): unblock the session, then send a
  `ProcessDeferredRequest` if the deferred count is above zero (`ContinueWithAnyDeferredMessages`,
  line 637).

On the normal path the queue never holds a failed message. If sending a response, writing session
state or completing fails, the delivery stays unsettled and is redelivered.

### 2.3 Transient failures

`MessageHandler` catches `TransientException` and calls `Abandon`
(`Core/Messages/MessageHandler.cs:70-82`). On Service Bus `Abandon` does nothing
(`ServiceBus/MessageContext.cs:250-257`): the message stays unsettled and returns when the session
lock is released or expires. Other sessions are unaffected.

### 2.4 Service Bus features the transport relies on

| Feature | Where | Notes |
|---|---|---|
| Session receivers, `MaxConcurrentSessions` in parallel | SDK receiver host, Resolver host | The ordering primitive; sessions spread across instances automatically |
| Session lock: expiry, renewal, `SessionLockLost` | `MessageContext.cs:281` | Fences a consumer that lost the session |
| Session state | `ServiceBus/SessionState.cs`, `ServiceBusSession.cs:95-145` | `BlockedByEventId`, `BlockedByMessageId`, `DeferredCount`, `NextDeferralSequence` |
| `Deferred` subscription (sessions on) | `TopologyDescriptor.cs:177-192` | Filter `To = 'Deferred' AND OriginalSessionId IS NOT NULL` |
| `DeferredProcessor` subscription (sessions off) | `TopologyDescriptor.cs:195-206`, `SDK/Hosting/DeferredMessageProcessorHostedService.cs:80` | Receives `ProcessDeferredRequest` (`ResponseService.cs:377`), consumed by its own hosted service (ADR-003), not by `StrictMessageHandler` (line 431) |
| Replay | `ServiceBus/DeferredMessageProcessor.cs:63-130, 190` | Lock the session on `Deferred`, sort by deferral number, republish to the topic with a new `MessageId`, complete; stop when a replay is parked again |
| Scheduled send and cancel | `ServiceBus/Sender.cs:30-33` | Token is the Service Bus sequence number |
| Dead-letter queue | `ServiceBusSession.cs:80-88`, WebApp `ResolverDeadLetterClient` | |
| Rule actions and auto-forward | `TopologyDescriptor.cs:94-115, 123-345` | The subscription matrix in §6.5 |

### 2.5 Who publishes

The broker applies a topic's rules no matter which process published, or whether the endpoint has a
running receiver:

- NimBus publishers: `PublisherClient`, the outbox dispatcher, the Resolver, `ManagerClient`, the
  WebApp's heartbeat, simulation and agent publisher. **Publish-only processes exist**:
  `AspirePubSub.Publisher` and the CRM and ERP APIs call `AddNimBusPublisher` with no receiver.
  `PublisherClient` sets `MessageId` to a content hash (`<eventType>-<hash>`,
  `PublisherClient.cs:431-436`), so two identical publishes share it, and leaves `EventId` for the
  forwarding rule.
- Raw publishers with no NimBus code: PartnerPortal sends a CloudEvent with a random `MessageId` and
  a session id using only the Service Bus SDK (`samples/CrmErpDemo/PartnerPortal/Program.cs:115`).
- Raw consumers read the original publication before any rule runs:
  `CloudEventsInterop.NonNimBusConsumer` on a plain subscription of the producer topic.

### 2.6 Coupling outside the transport project

| Area | Coupling |
|---|---|
| `NimBus.Manager` | `ManagerClient` takes a `ServiceBusClient` and publishes directly (`ManagerClient.cs:66`); resubmit and skip are ordinary messages |
| WebApp startup | `AddServiceBusClients` runs unconditionally (`Startup.cs:31`); the heartbeat sender is `ServiceBusHeartbeatMessageSender` (`Startup.Web.cs:139`); `SubscriptionAdminService` is built from `IServiceBusManagement` and `ServiceBusClient` (`Startup.Web.cs:101-113`) |
| WebApp controllers and services | `EventImplementation` takes a `ServiceBusClient` (`EventImplementation.cs:34-50`) and composes events with `PublisherClient.CreateAsync` (`EventImplementation.OperatorActions.cs:130`); `EndpointImplementation` requires `IServiceBusManagement` (line 56); `AdminService` takes `ServiceBusAdministrationClient` and `ServiceBusClient` (`AdminService.cs:49-58`); the simulation services |
| WebApp admin | Purge, subscription admin, topology audit, dead-letter client, deferred inspector: about 2,800 lines in `Services/AdminService*.cs`, `SubscriptionAdminService.cs`, `ResolverDeadLetterClient.cs`, `DeferredMessageInspector.cs`; `IServiceBusManagement` for endpoint enable/disable |
| WebApp storage | Ten files reference `Microsoft.Azure.Cosmos`, mostly `catch (CosmosException) when NotFound` around store calls (`EndpointImplementation.cs:117`, `EventImplementation.OperatorActions.cs:270`, `HandoffSettlementService.cs:84`). A SQL-only question: does each path behave correctly when the store is SQL Server? (the 1e inventory, §10) |
| `NimBus.Management.ServiceBus`, `NimBus.CommandLine` | Endpoint management and `nb topology apply` over the administration client; every `CommandRunner.Run` overload that touches messaging builds a `ServiceBusClient` or administration client (`CommandRunner.cs:111-156`) |
| Other Azure dependencies | `ServiceDefaults`: Azure Monitor exporter. WebApp: `Microsoft.Identity.Web` (Entra). Resolver: Functions worker. Replaceable with OTLP, local Identity (`NimBus.Extensions.Identity`) and `Resolver.Host` |

### 2.7 Existing Core bugs, both transports

The investigation found three bugs that affect Service Bus installations today. An on-prem
installation would inherit them, so they are fixed first, in Core, with regression tests (phase 1).

1. **Overtaking after unblock.** The session guard (`StrictMessageHandler.cs:99-109`) checks only
   `BlockedByEventId`. After an unblock, a message that arrives before the replay is handled ahead
   of older parked messages, because replays are republished to the back of the topic. No test
   covers the window. `DeferredCount` can't serve as a guard as it stands: it is incremented when
   parking and never decremented or reset in production code.
2. **A duplicate replay can rewrite the outcome.** A replay redelivered after its handler and inbox
   record committed hits the inbox pre-check (`StrictMessageHandler.cs:90`) and sends a skip
   response, which the SQL store applies over the earlier completed state
   (`SqlServerMessageTrackingStore.Writes.cs:169`).
3. **A redelivered skip can strand parked messages.** `HandleSkipRequest`
   (`StrictMessageHandler.cs:326-345`) unblocks the session, then sends the drain trigger. If it
   stops between the two, the redelivered skip fails `VerifySessionIsBlockedByThis`, sends only a
   skip response and completes. Nothing sends the trigger later. Handoff completion has a repair
   branch for the same window; skip does not.

Each fix is a separate change with its own design; this spec only requires them before release.

## 3. Goals

1. Run NimBus with no Azure dependency at runtime: RabbitMQ, SQL Server and .NET services.
2. Match Service Bus behaviour: per-session order, failure isolation, parking and replay,
   scheduling, dead-lettering, fan-out (including raw publishers and consumers), request/reply.
   Record every difference (§7).
3. Keep Core's handler logic and its four transport contracts unchanged.
4. Keep Service Bus installations unchanged: no behaviour change, no breaking API change, no new
   required configuration.
5. Prove parity with one behaviour suite that runs on the in-memory transport, the Service Bus
   emulator and RabbitMQ.
6. Keep CI's critical path no longer than today's while adding the RabbitMQ lane.

## 4. Non-goals

- A generic transport framework, or any broker besides RabbitMQ (ADR-018).
- Improving on Service Bus behaviour inside the transport. Improvements go to Core, for both.
- Exactly-once processing. Both transports are at-least-once; handlers stay idempotent.
- PostgreSQL or another store. SQL Server only.
- A live bridge or migration path from an Azure installation. First release: new installations, or
  an offline cutover after draining sessions and `Deferred` subscriptions.
- WebApp admin parity in the first RabbitMQ release (§6.16 lists what's hidden until phase 4).
- Azure Functions hosting on-prem. Endpoints run as worker services.
- Running the NimBus Service Bus emulator in production.

## 5. Decisions

| # | Decision | Where |
|---|---|---|
| D1 | One repository; RabbitMQ ships as `Akaule.NimBus.Transport.RabbitMq`; on-prem deployment artifacts may live in a separate distribution repository | §6.2 |
| D2 | Port the Service Bus design feature by feature (parity first); Service Bus stays the reference and the default | §1 |
| D3 | A minimal transport seam in Core; the SDK falls back to Service Bus when no transport is registered, so existing applications don't change | §6.3 |
| D4 | A router service evaluates subscription rules; it runs in `Resolver.Host` by default and can run on its own | §6.1, §6.5 |
| D5 | SQL Server holds routing, leases, session state, parked and scheduled messages, in a `nimbus_transport` schema applied with DbUp | §6.14 |
| D6 | Quorum queues with single active consumer, fixed partitions per endpoint, partition computed at publish time and never stored | §6.6 |
| D7 | RabbitMQ 4.3 or later, AMQP 0-9-1, `RabbitMQ.Client` 7.x | §6.18 |
| D8 | The RabbitMQ registration API carries `[Experimental]` until phase 4 | §6.3 |
| D9 | CI splits into lanes behind one aggregate `ci` check; live-infra tests are selected by test category | §8 |
| D10 | The three Core bugs in §2.7 are fixed before any on-prem release | §10 |

## 6. Design

### 6.1 Components

An on-prem installation runs:

| Component | Runs | Needs |
|---|---|---|
| RabbitMQ | Three-node cluster; quorum queues replicated across it | — |
| SQL Server | Message store, inbox, outbox, transport state | HA and backup policy that covers in-flight work: parked and scheduled messages exist only in SQL |
| Endpoint hosts | Worker services: receiver (§6.7) and deferred-processor host (§6.10) | RabbitMQ, SQL Server (transport state) |
| Publish-only applications | `AddNimBusPublisher` only | RabbitMQ; SQL Server for the routing table (partition count and version, §6.6), scheduling and the outbox |
| `Resolver.Host` | Resolver receiver, **router** (§6.5) and scheduled dispatcher (§6.11) by default; two or more replicas | RabbitMQ, SQL Server |
| WebApp | ASP.NET Core, local Identity | RabbitMQ (sends), SQL Server; RabbitMQ management API in phase 4 |

The router is a platform service because topic rules must run whether or not a topic's endpoint
has a running host (§2.5). Hosting it in `Resolver.Host` by default keeps the process count down;
both are platform-critical and both need SQL. A standalone `NimBus.Router.Host` is available when
routing load should scale separately.

### 6.2 Repository and packaging

Same repository (ADR-018). New projects:

| Project | Package | Contents |
|---|---|---|
| `src/NimBus.Transport.RabbitMq` | `Akaule.NimBus.Transport.RabbitMq` | Sender, receiver, lease, session state, parking and drain, request/reply, router, scheduler, topology provisioner, DbUp scripts, health check |
| `src/NimBus.Router.Host` | deployable app | Standalone router and scheduler host (optional) |
| `tests/NimBus.Transport.RabbitMq.Tests` | — | Unit tests and `LiveRabbitMq` tests (§8, §9) |

`NimBus.Transport.RabbitMq` references `NimBus.Core` and `RabbitMQ.Client`, never
`NimBus.ServiceBus` or any Azure package.

**The SDK keeps its reference to `NimBus.ServiceBus` until the next major.** Removing it would
break every application that relies on the Service Bus defaults. On-prem applications therefore
carry the Service Bus assembly without loading a client or connecting anywhere. The next major
moves the Service Bus registration into its own package; §14 asks whether that matters enough to
customers to bring it forward.

**Distribution.** Compose files, an installer, upgrade runbooks and sample configuration for
on-prem installations can live in a separate distribution repository that consumes released
packages and the prebuilt application zips from ADR-015 phase 2. No NimBus code lives there.

### 6.3 Transport seam (phase 1)

Phase 1 adds the seam without changing Service Bus behaviour. Provisional shape; phase 1 settles
names and signatures.

In `NimBus.Core`, namespace `NimBus.Core.Transport`:

- **`INimBusTransport`**: one per process.
  - `ISender CreateSender(string topic)`: publishers, subscriber responses, handoff clients, the
    outbox dispatcher, `ManagerClient`, the WebApp.
  - `ITransportReceiver CreateReceiver(TransportReceiverRegistration registration)`: the endpoint's
    receive loop. The registration carries the endpoint, the `IMessageHandler`, options and the
    circuit breaker.
  - `ITransportReceiver CreateDeferredTriggerReceiver(string endpoint)`: the deferred-processor
    trigger loop (§6.10).
  - `IDeferredMessageProcessor`, `IReplyDispatcher` and a new `IReplyReceiver` (§6.13).
  - `TransportCapabilities Capabilities`: what the WebApp may offer (§6.16).
  - `string MessagingSystem`: the OpenTelemetry `messaging.system` value.
- **`ITransportReceiver`**: `StartAsync`/`StopAsync`. Core stays free of a hosting dependency; the
  SDK wraps receivers in hosted services.

Registration:

- `services.AddNimBusServiceBusTransport()` in `NimBus.ServiceBus`: today's behaviour, using the
  `ServiceBusClient` from dependency injection.
- `services.AddNimBusRabbitMqTransport(options => …)` in `NimBus.Transport.RabbitMq`, marked
  `[Experimental("NIMBUS0041")]` until phase 4. A call to an experimental API is a build error
  unless the caller suppresses its diagnostic, so every caller in this repository (`Resolver.Host`,
  `NimBus.Router.Host`, the WebApp, the samples and tests that select RabbitMQ) adds
  `<NoWarn>$(NoWarn);NIMBUS0041</NoWarn>` to its project. Customers opt in the same way.
- **Fallback:** if no transport is registered, the SDK registers the Service Bus transport. An
  existing application that registers a `ServiceBusClient` and calls `AddNimBusPublisher` or
  `AddNimBusSubscriber` keeps working with no code change and no new warning.

SDK, host and CLI changes:

- Every construction site in the phase 1e inventory resolves `INimBusTransport` instead of
  `ServiceBusClient`: the SDK sites in §2.1, the WebApp and CLI sites in §2.6, and the Resolver's.
  The inventory comes first (§10), so the list is complete before the migration starts.
- **`INimBusSubscriber`** (new, transport-neutral) takes `RegisterHandler`. `ISubscriberClient`
  becomes `INimBusSubscriber, IServiceBusAdapter`, so Azure Functions subscribers keep calling
  `Handle(ServiceBusReceivedMessage…)` unchanged. On RabbitMQ, applications resolve
  `INimBusSubscriber`.
- `NimBusReceiverHostedService`, `DeferredMessageProcessorHostedService`, `OutboxDispatcherSender`
  and `HandoffClientFactory` keep their public constructors, which stay Service Bus-typed. New
  transport-neutral equivalents sit next to them. The old constructors get `[Obsolete]` only once a
  neutral path covers every use, and are removed in the next major (`docs/versioning.md`).
- `NimBusReceiverOptions.MaxConcurrentSessions` applies to Service Bus only; on RabbitMQ the
  partition count sets parallelism (§6.7).

`ManagerClient` sends through `ISender`, keeping its `ServiceBusClient` constructor as a bridge.

Phase 1 changes how the hosts get their transport, not what they need: `Resolver.Host` and the
WebApp still require Service Bus configuration until a RabbitMQ registration exists to select
(phase 3, §6.16, §6.17).

### 6.4 Configuration

| Setting | Values | Read by |
|---|---|---|
| `NimBus:Transport` (`NimBus__Transport`) | `servicebus` (default), `rabbitmq` | `Resolver.Host`, WebApp, `NimBus.Router.Host`, `nb` |
| `ConnectionStrings:rabbitmq` | AMQP URI (`amqps://` with TLS), as Aspire's `AddRabbitMQ("rabbitmq")` emits | every RabbitMQ process |
| `ConnectionStrings:nimbus-transport` | SQL Server connection for transport state; may point at the message store's database | every RabbitMQ process |
| `NimBus:Transport:RabbitMq:VirtualHost` | default `/` | every RabbitMQ process |
| `NimBus:Transport:RabbitMq:LeaseDuration` / `LeaseRenewal` | default 60 s / 20 s | receivers, router |
| `NimBus:Transport:RabbitMq:AbandonBackoff` | default 5 s | receivers |
| `NimBus:Transport:RabbitMq:DrainSettle` | default 5 s | deferred-processor hosts |
| `NimBus:Transport:RabbitMq:Router:Enabled` | default `true` in `Resolver.Host` | `Resolver.Host` |
| `NimBus:Transport:RabbitMq:Router:MaxIntakeAge` | default 1 min; the readiness threshold (§6.19) | router hosts |
| `NimBus:Transport:RabbitMq:ManagementApiUrl` | RabbitMQ management API (phase 4) | WebApp |

Endpoint applications choose the transport in code (`AddNimBusRabbitMqTransport`). The partition
count is topology, not runtime configuration: `nb topology apply` writes it to the routing table
(§6.15).

### 6.5 Topics, subscriptions and the router

On Service Bus a NimBus sender publishes to a **topic**, and the topic's **subscriptions** decide
where copies go (`TopologyDescriptor.ForEndpointTopic`). On RabbitMQ a topic becomes an exchange,
and the **router** evaluates the same subscription matrix and publishes the copies. Everything
published to a topic goes through the router, as everything on Service Bus goes through a topic's
rules: Resolver responses, retries, continuations, replays, scheduled dispatches and raw publishes.

Per endpoint topic `T`:

- **Exchange `nimbus.T`** (direct). NimBus senders publish with routing key `p<i>` (§6.6); raw
  publishers use `raw`.
  - **Intake queues `nimbus.T.in.p<i>`**, bound with key `p<i>`.
  - **Raw lane `nimbus.T.in.raw`**, bound with key `raw`.
  - **Alternate exchange `nimbus.T.unrouted`** (fanout), bound to the raw lane, so a raw publish
    with any other key lands there too. RabbitMQ counts a message routed through the alternate
    exchange as routable, so `mandatory` doesn't return it.
- **Partition queues `nimbus.T.p<i>`**, bound to the shared direct exchange **`nimbus.route`**
  with key `T.<i>`: what T's handlers consume.
- **Trigger queue `nimbus.T.deferred-processor`**, bound to `nimbus.route` with key
  `T.deferred-processor` (§6.10).
- **Dead-letter queue `nimbus.T.dlq`**, through a dead-letter exchange.

The Resolver topic has the same shape, with one rule: everything goes to its partitions (the
retained `$Default` rule). The router may route Resolver copies straight to the Resolver's
partitions, since that rule passes everything.

All quorum queues except trigger queues use `x-single-active-consumer: true`. All quorum queues use
`dead-letter-strategy: at-least-once` and `overflow: reject-publish`; the defaults dead-letter at
most once. **Raw consumers** (§2.5) bind their own queue to `nimbus.T` with every key (`p0` …
`p<N-1>`, `raw`) and to `nimbus.T.unrouted`. A queue bound with several keys gets each message once.

**Sending.** Every publish the RabbitMQ transport makes uses publisher confirms and `mandatory`: the
`ISender` (publishers, subscriber responses, the outbox dispatcher, `ManagerClient`, the WebApp), the
router, the drain, the scheduled dispatcher and the reply dispatcher. `Send` returns only after the
broker confirms that the destination queues accepted the message. A nack (for example from
`overflow: reject-publish`), a return or a confirm timeout throws, so callers see what a failed
Service Bus send gives them: Core leaves the delivery unsettled, and the outbox keeps the row.

**The router** consumes every topic's intake queues and raw lane in order (single active consumer,
prefetch 1). Router replicas share the lanes: each lane has one active consumer, so a crashed
replica's lanes fail over. For each message, the router evaluates every subscription below. Each
subscription's rules are mutually exclusive (they test different `To` values or event types), so a
subscription yields at most one copy, as on Service Bus.

| Service Bus subscription on topic T | Filter | Copy | Destination |
|---|---|---|---|
| Endpoint subscription `T` | `To = T` | unchanged | `nimbus.route`, key `T.<partition>` |
| | `To = Continuation` | `To = T`, `From = Continuation` | same |
| | `To = Retry` | `To = T`, `From = Retry` | same |
| Reply subscription `T-reply` | `To = T-reply` | not used on RabbitMQ: replies go straight to a reply queue (§6.13) | — |
| Resolver fan-out | `To = Resolver` | `From = T` | `nimbus.Resolver` |
| | `To = T` | unchanged | `nimbus.Resolver` |
| `Deferred` | `To = Deferred AND OriginalSessionId IS NOT NULL` | no broker copy: a `ParkedMessage` row (§6.10). The NimBus sender writes it directly; a raw publish that matches is parked by the router. Without `OriginalSessionId` nothing matches and it is dropped, as on Service Bus | SQL |
| `DeferredProcessor` | `To = DeferredProcessor` | unchanged | `nimbus.route`, key `T.deferred-processor` |
| Consumer forwarder `C` (compiled), one per consumer | `EventTypeId ∈ C's types AND From IS NULL` | `From = T`, `To = C`, per-copy `EventId` | `nimbus.C`, key `p<partition>` |
| Dynamic forwarder `dynamic-C` | same, for dynamic types | `From` = the configured dynamic source string (which can differ from `T` in case), `To = C`, per-copy `EventId` | same |

A message no subscription matches is acknowledged and counted, as Service Bus discards it.

**Publishing a copy.** Every router publish uses publisher confirms and `mandatory`. The router
acknowledges the intake message only when every copy is confirmed and none was returned. A return
(an unroutable copy) or a failed confirm leaves the intake message unacknowledged; it is retried
with `basic.reject` and dead-lettered at the delivery limit (§6.12). A retry republishes copies that
were already confirmed, so a destination can receive the same copy twice. Every copy keeps the
original `MessageId` (Service Bus auto-forwarding keeps it too). A raw CloudEvent without one gets a
`MessageId` derived from its `source` and `id` (UUID v5), because the inbox skips deduplication for
a blank `MessageId` (`InboxDuplicateDetector.cs:160`). With a stable `EventId` (below), a duplicate
copy is recognisable by both ids.

**Per-copy `EventId`.** Service Bus uses `newid()`; there, auto-forwarding is transactional. Here, a
redelivered intake message must produce the same copies. So the `EventId` is a name-based UUID (v5)
of:

- the **publish identity**: a random publish-id header the RabbitMQ sender stamps on every NimBus
  publish, or, for a raw publish, the CloudEvents `source` plus `id` (unique as a pair by the
  CloudEvents spec), falling back to `MessageId`;
- the topic `T`;
- the subscription name (`C` or `dynamic-C`), so a compiled and a dynamic forwarder to the same
  consumer give two copies different ids, as two Service Bus rule actions do.

Two identical NimBus publishes have different publish ids, so they stay two events. A raw message
with no `MessageId` and no CloudEvents `id` is dead-lettered with a reason.

**Routing table.** The router reads the matrix from SQL (§6.14), written by `nb topology apply`
from the same `IPlatform` catalog `TopologyDescriptor` uses. It caches the table and refreshes it
periodically and on a version change.

**Cost.** Every message crosses the broker at least twice (topic exchange, router, partition), and
an event four times from producer to consumer (P's topic → router → C's topic → router → C's
partitions), as it crosses two topics on Service Bus.

**When no router runs.** Service Bus applies topic rules as long as the namespace is up; here they
need a running router replica. With every replica down:

- Publishes still succeed once the intake queue confirms them, and wait there.
- Nothing reaches handlers or the Resolver: no new events, responses, retries, replays or due
  scheduled messages. Due rows stay in `ScheduledMessage` and dispatch when a router returns.
- Requests time out. A reply to a request already being handled still arrives, because replies
  don't go through the router (§6.13).

That is a degraded state, not a healthy one with no traffic. The router health check (§6.19) reports
it: the installation is unhealthy when an intake lane has no active router consumer or its oldest
message is older than a threshold. Installations run two or more router replicas (§6.1), so this
takes losing all of them.

### 6.6 Partitioning

`partition = stableHash(SessionId) % N(topic)`. The NimBus sender computes it for the topic it
publishes to (key `p<i>`); the router computes it for each destination (key `T.<i>` for
partitions, `p<i>` for another topic's exchange).

- The hash is fixed and versioned: xxHash64 of the UTF-8 session id, documented so raw publishers
  can compute it. Never `string.GetHashCode`.
- **Order is per session per lane.** Every NimBus publish for a session enters the topic through
  the same intake lane. Raw publishes enter through the raw lane, so a raw publish and a NimBus
  publish for the same session have no defined order between them; on Service Bus they meet in
  one topic in arrival order. A raw publisher that needs that order uses key `p<i>` with the
  documented hash instead of `raw`.
- The router reads a raw message's session id from a `nimbus-session-id` header, falling back to
  the CloudEvents `partitionkey` extension.
- **A partition is never stored.** Parked, scheduled and outbox rows keep the destination and
  session id; the partition is computed at publish time.
- **Changing N** for an endpoint: stop everything that publishes to it (producers, the router, the
  scheduled and outbox dispatchers), let its intake and partition queues drain, update N, the
  routing table version and raw consumers' bindings, restart. Senders and the router refuse to
  publish under a stale version.
- **Not** the consistent-hash exchange. Its ring is rebuilt at boot, so after a restart "a given
  routing key now may route to a different queue", which breaks per-session order.
- **Missing session id.** The SDK assigns a GUID to events without a session key
  (`Abstractions/Events/Event.cs:23`), but `MessageHelper.cs:141` passes any `IMessage.SessionId`
  through. The RabbitMQ sender and router do what Service Bus does with an empty session id on a
  session-enabled subscription (§15).

### 6.7 Receiving

The receiver consumes every partition queue of its endpoint (prefetch 1). Single active consumer
makes one consumer active per partition, with automatic failover. Messages in a partition are
handled one at a time. The broker ack is `Complete`. Endpoint parallelism is the number of
partitions, instead of `MaxConcurrentSessions`.

**Spreading partitions across replicas.** With single active consumer, the first replica to
subscribe can end up active on every partition, so a second replica adds failover but no
throughput. Service Bus spreads sessions automatically. Here a replica that holds more than its fair
share of partition leases (§6.8; fair share = N divided by the live replicas in `ReplicaHeartbeat`)
hands each surplus partition over:

1. It cancels its consumer on the partition, finishes and acknowledges the delivery in flight, and
   releases the lease (§6.8).
2. The broker activates another waiting consumer as soon as the cancel lands. That consumer waits
   for the lease before handling anything, so the two never overlap.
3. It re-subscribes with a lower consumer priority (`x-priority`), so it isn't chosen again while
   another replica is available.

Since RabbitMQ 4.0, consumer priority also decides which consumer of a quorum queue with single
active consumer is active: a higher-priority consumer takes over once the current deliveries are
acknowledged. RabbitMQ 3.13 didn't do this; D7's 4.3 floor covers it. Phase 2 tests the timing:
handover order, lease release and no overlap.

**Behavioural difference:** a slow handler delays the other sessions in its partition. It never
blocks them, because failures park (§2.2). Choosing N trades parallelism against queue count.

### 6.8 Partition lease

The counterpart of the session lock. `PartitionLease(Queue, Owner, Epoch, ExpiresUtc)`:

- A consumer must hold the lease before it handles a delivery. On its first delivery after
  subscribing, it takes the lease if it is free or expired (`Epoch + 1`); if another owner's lease is
  still valid, it holds the delivery unacknowledged and retries until the lease expires.
- The holder renews it on a timer (`LeaseDuration` 60 s, `LeaseRenewal` 20 s). A holder that hands
  a partition over (§6.7) or shuts down cleanly releases the lease after acknowledging its last
  delivery, so the successor doesn't wait for expiry.
- **If renewal fails or the lease is found expired,** the consumer cancels the running handler's
  cancellation token, stops publishing for it, and closes its channel without acknowledging. The
  broker requeues the unacknowledged delivery for the successor. Like Service Bus after
  `SessionLockLost`, anything the handler already published stays published.
- **Timeouts.** A successor waiting for a lease holds one delivery unacknowledged. The lease period
  plus the SQL timeout must stay well below the RabbitMQ consumer timeout (default 30 minutes). If
  the timeout fires anyway, for example during a long SQL outage, the broker cancels the consumer
  and requeues the delivery; that increments only the acquired count, not the delivery count.
- **Every SQL write made while handling a partition's delivery** (session state, parking,
  scheduling) carries `WHERE Owner = @me AND Epoch = @epoch AND ExpiresUtc > SYSUTCDATETIME()`
  against the lease row, in the same transaction. A stale write fails with `TransientException`, as
  `SessionLockLost` does on Service Bus.
- **How the lease reaches those writes.** Session-state writes and `ScheduleRedelivery` go through
  the delivery's `IMessageContext`, which holds the lease. Parking and retry scheduling go through
  `ISender` (`Send` with `To = "Deferred"`; `ScheduleMessage` or `Send` with a delay,
  `ResponseService.cs:175-186`), which has no delivery parameter, and
  the SDK's senders are singletons. So the RabbitMQ receiver runs each handler call inside a delivery
  scope (an `AsyncLocal`, internal to the RabbitMQ package) holding the partition queue, owner and
  epoch, and the RabbitMQ `ISender` adds the lease check to any SQL write it makes inside a scope.
  Core's sends for a delivery run on the handler's async flow, so they see the scope. The scope is
  closed when the delivery settles; a write through a closed scope fails, so a task the handler left
  running can't write under a lease it no longer holds. Neither Core nor the `ISender` contract
  changes.
- **Writes outside a partition delivery have their own authority**, as on Service Bus, where the
  drain's lock on the `Deferred` session is separate from the main session lock. The drain deletes
  rows on the connection that holds its application lock (§6.10). The router and the scheduled
  dispatcher only insert idempotent rows or claim them with a state check. A `Send` or
  `ScheduleMessage` with no delivery scope (publishers, the WebApp, the outbox dispatcher) writes
  without a lease check, as a Service Bus send outside a session is unfenced. The WebApp's
  session-state change uses optimistic concurrency (§6.9).

A consumer that lost its broker connection keeps a valid lease until it expires, but its successor
doesn't start until then, so the two never overlap: the guarantee a Service Bus session lock gives.
As on Service Bus, this doesn't fence handler side effects: a Resolver response already published,
inbox or outbox rows written by handler code, calls to external systems. The cost is a takeover
delay of up to one lease period after a crash.

### 6.9 Session state

`SessionState(Endpoint, SessionId, BlockedByEventId, BlockedByMessageId, DeferredCount,
NextDeferralSequence)`: the same four fields, read and written through the same `IMessageContext`
calls as on Service Bus, under the lease (§6.8).

**Writers outside the partition.** The WebApp's "reprocess deferred" clears a session's state, then
sends the trigger. On Service Bus it clears the state while holding the session lock
(`AcceptSessionAsync`, `AdminService.Resubmit.cs:207`), so it can't interleave with a handler. The
partition lease covers a whole partition and the WebApp doesn't hold it, so `SessionState` uses
optimistic concurrency as well: the RabbitMQ `IMessageContext` reads the row's `RowVersion` with the
state, and each of its writes checks that `RowVersion` as well as the lease. The WebApp's clear checks
it too. The write that loses the race fails with `TransientException`: a handler's delivery is
redelivered and reads the new state; the WebApp reports the conflict and the operator retries.

### 6.10 Parking and replay

Same flow as Service Bus, with SQL instead of the `Deferred` subscription:

- **Park.** `ISender.Send` with `To = "Deferred"` inserts a `ParkedMessage` row. The parked copy has
  no `MessageId` (§2.2), so the row is unique on `(Endpoint, SessionId, ParentMessageId)`: a
  redelivered original that parks again doesn't create a second row. Order is `DeferralSequence`,
  as on Service Bus. Core's other two calls (sequence, count) update `SessionState` as before.
- **Trigger.** `ProcessDeferredRequest` (`To = "DeferredProcessor"`) goes through the router to
  `nimbus.T.deferred-processor`, a plain quorum queue. A RabbitMQ variant of
  `DeferredMessageProcessorHostedService` (which builds a `ServiceBusProcessor` today, line 72)
  consumes it in the endpoint host, as ADR-003 places it, and calls
  `IDeferredMessageProcessor.ProcessDeferredMessagesAsync(sessionId, topicName)` with the endpoint
  as `topicName`.
- **Drain.** The RabbitMQ `IDeferredMessageProcessor`, standing in for `AcceptSessionAsync`:
  1. Opens a dedicated SQL connection with `Pooling=False` and takes a session-owned application
     lock (`sp_getapplock`) on `(Endpoint, SessionId)`, held for the whole drain, so two triggers
     can't drain one session at once. `Pooling=False` matters: a pooled connection would keep the
     session, and the lock, alive after `Dispose`, as `SqlServerSchemaInitializer` already notes.
     If the lock is taken, the trigger is a no-op, as `SessionCannotBeLocked` is today.
  2. Repeats: read the next batch of rows with `DeferralSequence` above the last one handled, in
     order. For each row, if its `ParentMessageId` is the `MessageId` of a replay from this drain,
     the replay was parked again: stop, leaving it and everything after it
     (`IsReplayParkedAgain`). Otherwise publish it to topic T (`nimbus.T`, key `p<i>`) with a new
     `MessageId`, as `CreateRepublishedMessage` does; wait for the confirm, check it wasn't
     returned, and delete the row.
  3. On an empty read, waits `DrainSettle` (5 s, the Service Bus drain's receive timeout) and reads
     again, so a replay re-parked just after its confirm is still seen. Finishes when that read is
     empty too, then releases the lock.

  Reading in batches by sequence finds rows parked while the drain runs, as receiving from
  `Deferred` does. The replay goes through the router, so it reaches T's partitions and the Resolver
  fan-out exactly as a Service Bus replay published to T's topic does. A crash between confirm and
  delete republishes that row on the next drain: the window Service Bus has between send and
  complete.

### 6.11 Scheduling

There is no native scheduled delivery. `ScheduledMessage` holds the message and its due time. The
scheduled dispatcher runs with the router. It claims due rows with
`UPDATE … SET State = 'Dispatching' WHERE State = 'Pending'`, publishes each to its topic (confirm +
`mandatory`), and deletes it after the confirm. The row id is the `long` token
`ISender.ScheduleMessage` returns; `CancelScheduledMessage` deletes the row only while it is
`Pending`, so a cancel and a dispatch can't both win. A row stuck in `Dispatching` after a crash is
retried after a timeout, which can publish it twice: at-least-once. A returned (unroutable) message
keeps its row and is flagged for an operator.

`ScheduleRedelivery` and retry-policy delays use the same table. `ScheduleRedelivery` keeps the
original `MessageId`, as the Service Bus implementation does (`MessageContext.cs:594`, Spec 030
§5.7), and completes the original only after the row has committed.

**Not** the delayed-message-exchange plugin: its delayed messages have a single replica per node,
and it isn't meant for large numbers of delayed messages.

### 6.12 Transient retries

The RabbitMQ `Abandon` waits `AbandonBackoff` in-process, standing in for the wait for the Service
Bus session lock (§2.3), then sends `basic.reject` with requeue. Not `basic.nack`: from RabbitMQ
4.3, reject and a crashed channel increment the delivery count but nack does not, so a nack-based
retry could loop forever. With reject, the quorum queue's delivery limit (default 20) dead-letters
the message. Returned messages are redelivered "in the exact order they were returned".

**Difference from Service Bus:** the backoff and retries hold up the rest of the partition; Service
Bus holds only the session. The bound is roughly the delivery limit times the backoff. Phase 2
measures whether that is acceptable or whether a transient failure should park the session and
schedule a retry instead.

### 6.13 Request/reply

`PublisherClient.Request` waits for one reply on a Service Bus reply session today. On RabbitMQ the
`IReplyReceiver` declares an exclusive, auto-delete reply queue per client instance, named
`nimbus.reply.<application>.<guid>` (not a server-generated `amq.gen-…` name, so permissions can
match it, §6.18). It binds the queue to the shared direct exchange `nimbus.reply` with the queue name
as key and **starts consuming before the request is sent**, with `ReplyTo = <queue>` and a
correlation id. Today the client sends first and then opens the reply session; on RabbitMQ a fast
reply to an undeclared queue would be unroutable. Replies are matched by correlation id; they don't
need session order, and a reply with an unknown correlation id is dropped. `IReplyDispatcher`
publishes to `nimbus.reply` with the `ReplyTo` value as routing key, with confirms and `mandatory`.
Replies don't go through the router. Responders need write access to `nimbus.reply` only, not to the
default exchange, which would let them publish to any queue by name.

### 6.14 SQL schema

Schema `nimbus_transport` (configurable), created by DbUp scripts embedded in
`NimBus.Transport.RabbitMq`, with their own journal table. They run from `nb topology apply` and,
optionally, at startup under a session-owned `sp_getapplock` on a `Pooling=False` connection, as
`SqlServerSchemaInitializer` does for the message store.

| Table | Key | Columns | Notes |
|---|---|---|---|
| `RoutingVersion` | — | `Version`, `AppliedUtc` | Senders and the router refuse a stale version (§6.6) |
| `Topic` | `Topic` | `Partitions`, `HashVersion` | Written by `nb topology apply` |
| `RoutingRule` | `(Topic, Subscription, Rule)` | `Kind`, `FilterTo`, `FilterEventTypeId`, `StampFrom`, `StampTo`, `Destination` | The §6.5 matrix, as data |
| `PartitionLease` | `Queue` | `Owner`, `Epoch`, `ExpiresUtc` | §6.8; one row per partition queue and intake lane |
| `ReplicaHeartbeat` | `Replica` | `Role`, `LastSeenUtc`, `RoutingVersion` | Fair-share count for §6.7; the routing version each router runs, for §6.15 |
| `SessionState` | `(Endpoint, SessionId)` | the four fields, `RowVersion` | §6.9 |
| `ParkedMessage` | `Id` identity | `Endpoint`, `SessionId`, `DeferralSequence`, `ParentMessageId`, `Body varbinary(max)`, `Headers nvarchar(max)`, `ParkedAtUtc` | Unique `(Endpoint, SessionId, ParentMessageId)`; index `(Endpoint, SessionId, DeferralSequence)` |
| `ScheduledMessage` | `Id bigint` identity | `Destination`, `SessionId`, `MessageId`, `Body`, `Headers`, `DueUtc`, `State`, `ClaimedUtc` | Index `(State, DueUtc)` |

Rows are deleted when their work is done, so the tables stay small. A parked session that is never
unblocked keeps its rows, as a `Deferred` session does on Service Bus.

### 6.15 Topology provisioning and the CLI

`nb topology apply --transport rabbitmq`:

1. Applies the SQL schema (§6.14).
2. Builds the expected topology from the `IPlatform` catalog, through the same descriptor logic as
   `TopologyDescriptor`. Rejects an endpoint name containing `.`, which the permission patterns in
   §6.18 rely on.
3. Declares exchanges, queues and bindings idempotently, then checks each queue with a passive
   declare. RabbitMQ rejects re-declaring a queue with different arguments; the command reports such
   queues and refuses to continue unless `--migrate-queue <name>` is given, which drains and
   re-creates it.
4. Only then, in one SQL transaction, writes `Topic` and `RoutingRule` and a new `RoutingVersion`.
   Routers and senders pick up the new routes at that point, when every destination already exists.
5. Reports NimBus-named queues and bindings the catalog no longer describes, as the WebApp's
   topology audit does for Service Bus. It deletes them only when asked, and only after every live
   router reports the new version in `ReplicaHeartbeat`, so no router still routes to them.

A partial apply is safe to re-run. If it stops before step 4, the old routing version stays active
and routes only to resources that still exist; the extra resources are idle until the next run. If
it stops after step 4, only the report and cleanup remain.

`nb infra apply`, `nb deploy apps` and the other Azure commands don't apply on-prem; the
distribution repository (§6.2) covers installation.

### 6.16 WebApp

Startup chooses the transport from `NimBus:Transport`, as it chooses the store from
`NimBus:StorageProvider`. `AddServiceBusClients` runs only for Service Bus. The heartbeat sender,
`ManagerClient`, `AgentEventPublisher`, event composition (`EventImplementation`, through the
transport's sender instead of `PublisherClient.CreateAsync(serviceBusClient, …)`) and the simulation
publisher send through `ISender`.

**The container must build every controller on RabbitMQ.** Today `EventImplementation`,
`EndpointImplementation`, `AdminService` and `SubscriptionAdminService` take Service Bus types
(§2.6), so a RabbitMQ WebApp without them would fail at the first request to those controllers.
Phase 1g replaces the concrete Service Bus dependencies with interfaces covering what each service
does with them (endpoint status, subscription admin, dead-letter browsing, deferred inspection).
Service Bus registers today's implementations behind them. RabbitMQ registers implementations that
throw `NotSupportedException` until phase 4 supplies real ones; the API returns 501 for them, as it
already does when a storage provider doesn't support an operation
(`EventImplementation.Deferred.cs:61`).

**The API reports what is available.** Phase 3 adds two nullable fields to `ApplicationStatus` in
`api-spec.yaml`: `transport` (`servicebus` or `rabbitmq`, next to the existing `storageProvider`,
`ApplicationImplementation.cs:71`) and `transportCapabilities`, a list of names from
`TransportCapabilities`. The change is additive, and NSwag regenerates the controller and client. The
frontend hides a feature whose capability is missing; an older frontend that ignores the fields gets
501s instead.

| Feature | Service Bus | RabbitMQ, phase 3 | RabbitMQ, phase 4 |
|---|---|---|---|
| Message search, detail, history, metrics from the store | yes | yes | yes |
| Resubmit, skip, bulk resubmit failed, skip by status | yes | yes (messages through `ISender`) | yes |
| Handoff settlement | yes | yes | yes |
| Heartbeat | yes | yes | yes |
| Delete events, purge session, reconcile stale pending | yes | yes where the phase 1 inventory confirms store-only; otherwise hidden | yes |
| Copy endpoint data | Cosmos DB store only (`AdminService.Copy.cs:19`); SQL Server installations use backup and restore | no: on-prem uses SQL Server | no |
| Reprocess a deferred session | yes | yes: clears the session's state in SQL with the `RowVersion` check (§6.9), then sends the trigger; the drain reads SQL | yes |
| Deferred-message inspector | yes | hidden | from `ParkedMessage` |
| Topic and subscription overview, purge, delete, recreate, rules | yes | hidden | queues and bindings through the RabbitMQ management API; rules from the routing table |
| Topology audit and cleanup | yes | hidden | against the routing table and broker |
| Endpoint enable/disable (subscription status, topic send) | yes | hidden | design in phase 4 |
| Resolver dead letters: browse, resubmit, delete | yes | hidden | a dead-letter recorder writes RabbitMQ dead letters to SQL; the WebApp browses SQL. Browsing a queue by consume-and-requeue is not safe |
| Simulation hosts | yes | hidden | hidden |
| MCP operator tools | yes | the tools whose features are available | same |

The phase 1 inventory (§10) confirms each row, including the `CosmosException` paths in §2.6.

### 6.17 Resolver

`Resolver.Host` chooses the transport from `NimBus:Transport` and builds the Resolver's receiver
through `INimBusTransport`. On RabbitMQ it also hosts the router and the scheduled dispatcher unless
`Router:Enabled` is false. The Functions entry point (`NimBus.Resolver/Functions.cs`) stays Service
Bus-only. `AddResolver` builds a `ServiceBusClient` and requires its configuration today
(`NimBus.Resolver/ServiceExtensions.cs:37-53`); phase 3a moves that registration behind the transport
choice, which is when `Resolver.Host` can start with no Service Bus configuration.

### 6.18 Security

- **RabbitMQ.** TLS (`amqps`), one virtual host per installation, and a user per role. RabbitMQ
  grants configure, write and read permissions by one regular expression each, matched against
  exchange and queue names. Publishing needs write on the exchange; consuming needs read on the
  queue; binding a queue needs write on the queue and read on the exchange; declaring or deleting
  needs configure. The patterns below assume endpoint names contain no `.` (§6.15 enforces it):

  - **Provisioner** (`nb topology apply`): configure, write and read `^nimbus\.`.
  - **Router and scheduled dispatcher** (`Resolver.Host` or `NimBus.Router.Host`): configure nothing;
    write `^nimbus\.(?!reply$)` (topic exchanges and `nimbus.route`); read `^nimbus\.[^.]+\.in\.`
    (intake lanes and raw lanes).
  - **Endpoint `E`** (its receiver, deferred-processor host and replies): configure
    `^nimbus\.reply\.E\.` (its reply queues); write `^(nimbus\.E|nimbus\.reply|nimbus\.reply\.E\..+)$`
    (its own topic, replies, binding its reply queues); read
    `^(nimbus\.E\.(p\d+|deferred-processor)|nimbus\.reply|nimbus\.reply\.E\..+)$`.
  - **Publish-only application `P`**: configure `^nimbus\.reply\.P\.`; write
    `^(nimbus\.P|nimbus\.reply\.P\..+)$`; read `^(nimbus\.reply|nimbus\.reply\.P\..+)$`.
  - **WebApp**: write `^nimbus\.(?!route$|reply$)[^.]+$` (topic exchanges, for resubmit, skip,
    heartbeat and composed events); nothing else. Phase 4 adds a management API user with the
    `monitoring` tag.
  - **Raw publisher** to topic `T`: write `^nimbus\.T$`.

  RabbitMQ evaluates the patterns as Erlang regular expressions, which support the negative
  lookaheads. Write access to `nimbus.reply` lets a principal send a reply to any reply queue, as
  send access to a Service Bus topic lets a sender address any reply subscription on it; replies are
  matched by correlation id (§6.13). Nothing gets write access to the default exchange. The patterns
  are a starting point; phase 2 checks them against every publish path (commands, request/reply,
  handoff) with a live test per role.

- **SQL Server.** The transport schema gets its own database role. Endpoint principals need read and
  write on `SessionState`, `ParkedMessage`, `ScheduledMessage` and `PartitionLease`. SQL Server
  grants per table, not per row, so an endpoint principal can technically touch another endpoint's
  rows. Row-level security is an option if a customer requires isolation (§14).
- **Secrets** come from configuration or the customer's secret store; nothing depends on Entra.
  The WebApp uses local Identity.

### 6.19 Observability and health

- W3C trace context in message headers, carried through the router, so a trace spans publisher,
  router and handler. `messaging.system` is `rabbitmq`. Export through OTLP instead of Azure Monitor.
- Metrics: intake and partition queue depth (management API), router lag (intake age), lease
  takeovers, parked rows per endpoint, due scheduled rows, dead-lettered messages.
- Health checks: RabbitMQ connection and a passive declare of the process's queues; SQL transport
  schema version. The router's readiness check fails when an intake lane has no active router
  consumer or its oldest message is older than a threshold (default 1 minute), so "routing stopped"
  is distinguishable from "no traffic" (§6.5).

### 6.20 Local development

The Aspire AppHost gets `NIMBUS_TRANSPORT=rabbitmq`, next to `NIMBUS_STORAGE_PROVIDER` and
`NIMBUS_SB_EMULATOR`. Today it wires a Service Bus graph throughout (`NimBus.AppHost/Program.cs`):
the `AspirePubSub.Provisioner`, the Resolver as an Azure Functions project, and Service Bus
references on the WebApp and the samples. With `rabbitmq` the AppHost builds a different graph:

- A RabbitMQ resource (`Aspire.Hosting.RabbitMQ` 13.5.4, matching the AppHost's Aspire version, with
  the management plugin) instead of the `servicebus` connection or emulator.
- The SQL Server resource, whatever `NIMBUS_STORAGE_PROVIDER` says, because transport state needs it;
  `nimbus-transport` points at it.
- The provisioner applies the RabbitMQ topology, with the same code as
  `nb topology apply --transport rabbitmq`, instead of the Service Bus topology. Everything else waits
  for it to complete, as today.
- `NimBus.Resolver.Host` (resolver, router and scheduled dispatcher) instead of the Functions
  project.
- The WebApp, publisher and subscribers get the `rabbitmq` and `nimbus-transport` references and
  `NimBus__Transport=rabbitmq`; the samples select the transport from that setting.

CrmErpDemo gets the same switch in phase 3, and the nightly end-to-end workflow runs it on both
transports.

## 7. Parity with Service Bus

| Behaviour | Service Bus | RabbitMQ + SQL Server |
|---|---|---|
| Per-session order | Session lock, in topic arrival order | Partition with single active consumer and lease; per intake lane, so a raw and a NimBus publish for one session aren't ordered against each other (§6.6) |
| Failed session isolated from others | Yes (park) | Yes (park) |
| Concurrency unit | Session | Partition |
| Scale-out across replicas | Automatic | Fair-share handover with consumer priorities (§6.7) |
| Slow handler or transient retry delays others | No | Same partition only, bounded |
| Transient retry bounded | Delivery limit | Delivery limit, using `basic.reject` |
| Two consumers on one session | Prevented by the lock; takeover after expiry | Prevented by the lease; takeover after expiry |
| Writes after lock loss rejected | Session state and settlement | Transport SQL writes after lease expiry or epoch change; acks fail once the channel closes. Neither fences publishes already made |
| Handler side effects after lock loss | Not fenced | Not fenced |
| Topic rules run without a receiving host | Yes (broker) | Yes (router) |
| Routing while the platform's own service is down | The namespace routes | With every router replica down, messages wait in intake queues and health reports it (§6.5) |
| Session-state change from the WebApp | Under the session lock | Optimistic `RowVersion` check (§6.9) |
| Fan-out for raw publishers and consumers | Yes | Yes |
| Forwarding | Transactional auto-forward per subscription copy | Not transactional; a redelivered intake message republishes identical copies (same `MessageId` and `EventId`) |
| Parking, replay, trigger | 3 writes; republish with a new `MessageId` | Same; duplicate park suppressed |
| The three bugs in §2.7 | Present | Fixed in Core for both before an on-prem release |
| Scheduled send token | Sequence number | SQL identity |
| Delivery guarantee | At least once | At least once |
| WebApp admin features | All | Subset until phase 4 (§6.16) |

## 8. CI

### 8.1 Today

One job, `ci` (`.github/workflows/dotnet.yml`), on pull requests, pushes to master and merge-queue
groups. A recent master run (2026-10-21, run 39562063002) took about 5 min 50 s:

| Step | Time |
|---|---|
| Start SQL Server and Cosmos DB emulator containers | 32 s |
| Restore (Release) | 28 s |
| Build the solution (Release, SPA and NSwag) | 113 s |
| Test the whole solution | 151 s |
| Coverage report, skip gates | 11 s |

Test assemblies run in parallel; the critical path is `NimBus.WebApp.Tests` and
`NimBus.ServiceBusEmulator.Tests`, about 1.9 minutes each. Gaps:

- **The WebApp frontend tests don't run in CI.** `AGENTS.md` lists `npm run test:ci`, but no
  workflow runs it; the build only runs `npm run build`.
- **Every run starts both service containers,** and the RabbitMQ suite would add a third, plus its
  tests, to every pull request's critical path.
- **One flaky suite reruns everything,** including the 2-minute build.
- **Live-infra tests are found by assembly.** The skip gates grep the Cosmos and SQL Server
  assemblies' trx files, while live tests also sit in `WebApp.Tests`, `Extensions.Identity.Tests`,
  `ServiceBus.Tests`, `Extensions.IntegrationIntelligence.Tests` and `CrmErpDemo.AppHost.Tests`.

### 8.2 Lanes

Constraint: the org ruleset requires a status check named exactly `ci`, and the merge queue needs
`merge_group` to trigger it (`dotnet.yml`). So the jobs stay in one workflow, and a final job named
`ci` aggregates them.

| Lane (job) | Services | Builds | Runs | Gate |
|---|---|---|---|---|
| `build` | none | Whole solution, Release, SPA + NSwag: the compile and warnings gate | All tests except categories `LiveSqlServer`, `LiveCosmos` and `LiveRabbitMq` | Nothing skipped |
| `sqlserver` | SQL Server 2022 | Whole solution, `-p:SkipSpaBuild=true` | `TestCategory=LiveSqlServer` | At least one test ran; nothing skipped |
| `cosmos` | Cosmos DB emulator (vNext) | same | `TestCategory=LiveCosmos` | same; emulator readiness wait as today |
| `webapp-client` | none (Node 22) | `npm ci` in `ClientApp` | `npm run test:ci` (Vitest) | JUnit results uploaded |
| `rabbitmq` (phase 2) | RabbitMQ 4.3 + SQL Server | same as `sqlserver` | `TestCategory=LiveRabbitMq` | At least one test ran; nothing skipped |
| `coverage` | — | — | Downloads every .NET lane's Cobertura files and runs ReportGenerator with today's Resolver/SDK filters | Informational, as today (issue #136); not a need of `ci` |
| `ci` | — | — | `needs` every lane, `if: always()` | Fails unless every need's `result` is `success`; `failure`, `cancelled` and `skipped` all fail it |

Details:

- **Categories instead of assembly lists.** The repository runs MSTest on VSTest (test projects
  reference `Microsoft.NET.Test.Sdk` and `MSTest.TestAdapter`; no `global.json` selects
  Microsoft.Testing.Platform), so `dotnet test --filter "TestCategory=…"` and `TestCategory!=…`
  work as documented. Every test that needs a live service gets `[TestCategory("LiveSqlServer")]`,
  `("LiveCosmos")` or `("LiveRabbitMq")`. Lanes filter by category, so a new live test lands in the
  right lane without workflow changes. By the review's count that is more than 30 test classes
  across eight test projects today, with no live category yet (the only categories in use are
  `CommonFidelity`, `Emulator` and `EmulatorOnly`).
- **Mixed-provider methods are split.** A category applies to a method, not to one `DataRow`.
  `DurableStoreConformanceTests` has memory, SQL Server and Cosmos DB rows on one method, and
  `IntelligenceSettingsStoreTests` has SQL Server and Cosmos DB rows. 1a splits each into one method
  per provider, so each lands in its own lane and the in-memory case stays in `build`.
- **The nothing-skipped gates.** Each lane counts `NotExecuted`, `Inconclusive` and `Skipped`
  outcomes across all of its own TRX files, as today's Cosmos and SQL Server gates do for one
  assembly each. The `build` lane's gate catches a live test someone forgot to tag: without its
  environment variable it would skip, and the gate fails. Before the gate can hold, 1a deals with
  the skips that have other causes:
  - `ServiceBusInstrumentationConformanceTests` always ends `Inconclusive`: even with
    `NIMBUS_SERVICEBUS_TEST_CONNECTION` set, its live publish is a TODO
    (`ServiceBusInstrumentationConformanceTests.cs:45`). 1a removes the placeholder subclass and
    opens an issue to run the instrumentation conformance against the emulator instead. The shared
    base class and `InMemoryInstrumentationConformanceTests` stay.
  - `DeterministicContactClassifierTests` is inconclusive only when `ANTHROPIC_API_KEY` is set, and
    `ArchitectureReferenceTests` only when the host assemblies aren't built. Neither applies in the
    `build` lane.
- **`CommonFidelity` tests stay in `build`:** they start a local emulator unless
  `NIMBUS_SBEMULATOR_TEST_CS` points them at real Azure, which only `servicebus-emulator-compat.yml`
  does.
- **Coverage keeps its scope.** Today one run collects Cobertura files from every test project and
  merges them into the Resolver/SDK report. After the split each .NET lane collects coverage and
  uploads its Cobertura files as an artifact, and the `coverage` job merges them with the same
  ReportGenerator filters, so the report doesn't silently lose the live suites.
- **Service lanes rebuild without the SPA.** Sharing build output between jobs means uploading
  several hundred MB of `bin` folders; rebuilding with `SkipSpaBuild=true` is simpler and skips the
  npm install. `ApiContract.g.cs` is committed, so the build doesn't need NSwag.
- **NuGet cache** (`actions/cache` keyed on the `.csproj` and `Directory.*.props` hashes) in every
  .NET lane.
- **Vitest becomes a required gate.** The known flake causes were fixed in #217 (in-test dynamic
  imports, a ref read by an effect). A new flake is fixed with the parallel-stress recipe, not
  retried away.
- **No docs-only filter.** Every lane runs on every pull request, push to master and merge group.
  Skipping .NET lanes on docs-only changes would skip tests that guard documentation:
  `BuildingAdaptersGuideTests` reads `docs/building-adapters.md` and `RateLimitWiringTests` checks
  `docs/webapp-rest-api.md`. It also keeps the aggregator simple: no lane may be skipped. The
  workflow keeps no path filter, so the required `ci` check is never left pending.

### 8.3 Effect

An estimate, to be measured on the PR that makes the change:

- **Critical path:** the `build` lane, about 4.5 minutes (no container start; the SQL Server and
  Cosmos suites leave it; `WebApp.Tests` stays the longest test assembly). Today it's about
  5.8 minutes. The service lanes take about 3-3.5 minutes in parallel.
- **The RabbitMQ lane adds no critical-path time** once it exists.
- **Runner minutes roughly double or triple.** The repository is public, so standard GitHub-hosted
  runners cost nothing.
- **Reruns are per lane:** a flaky Cosmos run reruns the Cosmos lane only.

`nuget-publish.yml` still builds and tests the whole solution before packing; it keeps today's
behaviour (live suites skip there). `crm-erp-e2e.yml` stays nightly; phase 3 adds a RabbitMQ
variant to its matrix.

The CI split doesn't depend on RabbitMQ. It can land first, as phase 1's first pull request.

## 9. Tests

**Shared transport behaviour suite** in `NimBus.Testing` (`TransportConformanceTests`), run against
the in-memory transport, the Service Bus emulator and RabbitMQ:

- **Interleaved order.** Sessions A and B share a partition (or, on Service Bus, run concurrently).
  A fails and parks; B completes; retry or skip of A replays A's parked messages in deferral order.
- **Park during drain.** A replay fails again mid-drain; the drain stops and leaves the rest parked.
- **Every routed subscription.** For each §6.5 row that produces a broker copy: endpoint,
  continuation, retry, Resolver fan-out (both rules), DeferredProcessor, compiled and dynamic
  forwarders (including both to one consumer), and a message no route matches. Assert `To`, `From`,
  `EventId` distinctness and the destination. Reply and `Deferred` are covered by request/reply and
  parking tests, since RabbitMQ replaces those subscriptions.
- **Publish-only producer.** Events from a topic with no running endpoint host reach consumers.
- **Transient retry.** A handler that always throws `TransientException` is dead-lettered at the
  delivery limit.
- **Scheduling.** Schedule, cancel, race a cancel against a due dispatch, and `ScheduleRedelivery`
  keeping the original `MessageId`.
- **Request/reply** across two instances, including a reply that arrives immediately.
- **The three §2.7 bugs**, as regression tests for their Core fixes.

**RabbitMQ-only tests** (`LiveRabbitMq`):

- **Lease.** Pause the active consumer mid-handler and cut its connection: the successor handles
  nothing until the lease expires, and the old consumer's later SQL writes fail. Repeat with the
  pause before the successor's first delivery. Make renewal fail mid-handler: the handler is
  cancelled and its delivery requeued. Hold a waiting successor past a short consumer timeout: the
  delivery is requeued without counting toward the delivery limit. A handler whose lease expired
  parks and schedules through `ISender`: both writes fail. A task the handler left running writes
  after the delivery settled: the write fails.
- **Session-state race.** The WebApp clears a session's state while a handler for that session
  writes it: one write fails on `RowVersion`, and the outcome matches one of the two orders.
- **Scale-out.** Start a second replica: partitions move until each holds its fair share, with no
  overlap and no reordering.
- **Crash points.** Kill the process before and after each SQL write and before and after each ack,
  in handling, parking, draining, routing and scheduled dispatch. Nothing is lost; a redelivered
  park doesn't add a row; a redelivered intake message republishes identical copies.
- **Partial router confirm.** One copy confirmed, the next returned: the intake message is not
  acknowledged, and the retry produces the same `MessageId` and `EventId` for the first copy.
- **Concurrent drains.** Two triggers for one session drain it once.
- **Raw ingress.** A raw publish with key `raw`, with an arbitrary key (alternate exchange), and
  without `message-id` or CloudEvents `id` (dead-lettered). A raw consumer sees all originals.
- **Router failover.** Kill the router replica that owns a lane; another takes over the lane and
  order holds. Stop every replica: publishes still succeed, the readiness check fails, and
  everything is delivered in order once a replica returns.
- **Broker failover.** Kill a quorum queue leader and the active consumer; order holds.
- **Publisher confirms.** Publish into a full intake queue (`reject-publish`) and to a missing
  exchange: `Send` throws, and an outbox row stays pending.
- **Topology.** `nb topology apply` is idempotent; a changed queue argument is reported, not applied.
  Add a consumer while routers run, and stop the apply before the routing version changes: nothing
  is routed to the new queues, and nothing is returned. Re-run it: routing starts.
- **Permissions.** For each role in §6.18, a live test publishes and consumes what the role needs
  and is refused what it doesn't, including publishing to the default exchange.
- **Capacity.** Throughput and p95 latency against the customer targets (§14) for a chosen N.

Service Bus behaviour is unchanged by phase 1. The existing test projects, the CRM/ERP end-to-end
suite and the shared suite on the emulator prove it.

## 10. Phases and pull requests

| Phase | Pull requests | Exit |
|---|---|---|
| 0 | ADR-018 accepted, and the guidance files that cite it drop "proposed"; a GitHub issue per the contribution process; customer throughput, latency and session-count targets; the live experiments in §15 | ADR accepted, targets agreed, results recorded in this spec |
| 1 | **1a** CI lanes, test categories, the split of mixed-provider methods, the placeholder suite and the `coverage` job (§8). **1b-d** the three Core fixes (§2.7), one PR each with its regression test. **1e** the inventory: every Service Bus- or Cosmos-typed construction site and dependency in the SDK, `Resolver.Host`, the WebApp and the CLI (§2.1, §2.6), with an on-prem answer each; it gates 1f and 1g. **1f** the transport seam in Core and the SDK, Service Bus registration and fallback (§6.3). **1g** `ManagerClient` over `ISender`; WebApp and `Resolver.Host` get their transport through `INimBusTransport`, and the WebApp's Service Bus dependencies move behind interfaces (§6.16). **1h** an in-memory `IDeferredMessageProcessor` and the shared behaviour suite, green on in-memory and the Service Bus emulator | Release build and all tests green; Service Bus behaviour unchanged; every site in the 1e inventory goes through the seam. The hosts still need Service Bus configuration |
| 2 | **2a** `NimBus.Transport.RabbitMq` skeleton, with `IsPackable=false`: sender with confirms, SQL schema, topology provisioner and `nb topology apply --transport rabbitmq`. **2b** receiver, lease and delivery scope, session state, fair-share handover. **2c** router. **2d** parking, drain and the deferred-processor host. **2e** scheduled dispatcher. **2f** request/reply. **2g** the `rabbitmq` CI lane, shared suite on RabbitMQ, RabbitMQ-only tests; the project becomes packable | §9 passes on RabbitMQ |
| 3 | **3a** `Resolver.Host` with router and scheduler, its Service Bus registration behind the transport choice; `NimBus.Router.Host`. **3b** WebApp on RabbitMQ: the `ApplicationStatus` fields and capability gating (§6.16). **3c** Aspire switch (§6.20) and CrmErpDemo on RabbitMQ; nightly end-to-end on both transports. **3d** OTLP, health checks, on-prem guide | CrmErpDemo end-to-end tests green on RabbitMQ and SQL Server; WebApp and `Resolver.Host` start with no Service Bus configuration |
| 4 | **4a** WebApp admin on the RabbitMQ management API, parked-message inspector, dead-letter recorder. **4b** distribution repository: compose files, installer, runbooks. **4c** HA, backup/restore and capacity tests. **4d** remove `[Experimental]` | Supportable release |

Release placement: phase 1 is additive and can ship in 5.x minors. The RabbitMQ package first ships
in a 5.x minor marked experimental. The Service Bus-typed bridges are removed in the next major.
Pull requests 2a-2f merge into master, but the project stays non-packable until 2g: the release
workflow packs every packable project (`nuget-publish.yml:56`), and projects are non-packable unless
they opt in (`Directory.Build.props:59`). A release cut during phase 2 therefore ships no partial
transport.

No effort estimate yet. Estimate after phase 0, from the inventory (1e) and a phase 2 work
breakdown.

## 11. Documentation

- `docs/on-prem.md` (new): requirements, installation, configuration (§6.4), RabbitMQ setup and
  permissions (§6.18), partition sizing, backup and restore of SQL transport state, upgrades,
  limits (§7).
- `docs/architecture.md`: the transport seam, the router, and both transports' flows.
- `docs/getting-started.md`: the `NIMBUS_TRANSPORT=rabbitmq` AppHost switch.
- `AGENTS.md` and `CONTRIBUTING.md`: the CI lanes, test categories and `NIMBUS_RABBITMQ_TEST_*`
  variables, next to the SQL Server and Cosmos ones.
- `README.md`: on-prem support once phase 4 completes.
- The raw-publisher contract: routing key, partition hash, `nimbus-session-id`, `message-id`.

## 12. Residual risks

1. **Head-of-line latency** inside a partition from slow handlers and transient retries (§6.7,
   §6.12). Needs targets from customers.
2. **Router hops.** Every message crosses the broker at least twice, an event four times. Phase 2
   measures the cost; if it's too high, the router can route a NimBus event straight to consumer
   partitions when the producer topic has no raw consumers, at the price of a second code path.
3. **Fair-share handover** depends on consumer-priority behaviour on quorum queues (§6.7).
4. **Raw publishers share one serial lane** per topic unless they compute the partition (§6.6).
5. **Takeover delay** of up to one lease period after a consumer crash (§6.8).
6. **Partition count** is fixed between maintenance windows (§6.6).
7. **Requeue order after a dropped connection** isn't explicitly documented by RabbitMQ (§15).
8. **SQL Server becomes critical for routing.** A SQL outage stops parking, leases and scheduling;
   receivers stop taking work rather than continue without state.
9. **The router is a platform service Service Bus doesn't need.** With every replica down, nothing
   is routed (§6.5). Mitigated by replicas and the readiness check, not removed.
10. **WebApp parity** decides when the transport is supportable, more than the message flow.

## 13. Alternatives considered

| Alternative | Why not |
|---|---|
| Fork an on-prem edition | Duplicates Core, Resolver, store, WebApp and SDK; every fix lands twice (ADR-018) |
| Run the NimBus Service Bus emulator in production | In-memory messages, locks and session state; loopback only; credentials not validated |
| A SQL "session runtime" that acknowledges on intake and owns all pending work (September 2026 study) | Aims for stronger guarantees than Service Bus gives; much more NimBus code. Kept as the fallback if phase 2 fails |
| One queue per session | Queue churn and count grow with session cardinality |
| RabbitMQ streams / super streams | Different offset model; per-session parking still needed; single active consumer needs the stream protocol client |
| Apache ActiveMQ Artemis message groups | Closer to sessions, but another broker to support; rebalancing caveats |
| Consistent-hash exchange for partitioning | Its ring is rebuilt at boot; routing can change after a restart |
| Delayed-message-exchange plugin for scheduling | One replica per node; not for large volumes |
| Fan-out in the sender (investigation revision 2) | Misses raw publishers and the outbox dispatcher's path; no stable `EventId` seed |
| Replay inside the partition consumer (revision 2) | Breaks Core's retry, skip, resubmit and handoff paths |
| Router in each endpoint host (revisions 3-5) | Publish-only processes have no host, so their topics would never be routed |
| A general-purpose messaging library underneath (MassTransit, NServiceBus transports) | Brings its own topology and session semantics; works against ADR-018's minimal seam |

## 14. Open questions for the owner

D1-D10 (§5) are this spec's decisions. They stand when ADR-018 is accepted unless the owner objects
to one; this section doesn't ask them again. What the spec can't decide:

1. **Release placement:** phase 1 in 5.x minors and the RabbitMQ package as experimental in a later
   5.x, or hold everything for a major?
2. **Customer targets:** message rate, session count, acceptable latency and takeover delay. They
   set N, the lease period and whether the router's extra hop is acceptable.
3. **Service Bus assembly on-prem:** is carrying `Azure.Messaging.ServiceBus` in on-prem
   applications (unused) acceptable until the next major (§6.2)?
4. **Row-level security** for transport tables, or per-table grants (§6.18)?
5. **Broker support:** customer-managed RabbitMQ only, or does NimBus document and support a
   reference cluster configuration?

## 15. Facts

**Recorded from code and documentation:**

1. **Duplicate copies at the Resolver, on SQL Server.** `StoreMessage` inserts a history row only if
   no row with the same `EventId` and `MessageId` exists
   (`SqlServerMessageTrackingStore.Messages.cs:13`), and the status upsert never lets an
   `EventRequest` or `DeferralResponse` overwrite a terminal status
   (`SqlServerMessageTrackingStore.Writes.cs:178-182`). A duplicate router copy is therefore a no-op
   for history and can't reopen a finished event.
2. **Consumer timeout.** RabbitMQ's default is 30 minutes, and it can be set per queue with the
   `consumer-timeout` policy key or the `x-consumer-timeout` queue argument
   ([Consumers](https://www.rabbitmq.com/docs/consumers)).
3. **Consumer priority on quorum queues with single active consumer** decides the active consumer
   from RabbitMQ 4.0 (§6.7).
4. **`Aspire.Hosting.RabbitMQ` 13.5.4** exists, matching the AppHost's Aspire version.
5. **Tests and the `build` lane's gate:** the live-test categories, the mixed-provider methods and
   the other skip sources are listed in §8.2.

**Live experiments for phase 0:**

1. Quorum queue requeue order after a dropped consumer connection, with single active consumer and
   prefetch 1, on the pinned RabbitMQ version.
2. The handover sequence in §6.7: cancel, lease release, the next consumer's activation, and the
   re-subscription with a lower priority.
3. What Service Bus does with an empty session id on a session-enabled subscription (§6.6).
4. `RabbitMQ.Client` 7.x publisher-confirm tracking and `mandatory` return handling, as §6.5's
   sending rule needs them.
5. Throughput of one quorum queue with single active consumer and prefetch 1, as the capacity
   baseline for choosing N.

The WebApp's `catch (CosmosException) when NotFound` paths (§2.6) are part of the 1e inventory.

## 16. References

- RabbitMQ: [Quorum queues](https://www.rabbitmq.com/docs/quorum-queues) (single active consumer,
  returned-message order, delivery limit, from 4.3 `basic.reject` and channel crashes count and
  `basic.nack` doesn't, at-least-once dead-lettering);
  [Consumers](https://www.rabbitmq.com/docs/consumers) (single active consumer failover, consumer
  priority on quorum queues, consumer timeout);
  [Alternate exchanges](https://www.rabbitmq.com/docs/ae);
  [Exchanges](https://www.rabbitmq.com/docs/exchanges);
  [AMQP concepts](https://www.rabbitmq.com/tutorials/amqp-concepts);
  [Confirms](https://www.rabbitmq.com/docs/confirms);
  [consistent hash exchange](https://github.com/rabbitmq/rabbitmq-server/tree/main/deps/rabbitmq_consistent_hash_exchange).
- Delayed-message plugin limitations: Tencent Cloud TDMQ,
  [delayed message limitations](https://intl.cloud.tencent.com/document/product/1112/60154), and the
  plugin's [README mirror](https://github.com/Sergi0Limit/rabbitmq-delayed-message-exchange)
  (secondary sources).
- [CloudEvents specification](https://github.com/cloudevents/spec/blob/main/cloudevents/spec.md):
  `source` plus `id` uniqueness.
- NimBus: [ADR-001](../../adr/001-session-based-ordering.md),
  [ADR-003](../../adr/003-separated-deferred-processor.md),
  [ADR-010](../../adr/010-pluggable-message-storage.md),
  [ADR-015](../../adr/015-customer-deployment-distribution.md),
  [ADR-018](../../adr/018-on-prem-rabbitmq-transport.md),
  `src/NimBus.ServiceBusEmulator/README.md`, `docs/versioning.md`.
- Earlier work: the September study (`docs/spec/on-prem-nimbus-feasibility.html`, added in
  `b29ff5bb`, removed in `71f2220a`; plan at
  [2026-09-14-on-prem-feasibility.md](../../plan/2026-09-14-on-prem-feasibility.md)).

## 17. Review trail

The investigation went through five revisions on 2026-10-21, each reviewed by Codex
([review file](../../plan/2026-10-21-spec-041-review.md)): revision 1 (rethink), revision 2
(rethink), revision 3 (sound after fixes), revision 4 (sound after fixes, five narrow findings,
fixed in revision 5). The main turns:

- Revision 2 tried to improve on Service Bus inside the transport (a synthetic blocker,
  in-partition replay) and to fan out in the sender. Both broke Core paths or missed publishers.
  Revision 3 switched to porting the Service Bus design (parity first).
- Revisions 3-5 completed the subscription matrix, the lease, the drain loop and the raw-publisher
  paths, and recorded three existing Core bugs.

This draft (revision 6) turns the investigation into a specification and adds:

- **The router** as a platform service. Revisions 3-5 ran forwarding in each endpoint host, which
  fails for publish-only processes (§2.5); the reviews didn't catch it.
- **Fair-share partition handover** across replicas (§6.7), since single active consumer alone
  gives failover but no scale-out.
- Repository and packaging (§6.2), the transport seam (§6.3), configuration (§6.4), the SQL schema
  (§6.14), provisioning (§6.15), the WebApp feature matrix (§6.16), security (§6.18), observability
  (§6.19), local development (§6.20), the CI split (§8), pull-request phasing (§10), open questions
  and facts to verify (§14, §15).
- ADR-018, which amends ADR-001.

Codex reviewed revision 6 (sound after fixes: 20 findings, 7 blocking). Revision 7 answers each:

| Finding | Change |
|---|---|
| Parking and scheduling through `ISender` can't carry the lease epoch (blocking) | Delivery scope read by the RabbitMQ `ISender`; closed on settlement (§6.8) |
| Ordinary sends had no confirm requirement (blocking) | Every RabbitMQ publish uses confirms and `mandatory`; `Send` throws on nack, return or timeout (§6.5) |
| `nb topology apply` activated routes before declaring queues (blocking) | Declare and check first, then switch the routing version in one transaction; cleanup after routers report the new version; partial-apply rules (§6.15) |
| WebApp gating had no API field, and controllers couldn't be built without Service Bus (blocking) | Two additive `ApplicationStatus` fields; Service Bus dependencies behind interfaces with 501-throwing RabbitMQ implementations (§6.16) |
| Reply permissions needed the default exchange and server-named queues (blocking) | Client-named reply queues on a `nimbus.reply` exchange; per-role patterns rewritten (§6.13, §6.18) |
| Mixed-provider `DataRow` methods can't be split by category (blocking) | 1a splits them (§8.2) |
| The nothing-skipped gate fails today (blocking) | 1a removes the always-inconclusive Service Bus instrumentation placeholder; other skip sources checked (§8.2) |
| Deferred reprocess also clears session state | `RowVersion` optimistic concurrency against the partition consumer (§6.9, §6.16) |
| Copy endpoint data is Cosmos DB-only | Its own row: unavailable on SQL Server in every phase (§6.16) |
| Aspire switch must replace the whole graph | The rabbitmq graph spelled out (§6.20) |
| Router outage behaviour | Degraded-state contract, readiness check, parity row, risk (§6.5, §6.19, §7, §12) |
| Phase 1 exit contradicted the fallback | Hosts need Service Bus until phase 3; exit criteria moved (§6.3, §6.17, §10) |
| Construction-site list missed WebApp and CLI | §2.6 lists them; the 1e inventory gates the migration (§6.3, §10) |
| `[Experimental]` calls fail the build | In-repo callers suppress `NIMBUS0041` per project (§6.3) |
| Coverage scope narrowed silently | Lanes upload Cobertura files; a `coverage` job merges them (§8.2) |
| Phase 2a could ship a partial package | Non-packable until 2g (§10) |
| §14 re-asked D1, D4 and D9; §15 had facts already checkable | §14 trimmed; §15 split into recorded facts and live experiments |
| ADR-018 overstated the in-memory transport; guidance said "allows" for a proposed ADR; ADR-002 contradicted | Fixed in ADR-018, §2.1 and the guidance files; ADR-002 and ADR-013 get notes |

Found while revising: tests read files under `docs/` (`BuildingAdaptersGuideTests`,
`RateLimitWiringTests`), so the optional docs-only CI filter would have skipped tests that guard
documentation. It is dropped (§8.2).
