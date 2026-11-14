# Spec 043 — Intelligent Failed Message Insights: implementation plan

Date: 2026-11-06. Spec: [043](../spec/043-intelligent-failed-message-insights/spec.md) (revision 2).
Branches: one per PR below, prefixed `feat/insights-`. Spec PR: #323.

## Problem

Failed Message Insights groups failures by normalized error text only. Spec 043 adds two
groupings from saved failure classifications (likely cause, recovery guidance), four-state
coverage, an endpoint filter, occurrence drill-down and group detail, and a read-only activation
mode so saved results stay readable without a provider key. Phase 1 reads only.

This plan resolves the four decisions in spec section 11 and sequences the work into five PRs
that each leave `master` releasable. Everything below was checked against the working tree at
`1e0e72b0`.

## Decisions

### D1. Limits and cache lifetimes (proposed defaults; PR 1 measures and fixes them)

| Setting (`NimBus:Insights:*`) | Default | Rationale |
|---|---|---|
| `EnrichmentCeiling` | 10 000 occurrences, counted after the endpoint filter | Spec 8.1 |
| `EnrichmentCacheSeconds` | 15 | Spec 8.3; never above the 30 s metrics TTL |
| `BulkReadBatchSize` | 500 ids | SQL `IN` stays under the 2 100-parameter limit; Cosmos `ReadManyItemsAsync` tuples per call |
| `BulkReadConcurrency` | 4 batches in flight | 10 000 ids = 20 batches, 5 rounds |
| `TopPatternsPerGroup` | 10 | Spec 6.2 |

Bound in a new `FailedInsightsOptions` (WebApp, `Services/Insights/`), validated at startup
(ceiling 100–100 000, cache 0–30 s, batch 50–1 000, concurrency 1–8, patterns 1–50).

**Measurement step, split across PR 1 and PR 2 (both before PR 3 is opened).**

- PR 1: the SQL occurrence projection read for 1d, 7d and 30d windows. Done; see
  "Measurements". Result: no index migration (the candidate index does not move the 30-day
  read).
- PR 2: one cold enrichment of 10 000 ids in 500-id batches on SQL and on the Cosmos emulator
  (p95 wall time), with the enrichment cache warm and cold. The bulk reader only exists from
  PR 2, so this cannot run earlier.
- Cosmos latency and RU need a live account: the vNext emulator reports no request charge and
  is not representative for latency (see "Measurements"). Measure both on the dev account
  (`rg-nbdemo-dev`) before PR 3 merges, not before it opens.

Accept the enrichment defaults if a cold 10 000-id enrichment stays under 3 s p95 on SQL and on
the live Cosmos account, and under 50 000 RU on the live account; otherwise lower
`EnrichmentCeiling` to the largest scope that meets both, and record why.

### D2. Contracts

**Bulk read.** Whole documents, no summary field (spec 8.1). A new interface in the extension:

```csharp
public interface IFailureClassificationBulkReader
{
    Task<ClassificationBulkReadResult> ReadLatestAsync(IReadOnlyCollection<string> failureMessageIds, CancellationToken ct = default);
}
public sealed record ClassificationBulkReadResult(bool Available, IReadOnlyDictionary<string, ClassificationSnapshot> Snapshots);
public sealed record ClassificationSnapshot(FailureClassification? Latest, string? LatestOperationStatus, string? LatestOperationErrorCode, bool Deleted);
```

`AtomicClassificationStore` implements it through a new `protected abstract
IAsyncEnumerable<ClassificationDocument> ReadManyAsync(IReadOnlyList<string> ids, CancellationToken ct)`
and derives the snapshot from `Operations` exactly as `GetLatestAsync` and
`GetLatestOperationAsync` do (including the expiry-to-`AnalysisOutcomeUnknown` normalization).
Implementations:

- `SqlClassificationStore`: `SELECT FailureMessageId, StateJson FROM dbo.FailureClassifications WHERE FailureMessageId IN (@p0…)`
  per batch. `SqlException` number 208 (invalid object name) and any other storage error →
  `Available = false`.
- `CosmosClassificationStore`: `ReadContainerAsync` once per bulk read (NotFound → unavailable),
  then `ReadManyItemsAsync<ClassificationDocument>` with `("state", new PartitionKey(id))`
  tuples per batch. Missing items are absent only because the container check passed.
- `InMemoryClassificationStore` (test project) and `UnavailableClassificationStore`
  (`Available = false`).

A registered `IFailureClassificationStore` that does not implement the interface yields
enrichment status `unsupported`.

**Read-only service.** `FailureClassificationReader` (extension, sealed) with `GetLatestAsync`,
`GetHistoryAsync` (moved from `FailureClassificationService`, which delegates to keep its API)
and `ReadForInsightsAsync(ids)`; it depends on `IFailureClassificationStore`,
`IIntegrationIntelligenceHost` and `IMessageTrackingStore` only. `FailureClassificationService`
keeps `AnalyzeAsync`.

**Controllers.** Split `IntegrationIntelligenceController` into
`IntegrationIntelligenceReadController` (GET and GET `history`, depends on the reader) and
`IntegrationIntelligenceController` (POST only, depends on the service), both on
`api/integration-intelligence/failures/{eventId}/{messageId}/classification`. The feature
provider prunes all intelligence controllers when not readable and only the POST controller
when not ready.

**Registration.** In `AddNimBusIntegrationIntelligence`:
`readable = options.Enabled && bindingErrors.Length == 0 && hasStorage && hasHostAdapter`,
`ready = readable && validationErrors.Length == 0`. When readable: store, `SqlClassificationSchemaInitializer`,
`ClassificationRetentionWorker`, `FailureClassificationReader`, `IntegrationIntelligenceStatusService`.
When ready: redactor, evidence builder, HTTP client, provider, `FailureClassificationService`.
`IntegrationIntelligenceActivation` gains `bool Readable { get; init; }` (init property, not a
positional parameter, so existing constructions compile). `OperatorClassificationSource` takes
`IEnumerable<FailureClassificationReader>` for reads and keeps `IEnumerable<FailureClassificationService>`
for `AnalyzeAsync`; `IsAvailable` means a reader is present.

**API (`src/NimBus.WebApp/api-spec.yaml`, Metrics tag, site Reader).** All additions are
optional properties or new routes; `totalFailed` stays.

| Method | Route | Parameters → Result |
|---|---|---|
| GET | `/api/metrics/failed-insights` (existing) | adds `endpointId`, `refresh` (bool: bypass the enrichment cache) → `FailedInsightsOverview` + new fields |
| GET | `/api/metrics/failed-insights/occurrences` | `from`, `to` (required), `endpointId`, `perspective`, `groupKey`, `pageSize` (default 50, max 100), `continuationToken` → `InsightOccurrencePage`; 400 for an unknown key or a cursor from another scope |
| GET | `/api/metrics/failed-insights/patterns` | same scope, `perspective` (cause or guidance), `groupKey`, `pageSize`, `continuationToken` → `InsightGroupPatternPage` |

New schema members on `FailedInsightsOverview`: `from`, `to`, `endpointId`,
`totalOccurrences`, `occurrencesReadAt`, `classificationsReadAt`, `enrichment`
(`EnrichmentStatus`: available, disabled, unavailable, unsupported, limitExceeded),
`enrichmentLimit`, `coverage` (`InsightsCoverage`: classified, uncertain, notAnalyzed,
notEligible, analyzed, total; nullable), `causeGroups` and `guidanceGroups` (`InsightGroup[]`,
nullable). `ErrorPatternGroup` and `ErrorSubGroup` gain `groupKey` and nullable `coverage`.
`InsightGroup`: `groupKey`, `label`, `count`, `coverage`, `endpoints`, `eventTypes`,
`latestOccurrence`, `topPatterns` (`InsightGroupPattern`: `groupKey`, `normalizedPattern`,
`count`, `exampleErrorText`), `morePatterns`. `InsightOccurrence`: `messageId`, `eventId`,
`endpointId`, `eventTypeId`, `failedAt`, `errorPatternKey`, `normalizedPattern`,
`analysisState` (`InsightAnalysisState`: classified, uncertain, notAnalyzed, notEligible,
unavailable), `operationMarker` (nullable: newerAnalysisRunning, outcomeUnknown,
lastAnalysisFailed, identityMismatch), `category`, `categoryConfidence`, `guidance`,
`revision`, `classifiedAt`, `currentStatus` (nullable `ResolutionStatus`). Pages carry
`continuationToken` and `readAt`. The cursor parameter is named `continuationToken` like the
rest of the API.

**Group keys.** `ec:` + 32 hex chars of SHA-256 over the category string; `ep:` + the same
over the normalized pattern; cause keys are the taxonomy names; guidance keys are the
`FailureGuidance` names; plus `uncertain`, `notAnalyzed`, `notEligible`. Keys are resolved
against the current scope's groups; an unresolvable key is 400.

**Cursor.** Base64url JSON `{ "t": <failedAt ticks>, "m": <messageId>, "s": <scope hash> }`
for occurrences and `{ "c": <count>, "k": <groupKey>, "s": … }` for patterns, where the scope
hash is SHA-256 over `from|to|endpointId|perspective|groupKey`. A mismatching `s` is 400. No
signing: the scope is re-sent and re-authorized on every request.

### D3. Layout

Chart (top 15 groups of the current perspective, existing recharts bar) stays above. Below it:
the scope summary line (count, "analyzed" with a segmented coverage bar, both read times,
Refresh, and the enrichment banner when not `available`), then a two-column area at ≥ 1024 px:
group list left, drill-down right. Under 1024 px the columns stack and the drill-down opens
below the selected group. Groups show their top patterns inline with an "and N more" link that
opens the patterns page in the right column. The wireframe in the spec's visualization is the
reference; the PR 5 description carries the screenshot.

### D4. Single-failure GET alignment

Phase 1 keeps the GET route's current 409/503 answers and ships the drill-down marker (spec 5).
`intelligence-card.tsx` already renders those states; changing the route would widen Phase 1
and touch the MCP tool's error mapping. Alignment is a follow-up, to be scheduled with a
`docs/versioning.md` note because it changes an HTTP contract.

## Implementation

Delivered as five PRs, in order. Each has its own branch, RED-first tests and a Release build.

### PR 1 — `feat(store): expose the failed attempt's message id in failed-message insights`

- `FailedMessageInfo.SessionId` (`string?`) for the stored event id that PR 4's status lookup
  needs, with a conformance test that resolves a projected row through `GetEventsByIds` on every
  provider. Added after PR 4's design showed that bare event ids miss on Cosmos.
- `FailedMessageInfo.MessageId` (`string?`, nullable so a custom provider that never sets it
  reads as `unsupported`).
- Projections: SQL `SqlServerMetricsStore.GetFailedMessageInsights` adds `MessageId` to the
  SELECT; Cosmos `CosmosDbMetricsStore` adds `c.message.MessageId` and a DTO property;
  `InMemoryMessageStore` maps `m.MessageId`.
- `MetricsStoreConformanceTests.GetFailedMessageInsights_returns_recent_failed_error_details`
  asserts the id.
- The SQL projection part of the D1 measurement step; results appended to this plan.

### PR 2 — `feat(intelligence): read saved classifications without a provider`

- `IFailureClassificationBulkReader`, `ClassificationBulkReadResult`, `ClassificationSnapshot`
  in `IntegrationIntelligenceContracts.cs`; `ReadManyAsync` on `AtomicClassificationStore`
  with the SQL, Cosmos, in-memory and unavailable implementations.
- `FailureClassificationReader`; `FailureClassificationService.GetLatestAsync/GetHistoryAsync`
  delegate to it.
- Controller split and feature-provider change; `IntegrationIntelligenceActivation.Readable`;
  registration split as in D2; `OperatorClassificationSource` over the reader.
- Telemetry: `nimbus.intelligence.insights.bulk_reads` (counter, tags `provider`, `outcome`)
  and `nimbus.intelligence.insights.documents` (histogram of ids per read) in
  `NimBusIntelligenceTelemetry`.
- `docs/integration-intelligence.md`: the three activation modes and what each serves.
- The enrichment part of the D1 measurement step, through the new bulk reader; results
  appended to this plan.

### PR 3 — `feat(webapp): group failed insights by likely cause and recovery guidance`

- `api-spec.yaml`: the overview additions, `endpointId` and `refresh` parameters, and the new
  schemas (the two routes come in PR 4). NSwag regenerates `ApiContract.g.cs` and
  `api-client/index.ts`; the build that validates this must not set `SkipSpaBuild`.
- `Services/Insights/FailedInsightsService.cs` behind `IFailedInsightsQuery`:
  `BuildOverviewAsync(InsightsScope scope, bool enrich, bool refresh, CancellationToken)`.
  It owns window resolution and the two-level occurrence cache (keys
  `metrics:failed-insights:window:{preset}` and `metrics:failed-insights:rows:{fromTicks}-{toTicks}`),
  duplicate collapsing, missing-id detection, the in-memory endpoint filter, the ceiling, the
  enrichment cache (`metrics:failed-insights:enrichment:{fromTicks}-{toTicks}:{endpointId}`, TTL
  from options, bypassed when `refresh`), coordinate verification, reader validation
  (`Enum.IsDefined(typeof(FailureGuidance), value)` and the taxonomy set, which lives in
  `FailureClassificationQuestionSet` and is exposed as a static `KnownCategories`), coverage,
  the three perspectives, group keys and the top-N pattern embedding.
- `IInsightsEnrichmentSource` (WebApp) with one implementation over
  `IEnumerable<FailureClassificationReader>` plus `FailureClassificationOptions.AllowedEndpoints`
  for `notEligible`; absent reader → `disabled`.
- `MetricsImplementation.GetMetricsFailedInsightsAsync` delegates to the service with
  `enrich: true`. `OperatorQueries` takes `IFailedInsightsQuery` and calls it with
  `enrich: false`; `OperatorInsightToolsTests` fakes the new interface instead of the metrics
  failed-insights method.
- `FailedInsightsOptions` bound from `NimBus:Insights`.
- Telemetry in the WebApp meter: `nimbus.insights.identity_mismatches`,
  `nimbus.insights.duplicate_identities`, `nimbus.insights.enrichment` (counter, tag `status`).

### PR 4 — `feat(webapp): failed insights occurrence drill-down and group patterns`

- `api-spec.yaml`: the two routes and page schemas; `MetricsImplementation` implements the two
  generated members (match the generated nullability exactly: CS8767 fails Release).
- `FailedInsightsService.PageOccurrencesAsync` and `PagePatternsAsync` over the same cached
  inputs; keyset ordering, cursor encode/validate, 400 mapping.
- Current status per page: group rows by endpoint, call
  `IMessageTrackingStore.GetEventsByIds(endpointId, storedIds)` once per endpoint with stored
  ids `{EventId}_{SessionId}` (a bare event id finds nothing on Cosmos; see spec 4.3), map
  `ResolutionStatus`; a thrown `EndpointNotFoundException` or a missing event → `currentStatus`
  null (shown as unavailable).

### PR 5 — `feat(webapp): group-by, coverage and drill-down on the Insights page`

- `pages/insights.tsx`: URL state through `useSearchParams` (`period` or `from`/`to`,
  `endpoint`, `by`, `group`, `token`), the Group by control, the endpoint select (same endpoint
  list the Failed messages page uses), the scope summary, Refresh (`refresh=true`), a request
  sequence ref that discards late responses, and refetch on `focus`/return from details.
- New components under `components/insights/`: `scope-summary.tsx`, `enrichment-banner.tsx`,
  `coverage-bar.tsx`, `group-list.tsx`, `occurrence-table.tsx`, `group-patterns.tsx`. Rows link
  to the existing event details route with the occurrence's message id.
- Labels per spec 6 and 7: "Error pattern", "Not analyzed (includes failures that can no longer
  be analyzed)", "Not eligible", "Threshold settings not recorded".
- Docs: the Insights section of the WebApp documentation; screenshot in the PR.

## Tests (RED first)

PR 1
- Conformance: `GetFailedMessageInsights` returns each attempt's `MessageId` (memory, SQL,
  Cosmos).

PR 2
- `DurableStoreConformanceTests` (memory, sql, cosmos): bulk read of 1 200 ids where some are
  absent, some tombstoned, some with an active or expired newer operation; the result holds the
  latest completed result and the normalized operation state and distinguishes absent from
  `Available = false` (table/container dropped → unavailable).
- `IntelligenceActivationTests`: enabled with storage and no `ApiKey` → `Readable` true,
  `Ready` false, GET and `history` routes present, POST absent, status `ProviderNotConfigured`;
  binding failure → nothing registered; ready → all routes.
- `ServiceAndEvidenceTests`: `FailureClassificationService.GetLatestAsync` behaves as before
  through the reader (409 on active, 409 on unknown outcome, 503 on failed).
- MCP: `nimbus_get_classification` answers in read-only mode; `nimbus_classify_failure` reports
  the feature unavailable.

PR 3
- `FailedInsightsServiceTests` over `InMemoryMessageStore`, `InMemoryClassificationStore` and a
  real `StoreResultCache`:
  - three attempts of one event with different results count three occurrences, each joined to
    its own id;
  - latest completed revision wins over a newer active operation; the row marker is set;
  - coverage partitions and per-perspective sums reconcile for a mixed scope (classified,
    uncertain by `unknown`, uncertain by out-of-range guidance integer, uncertain by unknown
    category string, uncertain by mismatched endpoint, not analyzed, not eligible);
  - `[from, to)` boundaries and the endpoint filter select exactly the matching occurrences;
  - duplicate identities count once; a missing id makes the scope `unsupported`; error-pattern
    groups still present;
  - over the ceiling → `limitExceeded` with the limit; storage unavailable → `unavailable`
    and null coverage; no reader → `disabled`;
  - a recording bulk reader proves one bulk read per scope, batches of at most
    `BulkReadBatchSize`, and never a `GetLatestAsync` call;
  - preset overview then a request with the returned window reads the projection once;
  - two concurrent builds share one bulk read; `refresh` bypasses the enrichment cache but not
    the occurrence cache; a tombstoned classification is never exposed.
- `MetricsImplementationTests`: non-Reader is forbidden; invalid window is 400; `totalFailed`
  equals `totalOccurrences`.
- `OperatorInsightToolsTests`: the failures view calls the query with `enrich: false` and its
  output is unchanged with hidden endpoints.

PR 4
- Occurrence paging: ordering by failed time then id, page size bounds, cursor round trip,
  cursor from another scope → 400, unknown group key → 400, current status via one
  `GetEventsByIds` call per endpoint (recording store), unauthorized caller forbidden.
- Pattern paging: top-N embedding plus the remainder, and the page continuing past it.

PR 5 (Vitest, `npm run test:ci`)
- Grouping switches perspective without refetching the window; URL reflects the scope and a
  reload restores it.
- Coverage and the banner for each enrichment status; "Not analyzed" and "Not eligible" labels.
- Drill-down pages with the cursor, shows the row marker and current status, and links to the
  details route.
- A late response for a previous scope is discarded; Refresh sends `refresh=true`.
- Existing `insights.range.test.tsx` keeps passing.

## Verification

Per PR: `dotnet build src/NimBus.slnx -c Release` (zero CS warnings),
`dotnet test src/NimBus.slnx -c Release --no-build`; PR 3 onwards also
`npm --prefix src/NimBus.WebApp/ClientApp run test:ci` and `run build`. PR 1 and PR 2 run the
live SQL and Cosmos conformance suites (`NIMBUS_SQL_TEST_CONNECTION`,
`NIMBUS_COSMOS_TEST_CONNECTION`) and report them; CI rejects skipped conformance tests. PR 5
captures the finished page (grouped by cause, one group open) for the PR description. The
measurement results from D1 must be in this plan before PR 3 is opened.

## Measurements

### SQL occurrence projection (PR 1, 2026-11-07)

Setup: SQL Server 2022 in Docker on the development workstation; a `Messages` table with the
production DDL and all five production indexes (`0002_Messages.sql`, `0014_SearchSortIndexes.sql`);
1 000 000 rows over 30 days, every tenth an `ErrorResponse` (100 000 failures) with a ~1 KB
`MessageContentJson` including error text and stack trace, the rest `EventRequest` and
`ResolutionResponse` with ~0.7 KB JSON. The query is the exact `GetFailedMessageInsights`
projection with `MessageId`, results inserted into a table variable. Cold means after
`CHECKPOINT; DBCC DROPCLEANBUFFERS`.

| Window | Failures | Today cold / warm | With `(MessageType, EnqueuedTimeUtc) INCLUDE (EndpointId, EventTypeId, EventId, MessageId)` cold / warm |
|---|---|---|---|
| 1d | 3 333 | 410 / 132 ms | 181 / 78 ms |
| 7d | 23 333 | 1 914 / 721 ms | 1 012 / 495 ms |
| 30d | 100 000 | 3 114 / 2 124 ms | 3 319 / 2 363 ms |

Findings:

- The existing date-leading `IX_Messages_EnqueuedTimeUtc` (migration 0014) is not used for a
  7-day window: the range covers about 23 % of rows, and the optimizer chooses a clustered
  index scan over 23 000 key lookups. The spec's "no index leading on (MessageType,
  EnqueuedTimeUtc)" is literally true; the effect is a scan for wide windows.
- The candidate index halves the 1d and 7d reads but does not change the 30d read, which is
  dominated by reading 100 000 JSON documents for `$.ErrorContent.ErrorText`. Any index that
  helps the 30-day read would have to include the error text, which today lives only inside
  `MessageContentJson`.
- The 30-day read misses the 1 s target either way. It is today's cost for the existing page,
  Phase 1 does not change it, the occurrence cache bounds it to one read per window per 30 s,
  and a 100 000-occurrence scope is above the 10 000 enrichment ceiling, so it returns
  `limitExceeded` for the new perspectives.

Decision: no index migration in Phase 1. A persisted `ErrorText` column (or a computed column
over the JSON path, indexed with the identity columns) is the only change that would move the
30-day read; it is a separate storage change and is listed under Out of scope.

### Enrichment (PR 2, 2026-11-07)

Setup: 10 000 classification documents, one to three completed revisions each (~2–6 KB of
JSON per document), read through `FailureClassificationReader.ReadLatestInBulkAsync` from a
throwaway harness on the development workstation. Six measured runs after one warm-up; SQL runs
drop the buffer pool and the connection pool first. The Cosmos emulator is the vNext preview in
gateway mode.

| Provider | Batch × concurrency | Median | Max |
|---|---|---|---|
| SQL Server 2022 | 500 × 4 (default) | 1 886 ms | 1 974 ms |
| SQL Server 2022 | 1 000 × 4 | 1 925 ms | 2 034 ms |
| SQL Server 2022 | 500 × 8 | 2 832 ms | 4 061 ms |
| Cosmos vNext emulator | 500 × 4 (default) | 20 846 ms | 22 398 ms |
| Cosmos vNext emulator | 1 000 × 4 | 14 534 ms | 14 732 ms |
| Cosmos vNext emulator | 500 × 8 | 17 514 ms | 18 093 ms |

Findings:

- SQL meets the 3 s gate with the defaults. More concurrency is slower on a cold buffer pool,
  so `BulkReadConcurrency` stays at 4.
- The emulator misses the gate by a factor of about seven. It is not a performance reference:
  it is Postgres-backed, gateway-only, and reports no request charge. Lowering the ceiling on
  its evidence would cap a live account at roughly 1 000 occurrences for no measured reason.

Decision: keep the defaults for now. The Cosmos gate (3 s p95 and 50 000 RU for a cold
10 000-id read) moves entirely to the live-account measurement, which must pass before PR 3
merges. Seeding 10 000 documents into the shared dev account needs the owner's approval
first. If the live account misses the gate, lower `EnrichmentCeiling` (and consider a
1 000-id batch, which the emulator favoured) before PR 3 merges.

### Enrichment on a live Cosmos account (2026-11-07)

No NimBus Cosmos account existed (the dev environment named above had been removed), so with
the owner's approval a temporary **serverless** account was created in swedencentral, measured
and deleted: `cosmos-nbinsights-perf-1107` in `rg-nimbus-insights-perf`. Same 10 000 documents
and harness as above, Direct mode (the SDK default NimBus uses), every response's request
charge summed. Measured from the development workstation in Europe over the internet (about
20 ms per round trip), so in-region latency from the WebApp will be lower; RU are exact.

| Scope | Batch × concurrency | Median | Max | RU per read | 429s |
|---|---|---|---|---|---|
| 10 000 | 500 × 4 (default) | 1 863 ms | 3 044 ms | 3 556 | 0 |
| 10 000 | 1 000 × 4 | 2 058 ms | 2 284 ms | 3 971 | 0 |
| 10 000 | 500 × 8 | 1 449 ms | 1 468 ms | 3 556 | 0 |
| 5 000 | 500 × 4 | 1 001 ms | 1 216 ms | 1 779 | 0 |
| 5 000 | 500 × 8 | 861 ms | 972 ms | 1 779 | 0 |
| 2 500 | 500 × 4 | 624 ms | 646 ms | 889 | 0 |
| 1 000 | 500 × 4 | 298 ms | 300 ms | 355 | 0 |

Findings:

- **RU**: a cold 10 000-id enrichment costs about 3 600 RU, about 0.36 RU per failure and 7 %
  of the 50 000 RU budget. ReadMany point reads are cheap; whole-document reads (D2) do not
  need a summary field for cost reasons.
- **Latency**: with the defaults the median is 1.9 s; one of six runs took 3.04 s, just over
  the 3 s target, and the other five stayed under 1.9 s. With 8 batches in flight every run
  stayed under 1.5 s. Cost scales linearly with scope (1 000 ids: 0.3 s).
- **Concurrency differs by provider**: 8 in flight is faster on Cosmos but slower on SQL (2.8 s
  median, 4.1 s max above), so no single default is best for both.
- Seeding needed rate-limit retries on serverless (429 `RUBudgetExceeded` at 32 concurrent
  writes); the reads were never throttled.

Decision: the gate passes. Keep `EnrichmentCeiling` at 10 000 and the SQL-safe defaults
(500 × 4). Document that Cosmos deployments may set `NimBus:Insights:BulkReadConcurrency` to 8.
The single 3.04 s run came from a cross-internet path; it does not justify lowering the ceiling.

## Out of scope

- Selective analysis, representative hypotheses, resumable bulk execution, hidden-endpoint
  rules for the MCP failures view, a latest-result summary field, and status-based eligibility
  (spec section 12).
- Changing the single-failure GET route's 409/503 answers (D4).
- A `nb` CLI counterpart.
- A persisted or computed `ErrorText` column on SQL `Messages` to speed up long-window
  projections (see "Measurements").
