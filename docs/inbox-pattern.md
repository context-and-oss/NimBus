# Consumer inbox

NimBus subscribers can opt into a consumer inbox that recognizes a redelivery after the same `MessageId` has already completed successfully on the same endpoint. The inbox complements the publisher outbox, but it does not make arbitrary application side effects transactional or exactly once.

The deduplication key is the `(endpoint, MessageId)` pair. NimBus fan-out forwards copies of one published message — with the same broker `MessageId` — to every subscribed endpoint, so endpoints that share one physical inbox store (one SQL table, one Cosmos container) stay isolated: one endpoint's record never turns another endpoint's first delivery into a duplicate skip. Identifiers are opaque, byte-exact keys on every provider: the durable providers key on a shared SHA-256 hash of the delimited `(endpoint, MessageId)` encoding, so ids that differ only by case or trailing whitespace are distinct everywhere (SQL Server's native `NVARCHAR` comparison would otherwise pad trailing spaces and conflate them).

The endpoint side of the key is the **configured subscriber endpoint** (`NimBusSubscriberOptions.Endpoint`), not the message's `To` address. The two agree for native NimBus messages, but an external CloudEvents producer stamps no `user.To`, so the transport substitutes the mapped event type there — a value that is neither unique per endpoint (a reused `MessageId` across two event types must still deduplicate) nor distinct across endpoints sharing one store (two subscribers consuming the same event type must not skip each other's first deliveries). The NimBus subscriber and test-transport compositions pass the configured endpoint into the inbox automatically; standalone `InboxMiddleware` compositions should pass `endpointScope` explicitly when they consume CloudEvents.

## Registration

Register one inbox provider, then enable the inbox on the subscriber and select that provider:

```csharp
builder.Services.AddNimBusSqlServerInbox(connectionString);

builder.Services.AddNimBusSubscriber("BillingEndpoint", subscriber =>
{
    subscriber.AddHandler<OrderPlaced, OrderPlacedHandler>();
    subscriber.UseInbox(options =>
    {
        options.DeduplicationStore = InboxStore.SqlServer;
        options.RetentionPeriod = TimeSpan.FromDays(7);
        options.CleanupInterval = TimeSpan.FromHours(1);
    });
});
```

The inbox is opt-in. A subscriber that does not call `UseInbox` does not resolve an inbox store, add a handler decorator, or run the cleanup service.

`UseInbox` requires NimBus's own subscriber wiring: if a custom `ISubscriberClient` is already registered in the service collection, `AddNimBusSubscriber` fails fast with a configuration error instead of installing the cleanup service and lifecycle notifier against a subscriber the inbox decorator can never reach. The reverse order is guarded too — a custom `ISubscriberClient` registered *after* `AddNimBusSubscriber` would win Microsoft DI's last-registration rule, so a startup validation service resolves the effective client and fails host startup unless it is the instance NimBus composed with the inbox decorator.

The supported providers are:

- `AddNimBusSqlServerInbox(...)` from `NimBus.Inbox.SqlServer`;
- `AddNimBusCosmosInbox(...)` from `NimBus.MessageStore.CosmosDb`;
- `AddNimBusAzureTableInbox(TableClient)` from `NimBus.Inbox.AzureTables`;
- `AddNimBusInMemoryInbox()` from `NimBus.Testing`, intended for tests and local harnesses.

Selecting a provider without registering its package fails during host startup with a configuration error. The in-memory provider is not a durable production store and should not be used where deduplication must survive a restart.

Enabling the inbox does not backfill IDs for messages that succeeded before deployment. A redelivery of one of those older messages can therefore run once and create its first inbox record. Plan the rollout and retention window with that compatibility boundary in mind.

### Azure Table Storage for lightweight adapters

Install `Akaule.NimBus.Inbox.AzureTables` in the subscriber. Provision a dedicated table such as `NimBusInbox` in an Azure Storage account. Give the adapter identity the **Storage Table Data Contributor** role scoped to that table. Build the client with the account's primary Table service URI so every adapter instance reads committed receipts.

```csharp
using Azure.Data.Tables;
using Azure.Identity;
using NimBus.Core.Inbox;
using NimBus.Inbox.AzureTables;

var table = new TableClient(
    new Uri("https://<account>.table.core.windows.net"),
    "NimBusInbox",
    new ManagedIdentityCredential(ManagedIdentityId.SystemAssigned));

builder.Services.AddNimBusAzureTableInbox(table);
builder.Services.AddNimBusSubscriber("BillingEndpoint", subscriber =>
{
    subscriber.AddHandler<OrderPlaced, OrderPlacedHandler>();
    subscriber.UseInbox(options => options.DeduplicationStore = InboxStore.AzureTable);
});
```

The managed identity example uses the adapter's `Azure.Identity` package. For local development, pass `new TableClient("UseDevelopmentStorage=true", "NimBusInbox")` and create the table in Azurite first. To run the provider tests, set `NIMBUS_TABLE_TEST_CONNECTION` to the emulator connection string. The suite creates and removes its own isolated tables.

Each endpoint uses one partition. A duplicate check is one point read. A successful handler writes the receipt and a time-ordered index entry in one transaction. Cleanup queries the expired range in that partition and deletes each pair atomically. Only hashes and creation-time information are stored. Each pass examines up to 1,000 expired receipts per subscriber instance. For example, with hourly cleanup on one instance, sustained traffic above 1,000 new receipts per hour can build a cleanup backlog. Shorten `CleanupInterval` to keep pace. A busy endpoint is subject to the throughput limits of one Table Storage partition.

## Processing order

For a usable `MessageId`, the subscriber performs these operations in order:

1. Check whether the ID has already been processed on this endpoint. The check runs before the session-state guards, so a redelivered duplicate is surfaced as a duplicate even when its session state has moved on (for example a successfully handled RetryRequest whose session is already unblocked, or an EventRequest arriving while its session is blocked by a different event).
2. If it is new, invoke the application handler.
3. Only after the handler returns successfully, record the ID.
4. Publish the normal Resolver response and settle the broker message.

A fresh delivery costs exactly one store check and one record: the hosted compositions run the pre-session-guard check in the message handler for every dispatching entry point (event, retry, and resubmission requests), and the decorator at the handler seam then only records successes. A stand-in (see [Retries, resubmissions and deferred replays](#retries-resubmissions-and-deferred-replays)) costs one more record when it succeeds, and a replayed parked copy up to one more check.

A duplicate whose session is no longer blocked at all also triggers the deferred-sibling drain before completing — this covers a crash between the first attempt's session unblock and its deferred drain, where the redelivered duplicate is the only remaining trigger. A session blocked by an unrelated later event is left untouched; that blocker's settlement owns the drain.

Recording before the handler would be unsafe: if the handler then failed, the broker redelivery would look processed and the work would be lost. Record-on-success keeps a failed attempt eligible for redelivery. A check or record store failure also follows the transient-failure path so the broker can redeliver; a store outage is never interpreted as a duplicate.

The legacy deferred-continuation path removes the next deferred message's sequence reference from session state before dispatching it. If the inbox check or record — or any other transient failure — then aborts that dispatch before the message settles, the reference is restored to the front of the session's deferred order, so the redelivered continuation (or a later drain) can still reach the message instead of orphaning it in the broker's deferred state. The restore runs under its own bounded token rather than the caller's: cancellation is itself one of the failure modes that leaves the popped message unsettled, and reusing an already-cancelled token would cancel the session-state write and silently skip the recovery.

When the check finds an existing ID, NimBus does not invoke the application handler. It emits the `OnDuplicateDetected` lifecycle signal, records bounded-cardinality telemetry, publishes a `Skipped` outcome to the Resolver with reason `DuplicateDetected`, and completes the broker message. Logs contain message identity and routing metadata, never the message payload.

Messages with a missing, blank, or longer-than-512-character `MessageId` (or a missing or longer-than-260-character endpoint identity) cannot be safely deduplicated. NimBus logs a warning and invokes the handler without consulting or updating the inbox. It never drops such work. This bypass also holds on the real Service Bus transport, whose message context throws for a `MessageId` absent from the wire — inbox processing, lifecycle notification, and diagnostic logging all read identity through non-throwing accessors.

A handler that signals a pending handoff is **not** recorded in the inbox. The pending response and the session block are established after the handler returns, and both must be recreatable if a crash forces redelivery; recording the message would turn that redelivery into a duplicate skip that never re-establishes the pending state. Pending-handoff idempotency is governed by session blocking and the handoff settlement flow instead.

## Retries, resubmissions and deferred replays

Three flows complete an event with a stand-in, a message that reaches the endpoint with a fresh `MessageId`:

- a RetryRequest scheduled after a failure;
- an operator's resubmission;
- the replay of a message that was parked (deferred) while its session was blocked.

If the inbox recorded only the stand-in's `MessageId`, a later delivery of the original — the at-least-once case the inbox exists for — would run the handler again. So when a stand-in succeeds, the inbox first records the `MessageId` the source delivered the event with, then the stand-in's own:

- A RetryRequest and a parked copy carry the source `MessageId` as the `InboxMessageId` application property. It is set from the delivery that failed or was parked, or carried on from an earlier stand-in (a retry of a retry, a replay parked again).
- A resubmission is built by the Manager and carries none. Blocking a session also stores the blocking delivery's source `MessageId` in the session state (`BlockedByMessageId`), and a resubmission that resolves the event blocking its session records it. That read is best effort: the handler has already succeeded, so a failed read only means the resubmission records its own `MessageId`, as before.

A replayed parked copy is also a duplicate when its `InboxMessageId` was already recorded — for example a second delivery of a message that was parked behind the same block as the first. RetryRequests and resubmissions are checked against their own `MessageId` only: a retry of an event that completed another way is answered by the session guard, and a resubmission remains a deliberate new attempt.

`OriginatingMessageId` is not used for any of this. It names the first message of the whole causal chain, which for an event published from another event's handler (`PublishFromContext`) is that other event's delivery; recording it could skip that delivery on this endpoint.

Stand-ins created before this behaviour shipped carry no `InboxMessageId`, and sessions blocked before it store no `BlockedByMessageId`; those stand-ins record only their own `MessageId`. A pending handoff that the Manager settles as completed runs no handler, so it records nothing either (see pending handoffs above).

## Retention and cleanup

`InboxOptions.RetentionPeriod` defaults to seven days. `CleanupInterval` defaults to one hour. The opt-in cleanup hosted service deletes a bounded batch of records older than `UtcNow - RetentionPeriod` on each pass. Cleanup propagates the host cancellation token, stops promptly during shutdown, and logs a failed pass before trying again on the next interval.

Retention is configured — and enforced — per subscriber endpoint: each subscriber's cleanup service purges only its own endpoint's records (`IInboxStore.PurgeExpiredAsync` takes the endpoint scope). Endpoints with different retention periods can therefore safely share one physical table or container; a one-day-retention subscriber can never delete a seven-day-retention subscriber's still-valid records and reopen its duplicate window.

SQL Server stores records in an independently provisioned inbox table with a **nonclustered** primary key on a 32-byte identity hash — never on the raw `NVARCHAR` pair, whose trailing-space padding under SQL comparison rules would conflate distinct ids — plus the raw identifier columns for diagnostics and a clustered index on the creation timestamp for efficient bounded purges. The purge uses `READPAST` together with `READCOMMITTEDLOCK`, so it also works on databases with `READ_COMMITTED_SNAPSHOT` enabled (the Azure SQL default). Cosmos DB stores inbox documents in a dedicated container keyed by the same identity hash, uses explicit bounded cleanup with ETag-conditioned deletes (a record recreated between the purge query and the delete survives), and does not depend on container TTL. A custom `ICosmosContainerAdapter` must override the options-aware delete overload to run cleanup; the interface default fails closed with `NotSupportedException` rather than silently dropping the ETag precondition. The in-memory provider keeps timestamps in a concurrent map.

### Cosmos DB consistency requirement

The Cosmos provider validates at first use that the account's **effective consistency level is `Strong`** and fails fast otherwise. The duplicate check must observe the latest committed record even when a different process wrote it and crashed before broker settlement; Cosmos session tokens never cross process boundaries, so under Session (or weaker) consistency a redelivery on another replica can read a stale miss, re-run the handler, and silently swallow the conflicting record write. A custom `ICosmosClientAdapter` must override `GetAccountConsistencyLevelAsync` to participate in this validation; the interface default fails closed. `CosmosInboxOptions.AllowRelaxedConsistency` skips the validation for deployments that accept the residual duplicate-side-effect window — for example a single consumer process per endpoint, or local development against the Cosmos emulator (which only offers Session consistency).

There is no numbered data migration or application-table rewrite. The SQL provider uses an idempotent inline schema bootstrap for its new table; set `SqlServerInboxOptions.AutoCreateTable` to `false` only when deployment tooling provisions the same schema ahead of time. The Cosmos provider creates its dedicated container if it is absent. Grant those create permissions during provisioning, or pre-provision the resources before processing starts. The platform template (`deploy/bicep/templates/cosmosDB.bicep`) deliberately does not declare the inbox container: its account uses Session consistency and its id is configurable. A subscriber that authenticates with Entra data-plane RBAC, which cannot create containers, must pre-provision it (partition key `/id`, no default TTL) in a Strong-consistency account.

Choose retention to exceed the longest plausible broker redelivery, replay, and operational recovery window. After a record expires, the same ID is treated as new and can run again.

## Guarantees and remaining windows

The inbox deduplicates redeliveries whose first processing attempt reached the inbox record. It deliberately does not claim exactly-once side effects:

- The handler's database transaction and the inbox write are separate. If handler side effects commit and the process or inbox write fails before the record is durable, redelivery invokes the handler again.
- Two genuinely concurrent first deliveries can both pass the check before either records the ID. Both handlers can run; the provider's record operation is idempotent so both successful writes do not fail.
- A crash after the inbox record but before broker settlement causes a redelivery, but that delivery is recognized and skipped.
- A receiver stop after the handler returns (host shutdown, or the [endpoint circuit](circuit-breaker.md) opening) interrupts neither the record nor the settlement: both run to the end without observing the processor's cancellation (see [Settlement and receiver shutdown](message-flows.md#settlement-and-receiver-shutdown)).

Process-manager transitions must therefore remain idempotent. Derive outgoing message IDs deterministically from stable workflow identity, logical transition, and workflow version or attempt, and make application-side state transitions safe to retry. Do not use the inbox as a substitute for a transaction in the handler's own data store.

See [Orchestration](orchestration.md) for the process-manager conventions and deterministic identity guidance.

The inbox key is the `(endpoint, MessageId)` pair, not a content hash. Separate messages that reuse an ID are treated as the same logical delivery. Conversely, the current Manager resubmit flow creates a new transport message ID, so an operator-requested resubmit remains a deliberate new attempt; when it succeeds, it also records the source `MessageId` of the delivery whose failure it resolves (see [Retries, resubmissions and deferred replays](#retries-resubmissions-and-deferred-replays)). Any replay mechanism that preserves the original ID is skipped while its inbox record remains within retention.
