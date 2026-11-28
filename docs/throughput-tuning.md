# Throughput Tuning

Which Azure Service Bus and NimBus parameters actually control throughput, what
NimBus sets today, and what to set them to for different workload shapes.

Read this when an endpoint is falling behind, when you are sizing a new adapter,
or before upgrading a namespace tier in the hope that it helps (often it does
not — the ceiling is usually client-side concurrency or session-key design).

- [The four layers](#the-four-layers)
- [What NimBus sets today](#what-nimbus-sets-today)
- [Layer 1 — Consumer concurrency](#layer-1--consumer-concurrency)
- [Layer 2 — Publisher](#layer-2--publisher)
- [Layer 3 — Entity and topology](#layer-3--entity-and-topology)
- [Layer 4 — Namespace tier](#layer-4--namespace-tier)
- [Workload profiles](#workload-profiles)
- [A tuning procedure that works](#a-tuning-procedure-that-works)
- [Symptoms and their causes](#symptoms-and-their-causes)
- [Full parameter reference](#full-parameter-reference)

---

## The four layers

Throughput is bounded by the *lowest* of these. Tune in this order — the cheap
layers are also the ones that usually bind.

| Layer | Dial | Change cost |
| --- | --- | --- |
| 1. Consumer concurrency | `MaxConcurrentSessions`, prefetch, replica count | Config / restart |
| 2. Publisher | Batching, client reuse, outbox dispatch rate | Code |
| 3. Entity + topology | Session-key cardinality, lock duration, filter type, partitioning | Re-provision (some are create-time only) |
| 4. Namespace tier | Standard → Premium, Messaging Units | Cost + migration |

The single most common real ceiling in NimBus is **session-key cardinality**,
not any of the numbers below. See
[Session cardinality is the real ceiling](#session-cardinality-is-the-real-ceiling).

---

## What NimBus sets today

### Consumer (Worker receiver)

`NimBusReceiverHostedService` builds a `ServiceBusSessionProcessor` from
[`NimBusReceiverOptions`](../src/NimBus.SDK/Hosting/NimBusReceiverOptions.cs):

| Setting | NimBus value | Notes |
| --- | --- | --- |
| `MaxConcurrentSessions` | `8` | The main throughput dial. Configurable. |
| `MaxAutoLockRenewalDuration` | `5 min` | Configurable. |
| `SessionIdleTimeout` | `30 s` | Configurable. |
| `AutoCompleteMessages` | `false` | Fixed — NimBus settles explicitly. |
| `MaxConcurrentCallsPerSession` | *not set* → **1** | Deliberate: it is what makes per-session ordering hold ([ADR-001](adr/001-session-based-ordering.md)). Do not raise it. |
| `PrefetchCount` | `0` | Configurable. Opt-in — off by default because prefetch hurts slow-handler workloads. |

### Consumer (Azure Functions)

Concurrency comes from `host.json`, not from NimBus — see
[azure-functions-hosting.md](azure-functions-hosting.md). The same dials are
available there under different names: `maxConcurrentSessions`,
`prefetchCount`, `sessionIdleTimeout`, `maxAutoLockRenewalDuration`.

### Deferred processor

[`DeferredMessageProcessorHostedServiceOptions`](../src/NimBus.SDK/Hosting/DeferredMessageProcessorHostedServiceOptions.cs)
pins `MaxConcurrentCalls = 1`. That is the replay path's *only* ordering
mechanism. Raising it trades ordered replay for throughput — a real option for
endpoints that do not need ordered replay, but make it a conscious decision.

#### Hosting it yourself

`AddNimBusDeferredProcessorHostedService` registers one deferred processor per
process. A host that runs several endpoints in one process (the WebApp traffic
simulator does) can construct `DeferredMessageProcessorHostedService` directly,
one instance per endpoint, and call `StartAsync` / `StopAsync` itself:

```csharp
var deferred = new DeferredMessageProcessorHostedService(
    serviceBusClient,
    new DeferredMessageProcessor(serviceBusClient),
    new DeferredMessageProcessorHostedServiceOptions(TopicName: endpoint, SubscriptionName: "deferredprocessor"),
    loggerFactory.CreateLogger<DeferredMessageProcessorHostedService>());
await deferred.StartAsync(cancellationToken);
```

The constructor rejects a blank topic or subscription and a
`MaxConcurrentCalls` below 1. Leave `MaxConcurrentCalls` at 1 unless the
endpoint tolerates out-of-order replay: the trigger subscription is
non-session, so single concurrency is its only ordering mechanism.

### Topology (`ServiceBusTopologyProvisioner`)

| Entity | Setting | Value |
| --- | --- | --- |
| Topic | `SupportOrdering` | `true` |
| Topic | `EnableBatchedOperations` | `true` |
| Topic | `MaxSizeInMegabytes` | `5120` (omitted on the emulator) |
| Topic | `DuplicateDetectionHistoryTimeWindow` | `10 min` — **inert**: `RequiresDuplicateDetection` is never set, so duplicate detection is off. No write-path cost, and no dedup either. Idempotent handlers or the [inbox pattern](inbox-pattern.md) remain your dedup story. **Leave it off.** Since 3.7.0 a rescheduled or replayed copy keeps its original `MessageId` so the tracking store can recognise it as already answered; with duplicate detection enabled the broker would silently drop that copy inside the detection window and the audit update would be lost. Brownfield namespaces must be checked before deploying. |
| Subscription | `MaxDeliveryCount` | `10` |
| Subscription | `LockDuration` | `30 s` |
| Subscription | `EnableBatchedOperations` | `true` |
| Subscription | `EnableDeadLetteringOnFilterEvaluationExceptions` | `true` |
| Subscription | `RequiresSession` | Per endpoint definition |
| Topic | `EnablePartitioning` | **Never set** → non-partitioned |

Topics are created **non-partitioned**. On Standard that caps a single topic at
one broker's throughput. Partitioning is create-time only, so a partitioned
topic means a new entity, not an update.

### Client options

NimBus does **not** construct the `ServiceBusClient` for adapters — your host
registers it (`AddNimBusPublisher`/`AddNimBusReceiver` resolve it from DI). So
`ServiceBusClientOptions` — `TransportType`, `RetryOptions.TryTimeout`,
`MaxRetries`, `Mode` — are entirely yours to set, and default if you don't.

---

## Layer 1 — Consumer concurrency

### `MaxConcurrentSessions` (Worker) / `maxConcurrentSessions` (Functions)

The number of *sessions* processed in parallel per host. Per-session ordering is
untouched; this only widens parallelism *across* sessions.

Starting point: `MaxConcurrentSessions ≈ target messages/sec × average handler
seconds`, then round up ~50% for headroom. A handler averaging 200 ms that must
sustain 100 msg/s needs ~20 concurrent sessions.

Raise it until one of these binds, then stop:

- host CPU > ~70%
- the downstream dependency (SQL, ERP API) starts throttling or its p95 climbs
- distinct active sessions run out — the hard ceiling below

```csharp
builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "CrmEndpoint";
    opts.SubscriptionName = "CrmEndpoint";
    opts.MaxConcurrentSessions = 32;
    opts.MaxAutoLockRenewalDuration = TimeSpan.FromMinutes(5);
});
```

### Session cardinality is the real ceiling

Effective parallelism is `min(MaxConcurrentSessions, distinct active session
IDs)`. `MaxConcurrentSessions = 200` against three session IDs gives you
**three** concurrent messages, and no amount of scale-out or tier upgrade
changes that.

Design the session key for the *narrowest* scope that still preserves the
ordering you actually need:

| Session key | Cardinality | Use when |
| --- | --- | --- |
| Per aggregate/entity (`customerId`, `orderId`) | High — good | The default. Ordering matters per entity, not globally. |
| Per tenant | Medium — often too low | A big tenant becomes a serial hot spot. |
| Per endpoint / constant | 1 — a serial pipe | Only when the endpoint genuinely must be strictly serial. |

A hot session (one entity producing a large share of traffic) serializes that
share regardless of every other setting. Look for it before touching any dial.

### `SessionIdleTimeout`

How long a receiver holds an idle session before rotating. With many short-lived
sessions, the default 30 s pins slots on drained sessions and starves waiting
ones. Drop to 5–10 s for high-cardinality/short-session workloads. Keep it
higher when sessions are long-lived and chatty, to avoid churning session locks.

### `MaxAutoLockRenewalDuration`

Not a throughput dial — it prevents lock loss from causing redelivery. Set above
your p99 handler duration (including retries within the handler). Too low and
slow messages get reprocessed, which costs throughput indirectly.

### Prefetch

Prefetch pulls messages into memory ahead of processing, removing a broker
round-trip per message. It is the biggest single win for small, fast messages.
It defaults to `0` (off) on both hosts, because on the wrong workload it makes
throughput *worse* — see the lock caveat below.

```csharp
builder.Services.AddNimBusReceiver(opts =>
{
    opts.TopicName = "CrmEndpoint";
    opts.SubscriptionName = "CrmEndpoint";
    opts.MaxConcurrentSessions = 32;
    opts.PrefetchCount = 320;   // ~10x concurrency; fast handlers only
});
```

- Starting point: `PrefetchCount ≈ concurrency × 10–20`, or roughly the number
  of messages consumed in 2–3 × average handler time.
- Set **0** when handlers are slow (> ~1 s). Prefetched messages hold locks and
  their lock timers run while queued locally, so with slow handlers they expire
  and get redelivered — prefetch makes throughput *worse*.
- Set 0 when messages are large; prefetch multiplies memory.

Because prefetch interacts with lock expiry, raise it and
`MaxAutoLockRenewalDuration`/`LockDuration` together, and watch redelivery
(`nimbus.message.received` climbing faster than `nimbus.message.processed`)
after the change. The effective value is logged at receiver startup.

### Scale out vs scale up

One process with 200 concurrent sessions and one CPU core is worse than four
processes with 50 each. Raise concurrency until CPU is the limit, then add
replicas. Both scale the same session pool — sessions are distributed across
competing receivers automatically.

Share **one** `ServiceBusClient` per namespace across all senders, receivers,
and processors in the host. The client multiplexes over one AMQP connection;
constructing several multiplies connections without adding throughput.

---

## Layer 2 — Publisher

- **Batch.** `ISender.Send(IEnumerable<IMessage>)` sends in one call, which is
  worth roughly 5–10× over per-message sends on send-heavy paths. Caveat: NimBus
  passes the whole list to `SendMessagesAsync` without size-chunking
  ([`Sender.cs`](../src/NimBus.ServiceBus/Sender.cs)), so the Service Bus SDK
  throws if the batch exceeds the entity's max message size. Chunk large sets
  yourself (~100 messages, or below 256 KB Standard / 100 MB Premium total).
- **Reuse the client and sender.** Senders are cached per entity; do not create
  one per publish.
- **Payload size dominates.** Above a few hundred KB, use a claim-check (blob
  reference in the message) instead of an inline payload.
- **Outbox.** The [outbox](building-adapters.md) trades publish latency for
  atomicity. `AddNimBusOutboxDispatcher(pollingInterval, batchSize)` defaults to
  **1 s / 100**. The dispatcher drains greedily — a full batch re-polls
  immediately without waiting
  ([`OutboxDispatcherHostedService.cs:46`](../src/NimBus.SDK/Hosting/OutboxDispatcherHostedService.cs)) —
  so the interval only governs *idle* latency, not sustained rate. Raise
  `batchSize` (250–500) for high-volume publishing; lower `pollingInterval`
  (200–500 ms) only if publish latency at low volume matters, and note it costs
  a store query per tick.
  With a claiming outbox (SQL Server) and the Service Bus sender, each session's
  consecutive rows for one destination go out as one `ServiceBusMessageBatch`
  with one checkpoint, so throughput grows with the rows per session in a
  batch; rows spread over many sessions gain less. The harness in
  [`OutboxDispatchThroughputBenchmark.cs`](../tests/NimBus.Core.Tests/OutboxDispatchThroughputBenchmark.cs)
  compares per-row and batched dispatch.

---

## Layer 3 — Entity and topology

| Parameter | Effect | Guidance |
| --- | --- | --- |
| `LockDuration` (30 s) | Too short → locks expire mid-handler → redelivery storms that eat throughput | Raise toward 5 min for slow handlers, or rely on auto lock renewal. Max 5 min. |
| `MaxDeliveryCount` (10) | Poison messages retry 10× before DLQ, burning capacity | Lower to 3–5 for endpoints with a strong permanent-failure classification ([error-handling.md](error-handling.md)). |
| Filter type | SQL filters are evaluated per message per subscription and cost real CPU | Prefer correlation filters where the routing is an equality match — dramatically cheaper at high volume. |
| `EnablePartitioning` | Spreads one entity across brokers — the main way to lift a *single topic's* ceiling | Create-time only, and not set by the NimBus provisioner. On Premium, use namespace partitions instead. |
| `RequiresDuplicateDetection` | A write-path index on every send | Off today. Leave it off; dedup on the consumer side with the [inbox](inbox-pattern.md). |
| `ForwardTo` (auto-forward) | Each hop adds latency and an operation | Fine for the NimBus handoff paths; avoid deep chains on hot paths. |
| Subscription count per topic | Every subscription gets a copy — throughput cost is per subscription | Fan-out multiplies broker work; watch it when adding subscribers to a hot topic. |

---

## Layer 4 — Namespace tier

- **Standard** shares throughput across tenants and *will* throttle under load —
  the signal is `ServiceBusFailureReason.ServiceBusy` / HTTP 503 with retries in
  the client. No client-side setting fixes sustained throttling.
- **Premium** gives dedicated Messaging Units (1/2/4/8/16), predictable latency,
  no throttling, and a 100 MB message limit. This is the fix for a genuinely
  throughput-bound namespace, and the only tier worth benchmarking against.
- Premium scales two ways: **Messaging Units** (vertical) and **namespace
  partitions** (horizontal, set at namespace creation).
- Geo-replication and zone redundancy add resilience, not throughput.

The NimBus topology is tier-agnostic
([azure-requirements.md](azure-requirements.md)) — moving to Premium needs no
code change.

---

## Workload profiles

Starting points, not benchmarks. Measure, then adjust — see
[the tuning procedure](#a-tuning-procedure-that-works).

### A. High-volume ingest — fast handler, ordering per entity

Telemetry, change feeds, IoT-shaped events. Handler < 50 ms, small payloads,
high session cardinality.

| Parameter | Value |
| --- | --- |
| `MaxConcurrentSessions` | 100–200 (Functions), 64–128 (Worker) |
| `PrefetchCount` | 200–500 |
| `SessionIdleTimeout` | 5–10 s |
| `LockDuration` | 30 s (default) |
| `MaxAutoLockRenewalDuration` | 1–2 min |
| `MaxDeliveryCount` | 3–5 |
| Session key | Per entity — cardinality in the thousands |
| Tier | Premium; partition the topic if a single topic saturates |
| Scale | 3+ replicas, then widen concurrency |

### B. Ordered business events — the typical NimBus adapter

CRM/ERP integration, handler 100–500 ms, moderate volume, ordering matters per
customer/order.

| Parameter | Value |
| --- | --- |
| `MaxConcurrentSessions` | 16–32 |
| `PrefetchCount` | 0–50 |
| `SessionIdleTimeout` | 30 s (default) |
| `LockDuration` | 30 s–1 min |
| `MaxAutoLockRenewalDuration` | 5 min (default) |
| `MaxDeliveryCount` | 10 (default) |
| Session key | Per aggregate |
| Tier | Standard is usually fine; Premium for predictable latency |
| Scale | 2 replicas for availability; widen concurrency first |

This is what the NimBus defaults (`8`) target, conservatively. `16–32` is the
usual first move for a busy adapter.

### C. Slow external I/O — rate-limited upstream

ERP APIs, SOAP endpoints, anything where the handler waits seconds and the
downstream has its own quota.

| Parameter | Value |
| --- | --- |
| `MaxConcurrentSessions` | 4–16 — **match the upstream's concurrency budget, not the bus's** |
| `PrefetchCount` | **0** — prefetched locks would expire while queued |
| `SessionIdleTimeout` | 30–60 s |
| `LockDuration` | 5 min, or rely on lock renewal |
| `MaxAutoLockRenewalDuration` | 10+ min (above p99 handler time) |
| `MaxDeliveryCount` | 3–5, with permanent-failure classification |
| Tier | Not the bottleneck — do not upgrade for this |
| Scale | Do not scale out past the upstream's limit; queue depth is the buffer, that is the point |

Here the bus is deliberately *not* the constraint. Adding concurrency converts a
healthy backlog into upstream 429s and retry storms. A custom throttling
[pipeline behavior](pipeline-middleware.md) is a better lever than a bigger
concurrency number.

**The Resolver is profile C.** Its limit is the Cosmos RU budget, not the bus:
every event produces two Resolver messages (the request copy and the response),
and each ends in Cosmos writes. It ships at `maxConcurrentSessions = 16` (a
template-owned app setting in `deploy/bicep/deploy.core.bicep`, tunable with
`nb setup --resolver-max-sessions`) and `prefetchCount = 0` in `host.json`.
It deliberately uses a 1 s `SessionIdleTimeout` instead of profile C's 30–60 s:
the Resolver sees thousands of per-aggregate sessions, and a session slot is
held for handler time plus idle timeout per message, so its throughput ceiling
is roughly `sessions × instances / (t_handler + idle)`. With a 30 s idle timeout
that ceiling collapses to a fraction of a message per second per slot; with 1 s
the slot rotates to the next waiting session almost immediately.

### D. Low-volume control plane — latency-sensitive

Commands, request/reply, admin operations. A few messages per second, latency
visible to a user.

| Parameter | Value |
| --- | --- |
| `MaxConcurrentSessions` | 4–8 (default is fine) |
| `PrefetchCount` | 0 — with low volume it adds latency, not throughput |
| `SessionIdleTimeout` | 5–10 s (faster rotation → lower time-to-first-message) |
| `RetryOptions.TryTimeout` | 10–20 s instead of the 60 s default, so a wedged operation fails fast |
| Tier | Premium if p99 latency is a requirement — Standard's tail is noisy |
| Scale | 2 replicas for availability |

Note: request/reply creates and disposes a session receiver per request and is
not built for high throughput ([sdk-api-reference.md](sdk-api-reference.md)).

### E. Backfill / replay bursts

Migrations, catch-up after an outage, bulk resubmit from the WebApp.

| Parameter | Value |
| --- | --- |
| `MaxConcurrentSessions` | Temporarily 2–4× the steady-state value |
| `PrefetchCount` | High (500+) — replay is the ideal prefetch case |
| `MaxDeliveryCount` | Keep default; a backfill hitting poison data should DLQ, not spin |
| Deferred processor `MaxConcurrentCalls` | Consider > 1 **only if** ordered replay is not required |
| Publisher | Always batch — this is where chunked batch sends pay off most |
| Outbox `batchSize` | 250–500 if the backfill publishes through the outbox |
| Scale | Scale out for the duration, then scale back |

Run backfills on a dedicated endpoint or replica set when possible, so the burst
does not starve steady-state traffic sharing the same session pool.

### F. Fan-out — one topic, many subscriptions

| Parameter | Value |
| --- | --- |
| Filters | Correlation filters, not SQL, on every subscription |
| Subscriptions | Every subscription is a full copy — cost scales linearly |
| Tier | Premium once a hot topic exceeds ~10 subscriptions at volume |
| Per-subscriber concurrency | Tune independently; slow subscribers must not dictate fast ones |

Slow subscribers do not block fast ones, but they do accumulate backlog on their
own subscription and contribute to topic size. Watch per-subscription depth, not
just topic depth.

---

## A tuning procedure that works

1. **Measure first.** Get the baseline: messages/sec, handler p50/p95/p99,
   backlog growth rate, active session count.
2. **Find the binding constraint.** Handler duration? Downstream latency?
   Session cardinality? Broker throttling? Only one of them is binding — the
   others are noise.
3. **Change one dial.** Concurrency first; it is free and reversible.
4. **Re-measure for at least 10 minutes** under representative load. Short runs
   measure warm-up, not throughput.
5. **Stop when the next constraint appears.** Over-tuning past the downstream's
   capacity converts a queue into a retry storm.

### What to watch

NimBus emits these OpenTelemetry meters
([`NimBusMeters.cs`](../src/NimBus.Core/Diagnostics/NimBusMeters.cs), see
[testing.md](testing.md) for wiring):

| Metric | Reads as |
| --- | --- |
| `nimbus.message.process.duration` | Handler cost — the input to every concurrency calculation |
| `nimbus.message.queue_wait` | Time in the broker before consumer entry. **Rising = under-provisioned consumers.** The clearest under-capacity signal. |
| `nimbus.message.e2e_latency` | What the business actually experiences |
| `nimbus.message.received` vs `nimbus.message.processed` | Divergence = in-flight backlog building |
| `nimbus.message.publish.duration` | Publish-side cost; spikes here mean broker throttling |
| `nimbus.outbox.dispatch.duration` | Outbox dispatcher keeping up or not |

Alongside them, from Azure Monitor on the namespace: **active message count**
(backlog), **throttled requests** (tier ceiling), **server errors**, and
**CPU/memory** on Premium namespaces.

---

## Symptoms and their causes

| Symptom | Likely cause | Fix |
| --- | --- | --- |
| Backlog grows, host CPU low, downstream idle | Session cardinality too low, or a hot session | Re-key sessions to a narrower scope |
| Backlog grows, `MaxConcurrentSessions` already high | Handler is slow, or upstream throttles | Profile the handler; do not raise concurrency |
| Messages redelivered without handler errors | Lock expiry — slow handlers, or prefetch holding locks | Raise `LockDuration` / lock renewal; drop prefetch to 0 |
| `ServiceBusBusy` / 503 on send | Standard tier throttling | Batch sends; move to Premium |
| Throughput does not improve after scaling out | Session pool exhausted, or downstream is the limit | Check distinct active sessions; check downstream p95 |
| Worker "processes one message at a time" | `MaxConcurrentSessions = 1`, or a single session ID | Both are worth checking — the second is more common |
| DLQ fills during a load spike | `MaxDeliveryCount` exhausted by lock expiry, not by real failures | Fix lock duration first; do not raise `MaxDeliveryCount` |
| Latency fine at low volume, terrible at high | Prefetch 0 with small fast messages, or SQL filters at volume | Add prefetch; convert filters to correlation |

---

## Full parameter reference

| Parameter | Where | Default | NimBus | Throughput impact |
| --- | --- | --- | --- | --- |
| `MaxConcurrentSessions` | `NimBusReceiverOptions` / `host.json` | 8 (NimBus) | Configurable | **High** — primary dial |
| `MaxConcurrentCallsPerSession` | Processor options | 1 | Fixed at 1 | High, but breaks ordering |
| `PrefetchCount` | `NimBusReceiverOptions` / `host.json` | 0 | Configurable | **High** for small fast messages |
| `SessionIdleTimeout` | `NimBusReceiverOptions` | 30 s | Configurable | Medium — session rotation |
| `MaxAutoLockRenewalDuration` | `NimBusReceiverOptions` | 5 min | Configurable | Indirect — prevents redelivery |
| Replica count | Host platform | — | — | **High** — horizontal |
| Batch send | `ISender.Send(IEnumerable<>)` | — | Available, unchunked | **High** on send-heavy paths |
| `TransportType` | `ServiceBusClientOptions` | AmqpTcp | Host's choice | Medium — keep AmqpTcp |
| `RetryOptions.TryTimeout` | `ServiceBusClientOptions` | 60 s | Host's choice | Medium — tail latency |
| `LockDuration` | Subscription | 30 s (NimBus) | Provisioner | Indirect — redelivery |
| `MaxDeliveryCount` | Subscription | 10 (NimBus) | Provisioner | Indirect — wasted capacity |
| Filter type | Subscription rules | SQL | Platform definition | Medium at volume |
| `EnablePartitioning` | Topic (create-time) | off | Not set | **High** for a single hot topic |
| `RequiresDuplicateDetection` | Topic (create-time) | off | Not set | Negative if enabled |
| Session key design | Your event contract | — | — | **Highest** — the real ceiling |
| Tier / Messaging Units | Namespace | Standard | Deployment choice | **High** once throttled |
| Deferred `MaxConcurrentCalls` | Deferred processor options | 1 | Fixed at 1 by default | High for replay, breaks ordered replay |
| Outbox `batchSize` | `AddNimBusOutboxDispatcher` | 100 | Configurable | Medium — outbox publish rate |
| Outbox `pollingInterval` | `AddNimBusOutboxDispatcher` | 1 s | Configurable | Idle publish latency only (drains greedily) |

---

## Related

- [ADR-001 — Session-based ordering](adr/001-session-based-ordering.md) — why
  per-session concurrency is pinned at 1
- [Building Adapters](building-adapters.md) — receiver setup and the production
  checklist
- [Azure Functions Hosting](azure-functions-hosting.md) — `host.json`
  concurrency settings
- [Error Handling](error-handling.md) — failure classification, which keeps
  retries from eating throughput
- [Deferred Messages](deferred-messages.md) — session blocking and the replay
  path
- [Consumer Inbox](inbox-pattern.md) — deduplication without broker-side
  duplicate detection
- [Azure Requirements](azure-requirements.md) — tier and namespace inventory
