# Spec 027 — NimBus Service Bus Emulator

| | |
|---|---|
| **Status** | Draft for implementation handover |
| **Date** | 2026-08-09 |
| **Depends on** | Azure.Messaging.ServiceBus 7.20.1 (repo pin; floor for emulator admin support). Compatibility is *claimed* only for 7.20.x, which the research verified (7.20.2) and TST-3 pins; newer SDK versions must pass TST-3 before the claim extends. Aspire 13.4.6, .NET 10 |
| **Deliverables** | `src/NimBus.ServiceBusEmulator/`, `src/NimBus.ServiceBusEmulator.AspireHosting/`, `tests/NimBus.ServiceBusEmulator.Tests/`, AppHost integration |

A local, in-process Azure Service Bus emulator that the **unmodified** Azure Service Bus SDK talks to, so the whole NimBus stack (provisioner, SDK hosts, Resolver, WebApp, CLI) runs under Aspire with **zero NimBus code changes**. It implements exactly the Service Bus surface NimBus uses — nothing more — and replaces the official Microsoft emulator, whose two blocking limitations are documented below.

Feasibility is **already validated**: a prototype AMQP.Net Lite broker completed a full `SendMessageAsync` round-trip (SASL → CBS → attach → transfer → disposition) against the stock 7.20.x SDK during the research for this spec. The non-obvious wire details discovered there are captured as hard requirements in §6–§7.

---

## 1. Motivation

NimBus local development today needs a **real Azure namespace** (user-secret connection string) for the main AppHost. The `CrmErpDemo` sample can run the official Microsoft emulator (`mcr` image 2.0.0) behind a flag, and that experience exposed the exact problems this spec eliminates:

1. **No usable admin plane.** The official emulator's HTTP admin endpoint lives on a separate container port (5300), which the SDK's connection-string URL synthesis never finds — so `ServiceBusAdministrationClient` needs a second, hand-built connection string, and Aspire doesn't expose it (aspire#14041). Consequences in this repo:
   - `ServiceBusTopologyProvisioner` cannot run → the demo AppHost **skips the provisioner entirely** in emulator mode (`samples/CrmErpDemo/CrmErpDemo.AppHost/Program.cs:104-107`) and pre-declares topology via a static `UserConfig` JSON (`EmulatorTopologyConfigBuilder.cs`, 376 lines) that must stay byte-identical with the provisioner — a second source of truth the code itself flags as a risk.
   - Every WebApp operator surface backed by `IServiceBusManagement` (topic/subscription listing, runtime counts, pause/resume, purge, rebuild — `docs/service-bus-subscription-admin.md`) degrades or breaks. This is the largest untested surface in local dev.
2. **Warm-up flakiness.** Emulator 2.0.0 drops AMQP connections and throws `MessagingEntityNotFound` during warm-up (documented in `samples/CrmErpDemo/e2e/demo/README.md:16-17` and `docs/demo-video-script.md:31`); the demo-video harness must use a real namespace because of it.
3. **Operational weight.** Two containers (emulator + SQL Server 2022 backing store), static topology requiring container restart on change, 100 MB entity cap (provisioner wants 5120), 50-entity quota, a 1-hour max TTL, no persistence, no official support. (Note on TTL: this emulator imposes **no** TTL cap, but NimBus's own `IsEmulator` detection keys off `UseDevelopmentEmulator=true` and still *provisions* the 1-hour `Deferred` TTL locally — a retained, acceptable local-dev behavior, see ASP-3.)

A purpose-built emulator running as a plain .NET Aspire project resource fixes all three: one process, one connection string, both planes on one port, the provisioner runs unchanged, and topology/TTL/quota constraints disappear.

### Why the SDK cooperates

Since 7.20.1, both SDK clients are emulator-aware off a single connection string:

- `UseDevelopmentEmulator=true` → `ServiceBusClient` uses **plain TCP** (no TLS) and honors a **custom port** in the endpoint (verified empirically against 7.20.2; the "port is fixed at 5672" claim in emulator-installer issue #139 is false for the current .NET SDK).
- `ServiceBusAdministrationClient` switches to **plain HTTP** on the same host/port when the flag is present (7.20.1 changelog fixed custom-port handling).

Both planes derive host *and port* from the same `Endpoint=` value, so a broker that serves AMQP and HTTP **on one TCP port** needs no second connection string. That is the cornerstone decision of this design (§5.1).

---

## 2. Goals and non-goals

### Goals

- **G1** — The unmodified Azure SDK (data + admin planes) works against the emulator with a single connection string: `Endpoint=sb://localhost:{port};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=<any>;UseDevelopmentEmulator=true`.
- **G2** — Zero changes to NimBus product code. `ServiceBusTopologyProvisioner.ApplyAsync` provisions the full topology at runtime; a second apply is a **zero-churn no-op** (the byte-identical rule round-trip invariant, §7.1).
- **G3** — Full NimBus feature coverage: session-ordered processing, rules with SET actions, auto-forwarding, request/reply, NimBus-level deferral (the `Deferred` subscription flow — plain routing, no SB defer), scheduling, purge/drain, subscription admin, runtime counts.
- **G4** — First-class Aspire resource: `AddNimBusServiceBusEmulator()` in the main AppHost, provisioner **not** skipped, WebApp admin screens fully functional locally.
- **G5** — Deterministic and fast: no warm-up flakiness, instant start, suitable for e2e tests (Playwright suites, `07-agent-enrichment` and future ones).

### Non-goals (out of scope permanently unless revisited)

- General AMQP transactions / send-via. The emulator supports only the narrow
  Resolver replay shape: complete one locked regular-DLQ message and send one
  replacement to a topic on the same connection, committed or rolled back together.
- Duplicate detection **enforcement** (`RequiresDuplicateDetection` is never set; the `DuplicateDetectionHistoryTimeWindow` property must merely round-trip).
- Partitioned entities, geo-DR, autoscale, quotas, large-message support beyond the configured max, JMS, AMQP WebSockets (NimBus never sets `TransportType`; SDK default is AmqpTcp).
- Entra ID / real SAS signature validation (accept-all auth; see §6.2, §8.4).
- SB message deferral (`receive-by-sequence-number`, defer disposition, deferred peek state) — dead code in NimBus, rejected loudly; see §3.
- Performance/load testing fidelity. Correct under concurrency, not benchmarked.
- A management UI. The NimBus WebApp *is* the management UI and it works against this emulator.

### Explicitly deferred (P2 — implement only if needed)

- Queue entities (NimBus is 100 % topics/subscriptions; the ATOM endpoints for queues may return 501 initially — but see Open Question OQ-2).
- `CorrelationRuleFilter` (zero occurrences in `src/`).
- Transfer-DLQ browsing. Regular subscription DLQ receive links are supported for Resolver replay; see BRK-5.
- AMQP rule management ops (`add-rule`/`remove-rule`/`enumerate-rules` — only `ServiceBusRuleManager` uses them; NimBus does rule CRUD over HTTP).
- `batch-delete-messages`, `get-message-sessions` management ops.
- SQL Server / SQLite persistence providers (§9).

---

## 3. Resolved: the SB defer API is dead code — out of scope

The `DeferMessageAsync`/`ReceiveDeferredMessage*` call sites that exist in `src/` are **not reachable from any live flow**; NimBus's deferral is its own mechanism (regular messages routed to the `Deferred` subscription and replayed by `DeferredMessageProcessor` — no SB defer involved). Verified on current `master`:

- **Write path dead.** The blocked-session flow calls `DeferMessageToSubscription` (`StrictMessageHandler.cs:206` → `SendToDeferredSubscription` + `Complete`). The private `DeferMessage` wrapper (`StrictMessageHandler.cs:492`) is the *only* production caller of `IMessageContext.Defer` (the real SB API, `MessageContext.cs:319` → `ServiceBusSession.cs:107-115`) and is itself **never called**; `DeferOnly` has zero production callers. Only unit tests exercise them.
- **Read paths are legacy-drain only.** `ReceiveNextDeferred(WithPop)` and `IsSessionBlocked` iterate `SessionState.DeferredSequenceNumbers`, whose only writer is the dead `Defer` (`MessageContext.cs:325`). On a fresh environment the list is always empty, so `ReceiveDeferredMessagesAsync` never hits the wire.
- **Admin/purge/CLI `State == Deferred` branches** (`AdminService.Purge.cs`, `SubscriptionAdminService.cs:410`, `Endpoint.cs`) are defensive compatibility for legacy state in real Azure namespaces. A fresh emulator namespace can never contain an SB-deferred message, so these branches simply never trigger there.

**Consequences for the emulator** (all requirements formerly tagged `[DEFER]` are removed):

- No deferred-message store. `receive-by-sequence-number` → `statusCode` 501. `update-disposition` supports `completed`/`abandoned`/`suspended` only; the defer disposition (`modified` with `undeliverable-here=true`) and a `"defered"` disposition-status are rejected with a clear error naming this section — so any future reintroduction of SB defer in NimBus fails loudly instead of silently misbehaving.
- Peek returns active messages only; `x-opt-message-state` may be omitted (SDK default is `Active`), keeping all purge-path `State` branches on their no-op arms.

**Follow-up recommendation for NimBus** (separate change, not part of this spec): mark the dead surface — `IMessageContext.Defer`/`DeferOnly`/`ReceiveNextDeferred*`/`RestoreNextDeferred` and `ServiceBusSession`'s defer members — `[Obsolete]` per repo convention, so the emulator's 501s are unreachable by construction.

---

## 4. Required surface — what NimBus actually uses

This section is the contract. Anything not listed here is intentionally unimplemented (fail loud: reject unknown operations with a clear `NotSupported` error naming this spec).

### 4.1 Data plane (all clients constructed with **default options** — no `ServiceBusClientOptions` anywhere in `src/`)

| SDK API | NimBus usage |
|---|---|
| `ServiceBusClient` ctor (conn-string or FQNS+credential) | WebApp `Startup.cs:405,410`, Resolver, CLI. Emulator path is always conn-string |
| `CreateSender(topic)` → `SendMessageAsync`, `SendMessagesAsync(IList)` | Topics only, never queues. No `CreateMessageBatchAsync`/`ServiceBusMessageBatch` — NimBus pages client-side (64 000-byte budget, `PublisherClient.cs:43`) and sends plain lists |
| `ScheduleMessageAsync` → seq no; `CancelScheduledMessageAsync(seq)` | `Sender.cs:30,33`, throttle redelivery `ServiceBusSession.cs:215` |
| `CreateSessionProcessor(topic, sub, opts)` | THE consumer path. SDK hosts: `MaxConcurrentSessions=8`, `MaxAutoLockRenewalDuration=5m`, `SessionIdleTimeout=30s`, `PrefetchCount=0`, `AutoCompleteMessages=false`, `MaxConcurrentCallsPerSession` unset (=1). ⚠ The **Functions Resolver requests `maxConcurrentSessions: 200`** (`src/NimBus.Resolver/host.json:12`) — SEC-3's bounds must comfortably carry hundreds of concurrent session links on one connection |
| `CreateProcessor(topic, sub)` non-session | DeferredProcessor trigger only, `MaxConcurrentCalls=1`, `AutoCompleteMessages=false` |
| `AcceptSessionAsync(topic, sub, sessionId)` | Always explicit id from app code; **`AcceptNextSessionAsync` never called by NimBus code** — but the **session processor calls it internally**, so next-available accept is P0 |
| `CreateReceiver(topic, sub)` | Peek/drain paths; **peeks session-enabled subscriptions from a non-session receiver** (`SubscriptionAdminService.cs:344`) |
| `ReceiveMessagesAsync(100, 5s)` / `ReceiveMessageAsync(timeout)` | Drain loops; reply receive `PublisherClient.cs:286` |
| `PeekMessagesAsync(100[, fromSequenceNumber])` | Purge/drain/CLI; paginates `last.SequenceNumber + 1`; purge paths branch on `State`, which is always `Active` here (§3) |
| Settlement | `Complete` (4 SDK surfaces: receiver, session receiver, `ProcessMessageEventArgs`, Functions `ServiceBusMessageActions`), `Abandon` (never with properties), `DeadLetter(reason, description≤4096)`. Defer/receive-deferred: dead code, excluded (§3) |
| Lock renewal | Never explicit — **only** the processors' auto-renewal (`renew-lock` / `renew-session-lock` management ops fire on the wire) |
| Session state | `GetSessionStateAsync` / `SetSessionStateAsync` — UTF-8 JSON payload; cleared with **empty `BinaryData`** on the Functions path and **null** on raw-receiver paths; null-or-empty read = fresh state |
| Message fields written | `Body`, `MessageId`, `SessionId`, `CorrelationId`, `ReplyTo`, `ReplyToSessionId`, `ContentType` (CloudEvents), `TimeToLive` (replies: 5 min), `Subject`+`To`+`PartitionKey`+`TransactionPartitionKey` (throttle-redelivery clone only), **`ScheduledEnqueueTime` — always set, usually "now"** → a past/now value must mean *immediate* enqueue |
| Application properties | ~25 keys from `UserPropertyName` + `ThrottleRetryCount`, `ReplyStatus`, `ErrorType`, `ErrorText`, `cloudEvents:*`/`ce-*`, `traceparent`/`tracestate`. Types are **mixed string/int/long and must round-trip** (readers call `?.ToString()`) |
| Received fields read | `LockToken`, `SessionId`, `Body`, `MessageId`, `CorrelationId`, `DeliveryCount`, `SequenceNumber`, `EnqueuedTime`, `ContentType`, `ReplyTo`, `ReplyToSessionId`, `ApplicationProperties`, `State`, `PartitionKey`, `TransactionPartitionKey`, `TimeToLive`, `Subject`, `To`. (`LockedUntil`, `ExpiresAt`, `DeadLetterSource`, SDK `DeadLetterReason` never read) |
| Error contract | See §7.3 — specific `ServiceBusFailureReason`s are pattern-matched and load-bearing |

### 4.2 Admin plane (`ServiceBusAdministrationClient`, default options)

| SDK API | NimBus usage |
|---|---|
| `CreateTopicAsync` | `SupportOrdering=true`, `DuplicateDetectionHistoryTimeWindow=10m`, `EnableBatchedOperations=true`, `MaxSizeInMegabytes=5120` (skipped under emulator detection — our emulator should simply accept it, making the branch moot) |
| `CreateSubscriptionAsync` | `MaxDeliveryCount=10`, `LockDuration=30s`, `EnableBatchedOperations=true`, `EnableDeadLetteringOnFilterEvaluationExceptions=true`, `RequiresSession`, conditional `ForwardTo`, conditional `DefaultMessageTimeToLive` on `Deferred` — **14 d is the production value; the effective locally-provisioned value is 1 h** because NimBus's own emulator detection fires (ASP-3) |
| `CreateRuleAsync` | `SqlRuleFilter` + optional `SqlRuleAction` only. **Never** correlation/true filters from NimBus code |
| `TopicExistsAsync`, `SubscriptionExistsAsync` | check-then-act idempotency; **no** 409 handling exists — emulator must still return 409 on duplicate create (races surface loudly, matching real service) |
| `GetSubscriptionAsync`, `GetRuleAsync`, `GetTopicAsync` | reconcile-before-create |
| `GetTopicsAsync`, `GetSubscriptionsAsync`, `GetRulesAsync` | `.AsPages()` iteration → `$skip`/`$top` paging required |
| `GetTopicsRuntimePropertiesAsync`, `GetSubscriptionsRuntimePropertiesAsync` | **collection forms only** (never per-entity). Fields consumed: topic `Name`, `SubscriptionCount`, `ScheduledMessageCount`, `SizeInBytes`; subscription `SubscriptionName`, `ActiveMessageCount`, `DeadLetterMessageCount`, `TransferMessageCount`, `TransferDeadLetterMessageCount`, `TotalMessageCount`, `AccessedAt` |
| `UpdateSubscriptionAsync`, `UpdateTopicAsync` | get → mutate → PUT-with-`If-Match:*`. Mutated: `Status` (`Active`/`ReceiveDisabled`/`SendDisabled`), `ForwardTo` (cleared with `""`, never null) |
| `DeleteSubscriptionAsync`, `DeleteRuleAsync` | provisioner reconcile + admin ops. `DeleteTopicAsync`/queues: never |
| Settings fields read back | topic `Name`,`Status`; subscription `SubscriptionName`,`TopicName`,`Status`,`RequiresSession`,`ForwardTo`; rule `Name`,`Filter`,`Action` |
| Error contract | 404 must surface as **both** `RequestFailedException(404)` (HTTP) and be tolerated where code catches `ServiceBusException/MessagingEntityNotFound` — returning proper HTTP 404 gives the SDK what it needs for both |

### 4.3 Topology shape produced by the provisioner (the emulator's primary workload)

Topics = endpoint ids + literal `Resolver`. Per endpoint topic `E`, subscriptions:
`E` (session, 3 rules), `E-reply` (session, `ReplyFilter`), `Resolver` (forward → `Resolver` topic, 2 rules incl. SET action), `Deferred` (session, TTL, filter), `DeferredProcessor` (filter), one `{consumer}` per consuming endpoint (forward → consumer, per-event-type SET-action rules), `AgentDyn-{target}` (dynamic, `dyn-{eventTypeId}` rules). `$Default` is deleted everywhere except the Resolver topic's own subscription. Exact strings in §7.1.

---

## 5. Architecture

```
                     one TCP port (Aspire-assigned)
                              │
                     ┌────────▼────────┐
                     │ TcpMultiplexer   │  exact prefix (SEC-5): AMQP 8-byte header → AMQP, HTTP method → HTTP
                     └───┬─────────┬───┘
              AMQP bytes │         │ HTTP bytes
                 ┌───────▼──┐   ┌──▼───────────┐
                 │ AmqpFront│   │ AdminFront    │
                 │ (AMQP.Net│   │ (Kestrel,     │
                 │  Lite    │   │  ATOM XML +   │
                 │  listener│   │  /health)     │
                 └───────┬──┘   └──┬───────────┘
                         │         │
                 ┌───────▼─────────▼───────┐
                 │       BrokerCore         │  single source of truth, lock-free reads,
                 │  NamespaceState          │  per-entity ordered apply
                 │  ├ TopicEntity[]         │
                 │  │  └ SubscriptionEntity[]  (message log, sessions, locks, DLQ,
                 │  │       rules, scheduled, counters)
                 │  ├ FilterEngine (SQL subset, verbatim round-trip)
                 │  └ TimerWheel (lock expiry, scheduled due, TTL, session long-poll)
                 └───────────┬─────────────┘
                             │ (optional, P2)
                     ┌───────▼────────┐
                     │ IStateJournal   │  in-memory no-op (default) · SQLite · SQL Server
                     └────────────────┘
```

**Projects**

| Project | Contents |
|---|---|
| `src/NimBus.ServiceBusEmulator/` | Everything above as a library + a minimal `Program.cs` host (Kestrel bootstrap, config via env vars). Runnable stand-alone: `dotnet run --project src/NimBus.ServiceBusEmulator -- --port 5672` |
| `src/NimBus.ServiceBusEmulator.AspireHosting/` | `AddNimBusServiceBusEmulator()` extension (§10) |
| `tests/NimBus.ServiceBusEmulator.Tests/` | MSTest, `#pragma warning disable CA1707, CA2007`, conformance + fidelity suites (§11) |

**Dependencies**: `AMQPNetLite.Core`, **pinned to one exact reviewed version** chosen at M1 start (current stable line is 2.4.x; record the pin plus a license/changelog review note in the csproj — no floating ranges, upgrades only with the conformance suite green). Chosen because its listener APIs are public and aimed at brokers; `Microsoft.Azure.Amqp`'s listener side is `internal` and unusable. ASP.NET Core (in-box) for the admin plane. No other runtime deps in the default configuration.

**Prior art to read before coding**: [gkinsman/AlmostServiceBus](https://github.com/gkinsman/AlmostServiceBus) independently arrived at the same multiplexer + AMQP.Net Lite + delivery-tag-rewriting design and reports full Azure SDK compatibility. Do not vendor it (license/audit review first, and its scope is broader than ours), but its `EmulatorContainer` replacement for `ContainerHost` is the proven approach for the delivery-tag and batch-decoding hooks.

### 5.1 `[NET]` Network front door

- **NET-1** — Single TCP listener. Classify each accepted connection by a **bounded exact prefix** under the SEC-5 deadlines: the 8-byte AMQP protocol header → hand the (pushed-back) stream to the AMQP front-end; a known HTTP method token + space → hand to Kestrel (bind Kestrel to an in-process transport, e.g. `IConnectionListenerFactory` over the multiplexer, or an internal loopback forward — implementation's choice, but the socket the SDK sees is one port). Anything else → close.
- **NET-2** — No TLS anywhere. `UseDevelopmentEmulator=true` makes both SDK clients speak plaintext.
- **NET-3** — Port is configuration (`--port` / `NIMBUS_SBEMULATOR_PORT`); default 5672 for stand-alone use, Aspire-assigned in AppHost use.
- **NET-4** — `GET /health` is a **readiness** probe, not a static 200: it returns `200 {"status":"ok"}` only once the listener is bound, the AMQP front-end is accepting connections, the admin front-end is serving, and the broker core is initialized; before that the socket is either not yet bound or returns `503`. This is what lets `WaitFor(servicebus)` genuinely eliminate provisioner warm-up races (ASP-1/ASP-2).

### 5.2 `[SEC]` Security boundary — fail closed

Plaintext transport, accept-all CBS, and header-presence-only HTTP auth are acceptable **only while loopback is guaranteed**. That must be enforced, not assumed:

- **SEC-1** — Bind `127.0.0.1`/`::1` **only**; refuse to start on any non-loopback address, no override flag. Remote exposure is out of scope for P0 entirely — a genuinely secured mode (TLS + real SAS validation) would be its own spec; a bypass flag on an accept-all-auth broker is not a mode, it's a hole. The Aspire resource never marks the endpoint external (ASP-1).
- **SEC-2** — XML hardening on the admin plane: `DtdProcessing.Prohibit`, no external entity resolution, no `XmlResolver`; reject request bodies over 1 MiB with `413`.
- **SEC-3** — Connection/frame bounds, concrete defaults (all configurable): ≤ 256 concurrent TCP connections (refused beyond, never queued unboundedly); AMQP `max-frame-size` 1 MiB; ≤ **1 024 AMQP channels (sessions) per connection**; ≤ **2 048 links per connection**. Sizing rationale: the SDK opens a **separate AMQP session per receive link** (`CreateSessionIfNeededAsync` — single-session mode only under `EnableCrossEntityTransactions`, which NimBus never sets), so the Functions Resolver's `maxConcurrentSessions: 200` needs hundreds of channels on one connection — a small channel cap is a functional break around receiver #17, not conservative sizing. TST-4 pins this with a 200-session activation test.
- **SEC-5** — Protocol strictness and deadlines: the multiplexer classifies on a **bounded exact prefix**, not one byte — AMQP requires the full 8-byte protocol header (`AMQP` + version bytes), HTTP requires a token from the known method set (`GET PUT POST DELETE HEAD OPTIONS PATCH`) followed by a space; anything else → close immediately. Deadlines, all enforced server-side: 5 s from accept to a classified prefix, 10 s to complete SASL+open, 10 s for HTTP request headers, **30 s for a complete HTTP body with a 1 KiB/s minimum data rate** (slow bodies → close/`408`), **30 s from the first frame of a multi-transfer AMQP delivery to its final frame** (partial deliveries released and the link detached on breach), and 60 s idle timeout on connections with no open links and no in-flight requests. Cumulative size is enforced **incrementally**: an in-progress HTTP body or fragmented AMQP delivery is aborted the moment it exceeds its limit (SEC-2's 1 MiB / LNK-2's max-message-size), never buffered first. Slow-loris, drip-fed-body, and garbage-prefix cases are `[EmulatorOnly]` tests alongside SEC-2/SEC-3 malformed-input cases.
- **SEC-6** — Logging contract: logs must never contain CBS tokens, `Authorization` header values, connection strings, raw ATOM/XML request bodies, or message payloads — log identifiers (entity paths, message ids, correlation ids, operation names, sizes) only. `[EmulatorOnly]` test: capture all logs across a full conformance run and assert the absence of `SharedAccessSignature`, `SharedAccessKey=`, and known payload markers.
- **SEC-4** — Memory bounds. A configurable total budget (default 512 MiB) that **accounts every broker-held memory class**, not just raw inbound bytes: message bodies charged **once per subscription fan-out copy** (a 256 KiB message matching 6 subscriptions charges ~1.5 MiB), scheduled store, DLQ and transfer-DLQ copies, session state (≤ 256 KiB per session, matching Azure's order of magnitude), and topology metadata. When exceeded, reject incoming transfers with `amqp:resource-limit-exceeded` (→ SDK `QuotaExceeded`) rather than growing without limit. Ancillary structures are individually capped: pending session-accept waiters ≤ 1 024 per subscription (excess attach rejected with `com.microsoft:server-busy`), timer entries bounded by construction (≤ one lock timer + one TTL timer per stored message). Bounded channels everywhere (actor mailboxes, forward pumps) with backpressure, never unbounded queues.

---

## 6. AMQP data plane

### 6.1 `[SASL]` Handshake

- **SASL-1** — Advertise mechanisms **`MSSBCBS`** and `ANONYMOUS` (and optionally `PLAIN`). The SDK registers a SASL-ANONYMOUS handler under the literal name `MSSBCBS` and **fails the connection if the server list doesn't include it** (verified empirically: offering only `ANONYMOUS` → client throws). With AMQP.Net Lite: `saslSettings.EnableMechanism("MSSBCBS", SaslProfile.Anonymous)`.

### 6.2 `[CBS]` Claims-based security

- **CBS-1** — Implement the `$cbs` node as a request/response pair. Request: `application-properties` `operation="put-token"`, `type="servicebus.windows.net:sastoken"` (accept `jwt` too), `name=<audience>`, body = amqp-value string token.
- **CBS-2** — Always accept. Reply correlating on `correlation-id` = request `message-id` with application properties **`status-code`** (int, hyphenated!) = `202` and `status-description`. Do not verify signatures. Note the audience will be `amqps://…` even on a plaintext connection (SDK signs with the TLS scheme) — irrelevant since we don't validate, but don't "sanity-check" the scheme.
- **CBS-3** — ⚠ Spelling trap: `$cbs` replies use **`status-code`**; `$management` replies use **`statusCode`**. Getting either wrong produces a bare client-side `NullReferenceException` with no diagnostic. Encode both as named constants with a comment pointing here.

### 6.3 `[LNK]` Link addressing and attach

- **LNK-1** — Entity address = attach `Target.Address` (client sender) or `Source.Address` (client receiver), **with a leading `/` to strip** (observed: `/queue.1`). Recognized shapes: `{topic}`, `{topic}/Subscriptions/{sub}`, `{entityPath}/$DeadLetterQueue` (regular subscription DLQ), `{entityPath}/$management`, `$cbs`, and the local transaction coordinator used by Resolver replay. Entity resolution is **case-insensitive** (WebApp/CLI audits lowercase everything); names are stored and returned as created.
- **LNK-2** — Attach responses **must set `max-message-size`** (verified: omitting it → every send fails with "larger than is currently allowed (-1 bytes)"). Default 262 144; configurable to 1 048 576 ("Premium mode").
- **LNK-3** — Reject attach to a nonexistent entity with AMQP error `amqp:not-found` (→ SDK `MessagingEntityNotFound`; `PublisherClient.cs:279` turns this into the "run `nb topology apply`" hint — keep the mapping exact).
- **LNK-4** — Reject **receive** attach on a subscription with non-empty `ForwardTo`, and on `ReceiveDisabled` entities; reject **send** attach on `SendDisabled` topics. The WebApp purge guards (`SubscriptionAdminService.cs:254-276`) rely on these rejections existing.
- **LNK-5** — Lock token == delivery tag: **every outgoing delivery tag must be exactly 16 bytes, the little-endian .NET GUID** of the lock token; settlement arrives addressed by that same tag. AMQP.Net Lite assigns 4-byte counter tags by default — inject the GUID tag via the connection `IHandler` `SendDelivery` event (fires before the default assignment; only assigned `if (delivery.Tag == null)`).
- **LNK-6** — Honor `flow` frames incl. the `drain` flag (receiver batch loops use credit=100 + drain on timeout; echo drain completion with credit consumed).

### 6.4 `[SES]` Sessions

- **SES-1** — A session receiver attach carries `Source.FilterSet["com.microsoft:session-filter"]`. Value = session id string (explicit accept) or **null** (next-available — used constantly by `ServiceBusSessionProcessor`).
- **SES-2** — The attach **response** must echo the filter with the **resolved** session id string; the SDK throws `SessionFilterMissing` if absent, and treats a null value as retryable-failure.
- **SES-3** — Session lock expiry returns as link **property** `com.microsoft:locked-until-utc` = **.NET ticks** (`long`, 100 ns units since year 1 — NOT epoch ms; the per-message annotation `x-opt-locked-until` is by contrast a normal AMQP timestamp).
- **SES-4** — Explicit accept of session `S` locks any otherwise eligible session id immediately, including an id with no messages or stored state and a previously materialized session that is now empty. If another receiver owns `S`, reject the attach with `com.microsoft:session-cannot-be-locked` (→ `SessionCannotBeLocked`). A session receiver attach to a non-session subscription is rejected immediately rather than entering the acceptance loop. Explicit acceptance has no availability wait or broker timeout; cancellation and link closure still end attachment. Request/reply remains valid: `PublisherClient` can accept and lock its unique reply session before the reply exists, then its receive waits for the later message. Session selection and lock acquisition occur under the broker lock, so only one owner succeeds per ownership generation. The empty-session cases remain `[CommonFidelity]` candidates until a linked real-Azure compatibility run verifies them; local emulator results alone do not establish Azure parity.
- **SES-5** — Next-available accept: pick an unlocked session with ≥1 *deliverable* (active, due, unlocked) message; if none, wait up to the client's `com.microsoft:timeout` link property (uint ms), then reject attach with `com.microsoft:timeout` (→ `ServiceTimeout`, which the session processor swallows and retries — this is its idle loop).
- **SES-6** — Session lock: one owner per (subscription, session); duration = subscription `LockDuration` (30 s); renewed via `renew-session-lock`; on expiry the session becomes acceptable elsewhere and any late settlement/state call from the old owner fails with `com.microsoft:session-lock-lost` (→ `SessionLockLost`, mapped to `TransientException` in eight `MessageContext` sites).
- **SES-7** — Within a locked session, deliver strictly FIFO by sequence number, one credit at a time as granted (the ordering guarantee of ADR-001; `MaxConcurrentCallsPerSession=1` on the client side means the broker just must not reorder).
- **SES-8** — Session state: opaque byte payload per (subscription, session), capped at 256 KiB (SEC-4). `get-session-state` → return bytes or AMQP null; `set-session-state` with null or empty **clears** it. State survives session lock cycling; a session with only state (no messages) remains explicitly acceptable (SES-4) — `AdminService.Resubmit.cs:208-215` depends on it.
- **SES-9** — Close and disconnect semantics. **Clean close** (link detach, or the owning AMQP connection closes/drops in a way the broker observes): release the session lock immediately; unsettled deliveries return to `Active` in order **without a delivery-count increment** — per [Azure's documented behavior](https://learn.microsoft.com/en-us/azure/service-bus-messaging/message-sessions#impact-of-delivery-count), delivery count increments only on **abandon** or **lock expiry**, never on session/connection close; the session is immediately re-acceptable. **Unobserved half-open connections** (no detach, no TCP close): the lock is held until `LockDuration` expiry per SES-6, and *that* redelivery does increment. Lock renewal via `renew-session-lock` may extend a session lock indefinitely past `LockDuration` (clients renew for up to `MaxAutoLockRenewalDuration` = 5 min). Tests must assert the **exact** delivery count for each transition: receiver `DisposeAsync` (unchanged), abrupt TCP reset (unchanged), processor stop/restart mid-session (unchanged), half-open lock expiry (+1), explicit abandon (+1), and a session held via renewal for &gt;3× `LockDuration`.

### 6.5 `[XFER]` Transfers in

- **XFER-1** — Single messages: standard transfer to the topic node. Run the send pipeline: content stamping (sequence number, enqueued time) → rule evaluation per subscription (§7.2) → per-matching-subscription enqueue (SET actions apply to *that subscription's copy*) → hand off to the forward pump when `ForwardTo` is set (BRK-6; never a synchronous hop).
- **XFER-2** — Batched messages: message format `0x80013700` — body is a list of `Data` sections, **each one a complete encoded AMQP message**; decode and enqueue individually (a broker that ignores the format stores one opaque blob and breaks `SendMessagesAsync`). Envelope-hoisted `message-id`/`group-id`/partition-key are informational only.
- **XFER-3** — `x-opt-scheduled-enqueue-time` annotation: park until due; **a value ≤ now means enqueue immediately** — NimBus stamps `ScheduledEnqueueTime` on *every* message (`MessageHelper.cs:101`), usually "now".
- **XFER-4** — Enforce max message size (LNK-2 value) per message → `amqp:link:message-size-exceeded`; enforce nothing else (no entity quotas).
- **XFER-5** — Preserve application-property AMQP types (string/int/long) through store and redelivery.
- **XFER-6** — Messages sent to a topic with **zero matching subscriptions are dropped** (real ASB semantics; NimBus relies on rules for routing, not on send failure). `SupportOrdering=true` topics: preserve arrival order per session into each subscription.

### 6.6 `[MGMT]` Entity `$management` node operations

Request messages carry application-properties `operation`, `com.microsoft:server-timeout`, optional `associated-link-name` (ties message-scoped ops to the lock-holding link); body = amqp-value map. Responses carry **`statusCode`** (int), `statusDescription`, optional `errorCondition` (camelCase — see CBS-3). Required operations (all names prefixed `com.microsoft:`):

**A. Locks**
- **MGMT-1** `renew-lock` — body `{lock-tokens: uuid[]}` → `{expirations: timestamp[]}`. Unknown/expired token → 410 + `com.microsoft:message-lock-lost`. Non-session deliveries only — session receivers never call this (BRK-3); a `renew-lock` for a session-owned delivery → 410 + `com.microsoft:session-lock-lost`.
- **MGMT-2** `renew-session-lock` — body `{session-id}` → `{expiration}`. Lost → 410 + `com.microsoft:session-lock-lost`.

**B. Peek**
- **MGMT-3** `peek-message` — body `{from-sequence-number, message-count[, session-id]}` → `{messages: [{message: <full encoded AMQP message bytes>}...]}`, `statusCode` 200, or **204 when empty**. Returns **active** messages in sequence order, non-destructive, no locks; `x-opt-message-state` may be omitted (SDK default `Active` — the purge paths' `State` branches stay on their no-op arms, §3). Works from non-session receivers against session subscriptions (LNK-1 note; peek is link-scoped to the subscription, not session-filtered unless `session-id` given).
- **MGMT-4** Scheduled messages **must not** appear in subscription peeks (they live topic-side; they surface in `ScheduledMessageCount` on the *topic* runtime properties instead).

**C. Scheduling**
- **MGMT-5** `schedule-message` — body `{messages: [{message: bytes, message-id, session-id?, partition-key?}...]}` → `{sequence-numbers: long[]}`. The returned value is a **scheduling identifier used only for cancellation**. At the due time the message runs the normal send pipeline (§6.5) and each subscription copy is stamped with a **new** delivery sequence number, [matching Azure](https://learn.microsoft.com/en-us/azure/service-bus-messaging/message-sequencing); the delivery TTL clock starts at **activation** (`x-opt-enqueued-time` = activation time), not at scheduling. Messages in `Scheduled` state are not subject to delivery TTL (BRK-1).
- **MGMT-6** `cancel-scheduled-message` — body `{sequence-numbers}` → 200; unknown → 404 + `com.microsoft:message-not-found`.

**D. Settlement fallback**
- **MGMT-7** `update-disposition` — body `{disposition-status: "completed"|"abandoned"|"suspended", lock-tokens[, deadletter-reason, deadletter-description, properties-to-modify, session-id]}`. This is the SDK's **fallback settlement path** after link reconnect (it retries here when a link disposition returns `Rejected`/`amqp:not-found`) — both paths (§6.7 and this) must hit the same state machine. The `"defered"` status (note the service's historical misspelling) is rejected per §3.

**E. Session state**
- **MGMT-9** `get-session-state` / `set-session-state` — per SES-8; body keys `session-id`, `session-state` (binary or null).

Unimplemented operations (`receive-by-sequence-number`, `add-rule`, `enumerate-rules`, `get-message-sessions`, `batch-delete-messages`, …) → `statusCode` 501 with a description naming this spec.

### 6.7 `[SET]` Settlement via link dispositions

| Disposition received | Meaning | Broker action |
|---|---|---|
| `accepted` | Complete | Remove message; counters update |
| `modified`, `undeliverable-here` unset | Abandon | Release lock, keep at head of session/queue order, increment delivery count on **next** delivery |
| `modified`, `undeliverable-here=true` | Defer | Reject with a clear error (§3 — SB defer is dead code in NimBus; loud failure beats silent mis-storage) |
| `rejected` (with or without error) | Dead-letter | Move to subscription DLQ. When error present: condition `com.microsoft:dead-letter`, `Info["DeadLetterReason"]`/`Info["DeadLetterErrorDescription"]` (**PascalCase**) → store and surface as application properties on the DLQ copy. Accept descriptions up to and including 4096 chars (NimBus truncates to exactly 4096, `MessageContext.cs:21-22`) |

- **SET-1** — Do **not** implement `released`-as-abandon; the modern SDK never sends it (the public protocol guide's claim that `modified` "isn't used" is wrong — trust this table, which was read from SDK source and verified live).
- **SET-2** — Settlement on an expired lock: for a delivery from a **non-session** receiver → `com.microsoft:message-lock-lost`; for a delivery from a **session** receiver whose session lock has expired → **always** `com.microsoft:session-lock-lost` (never `message-lock-lost` — per BRK-3 session deliveries have no independent message-lock expiry).
- **SET-3** — Delivery count, normative: the AMQP `header.delivery-count` on the wire is the count of **prior failed attempts** (0 on first delivery); the SDK surfaces `DeliveryCount = header + 1`, so the first delivery reads **1**. A message is delivered at most `MaxDeliveryCount` (10) times: when an abandon or lock expiry occurs and the just-failed delivery had public `DeliveryCount == MaxDeliveryCount`, the message moves to the DLQ (reason `MaxDeliveryCountExceeded`) instead of redelivering; the DLQ copy reads `DeliveryCount == MaxDeliveryCount`. These exact public values are asserted by a `[CommonFidelity]` dual-target test — run the Azure probe in **M1** and reconcile this paragraph before building the cutoff, but the counts above are the spec, not a placeholder.

### 6.8 `[ANN]` Message annotations & properties written by the broker on delivery

`x-opt-sequence-number` (long, per-subscription monotonic — see BRK-2), `x-opt-enqueued-time` (timestamp), `x-opt-locked-until` (timestamp), `x-opt-message-state` (may be omitted — always `Active`, §3), `x-opt-deadletter-source` (DLQ deliveries, P2), `header.delivery-count`, `header.ttl` where set; session id rides standard `properties.group-id`, `ReplyToSessionId` = `properties.reply-to-group-id`, message id = `properties.message-id` (no `x-opt` for it).

---

## 7. Broker core semantics

### 7.1 `[FID]` Fidelity invariants

- **FID-1 — SQL expressions round-trip byte-identical.** `ServiceBusTopologyProvisioner.RuleMatches` (`:290-296`) compares the read-back `SqlRuleFilter.SqlExpression` / `SqlRuleAction.SqlExpression` **ordinally** against what it sent. Store the **verbatim string** as the source of truth; parse to an AST for evaluation only; never normalize whitespace, quotes, casing, or trailing semicolons (note the provisioner's own asymmetry: `ForwardAction` ends with `;`, `RedirectAction` doesn't). Violation symptom: every `nb topology apply` silently deletes and recreates every rule. Acceptance test TST-1 pins this.
- **FID-2 — `ForwardTo` round-trips as a bare entity name.** `ForwardToMatches` (`:234-252`) compares trailing path segments case-insensitively and tolerates `name`, `lowercased`, or `sb://host/name`. Returning the stored bare name is safe; anything not ending in the entity name causes delete/recreate churn on subscriptions.
- **FID-3 — Explicit empty-session acceptance completes promptly.** An unlocked explicit session id is locked even when it has no message or stored state; next-session discovery still considers only sessions with deliverable messages. Contention and invalid non-session entities fail promptly. Real-Azure parity requires the protected CommonFidelity workflow evidence described in TST-3.
- **FID-4 — Admin topology commits do not rewind the data plane.** Admin writers prepare a definition-only candidate, atomically persist it, then apply the prepared delta. Until journal replacement succeeds, reads and message operations see committed topology. Persistence failure discards only the candidate and cannot erase sends, resurrect completions, or restore locks, session state, schedules, or sequence counters.
- **FID-5 — Receive-pump ownership has no lost-wakeup window.** Flow during drain completion or send-failure recovery records restart intent under the pump gate. Completion consumes that intent or rechecks credit while holding the same gate; it never clears a concurrent restart based on a stale credit sample, and at most one pump owns delivery.

Exact strings the emulator will round-trip (from `TopologyDescriptor.cs:92-114`, `$Default` = TrueFilter `1=1`):

```
user.To = '{E}'
user.To = 'Continuation'            → SET user.To = '{E}'; SET user.From = 'Continuation'
user.To = 'Retry'                   → SET user.To = '{E}'; SET user.From = 'Retry'
user.To = '{E}-reply'                                     (rule name: ReplyFilter)
user.To = 'Resolver'                → SET user.From = '{E}'
user.To = 'Deferred' AND user.OriginalSessionId IS NOT NULL
user.To = 'DeferredProcessor'
user.EventTypeId = '{et}' AND user.MessageType = 'EventRequest' AND user.From IS NULL
   → SET user.From = '{p}'; SET user.EventId = newid(); SET user.To = '{c}';
```

### 7.2 `[FLT]` SQL filter/action engine

Required grammar (everything the repo emits — reject the rest with a clear parse error at rule-creation time, mirroring ASB's 400):

- Predicates: `user.<prop> = '<string literal>'`, `<expr> AND <expr>`, `user.<prop> IS NULL`, `user.<prop> IS NOT NULL`, integer-literal comparison `1=1` (for `$Default`/TrueFilter) and `1=0` (FalseFilter, matches nothing). Comparison against application properties by exact key (`user.` prefix strips to the property bag; system-property references like bare `sys.` are not needed).
- Actions: sequence of `SET user.<prop> = '<string literal>'` and `SET user.<prop> = newid()` separated by `;`, optional trailing `;`. `newid()` produces a **`uniqueidentifier` — an AMQP `uuid` value surfacing as `System.Guid`**, not a string ([Azure SQL-rule-action semantics](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-messaging-sql-rule-action)); the dual-target test asserts the runtime type. NimBus tolerates this — readers go through `?.ToString()` (`ServiceBusMessage.cs:98`), and `Guid.ToString()` matches what the audit trail stores today.
- Evaluation: per subscription, first stamp a *copy* of the message, evaluate the filter against the copy's application properties; on match apply the action **to that subscription's copy only**, then enqueue (then forward, if `ForwardTo`).
- `EnableDeadLetteringOnFilterEvaluationExceptions=true`: an evaluation error (e.g. type mismatch) dead-letters into that subscription's DLQ rather than dropping. (Rare with this grammar; implement as a guard, not a feature.)
- Rule fan-in follows [Azure's documented semantics](https://learn.microsoft.com/en-us/azure/service-bus-messaging/topic-filters): all matching rules **without** actions collapse into **one** copy of the message; each matching rule **with** an action produces its **own** transformed copy, stamped with a `RuleName` application property naming the matched rule. (NimBus's provisioned rule sets are filter-disjoint, so it only ever sees one match per subscription — but the emulator must match Azure here so the dual-target overlapping-rule test, TST-3, passes.)
- `$Default` (TrueFilter) is auto-created on subscription creation and deletable; the provisioner deletes it on all but one subscription. A create whose `SubscriptionDescription` carries a `DefaultRuleDescription` (the SDK's `CreateSubscriptionAsync(options, rule)`) is seeded with that rule instead; the provisioner uses this to create subscriptions with a closed `$Default` (`1=0`) so they never match the whole topic. The topology journal replays a journaled `$Default` as saved.

### 7.3 `[ERR]` Error mapping (load-bearing subset)

| Emulator condition (AMQP / HTTP) | SDK `ServiceBusFailureReason` | NimBus dependence |
|---|---|---|
| `com.microsoft:session-cannot-be-locked` on attach (session locked by another receiver, SES-4) | `SessionCannotBeLocked` | treated as a graceful "nothing to do" no-op ×5 sites |
| mgmt 404 + `com.microsoft:message-not-found` | `MessageNotFound` | `cancel-scheduled-message` unknown seq (legacy deferred paths also map here but never fire, §3) |
| `com.microsoft:session-lock-lost` | `SessionLockLost` | → `TransientException` ×8 in `MessageContext` |
| `com.microsoft:message-lock-lost` | `MessageLockLost` | never caught by reason — still map correctly |
| `amqp:not-found` on attach / HTTP 404 | `MessagingEntityNotFound` | reply-subscription hint, recreate tolerance |
| `com.microsoft:timeout` | `ServiceTimeout` | session-processor idle loop (SES-5); also in the transient-restart set |
| `com.microsoft:server-busy` | `ServiceBusy` | transient-restart set — emulator should never emit it in normal operation |

The processor-restart heuristic (`NimBusReceiverHostedService.cs:389-412`) restarts after 5 recoverable errors in 2 minutes — a correctly behaving emulator (no spurious disconnects) never triggers it; that alone removes the official emulator's warm-up flakiness class.

### 7.4 `[BRK]` Message lifecycle

- **BRK-1** — Per-subscription message store; states: `Scheduled(topic-side) → Active → {Locked → (Completed|Abandoned→Active|DLQ)}`. TTL expiry applies from any non-locked **delivery** state; `Scheduled` is exempt — its identity is the cancellation sequence number, and the delivery TTL clock only starts at activation (MGMT-5).
- **BRK-2** — Sequence numbers: monotonically increasing per **topic**, stamped at enqueue into subscriptions (copies share the topic sequence number — sufficient for NimBus's `+1` peek pagination; matches ASB observable behavior closely enough. ⚠ If implementation finds the SDK exposes per-subscription gaps oddly, per-subscription counters are acceptable — nothing in NimBus compares sequence numbers across subscriptions).
- **BRK-3** — Locks, two models by subscription kind:
  - **Non-session subscriptions**: per-message GUID lock token with independent `LockDuration` (30 s) expiry, renewable via `renew-lock` (MGMT-1); expiry via timer wheel → back to Active, delivery-count increment on next delivery.
  - **Session subscriptions**: the **session lock is the umbrella lock** — delivery lock tokens remain individual settlement *identities*, but their **validity follows the owning session lock**, with no independent per-message expiry. `renew-session-lock` therefore extends settlement validity for **all** unsettled deliveries owned by that session receiver. This matches the SDK: session receivers never call `renew-lock` (message auto-renewal runs only for `!IsSessionReceiver`); the session processor renews the *session* lock and expects in-flight messages to stay settleable. An emulator with independent message-lock expiry would fail any handler that runs past `LockDuration` — e.g. `Complete` failing at T+30 s while the session lock is validly renewed to T+60 s.
- **BRK-4** — TTL: message TTL = min(message `header.ttl`, subscription/topic `DefaultMessageTimeToLive` where set — `Deferred` sub: 14 d in production, **1 h as effectively provisioned locally** (ASP-3); replies carry 5 min). `DeadLetteringOnMessageExpiration` is never enabled → expired messages are **removed silently**. ⚠ Deliberate divergence: the emulator expires messages **independently and without requiring an active listener**, whereas Azure ties session-message expiration to an active listener and can [expire session messages together](https://learn.microsoft.com/en-us/azure/service-bus-messaging/message-sessions#message-expiration). Independent expiry is strictly simpler and NimBus depends on no session-coupled expiry behavior — listed under `[AzureDivergence]`, with a two-message dual-target probe test documenting the difference rather than asserting equality.
- **BRK-5** — DLQ: per-subscription sub-queue. Inbound paths: explicit dead-letter (SET table), max-delivery-count (SET-3), filter-eval exception (FLT). Counts surface in runtime properties. Stock-SDK peek/PeekLock receive and settlement on the regular `/$DeadLetterQueue` are supported for Resolver replay; transfer-DLQ browsing remains deferred.
- **BRK-6** — Auto-forward is an **asynchronous per-subscription forward pump**, not a synchronous hop. A subscription with non-empty `ForwardTo` and `Active` status has a pump that drains its messages in order and re-sends each through the target topic's full pipeline via the target actor's mailbox — never while holding the source topic's writer (BRK-9), so A→B and B→A forwarding cannot deadlock. The pump activates on: message arrival, `ForwardTo` being set or restored, and status flipping back to `Active` — this is what makes the WebApp's pause/resume work, which deliberately detaches `ForwardTo`, lets backlog **accumulate**, and re-attaches it on resume expecting the backlog to then flow (`SubscriptionAdminService.cs:163-245`). NimBus's `user.From IS NULL` convention remains the application-level loop guard, but the broker also enforces a **hop limit of 4** (matching Azure's chained-auto-forward limit); exceeding it, or a missing or `SendDisabled` forward target, moves the message to the subscription's **transfer DLQ**. `TransferMessageCount` = messages in `Active`/`TransferPending` on a forwarding subscription; `TransferDeadLetterMessageCount` counts transfer-DLQ arrivals — both surface truthfully in runtime properties (the WebApp displays them split, `docs/service-bus-subscription-admin.md:18`).
- **BRK-6a** — Forward transfer protocol (all transitions mediated by the **source** actor, which is the sole writer of its own store):
  1. **Reserve**: the source actor moves the head message `Active → TransferPending`, stamps it with the pump's current **generation** (see below), and hands the transfer intent to the **pump**, which delivers it to the target actor's mailbox — per the BRK-9 invariant, the pump (not the source actor) is what may await target capacity. `TransferPending` messages are invisible to peeks-for-delivery and never receivable (receive is rejected on forwarding subscriptions anyway, LNK-4).
  2. **Commit**: the target actor enqueues the copy through its full pipeline and **completes the pump-owned result** for that transfer (e.g. a `TaskCompletionSource` carried in the transfer intent) — it never posts into the source actor's mailbox itself. The **pump** then delivers the acknowledgement to the source actor (the pump, per BRK-9, is the only party allowed to await either side's capacity); on receiving it, the source actor removes the message (`TransferPending → Forwarded`, i.e. gone). Only then is the next message reserved — transfers are strictly one-at-a-time per subscription, preserving order.
  3. **Fault**: the target actor completes the pump-owned result with hop-limit exceeded / entity missing / `SendDisabled` instead of success; the pump delivers the fault to the source actor, which moves the message `TransferPending → TransferDLQ`.
  4. **Generation & cancellation**: the pump generation increments whenever `ForwardTo` changes or the subscription status changes — both of which are themselves admin operations processed by the source actor. When an ack or fault arrives carrying a **stale generation**, and the message is still `TransferPending` locally, the source actor resolves it conservatively: a stale **ack** still removes the message (the copy exists at the target; keeping it would duplicate), a stale **fault** rolls it back to `Active`.
  5. **Pause linearization**: the admin PUT that detaches `ForwardTo` completes only after the source actor has processed the config change (bumping the generation and stopping new reserves). Because reserves are one-at-a-time, at most **one** message can still commit after pause returns — a bounded, documented escape window (record it in the operator guide, `docs/service-bus-subscription-admin.md`); NimBus's purge path independently refuses to run while `ForwardTo` is attached, so the window cannot race a purge. A `TransferPending` message at pause time that later faults returns to `Active` and counts in the accumulated backlog.
- **BRK-7** — `EntityStatus`: enforce `Active`/`ReceiveDisabled`/`SendDisabled` on attach (LNK-4) and on running links (detach with `amqp:not-allowed` when status flips mid-flight is acceptable; NimBus flips status only around drains).
- **BRK-8** — `AccessedAt` per entity: bump on any data-plane operation; `CreatedAt`/`UpdatedAt` maintained but unconsumed.
- **BRK-9** — Concurrency model: one logical writer per topic (channel/actor); reads (peek, runtime props) snapshot-consistent. Cross-topic interaction happens **only** via asynchronous mailbox handoff (the forward pump, BRK-6). The no-deadlock claim is an **implementation invariant, not a hope**: an actor must **never await capacity in another actor's mailbox** — with SEC-4's bounded channels, `await WriteAsync` from actor A into a full mailbox of actor B (while B symmetrically awaits A) is exactly the deadlock BRK-9 exists to prevent. Only the **pump** (an independent async component, not the actor loop) may await target capacity; alternatively `TryWrite` with the transfer intent retained in the pump's own retry state. The source actor performs `Active → TransferPending`, hands the intent to the pump, and immediately returns to its mailbox. `[EmulatorOnly]` stress test: mailbox capacity forced to 1, A↔B mutual forwarding, concurrent publishes plus pause/resume — must show progress and bounded memory.

---

## 8. HTTP admin plane (ATOM/XML)

- **ADM-1** — Accept `api-version` ∈ {`2017-04`, `2021-05`, `2024-05`} (7.20.2 sends `2021-05`; 7.21+ sends `2024-05`); ignore the value.
- **ADM-2** — Routes (case-insensitive; `{t}`=topic, `{s}`=subscription, `{r}`=rule):

| Route | Verbs | Notes |
|---|---|---|
| `/{t}` | GET, PUT, DELETE | PUT create (409 if exists, unless `If-Match:*` → update). `?enrich=True` adds runtime counts |
| `/{t}/Subscriptions/{s}` | GET, PUT, DELETE | same pattern |
| `/{t}/Subscriptions/{s}/Rules/{r}` | GET, PUT, DELETE | |
| `/{t}/Subscriptions` | GET | list, `$skip`/`$top` paging, `?enrich=True` for runtime-collection |
| `/{t}/Subscriptions/{s}/Rules` | GET | list |
| `/$Resources/topics` | GET | list all topics, paging, `enrich` |
| `/$Resources/queues` | GET | return empty feed (SDK may probe; NimBus never lists queues) |
| `/{q}` queue CRUD | — | 501 + explanatory body (revisit under OQ-2) |
| `/health` | GET | NET-4; the **only unauthenticated route** (ADM-6) |

- **ADM-3** — Payloads: ATOM `entry` (ns `http://www.w3.org/2005/Atom`) wrapping `TopicDescription`/`SubscriptionDescription`/`RuleDescription` (ns `http://schemas.microsoft.com/netservices/2010/10/servicebus/connect`). **Build the serializer test-first against captured SDK requests** (the research probe approach): create each entity type with the SDK, snapshot the PUT bodies as test fixtures, and assert our GET responses parse back into `*Properties` objects with every §4.2 field intact. Accept lenient element order on input; emit the service's canonical order on output.
- **ADM-4** — Rule XML: `Filter` with `i:type="SqlFilter"` (`SqlExpression` verbatim — FID-1 — plus `CompatibilityLevel>20</`), `i:type="TrueFilter"` for `$Default` (expression `1=1`); `Action` absent/`EmptyRuleAction` or `i:type="SqlRuleAction"` (`SqlExpression` verbatim).
- **ADM-5** — Runtime properties are the **same GET with `enrich=True`**, adding `MessageCount`, `SizeInBytes`, `SubscriptionCount`, `AccessedAt`, `CreatedAt`, `UpdatedAt`, and `CountDetails` (`ActiveMessageCount`, `DeadLetterMessageCount`, `ScheduledMessageCount`, `TransferMessageCount`, `TransferDeadLetterMessageCount`) — exactly the fields in §4.2. Collection-with-enrich must work (`GetTopicsRuntimePropertiesAsync`, `GetSubscriptionsRuntimePropertiesAsync` are the only forms NimBus calls).
- **ADM-6** — Auth: require an `Authorization: SharedAccessSignature …` header to be *present* on every admin route; do not validate the signature (audience signs `https://` even over plain http — another reason not to). Missing header → 401 (keeps accidental unauthenticated tooling honest). **Exactly one exemption: `GET /health` is unauthenticated** — Aspire's health probe sends no SAS header (ASP-1), so an authenticated health route would fail readiness forever. Tests: unauthenticated `/health` → 200/503; unauthenticated admin route → 401; authenticated admin route → normal handling.
- **ADM-7** — Errors: 404 with an ATOM error body for missing entities (the SDK maps to `RequestFailedException(404)` / `MessagingEntityNotFound`); 409 for duplicate create; 400 for filter parse errors. **Never 500 for a missing entity** — the SDK's retry pipeline hammers 5xx four times before surfacing.
- **ADM-8** — `PUT` update semantics: `If-Match: *` present → treat as update of `Status` and `ForwardTo` (the only fields NimBus mutates); other property changes may be accepted-and-stored. `ForwardTo` cleared by empty string. `ServiceBusSupplementaryAuthorization` headers: accept and ignore.
- **ADM-9** — `GET /$namespaceinfo`: implement as a stub (`NamespaceProperties` with `MessagingSku=Standard`); zero NimBus usage but trivially cheap insurance.

---

## 9. Storage

**Decision: messages are in-memory; topology is durable by default.** P0 ships two pieces: the in-memory message store, and a lightweight **topology journal** — entities, rules, and status only, no messages — written on every admin mutation to a JSON file (default `%TEMP%/nimbus-sbemulator/{resource-name}/topology.json`, overridable via `NIMBUS_SBEMULATOR_TOPOLOGY_PATH`; the Aspire resource pins it per AppHost) and replayed on boot. This exists for exactly one reason: an emulator that restarts mid-AppHost-run must come back **provisioned**, because the one-shot provisioner will not rerun on its own (STO-4). Messages are deliberately volatile. Rationale for not going further:

- The provisioner runs on every AppHost start and recreates topology in seconds — durability of *topology* is free already.
- In-flight local-dev messages are transient by nature; the official emulator offers no persistence either, and nobody in this repo has asked for it.
- The broker is timer- and lock-centric; making a database the source of truth would force distributed-lock semantics onto a single-process problem. The official emulator's mandatory SQL Server side-car is one of its cited costs.

**Optional durability (P2)** via a narrow journal interface, applied write-behind from the per-topic writer (BRK-9 makes replay trivial — single-writer event log per topic):

```csharp
interface IStateJournal
{
    ValueTask AppendAsync(BrokerEvent evt, CancellationToken ct);   // EntityCreated/RuleUpserted/MessageEnqueued/Settled/...
    IAsyncEnumerable<BrokerEvent> ReplayAsync(CancellationToken ct);
    ValueTask CheckpointAsync(BrokerSnapshot snapshot, CancellationToken ct);
}
```

- **STO-1** (P2) — `SqliteStateJournal`: single file next to the AppHost run, zero infra, survives restarts. Recommended durable option if one is ever needed.
- **STO-2** (P3) — `SqlServerStateJournal`: same interface against the Aspire SQL container the AppHost already runs (schema via DbUp like `NimBus.MessageStore.SqlServer`). Only worth building if a team standardizes on inspecting broker state via SQL, or wants durability without local files. The requested "SQL Server for storage" option is thus supported by design but deliberately not the default — a database as primary store buys nothing for the local-dev use case and costs a second moving part, which is precisely the official emulator's mistake.
- **STO-3** — Default `NullStateJournal` (no-op). Store choice via config: `NIMBUS_SBEMULATOR_STORE=memory|sqlite|sqlserver`.
- **STO-4** — Restart semantics. With the default topology journal, an emulator restart inside a running AppHost comes back **fully provisioned** (topology replayed from the journal before `/health` reports ready — readiness includes journal replay) but with **all messages lost**, visible as counts resetting to zero. A restarted-but-unprovisioned state is therefore impossible by construction; the provisioner never needs to rerun. Supporting requirements:
  - `/health` includes an `instance` id (fresh GUID per process start) so tooling and tests can still detect a restart behind a green probe.
  - `[EmulatorOnly]` tests: (a) kill and restart the emulator, assert topology is intact **without** rerunning the provisioner and publish/consume works; (b) delete the journal file, restart, rerun the provisioner, assert full recovery from empty.
  - The journal is versioned and best-effort on read: a corrupt/incompatible file is renamed aside and the broker starts empty (then recovery path (b) applies) — never a crash loop.
  - Where full restart-resilience (messages too) matters, the durable journal (STO-1/STO-2) is the opt-in answer.

---

## 10. Aspire integration

- **ASP-1** — `NimBus.ServiceBusEmulator.AspireHosting` exposes:

```csharp
var servicebus = builder.AddNimBusServiceBusEmulator<Projects.NimBus_ServiceBusEmulator>("servicebus");

builder.AddProject<Projects.AspirePubSub_Provisioner>("provisioner")
    .WithReference(servicebus.ConnectionString)   // injects ConnectionStrings__servicebus
    .WaitFor(servicebus.Project);
```

Implemented as a project-backed resource (no container, no image pulls, sub-second start). ⚠ `ProjectResource` does **not** implement `IResourceWithConnectionString` in Aspire 13.4.6 (verified by reflection during review), and the public `AddProject` APIs cannot construct a custom `ProjectResource` subclass — so the single-resource shape is **rejected**. The specified shape is the **two-resource handle**:
  - `AddNimBusServiceBusEmulator<TProject>(name)` with `TProject : IProjectMetadata, new()` (the `new()` constraint is required by `AddProject`) returns a handle struct exposing exactly two members, used consistently everywhere in this spec: **`.Project`** (`IResourceBuilder<ProjectResource>`, resource name `{name}-emulator`) and **`.ConnectionString`** (`IResourceBuilder<IResourceWithConnectionString>`, resource name **`{name}`** — the caller-facing connection name, so `.WithReference(servicebus.ConnectionString)` injects `ConnectionStrings__servicebus` exactly as `AddConnectionString("servicebus")` does today and consumers need no config changes). A non-generic overload taking a project path exists for exotic setups.
  - The connection-string resource's expression is built from the project resource's endpoint (see below); it carries a `WaitFor`-able health dependency on the project resource so referencing the connection string alone is safe, but dependents that must not start early (the provisioner) also take `.WaitFor(servicebus.Project)` explicitly.
  - The AppHost gains a `ProjectReference` to `src/NimBus.ServiceBusEmulator/` in `NimBus.AppHost.csproj` (that is what generates the `Projects.NimBus_ServiceBusEmulator` metadata type).
  - **M4 must compile-test this exact snippet as its first task** — the API is not considered specified until it compiles.
  - One named `tcp` endpoint (not `http` — the SDK dials raw AMQP first); **not** `IsExternal`.
  - `ConnectionStringExpression` is built from **endpoint expressions**, never hardcoded host/port: `Endpoint=sb://{ep.Property(Host)}:{ep.Property(Port)};SharedAccessKeyName=RootManageSharedAccessKey;SharedAccessKey=nimbus-local;UseDevelopmentEmulator=true`.
  - The emulator process learns its listen port via `NIMBUS_SBEMULATOR_PORT` = `{ep.Property(TargetPort)}` injected as an environment variable on the project resource.
  - A health check polls `http://{ep.Property(Host)}:{ep.Property(Port)}/health` (the multiplexer serves HTTP on the same port); combined with NET-4's real readiness semantics, `.WaitFor(servicebus)` guarantees the provisioner only starts against a serving broker.

- **ASP-2** — `src/NimBus.AppHost/Program.cs` changes:
  1. Keep a flag like CrmErpDemo's (`UseEmulator` config / `NIMBUS_SB_EMULATOR` env; flip the default to emulator once M4 lands): emulator resource vs `AddConnectionString("servicebus")`.
  2. Fix line 64: `AzureWebJobsServiceBus` must come from `servicebus.ConnectionString.Resource.ConnectionStringExpression`, not `builder.Configuration["ConnectionStrings:servicebus"]!` — the raw-config read is null for any resource that materializes its connection string at start. (CrmErpDemo already does this correctly at its lines 122/234/252.)
  3. **Do not skip the provisioner** — that's the point. `provisioner` runs unchanged, with `.WaitFor(servicebus.Project)`.
- **ASP-3** — `ServiceBusTopologyProvisioner.IsEmulator` detection (`UseDevelopmentEmulator=true` substring, `ServiceBusTopologyProvisioner.cs:77-81`) fires against our connection string too — the flag is non-negotiable (it's what makes the SDK speak plaintext), so **NimBus will still provision the 1-hour `Deferred` TTL and skip `MaxSizeInMegabytes=5120` locally**. This is explicitly retained: the emulator itself imposes no TTL or size caps and would accept the production values, but distinguishing "our emulator" from Microsoft's inside NimBus would require a NimBus code change, violating G2. Local runs losing 13 days and 23 hours of parking TTL on `Deferred` is irrelevant for dev workflows. If it ever matters, the follow-up is a NimBus-side change (e.g. an explicit `NimBus:ServiceBus:EmulatorProfile` setting overriding the substring detection) — out of this spec's scope. Document in the emulator README.
- **ASP-4** — CrmErpDemo migration (follow-up, not this spec's deliverable): swap `RunAsEmulator` for `AddNimBusServiceBusEmulator`, delete the provisioner-skip branches, and retire `EmulatorTopologyConfigBuilder` (–376 lines and a two-sources-of-truth risk). Do it only after M4's compat suite is green.
- **ASP-5** — Functions-based Resolver: the WebJobs Service Bus extension builds its `ServiceBusClient` from the same connection string via the same SDK. **Verification item for M2**: confirm the extension version bundled by `NimBus.Resolver` transitively references `Azure.Messaging.ServiceBus` ≥ 7.20.1 (custom-port emulator fix); if older, add a direct package reference to lift it.
- **ASP-6** — Stand-alone use (`dotnet run --project src/NimBus.ServiceBusEmulator -- --port 5672`) documented for non-Aspire workflows and CI service-container-style usage. Optional CLI sugar `nb emulator run` is P3.

---

## 11. Testing & acceptance

Framework: MSTest, same conventions as sibling test projects. All tests run the emulator in-process (no network flakes) except where noted.

- **TST-1 — Zero-churn provisioning (the flagship acceptance test).** Build the full `TopologyDescriptor` topology for a representative platform (reuse the test platform from `EmulatorTopologyConfigBuilderTests`), run `ServiceBusTopologyProvisioner.ApplyAsync` **twice** against the emulator; assert via the emulator's **operation log** — a structured, in-memory audit the broker keeps of every admin-plane mutation (`{verb, entityPath, kind: Create|Update|Delete, timestamp}`), exposed to tests via an internal accessor — that the second apply performed **zero creates, zero deletes, and zero mutating PUTs**. This single test pins FID-1, FID-2, ADM-3/4, and the reconcile semantics.
- **TST-2 — SDK conformance suite.** One test per requirement ID in §6–§8, written **against the public SDK** (not the emulator's internals). Every test is tagged into one of three categories that make TST-3 well-defined:
  - **`[CommonFidelity]`** — must pass identically on the emulator and real Azure: session processor consumes in order; next-available idles correctly (SES-5); explicit accept locks a never-materialized session and can relock a previously materialized session after it becomes empty; request/reply accepts its reply session before the reply is published and then receives the eventual message; accept of a session locked by another receiver throws `SessionCannotBeLocked`; close/disconnect semantics with **exact delivery counts** (SES-9: dispose, TCP reset, processor restart → unchanged; half-open expiry, abandon → +1; long renewal); **umbrella session lock** (BRK-3): a handler holds one session message for &gt;3× `LockDuration` while only session-lock renewal runs, then `Complete` succeeds with `DeliveryCount` unchanged; NimBus-level deferral end-to-end (message routed to `Deferred` subscription, replayed by `DeferredMessageProcessor` idioms); schedule + cancel (unknown seq → `MessageNotFound`); scheduled activation assigns a new delivery sequence number (MGMT-5); TTL expiry; max-delivery → DLQ count; abandon → redelivery; lock expiry → redelivery; batch send format; peek pagination; forwarding with SET actions end-to-end (publish `EventTypeId=X` → arrives on consumer subscription with rewritten `From`/`EventId`/`To`); pause → backlog accumulates → resume → backlog forwards (BRK-6); overlapping-rule fan-in incl. `RuleName` stamping (§7.2); reply round-trip (`PublisherClient` semantics); status-flip guards (LNK-4); admin CRUD + runtime properties + paging; 404/409 mapping.
  - **`[EmulatorOnly]`** — verifies deliberate emulator behavior that Azure does not share: defer disposition and `receive-by-sequence-number` rejected loudly (§3 — Azure *supports* deferral, so these never run against Azure); unimplemented-operation 501s; SEC-1 loopback refusal; quota rejection (SEC-4); malformed XML/oversized-body rejection (SEC-2); connection/link-cap refusal (SEC-3); garbage-prefix close and slow-client deadline enforcement (SEC-5); unauthenticated `/health` vs 401 admin routes (ADM-6); restart + reprovision (STO-4); capacity-1 mailbox A↔B forwarding stress with pause/resume — progress and bounded memory (BRK-9).
  - **`[AzureDivergence]`** — a short, documented list of intentional divergences (currently: SB deferral unsupported; no quotas below SEC-4's safety bounds; auth not validated; session-message expiry is independent and listener-free, BRK-4). Each entry links the spec section that justifies it. Anything divergent and *not* on this list is a bug.
- **TST-3 — Dual-target compatibility runs.** The `[CommonFidelity]` subset runs against a **real Azure namespace** (`NIMBUS_SBEMULATOR_COMPAT_CS`). Divergence between targets is a red build. Because the existing CI workflow carries no Azure credential (`.github/workflows/dotnet.yml`), env-gating alone would let the gate **silently skip** — so the operating rules are explicit:
  - A dedicated `servicebus-emulator-compat` **manual/post-merge workflow** (`workflow_dispatch` + `push` to master). The connection string lives in a **GitHub environment secret** whose environment is restricted to the `master` branch — `workflow_dispatch` can otherwise run a *selected branch* with repo secrets, which would hand the credential to any pushed branch. Defense in depth: an explicit `if: github.ref == 'refs/heads/master'` guard on the job, the secret injected at **job** scope only (not workflow-wide env), and top-level `permissions: contents: read`. Never exposed to `pull_request` runs of untrusted code.
  - Runs use a **dedicated compat namespace**, prefix every entity with a per-run id (`compat{runId}-…`), delete by prefix in a `finally` step, and sweep stale prefixes older than 24 h at startup — runs must be safely concurrent and crash-tolerant.
  - **M4 sign-off requires a linked green run of this workflow** (recorded in the PR/issue). A locally-skipped TST-3 is a skipped gate, not a passed one; the per-milestone exit criteria treat "did not run" as red.
- **TST-4 — AppHost e2e.** Two smoke levels, both M4:
  - `samples/AspirePubSub` full cycle on the emulator: provisioner → publish → session processing → resolver tracking → WebApp shows the message, exercised as a new **AspirePubSub-owned** e2e (M4 must not depend on CrmErpDemo: Playwright `07-agent-enrichment` runs against `CrmErpDemo.AppHost` and only switches to this emulator with the CrmErpDemo migration, ASP-4/M5).
  - **Main `src/NimBus.AppHost`** in emulator mode — the smoke must set **both** `NIMBUS_STORAGE_PROVIDER=sqlserver` and `NimBus__StorageProvider=sqlserver` (the AppHost otherwise defaults to Cosmos, and WebApp secrets can override one key alone): the **Functions-based Resolver** (`[ServiceBusTrigger]`, `maxConcurrentSessions: 200` — a different client stack than the SDK hosts) processes a message end-to-end; the **CLI** leg runs `nb endpoint purge <endpoint>` against the emulator via `AzureServiceBus_ConnectionString` (this path takes a plain connection string today). ⚠ `nb topology apply` is **not** emulator-runnable as shipped — it takes Azure coordinates and unconditionally shells `az servicebus … keys list` (`Program.cs:181`, `CommandLine/ServiceBusTopologyProvisioner.cs:59-70`); this spec **authorizes the small CLI change** to add a `--connection-string` (or `AzureServiceBus_ConnectionString` fallback) path to `topology apply` in M4, after which the CLI leg also covers a zero-churn second apply. Until that lands, provisioning fidelity is covered by TST-1's in-process `ApplyAsync`. The WebApp subscription-admin flows (pause/resume/purge/rebuild) are driven **through the WebApp's HTTP API surface** (`AdminImplementation` routes), not just `IServiceBusManagement` in-process.
  - **200-session activation** (protects the SEC-3 sizing): one `ServiceBusClient`, a session processor with `MaxConcurrentSessions=200`, publish into ≥200 distinct sessions; all 200 become concurrently active with no channel/resource-limit failures.
- **TST-5 — No regressions.** Existing suites (`NimBus.EndToEnd.Tests`, unit tests) untouched and green; `dotnet build -c Release` clean (CS8767 warning gotcha applies to new projects — Release-build locally before pushing).

---

## 12. Milestones

| # | Scope | Exit criterion |
|---|---|---|
| **M1** | Multiplexer, SASL(`MSSBCBS`)/CBS, attach/transfer/dispositions for non-session subscriptions, in-memory entities, ATOM CRUD for topics/subscriptions/rules (fixture-driven serializer), SQL filter engine with verbatim round-trip | `SendMessageAsync`+`ReceiveMessagesAsync` round-trip; TST-1 passes |
| **M2** | Sessions (explicit + next-available + state + locks incl. umbrella model), `$management` ops **A/B/C/E**, scheduled messages, TTL, delivery counts, DLQ, status enforcement, error mapping table; SES-4 compatibility cases | Named `[CommonFidelity]` tests green on the emulator and in the protected real-Azure workflow: empty explicit-session acceptance/reacceptance, contention, session-processor ordering, umbrella-lock long-handler, schedule/cancel + **TTL-starts-at-activation**, exact delivery counts (SES-9), max-delivery → DLQ (SET-3), status-flip guards, error-mapping table; ASP-5 verified |
| **M3** | `update-disposition` fallback, batch format, runtime properties incl. collection-enrich, paging, loud-rejection paths for excluded features (§3) | Full TST-2 green |
| **M4** | Aspire resource (compile-test the ASP-1 shape first) + AppHost integration + TST-3/TST-4 + compat workflow; README + docs update (incl. fixing the stale "no local emulator path" claims in `README.md:182` and `samples/CrmErpDemo/README.md:318`) | Main AppHost runs fully local incl. the Functions Resolver; WebApp admin screens work; **linked green TST-3 run** (not skipped) |
| **M5** (optional) | P2 items on demand: SQLite journal, transfer-DLQ browsing, queues, correlation filters; CrmErpDemo migration (ASP-4, incl. switching Playwright `07-agent-enrichment` to this emulator) | — |

Suggested sizing: M1–M2 are the bulk (broker core + wire fidelity); M3–M4 are wide but mechanical against this spec.

---

## 13. Risks & mitigations

| Risk | Mitigation |
|---|---|
| SDK-internal wire details drift across versions (mgmt op encodings, api-version bumps) | Pin floor 7.20.1; TST-3 dual-target runs catch divergence; ADM-1 accepts multiple api-versions |
| AMQP.Net Lite listener gaps (delivery-tag hook, drain, batch decode) | All three already identified with concrete hooks (LNK-5, LNK-6, XFER-2); prototype validated the core path; AlmostServiceBus corroborates feasibility of the remainder |
| ATOM XML fidelity — admin client silently mis-parses a hand-rolled response | ADM-3 fixture-first workflow: every serializer test round-trips through the real SDK parser |
| Off-by-one/format traps (`status-code` vs `statusCode`, ticks vs timestamps, delivery-count) | Encoded as named requirements (CBS-3, SES-3, SET-3) with dedicated tests; treat each ⚠ marker in this spec as a test case |
| Functions extension bundles an older SDK (ASP-5) | Check in M2; direct package reference lifts it |
| Emulator-only tests encode emulator bugs | TST-3 is non-negotiable before M4 sign-off |
| Session next-available semantics subtly differ from ASB under contention (8 concurrent sessions) | TST-3 includes a contended-session test; SES-5 long-poll matches observed SDK behavior |

---

## 14. Open questions

- ~~OQ-1~~ — *Resolved:* the SB defer API is dead code on `master` (§3); SB deferral is out of scope. Follow-up: `[Obsolete]`-mark the dead NimBus surface (§3).
- **OQ-2** — Queue entities: NimBus never uses queues, but non-NimBus consumers in samples (PartnerPortal, CloudEventsInterop non-NimBus consumer) use the raw SDK — verify they are topic-only too (research indicates yes). If any needs a queue, promote queues from 501 to a thin wrapper (a queue is a topic with one implicit subscription in this model).
- **OQ-3** — Default-on: after M4, should the main AppHost default to the emulator (real namespace behind the flag instead)? Recommended yes; decide at M4 review.

---

## 15. Reference index

**NimBus contract sites** (the inventory this spec is derived from): `src/NimBus.ServiceBus/Provisioning/TopologyDescriptor.cs` (all filter strings, `:50-55` byte-identical warning), `ServiceBusTopologyProvisioner.cs` (`:234-252` ForwardToMatches, `:290-296` RuleMatches, `:298-320` subscription options), `src/NimBus.ServiceBus/MessageHelper.cs` (wire format), `ServiceBusSession.cs` (settlement surfaces), `MessageContext.cs` (defer/redelivery/error mapping), `src/NimBus.SDK/Hosting/NimBusReceiverHostedService.cs` (processor options + restart heuristic), `src/NimBus.Management.ServiceBus/ServiceBusManagement.cs` + `EndpointManagement.cs` (admin ops, byte-identical warning), `src/NimBus.WebApp/Services/SubscriptionAdminService.cs` + `AdminService.Purge.cs` + `AdminService.Resubmit.cs` (drain/purge idioms), `src/NimBus.CommandLine/Endpoint.cs` (CLI twins), `samples/CrmErpDemo/CrmErpDemo.Contracts/EmulatorTopologyConfigBuilder.cs` (official-emulator precedent to retire).

**Protocol sources**: Azure SDK for .NET Service Bus source (`AmqpConnectionScope`, `AmqpCbsLink`, `AmqpMessageConverter`, `ManagementConstants`, `ServiceBusConnectionStringProperties`, `HttpRequestAndResponse`, `ServiceVersionExtensions`), SDK CHANGELOG 7.20.1, [Service Bus AMQP protocol guide](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-amqp-protocol-guide) (⚠ its settlement paragraph is wrong — see SET-1), [AMQP request/response operations](https://learn.microsoft.com/en-us/azure/service-bus-messaging/service-bus-amqp-request-response), [official emulator overview](https://learn.microsoft.com/en-us/azure/service-bus-messaging/overview-emulator) + [local-testing doc](https://learn.microsoft.com/en-us/azure/service-bus-messaging/test-locally-with-service-bus-emulator), [amqpnetlite listener docs](https://azure.github.io/amqpnetlite/articles/listener.html), [aspire#14041](https://github.com/microsoft/aspire/issues/14041), [AlmostServiceBus](https://github.com/gkinsman/AlmostServiceBus). Empirical verifications (custom port honored, `MSSBCBS` required, `status-code`/`statusCode` split, `max-message-size` mandatory, leading-slash addresses) were performed against Azure.Messaging.ServiceBus 7.20.2 during the research phase for this spec.
