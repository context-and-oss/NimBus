# Spec 036 (one Cosmos tracking container): implementation plan

Status: **draft, revised** (2026-10-08). Nothing is implemented yet. Implements
[Spec 036](../spec/036-cosmos-single-tracking-container/spec.md) as revised on 2026-10-08. Decision
record: [ADR-017](../adr/017-single-cosmos-tracking-container.md) (proposed). Target release:
v5.0.0. Planned against master `7403c596` (v4.6.0 + 1). Reviewed by Codex on 2026-10-07
([plan review](2026-10-07-spec-036-implementation-plan-review.md)); see
[Review trail](#review-trail).

The spec is the design. This plan covers the order of work, how it splits into PRs, what each PR
tests first, and the few decisions the spec leaves to implementation
([Decisions this plan makes](#decisions-this-plan-makes)). Where the two disagree, the spec wins.
The open question for the repo owner is in [Open questions](#open-questions).

## Where things stand

### Prerequisites (spec §9)

| Prerequisite | State |
|---|---|
| (a) Exact endpoint filter in `GetEventsByFilter` on SQL Server and in-memory | **Done.** #196 (`a1266587`), shipped in v4.3.0. SQL uses `EndpointId = @EndpointId` (`SqlServerMessageTrackingStore.Search.cs:160`). In-memory uses `string.Equals(…, OrdinalIgnoreCase)` (`InMemoryMessageStore.cs:308`). `GetEventsByFilter_matches_endpointId_exactly_not_by_prefix` (`MessageTrackingStoreConformanceTests.cs:1746`) pins it |
| (b) A 4.x minor release (v4.7.0) that reserves `unresolvedevents` | Not started (`CosmosContainerDefaults.cs:31` doesn't list it) |
| (c) Maintenance-branch procedure in `docs/versioning.md` | Not started (`docs/versioning.md` only has "Cutting a release", which tags master) |

### Release line

Master still ships 4.x: v4.3.0 through v4.6.0 followed v4.0.0. The first PR that changes the store's
layout (PR 3) makes master v5-bound. So v4.7.0 ships from master first, and `release/4.x` is cut from
its tag before PR 3 merges.

`deploy.yml` builds `nb` from the ref the workflow is dispatched on (`actions/checkout`, then
`dotnet build src/NimBus.CommandLine`), so nothing stops a master deploy by itself. From the cut
until v5.0.0 ships, **existing environments deploy from `release/4.x`**: the workflow is dispatched
on that branch, and the Azure Pipelines template is pointed at it. PR 0b documents this. As a
backstop, the deploy preflight lands in PR 3 together with the guard, so a master deploy against a
legacy Cosmos layout refuses with a clear message instead of starting apps that answer `503`.

### Code drift since the spec's first baseline (`e9996280`)

The 2026-10-08 revision of the spec refreshed its references. For implementers, the call sites that
matter:

- **27** `_getEndpointContainer` call sites: `EndpointState.cs` ×4, `Lookups.cs` ×7, `Search.cs` ×6
  (fan-out included), `Writes.cs` ×10 (both purges included). PR 3 moves all of them behind the
  scopes.
- The Spec 035 compare-and-swap members `TryArchiveUnresolvedEvent` and `TryRestoreArchivedEvent`
  (`Writes.cs:70-110`), which the spec's §5.4 now lists.
- Callers of the members that become `[Obsolete]`: `CosmosDbClient.cs:213-217`, `Container.cs:80,90`,
  `AdminService.Copy.cs:22,33`, `EndpointContainerProvisioner.cs:43,84`, and the tests
  `CosmosContainerDefaultsTests.cs:18,43,53`.
- Unit tests that build `CosmosDbClient` on the recording adapters with endpoint-named containers,
  for example `CosmosDbClientGuardedWriteTests.cs:28` and `CosmosDbClientTryCompleteTests.cs:27`.
- The indirect tracking caller `CosmosDbSubscriptionStore.UpdateSubscription`
  (`CosmosDbSubscriptionStore.cs:201-208`).

## Decisions this plan makes

The spec leaves these to implementation.

1. **Names of the public types.** The copy type is `TrackingRowCopier` and the migration type is
   `TrackingMigrator`, both in `NimBus.MessageStore.CosmosDb`. The layout reader is `TrackingLayout`,
   as the spec names it.
2. **The health check's DI shape.** `CosmosDbHealthCheck` is activated by
   `AddCheck<CosmosDbHealthCheck>` (`HealthCheckExtensions.cs:13`) and has two public constructors.
   The layout state comes in through an internal `ITrackingLayoutState` and a third constructor, and
   the registration switches to a factory so activation stays unambiguous. A check built with the
   two existing constructors reports account reachability only, as today.
3. **When the members become obsolete.** In PR 6, the PR that removes their last caller in `src/`
   (spec §5.9). PR 5 has already moved both copy tools to `TrackingRowCopier`; PR 3 has removed the
   store's own use; PR 6 deletes `EndpointContainerProvisioner`. Earlier would break the Release
   build on CS0618.
4. **Where the deploy preflight lands.** PR 3, beside the guard and `TrackingLayout`, rather than with
   the rest of the CLI in PR 6. See [Release line](#release-line).
5. **The background purge's required audit without a request.** `IAuditLogService` gains a
   `LogRequiredAuditAsync` overload that takes an auditor name and no `HttpContext`; the existing one
   needs a context and can't override the auditor (`IAuditLogService.cs:75-83`).

## Delivery

Each PR builds green in Release, adds its failing tests first (RED, then GREEN), and follows
[CONTRIBUTING.md](../../CONTRIBUTING.md) and Conventional Commits. Every PR that touches
`NimBus.MessageStore.CosmosDb` runs the Cosmos suites against the emulator without skips. Every
WebApp PR with a visible change includes a screenshot (AGENTS.md).

| Step | Lands on | Content |
|---|---|---|
| 0a | master, ships as v4.7.0 | Reserve `unresolvedevents` (prerequisite b) |
| 0b | master | Maintenance-branch procedure and the deploy-from-`release/4.x` rule (prerequisite c) |
| Phase 0 | spike branch | §14 items 1-8 and 9a, with a scratch harness and a draft template. Nothing merges to master. Results go into the spec folder |
| Cut | — | `release/4.x` from the v4.7.0 tag. Master is v5-bound from the next step |
| 1 | master | Additive adapter seams, `RequestPriority`, test infrastructure, the emulator pin and emulator tests |
| 2 | master | Infrastructure: `unresolvedevents`, the autoscale parameter, priority-based execution, the Resolver trigger carried forward, `nb infra apply` pinning |
| 3 | master | The store on `unresolvedevents`: scopes, every member, purge, layout guard, health check, deploy preflight |
| 4 | master | The failed search and histogram as single set-scope queries; `FailedEventPageCursor` goes |
| 5 | master | `TrackingRowCopier`, `TrackingMigrator`, `MigrationScope`; both copy tools switch to the copier |
| 6 | master | CLI: `nb container migrate`, the end of Cosmos provisioning, the obsolete members |
| 7 | master | WebApp: low priority, background purge, `503` and the migration notice, Storage containers, wording |
| 8 | master | Docs, ADRs, pipelines and the v5.0.0 release notes draft |
| 9 | — | Release gate: §14 item 9b on both Resolver plans and a migration rehearsal on a production-sized copy |
| 10 | — | Release v5.0.0 |

The order is strict from PR 1 to PR 7:

- **PR 2 before PR 3.** Under managed identity the store can't create `unresolvedevents` on first
  use (spec §5.6); the Bicep has to declare it first. A Bicep-declared, empty `unresolvedevents` is
  harmless to a 4.x deployment from v4.7.0 on.
- **PR 3 before PR 4.** PR 3 keeps the per-endpoint fan-out of the failed search working, each
  endpoint read through its own scope, so the Failed page works between them.
- **PR 5 before PR 6 and 7.** Both copy tools move to `TrackingRowCopier` in PR 5, which removes two
  of the three remaining callers of the members PR 6 obsoletes. Between PR 3 and PR 5 the copy tools
  still copy legacy endpoint containers; that is harmless on master, which no environment deploys
  ([Release line](#release-line)).

### PR 0a: reserve `unresolvedevents` (v4.7.0)

- `CosmosContainerDefaults.ReservedContainerIds` gains `"unresolvedevents"`.
- `CosmosBicepContainerSyncTests.NotDeclaredByPlatformTemplate` (`:26`) becomes
  `["inbox", "unresolvedevents"]`, because the 4.x template doesn't declare it.
- Tests:
  - `AdminCosmosContainerTests`: the Storage page reports `unresolvedevents` as
    platform-owned and refuses to delete it.
  - `CosmosContainerDefaultsTests`: `EnsureNotReservedEndpointId("unresolvedevents")` throws.
- Release v4.7.0, a minor (spec §9): it makes `unresolvedevents` invalid as an endpoint id, which
  the release notes list under Compatibility.

### PR 0b: maintenance-branch procedure

`docs/versioning.md` gains a "Maintenance releases" section:

- the branch, `release/4.x`, cut from the v4.7.0 tag (or a later 4.x tag) before master takes its
  first v5 change;
- fixes merge to master first and are cherry-picked to `release/4.x`, unless the fix applies only to
  4.x;
- tags are cut on `release/4.x`. `nuget-publish.yml` triggers on any `v*` tag push, so it needs no
  change; the PR confirms that its checkout and version derivation work from a non-master tag;
- the release notes' "Commits" compare link uses the previous 4.x tag as its base, never a v5 tag;
- until v5.0.0 ships, deployments run from `release/4.x` (`deploy.yml` dispatched on that ref, the
  Azure Pipelines template pointed at it).

### Phase 0 (spike; nothing merges)

Runs before any Phase 1 PR merges, on a spike branch. The harness (a small console project) and a
draft `cosmosDB.bicep` live there and are thrown away; their findings shape PRs 1-3. Record the
results in `docs/spec/036-cosmos-single-tracking-container/phase0-results.md` and amend the spec
where they contradict it.

**First step:** confirm which live Cosmos deployment the checks run against and that the person
running them has data-plane and control-plane access to it. Operations notes name `rg-nbdemo-dev` as
the live deployment; nothing in the repo confirms it, so verify before relying on it.

| §14 item | How |
|---|---|
| 1 Throughput baseline | `az cosmosdb sql container throughput show` against two endpoint containers of the live deployment |
| 2 Emulator | The harness runs every check of item 2 against the dated `vnext` tag CI will pin: `MultiHash` creation with and without autoscale; full-key CRUD, `IfMatch`, patch and `ReadMany`; SQL and LINQ with the endpoint predicate (`GROUP BY`, `ORDER BY`, `OFFSET`/`LIMIT`, `TOP`, `ARRAY_CONTAINS`, `STARTSWITH(…, true)`, continuation); the set query ordered by `updatedAtTicks` and paged across endpoints; a `Low` priority request; .NET bulk support. PR 1 turns these into permanent CI tests |
| 3 Bicep `MultiHash` at `2021-06-15` | `what-if`, then a deploy of the draft template into a throwaway resource group |
| 4 Split container | Throwaway container on a live account. Load it, raise its max above 10,000 RU/s and wait for the split (4-6 h). Run the scoped queries through the harness; compare `ARRAY_CONTAINS` with `IN`; resume a pre-split failed-search token |
| 5 RU per operation | The harness drives the store's operations and queries against a legacy container and a `MultiHash` container, with and without each candidate composite index. The result decides the indexing policy PR 2 declares |
| 6 Today's purge under MI | `POST /api/endpoint/{id}/purge` on the live deployment, with a throwaway endpoint |
| 7 `MAX(_ts)` cost | Against the largest legacy container of the live deployment |
| 8 Priority-based execution | `what-if` on the account API move to `2025-10-15` with the draft template; the metric split by priority; a `Low` request against an account without the feature |
| 9a Trigger pause | `AzureWebJobs.Resolver.Disabled=true` stops processing on Flex Consumption and on Elastic Premium, and the draft Function App templates carry the setting through a deployment |

Exit: every row answered. Item 9b is a release gate (step 9). If item 5 recommends composites, PR 2
declares them, PR 3 writes the leading `ORDER BY` terms, and PR 3's composite-coverage test pins
them.

### PR 1: additive seams and test infrastructure

Nothing in it changes behavior.

- `ICosmosContainerAdapter.ToFeedIterator<T>(IQueryable<T>)`. The default calls the SDK extension.
  `TransientTranslatingCosmosContainerAdapter` forwards it explicitly, as the file already does for
  other defaults (`CosmosAbstractions.cs:185-187`).
- `ICosmosDatabaseAdapter.CreateContainerIfNotExistsAsync(ContainerProperties, ThroughputProperties?,
  CancellationToken)`. The default throws when throughput is non-null. Implemented in
  `CosmosDatabaseAdapter`, forwarded in `TransientTranslatingCosmosDatabaseAdapter`.
- `ICosmosDatabaseAdapter.GetContainersAsync(CancellationToken)`, with the same implement-and-forward
  pair. The default throws `NotSupportedException`; the guard (PR 3) translates that into a pending
  state.
- `CosmosContainerDefaults`: `TrackingContainerId`, `TrackingPartitionKeyPaths` and
  `TrackingContainer()`, unused for now.
- `CosmosDbMessageStoreOptions.RequestPriority` (`PriorityLevel?`):
  - binds from `NimBus:Cosmos`;
  - `CreateCosmosClient` (`CosmosDbMessageStoreBuilderExtensions.cs:158`) copies it into
    `CosmosClientOptions.PriorityLevel`;
  - for host-built clients, `CosmosContainerAdapter` sets `RequestOptions.PriorityLevel` on each
    request, from a priority it is constructed with.

  Unset means no change.
- `RecordingCosmosAdapters`:
  - `GetItemLinqQueryable` returns a queryable from an offline `CosmosClient` instead of throwing
    (`RecordingCosmosAdapters.cs:193`);
  - `ToFeedIterator` records `ToQueryDefinition()`;
  - every point operation records its `PartitionKey`, and `ReadManyItemsAsync` records its pairs;
  - `GetContainersAsync` returns a scripted list.
- `dotnet.yml:42`: pin the emulator to the dated tag Phase 0 used instead of `vnext-latest`.
- `HierarchicalKeyEmulatorTests` (new, emulator): Phase 0's item 2 checks as permanent tests.
- Tests first:
  - adapter defaults: the throughput overload throws when throughput is given; `GetContainersAsync`
    throws;
  - options binding, and the DI factory copying `RequestPriority`;
  - a host-built client: the adapters set `RequestOptions.PriorityLevel` on each request (spec §8);
  - the recording adapter captures a LINQ query.

### PR 2: infrastructure

- `cosmosDB.bicep`:
  - `unresolvedevents` with `paths: ['/endpointId', '/id']`, `kind: 'MultiHash'`, `version: 2`,
    `defaultTtl: -1`, autoscale `maxThroughput: cosmosTrackingMaxThroughput` and Phase 0's indexing
    policy;
  - `param cosmosTrackingMaxThroughput int = 4000`;
  - the account resource moves from `@2022-05-15` to `@2025-10-15` and gains
    `enablePriorityBasedExecution: true`;
  - the comment saying per-endpoint containers can't be declared statically goes.
- `deploy.core.bicep:316` passes the parameter through.
- `functionApp.bicep` and `flexConsumptionFunctionApp.bicep` read the Resolver app's current settings
  and carry `AzureWebJobs.Resolver.Disabled` forward (spec §5.6), the pattern `webApp.bicep` uses for
  `AzureAd__*` (`InfrastructureDeployer.cs:531-534` passes `webAppExists`; the Function App needs the
  same existence flag).
- `InfrastructureDeployer`:
  - `--cosmos-tracking-max-throughput` on `nb infra apply` and `nb setup`;
  - when the flag is absent and the container exists, it reads
    `az cosmosdb sql container throughput show` and pins the deployed max, beside the plan and SKU
    pins (`InfrastructureDeployer.cs:73-88`). Otherwise it uses the default;
  - the Resolver's existence flag for the trigger carry-forward.
- `CosmosBicepContainerSyncTests`: parses `MultiHash` path lists, expects `["/endpointId", "/id"]`
  for `unresolvedevents`, and removes `unresolvedevents` from `NotDeclaredByPlatformTemplate`.
- Template tests: `enablePriorityBasedExecution: true`, the parameter, and the trigger carry-forward
  in both Function App templates.
- `InfrastructureDeployerCapacityTests`: the deployed max is pinned when the container exists, the
  default used when it doesn't, the flag used when given.

### PR 3: the store on `unresolvedevents`

The core change and the largest PR. It touches `NimBus.MessageStore.CosmosDb`, its tests, the
conformance suite, and the deploy preflight in `NimBus.CommandLine` (Decision 4). It doesn't mark
anything obsolete (Decision 3).

**Tests first:**

- Conformance (`MessageTrackingStoreConformanceTests.cs`): the cross-endpoint isolation tests of
  spec §8, for every read and write member, both purges, counts, paging, blocked and invalid lists,
  the failed search, and both Spec 035 compare-and-swap members. They pass on SQL Server and
  in-memory today, because prerequisite (a) is done, and fail on Cosmos until this PR.
- `TrackingScopeTests` (new, recording adapters): scope heads on every recorded statement; full keys
  on every point operation; written rows carry `endpointId` from the argument, `updatedAtTicks`, and
  no migration stamps; patches null both stamps; `billing` and `Billing` stay isolated; `@__`
  parameters and malformed `OffsetLimit` are rejected; no `STARTSWITH` on `endpointId`.
- Composite coverage: every multi-property `ORDER BY` the store emits matches a composite in
  `cosmosDB.bicep` and in lazy creation (with no composites, that no multi-property `ORDER BY`
  exists).
- Purge (spec §5.5): `c._ts < @purgeStart`, full-key `IfMatch` deletes, 412 keeps the row, 404 counts
  as done, parallelism 2; the session purge gains the predicate and the 404 rule
  (`CosmosDbClientPurgeTests`). This replaces
  `PurgeMessages_evicts_cached_handle_so_next_access_recreates_container`
  (`CosmosDbClientUnitTests.cs:58`).
- Creation: `unresolvedevents` with the `MultiHash` paths, TTL `-1` and autoscale 4,000, and a
  warning outside the emulator. This replaces `CosmosDbEndpointContainerTtlTests`, the
  endpoint-container cases in `CosmosDbClientRetentionTests` and the handle-caching and
  faulted-creation cases in `CosmosDbClientUnitTests`.
- Layout guard: every case in spec §8's "Layout guard" list, including the 15-minute recheck while
  legacy containers exist, excluded containers' high-water mark, an adapter without
  `GetContainersAsync`, `PurgeMessages` propagating the exception, subscription updates blocked and
  `messages`/`audits`/`eventreports` members not blocked.
- `CosmosDbHealthCheckTests`: Unhealthy with the reason while pending.
- Deploy preflight (`tests/NimBus.CommandLine.Tests`): `nb deploy apps` and `nb setup` refuse while
  pending, read with Entra first, fall back to keys only when local auth is allowed, refuse when
  neither works, and proceed with `--allow-pending-migration`.

**Existing tests that change:** every unit test that builds `CosmosDbClient` on the recording
adapters with endpoint-named containers moves to the shared container and the full key, and its
adapter implements `GetContainersAsync` and serves a ready marker (spec §8 "Existing fakes"). On
`7403c596` the unit tests that build `CosmosDbClient` on a fake adapter are in
`CosmosDbClientGuardedWriteTests`, `CosmosDbClientTryCompleteTests`,
`CosmosDbClientWriteOptionsTests`, `CosmosDbClientPurgeTests`, `CosmosDbClientRetentionTests`,
`CosmosDbClientUnitTests`, `DeferredRecoveryTests` and `EndpointCountQueryTests`. The emulator
harness (`CosmosDbStoreTestHarness`) builds it on a real client against the shared
`MessageDatabase` (`CosmosDbStoreTestHarness.cs:11,78`; the id is the internal constant
`CosmosDbClient.DatabaseId`). Today's conformance runs leave per-endpoint containers in it, so on a
reused local emulator the guard would see legacy containers and report pending; the harness deletes
them, or uses a fresh database, before the suite runs. CI's emulator starts empty each run. The PR
body lists each test that is converted, replaced or deleted with the code it covered.

**The database id becomes an internal parameter.** `CosmosDbClient` gains an internal constructor
argument for the database id (default `MessageDatabase`), visible to the test project. PR 5's
migration tests seed legacy containers, and in the shared database they would flip every conformance
test running beside them to pending. They seed and migrate in a database of their own instead.

**Production code:**

- `EventDbo` and the projection types move out of `CosmosDbMessageTrackingStore.cs:52-94` as internal
  types. `EventDbo` gains `endpointId`, `updatedAtTicks`, `migratedFromTs` and `migratedFromEtag`
  (the stamps with `NullValueHandling.Ignore`).
- New internal types in a `Tracking/` folder: `TrackingContainerAccessor`, `EndpointScope`,
  `EndpointSetScope`, `ScopedQuery` and `MigrationScope` (used from PR 5), as spec §5.3 specifies.
  The accessor resolves the container through `GetCachedContainerAsync` with the new properties and
  throughput, runs the guard before handing out an endpoint or set scope, and hands out the migration
  scope without it.
- `CosmosDbMessageTrackingStore`:
  - its constructor takes the accessor instead of `getEndpointContainer` and
    `removeEndpointContainerCache` (`:28-35`);
  - each of the 27 call sites moves to the scope;
  - `ExactOldestFailureAt` takes a scope;
  - `GetPendingEventsOnSession`'s container-404 branch (`Search.cs:499`) goes;
  - the failed search and histogram keep their per-endpoint merge for now, each endpoint read
    through its own `EndpointScope`.
- `PurgeMessages(endpointId)` and `PurgeMessages(endpointId, sessionId)` by spec §5.5.
- `CosmosDbClient`: `GetEndpointContainer` (`:203`), its reserved-id check and the cache-eviction
  lambda (`:102`, `:129`) go; `GetCachedContainerAsync` gains the tracking container's properties and
  throughput, keyed by id as before; the TTL-only path stays for the heartbeat containers
  (`:254-257`).
- `TrackingLayout` (public), `TrackingLayoutPendingException` (public, spec §5.9) and the internal
  `ITrackingLayoutState`: pending re-evaluated at most once a minute; ready rechecked every 15 minutes
  while legacy containers exist; `NotSupportedException` from `GetContainersAsync` reported as
  pending with a reason.
- `CosmosDbHealthCheck` by Decision 2.
- `nb deploy apps` and `nb setup`: the preflight and `--allow-pending-migration` (spec §6.5).

**Exit:** Release build green; the Cosmos, SQL and in-memory conformance suites pass without skips;
every existing Cosmos unit test passes, is converted, or is replaced by its spec §8 counterpart.

### PR 4: the failed search as one query

- Tests first:
  - `GetFailedEventsAcrossEndpoints_pages_every_row_exactly_once_newest_first`
    (`MessageTrackingStoreConformanceTests.cs:1840`) already pins the result;
  - new unit tests: one query per page; `ORDER BY c.updatedAtTicks DESC`; `UpdatedAt` bounds applied
    on ticks server-side; reads `pageSize + 1` rows; an exactly full last page returns no token; a
    malformed or Cosmos-rejected (400) token restarts at page 1; the histogram's single query keeps
    its 50,000-row cap and reads `endpointId` from the rows.
- `EndpointSetScope` emits whichever of `ARRAY_CONTAINS` and `IN` Phase 0 item 4 picked.
- A `FailedSearchToken` codec (`{token, skip}`) replaces `FailedEventPageCursor.cs`;
  `FailedEventPageCursorTests.cs` becomes codec tests. `ForEachEndpointAsync`,
  `FailedSearchParallelism`, `ReadFailedEventStreamAsync` and the `SourceBuffer` types go.
- Enqueued bounds keep the widened server filter and the exact in-memory check (spec §5.4).
- Only the failed search restarts on an unusable token, as spec §5.4 says. `GetEventsByFilter` and
  `DownloadEndpointStatePaging` keep passing tokens straight through: `GetEventsByFilter` drives the
  CLI's delete, resubmit and skip loops (`Container.cs:133,163,228`) and the WebApp's bulk operations
  (`AdminService.Purge.cs:549`), where a silent restart would repeat work.

### PR 5: copy and migration

- `TrackingRowCopier` (spec §5.7):
  - reads the source through `EndpointScope.Query<JObject>` with today's filters (enqueued range,
    statuses, `deleted != true`) and writes the target's `unresolvedevents` with the full key;
  - removes `ttl`, as both copy tools do today (`AdminService.Copy.cs:139`, `Container.cs:352`), so
    copied rows never expire in the target. That is today's behavior; spec §8 now states it and a
    test pins it;
  - checks both accounts' `unresolvedevents` for `MultiHash` `["/endpointId", "/id"]` before reading
    either, and refuses otherwise without creating anything.
- Both copy tools switch to it: `Container.CopyEndpointData` (`Container.cs:77-120`) and
  `AdminService.CopyEndpointDataAsync` (`AdminService.Copy.cs:17`). The `messages` half is
  unchanged. Their `EnsureNotReservedEndpointId` and `EndpointContainer` calls go.
- `TrackingMigrator` (spec §6.1 steps 1-5, without the Azure-side trigger check, which is PR 6's):
  preflight of the target; selection with an optional catalog endpoint set; the `--exclude` refusal
  for live data; copy through `MigrationScope` with the TTL rules, ETag stamps, 409 convergence and
  the reconcile pass; the rising-only writer check; verification from the copy's own stream, matched
  on ETags; the marker merge with `sourceMaxTs` for endpoints and exclusions. It returns a report the
  CLI renders, and it uses bounded parallel creates.
- Tests first: spec §8's "Migration" scenarios (emulator, `NimBus.MessageStore.CosmosDb.Tests`) and
  its "Migration scope" and "Copy type" unit cases. Expired-during-run cases use short TTLs
  (`ttl: 5`) and a margin parameter the tests can set, so the suite doesn't wait an hour. Each test
  class seeds and migrates in its own database (PR 3's internal database-id parameter), so its legacy
  containers never reach the shared `MessageDatabase` the conformance suite runs against.

### PR 6: CLI

- `nb container migrate`, with every option of spec §6.1:
  - wires `TrackingMigrator`;
  - creates its client at `PriorityLevel.Low` through `CommandRunner.CreateCosmosClient`
    (`CommandRunner.cs:53`), which gains an options parameter;
  - checks the Resolver trigger with `az functionapp config appsettings list` through
    `IAzureCliRunner`, unless `--force` is given;
  - `--reset-marker` deletes the marker and exits.
- `nb topology apply` and `nb setup` stop calling `EndpointContainerProvisioner`
  (`TopologyCommands.cs:121`, `SetupCommand.cs:137`); `EndpointContainerProvisioner.cs` and
  `EndpointContainerProvisionerTests.cs` go; `--storage-provider` on `topology apply` stays accepted
  with a deprecation warning (`TopologyCommands.cs:60`).
- `CosmosContainerDefaults.EndpointPartitionKeyPath`, `EndpointContainer(string)` and
  `EnsureNotReservedEndpointId(string)` become `[Obsolete]` (Decision 3).
  `CosmosContainerDefaultsTests.cs` suppresses CS0618 around the bridge tests, with a comment naming
  v6.0.0.
- Tests (`tests/NimBus.CommandLine.Tests`, on `RecordingAzureCliRunner`): `topology apply` issues no
  `az cosmosdb sql container create`; `migrate` option parsing; refusal without deployment options
  unless `--force`; refusal while the trigger is enabled; the low-priority client.

### PR 7: WebApp

- **Registration:** `RequestPriority = Low` for the WebApp's store registration, with a test.
- **Background purge (spec §5.5):**
  - the purge lease in `settings` (`purge-lease-<endpointId>`, create to take, renew every minute,
    take over `IfMatch` once `leaseUntil` has passed), giving `409` across instances;
  - the start audit through `LogRequiredAuditAsync`; `503` and no purge when it throws;
  - a singleton queue and a `BackgroundService` that runs each purge in its own `IServiceScope`;
  - the endpoint page's purge runs `ClearEndpoint` first and skips the row purge when it fails;
  - the outcome audit, with deleted and kept counts and duration, through the new
    `LogRequiredAuditAsync` overload (Decision 5), retried with backoff and logged on failure.
- **`api-spec.yaml`:** `post-endpoint-purge` (`:981`) and `post-admin-delete-all` (`:2219`) return
  `202`, `409` and `503`, and delete-all drops its `BulkOperationResult` body; a `503` problem
  response on the tracking operations; the endpoint-status schema's "per-endpoint storage container"
  wording (`:3901-3904`). Regenerate through the build; never edit `ApiContract.g.cs` or
  `api-client/index.ts` by hand.
- **Layout pending:** an exception filter maps `TrackingLayoutPendingException`, and only it, to
  `503` with the problem type; the ClientApp shows the migration notice, linking the runbook,
  wherever a tracking request returns it.
- **Topology → Storage (spec §5.8):** `IsProtectedContainer` (`AdminImplementation.cs:325`) takes
  the marker, read once per request through `TrackingLayout`; a catalog-named container stays
  protected until the marker lists it as migrated or excluded; excluded containers carry a "not
  migrated" label.
- **ClientApp:** both purge confirmations say the purge runs in the background, takes time and
  consumes throughput, and both handle `202`, `409` and `503`; Operations → Delete all events loses
  "Delete the entire endpoint container" (`advanced-operations.tsx:563`).
- **Tests:** `AdminCosmosContainerTests` for marker-aware protection and the label; the purge cases
  of spec §8 (`202`, own DI scope, required audits under the captured identity, `503` on an
  unauditable start, `409` across instances and lease takeover, `ClearEndpoint` first); `503` while
  pending; Vitest for the notice and the purge dialogs
  (`npm --prefix src/NimBus.WebApp/ClientApp run test:ci`).
- **Screenshot** of the purge confirmation and the migration notice in the PR body.

### PR 8: documentation and pipelines

Everything in spec §10:

- ADR-008 superseded; ADR-017 accepted; `docs/adr/README.md`;
- `docs/storage-providers.md`, `docs/azure-requirements.md`, `docs/deployment.md` (spec §6.2's
  runbook and §6.3's rollback, the deploy preflight and its Entra role) and `docs/cli.md`;
- `deploy.yml` and `pipelines/azure-pipelines-deploy.yml` pass `--cosmos-tracking-max-throughput`
  through. They never pass `--allow-pending-migration`;
- the other ADR-008 references, the two CrmErpDemo `docs/TDD.md` files and the adapter-docs skill
  templates;
- spec 036's status line becomes "implemented in v5.0.0";
- a draft of the v5.0.0 release notes in the house pattern (`docs/versioning.md#cutting-a-release`):
  ⚠️ Breaking for the migration and the purge API; Compatibility for the three adapter members,
  `TrackingLayoutPendingException` and the purge's new audit entries.

### Step 9: release gate

- §14 item 9b on Flex Consumption and Elastic Premium: deploy with `--allow-pending-migration` keeps
  the trigger paused; a v5 `nb infra apply` mid-cutover keeps it paused; pending to ready within a
  minute of the marker; `nb container migrate` reads the trigger; a rollback by §6.3 and a retry by
  §6.2 converge.
- A migration rehearsal on a production-sized copy of the live deployment's account, timed against
  spec §6.4's worked example.

Results go into the spec folder and the release notes.

### Step 10: release v5.0.0

Release from master by the standard procedure. Then Phase 2 (spec §9): migrate each Cosmos
deployment by the runbook, starting with the live dev deployment. Phases 3 and 4 are outside this
plan, but PR 8's docs record what they remove.

## Verification gate (before step 10)

```powershell
dotnet build src/NimBus.sln -c Release
dotnet test src/NimBus.sln -c Release --no-build
npm --prefix src/NimBus.WebApp/ClientApp run test:ci
npm --prefix src/NimBus.WebApp/ClientApp run build
```

- Live SQL and Cosmos conformance with `NIMBUS_SQL_TEST_CONNECTION`,
  `NIMBUS_COSMOS_TEST_CONNECTION` (or endpoint plus key), `NIMBUS_COSMOS_TEST_GATEWAY=1` for the
  emulator and `NIMBUS_COSMOS_TEST_REQUIRED=1`.
- The test summary shows no skipped or inconclusive provider tests.
- CI's "must not skip" gate stays green on the pinned emulator tag.
- Step 9's reports are attached.

## Open questions

1. ~~Retiring Topology → Storage in a 5.x minor.~~ **Decided 2026-10-08:** deprecated in a
   5.x minor, removed in v6.0.0 (spec §5.8, §13 item 5). It affects Phases 3 and 4 only, not this
   plan's PRs.
2. **Other v5 removals.** Five `IMetricsStore` overloads say "removed in v5" (`IMetricsStore.cs:22-42`).
   They aren't part of Spec 036, but v5.0.0 should remove them under `docs/versioning.md`. Track them
   as a separate PR before step 10.

## Risks this plan adds to the spec's §11

- **PR 3's size.** It changes every tracking member at once. Mitigation: the conformance suite and
  the recorded-query tests come first, and the PR's commits go member family by member family (point
  reads, guarded writes, terminal writes, patches, queries, purge, guard, preflight), each green.
- **Master unreleasable for the length of PRs 3-8.** 4.x fixes go through `release/4.x`, with the
  cherry-pick overhead that brings.
- **Deploying master by mistake.** `deploy.yml` builds whatever ref it is dispatched on. Mitigation:
  the deploy-from-`release/4.x` rule (PR 0b) and the preflight in PR 3.

## Review trail

- 2026-10-07: first draft.
- 2026-10-07: reviewed by Codex ([plan review](2026-10-07-spec-036-implementation-plan-review.md)),
  together with a second review of the spec
  ([spec review](2026-10-07-spec-036-spec-review.md)). Claude checked every finding against the code;
  all twelve plan findings held.
- 2026-10-08: revised. Changes, by plan-review finding:
  1. Phase 0 runs first, on a spike branch with a harness and a draft template; only item 9b moves
     to a release gate, and the spec's §14 item 9 now splits in two.
  2. The obsolete members move to PR 6, after their last caller in `src/` goes (Decision 3).
  3. PR 3 converts every test fake the guard affects and lists them.
  4. The private-networking fallback is dropped: Spec 034 already runs deployments in-network, and
     the spec's preflight stays fail-closed (spec §6.5).
  5. The third adapter member and `TrackingLayoutPendingException` are now in spec §5.9 and §8, with
     the `NotSupportedException` translation specified and tested.
  6. PR 2 must precede PR 3; the copy tools move to the copier in PR 5; deployments run from
     `release/4.x` until v5.0.0, and the preflight lands in PR 3.
  7. The token-restart extension is dropped; only the failed search restarts (PR 4).
  8. PR 1 gains the host-built priority test, and Phase 0 and PR 1 the set-query paging check.
  9. Subscription updates are named as blocked under the guard (spec §6.5) and tested in PR 3.
  10. The reservation release is v4.7.0, a minor, now recorded in spec §9.
  11. Copied rows never expire, as today; spec §8 states it and PR 5 tests it.
  12. Phase 0 starts by confirming access to the live deployment.

  The spec review's findings changed the spec (its §16) and, through it, PRs 2, 3, 5 and 7: the
  purge's snapshot and lease, ETag stamps, the `--exclude` refusal, the trigger carry-forward, the
  guard's recheck and Entra-first preflight.
