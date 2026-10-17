# Spec 036 — One Cosmos DB tracking container for all endpoints

Status: **proposed** (2026-09-26). The repo owner decided its open questions on 2026-09-28 (§13).
Revised on 2026-10-05 after a review
([2026-10-04-spec-036-review.md](../../plan/2026-10-04-spec-036-review.md)); §16 lists the changes.
Revised again on 2026-10-08 after a second review of the spec and a review of its implementation
plan ([spec review](../../plan/2026-10-07-spec-036-spec-review.md),
[plan review](../../plan/2026-10-07-spec-036-implementation-plan-review.md)).
Nothing is implemented. Target release: v5.0.0. Implementation plan:
[2026-10-07-spec-036-implementation-plan.md](../../plan/2026-10-07-spec-036-implementation-plan.md).
Decision record: [ADR-017](../../adr/017-single-cosmos-tracking-container.md) (proposed), which
supersedes ADR-008 when accepted.
Baseline: master `f16a0369`; the review rechecked the code at `e9996280`, and the 2026-10-08
revision at `7403c596` (v4.6.0 + 1).
Scope: `NimBus.MessageStore.CosmosDb`, the `nb` CLI (`infra apply`, `topology apply`, `setup`,
`deploy apps`, `container copy` and a new `container migrate`), the WebApp's Cosmos admin paths
(purge, copy, storage containers), its Cosmos request priority and its two purge operations in
`api-spec.yaml`, `deploy/bicep/templates/cosmosDB.bicep` and docs. Unchanged: the members and
signatures of the storage contracts in `NimBus.MessageStore.Abstractions`, the `messages` and
`audits` containers, the Service Bus topology and the message flow. Three prerequisites land
first, outside this spec (§9): an exact endpoint filter in `GetEventsByFilter` on the SQL Server
and in-memory providers (§5.4, done in v4.3.0), a 4.x minor release that reserves
`unresolvedevents`, and a maintenance-branch procedure in `docs/versioning.md`.
Why: the per-endpoint layout (ADR-008) costs each endpoint a fixed minimum throughput. It needs a
control-plane provisioning step per endpoint, which managed identity can't do at runtime, and it
couples endpoint ids to container names. None of the benefits ADR-008 cites is in use today (§2.2).

## 1. Summary

Today every endpoint has its own Cosmos container for its tracking rows (the unresolved, failed,
deferred and completed projections the Resolver writes). This spec replaces those containers with
one container, `unresolvedevents`:

- **Partitioning.** A hierarchical partition key, `/endpointId` then `/id`. Endpoint-scoped queries
  stay routed to the endpoint's own partitions. Every row stays its own logical partition, so no
  endpoint can reach the 20 GB logical partition limit.
- **Isolation by construction.** An internal `EndpointScope` is the only way the store reaches the
  container. It builds the full partition key for point operations, and it builds and runs every
  query with `c.endpointId = @endpointId` as the first conjunct.
- **Provisioning.** Bicep declares the container once. Adding an endpoint no longer touches Cosmos.
- **Purge.** Purging an endpoint deletes its rows in the background instead of deleting a
  container.
- **Cross-endpoint search.** The failed-messages search and its histogram each become one query
  over the requested endpoints, replacing today's per-container fan-out and merge.
- **Priority.** The account turns on priority-based execution. The WebApp, purges and the migration
  run at low priority, so the Resolver's writes go first when the shared budget runs short.
- **Migration.** Existing deployments migrate once and offline with `nb container migrate`, in
  v5.0.0. The legacy containers stay in place for rollback until an operator deletes them. A layout
  guard keeps v5 apps from running on unmigrated data (§6.5).

The main costs are a shared throughput budget (noisy neighbours), isolation that now depends on
code rather than on container boundaries, a purge that costs request units (RUs), a young
emulator feature and a short migration outage. §7 weighs them.

## 2. Current state

### 2.1 Facts

| Aspect | Today |
|---|---|
| Layout | One container per endpoint in `MessageDatabase`. The container id is the endpoint id (ADR-008) |
| Partition key | `/id` (`CosmosContainerDefaults.EndpointPartitionKeyPath`, `CosmosContainerDefaults.cs:15`). The row id is `{eventId}_{sessionId}`, so every row is its own logical partition |
| TTL | Container TTL on, no default (`-1`). Rows carry item TTLs: 30 days for terminal and archived rows, 60 s for soft-deleted rows, `UnresolvedRetentionDays` for unresolved rows |
| Throughput | Never set: not in Bicep, not by `EndpointContainerProvisioner`, not by the SDK calls. A container with dedicated manual throughput has a 400 RU/s minimum (§14 item 1) |
| Creation | (a) `nb topology apply` and `nb setup` call `EndpointContainerProvisioner` (`TopologyCommands.cs:121`, `SetupCommand.cs:137`), which runs `az cosmosdb sql container create`. (b) `CosmosDbClient.GetEndpointContainer` (`CosmosDbClient.cs:203`) creates the container lazily. This works only with account keys, because data-plane RBAC can't create containers; under managed identity the first message on an unprovisioned endpoint fails with 403. (c) `nb container copy` and the WebApp's Copy Endpoint Data create the container in the target account |
| Naming rules | An endpoint id may not equal one of the 13 reserved container ids (`CosmosContainerDefaults.ReservedContainerIds`, `CosmosContainerDefaults.cs:31`). Five call sites check this |
| Access | The Resolver and WebApp identities hold Cosmos DB Built-in Data Contributor at account scope (`roleAssignments.bicep:63-67`). Nothing is granted per container |
| Endpoint purge | Deletes the endpoint container, then drops the cached handle so the next access re-creates it (`CosmosDbMessageTrackingStore.Writes.cs:271`). Two WebApp entry points call it, and both await it inside the HTTP request: the endpoint page's purge, refused in Production and Staging unless the caller is a site Owner (`EndpointImplementation.cs:476-519`), which then runs `ClearEndpoint` to delete and re-create the endpoint's Service Bus subscription (`EndpointManagement.cs:18-27`); and Operations → Delete all events, which requires a site Owner, works in every environment and writes no audit entry (`AdminImplementation.cs:228`) |
| Cross-endpoint reads | The failed-messages page and the histogram query each endpoint container, eight at a time, and merge the results (`CosmosDbMessageTrackingStore.Search.cs:194`) |
| Storage admin | Topology → Storage lets a site Owner delete any container that is neither reserved nor named after a catalog endpoint (`AdminImplementation.cs:325`). It deletes through ARM when `CosmosAccountResourceId` is set (`ArmCosmosContainerAdmin`) |
| SQL Server provider | One `UnresolvedEvents` table with an `EndpointId` column and indexes `(EndpointId, Status)`, `(EndpointId, SessionId, Status)` and `(EndpointId, UpdatedAtUtc DESC)` (`Schema/0003_Events.sql:37-39`) |
| Upstream | DIS, which NimBus is forked from, keeps per-endpoint containers (`BH.DIS.MessageStore/CosmosDbClient.cs`) |

### 2.2 What ADR-008's benefits amount to today

| ADR-008 benefit | Today |
|---|---|
| Queries for one endpoint don't scan other endpoints' data | True. A partition key that starts with the endpoint gives the same routing inside one container (§5.1) |
| Throughput can be provisioned per endpoint | Never used. No code path or template sets throughput on an endpoint container, so each one carries the same minimum whether it is busy or idle |
| Purge is a container delete | The deployed apps authenticate with data-plane RBAC. Deleting and lazily re-creating a container are management operations that data-plane RBAC doesn't permit, so this path most likely works only with account keys (§14 item 6) |
| Copy can target one endpoint without filtering | The copy tools already build filtered queries (`AdminService.Copy.cs`, `Container.cs:77`), so a filter on `endpointId` is one more condition |

## 3. Goals

1. One tracking container for all endpoints, declared statically. Adding, renaming or removing an
   endpoint never creates or deletes Cosmos resources.
2. Every `IMessageTrackingStore` member keeps its observable behaviour. The conformance suite pins
   this, and new cross-endpoint isolation tests extend it (§8).
3. Endpoint isolation is enforced by one code path that callers can't bypass, not by convention.
4. Endpoint-scoped reads stay targeted as the container grows, with no full fan-out.
5. Existing Cosmos deployments get a convergent, verifiable migration, a guard that keeps v5 apps
   off unmigrated data, and a rollback path.
6. Tracking throughput cost follows aggregate load, not endpoint count.

## 4. Non-goals

- The other platform containers (`messages`, `audits`, `subscriptions`, `Metadata` and the rest).
  Consolidating the small ones is a possible follow-up, not part of this change.
- Shared database throughput or a serverless account. Microsoft recommends container-level
  throughput for most workloads, and an existing account can't be converted from provisioned to
  serverless (§12).
- The SQL Server and in-memory providers. They already use one store per concern.
- The row id format `{eventId}_{sessionId}`. It appears in API responses and URLs.
- A zero-downtime migration through the change feed (§12).
- Throughput or TTL tuned per endpoint.

## 5. Design

### 5.1 The container

| Property | Value |
|---|---|
| Id | `unresolvedevents`, in `MessageDatabase`. It mirrors the SQL Server table name |
| Partition key | Hierarchical: `["/endpointId", "/id"]`, kind `MultiHash`, version 2 |
| TTL | `defaultTtl: -1`, which turns TTL on with item-level expiry, exactly as endpoint containers have it today |
| Throughput | Autoscale. The max comes from a new Bicep parameter, pinned to the deployed value by `nb infra apply` like the existing plan and SKU options (§5.6). Default: 4,000 RU/s (§13 item 3) |
| Indexing | The default policy (all paths). Every query keeps today's single-property `ORDER BY`, which the default range indexes serve, so no composite index is needed for correctness. Composites are an optional optimization. A Cosmos composite serves a filter plus `ORDER BY` only when the `ORDER BY` lists the equality-filtered properties first, and Cosmos rejects a multi-property `ORDER BY` that no composite matches exactly, directions included. So an optimized query adds the leading terms itself (for example `ORDER BY c.endpointId DESC, c.sessionId DESC, c.event.UpdatedAt DESC` for the session list, `Search.cs:423`), and the exactly matching composite is declared in `cosmosDB.bicep` and in lazy creation. Phase 0 decides which optimizations pay for their write cost on the Resolver's path, measured on a live account; the emulator ignores indexing policies (§14 item 5). A unit test maps every multi-property `ORDER BY` the store emits to a composite in the Bicep policy, because CI can't catch a missing one (§8) |

Why `id` is the second level:

- **No signature changes.** Every point operation already has both values: the `endpointId`
  argument and the row id.
- **Targeted reads.** A query whose `WHERE` clause pins the first level runs only against the
  physical partitions that hold that prefix
  ([hierarchical partition keys](https://learn.microsoft.com/azure/cosmos-db/hierarchical-partition-keys)).
- **No 20 GB ceiling.** With `id` last, every logical partition holds one row, so no endpoint can
  reach the per-logical-partition limit. Microsoft recommends the item id as the last level for
  this reason.
- **Same row semantics.** Row ids only need to be unique per full key. The same
  `{eventId}_{sessionId}` on two endpoints stays two rows, as it is today when one event reaches
  several subscribers.

Three levels (`/endpointId`, `/sessionId`, `/id`) were rejected (§12).

### 5.2 Document changes

- `EventDbo` gains `[JsonProperty("endpointId")] string EndpointId`. The store sets it from the
  `endpointId` argument of every write, which is the value that selects the container today. It
  never takes it from `UnresolvedEvent.EndpointId`.
- `EventDbo` also gains `[JsonProperty("updatedAtTicks")] long UpdatedAtTicks`, which is
  `event.UpdatedAt` in ticks. The store sets it wherever it writes `event.UpdatedAt`. It exists for
  the single failed-search query (§5.4). Cosmos sorts `event.UpdatedAt` as its ISO string, and the
  default serializer trims trailing zeros from the fraction, so that order isn't chronological
  within a second. Today's `FailedEventPageCursor` merge compares ticks instead.
- Rows that `nb container migrate` copies also carry `migratedFromTs` and `migratedFromEtag`, the
  source row's `_ts` and `_etag` (§6.1). Every v5 write omits or nulls both (§5.3), so a row whose
  `migratedFromTs` is defined and non-null is one nothing has changed since the copy. The ETag
  identifies the source version exactly; `_ts` has one-second resolution, so it can't tell two
  changes within one second apart.
- The partition key is `new PartitionKeyBuilder().Add(endpointId).Add(id).Build()`.
- `EventDbo` is a private nested class today (`CosmosDbMessageTrackingStore.cs:85`). It and the
  query projection types become internal types, so the scopes (§5.3) can use them.
- The row id, the embedded `event` document and the server-side projections (`ProjectForSearch` and
  the paging projection) are unchanged. The projections already carry `event.EndpointId`.

### 5.3 Scopes: the only way into the container

The store reaches `unresolvedevents` only through two scopes, which an internal
`TrackingContainerAccessor` hands out. The accessor is the only code that holds the container
adapter for `unresolvedevents`.

```csharp
internal sealed class TrackingContainerAccessor
{
    public Task<EndpointScope> ForEndpointAsync(string endpointId);
    public Task<EndpointSetScope> ForEndpointsAsync(IReadOnlyCollection<string> endpointIds);
}

internal sealed class EndpointScope
{
    public string EndpointId { get; }

    // Point operations take row ids and build the full key (EndpointId, id) themselves.
    // Create, Upsert and Replace stamp endpointId and updatedAtTicks and omit migratedFromTs and
    // migratedFromEtag. PatchAsync appends PatchOperation.Set("/migratedFromTs", null) and
    // PatchOperation.Set("/migratedFromEtag", null) and never touches key paths.
    public Task<ItemResponse<EventDbo>> ReadAsync(string id, ItemRequestOptions? options = null);
    public Task<ItemResponse<EventDbo>> CreateAsync(EventDbo row, ItemRequestOptions? options = null);
    public Task<ItemResponse<EventDbo>> UpsertAsync(EventDbo row, ItemRequestOptions? options = null);
    public Task<ItemResponse<EventDbo>> ReplaceAsync(EventDbo row, ItemRequestOptions options);
    public Task<ItemResponse<EventDbo>> PatchAsync(string id, IReadOnlyList<PatchOperation> operations,
        PatchItemRequestOptions? options = null);
    public Task<ItemResponse<EventDbo>> DeleteAsync(string id);
    public Task<FeedResponse<EventDbo>> ReadManyAsync(IReadOnlyList<string> ids);

    // Queries: the scope builds the statement's head and runs the query.
    public FeedIterator<T> Query<T>(ScopedQuery query, string? continuationToken = null,
        QueryRequestOptions? options = null);
    public FeedIterator<T> Linq<T>(Func<IQueryable<EventDbo>, IQueryable<T>> shape,
        string? continuationToken = null, QueryRequestOptions? options = null);
}

internal sealed record ScopedQuery(
    string Select,
    string? Where = null,
    string? GroupBy = null,
    string? OrderBy = null,
    string? OffsetLimit = null,
    IReadOnlyDictionary<string, object?>? Parameters = null);
```

- `CosmosDbMessageTrackingStore` takes a `TrackingContainerAccessor` instead of
  `Func<string, Task<ICosmosContainerAdapter>>` (`CosmosDbMessageTrackingStore.cs:20`). It keeps
  its adapters for `messages`, `audits` and `eventreports`, which hold no tracking rows. Its method
  bodies never see the `unresolvedevents` container. Private helpers that query it today take a
  scope instead, for example `ExactOldestFailureAt` (`EndpointState.cs:63`).
- `Query` emits `SELECT {Select} FROM c WHERE c.endpointId = @__endpointId [AND ({Where})]
  [GROUP BY {GroupBy}] [ORDER BY {OrderBy}] [{OffsetLimit}]` and runs it with
  the caller's `QueryRequestOptions` and continuation token. That covers every query the store runs
  against endpoint containers today: 13 `GetItemQueryIterator<T>` sites with projections such as
  `IdProjection`, `StatusQueryResult`, `BlockedEventProjection`, `int` and `DateTime`;
  `GROUP BY c.status` (`EndpointState.cs:16`); `ORDER BY … OFFSET @skip LIMIT @take`
  (`Search.cs:423`); and `MaxItemCount = 1` (`Lookups.cs:84`).
- The slots are constants in the store's code. Values travel only as parameters, and the scope
  rejects parameter names that use its reserved `@__` prefix. `OffsetLimit` must have the form
  `OFFSET @x LIMIT @y`.
- The scope passes `ORDER BY` through unchanged. Prepending `c.endpointId` would turn every
  ordering into a multi-property one, which Cosmos rejects unless a composite index matches it
  exactly (§5.1). A query that Phase 0 optimizes with a composite writes its leading terms itself.
- `Linq` applies `.Where(x => x.EndpointId == EndpointId)` before the caller's shape. It then turns
  the queryable into an iterator through a `ToFeedIterator<T>(IQueryable<T>)` seam that
  `ICosmosContainerAdapter` gains, so the recording adapter sees the `QueryDefinition` the SDK
  would send (§8).
- The predicate is **equality**, never `STARTSWITH`. `Billing` must not match `BillingV2`, the same
  trap that `SearchAudits_EndpointIdExact_excludes_prefix_siblings` pins for audits.
- The predicate also routes the query. Microsoft's guidance is to put prefix values in the
  `WHERE` clause, because supplying them only through `PartitionKeyBuilder` doesn't guarantee
  efficient routing.
- The scope doesn't set a prefix `QueryRequestOptions.PartitionKey` as a second fence. The `WHERE`
  predicate already routes the query, and request-option prefix scoping has had SDK and emulator
  defects (§12, §14 items 2 and 4).
- The endpoint id must be non-empty, as today. The reserved-name check is no longer needed.

The cross-endpoint failed search and its histogram (§5.4) read several endpoints in one query. They
use a set form of the scope, which has queries only:

```csharp
internal sealed class EndpointSetScope
{
    public IReadOnlyList<string> EndpointIds { get; }   // distinct, non-empty, at least one

    public FeedIterator<T> Query<T>(ScopedQuery query, string? continuationToken = null,
        QueryRequestOptions? options = null);
    public FeedIterator<T> Linq<T>(Func<IQueryable<EventDbo>, IQueryable<T>> shape,
        string? continuationToken = null, QueryRequestOptions? options = null);
}
```

- `Query` emits `SELECT {Select} FROM c WHERE ARRAY_CONTAINS(@__endpointIds, c.endpointId)
  [AND ({Where})] [GROUP BY {GroupBy}] [ORDER BY {OrderBy}] [{OffsetLimit}]`, and `Linq` applies
  the same set filter.
- The match is exact: `ARRAY_CONTAINS` compares whole strings, so prefix siblings stay excluded.
  Phase 0 compares it with `c.endpointId IN (@e1, …)` on a split container (§14 item 4), and the
  set scope emits whichever routes better.
- The set holds exactly the endpoint ids the caller passed, which the WebApp has already filtered to
  those the user may read.

The copy tools reach the container through the same scopes, via the public copy type in
`NimBus.MessageStore.CosmosDb` (§5.7).

The migration (§6.1) needs writes the endpoint scope deliberately doesn't offer: it must keep
`migratedFromTs` and `migratedFromEtag` and delete stale copies. So the accessor also hands out an
internal `MigrationScope`, bound to one endpoint id, for `nb container migrate` only. It builds the
full key, stamps `endpointId` from its own binding, keeps both stamps, and offers create, `IfMatch`
replace and `IfMatch` delete. Its reads and verify queries go through `EndpointScope.Query`, so
the recorded-query and key tests cover them too (§8). The accessor hands out the endpoint and set
scopes only once the layout guard reports ready (§6.5); the migration scope bypasses the guard,
because the migration runs while the layout is pending.

### 5.4 Operation by operation

| Store members | Today | Proposed |
|---|---|---|
| `GetPendingEvent`, `GetFailedEvent`, `GetDeferredEvent`, `GetDeadletteredEvent`, `GetUnsupportedEvent`, `GetEventById` | Point read, key `id` | Point read, key `(endpointId, id)` |
| `UploadPendingMessage`, `UploadDeferredMessage` (guarded writes, Spec 030) | Read, then create or ETag-conditional upsert | The same with the full key. The compare-and-swap is unchanged |
| `UploadFailedMessage`, `UploadDeadletteredMessage`, `UploadUnsupportedMessage`, `UploadCompletedMessage`, `UploadSkippedMessage` | Upsert | Upsert with `endpointId` stamped |
| `TrySkipDeferredMessage`, `TryCompletePendingMessage` | Read, then conditional replace or upsert | The same with the full key |
| `TryArchiveUnresolvedEvent`, `TryRestoreArchivedEvent` (Spec 035, added after this spec's baseline) | Read, then `IfMatch` replace that flips `deleted` and `ttl` (`Writes.cs:70-110`) | The same with the full key. The replace goes through `EndpointScope.ReplaceAsync`, so it clears both migration stamps. While the layout is pending they throw like every tracking member, so the operator tools that call them (`OperatorCommandCoordinator.cs:351,391`, REST and MCP) refuse with the guard's error before any side effect |
| `RemoveMessage`, `ArchiveFailedEvent` | Patch | Patch with the full key |
| `GetEventsByIds` | `ReadManyItemsAsync` with `(id, key(id))` pairs | `(id, key(endpointId, id))` pairs |
| `DownloadEndpointStateCount`, `DownloadEndpointStatePaging`, `GetEventsByFilter`, `GetCompletedEventsOnEndpoint`, `GetEndpointErrorList`, `GetInvalidEventsOnSession`, `GetPendingEventsOnSession`, `GetEvent(endpointId, eventId)`, `GetPendingHandoffByExternalJobId`, `GetNextPendingHandoffEvent` | Query over the whole endpoint container | The same query with the endpoint predicate, routed by prefix |
| `DownloadEndpointSessionStateCount`, `DownloadEndpointSessionStateCountBatch`, `GetBlockedEventsOnSession`, `PurgeMessages(endpointId, sessionId)` | Query by `sessionId` | The same with the endpoint predicate |
| `GetFailedEventsAcrossEndpoints`, `GetFailedEventHistogram` | One query per endpoint container, eight at a time, merged by `FailedEventPageCursor` | One query each through an `EndpointSetScope` (§5.3). The search orders by `updatedAtTicks` descending (§5.2) and pages with the query's own continuation token, wrapped as described below. The histogram reads `endpointId` from the rows and keeps its 50,000-row cap. `FailedEventPageCursor`'s merge and the per-endpoint fan-out are deleted (§13 item 7) |
| `PurgeMessages(endpointId)` | Deletes the container | §5.5 |

**The endpoint filter of `GetEventsByFilter`.** *Done: #196 (`a1266587`), shipped in v4.3.0. The
contract now documents an exact match, SQL Server uses `EndpointId = @EndpointId`
(`SqlServerMessageTrackingStore.Search.cs:160`), in-memory uses an ordinal-ignore-case equality
(`InMemoryMessageStore.cs:308`), and `GetEventsByFilter_matches_endpointId_exactly_not_by_prefix`
pins it. The rest of this paragraph records why it was needed.* The contract documented
`EventFilter.EndPointId` as a case-insensitive prefix, and the SQL Server and in-memory providers
implemented it that way. Cosmos is exact, because the endpoint id selects the container, and it
stays exact through the scope. On SQL Server the prefix match leaked rows in two ways:

- The WebApp authorizes Reader on the route endpoint and passes that endpoint as the filter
  (`EventImplementation.Search.cs:32,65`), so a Reader on `Billing` sees `BillingV2` rows.
- The admin bulk skip pages the filter and rewrites what it finds as Skipped rows under the endpoint
  it was asked to skip (`AdminService.Purge.cs:549-560`), so it also rewrites sibling endpoints'
  events.

The prerequisite fix (§9) made the endpoint filter exact on every provider and updated the contract
comment. The other ID-like filters stay prefix matches. The isolation conformance tests (§8) depend
on this fix.

**Paging the failed search.** The single query keeps the guarantees `FailedEventPageCursor` gives
today:

- **Exact bounds.** `UpdatedAtFrom` and `UpdatedAtTo` apply on the server to `updatedAtTicks`,
  exactly. That removes the in-memory `WithinDateBounds` post-filter for these bounds, so rows it
  would have dropped no longer shorten a page. Enqueued bounds, which the Failed page doesn't set,
  keep today's widened server filter and exact in-memory check.
- **Exact `hasMore`.** The store reads `pageSize + 1` rows, as the SQL Server provider does. It
  returns a token only when more rows exist. The token wraps the Cosmos continuation token with the
  number of rows of that Cosmos page already returned (`{token, skip}`), the shape the current
  cursor uses. So an exactly full last page carries no token, and the error groups' `Truncated`
  flag stays exact at 5,000 rows. A small codec for this token replaces `FailedEventPageCursor`.
- **Lenient decoding.** A token the store can't parse, or one Cosmos rejects with 400, restarts at
  page 1, as today. `CosmosExceptionTranslation` doesn't map 400, so without this rule a stale
  token would surface as a 500. Tokens issued before the cutover are invalid afterwards, so open UI
  pages restart their lists once.
- **Ties.** Rows with equal `updatedAtTicks` keep the order of the Cosmos continuation, which
  neither skips nor repeats them. No secondary sort key is needed: the conformance suite allows
  short pages and pins every row exactly once.

### 5.5 Purging an endpoint

**In the store.** `PurgeMessages(endpointId)` deletes the rows that existed when the purge started,
and only those. Today's container delete takes a few seconds, so live traffic barely overlaps it.
A purge row by row can run for an hour, so it has to cope with the Resolver writing the same
endpoint meanwhile:

- It records `purgeStart`, the current Unix time in seconds, before its first read.
- It pages `SELECT c.id, c._etag FROM c WHERE c.endpointId = @e AND c._ts < @purgeStart`. A row
  written in the start second or later is a row the purge didn't mean to delete, so it survives.
- It deletes each row `IfMatch` its `_etag`, with bounded parallelism. A `412` means the Resolver
  rewrote the row after the scan read it, so the row survives and counts as kept. A `404` counts
  as done: another purge or TTL got there first.
- The session purge (`Writes.cs:234`) already pages and deletes; it adopts the same 404 rule. It
  stays unconditional, as today.
- The endpoint purge uses a lower default parallelism than the session purge's 8 (proposed: 2), so
  it can't saturate the budget the Resolver writes against.

The method returns `false` on any other failure, as its contract says today, and a rerun deletes
whatever is left. Its result distinguishes nothing more: the counts of deleted and kept rows go
into the purge's audit entry (below).

**In the WebApp: a background operation.** A container delete returns quickly; a delete per row
doesn't. At parallelism 2, an endpoint with 100,000 rows takes minutes and one with 1,000,000 rows
about an hour. App Service ends a request after about 230 seconds
([request timeout](https://learn.microsoft.com/troubleshoot/azure/app-service/web-request-times-out-app-service)),
so the purge can't run inside the request:

- Both entry points authorize and validate as today. They then:
  1. take the endpoint's purge lease (below), or return `409 Conflict`;
  2. write the start audit entry through the required audit path
     (`IAuditLogService.LogRequiredAuditAsync`), which ignores the audit selection (Settings → Compliance) and
     throws when the row can't be persisted. If it throws, they release the lease and return `503`
     without purging. A purge never runs unaudited;
  3. capture the caller's identity, queue the purge on an in-process background worker and return
     `202 Accepted`.
- **Order of work for the endpoint page's purge.** The worker runs `ClearEndpoint` first, then
  `PurgeMessages(endpointId)`. Clearing first drops the messages queued for the endpoint before
  the row scan starts. Rows the Resolver writes afterwards, for messages already in flight, carry a
  `_ts` at or after `purgeStart` or fail the `IfMatch`, so they survive and reflect real work. If
  `ClearEndpoint` fails, the worker doesn't purge rows. Operations → Delete all events doesn't clear the
  subscription, as today.
- The worker runs in its own `IServiceScope`, because the request's `HttpContext` and DI scope are
  gone once the `202` is sent (`EndpointImplementation.cs:70` captures the context per request).
  It writes the outcome audit entry (success or failure, deleted and kept row counts, duration)
  through a new `LogRequiredAuditAsync` overload that takes the captured identity as the auditor
  and needs no `HttpContext`. If that write fails, the worker retries it with backoff and logs an
  error that carries the outcome, so the result is never lost silently.
- **One at a time, across instances.** The purge lease is a document `purge-lease-<endpointId>` in
  the `settings` container. Taking it is a create; a live lease makes the create fail with `409`,
  which the API returns. The holder renews it every minute (`leaseUntil` = now + 3 minutes, `IfMatch`
  its ETag), and a lease whose `leaseUntil` has passed can be taken over `IfMatch`, so a crashed
  instance doesn't block the endpoint for longer than three minutes. The worker deletes the lease
  when it finishes. Today's per-request execution has no such exclusion; it didn't need one while a
  purge took seconds.
- **Cancellation.** `PurgeMessages` takes no cancellation token, and the contract stays unchanged,
  so a purge stops only when the process stops. The audit log then shows a start without an
  outcome, the lease expires, and a rerun deletes what is left.
- **Progress.** The endpoint's counts fall as rows go, and the outcome lands in the audit log.
- **API.** In `api-spec.yaml`:
  - `post-endpoint-purge` and `post-admin-delete-all` return `202` instead of `200`, plus `409` for
    a purge already running and `503` when the start can't be audited.
  - `post-admin-delete-all` drops its `BulkOperationResult` body.
  - The endpoint purge's failure no longer surfaces in the response, because it happens after the
    `202`; the audit log records it.

  The v5.0.0 release notes list these under Breaking.

Other points:

- **Delete by partition key isn't an option.** It is in public preview and needs the
  `DeleteAllItemsByPartitionKey` account capability. On hierarchical containers it accepts only the
  full key; a prefix delete errors
  ([delete by partition key](https://learn.microsoft.com/azure/cosmos-db/how-to-delete-by-partition-key)).
  Here that would delete one row per call.
- **Cost.** Each delete is charged like a write and draws on the shared budget. Both purge entry
  points run in the WebApp, so their requests run at low priority (§5.10).
- **Who can purge in production.** The endpoint page's purge is refused in Production and Staging
  unless the caller is a site Owner (`EndpointImplementation.cs:490-498`). Operations → Delete all events
  requires a site Owner in every environment. So site Owners have two purge paths in production,
  and both now cost throughput. Both confirmations say that the purge runs in the background, takes
  time and consumes throughput in proportion to the endpoint's rows.
- **Behaviour change.** Deleting a container was all-or-nothing, and it also deleted rows written
  while it ran. The new purge can stop part-way; it reports failure in the audit log, and a rerun
  finishes the job. It never deletes a row written after it started.
- **Side benefit.** Purge no longer needs container-management rights, so it works under
  data-plane RBAC (§2.2).

### 5.6 Provisioning and runtime resolution

- **Bicep.** Declare `unresolvedevents` beside `messages` and `audits` in `cosmosDB.bicep`. The
  container API version the template already uses (`2021-06-15`) lists `MultiHash` in its schema;
  §14 item 3 confirms a real deployment.
- **Autoscale parameter.** A new `cosmosTrackingMaxThroughput` Bicep parameter (default 4,000)
  sets the max. `nb infra apply` and `nb setup` expose it as `--cosmos-tracking-max-throughput`.
  When the flag is absent and `unresolvedevents` exists, `nb infra apply` reads its deployed max
  (`az cosmosdb sql container throughput show`) and pins it, as it pins the plan and SKU, so a
  routine apply never resets an operator's setting. When the container doesn't exist yet, as on a
  fresh deployment or the first v5 apply of an upgrade (§6.2 step 2), it uses the default.
  `deploy.yml` and the Azure Pipelines template pass the flag through.
- **The Resolver's paused trigger survives `nb infra apply`.** The Function App templates replace
  the app settings wholesale (`functionApp.bicep:84`, `flexConsumptionFunctionApp.bicep:81`), so
  today a routine apply would clear `AzureWebJobs.Resolver.Disabled` and resume a paused Resolver
  mid-migration. Both templates read the app's current settings and carry that one setting forward,
  the way `webApp.bicep` already preserves `AzureAd__*` and `ServiceBusManagement__*`
  (`InfrastructureDeployer.cs:531-534`). With that, a pipeline that starts during the cutover can't
  resume the Resolver: its `infra apply` keeps the trigger paused, and its `deploy apps` refuses
  while the layout is pending (§6.5).
- **CLI.** `nb topology apply` and `nb setup` stop provisioning Cosmos containers, and
  `EndpointContainerProvisioner` is deleted. The `--storage-provider` option of `nb topology apply`
  existed only for that step. It is still accepted through v5.x, with a deprecation warning, and
  removed in v6.0.0.
- **Runtime.** `CosmosDbClient` resolves `unresolvedevents` through `GetCachedContainerAsync`, like
  every other platform container. Lazy creation still serves key-authenticated development and the
  emulator. `GetEndpointContainer`, its reserved-id rejection and the `_removeEndpointContainerCache`
  callback go away.
- **Lazy creation uses autoscale.** Created without throughput settings, the container would get
  400 RU/s of manual throughput, below today's N × 400 RU/s, and a Bicep deploy can't convert manual
  throughput to autoscale afterwards. So `ICosmosDatabaseAdapter.CreateContainerIfNotExistsAsync`
  gains a throughput argument, and the client creates `unresolvedevents` with
  `ThroughputProperties.CreateAutoscaleThroughput(4000)`. Outside the emulator it also logs a
  warning, because `nb infra apply` should have created the container. Under managed identity,
  creation fails with 403 as before.
- **Layout guard.** The provider checks the migration marker before it serves tracking requests
  (§6.5).
- **Reserved ids.** `CosmosContainerDefaults.ReservedContainerIds` gains `unresolvedevents`. A 4.x
  release (v4.7.0) reserves it too, before v5.0.0 ships, so a v4 WebApp's Storage page protects it
  during the cutover and after a rollback (§6.3, §9).
- **Bicep sync test.** `CosmosBicepContainerSyncTests` (#152) requires every reserved id to be
  declared in `cosmosDB.bicep` with the store's partition key. It parses only single-path keys, so
  it has to learn the `MultiHash` path list and expect `["/endpointId", "/id"]` for
  `unresolvedevents`. The Bicep comment saying per-endpoint containers can't be declared statically
  goes as well.
- **Adapter caveat.** The default `ICosmosDatabaseAdapter.CreateContainerIfNotExistsAsync(ContainerProperties)`
  implementation in `CosmosAbstractions.cs` forwards only the single `PartitionKeyPath` when the
  properties carry no `DefaultTimeToLive`, and throws when they do. The tracking container sets
  TTL, so a custom adapter must override it and forward the full `ContainerProperties`, which
  carries `PartitionKeyPaths`, and the throughput (§5.9). The production adapters already forward
  the properties; the test fakes must too.

### 5.7 Copy Endpoint Data (WebApp and `nb container copy`)

- The tracking-row half of the copy moves into `NimBus.MessageStore.CosmosDb`, next to the
  migration logic (§6.1), as a public type. It reads the source through an `EndpointScope` and
  writes the target's `unresolvedevents` with the full key.
- `AdminService.CopyEndpointDataAsync` (`AdminService.Copy.cs:17`) and `Container.CopyEndpointData`
  (`Container.cs:77`) call it instead of building their own SQL. They live in assemblies that can't
  reach the internal scopes, and a missing or `STARTSWITH` predicate in either would copy other
  endpoints' rows into another account. This way the endpoint predicate has one implementation,
  which the recorded-query test covers (§8).
- Both accounts must use the new layout. The tools resolve `unresolvedevents` in both accounts
  without creating it, check its partition key definition, and refuse when either is missing or
  isn't `MultiHash` `["/endpointId", "/id"]`. Lazy creation in a legacy source account would
  otherwise create an empty container and copy nothing. Moving data from a legacy account is the
  job of `nb container migrate` (§6).
- Copying `messages` is unchanged.

### 5.8 Topology → Storage

- `IsProtectedContainer` (`AdminImplementation.cs:325`) keeps protecting reserved ids, which now
  include `unresolvedevents`.
- It stops protecting containers merely because they are named after a catalog endpoint, with one
  exception: a legacy endpoint container stays protected until the migration marker (§6.5) lists it
  as migrated or excluded. An excluded container can't hold unresolved rows of a catalog endpoint,
  because `--exclude` refuses those (§6.1 step 2), so deleting it loses at most terminal history.
  The page labels excluded containers "not migrated" so the operator sees that before deleting.
- The page reads the marker through the public `TrackingLayout` reader (§6.5), once per request.
  `IsProtectedContainer` is a synchronous check over ids today (`AdminImplementation.cs:325`), so it
  takes the loaded marker as an argument.
- Once migrated, legacy containers show as outside the platform, and the existing audited delete
  removes them. That is the cleanup path after a migration.
- The WebApp's managed identity holds the Azure Cosmos DB Operator role on `MessageDatabase` only
  for this page. The `CosmosAccountResourceId` setting, which selects `ArmCosmosContainerAdmin`,
  exists for the same reason. When the legacy containers are gone, orphaned endpoint containers
  can no longer appear.
- **Retirement (§13 item 5): deprecated in a 5.x minor, removed in v6.0.0.** Removing a page and an
  API route is a change a consumer can observe, which `docs/versioning.md` reserves for a major. So:
  - **A later 5.x minor deprecates** the page and its API. The page shows a notice that it goes in
    v6.0.0 and points to `az cosmosdb sql container delete`. The operations under
    `/api/admin/storage/cosmos/containers` are marked `deprecated: true` in `api-spec.yaml`. The
    public `ICosmosContainerAdmin` and `CosmosContainerAdmin` in `NimBus.MessageStore.CosmosDb`
    become `[Obsolete]`. The release notes list all three under Compatibility. Everything keeps
    working, so the role assignment and the `CosmosAccountResourceId` setting stay.
  - **v6.0.0 removes** the page, the API operations, `ICosmosContainerAdmin`,
    `CosmosContainerAdmin`, the WebApp's Cosmos DB Operator role assignment and the
    `CosmosAccountResourceId` setting from the Bicep, and lists them under ⚠️ Breaking.
    Deployments don't delete role assignments that the template stops declaring, so operators remove
    the existing one themselves. Legacy containers left after that are deleted in the Azure portal or
    with `az cosmosdb sql container delete`.

### 5.9 Public API and configuration

- `CosmosContainerDefaults` (public) gains `TrackingContainerId`, `TrackingPartitionKeyPaths` and
  `TrackingContainer()`.
- `EndpointPartitionKeyPath`, `EndpointContainer(string)` and `EnsureNotReservedEndpointId(string)`
  become `[Obsolete]`, with their current behaviour as the bridge, and are removed in v6.0.0
  ([versioning](../../versioning.md)). Release builds treat compiler warnings, CS0618 included, as
  errors (`Directory.Build.props:49`), so they become obsolete only once no code in `src/` calls them,
  and the tests that pin the bridges (`CosmosContainerDefaultsTests.cs:18,43,53`) suppress CS0618
  locally. `EndpointContainerDefaultTimeToLive` keeps its value
  and also serves the tracking container.
- Three public, documented types in `NimBus.MessageStore.CosmosDb`: the migration logic (§6.1), the
  tracking-row copy (§5.7) and the `TrackingLayout` reader (§6.5).
- `CosmosDbMessageStoreOptions` gains `RequestPriority` (`PriorityLevel?`). When it is unset, the
  account default applies, which is High. It binds from `NimBus:Cosmos` like the other options, and
  the WebApp sets it to `Low` (§5.10). How it applies depends on who creates the client:
  - **The default registration.** The client is built by the DI factory in
    `AddCosmosDbMessageStore()` (`CosmosDbMessageStoreBuilderExtensions.CreateCosmosClient`), which
    reads only `IConfiguration` today. The factory resolves the bound options and copies
    `RequestPriority` into `CosmosClientOptions.PriorityLevel`. Every request on that client gets
    the priority, including those of `CosmosContainerAdmin`, the WebApp's copy reads and
    Integration Intelligence, which share it.
  - **A host-built client.** The `AddCosmosDbMessageStore(builder, CosmosClient, …)` overloads
    can't change that client's options. Instead, the store's adapters set
    `RequestOptions.PriorityLevel` on each of the store's own requests; a request-level priority
    overrides the client's.
  - **The CLI.** `nb container migrate` builds its client with `PriorityLevel = Low` through the
    CLI's client factory (`CommandRunner.CreateCosmosClient`), which gains the option.
- Unchanged: the members and signatures of `IMessageTrackingStore` and every other storage
  contract, and the existing configuration keys. The prerequisite fix narrows the documented
  meaning of `EventFilter.EndPointId` (§5.4). `api-spec.yaml` changes only for the two purge
  operations (§5.5) and a documented `503` problem response on the tracking operations while the
  layout is pending (§6.5).
- The public adapter interfaces in `CosmosAbstractions.cs` gain three members:
  - `ICosmosContainerAdapter.ToFeedIterator<T>(IQueryable<T>)`, whose default calls the SDK
    extension (§5.3).
  - An `ICosmosDatabaseAdapter.CreateContainerIfNotExistsAsync` overload that takes
    `ThroughputProperties?` (§5.6). Its default fails closed: it throws when throughput is
    non-null, like today's default does for container settings. A silent fallback would recreate
    the 400 RU/s manual container §5.6 avoids.
  - `ICosmosDatabaseAdapter.GetContainersAsync(CancellationToken)`, returning each container's
    `ContainerProperties` (id and partition key definition), for the layout guard (§6.5).
    `CosmosDbClient` can be built from an `ICosmosClientAdapter` alone (`CosmosDbClient.cs:105`), so
    the guard can't fall back to a raw `CosmosClient`. The default throws `NotSupportedException`.
    The guard catches it and reports the layout as pending with a reason that names the member, so
    a store on an adapter without it fails closed: it throws the guard's exception and never serves
    tracking data from an unchecked layout.
- A public `TrackingLayoutPendingException` in `NimBus.MessageStore.CosmosDb`, a subclass of
  `StorageProviderTransientException` with a one-minute `RetryAfter`, is what the guard throws
  (§6.5). The Resolver keeps handling it as a transient store failure. The WebApp maps this subclass,
  and only this subclass, to `503`; nothing in the WebApp maps store exceptions to HTTP statuses
  today.

### 5.10 Priority-based execution

Decided in §13 item 6. It softens the noisy-neighbour loss (§7.2).

- **Account.** `cosmosDB.bicep` sets `enablePriorityBasedExecution: true` on the account and leaves
  `defaultPriorityLevel` at High, so `nb infra apply` turns the feature on. The GA ARM API versions
  carry both properties from `2025-10-15`
  ([change log](https://learn.microsoft.com/azure/templates/microsoft.documentdb/change-log/databaseaccounts)),
  so the account resource moves there from `2022-05-15`. §14 item 8 checks that move.
- **Who runs low.** The WebApp sets `RequestPriority = Low` (§5.9), so its page reads, both purges
  and its other requests yield to the Resolver. `nb container migrate` creates its client at low
  priority too. The Resolver sets nothing and runs at High.
- **Scope.** The setting covers every container in the account, so WebApp reads of `messages` and
  `audits` also yield to Resolver traffic.
- **Limits.** The feature is best effort, with no SLA. Under load, Cosmos throttles low-priority
  requests first and the SDK retries them, so WebApp pages can slow down during Resolver bursts. It
  isn't supported on serverless accounts and is unpredictable on shared database throughput; NimBus
  uses neither. Once it is on, Data Explorer runs at low priority by default
  ([priority-based execution](https://learn.microsoft.com/azure/cosmos-db/priority-based-execution)).
  The ARM reference still describes the account property as a preview feature.

## 6. Migration

### 6.1 `nb container migrate`

The command takes the same connection options as the other `nb container` commands, plus:

| Option | Meaning |
|---|---|
| `--dry-run` | Reads and reports only. Writes neither rows nor the marker |
| `--endpoint <id>` | Repeatable. Migrates only these containers. Default: every detected legacy container |
| `--exclude <id>` | Repeatable. Skips a container and records it as excluded in the marker. Refused for a catalog endpoint's container that holds unresolved rows (step 2) |
| `-a\|--assembly`, `--platform-package`, `--platform-feed`, `--platform` | The platform catalog, parsed as `nb topology apply` parses it. Optional; used to tell orphans apart |
| `--include-orphans` | Also migrates detected containers that aren't named after a catalog endpoint |
| `--apply-elapsed-ttl` | In TTL-off source containers, skips rows whose `ttl` has elapsed instead of copying them (step 3) |
| `--solution-id`, `--environment`, `--resource-group` | Identify the deployment, so the preflight can check the Resolver trigger. Required unless `--force` is given |
| `--parallelism <n>` | Concurrent creates within one container |
| `--endpoint-parallelism <n>` | Containers copied at once. Default 1 |
| `--force` | Skips the quiet check and the trigger check of step 1 |
| `--yes` | Skips the confirmation of step 2 |
| `--reset-marker` | Deletes the marker and exits, for a rollback (§6.3). Copies nothing |

1. **Preflight.** The command refuses to run in any of these cases, the last two unless `--force`
   is given:
   - `unresolvedevents` is missing, its partition key isn't `["/endpointId", "/id"]` `MultiHash`, or
     its TTL is off. The command never creates it; `nb infra apply` does.
   - Any selected legacy container was written in the last 5 minutes (`SELECT VALUE MAX(c._ts)`). A
     copy taken while the Resolver or the WebApp still writes would silently miss later status
     changes.
   - The Resolver trigger isn't disabled (§6.2 step 3). The command reads
     `AzureWebJobs.Resolver.Disabled` from the Resolver Function App named by the deployment
     options.

   It warns when `unresolvedevents` isn't autoscale or its max is below the value planned for the
   run (§6.4). It reads each selected container's `DefaultTimeToLive` for step 3 and reports it.
2. **Selection.** A container in `MessageDatabase` is a legacy endpoint container when it:
   - isn't in `ReservedContainerIds`;
   - isn't a known extension container (`intelligencesettings`, `failureclassifications`);
   - has the single partition key path `/id`;
   - holds at least one tracking row:
     `SELECT TOP 1 c.id FROM c WHERE IS_DEFINED(c.status) AND IS_DEFINED(c["event"])`.

   The row check leaves out a Cosmos inbox container that a host renamed
   (`CosmosInboxOptions.ContainerId`), which also has key `/id`. It also leaves out empty
   containers, which hold nothing to migrate. The layout guard uses the same rule (§6.5).

   With a catalog, detected containers that aren't named after a catalog endpoint are orphans:
   containers of removed endpoints. The command lists them separately and skips them unless
   `--include-orphans` is given. Without a catalog it can't tell orphans apart and says so; their
   rows are migrated, and outside the catalog no WebApp purge reaches them. `nb container delete
   <endpoint> -s <statuses>` removes them in the new layout. Skipped orphans and `--exclude`d
   containers are recorded in the marker's `excluded` map (step 5), with `reason: orphan` or
   `reason: operator`.

   **Excluding is refused for live data.** `--exclude` refuses a container named after a catalog
   endpoint that holds unresolved rows (Pending, Deferred, Failed, DeadLettered or Unsupported, not
   deleted). Excluding one would hide its Failed rows from v5 while their sessions stay blocked in
   Service Bus. To skip such a container anyway, the operator resolves or purges its rows under v4
   first. Without a catalog, `--exclude` refuses any container that holds unresolved rows, because
   it can't tell an orphan from a live endpoint.

   The command prints each selected container with its row count, TTL mode and last write, then
   asks for confirmation unless `--yes` is given.
3. **Copy.** The command writes through the `MigrationScope` (§5.3). For each container it records
   a reference time, `now_ref`, streams `SELECT * FROM c` and transforms each row:
   - drops the system properties (`_rid`, `_self`, `_etag`, `_attachments`, `_ts`);
   - sets `endpointId` to the container id;
   - sets `updatedAtTicks` from `event.UpdatedAt` (§5.2);
   - sets `migratedFromTs` and `migratedFromEtag` to the source row's `_ts` and `_etag` (§5.2);
   - sets `ttl` by the source container's TTL mode:
     - **TTL on** (`DefaultTimeToLive` is `-1` or positive). Cosmos already hides expired rows from
       the query. A positive `ttl` becomes the remaining lifetime, `ttl - (now_ref - _ts)`. Rows
       whose remaining lifetime is under a margin are skipped and counted as elapsed. The margin is
       the larger of an hour and the estimated copy-plus-verify time of that container, so no
       copied row expires before step 4. Soft-deleted rows that `RemoveMessage` wrote, with
       `ttl: 60`, always fall under it. `-1` and a missing `ttl` stay as they are, except that a
       positive container default applies to rows without `ttl` the same way. NimBus never creates
       containers with a positive default.
     - **TTL off** (`DefaultTimeToLive` unset). Endpoint containers created before item TTLs took
       effect are TTL-off (`docs/storage-providers.md`, "Container-level TTL on endpoint containers
       created before this change"). Their item `ttl` values are inert and every row is live,
       including terminal rows older than 30 days and, with a bounded `UnresolvedRetentionDays`,
       old unresolved rows. A Failed row among them can still block its session. The command copies
       these rows with `ttl` unchanged, so their lifetime restarts at the copy.
       - `deleted: true` alone doesn't mean hidden. Completed and Skipped rows are written with
         `deleted: true` and a 30-day `ttl` (`Writes.cs:151`), archived Failed rows are patched the
         same way (`Writes.cs:259`), and lookups still return them (`Lookups.cs:112-117, 219`). So
         they are copied.
       - The command skips only the rows `RemoveMessage` soft-deleted (`deleted: true` with
         `ttl: 60`, `Writes.cs:167-170`), which would expire a minute after the copy.
       - With `--apply-elapsed-ttl`, it skips rows whose `ttl` elapsed by their `_ts` instead.

     The dry run reports, per status, how many rows each rule copies, restarts or skips.
   - creates the row with the full key. On `409 Conflict` it reads the target row, so a rerun
     converges. A target "carries a stamp" when its `migratedFromTs` is defined and non-null.
     - **The target carries a stamp.** Nothing has written it since a migration copied it. The
       command replaces it, `IfMatch` the target's ETag, when the source `_etag` differs from
       `migratedFromEtag`. Comparing ETags, not `_ts`, catches two source changes within one
       second.
     - **The target has no stamp.** v5 wrote it (§5.3). The command replaces it, `IfMatch` its
       ETag, when the source `_ts` is at or after the target's `_ts`. That happens only when v4
       changed the row after a rollback (§6.3); v4 then wrote last, because v5 stopped writing at
       the rollback. A tie within one second goes to the source for the same reason.
     - Otherwise the row counts as already present.

   **Reconcile.** For a container that was copied before, the rerun also cleans up stamped target
   rows that no longer correspond to a live source row. A container was copied before when the
   marker lists it or its endpoint has stamped rows in the target. The command deletes, `IfMatch`
   their ETag, the stamped target rows whose id the source stream didn't produce, or whose source
   row now falls under a skip rule: rows that v4 purged, removed or let expire after the first
   copy. It reports them as removed. Unstamped rows are never deleted.

   After each container it re-reads `MAX(c._ts)` and keeps the value as `sourceMaxTs`. If it rose
   during the copy, a writer was active, and the command fails that endpoint. A rerun then repairs
   the rows copied before the change. A fall isn't a writer: TTL expiry of the newest row lowers
   the maximum without any write, so the command records the new value and continues.
4. **Verify.** The source side comes from the copy's own stream at `now_ref`, not from a re-query,
   because skipped rows keep expiring in the source while the run continues. For each endpoint and
   each (`status`, `deleted`) pair, the rows that matched a copy rule must equal the rows the run
   owns in the target:
   - stamped rows whose `migratedFromEtag` equals the source row's `_etag` in the stream;
   - unstamped rows kept because they are newer (step 3), when their id appears in the source.

   Matching on the ETag, not only on counts per (`status`, `deleted`), catches a row whose content
   changed while its status didn't.

   Target rows that have no source row, written by v5 for events first seen after a cutover, are
   reported separately as target-only and aren't a mismatch. The report also gives each endpoint's
   Monitor-equivalent counts, meaning unresolved statuses on rows that aren't deleted. Those are
   the numbers the Monitor page shows once the WebApp is ready (§6.2 step 7).
5. **Mark.** The command merges its result into the marker `settings/tracking-layout` (§6.5) with
   an ETag read-modify-write. Each endpoint that verified is added to `endpoints` with its row count
   and `sourceMaxTs`, and each skipped container to `excluded` with its reason and its own
   `sourceMaxTs`, so the guard's high-water check covers excluded containers too (§6.5). Partial runs
   with `--endpoint` add or update entries and never remove them; only `--reset-marker` does.
6. **Never touches the source.** It doesn't modify or delete legacy containers.

Implementation notes:

- The command doesn't use the SDK's bulk mode; it uses bounded parallel creates. The first draft
  said the vNext emulator didn't support .NET bulk; Microsoft's current feature table lists the
  Bulk API as supported, but what the pinned dated image supports is unverified (§14 item 2). The
  design doesn't depend on it: bounded parallel creates give the command its own control over
  `IfMatch` replaces, 409 handling and the request rate.
- The reconcile pass keeps the set of source ids per container in memory, which takes a few tens of
  bytes per row.
- The copy and verify logic lives in `NimBus.MessageStore.CosmosDb`, and `nb` only wires the
  options. That puts its emulator tests in `NimBus.MessageStore.CosmosDb.Tests`, under CI's
  "Cosmos DB conformance must not skip" gate.
- In private networking mode (Spec 034), run the command from inside the network, as with every
  other data-plane operation.

### 6.2 Cutover runbook

1. **Prerequisites.**
   - The deployment runs v4.7.0, the 4.x release that reserves `unresolvedevents` (§9), or later.
   - Read the release notes. Confirm that no endpoint is named `unresolvedevents`.
   - Upgrade every `nb` installation and pipeline that targets this deployment to v5. After the
     cutover, a pre-v5 `nb container` command would act on the frozen legacy containers. For
     example, `nb container resubmit` would resubmit their Failed rows, including events completed
     since.
2. Upgrade `nb` and run `nb infra apply`. It creates `unresolvedevents`, turns on priority-based
   execution (§5.10) and leaves the existing containers alone. The old apps keep running.
3. **Pause the Resolver** by setting `AzureWebJobs.Resolver.Disabled=true` on the Resolver Function
   App (`az functionapp config appsettings set`).
   - Don't stop the app. On Flex Consumption, the default plan, `nb deploy apps` must run against a
     started app (`AppDeploymentService.cs:201-212`), and on Elastic Premium it stops and restarts
     the app itself.
   - Service Bus holds Resolver-bound messages in the Resolver subscription. Adapters keep
     processing their own subscriptions.
   - A v5 `nb infra apply` carries the paused trigger forward (§5.6), and `nb deploy apps` and
     `nb setup` refuse while the layout is pending (§6.5). So a Deploy NimBus workflow or Azure
     Pipelines run that starts during the cutover fails at its deploy step without resuming the
     Resolver. Still, don't start one: step 4 deploys directly. A pre-v5 `nb` doesn't preserve the
     setting, which is one more reason step 1 upgrades every installation.
4. **Deploy v5** by running `nb deploy apps --allow-pending-migration` directly, never through
   those pipelines.
   - The v5 WebApp starts with the layout guard pending (§6.5). It shows the migration notice in
     place of tracking data, and nothing writes tracking rows.
   - The v5 Resolver's trigger stays disabled; the setting survives the deploy and an Elastic
     Premium restart.
   - From here on no v4 code runs, so nothing writes the legacy containers.
5. **Raise throughput for the copy** (§6.4), with `az cosmosdb sql container throughput update`:
   - the max of `unresolvedevents`, to at most 10,000 RU/s;
   - the manual throughput of the largest legacy containers.
6. **Migrate.** Run `nb container migrate --dry-run --solution-id … --environment …
   --resource-group …` and read its report. Then run it without `--dry-run`. Its quiet check
   refuses within 5 minutes of the last legacy write; wait and rerun.
7. **Reconcile, then resume.**
   - Within a minute the guard reads the marker, and the WebApp leaves pending mode.
   - Compare the Monitor counts with the report's Monitor-equivalent counts. Nothing has drained
     yet, so they must match.
   - Don't act in the WebApp before the Resolver resumes, so that a rollback until then loses
     nothing.
   - Re-enable the Resolver trigger by removing the setting. The Resolver drains its backlog.
8. **Restore throughput.** Run `nb infra apply --cosmos-tracking-max-throughput <original>`; a plain
   apply would pin the raised value (§5.6). The lowest max Cosmos accepts depends on the highest
   max ever set and on the container's storage (§6.4); a deployment whose tracking data exceeds
   400 GB can't return to 4,000 RU/s. Lower the raised legacy containers too, because they are
   billed through the soak.
9. After a soak period, delete the legacy containers from Topology → Storage. The page is
   deprecated in a later 5.x minor and removed in v6.0.0 (§5.8); from v6.0.0, delete them in the
   Azure portal instead.

External readers of endpoint containers must switch to `unresolvedevents` and filter on
`endpointId`. That includes change-feed consumers, saved Data Explorer queries and ops tools such
as the Cosmos repair tool Spec 032 used as its reference implementation.

### 6.3 Rollback

`nb deploy apps` deploys the artifacts of its own version (`DeploymentArtifacts.cs:67`). So rolling
back takes these steps:

1. Run `nb container migrate --reset-marker` with the v5 `nb`. This deletes the marker, so that any
   later v5 deploy, through the runbook, a pipeline or `nb setup`, finds the layout pending and
   refuses until the migration is rerun. This step is the control, and it is required: the guard's
   high-water check (§6.5) catches v4 writes after a forgotten reset, but not v4 deletes.
2. Install the v4 `nb` and run `nb deploy apps` with it.
3. Re-enable the Resolver trigger.

When the rollback happens matters:

- **Before the Resolver resumes (§6.2 step 7).** Nothing but the migration has written
  `unresolvedevents`, and the legacy containers are intact.
- **After it resumes.** The legacy containers show the state at cutover; rows written since then
  exist only in `unresolvedevents`. No reverse migration ships, and nothing rebuilds tracking rows
  from `messages` and `audits` apart from the stale-pending reconcile (Spec 032). Expect these
  consequences and act on them before and after redeploying v4:
  - **Failures after the cutover.** These events have no legacy row, but their sessions stay
    blocked in Service Bus session state (`SessionState.cs:22`). The v4 WebApp can't resubmit or
    skip them. List them on the v5 Failed page before rolling back.
  - **Resubmitted under v5.** Rows that were Failed at cutover and then resubmitted and completed
    under v5 still show Failed. Check an event's audit trail before resubmitting it, or it is
    processed twice.
  - **Completed under v5.** Rows that were Pending at cutover and completed under v5 stay Pending.
    The Spec 032 reconcile repairs part of them.
  - **The new container.** The v4 WebApp's Storage page protects `unresolvedevents` only
    from v4.7.0 (§9), which is why step 1 of the runbook requires it. Without that release it
    would list the container as deletable, and it holds the only copy of the post-cutover rows.
- **Retrying after a rollback.** Follow the runbook again. The migration then converges (§6.1
  step 3):
  - it brings forward the rows that v4 changed after the rollback;
  - it removes stamped copies of rows that v4 deleted;
  - it keeps newer rows that v5 wrote.

  Its verify reports v5's rows for events first seen after the first cutover as target-only, and
  doesn't count them as a mismatch (§6.1 step 4). It writes a fresh marker.

### 6.4 Downtime

The copy is bounded on both sides:

- **Target.** The write rate is the available RU/s of `unresolvedevents` ÷ RU per create.
- **Source.** Each legacy container has its own throughput, 400 RU/s manual by default (§2.1). It
  serves the `SELECT *` scan and the two `MAX(c._ts)` scans, and that throughput can't be pooled
  across containers.

So each endpoint takes about rows ÷ min(source scan rate, target write rate). Endpoints are copied
`--endpoint-parallelism` at a time. Phase 0 measures the RU per row on both sides (§14 items 5
and 7).

Worked example (every input is an assumption until Phase 0 measures it): 1,000,000 rows at about
10 RU per create, with the autoscale max raised to 10,000 RU/s for the run, is about 1,000 creates
per second, or roughly 17 minutes, if the sources keep up. When one endpoint holds most of the rows,
its 400 RU/s scan is likely the bound instead. The migration copies every row during the outage,
terminal ones included (§13 item 4). Most rows in a busy deployment are terminal (Completed or
Skipped, 30-day TTL), so they set most of the copy time. The command's low priority (§5.10) doesn't
slow the copy: with the Resolver paused and the v5 WebApp pending, nothing competes with it.

The levers (§6.2 steps 5 and 8):

- **The target's autoscale max, up to 10,000 RU/s.** That is the instant maximum for a
  one-partition container. Raise it before the copy with
  `az cosmosdb sql container throughput update --max-throughput 10000`. Restore it after the
  Resolver resumes, with `nb infra apply --cosmos-tracking-max-throughput <value>`, or before that
  with the same `az` command; a plain `nb infra apply` would pin the raised value (§5.6). Don't go
  above 10,000:
  - above *physical partitions × 10,000*, Cosmos splits partitions asynchronously, typically in 4 to
    6 hours, which doesn't fit an outage window;
  - the split is permanent, and after scale-down it thins each partition's share, and so each
    endpoint's (§7.2);
  - the lowest max allowed afterwards is the largest of 1,000 RU/s, the highest max ever set ÷ 10,
    and the container's current storage in GB × 10, rounded up to the next 1,000
    ([autoscale FAQ](https://learn.microsoft.com/azure/cosmos-db/autoscale-faq)). So a max above
    40,000 RU/s rules out the 4,000 default, and so does storage above 400 GB, whatever the max
    was.
- **The sources' throughput.** Raise the largest legacy containers' manual throughput before the
  copy. Up to 10,000 RU/s is instant on one partition. The raised floor doesn't matter, because the
  containers are deleted after the soak, but lower them after the copy, because they are billed
  until then.
- **`--endpoint-parallelism`**, so that several 400 RU/s sources feed the target at once.

### 6.5 Layout guard

The migration marker is the document `tracking-layout` in the `settings` container (partition key
`/id`), beside singletons such as the heartbeat and audit settings:

```json
{
  "id": "tracking-layout",
  "layout": "unresolvedevents",
  "endpoints": { "<endpointId>": { "rows": 1234, "sourceMaxTs": 1790000000, "migratedAtUtc": "…" } },
  "excluded":  { "<containerId>": { "reason": "orphan | operator", "atUtc": "…" } }
}
```

A public `TrackingLayout` reader in `NimBus.MessageStore.CosmosDb` reads the marker and evaluates
the layout state. It applies §6.1 step 2's detection rule, without the catalog, which only the CLI
loads:

- **Ready.** The marker exists, every legacy endpoint container appears in `endpoints` or
  `excluded`, and no listed container's current `MAX(c._ts)` exceeds its recorded `sourceMaxTs`.
  The high-water check catches a legacy container that v4 wrote after the migration, for example
  after a rollback whose marker wasn't reset (§6.3). It costs one `MAX(c._ts)` query per legacy
  container each time a process evaluates the state, until the legacy containers are deleted; §14
  item 7 measures that cost.

  **What the high-water check can't see.** It detects writes: every v4 status change, remove and
  archive updates a row and raises `MAX(c._ts)`. It doesn't detect deletes. A v4 session purge
  deletes rows, and a v4 endpoint purge deletes the whole container, which then simply drops out of
  the listing. After a rollback, either leaves stamped copies in `unresolvedevents` that v4 no
  longer has. The control for that is the marker reset that the rollback runbook requires (§6.3
  step 1); the high-water check is a backstop for writes only, not a complete one. A rerun of the
  migration's reconcile pass removes those copies (§6.1 step 3).
- **Fresh.** There is no marker and no legacy container: a new install. The reader creates the
  marker with empty maps, ignoring `409`, and the state becomes ready.
- **Pending.** Anything else.

Listing containers and reading the marker are data-plane reads that Cosmos DB Built-in Data
Contributor allows, so the guard works under managed identity.

Who uses it:

- **The Cosmos provider.** It evaluates the state on first use and, while the state is pending, at
  most once a minute. Once the state is ready, it rechecks every 15 minutes for as long as any
  legacy container exists, so a process that started before a rollback's v4 writes doesn't stay
  ready indefinitely. When no legacy container is left, ready holds for the life of the process.
  A recheck that finds the layout pending again blocks tracking members from then on.
  - The guard covers the members that reach `unresolvedevents` through the accessor (§5.3). The
    members that use `messages`, `audits` and `eventreports` are unaffected.
  - While pending, those members throw `TrackingLayoutPendingException` (§5.9), a
    `StorageProviderTransientException` with a one-minute `RetryAfter` and a message that names the
    runbook. The check runs before `PurgeMessages`'s catch-all, so a purge propagates the exception
    instead of returning `false`.
  - The guard also reaches callers that use tracking members indirectly. Subscription updates read
    the endpoint's error list through the tracking store
    (`CosmosDbSubscriptionStore.UpdateSubscription`, `CosmosDbSubscriptionStore.cs:201-208`), so they
    throw too while the layout is pending; no notification goes out with an empty error list. The
    Spec 035 operator actions (§5.4) refuse the same way.
  - The Resolver treats the exception like any store outage (`ResolverService.cs:160`): it
    reschedules the message with backoff and dead-letters it once the shared delivery budget
    (`ServiceBusMaxDeliveryCount`, 10) is spent, from where Topology → Subscriptions can replay it.
    The runbook keeps the trigger disabled, so this is a backstop.
- **The health check.** `CosmosDbHealthCheck` reports Unhealthy while pending, with the reason.
- **The WebApp.** While pending, its tracking API returns `503` with a problem detail, documented
  in `api-spec.yaml` (§5.9), and the UI shows a migration notice that links the runbook.
- **`nb deploy apps` and `nb setup`.** Against a Cosmos deployment they evaluate the state before
  they deploy v5 apps, and they refuse while it is pending unless `--allow-pending-migration` is
  given (§6.2 step 4).
  - They read with the caller's Entra identity first (`DefaultAzureCredential` against the account
    endpoint, as `nb container` commands already can, `CommandRunner.cs:53-64`), which needs a
    Cosmos data-plane read role for the deploying identity. When that is refused and the account
    allows key authentication, they fall back to keys fetched with `az cosmosdb keys list`. Spec 034
    plans `disableLocalAuth` on the account, where only the Entra path works. When they can't read
    the marker, they refuse and explain the flag.
  - In private networking mode (Spec 034), deployments already run from an in-network runner
    (Spec 034 R5), so the preflight reaches the data plane the same way `nb deploy apps` reaches
    Kudu. The preflight doesn't relax for private deployments.
  - `deploy.yml` and the Azure Pipelines template run these commands, so they inherit the check.
  - After the soak, when the legacy containers are deleted, the state is ready and the check is a
    cheap read.
- **The Storage page** (§5.8).

## 7. Evaluation

Prices below are US list prices for a single write region: $0.008 per 100 RU/s-hour for manual
throughput and $0.012 for autoscale
([understanding your bill](https://learn.microsoft.com/azure/cosmos-db/understand-your-bill)).
400 RU/s of manual throughput costs about $23 a month.

### 7.1 Cost

| Endpoints | Tracking throughput today (N × 400 RU/s manual) | Proposed (`unresolvedevents`, autoscale max 4,000) |
|---|---|---|
| 5 | ~$117/month | ~$35/month idle, up to ~$350/month at max |
| 20 | ~$467/month | same range |
| 50 | ~$1,168/month | same range |

- **Fixed cost stops growing with endpoints.** Today every endpoint adds a fixed ~$23 a month for
  capacity it rarely uses. Autoscale bills the highest RU/s reached in each hour, and at least 10% of
  the max, so the proposed bill follows aggregate load.
- **Small deployments.** A five-endpoint deployment that runs near the autoscale max most of the
  time would pay more than today, but it would be getting ten times the capacity. The Bicep
  parameter lets it choose a max of 1,000 instead: $8.76 to $87.60 a month.
- **Unchanged.** The twelve platform containers, about $280 a month if each sits at the 400 RU/s
  minimum.
- **Not the lever.** Shared database throughput is the other obvious saving. Microsoft advises
  against it for most workloads, limits it to 25 containers and can't apply it to an existing
  database ([throughput](https://learn.microsoft.com/azure/cosmos-db/set-throughput)).

### 7.2 Performance

**Gains:**

- **Point operations.** These carry most of the Resolver's traffic: the guarded upsert pair,
  terminal upserts and patches. The row gains two short properties (`endpointId`,
  `updatedAtTicks`), and each composite index adds write cost, so writes cost slightly more RUs.
  Phase 0 measures the difference (§14 item 5).
- **First message on a new endpoint.** Today it waits for a lazy container creation, or fails with
  403 under managed identity. Now there is nothing to create.
- **Pooled capacity.** Idle endpoints' capacity is available to busy ones, and autoscale absorbs
  bursts. Today a busy endpoint throttles at its own 400 RU/s even when every other endpoint is
  idle.
- **Cross-endpoint queries.** Each is one query against one container instead of N queries merged
  in memory (§5.4), so the per-container overhead goes away. Its RU cost is part of the Phase 0
  comparison (§14 item 5).

**Neutral, pending measurement:**

- **Endpoint-scoped queries.** Today they run against a small container of their own. Proposed,
  they are prefix-routed to the endpoint's partitions and filtered by an indexed equality. While the
  container is one physical partition (up to 50 GB and 10,000 RU/s), the RU cost should be
  comparable, and the composite indexes target the `GROUP BY` and `ORDER BY` queries. The emulator
  reports no RU charges, so Phase 0 measures this on a live account (§14 item 5).

**Losses:**

- **Noisy neighbour.** A burst on one endpoint, or an expensive WebApp scan, can throttle Resolver
  writes for every endpoint. Throttling makes the Resolver call `ScheduleRedelivery`, the reorder
  path behind the Spec 030 incident. Spec 030's guard makes the stale copies harmless, but every
  endpoint is delayed. Mitigations:
  - autoscale;
  - priority-based execution, with the WebApp, purges and the migration at low priority (§5.10);
  - Spec 031's capacity controls. Spec 031's capacity visibility also gets simpler: one container's
    metrics instead of N.
- **Hot-endpoint ceiling.** With tens of endpoints, the first key level has low cardinality, so one
  endpoint's rows share a physical partition until splits spread the prefix. Cosmos divides a
  container's RU/s evenly across its physical partitions. One endpoint therefore tops out at
  min(10,000, autoscale max ÷ physical partitions): 4,000 RU/s at the default on one partition,
  2,000 after a split into two. A split comes from storage above 50 GB, which terminal rows with
  full payloads kept 30 days can reach, or from a max raised above 10,000 (§6.4).
  - Today an endpoint's ceiling is its container's 400 RU/s, so this isn't a regression in practice.
  - It is still a ceiling to document. Operators sizing the max watch Normalized RU Consumption by
    `PartitionKeyRangeId` and take the partition count into account.

### 7.3 Security

- **The boundary doesn't move.** Cosmos access is granted at account scope today, so the containers
  never isolated one endpoint's data from an identity. Per-endpoint authorization (Spec 026 roles)
  is enforced by the WebApp, before any query runs.
- **What changes.** Isolation now rests on the partition-key prefix and the query predicate. The
  scopes make that the only path, including for the copy tools (§5.7). Two kinds of tests pin it: a
  unit test that inspects every recorded query, and conformance tests for cross-endpoint isolation
  (§8). The equality predicate rules out prefix-sibling leaks.
- **What's lost.** Two future options, neither used today:
  - granting an identity data-plane access to one endpoint's container (role assignments can be
    scoped to `/dbs/<db>/colls/<container>`);
  - issuing per-endpoint resource tokens, which can't target a partial hierarchical key.

  A future feature that reads one endpoint's rows directly would go through the WebApp API instead.
- **What improves:**
  - The runtime no longer needs container management rights for tracking.
  - Purge works under data-plane RBAC.
  - Endpoint ids no longer double as resource names.
  - `EndpointScope` bounds a bug in a bulk operation to one endpoint.
- **Unchanged.** Payloads, PII handling and the audit trail are identical.
- **Restore granularity.** `cosmosDB.bicep` sets no `backupPolicy`, so deployments use periodic
  backup, where the Cosmos team restores through a support request into a new account.
  Point-in-time restore would need continuous backup. Either way, restoring one endpoint means
  restoring `unresolvedevents` into a new account and copying that endpoint's rows back with Copy
  Endpoint Data. Today it means restoring that endpoint's container.

### 7.4 Maintainability and operations

**Removed:**

- `EndpointContainerProvisioner` and `EndpointContainerProvisionerTests`, and the provisioning step
  in `nb topology apply` and `nb setup`;
- `GetEndpointContainer`, the reserved-endpoint-id checks at five call sites and the
  container-cache eviction on purge;
- the per-container TTL mode juggling;
- `FailedEventPageCursor`'s merge, its tests and the per-endpoint fan-out of the failed search;
- the hand-written tracking-row SQL in the two copy tools (§5.7);
- two operator procedures in `docs/storage-providers.md`: turning on TTL for old endpoint containers,
  and backfilling each container;
- in v6.0.0, after deprecation in a 5.x minor, Topology → Storage and the WebApp's
  control-plane Cosmos role (§5.8).

**Added:**

- one Bicep resource and its autoscale parameter;
- `TrackingContainerAccessor`, `EndpointScope` and `EndpointSetScope`;
- the stamped `updatedAtTicks`, `migratedFromTs` and `migratedFromEtag` (§5.2), and the
  `{token, skip}` codec of the failed search (§5.4);
- the account's `enablePriorityBasedExecution` in Bicep and the `RequestPriority` option (§5.10);
- the WebApp's background purge worker (§5.5);
- the migration command, kept at least one major so late upgraders can migrate, the public copy
  type and the layout guard (§6.5);
- tests.

**Other effects:**

- **Provider parity.** The Cosmos layout matches the SQL Server provider's `UnresolvedEvents` table,
  so the conformance behaviour of the two converges.
- **Operations.** New endpoints need no Cosmos step. The Cosmos 500-resource limit per account no
  longer caps the endpoint count, and there is one throughput dial and one set of metrics to watch.
- **Costs:**
  - Cosmos store changes ported from DIS stop applying mechanically.
  - CI now depends on the emulator's hierarchical-key support. The Linux vNext emulator listed it
    at GA on 2026-06-02, and its behaviour kept changing through 2026: a prefix-query bug (#290)
    came back as #329 and was fixed in `vnext-EN20260810`, and a creation bug (#346) was fixed in
    `vnext-EN20260907`. CI therefore pins a dated emulator tag (§8, §14 item 2).
  - Per-endpoint throughput and TTL tuning, never used, can't be added later without a new
    container.

### 7.5 Scale limits

| Limit | Per-endpoint layout | One container |
|---|---|---|
| Endpoints per account | ~485: 500 databases plus containers, minus the database and platform containers. Can't be increased | No Cosmos limit |
| Data per endpoint | 20 GB per logical partition doesn't bind (`/id` key) | Doesn't bind (`/id` is the last level) |
| Throughput per endpoint | The container's provisioned RU/s (400 by default) | min(10,000, autoscale max ÷ physical partitions) until the endpoint's prefix spans partitions: 4,000 RU/s at the default (§7.2) |

## 8. Tests

**Unit, in `tests/NimBus.MessageStore.CosmosDb.Tests`, extending `RecordingCosmosAdapters`:**

- **Recording LINQ queries.** The recording adapter builds its queryables from an offline
  `CosmosClient`; LINQ translation sends no request. It captures LINQ queries through the
  `ToFeedIterator` seam (§5.3), so `ToQueryDefinition()` works in unit tests.
  `RecordingCosmosAdapters.GetItemLinqQueryable` throws `NotSupportedException` today.
- **Keys.** Every point operation passes the key `(endpointId, id)`.
- **Query heads.** Every statement recorded against `unresolvedevents`, SQL or LINQ, starts with a
  scope's head:
  - `c.endpointId = @__endpointId`, bound to the scope's endpoint;
  - for the failed search and the histogram, the set filter, bound to exactly the requested
    endpoint ids.

  No query uses `STARTSWITH` on `endpointId`. The scope rejects parameter names with the `@__`
  prefix and malformed `OffsetLimit` slots.
- **Composite coverage.** Every multi-property `ORDER BY` recorded from the store, SQL or LINQ,
  has an exactly matching composite index, directions included, in `cosmosDB.bicep`'s policy and
  in lazy creation (§5.1).
- **Written rows.** They carry `endpointId` equal to the argument, even when
  `UnresolvedEvent.EndpointId` differs, and `updatedAtTicks` equal to `event.UpdatedAt`. Upserts,
  replaces and patches leave neither `migratedFromTs` nor `migratedFromEtag`. That includes the
  Spec 035 `TryArchiveUnresolvedEvent` and `TryRestoreArchivedEvent`.
- **Existing fakes.** Every unit test that builds `CosmosDbClient` on the recording adapters (for
  example `CosmosDbClientGuardedWriteTests`, `CosmosDbClientTryCompleteTests`) moves to the shared
  container and the full key, and its adapter implements `GetContainersAsync` and serves a ready
  marker.
- **Case.** Differently cased endpoint ids (`billing` and `Billing`) stay isolated. The SQL Server
  collation is case-insensitive, so the conformance suite can't pin this. It replaces the cased-id
  case in `CosmosDbClientRetentionTests`.
- **Failed search.** It is one query per page, ordered by `updatedAtTicks`, with `UpdatedAt`
  bounds applied on ticks. An exactly full last page returns no token. A malformed or
  Cosmos-rejected token restarts at page 1. Tests of the `{token, skip}` codec replace
  `FailedEventPageCursorTests`.
- **Priority.** The DI factory copies `RequestPriority` into `CosmosClientOptions.PriorityLevel`.
  With a host-built client, the adapters set `RequestOptions.PriorityLevel` per request. The
  WebApp's registration sets `Low`.
- **Purge.**
  - The endpoint purge pages ids by endpoint with `c._ts < @purgeStart`, deletes each with the full
    key `IfMatch` its ETag, keeps a row that answers 412, and counts a 404 as done. This replaces
    `PurgeMessages_evicts_cached_handle_so_next_access_recreates_container` in
    `CosmosDbClientUnitTests`.
  - `CosmosDbClientPurgeTests`, which covers the session purge, gains the endpoint predicate and
    the 404 rule.
- **Creating the container.** The client creates `unresolvedevents` with the `MultiHash` paths, TTL
  on and autoscale throughput. This replaces:
  - `CosmosDbEndpointContainerTtlTests`;
  - the endpoint-container cases in `CosmosDbClientRetentionTests` (container TTL, TTL mode by call
    order, reserved-id rejection);
  - the handle-caching and faulted-creation cases in `CosmosDbClientUnitTests`.
- **Layout guard (§6.5).**
  - A fresh account gets the marker and is ready.
  - A legacy container missing from the marker makes the state pending, and every member that
    reaches `unresolvedevents` throws `StorageProviderTransientException`.
  - A marker that lists the container, or excludes it, makes the state ready, without a restart.
  - A legacy write after marking, past the recorded `sourceMaxTs`, makes the state pending again.
  - Empty containers and inbox-shaped containers don't count as legacy.
  - Members that use only `messages`, `audits` or `eventreports` aren't blocked; subscription
    updates, which read the error list, are.
  - `PurgeMessages` propagates the guard's exception instead of returning `false`.
  - An adapter without `GetContainersAsync` makes the state pending with a reason naming the
    member, and tracking members throw `TrackingLayoutPendingException`.
  - A ready process rechecks while legacy containers exist and turns pending after a legacy write
    past `sourceMaxTs`; with no legacy container left, it stops rechecking.
  - An excluded container's write past its recorded `sourceMaxTs` makes the state pending.
- **Migration scope (§5.3).** It builds the full key, keeps both stamps, and its queries carry the
  endpoint predicate. `RemoveMessage` and `ArchiveFailedEvent` on a stamped row leave it unstamped.
- **Copy type (§5.7).** It reads through the scope; the recorded predicate is checked. It refuses a
  source or a target with the legacy layout and creates neither. Copied rows carry no `ttl`, so
  they never expire in the target, as with today's copy tools (`AdminService.Copy.cs:139`,
  `Container.cs:352`).
- **Adapter defaults.** The new `CreateContainerIfNotExistsAsync` overload's default throws when
  throughput is given (§5.9).
- **Bicep.** `CosmosBicepContainerSyncTests` parses hierarchical keys and checks `unresolvedevents`
  against its expected path list (§5.6). A template test checks that `cosmosDB.bicep` sets
  `enablePriorityBasedExecution: true` (§5.10) and declares the autoscale parameter.

**Conformance, for every provider, in `src/NimBus.Testing/Conformance/MessageTrackingStoreConformanceTests.cs`.**
New cross-endpoint isolation tests write the same `eventId` and `sessionId` to endpoints `A` and
`AB`, which is a prefix sibling. Then:

- each read member returns only its own endpoint's row, `GetEventsByFilter(EndPointId = "A")`
  included;
- writes, patches, removes and archives on `A` leave `AB` unchanged;
- `PurgeMessages(A)` and `PurgeMessages(A, session)` leave `AB` intact;
- counts, paging, blocked and invalid lists stay per endpoint;
- the failed search returns only the requested endpoints.

No existing test covers this. The tests pass on SQL Server and in-memory only once the prerequisite
exact-endpoint fix (§5.4) has landed. The existing
`GetFailedEventsAcrossEndpoints_pages_every_row_exactly_once_newest_first` already pins the single
query's order: every row once, newest first by ticks.

**Migration, against the emulator, in the Cosmos test project.**

Seed these containers:

- two TTL-on legacy containers with unresolved rows, terminal rows carrying a TTL, soft-deleted
  rows, rows with `ttl: -1` and rows without `ttl`;
- a TTL-off legacy container whose rows carry elapsed TTLs, including Completed, Skipped and
  archived rows with `deleted: true` and rows that `RemoveMessage` soft-deleted (`ttl: 60`);
- an inbox-shaped container with key `/id`;
- an empty container with key `/id`.

Migrate, then assert:

- the per-status counts and the Monitor-equivalent counts;
- the stamped `endpointId`, `updatedAtTicks`, `migratedFromTs` and `migratedFromEtag`;
- the remaining TTLs in the TTL-on sources;
- in the TTL-off source, rows copied with their lifetime restarted (Completed, Skipped and archived
  rows included), `RemoveMessage`'s rows skipped, and elapsed rows skipped with
  `--apply-elapsed-ttl`;
- verify passes while skipped source rows expire during the run;
- that the inbox-shaped and empty containers aren't selected;
- with a catalog, that an orphan is skipped and recorded as excluded;
- an idempotent rerun;
- after a source row changes, a rerun replaces the stale target row and verify passes;
- after a source row changes twice within one second with its status unchanged, a rerun replaces
  the target by ETag and verify passes;
- the newest source row expiring during the copy lowers `MAX(c._ts)` without failing the endpoint;
- `--exclude` refuses a catalog endpoint's container that holds unresolved rows, and excluded
  entries record `sourceMaxTs`;
- a newer row that v5 wrote (no stamp) is kept;
- after a source row is deleted or expires, a rerun removes its stamped copy;
- a rollback-then-retry: v5 writes target-only rows, the marker is reset, the source changes, and
  the rerun converges and verifies, reporting the target-only rows separately;
- partial `--endpoint` runs merge into the marker and record `sourceMaxTs`;
- `--reset-marker` deletes the marker;
- `--dry-run` writes nothing;
- refusal while a writer is active;
- refusal when the target partition key is wrong.

**CLI (`tests/NimBus.CommandLine.Tests`):**

- `EndpointContainerProvisionerTests` goes.
- `nb topology apply` no longer calls `az cosmosdb sql container create`.
- `nb container migrate` parses its options and creates its client at low priority. It requires
  the deployment options unless `--force` is given, and it refuses while the Resolver trigger is
  enabled.
- `nb deploy apps` and `nb setup` refuse while the layout is pending, unless
  `--allow-pending-migration` is given.
- `nb infra apply` pins the deployed tracking max when the flag is absent and the container exists,
  and uses the default when it doesn't, alongside `InfrastructureDeployerCapacityTests`.
- The Function App templates carry an existing `AzureWebJobs.Resolver.Disabled` forward (§5.6).
- The deploy preflight reads with Entra first, falls back to keys only when local auth is allowed,
  and refuses when it can read neither.

**WebApp (`tests/NimBus.WebApp.Tests`):**

- `AdminCosmosContainerTests.cs`: `unresolvedevents` is protected, and legacy endpoint containers
  stay protected until the marker lists them as migrated or excluded.
- Purge:
  - both purge operations return `202`, run the purge in the background in their own DI scope,
    and audit its start and its outcome under the caller's identity through the required audit
    path;
  - a start that can't be audited returns `503` and purges nothing;
  - a second purge of the same endpoint gets `409`, from the same instance or another, while the
    lease is live, and an expired lease can be taken over;
  - the endpoint page's purge runs `ClearEndpoint` before the row purge and skips the row purge when
    `ClearEndpoint` fails.
- While the layout is pending, the tracking API returns `503`.

**CI.**

- The Cosmos suites keep running under the "must not skip" gate.
- CI pins a dated emulator tag, `vnext-EN20260907` or later, instead of `vnext-latest`
  (`dotnet.yml:40`), and moves it on purpose. The vNext emulator's hierarchical-key behaviour
  changed through 2026 (§7.4).
- The emulator ignores indexing policies, reports no RU charges and doesn't support .NET bulk, so
  the live checks in §14 cover those gaps.

**Live, run by hand and recorded in the PR:**

- the RU comparison (§14 item 5);
- the checks on a split container (§14 item 4);
- a migration rehearsal on a production-sized copy;
- a rehearsal of the runbook on both Resolver plans (§14 item 9).

## 9. Phases

| Phase | Content | Exit |
|---|---|---|
| Prerequisites | Outside this spec, before Phase 1 merges: (a) the exact endpoint filter in `GetEventsByFilter` on SQL Server and in-memory (§5.4), **done in v4.3.0 (#196)**; (b) a 4.x **minor** release (v4.7.0) that adds `unresolvedevents` to `ReservedContainerIds` (§5.6, §6.3) and to `NotDeclaredByPlatformTemplate` in `CosmosBicepContainerSyncTests`, because v4's template doesn't declare it; v5 removes it from that list. It is a minor, not a patch, because it makes `unresolvedevents` invalid as an endpoint id, which a consumer can observe ([versioning](../../versioning.md)); (c) a maintenance-branch procedure in `docs/versioning.md`: the branch name, cut from the v4.7.0 tag or a later 4.x tag, the publish workflow and the base of the release notes' compare link | Each merged and released |
| 0 | Prove the platform. Nothing ships, and nothing of Phase 1 merges before it exits. It runs on a spike branch with a scratch harness and a draft template | Every item in §14 answered, with results added to this folder, except item 9b, which needs the v5 apps and is a release gate of Phase 1 |
| 1 | v5.0.0: store and scopes, Bicep and its autoscale parameter, CLI, WebApp (background purge, layout notice), migration command, layout guard and deploy preflight, priority-based execution, the single-query failed search, tests, docs | Release build green; Cosmos and SQL conformance run without skips; §14 item 9b passed on both Resolver plans |
| 2 | Rehearse, then migrate each Cosmos deployment; delete legacy containers after the soak | Migration reports reconciled; no unmigrated legacy containers left |
| 3 | A later 5.x minor: deprecate Topology → Storage (a notice on the page, `deprecated: true` on its API operations) and mark `ICosmosContainerAdmin` and `CosmosContainerAdmin` `[Obsolete]`. Nothing is removed (§5.8) | — |
| 4 | v6.0.0: remove the obsolete `CosmosContainerDefaults` members, `--storage-provider` on `nb topology apply`, and Topology → Storage with its API operations, `ICosmosContainerAdmin`, `CosmosContainerAdmin`, the WebApp's Cosmos DB Operator role assignment and `CosmosAccountResourceId` (§5.8) | — |

**Sequencing.** v4.0.0 was tagged on 2026-09-28 at `0acf1778`, so Phase 1 here can merge to master
without shipping in v4 (§13 item 1). Once it merges, master is v5.0.0-bound. Releases are tagged
on master ([versioning](../../versioning.md)). The 4.x line ships actively (v4.1.0 and v4.2.0
followed v4.0.0), so any 4.x release after that point needs a maintenance branch. Prerequisite (c)
defines one.

## 10. Documentation

- `docs/adr/008-per-endpoint-cosmos-containers.md`: status "Superseded by ADR-017"; update
  `docs/adr/README.md`.
- `docs/storage-providers.md`:
  - the Cosmos layout and purge semantics;
  - a replacement for the per-container TTL sections;
  - the "Reserved endpoint ids" section, which no longer constrains endpoint ids.
- `docs/azure-requirements.md`: the container list; drop "per-endpoint containers are created at
  runtime"; say that `nb infra apply` turns on priority-based execution.
- `docs/deployment.md`:
  - the upgrade flow, which gains the migration runbook (§6.2) and the deploy preflight (§6.5);
  - the Storage tab's wording, which changes in v5.0.0 (§5.8).
- In the 5.x minor that deprecates Topology → Storage: say so in `docs/deployment.md`. In
  v6.0.0, which removes it: drop the Cosmos DB Operator grant from `docs/azure-requirements.md` and
  `docs/deployment.md`.
- `docs/cli.md`: `container migrate`, the note on `container copy`, the `topology apply`
  deprecation, `--cosmos-tracking-max-throughput`, `--reset-marker` on `container migrate`, and
  `--allow-pending-migration` on `deploy apps` and `setup`.
- `.github/workflows/deploy.yml` and `pipelines/azure-pipelines-deploy.yml`: the autoscale parameter
  and the preflight (§6.5). `--allow-pending-migration` is used only during the runbook.
- `docs/versioning.md`: the maintenance-branch procedure (§9, prerequisite c).
- Other ADR-008 references: `docs/adr/002-centralized-resolver.md`,
  `docs/adr/010-pluggable-message-storage.md`, and the two CrmErpDemo adapter `docs/TDD.md` files.
  Also the adapter-docs skill that generates those TDDs: `.claude/skills/adapter-docs/templates/TDD_TEMPLATE.md`
  and `references/detection_heuristics.md`.
- User-facing wording that still describes per-endpoint containers: the endpoint-status schema in
  `api-spec.yaml` ("per-endpoint storage container", `:3901-3904`) and Operations → Delete all events
  ("Delete the entire endpoint container", `advanced-operations.tsx:563`).
- v5.0.0 release notes: ⚠️ Breaking entries that link the runbook (§6.2) and name the purge API's
  status change (§5.5); Compatibility entries for the three adapter members and
  `TrackingLayoutPendingException` (§5.9), and for Operations → Delete all events now writing audit
  entries.

## 11. Residual risks

1. **Noisy neighbour** (§7.2). Mitigated, not removed. The sharpest case is a purge of a large
   endpoint in production, which competes with the Resolver until it finishes (§5.5). Site Owners
   can start one from either entry point. Low priority (§5.10) helps on a best-effort basis, with
   no SLA.
2. **Isolation bypass.** A future query that reaches the container without a scope would bypass
   isolation. The accessor structure and the recorded-query test make that visible in review.
3. **Emulator maturity.** Its hierarchical-key support is recent and changed through 2026, so CI
   may fail spuriously or pass on behaviour the service doesn't share. A pinned emulator tag,
   Phase 0 and the live checks cover it.
4. **SDK behaviour after splits.** The .NET SDK has had hierarchical-key defects in prefix queries
   after partition splits (#4326, fixed in 3.39.0). The spec sets SDK 3.62.1, today's pin, as the
   minimum, scopes by `WHERE` only (§5.3), and tests a split container in Phase 0 (§14 item 4).
5. **Migration with writers active.** The `_ts` quiet check can be overridden with `--force`. A
   rerun converges on the rows that changed (§6.1 step 3).
6. **Interrupted purge.** A WebApp restart stops a background purge part-way. The audit log shows a
   start without an outcome, and a rerun finishes the job (§5.5).
7. **Hot-endpoint ceiling** of min(10,000, max ÷ physical partitions) RU/s, 4,000 at the default
   (§7.2).
8. **Restore granularity** (§7.3).
9. **Divergence from DIS** (§7.4).

## 12. Alternatives considered

| Alternative | Verdict |
|---|---|
| Keep per-endpoint containers; put them on shared database throughput | Rejected. Microsoft advises against it for most workloads, caps it at 25 containers, and an existing database can't switch. It fixes none of the provisioning problems |
| Keep per-endpoint containers; move to a serverless account | Rejected. No provisioned-to-serverless conversion exists, so it means a new account and a migration anyway, and the provisioning problems remain |
| One container, partition key `/endpointId` | Rejected. Caps each endpoint at 20 GB and 10,000 RU/s |
| One container, partition key `/id` with the endpoint folded into the row id | Rejected. Changes ids that the API and UI expose, and every endpoint query fans out to all physical partitions |
| One container, three levels: `/endpointId`, `/sessionId`, `/id` | Rejected. `GetEventById` and `GetEventsByIds` receive only the composite row id, and an event id may contain `_`, so the session can't be recovered reliably. Sessions can be null. No routing gain while an endpoint fits in one physical partition |
| A prefix `QueryRequestOptions.PartitionKey` as a second, server-side fence (§5.3) | Rejected. The `WHERE` predicate already routes the query, and request-option prefix scoping has had defects in the SDK and the emulator (#329) |
| Purge with delete by partition key | Rejected. Preview, needs an account capability, and supports only full keys on hierarchical containers |
| Purge row by row inside the HTTP request | Rejected. A large endpoint outlasts App Service's request timeout of about 230 seconds (§5.5) |
| Opt-in dual layout in a 4.x minor, default in the next major | Rejected (§13 item 2). It runs two layouts and two conformance passes for a release cycle |
| Copy only non-terminal rows during the outage; backfill terminal rows after the start | Rejected (§13 item 4). A shorter outage, but a second copy pass runs alongside the live Resolver. Copying everything during the outage is simpler |
| Stop the Resolver Function App during the migration | Rejected. On Flex Consumption, the default plan, `nb deploy apps` fails against a stopped app (§6.2 step 3) |
| Keep the per-endpoint merge for the failed search, one prefix query per endpoint | Rejected (§13 item 7). One query does the same work, and its continuation token, wrapped as `{token, skip}`, replaces `FailedEventPageCursor`'s merge |
| Zero-downtime migration through the change feed | Rejected. Much more machinery to save a planned outage of minutes |
| Azure container copy jobs for the migration | Rejected. Copy jobs copy documents unchanged, and the rows need a new top-level `endpointId` |

## 13. Decisions

The repo owner decided these on 2026-09-28. The first draft listed them as open.

1. **Release vehicle: v5.0.0.** v4.0.0 didn't wait for this spec; it was tagged on 2026-09-28
   (§9).
2. **Migration style: one-shot.** Existing deployments migrate once, offline, in v5.0.0 (§6). The
   opt-in dual layout is rejected (§12).
3. **Default autoscale max: 4,000 RU/s** for `unresolvedevents`, so a deployment with up to ten
   endpoints keeps at least its current total tracking capacity (§5.1, §7.1). The Bicep parameter
   still lets a deployment choose another max.
4. **Outage: copy everything.** The migration copies every row during the outage, terminal rows
   included (§6.4). The shorter variant that backfills terminal rows after the start is rejected
   (§12).
5. **Storage page: deprecate it in a later 5.x minor and remove it in v6.0.0**, together
   with its API operations, the public container-admin types and the WebApp's Cosmos DB Operator role
   assignment, which exists only for this page (§5.8). The first decision (2026-09-28) retired it in
   a 5.x minor; the repo owner changed that on 2026-10-08, because `docs/versioning.md` reserves the
   removal of a page and an API route for a major.
6. **Priority-based execution: on.** The account turns it on; the WebApp, purges and the migration
   run at low priority. It is best effort, with no SLA (§5.10).
7. **Failed search: one query.** The cross-endpoint failed search and its histogram each become one
   `ARRAY_CONTAINS(@endpointIds, c.endpointId)` query, and `FailedEventPageCursor`'s merge goes
   (§5.3, §5.4). The search orders by the stamped `updatedAtTicks`, because the string order of
   `event.UpdatedAt` isn't chronological within a second (§5.2).

## 14. Facts to verify before implementing (Phase 0)

1. **Throughput on existing endpoint containers.** Run `az cosmosdb sql container throughput show`
   against a real deployment to confirm the 400 RU/s cost baseline.
2. **Emulator support.** Check the dated `vnext` tag CI will pin (`vnext-EN20260907` or later, §8)
   with .NET SDK 3.62.1 in gateway mode:
   - creating a `MultiHash` container, with and without autoscale throughput (§5.6);
   - read, create, upsert, `IfMatch` replace, patch and delete with the full key;
   - `ReadManyItemsAsync` with full keys;
   - SQL and LINQ queries with the endpoint predicate, including `GROUP BY`, `ORDER BY`,
     `OFFSET`/`LIMIT`, `TOP`, `ARRAY_CONTAINS`, `STARTSWITH(…, true)` and continuation tokens;
     results contain exactly the endpoint's rows (#329 returned other rows for request-option
     prefix scoping);
   - the set query of §5.3 ordered by `updatedAtTicks`, paged across endpoints;
   - requests that carry a priority level (§5.10). Microsoft documents that a request's priority
     is ignored when the account hasn't enabled the feature
     ([SDK request option](https://learn.microsoft.com/dotnet/api/microsoft.azure.cosmos.requestoptions.prioritylevel)),
     so the WebApp's `Low` setting is safe on the emulator and before `nb infra apply` turns the
     feature on; check it on the pinned image and on a live account without the feature;
   - whether the pinned image supports .NET bulk execution (§6.1 implementation notes).

   Issue [#346](https://github.com/Azure/azure-cosmos-db-emulator-docker/issues/346) (hierarchical
   container creation failing) was confirmed fixed in `vnext-EN20260907`. It was reported against
   the Java SDK.
3. **Bicep.** `cosmosDB.bicep` deploys `unresolvedevents` with `MultiHash` at API version
   `2021-06-15`, or the resource moves to a newer API version.
4. **Prefix routing on a split container**, on a live account. A new container at ≤10,000 RU/s has
   one physical partition, where routing claims are trivially true. So use a throwaway container,
   load data on one partition, raise its max above 10,000 RU/s, and wait for the split, typically 4
   to 6 hours. On it, with SDK 3.62.1:
   - endpoint-scoped SQL and LINQ queries (session, `GROUP BY`, `ORDER BY`, `OFFSET`/`LIMIT`)
     return complete results, and the query metrics show only the endpoint's partitions read;
   - `ARRAY_CONTAINS(@ids, c.endpointId)` against `c.endpointId IN (@e1, …)`: routing and RU, which
     decide the set scope's form (§5.3);
   - a failed-search token issued before the split still resumes.
5. **RU per operation**, legacy container against `unresolvedevents`, with and without each
   candidate composite index (§5.1):
   - operations: the guarded upsert path, a terminal upsert, a patch, and a create of a
     representative-size row (§6.4);
   - queries: `DownloadEndpointStateCount`, `DownloadEndpointStatePaging`, `GetEventsByFilter`, the
     session list (`Search.cs:423`), the single failed-search query (with and without an index on
     `updatedAtTicks`), the session counts;
   - a `SELECT *` scan of a legacy container, per row (§6.4).

   The results decide the indexing policy, weighing each index's read gain against its write cost.
   They also check that the 4,000 RU/s default (§13 item 3) carries today's load.
6. **Today's purge.** Whether `PurgeMessages(endpointId)` fails on a managed-identity deployment.
   This affects only the claim in §2.2.
7. **`MAX(_ts)` cost.** The RU cost and latency of `SELECT VALUE MAX(c._ts)` on a large legacy
   container. The migration's quiet check runs it, and so does the guard's high-water check at each
   process start until the legacy containers are deleted (§6.5).
8. **Priority-based execution** on a live account (§5.10):
   - moving the account resource from API version `2022-05-15` to `2025-10-15` changes nothing
     unintended (`az deployment group what-if` before the first apply), and the property turns the
     feature on;
   - whether the feature is still in preview, as the ARM reference's wording suggests;
   - the account's metrics show the WebApp's requests at low priority and the Resolver's at high.
9. **Runbook rehearsal** on both Resolver plans, Flex Consumption and Elastic Premium (§6.2). It
   splits in two, because the second half needs the v5 apps and commands:
   - **9a, in Phase 0:** `AzureWebJobs.Resolver.Disabled=true` stops the Resolver from processing
     while its host runs, and a draft of the Function App templates carries the setting forward
     through `az deployment group create` (§5.6).
   - **9b, a release gate of Phase 1 (§9):**
     - `nb deploy apps --allow-pending-migration` succeeds with the trigger disabled and leaves it
       disabled;
     - a v5 `nb infra apply` during the cutover leaves it disabled;
     - the v5 WebApp and Resolver go from pending to ready within a minute of the marker (§6.5);
     - `nb container migrate` reads the trigger setting in its preflight;
     - a rollback by §6.3 and a retry by §6.2 converge.

## 15. References

Microsoft documentation:

- [Hierarchical partition keys](https://learn.microsoft.com/azure/cosmos-db/hierarchical-partition-keys):
  up to three levels, prefix routing, the item id as the last level, set at creation only.
- [Service quotas](https://learn.microsoft.com/azure/cosmos-db/concepts-limits): 400 RU/s manual
  minimum, 500 databases plus containers per account, 20 GB and 10,000 RU/s per partition,
  autoscale billing floor.
- [Provision throughput](https://learn.microsoft.com/azure/cosmos-db/set-throughput): shared database
  throughput not recommended; 25-container limit.
- [Scaling provisioned throughput](https://learn.microsoft.com/azure/cosmos-db/scaling-provisioned-throughput-best-practices):
  instant scale up to 10,000 RU/s per physical partition; asynchronous splits above it; RU/s
  divided evenly across physical partitions.
- [Time to live](https://learn.microsoft.com/azure/cosmos-db/nosql/time-to-live): TTL counts from
  the last modification; expired items vanish from queries; item TTLs have no effect when the
  container's TTL is off.
- [Delete by partition key](https://learn.microsoft.com/azure/cosmos-db/how-to-delete-by-partition-key):
  preview; full keys only on hierarchical containers.
- [Linux vNext emulator](https://learn.microsoft.com/azure/cosmos-db/emulator-linux): feature table;
  no .NET bulk. Also the
  [GA announcement](https://devblogs.microsoft.com/cosmosdb/announcing-general-availability-of-the-azure-cosmos-db-vnext-emulator/).
- [Priority-based execution](https://learn.microsoft.com/azure/cosmos-db/priority-based-execution):
  best effort, no SLA; requests default to high priority; enabled per account; not for serverless
  accounts.
- [Database account API change log](https://learn.microsoft.com/azure/templates/microsoft.documentdb/change-log/databaseaccounts):
  `enablePriorityBasedExecution` and `defaultPriorityLevel` appear in `2025-05-01-preview` and stay
  in the GA versions from `2025-10-15`
  ([account ARM schema, 2025-10-15](https://learn.microsoft.com/azure/templates/microsoft.documentdb/2025-10-15/databaseaccounts)).
- [Change capacity mode](https://learn.microsoft.com/azure/cosmos-db/how-to-change-capacity-mode):
  serverless to provisioned only.
- [Data-plane role-based access](https://learn.microsoft.com/azure/cosmos-db/nosql/security/how-to-grant-data-plane-role-based-access):
  account, database and container scopes.
- [Understanding your bill](https://learn.microsoft.com/azure/cosmos-db/understand-your-bill).
- [Container ARM schema, 2021-06-15](https://learn.microsoft.com/azure/templates/microsoft.documentdb/2021-06-15/databaseaccounts/sqldatabases/containers).
- [App Service request timeout](https://learn.microsoft.com/troubleshoot/azure/app-service/web-request-times-out-app-service):
  about 230 seconds.

Repo:

- ADR-008, ADR-010, Spec 026 (roles), Spec 030 (stale-write guard), Spec 031 (capacity controls),
  Spec 032 (stale-pending repair), Spec 034 (private networking).
- [Review of this spec, 2026-10-04](../../plan/2026-10-04-spec-036-review.md).
- `src/NimBus.MessageStore.CosmosDb/` (`CosmosDbClient.cs`, `CosmosContainerDefaults.cs`,
  `CosmosDbMessageTrackingStore.*.cs`, `FailedEventPageCursor.cs`,
  `CosmosDbMessageStoreBuilderExtensions.cs`, `ICosmosContainerAdmin.cs`,
  `HealthChecks/CosmosDbHealthCheck.cs`).
- `src/NimBus.CommandLine/EndpointContainerProvisioner.cs`, `InfrastructureDeployer.cs`,
  `AppDeploymentService.cs`, `Container.cs`.
- `src/NimBus.Resolver/Services/ResolverService.cs` (store-failure handling).
- `deploy/bicep/templates/cosmosDB.bicep`.

## 16. Review trail

- 2026-09-26: drafted from an investigation of the per-endpoint layout. Not yet reviewed.
- 2026-09-28: the repo owner decided §13: v5.0.0, a one-shot migration, a 4,000 RU/s default, a
  full copy during the outage, the Storage containers page retired in a later 5.x minor,
  priority-based execution on, and one query for the failed search. The spec now designs items 5
  to 7 (§5.2, §5.3, §5.4, §5.8, §5.10). It also adds the Phase 3 and 4 rows and the sequencing
  note (§9), and §14 item 8.
- 2026-10-04: reviewed against the code at `e9996280` and Microsoft's documentation
  ([review](../../plan/2026-10-04-spec-036-review.md)). The review made 24 findings, and none was
  refuted.
- 2026-10-05: the review's findings are folded in. The design changes:
  - **Scopes.** §5.3 specifies the full scope surface: the accessor, query execution, and the
    `GROUP BY`, `OFFSET`/`LIMIT` and LINQ seams, and a `MigrationScope`. The second fence is
    dropped. `ORDER BY` stays single-property, which needs no composite; composites are optional,
    and a query that uses one writes the matching leading terms (§5.1).
  - **Prerequisite fix.** §5.4 requires an exact endpoint filter in `GetEventsByFilter` on SQL
    Server and in-memory, and specifies the failed search's paging contract.
  - **Purge.** §5.5 makes the purge a background operation that returns `202`.
  - **Provisioning, copy and API.** §5.6 pins the autoscale parameter, creates the container lazily
    with autoscale, and adds a 4.x patch that reserves the name. §5.7 moves the copy's tracking SQL
    behind the scope. §5.9 defines `RequestPriority` for each client source.
  - **Migration.** §6.1 adds:
    - catalog- and shape-aware selection;
    - TTL-mode-aware copying, which keeps Completed, Skipped and archived rows;
    - a convergent rerun with a reconcile pass for deleted source rows;
    - verification from the copy's own stream;
    - marker merging with a per-endpoint high-water mark.

    §6.2 pauses the Resolver through its trigger setting, deploys v5 directly before migrating,
    and raises and restores throughput. §6.3 resets the marker on rollback and states the
    consequences. §6.4 adds the read side and the 10,000 RU/s ceiling. §6.5 adds the layout guard,
    its high-water check and the deploy preflight.
  - **Corrections.** §2.1 and §5.5 now say who can purge in production. §7.2 and §7.5 give the
    per-endpoint ceiling. §7.3 says which backup mode deployments use. §7.4 gives the emulator fix
    dates.
  - **Tests and process.** §8 lists the affected tests and pins the emulator. §9 adds the
    prerequisites and §10 the missing docs. §14 adds the split-container checks (item 4), the
    read-side RU and the index cost (item 5), and the runbook rehearsal (item 9).
- 2026-10-05, later: a completeness and consistency check of the revision found 18 issues; all are
  fixed:
  - `ORDER BY` is no longer prepended, which would have needed exact composites (§5.1, §5.3).
  - The TTL-off rule keeps `deleted: true` Completed, Skipped and archived rows (§6.1).
  - Rollback resets the marker, and the guard checks a high-water mark (§6.3, §6.5).
  - Migration writes go through a `MigrationScope` (§5.3).
  - Verify uses the copy's own stream and tolerates target-only rows, and a reconcile pass removes
    stale copies (§6.1).
  - The trigger check is on by default (§6.1).
  - Throughput steps are in the runbook, and pipelines are kept out of step 4 (§6.2).
  - Autoscale pinning keys on the container existing (§5.6).
  - The background purge runs in its own DI scope, with a captured identity, and the API changes
    are complete (§5.5, §5.9).
  - The public adapter interface changes are listed (§5.9).
  - Copy checks the source layout (§5.7).
  - The guard's scope covers purge propagation (§6.5).
  - The 4.x patch also updates the Bicep sync test (§9).
- 2026-10-07: a second review of the spec against `7403c596`
  ([spec review](../../plan/2026-10-07-spec-036-spec-review.md)) and a review of the implementation
  plan ([plan review](../../plan/2026-10-07-spec-036-implementation-plan-review.md)), both by Codex.
  Claude checked every finding against the code; none was refuted.
- 2026-10-08: the findings are folded in. The design changes:
  - **Purge (§5.5).** It deletes only rows written before it started (`_ts < purgeStart`,
    `IfMatch` each row's ETag), so it never deletes work that arrives during a long purge. The
    endpoint page's purge clears the Service Bus subscription first and skips the row purge when
    that fails. A lease document in `settings` gives one purge per endpoint across instances. The
    start and the outcome are written through the required audit path; a start that can't be
    audited returns `503`.
  - **Migration (§6.1).** Copies are stamped with the source ETag as well as `_ts`, so reruns and
    verify catch two changes within one second. Only a rising `MAX(c._ts)` fails an endpoint; a
    fall from TTL expiry doesn't. `--exclude` refuses a catalog endpoint's container that holds
    unresolved rows, and excluded entries record `sourceMaxTs`.
  - **Guard (§6.5).** The high-water check is described as a backstop for writes only; the marker
    reset on rollback is the required control. A ready process rechecks every 15 minutes while
    legacy containers exist. Subscription updates and the Spec 035 operator actions are named as
    blocked callers. The deploy preflight reads with Entra first and doesn't relax under private
    networking, where deployments already run in-network.
  - **Cutover (§5.6, §6.2).** The Function App templates carry `AzureWebJobs.Resolver.Disabled`
    forward, so a pipeline started during the cutover can't resume the Resolver.
  - **Public API (§5.9).** A third adapter member, `GetContainersAsync`, and a public
    `TrackingLayoutPendingException`, which the WebApp maps to `503`. The obsolete members become
    obsolete only once no code in `src/` calls them.
  - **Corrections.** Prerequisite (a) is marked done (v4.3.0). Prerequisite (b) is a minor release,
    v4.7.0. §5.4 lists the Spec 035 compare-and-swap members. §6.4 gives the full autoscale
    minimum, including storage. §6.1 no longer claims the emulator lacks bulk support. §14 item 9
    splits into a Phase 0 half and a release-gate half. §10 adds the stale per-endpoint wording in
    the API schema and the Admin UI. Line references are refreshed.
  - **Decided by the repo owner:** §13 item 5 retired Topology → Storage and its API route
    in a 5.x minor, but `docs/versioning.md` reserves removing observable behavior for a major. The
    page and its API are now deprecated in a 5.x minor and removed in v6.0.0 (§5.8, §9, §10, §13).
